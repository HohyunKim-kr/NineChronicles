using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BTAI;
using Cysharp.Threading.Tasks;
using Nekoyume.Game.Controller;
using Nekoyume.Game.VFX;
using Nekoyume.Game.VFX.Skill;
using Nekoyume.UI;
using UnityEngine;
using UniRx;
using Nekoyume.Model.Skill;
using Nekoyume.Model.Elemental;
using Nekoyume.Model.Character;
using UnityEngine.Rendering;
using Nekoyume.Game.Battle;
using Nekoyume.Model;

namespace Nekoyume.Game.Character
{
    public abstract class Actor : Character
    {
        public const float AnimatorTimeScale = 1.2f;
        protected static readonly WaitForSeconds AttackTimeOut = new WaitForSeconds(5f);

        [SerializeField]
        private bool shouldContainHUD = true;

        public GameObject attackPoint;
        public SortingGroup sortingGroup;

        private bool _applicationQuitting = false;
        private Root _root;
        private long _currentHp;

        private readonly List<IDisposable> _disposablesForModel = new List<IDisposable>();

        public CharacterBase CharacterModel { get; protected set; }

        public readonly Subject<Actor> OnUpdateHPBar = new Subject<Actor>();

        protected abstract float RunSpeedDefault { get; }
        protected abstract Vector3 DamageTextForce { get; }
        protected abstract Vector3 HudTextPosition { get; }
        public virtual string TargetTag { get; protected set; }

        public Guid Id => CharacterModel.Id;
        public SizeType SizeType => CharacterModel.SizeType;
        private float AttackRange => CharacterModel.attackRange;

        public int Level
        {
            get => CharacterModel.Level;
            set => CharacterModel.Level = value;
        }

        public long HP => CharacterModel.HP;

        public long CurrentHP
        {
            get => _currentHp;
            private set
            {
                _currentHp = Math.Min(Math.Max(value, 0l), HP);
                UpdateHpBar();
            }
        }

        protected bool IsDead => CurrentHP <= 0;

        public bool IsAlive => !IsDead;

        public float RunSpeed { get; set; }

        public HpBar HPBar { get; private set; }
        public HudContainer HudContainer { get; private set; }
        private ProgressBar CastingBar { get; set; }
        protected SpeechBubble SpeechBubble { get; set; }

        private readonly Dictionary<int, VFX.VFX> _persistingVFXMap = new();

        protected virtual bool CanRun
        {
            get
            {
                if (_forceStop)
                {
                    return false;
                }

                return !Mathf.Approximately(RunSpeed, 0f);
            }
        }

        protected BoxCollider HitPointBoxCollider { get; private set; }
        public Vector3 HitPointLocalOffset { get; set; }

        private readonly Queue<ActionParams> actionQueue = new();
        public IReadOnlyCollection<ActionParams> ActionQueue => actionQueue;

        private ActionParams _action;


        private bool _forceStop = false;

        #region Mono

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        protected virtual void Awake()
        {
#if !UNITY_EDITOR
            attackPoint.SetActive(false);
#endif

            HitPointBoxCollider = GetComponent<BoxCollider>();
        }

        protected virtual void Start()
        {
            InitializeHudContainer();
        }

        protected virtual void OnDisable()
        {
            foreach (var vfx in _persistingVFXMap.Values)
            {
                vfx.gameObject.SetActive(false);
            }
            _persistingVFXMap.Clear();

            RunSpeed = 0.0f;
            _root = null;
            actionQueue.Clear();
            _action = null;
            if (!_applicationQuitting)
                DisableHUD();
            _forceStop = false;

            SetDefaultColor();
            _expiredColorKeys.Clear();
            _colorPq.Clear();
        }

        #endregion

        public virtual void Set(CharacterBase model, bool updateCurrentHP = false)
        {
            _disposablesForModel.DisposeAllAndClear();
            CharacterModel = model;
            InitializeHudContainer();
            if (updateCurrentHP)
            {
                CurrentHP = HP;
            }
        }

        protected virtual IEnumerator Dying()
        {
            yield return new WaitWhile(HasAction);
            OnDeadStart();
            StopRun();
            Animator.Die();
            yield return new WaitForSeconds(.2f);
            DisableHUD();
            yield return new WaitForSeconds(.8f);
            OnDeadEnd();
        }

