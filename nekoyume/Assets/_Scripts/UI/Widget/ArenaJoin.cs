﻿using System;
using System.Collections.Generic;
using System.Linq;
using Nekoyume.Game;
using Nekoyume.Game.Controller;
using Nekoyume.Model.EnumType;
using Nekoyume.UI.Module;
using Nekoyume.UI.Module.Arena.Join;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace Nekoyume.UI
{
    using UniRx;

    public class ArenaJoin : Widget
    {
#if UNITY_EDITOR
        [SerializeField]
        private bool _useSo;

        [SerializeField]
        private ArenaJoinSO _so;
#endif

        [SerializeField]
        private ArenaJoinSeasonScroll _scroll;

        [SerializeField]
        private ArenaJoinSeasonBarScroll _barScroll;

        [SerializeField]
        private int _barPointCount;

        [SerializeField]
        private ArenaJoinSeasonInfo _info;

        [SerializeField]
        private Button _joinButton;

        [SerializeField]
        private Button _paymentButton;

        [SerializeField]
        private Button _earlyPaymentButton;

        [SerializeField]
        private Button _backButton;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        protected override void Awake()
        {
            base.Awake();

            _backButton.OnClickAsObservable().Subscribe(_ =>
            {
                Close(true);
                Game.Event.OnRoomEnter.Invoke(true);
            }).AddTo(gameObject);
            _joinButton.onClick.AsObservable().Subscribe(_ =>
            {
                AudioController.PlayClick();
                Find<ArenaBoard>().Show();
                Close();
            }).AddTo(gameObject);
            _paymentButton.onClick.AsObservable().Subscribe(_ =>
            {
                AudioController.PlayClick();
                Find<ArenaBoard>().Show();
                Close();
            }).AddTo(gameObject);
            _earlyPaymentButton.onClick.AsObservable().Subscribe().AddTo(gameObject);

            CloseWidget = () =>
            {
                Close(true);
                Game.Event.OnRoomEnter.Invoke(true);
            };
        }

        public override void Show(bool ignoreShowAnimation = false)
        {
            Find<HeaderMenuStatic>().UpdateAssets(HeaderMenuStatic.AssetVisibleState.Arena);
            InitializeScrolls(_disposables);
            UpdateInfo();
            UpdateButtons();
            base.Show(ignoreShowAnimation);
        }

        public override void Close(bool ignoreCloseAnimation = false)
        {
            _disposables.DisposeAllAndClear();
            base.Close(ignoreCloseAnimation);
        }

        private void InitializeScrolls(IList<IDisposable> disposables)
        {
            var scrollData = GetScrollData();
            var selectedIndex = 0;
            _scroll.SetData(scrollData, selectedIndex);
            var barIndexOffset = (int)math.ceil(_barPointCount / 2f) - 1;
            _barScroll.SetData(
                GetBarScrollData(barIndexOffset),
                ReverseScrollIndex(selectedIndex));

            // NOTE: Scroll events should subscribe after set data. 
            _scroll.OnSelectionChanged
                .Select(ReverseScrollIndex)
                .Subscribe(reversedIndex =>
                {
                    _barScroll.SelectCell(reversedIndex, false);
                    UpdateInfo();
                    UpdateButtons();
                })
                .AddTo(disposables);
            _barScroll.OnSelectionChanged
                .Select(ReverseScrollIndex)
                .Subscribe(reversedIndex =>
                {
                    _scroll.SelectCell(reversedIndex, false);
                    UpdateInfo();
                    UpdateButtons();
                })
                .AddTo(disposables);
        }

        private IList<ArenaJoinSeasonItemData> GetScrollData()
        {
#if UNITY_EDITOR
            if (_useSo && _so)
            {
                int? GetSeasonNumber(
                    IList<ArenaJoinSO.ArenaData> list,
                    ArenaJoinSO.RoundDataBridge data)
                {
                    var seasonNumber = 0;
                    foreach (var arenaData in list)
                    {
                        if (arenaData.RoundDataBridge.ArenaType == ArenaType.Season)
                        {
                            seasonNumber++;
                        }

                        if (arenaData.RoundDataBridge.Round == data.Round)
                        {
                            return arenaData.RoundDataBridge.ArenaType == ArenaType.Season
                                ? seasonNumber
                                : (int?)null;
                        }
                    }

                    return null;
                }

                return _so.ArenaDataList
                    .Select(data => new ArenaJoinSeasonItemData
                    {
                        RoundData = data.RoundDataBridge.ToRoundData(),
                        SeasonNumber = GetSeasonNumber(_so.ArenaDataList, data.RoundDataBridge),
                    }).ToList();
            }
#endif

            var blockIndex = Game.Game.instance.Agent.BlockIndex;
            if (!TableSheets.Instance.ArenaSheet.TryGetRowByBlockIndex(
                    blockIndex,
                    out var row))
            {
                Debug.LogError($"Failed to get arena row data by block index: {blockIndex}");
                return new List<ArenaJoinSeasonItemData>();
            }

            return row.Round
                .Select(roundData => new ArenaJoinSeasonItemData
                {
                    RoundData = roundData,
                    SeasonNumber = row.TryGetSeasonNumber(roundData.Round, out var seasonNumber)
                        ? seasonNumber
                        : (int?)null,
                }).ToList();
        }

        private IList<ArenaJoinSeasonBarItemData> GetBarScrollData(
            int barIndexOffset)
        {
            var cellCount = _barPointCount;
            return Enumerable.Range(0, cellCount)
                .Select(index => new ArenaJoinSeasonBarItemData
                {
                    visible = index == barIndexOffset,
                })
                .ToList();
        }

        private int ReverseScrollIndex(int scrollIndex) =>
            _barPointCount - scrollIndex - 1;

        private void UpdateInfo()
        {
            string getText(ArenaJoinSeasonItemData data) => data.RoundData.ArenaType switch
            {
                ArenaType.OffSeason => "off-season",
                ArenaType.Season => $"season #{data.SeasonNumber}",
                ArenaType.Championship => $"championship #{data.RoundData.ChampionshipNumber}",
                _ => throw new ArgumentOutOfRangeException()
            };

            _info.SetData(
                getText(_scroll.SelectedItemData),
                GetMedalId(),
                GetConditions(),
                GetRewardType(_scroll.SelectedItemData));
        }

        private void UpdateButtons()
        {
            // TODO: 아레나 라운드 정보에 따라 버튼 상태를 갱신한다.
            _joinButton.gameObject.SetActive(true);
            _paymentButton.gameObject.SetActive(false);
            _earlyPaymentButton.gameObject.SetActive(false);
        }

        private int GetMedalId()
        {
#if UNITY_EDITOR
            if (_useSo && _so)
            {
                return _so.MedalId;
            }
#endif

            return 700000;
        }

        private (int max, int current) GetConditions()
        {
#if UNITY_EDITOR
            if (_useSo && _so)
            {
                return _so.Conditions;
            }
#endif

            return (100, 0);
        }

        private ArenaJoinSeasonInfo.RewardType GetRewardType(ArenaJoinSeasonItemData data)
        {
#if UNITY_EDITOR
            if (_useSo && _so)
            {
                var soData = _so.ArenaDataList.FirstOrDefault(soData =>
                    soData.RoundDataBridge.Equals(data.RoundData));
                return soData is null
                    ? ArenaJoinSeasonInfo.RewardType.None
                    : soData.RewardType;
            }
#endif

            return ArenaJoinSeasonInfo.RewardType.Medal;
        }
    }
}
