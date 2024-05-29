using CommandLine;
using Cysharp.Threading.Tasks;
using Nekoyume.L10n;
using Nekoyume.Model.AdventureBoss;
using Nekoyume.Model.Mail;
using Nekoyume.UI.Scroller;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using System;

namespace Nekoyume.UI.Module
{
    using GluonGui.WorkspaceWindow.Views.WorkspaceExplorer.Explorer.Operations;
    using Nekoyume.Helper;
    using Nekoyume.UI.Model;
    using UniRx;

    public class WorldMapAdventureBoss : MonoBehaviour
    {
        [SerializeField] private GameObject open;
        [SerializeField] private GameObject wantedClose;
        [SerializeField] private TextMeshProUGUI[] remainingBlockIndexs;
        [SerializeField] private GameObject loadingIndicator;
        [SerializeField] private WorldButton worldButton;
        [SerializeField] private Transform bossImageParent;

        private readonly List<System.IDisposable> _disposables = new();
        private long _remainingBlockIndex = 0;

        private int _bossId;
        private GameObject _bossImage;

        private void Awake()
        {
            worldButton.OnClickSubject.Subscribe(button =>
            {
                var curState = Game.Game.instance.AdventureBossData.CurrentState.Value;
                if(curState == AdventureBossData.AdventureBossSeasonState.Ready)
                {
                    OnClickOpenEnterBountyPopup();
                }

                if(curState == AdventureBossData.AdventureBossSeasonState.Progress)
                {
                    OnClickOpenAdventureBoss();
                }
            }).AddTo(gameObject);
        }

        private void OnEnable()
        {
            Game.Game.instance.Agent.BlockIndexSubject
                .Subscribe(UpdateViewAsync)
                .AddTo(_disposables);

            Game.Game.instance.AdventureBossData.CurrentState.Subscribe(OnAdventureBossStateChanged).AddTo(_disposables);
        }

        private void OnDisable()
        {
            _disposables.DisposeAllAndClear();
        }

        public void SetLoadingIndicator(bool isActive)
        {
            loadingIndicator.SetActive(isActive);
        }

        private void UpdateViewAsync(long blockIndex)
        {
            var seasonInfo = Game.Game.instance.AdventureBossData.SeasonInfo.Value;

            if (Game.Game.instance.AdventureBossData.CurrentState.Value == AdventureBossData.AdventureBossSeasonState.Ready)
            {
                SetDefualtRemainingBlockIndexs();
                return;
            }

            if (Game.Game.instance.AdventureBossData.CurrentState.Value == AdventureBossData.AdventureBossSeasonState.End)
            {
                var adventureBossData = Game.Game.instance.AdventureBossData;
                if(adventureBossData.EndedSeasonInfos.TryGetValue(adventureBossData.SeasonInfo.Value.Season, out var endedSeasonInfo))
                {
                    RefreshBlockIndexText(blockIndex, endedSeasonInfo.NextStartBlockIndex);
                    return;
                }
            }

            RefreshBlockIndexText(blockIndex, seasonInfo.EndBlockIndex);
        }

        private void RefreshBlockIndexText(long blockIndex, long targetBlock)
        {
            _remainingBlockIndex = targetBlock - blockIndex;
            var timeText = $"{_remainingBlockIndex:#,0}({_remainingBlockIndex.BlockRangeToTimeSpanString()})";
            foreach (var text in remainingBlockIndexs)
            {
                text.text = timeText;
            }
        }

        private void SetDefualtRemainingBlockIndexs()
        {
            foreach (var text in remainingBlockIndexs)
            {
                text.text = "(-)";
            }
        }

        public void OnClickOpenEnterBountyPopup()
        {
            Widget.Find<AdventureBossEnterBountyPopup>().Show();
        }

        public void OnClickOpenAdventureBoss()
        {
            Widget.Find<LoadingScreen>().Show();
            try
            {
                Game.Game.instance.AdventureBossData.RefreshAllByCurrentState().ContinueWith(() =>
                {
                    Widget.Find<LoadingScreen>().Close();
                    Widget.Find<AdventureBoss>().Show();
                });
            }
            catch (System.Exception e)
            {
                NcDebug.LogError(e);
                Widget.Find<LoadingScreen>().Close();
            }
        }

        public void OnClickAdventureSeasonAlert()
        {
            var remaingTimespan = _remainingBlockIndex.BlockToTimeSpan();
            OneLineSystem.Push(MailType.System, L10nManager.Localize("NOTIFICATION_ADVENTURE_BOSS_REMAINIG_TIME", remaingTimespan.Hours, remaingTimespan.Minutes%60), NotificationCell.NotificationType.Notification);
        }

        private void OnAdventureBossStateChanged(AdventureBossData.AdventureBossSeasonState state)
        {
            switch (state)
            {
                case AdventureBossData.AdventureBossSeasonState.Ready:
                    worldButton.Unlock();
                    open.SetActive(true);
                    wantedClose.SetActive(true);
                    worldButton.HasNotification.Value = true;
                    break;
                case AdventureBossData.AdventureBossSeasonState.Progress:
                    worldButton.Unlock();
                    open.SetActive(true);
                    wantedClose.SetActive(false);
                    if(_bossId != Game.Game.instance.AdventureBossData.SeasonInfo.Value.BossId)
                    {
                        if(_bossImage != null)
                        {
                            DestroyImmediate(_bossImage);
                        }
                        _bossId = Game.Game.instance.AdventureBossData.SeasonInfo.Value.BossId;
                        _bossImage = Instantiate(SpriteHelper.GetBigCharacterIconFace(_bossId), bossImageParent);
                        _bossImage.transform.localPosition = Vector3.zero;
                        _bossImage.transform.localScale = Vector3.one * 0.5f;
                    }
                    worldButton.HasNotification.Value = true;
                    break;
                case AdventureBossData.AdventureBossSeasonState.None:
                case AdventureBossData.AdventureBossSeasonState.End:
                default:
                    worldButton.HasNotification.Value = false;
                    worldButton.Lock();
                    open.SetActive(false);
                    SetDefualtRemainingBlockIndexs();
                    break;
            }
        }
    }
}