        private void LateUpdate()
        {
            if (HudContainer)
            {
                HudContainer.UpdatePosition(ActionCamera.instance.Cam, gameObject, HUDOffset);
            }

            if (SpeechBubble)
            {
                SpeechBubble.UpdatePosition(ActionCamera.instance.Cam, gameObject, HUDOffset);
            }
        }

        protected virtual void Update()
        {
            _root?.Tick();
            UpdateColor();
        }

        private void InitializeHudContainer()
        {
            // No pooling. Widget.Create<HudContainer> didn't pooling HUD object.
            // HUD Pooling causes HUD positioning bug.
            if (!HudContainer && shouldContainHUD)
            {
                HudContainer = Widget.Create<HudContainer>(true);
            }
        }

        protected virtual void InitializeHpBar()
        {
            if (!HudContainer)
            {
                return;
            }

            HPBar = Widget.Create<HpBar>(true);
            HPBar.transform.SetParent(HudContainer.transform);
            HPBar.transform.localPosition = Vector3.zero;
            HPBar.transform.localScale = Vector3.one;
        }

        public virtual void UpdateHpBar()
        {
            if (!BattleRenderer.Instance.IsOnBattle)
                return;

            if (!HPBar)
            {
                InitializeHpBar();
                HudContainer.UpdateAlpha(1);
            }

            HudContainer.UpdatePosition(ActionCamera.instance.Cam, gameObject, HUDOffset);
            HPBar.Set(CurrentHP, CharacterModel.AdditionalHP, HP);
            HPBar.SetBuffs(CharacterModel.Buffs);
            HPBar.SetLevel(Level);

            // delete existing vfx
            var removedVfx = new List<int>();
            foreach (var buff in _persistingVFXMap.Keys)
            {
                if (CharacterModel.IsDead ||
                    !CharacterModel.Buffs.Keys.Contains(buff))
                {
                    _persistingVFXMap[buff].LazyStop();
                    removedVfx.Add(buff);
                }
            }

            foreach (var id in removedVfx)
            {
                _persistingVFXMap.Remove(id);
            }

            OnUpdateHPBar.OnNext(this);
        }

        public bool ShowSpeech(string key, params int[] list)
        {
            if (ReferenceEquals(SpeechBubble, null))
            {
                SpeechBubble = Widget.Create<SpeechBubble>();
            }

            SpeechBubble.enable = true;

            if (SpeechBubble.gameObject.activeSelf)
            {
                return false;
            }

            if (list.Length > 0)
            {
                var join = string.Join("_", list.Select(x => x.ToString()).ToArray());
                key = $"{key}_{join}_";
            }
            else
            {
                key = $"{key}_";
            }

            if (!SpeechBubble.SetKey(key))
            {
                return false;
            }

            if (!gameObject.activeSelf)
                return true;

            StartCoroutine(SpeechBubble.CoShowText());
            return true;
        }

        public virtual IEnumerator CoProcessDamage(
            Model.BattleStatus.Skill.SkillInfo info,
            bool isConsiderDie,
            bool isConsiderElementalType)
        {
            var dmg = info.Effect;
            var position = HudTextPosition;
            var force = DamageTextForce;

            // damage 0 = dodged.
            if (dmg <= 0)
            {
                var index = 0;
                if (this is StageMonster)
                {
                    index = 1;
                }

                MissText.Show(ActionCamera.instance.Cam, position, force, index);
                yield break;
            }

            CurrentHP -= dmg;
            Animator.Hit();
            AddHitColor();

            PopUpDmg(position, force, info, isConsiderElementalType);
        }

        protected virtual void OnDeadStart()
        {
            foreach (var vfx in _persistingVFXMap.Values)
            {
                vfx.LazyStop();
            }
        }

        protected virtual void OnDeadEnd()
        {
            Animator.Idle();
            gameObject.SetActive(false);
            actionQueue.Clear();
        }

        protected void PopUpDmg(
            Vector3 position,
            Vector3 force,
            Model.BattleStatus.Skill.SkillInfo info,
            bool isConsiderElementalType)
        {
            var dmg = info.Effect.ToString();
            var pos = transform.position;
            pos.x -= 0.2f;
            pos.y += 0.32f;
            var group = DamageText.TextGroupState.Basic;
            if (this is Player)
            {
                group = DamageText.TextGroupState.Damage;
            }

            if (info.Critical)
            {
                ActionCamera.instance.Shake();
                AudioController.PlayDamagedCritical();
                CriticalText.Show(position, force, dmg, group);
                if (info.SkillCategory == SkillCategory.NormalAttack)
                    VFXController.instance.Create<BattleAttackCritical01VFX>(pos);
            }
            else
            {
                AudioController.PlayDamaged(isConsiderElementalType
                    ? info.ElementalType
                    : ElementalType.Normal);
                DamageText.Show(ActionCamera.instance.Cam, position, force, dmg, group);
                if (info.SkillCategory == SkillCategory.NormalAttack)
                    VFXController.instance.Create<BattleAttack01VFX>(pos);
            }
        }

        #region AttackPoint & HitPoint

        protected virtual void UpdateHitPoint()
        {
            var source = GetAnimatorHitPointBoxCollider();
            if (!source)
            {
                throw new NullReferenceException(
                    $"{nameof(GetAnimatorHitPointBoxCollider)}() returns null.");
            }

            var scale = Animator.Target.transform.localScale;
            var center = source.center;
            var size = source.size;
            HitPointBoxCollider.center =
                new Vector3(center.x * scale.x, center.y * scale.y, center.z * scale.z);
            HitPointBoxCollider.size =
                new Vector3(size.x * scale.x, size.y * scale.y, size.z * scale.z);
        }

        protected abstract BoxCollider GetAnimatorHitPointBoxCollider();

        #endregion

        #region Run

        internal void InitBT()
        {
            _root = new Root();
            _root.OpenBranch(
                BT.Selector().OpenBranch(
                    BT.If(() => CanRun).OpenBranch(
                        BT.Call(ExecuteRun)
                    ),
                    BT.If(() => !CanRun).OpenBranch(
                        BT.Sequence().OpenBranch(
                            BT.Call(StopRun),
                            BT.If(() => ActionQueue.Any()).OpenBranch(
                                BT.Call(ExecuteAction)
                            )
                        )
                    )
                )
            );
        }

        public void StartRun()
        {
            RunSpeed = RunSpeedDefault;
            if (_root == null)
            {
                InitBT();
            }
        }

        protected virtual void ExecuteRun()
        {
            Animator.Run();

            Vector2 position = transform.position;
            position.x += Time.deltaTime * RunSpeed;
            transform.position = position;
        }

        public void StopRun()
        {
            RunSpeed = 0.0f;
            Animator.StopRun();
        }

        public void Run()
        {
            Animator.Run();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.gameObject.CompareTag(TargetTag))
                return;

            var character = other.gameObject.GetComponent<Actor>();
            if (!character)
                return;

            StopRunIfTargetInAttackRange(character);
        }

        private void OnTriggerStay(Collider other)
        {
            if (!other.gameObject.CompareTag(TargetTag))
                return;

            var character = other.gameObject.GetComponent<Actor>();
            if (!character)
                return;

            StopRunIfTargetInAttackRange(character);
        }

        private void StopRunIfTargetInAttackRange(Actor target)
        {
            if (target.IsDead || !TargetInAttackRange(target))
                return;

            _forceStop = true;
            StartCoroutine(CoStop(target));
            StopRun();
        }

        private IEnumerator CoStop(Actor target)
        {
            yield return new WaitUntil(() => IsDead || target.IsDead);
            _forceStop = false;
        }

        #endregion

        public virtual float CalculateRange(Actor target)
        {
            var attackRangeStartPosition = gameObject.transform.position.x + HitPointLocalOffset.x;
            var targetHitPosition = target.transform.position.x + target.HitPointLocalOffset.x;
            return attackRangeStartPosition - targetHitPosition;
        }

        public bool TargetInAttackRange(Actor target)
        {
            var diff = CalculateRange(target);
            return AttackRange > diff;
        }

        public void DisableHUD()
        {
            // No pooling. HUD Pooling causes HUD positioning bug.
            if (HPBar)
            {
                Destroy(HPBar.gameObject);
                HPBar = null;
            }

            // No pooling. HUD Pooling causes HUD positioning bug.
            if (HudContainer)
            {
                Destroy(HudContainer.gameObject);
                HudContainer = null;
            }

            if (!ReferenceEquals(CastingBar, null))
            {
                Destroy(CastingBar.gameObject);
                CastingBar = null;
            }

            if (!ReferenceEquals(SpeechBubble, null))
            {
                SpeechBubble.StopAllCoroutines();
                SpeechBubble.gameObject.SetActive(false);
                Destroy(SpeechBubble.gameObject, SpeechBubble.destroyTime);
                SpeechBubble = null;
            }
        }

        public void DisableHudContainer()
        {
            if (HudContainer)
            {
                HudContainer.UpdateAlpha(0);
                HudContainer.gameObject.SetActive(false);
                HudContainer = null;
            }
        }

        protected virtual void ProcessAttack(
            Actor target,
            Model.BattleStatus.Skill.SkillInfo skill,
            bool isLastHit,
            bool isConsiderElementalType)
        {
            if (!target) return;
            target.StopRun();
            StartCoroutine(target.CoProcessDamage(skill, isLastHit, isConsiderElementalType));
        }

        protected virtual void ProcessHeal(
            Actor target,
            Model.BattleStatus.Skill.SkillInfo info)
        {
            if (target && !info.Target!.IsDead)
            {
                target.CurrentHP = Math.Min(target.CurrentHP + info.Effect, target.HP);

                var position = transform.TransformPoint(0f, 1.7f, 0f);
                var force = new Vector3(-0.1f, 0.5f);
                var txt = info.Effect.ToString();
                PopUpHeal(position, force, txt, info.Critical);
//                Debug.LogWarning($"{Animator.Target.name}'s {nameof(ProcessHeal)} called: {CurrentHP}({Model.Stats.CurrentHP}) / {HP}({Model.Stats.LevelStats.HP}+{Model.Stats.BuffStats.HP})");
            }
        }

        private void ProcessBuff(Actor target, Model.BattleStatus.Skill.SkillInfo info)
        {
            if (target && !info.Target!.IsDead)
            {
                var position = transform.TransformPoint(0f, 1.7f, 0f);
                var force = new Vector3(-0.1f, 0.5f);
                var buff = info.Buff;
                var effect = Game.instance.Stage.BuffController.Get<Actor, BuffVFX>(target, buff);

#if TEST_LOG
                Debug.Log($"[TEST_LOG][ProcessBuff] [Buff] {effect.name} {buff.BuffInfo.Id} {info.Affected} {info?.DispelList?.Count()}");
#endif

                effect.Play();
                if (effect.IsPersisting)
                {
                    target.AttachPersistingVFX(buff.BuffInfo.GroupId, effect);
                    StartCoroutine(BuffController.CoChaseTarget(effect, target.transform));
                }

                target.UpdateHpBar();
                //Debug.LogWarning($"{Animator.Target.name}'s {nameof(ProcessBuff)} called: {CurrentHP}({Model.Stats.CurrentHP}) / {HP}({Model.Stats.LevelStats.HP}+{Model.Stats.BuffStats.HP})");
            }
        }

        private void AttachPersistingVFX(int groupId, BuffVFX vfx)
        {
            if (_persistingVFXMap.TryGetValue(groupId, out var prevVFX))
            {
                prevVFX.Stop();
                _persistingVFXMap.Remove(groupId);
            }

            _persistingVFXMap[groupId] = vfx;
        }

        private void PopUpHeal(Vector3 position, Vector3 force, string dmg, bool critical)
        {
            DamageText.Show(ActionCamera.instance.Cam, position, force, dmg, DamageText.TextGroupState.Heal);
            VFXController.instance.CreateAndChase<BattleHeal01VFX>(transform, HealOffset);
        }

        #region Animation

        private void PreAnimationForTheKindOfAttack()
        {
            AttackEndCalled = false;
            RunSpeed = 0.0f;
        }

        private bool CheckAttackEnd()
        {
            return AttackEndCalled || Animator.IsIdle();
        }

        protected virtual void ShowCutscene()
        {
            // Do nothing.
        }

        private IEnumerator CoAnimationAttack(bool isCritical)
        {
            while (true)
            {
                PreAnimationForTheKindOfAttack();
                if (isCritical)
                {
                    Animator.CriticalAttack();
                }
                else
                {
                    Animator.Attack();
                }

                yield return new WaitForEndOfFrame();
                yield return new WaitUntil(CheckAttackEnd);
                if (Animator.IsIdle())
                {
                    continue;
                }

                PostAnimationForTheKindOfAttack();
                break;
            }
        }

        private IEnumerator CoAnimationCastAttack(bool isCritical)
        {
            while (true)
            {
                PreAnimationForTheKindOfAttack();
                if (isCritical)
                {
                    Animator.CriticalAttack();
                }
                else
                {
                    Animator.CastAttack();
                }

                yield return new WaitForEndOfFrame();
                yield return new WaitUntil(CheckAttackEnd);
                if (Animator.IsIdle())
                {
                    continue;
                }

                PostAnimationForTheKindOfAttack();
                break;
            }
        }


        private IEnumerator CoAnimationCastBlow(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> infos)
        {
            var info = infos.First();
            var target = info.Target;
            var copy = new Model.BattleStatus.Skill.SkillInfo(target.Id, target.IsDead, target.Thorn, info.Effect,
                info.Critical, info.SkillCategory,
                info.WaveTurn, ElementalType.Normal, info.SkillTargetType, info.Buff, target);
            yield return StartCoroutine(CoAnimationCast(copy));

            var pos = transform.position;
            yield return CoAnimationCastAttack(infos.Any(skillInfo => skillInfo.Critical));
            if (info.ElementalType != ElementalType.Normal)
            {
                var effect = Game.instance.Stage.SkillController.GetBlowCasting(
                    pos,
                    info.SkillCategory,
                    info.ElementalType);
                effect.Play();
            }
            yield return new WaitForSeconds(0.2f);
            PostAnimationForTheKindOfAttack();
        }

        protected virtual IEnumerator CoAnimationCast(Model.BattleStatus.Skill.SkillInfo info)
        {
            PreAnimationForTheKindOfAttack();

            var sfxCode = AudioController.GetElementalCastingSFX(info.ElementalType);
            AudioController.instance.PlaySfx(sfxCode);
            Animator.Cast();
            var pos = transform.position;
            var effect = Game.instance.Stage.SkillController.Get(pos, info.ElementalType);
            effect.Play();
            yield return new WaitForSeconds(0.6f);

            PostAnimationForTheKindOfAttack();
        }

        private IEnumerator CoAnimationBuffCast(Model.BattleStatus.Skill.SkillInfo info)
        {
            PreAnimationForTheKindOfAttack();

            var sfxCode = AudioController.GetElementalCastingSFX(info.ElementalType);
            AudioController.instance.PlaySfx(sfxCode);
            Animator.Cast();
            var pos = transform.position;
            var effect = Game.instance.Stage.BuffController.Get(pos, info.Buff);
            effect.Play();
#if TEST_LOG
                Debug.Log($"[TEST_LOG][CoAnimationBuffCast] [Buff] {effect.name} {info.Buff.BuffInfo.Id}");
#endif
            yield return new WaitForSeconds(0.6f);

            PostAnimationForTheKindOfAttack();
        }

        private void PostAnimationForTheKindOfAttack()
        {
            var enemy = GetComponentsInChildren<Actor>()
                .Where(c => c.gameObject.CompareTag(TargetTag))
                .OrderBy(c => c.transform.position.x).FirstOrDefault();
            if (enemy != null && !TargetInAttackRange(enemy))
                RunSpeed = RunSpeedDefault;
        }

        #endregion

        #region Skill

        public IEnumerator CoNormalAttack(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            var skillInfosCount = skillInfos.Count;
            var battleWidget = Widget.Find<Nekoyume.UI.Battle>();

            yield return StartCoroutine(
                CoAnimationAttack(skillInfos.Any(skillInfo => skillInfo.Critical)));

            for (var i = 0; i < skillInfosCount; i++)
            {
                var info = skillInfos[i];
                var target = Game.instance.Stage.GetActor(info.Target);
                ProcessAttack(target, info, info.Target.IsDead, false);
                if (this is Player && !(this is EnemyPlayer))
                    battleWidget.ShowComboText(info.Effect > 0);
            }

            _expiredColorKeys.Add(SpineColorKey.Test);
        }

        public IEnumerator CoBlowAttack(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            var skillInfosCount = skillInfos.Count;

            if (skillInfos.First().SkillTargetType == SkillTargetType.Enemy)
            {
                yield return StartCoroutine(CoAnimationCast(skillInfos.First()));
                yield return StartCoroutine(
                    CoAnimationCastAttack(skillInfos.Any(skillInfo => skillInfo.Critical)));
            }
            else
            {
                yield return StartCoroutine(CoAnimationCastBlow(skillInfos));
            }

            for (var i = 0; i < skillInfosCount; i++)
            {
                var info = skillInfos[i];
                var target = Game.instance.Stage.GetActor(info.Target);
                if (target is null)
                    continue;

                if (info.ElementalType != ElementalType.Normal)
                {
                    var effect = Game.instance.Stage.SkillController.Get<SkillBlowVFX>(target, info);
                    if (effect is null)
                        continue;
                    effect.Play();
                }

                ProcessAttack(target, info, info.Target.IsDead, true);
            }
        }

        public IEnumerator CoShatterStrike(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            Vector3 effectPos = transform.position;
            effectPos.y += 0.55f;
            var effectObj = Game.instance.Stage.objectPool.Get("ShatterStrike_casting", false, effectPos) ??
                            Game.instance.Stage.objectPool.Get("ShatterStrike_casting", true, effectPos);
            var castEffect = effectObj.GetComponent<VFX.VFX>();
            if (castEffect != null)
            {
                castEffect.Play();
            }

            PreAnimationForTheKindOfAttack();
            Animator.Cast();
            yield return new WaitForSeconds(0.6f);
            PostAnimationForTheKindOfAttack();

            yield return StartCoroutine(
                    CoAnimationCastAttack(skillInfos.Any(skillInfo => skillInfo.Critical)));

            for (var i = 0; i < skillInfos.Count; i++)
            {
                var info = skillInfos[i];
                var target = Game.instance.Stage.GetActor(info.Target);
                if (target is null)
                    continue;

                Vector3 targetEffectPos = target.transform.position;
                targetEffectPos.y = Stage.StageStartPosition + 0.32f;
                var targetEffectObj = Game.instance.Stage.objectPool.Get("ShatterStrike_magical", false, targetEffectPos) ??
                                Game.instance.Stage.objectPool.Get("ShatterStrike_magical", true, targetEffectPos);
                var strikeEffect = targetEffectObj.GetComponent<VFX.VFX>();
                if (strikeEffect is null)
                    continue;
                strikeEffect.Play();

                ProcessAttack(target, info, info.Target.IsDead, true);
            }
        }

        public IEnumerator CoDoubleAttackWithCombo(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            var skillInfosFirst = skillInfos.First();
            var skillInfosCount = skillInfos.Count;

            var battleWidget = Widget.Find<Nekoyume.UI.Battle>();

            for (var i = 0; i < skillInfosCount; i++)
            {
                var info = skillInfos[i];
                var target = Game.instance.Stage.GetActor(info.Target);
                if (target is null)
                    continue;

                Vector3 effectPos = target.transform.position;
                effectPos.x += 0.3f;
                effectPos.y = Stage.StageStartPosition + 0.32f;

                var first = skillInfosFirst == info;

                yield return StartCoroutine(CoAnimationAttack(info.Critical));

                var effectObj = Game.instance.Stage.objectPool.Get($"TwinAttack_0{i + 1}", false, effectPos) ??
                            Game.instance.Stage.objectPool.Get($"TwinAttack_0{i + 1}", true, effectPos);
                var effect = effectObj.GetComponent<VFX.VFX>();
                if (effect != null)
                {
                    effect.Play();
                }

                ProcessAttack(target, info, !first, true);
                if (this is Player && !(this is EnemyPlayer))
                    battleWidget.ShowComboText(info.Effect > 0);
            }
        }

        public IEnumerator CoDoubleAttack(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            var skillInfosFirst = skillInfos.First();
            var skillInfosCount = skillInfos.Count;
            for (var i = 0; i < skillInfosCount; i++)
            {
                var info = skillInfos[i];
                var target = Game.instance.Stage.GetActor(info.Target);
                if (target is null)
                    continue;

                var first = skillInfosFirst == info;
                var effect = Game.instance.Stage.SkillController.Get<SkillDoubleVFX>(target, info);
                if (effect is null)
                    continue;

                yield return StartCoroutine(CoAnimationAttack(info.Critical));
                if (first)
                {
                    effect.FirstStrike();
                }
                else
                {
                    effect.SecondStrike();
                }

                ProcessAttack(target, info, !first, true);
            }
        }

        public IEnumerator CoAreaAttack(
            IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            var skillInfosFirst = skillInfos.First();
            var skillInfosCount = skillInfos.Count;

            ShowCutscene();
            yield return StartCoroutine(CoAnimationCast(skillInfosFirst));

            var effectTarget = Game.instance.Stage.GetActor(skillInfosFirst.Target);
            if (effectTarget is null)
                yield break;

            var effect =
                Game.instance.Stage.SkillController.Get<SkillAreaVFX>(effectTarget,
                    skillInfosFirst);
            if (effect is null)
                yield break;

            Model.BattleStatus.Skill.SkillInfo trigger = null;
            if (effect.finisher)
            {
                var count = FindObjectsOfType(effectTarget.GetType()).Length;
                trigger = skillInfos.Skip(skillInfosCount - count).First();
            }

            effect.Play();
            yield return new WaitForSeconds(0.5f);

            var isTriggerOn = false;
            for (var i = 0; i < skillInfosCount; i++)
            {
                var info = skillInfos[i];
                var target = Game.instance.Stage.GetActor(info.Target);
                if (target is null)
                    continue;

                yield return new WaitForSeconds(0.14f);
                if (trigger == info)
                {
                    isTriggerOn = true;

                    if (!info.Critical)
                    {
                        yield return new WaitForSeconds(0.2f);
                    }

                    if (info.ElementalType == ElementalType.Fire)
                    {
                        effect.StopLoop();
                        yield return new WaitForSeconds(0.1f);
                    }

                    var coroutine = StartCoroutine(CoAnimationCastAttack(info.Critical));
                    if (info.ElementalType == ElementalType.Water)
                    {
                        yield return new WaitForSeconds(0.1f);
                        effect.StopLoop();
                    }

                    yield return coroutine;
                    effect.Finisher();
                    ProcessAttack(target, info, true, true);
                    if (info.ElementalType != ElementalType.Fire
                        && info.ElementalType != ElementalType.Water)
                    {
                        effect.StopLoop();
                    }

                    yield return new WaitUntil(() => effect.last.isStopped);
                }
                else
                {
                    ProcessAttack(target, info, isTriggerOn, isTriggerOn);
                }
            }

            yield return new WaitForSeconds(0.5f);
        }

        public IEnumerator CoHeal(IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            yield return StartCoroutine(CoAnimationCast(skillInfos.First()));

            foreach (var info in skillInfos)
            {
                var target = Game.instance.Stage.GetActor(info.Target);
                ProcessHeal(target, info);
            }

            Animator.Idle();
        }

        public IEnumerator CoHealWithoutAnimation(IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null ||
                skillInfos.Count == 0)
                yield break;

            foreach (var info in skillInfos)
            {
                var target = Game.instance.Stage.GetActor(info.Target);
                ProcessHeal(target, info);
            }
        }

        public IEnumerator CoBuff(IReadOnlyList<Model.BattleStatus.Skill.SkillInfo> skillInfos)
        {
            if (skillInfos is null || skillInfos.Count == 0)
                yield break;

            yield return StartCoroutine(CoAnimationBuffCast(skillInfos.First()));

            HashSet<Actor> dispeledTargets = new HashSet<Actor>();
            foreach (var info in skillInfos)
            {
                var target = Game.instance.Stage.GetActor(info.Target);
                ProcessBuff(target, info);
                if (!info.Affected || (info.DispelList != null && info.DispelList.Count() > 0))
                {
                    dispeledTargets.Add(target);
                }
            }

            Animator.Idle();

            if(dispeledTargets.Count > 0)
            {
                yield return new WaitForSeconds(.4f);
            }
            foreach (var item in dispeledTargets)
            {
                Vector3 effectPos = item.transform.position;

                var effectObj = Game.instance.Stage.objectPool.Get("buff_dispel_success", false, effectPos) ??
                            Game.instance.Stage.objectPool.Get("buff_dispel_success", true, effectPos);
                var dispellEffect = effectObj.GetComponent<VFX.VFX>();
                if (dispellEffect != null)
                {
                    dispellEffect.Play();
                }
            }
        }

        #endregion

        public bool HasAction()
        {
            return actionQueue.Any() || _action is not null;
        }

        public void AddAction(ActionParams actionParams)
        {
            actionQueue.Enqueue(actionParams);
        }

        private void ExecuteAction()
        {
            StartCoroutine(CoExecuteAction());
        }

        private IEnumerator CoExecuteAction()
        {
            if (_action is not null)
            {
                yield break;
            }
            _action = actionQueue.Dequeue();

            var cts = new CancellationTokenSource();
            ActionTimer(cts).Forget();

            foreach (var info in _action.skillInfos)
            {
                var target = info.Target;
                if (target == null || target.IsDead)
                {
                    continue;
                }
                var targetActor = Game.instance.Stage.GetActor(target);
                if (!targetActor || targetActor == this || !targetActor.HasAction())
                {
                    continue;
                }

                var time = Time.time;
                yield return new WaitUntil(() => Time.time - time > 10f || !targetActor.HasAction());
            }

            yield return new WaitForSeconds(StageConfig.instance.actionDelay);
            if (_action != null)
            {
                yield return StartCoroutine(Game.instance.Stage.CoSkill(_action));
            }
            _action     = null;
            _forceStop = false;
            cts.Cancel();
            cts.Dispose();
        }

        private async UniTask ActionTimer(CancellationTokenSource cts)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(20), cancellationToken: cts.Token);
            NcDebug.LogError($"[{nameof(Actor)}] ActionTimer Timeout. {gameObject.name}");
            _action = null;
        }

        public void Dead()
        {
            StartCoroutine(Dying());
        }

        public void SetSortingLayer(int layerId, int orderInLayer = 0)
        {
            sortingGroup.sortingLayerID = layerId;
            sortingGroup.sortingOrder = orderInLayer;
        }

        public void Ready()
        {
            AttackEndCalled = false;
        }

#region SpineColor
        private readonly Priority_Queue.SimplePriorityQueue<SpineColorSetting, int> _colorPq = new();

        private readonly HashSet<SpineColorKey> _expiredColorKeys = new();

        private int _currentColorHash;

        private void UpdateColor()
        {
            if (_colorPq.Count == 0)
            {
                return;
            }

            ExpireColors();

            while (_colorPq.Count > 0)
            {
                var setting = _colorPq.First;
                if (setting.IsExpired)
                {
                    _colorPq.Dequeue();
                    if (_colorPq.Count == 0)
                    {
                        SetDefaultColor();
                    }
                    continue;
                }

                if (setting.GetHashCode() == _currentColorHash)
                {
                    break;
                }

                _currentColorHash = setting.GetHashCode();
                setting.SetColor(this);
                return;
            }
        }

        private void ExpireColors()
        {
            foreach (var colorSetting in _colorPq)
            {
                foreach (var expiredColorKey in _expiredColorKeys)
                {
                    colorSetting.ExpireByKey(expiredColorKey);
                }
                colorSetting.UpdateDuration(Time.deltaTime);

                if (colorSetting.IsExpired)
                {
                    colorSetting.Expire();
                }
            }
            _expiredColorKeys.Clear();
        }

        public virtual void SetSpineColor(Color color, int propertyID = -1)
        {
        }

        private void SetDefaultColor()
        {
            SetSpineColor(Color.white, SpineColorSetting.ColorPropertyId);
            SetSpineColor(Color.black, SpineColorSetting.BlackPropertyId);
            _currentColorHash = 0;
        }

        public void AddHitColor()
        {
            var color = new Color(1, 0.6651f, 0.65566f, 1f);
            var black = new Color(0.2452f, 0.091f, 0.091f, 1f);
            _colorPq.Enqueue(new SpineColorSetting(color, black, true, 0.3f), (int)SpineColorPriority.Hit);
        }

        public void AddSpineColor(Color color, bool hasDuration = false, float duration = 0f, SpineColorKey key = SpineColorKey.None)
        {
            _colorPq.Enqueue(new SpineColorSetting(color, hasDuration, duration, key), 0);
        }

        public void AddSpineColor(Color color, Color black, bool hasDuration = false, float duration = 0f, SpineColorKey key = SpineColorKey.None)
        {
            _colorPq.Enqueue(new SpineColorSetting(color, black, hasDuration, duration, key), 0);
        }
#endregion SpineColor
    }

    public class ActionParams
    {
        public Actor character;
        public IEnumerable<Model.BattleStatus.Skill.SkillInfo> skillInfos;
        public IEnumerable<Model.BattleStatus.Skill.SkillInfo> buffInfos;
        public Func<IReadOnlyList<Model.BattleStatus.Skill.SkillInfo>, IEnumerator> func;

        public ActionParams(Actor actor,
            IEnumerable<Model.BattleStatus.Skill.SkillInfo> enumerable,
            IEnumerable<Model.BattleStatus.Skill.SkillInfo> buffInfos1,
            Func<IReadOnlyList<Model.BattleStatus.Skill.SkillInfo>, IEnumerator> coNormalAttack)
        {
            character = actor;
            skillInfos = enumerable;
            buffInfos = buffInfos1;
            func = coNormalAttack;
        }
    }
}
