using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Cysharp.Threading.Tasks;
using Libplanet;
using Libplanet.Assets;
using Libplanet.Tx;
using Lib9c.Renderers;
using mixpanel;
using Nekoyume.Action;
using Nekoyume.Game.Character;
using Nekoyume.Model.Item;
using Nekoyume.State;
using Nekoyume.ActionExtensions;
using Nekoyume.Extensions;
using Nekoyume.Game;
using Nekoyume.Helper;
using Nekoyume.L10n;
using Nekoyume.Model.Mail;
using Nekoyume.Model.State;
using Nekoyume.State.Subjects;
using Nekoyume.UI;
using Nekoyume.UI.Scroller;
using UnityEngine;
using Material = Nekoyume.Model.Item.Material;
using RedeemCode = Nekoyume.Action.RedeemCode;

#if LIB9C_DEV_EXTENSIONS || UNITY_EDITOR
using Lib9c.DevExtensions.Action;
#endif

namespace Nekoyume.BlockChain
{
    using Nekoyume.PandoraBox;
    //using PlayFab;
    using System.Drawing;
    using UniRx;

    /// <summary>
    /// Creates an action of the game and puts it in the agent.
    /// </summary>
    public class ActionManager : IDisposable
    {
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(360f);

        private readonly IAgent _agent;

        private Guid? _lastBattleActionId;

        private readonly Dictionary<Guid, (TxId txId, long updatedBlockIndex)> _actionIdToTxIdBridge =
            new Dictionary<Guid, (TxId txId, long updatedBlockIndex)>();

        private readonly Dictionary<Guid, DateTime> _actionEnqueuedDateTimes = new Dictionary<Guid, DateTime>();

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        public static ActionManager Instance => Game.Game.instance.ActionManager;

        public static bool IsLastBattleActionId(Guid actionId) => actionId == Instance._lastBattleActionId;

        public Exception HandleException(Guid actionId, Exception e)
        {
            if (e is TimeoutException)
            {
                var txId = _actionIdToTxIdBridge.ContainsKey(actionId)
                    ? (TxId?)_actionIdToTxIdBridge[actionId].txId
                    : null;
                e = new ActionTimeoutException(e.Message, txId, actionId);
            }

            Debug.LogException(e);
            return e;
        }

        public ActionManager(IAgent agent)
        {
            _agent = agent ?? throw new ArgumentNullException(nameof(agent));
            _agent.BlockIndexSubject.Subscribe(blockIndex =>
            {
                var actionIds = _actionIdToTxIdBridge
                    .Where(pair => pair.Value.updatedBlockIndex < blockIndex - 100)
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var actionId in actionIds)
                {
                    _actionIdToTxIdBridge.Remove(actionId);
                }
            }).AddTo(_disposables);
            _agent.OnMakeTransaction.Subscribe(tuple =>
            {
                var (tx, actions) = tuple;
                var gameActions = actions
                    .Select(e => e.InnerAction)
                    .OfType<GameAction>()
                    .ToArray();
                foreach (var gameAction in gameActions)
                {
                    _actionIdToTxIdBridge[gameAction.Id] = (tx.Id, _agent.BlockIndex);
                }
            }).AddTo(_disposables);
        }

        public bool TryPopActionEnqueuedDateTime(Guid actionId, out DateTime enqueuedDateTime)
        {
            if (!_actionEnqueuedDateTimes.TryGetValue(actionId, out enqueuedDateTime))
            {
                return false;
            }

            _actionEnqueuedDateTimes.Remove(actionId);
            return true;
        }

        //|||||||||||||| PANDORA START CODE |||||||||||||||||||
        public IObservable<ActionEvaluation<TransferAsset>> TransferAsset(
            Address sender,
            Address recipient,
            FungibleAssetValue amount,
            string memo)
        {
            // Create an action.
            var action = new TransferAsset(
                sender,
                recipient,
                amount,
                memo);

            // Request to create a transaction to IAgent inside of ProcessAction method.
            ProcessAction(action);

            // Return observable of the first render of the `TransferAsset` rendered from now.
            return _agent.ActionRenderer.EveryRender<TransferAsset>()
                .Timeout(ActionTimeout)
                .First()
                .ObserveOnMainThread();
        }

        public IObservable<ActionEvaluation<ClaimStakeReward>> ClaimStakeReward()
        {
            var action = new ClaimStakeReward3(States.Instance.CurrentAvatarState.address);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<ClaimStakeReward>()
                .Timeout(ActionTimeout)
                .First()
                .ObserveOnMainThread();
        }
        //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||

        private void ProcessAction<T>(T actionBase) where T : ActionBase
        {
            var actionType = actionBase.GetActionTypeAttribute();
            Debug.Log($"[{nameof(ActionManager)}] {nameof(ProcessAction)}() called. \"{actionType.TypeIdentifier}\"");

            _agent.EnqueueAction(States.Instance.CurrentAvatarState.address,
                actionBase); //|||||||||||||| PANDORA CODE |||||||||||||||||||

            if (actionBase is GameAction gameAction)
            {
                _actionEnqueuedDateTimes[gameAction.Id] = DateTime.Now;
            }
        }

        //|||||||||||||| PANDORA START CODE |||||||||||||||||||
        private void ProcessAction<T>(T actionBase, Address avatarAddress)
            where T : ActionBase //other than CurrentAvatarState
        {
            var actionType = actionBase.GetActionTypeAttribute();
            Debug.Log($"[{nameof(ActionManager)}] {nameof(ProcessAction)}() called. \"{actionType.TypeIdentifier}\"");

            _agent.EnqueueAction(avatarAddress, actionBase);

            if (actionBase is GameAction gameAction)
            {
                _actionEnqueuedDateTimes[gameAction.Id] = DateTime.Now;
            }
        }

        public int GetQueueCount()
        {
            return _agent.GetQueueCount();
        }

        //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||

        #region Actions

        public IObservable<ActionEvaluation<CreateAvatar>> CreateAvatar(
            int index,
            string nickName,
            int hair = 0,
            int lens = 0,
            int ear = 0,
            int tail = 0)
        {
            if (States.Instance.AvatarStates.ContainsKey(index))
            {
                throw new Exception($"Already contains {index} in {States.Instance.AvatarStates}");
            }

            var action = new CreateAvatar
            {
                index = index,
                hair = hair,
                lens = lens,
                ear = ear,
                tail = tail,
                name = nickName,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            return _agent.ActionRenderer.EveryRender<CreateAvatar>()
                .Timeout(ActionTimeout)
                .SkipWhile(eval => !eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    Game.Game.instance.BackToNest();
                    throw HandleException(action.Id, e);
                });
        }

        public IObservable<ActionEvaluation<MimisbrunnrBattle>> MimisbrunnrBattle(
            List<Guid> costumes,
            List<Guid> equipments,
            List<Consumable> foods,
            List<RuneSlotInfo> runeInfos,
            int worldId,
            int stageId,
            int playCount)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            costumes ??= new List<Guid>();
            equipments ??= new List<Guid>();
            foods ??= new List<Consumable>();
            runeInfos ??= new List<RuneSlotInfo>();

            var action = new MimisbrunnrBattle
            {
                Costumes = costumes,
                Equipments = equipments,
                Foods = foods.Select(f => f.ItemId).ToList(),
                RuneInfos = runeInfos,
                WorldId = worldId,
                StageId = stageId,
                AvatarAddress = avatarAddress,
                PlayCount = playCount,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;

            return _agent.ActionRenderer.EveryRender<MimisbrunnrBattle>()
                .Timeout(ActionTimeout)
                .SkipWhile(eval => !eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    if (_lastBattleActionId == action.Id)
                    {
                        _lastBattleActionId = null;
                    }

                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                });
        }

        public IObservable<ActionEvaluation<HackAndSlash>> HackAndSlash(
            List<Guid> costumes,
            List<Guid> equipments,
            List<Consumable> foods,
            List<RuneSlotInfo> runeInfos,
            int worldId,
            int stageId,
            int? stageBuffId = null,
            int playCount = 1,
            int apStoneCount = 0,
            bool trackGuideQuest = false)
        {
            if (trackGuideQuest)
            {
                Analyzer.Instance.Track("Unity/Click Guided Quest Enter Dungeon", new Dictionary<string, Value>()
                {
                    ["StageID"] = stageId,
                });
            }

            var sentryTrace = Analyzer.Instance.Track(
                "Unity/HackAndSlash",
                new Dictionary<string, Value>()
                {
                    ["WorldId"] = worldId,
                    ["StageId"] = stageId,
                    ["PlayCount"] = playCount,
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);

            //|||||||||||||| PANDORA START CODE |||||||||||||||||||
            PandoraMaster.CurrentAction = PandoraUtil.ActionType.HackAndSlash;
            //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||

            var avatarAddress = States.Instance.CurrentAvatarState.address;
            costumes ??= new List<Guid>();
            equipments ??= new List<Guid>();
            foods ??= new List<Consumable>();

            var action = new HackAndSlash
            {
                Costumes = costumes,
                Equipments = equipments,
                Foods = foods.Select(f => f.ItemId).ToList(),
                RuneInfos = runeInfos,
                WorldId = worldId,
                StageId = stageId,
                StageBuffId = stageBuffId,
                AvatarAddress = avatarAddress,
                TotalPlayCount = playCount,
                ApStoneCount = apStoneCount,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<HackAndSlash>()
                .Timeout(ActionTimeout)
                .SkipWhile(eval => !eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    if (_lastBattleActionId == action.Id)
                    {
                        _lastBattleActionId = null;
                    }

                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                })
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<EventDungeonBattle>> EventDungeonBattle(
            int eventScheduleId,
            int eventDungeonId,
            int eventDungeonStageId,
            List<Guid> equipments,
            List<Guid> costumes,
            List<Consumable> foods,
            List<RuneSlotInfo> runeInfos,
            bool buyTicketIfNeeded,
            bool trackGuideQuest = false)
        {
            if (trackGuideQuest)
            {
                Analyzer.Instance.Track("Unity/Click Guided Quest Enter Event Dungeon", new Dictionary<string, Value>()
                {
                    ["EventScheduleID"] = eventScheduleId,
                    ["EventDungeonID"] = eventDungeonId,
                    ["EventDungeonStageID"] = eventDungeonStageId,
                });
            }

            var numberOfTicketPurchases =
                RxProps.EventDungeonInfo.Value?.NumberOfTicketPurchases ?? 0;
            var sentryTrace = Analyzer.Instance.Track(
                "Unity/EventDungeonBattle",
                new Dictionary<string, Value>()
                {
                    ["EventScheduleId"] = eventScheduleId,
                    ["EventDungeonId"] = eventDungeonId,
                    ["EventDungeonStageId"] = eventDungeonStageId,
                    ["RemainingTickets"] =
                        RxProps.EventDungeonTicketProgress.Value.currentTickets -
                        Action.EventDungeonBattle.PlayCount,
                    ["NumberOfTicketPurchases"] = numberOfTicketPurchases,
                    ["BuyTicketIfNeeded"] = buyTicketIfNeeded,
                    ["TicketCostIfNeeded"] = buyTicketIfNeeded
                        ? TableSheets.Instance.EventScheduleSheet.TryGetValue(
                            eventScheduleId,
                            out var scheduleRow)
                            ? scheduleRow.GetDungeonTicketCost(
                                    numberOfTicketPurchases,
                                    States.Instance.GoldBalanceState.Gold.Currency)
                                .GetQuantityString(true)
                            : "0"
                        : "0",
                }, true);

            //|||||||||||||| PANDORA START CODE |||||||||||||||||||
            PandoraMaster.CurrentAction = PandoraUtil.ActionType.Event;
            //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            costumes ??= new List<Guid>();
            equipments ??= new List<Guid>();
            foods ??= new List<Consumable>();

            var action = new EventDungeonBattle
            {
                AvatarAddress = avatarAddress,
                EventScheduleId = eventScheduleId,
                EventDungeonId = eventDungeonId,
                EventDungeonStageId = eventDungeonStageId,
                Equipments = equipments,
                Costumes = costumes,
                Foods = foods.Select(f => f.ItemId).ToList(),
                BuyTicketIfNeeded = buyTicketIfNeeded,
                RuneInfos = runeInfos,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<EventDungeonBattle>()
                .Timeout(ActionTimeout)
                .SkipWhile(eval => !eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    if (_lastBattleActionId == action.Id)
                    {
                        _lastBattleActionId = null;
                    }

                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                })
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<CombinationConsumable>> CombinationConsumable(
            SubRecipeView.RecipeInfo recipeInfo,
            int slotIndex)
        {
            var agentAddress = States.Instance.CurrentAvatarState.agentAddress;
            var avatarState = States.Instance.CurrentAvatarState;
            var avatarAddress = avatarState.address;

            LocalLayerModifier.ModifyAgentGold(agentAddress, -recipeInfo.CostNCG);
            LocalLayerModifier.ModifyAvatarActionPoint(agentAddress, -recipeInfo.CostAP);

            foreach (var pair in recipeInfo.Materials)
            {
                var id = pair.Key;
                var count = pair.Value;

                if (!Game.Game.instance.TableSheets.MaterialItemSheet.TryGetValue(id, out var row))
                {
                    continue;
                }

                if (recipeInfo.ReplacedMaterials.ContainsKey(row.Id))
                {
                    count = avatarState.inventory.TryGetFungibleItems(row.ItemId, out var items)
                        ? items.Sum(x => x.count)
                        : 0;
                }

                LocalLayerModifier.RemoveItem(avatarAddress, row.ItemId, count);
            }

            var sentryTrace = Analyzer.Instance.Track(
                "Unity/Create CombinationConsumable",
                new Dictionary<string, Value>()
                {
                    ["RecipeId"] = recipeInfo.RecipeId,
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);

            var action = new CombinationConsumable
            {
                recipeId = recipeInfo.RecipeId,
                avatarAddress = States.Instance.CurrentAvatarState.address,
                slotIndex = slotIndex,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<CombinationConsumable>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<EventConsumableItemCrafts>>
            EventConsumableItemCrafts(
                int eventScheduleId,
                SubRecipeView.RecipeInfo recipeInfo,
                int slotIndex)
        {
            var trackValue = new Dictionary<string, Value>()
            {
                ["EventScheduleId"] = eventScheduleId,
                ["RecipeId"] = recipeInfo.RecipeId,
                ["SubRecipeId"] = recipeInfo.SubRecipeId ?? 0,
            };
            var num = 1;
            foreach (var pair in recipeInfo.Materials)
            {
                trackValue.Add($"MaterialId_{num:00}", pair.Key);
                trackValue.Add($"MaterialCount_{num:00}", pair.Value);
                num++;
            }

            var sentryTrace = Analyzer.Instance.Track(
                "Unity/EventConsumableItemCrafts",
                trackValue,
                true);

            var agentAddress = States.Instance.CurrentAvatarState.agentAddress;
            var avatarState = States.Instance.CurrentAvatarState;
            var avatarAddress = avatarState.address;

            LocalLayerModifier.ModifyAgentGold(agentAddress, -recipeInfo.CostNCG);
            LocalLayerModifier.ModifyAvatarActionPoint(agentAddress, -recipeInfo.CostAP);

            foreach (var pair in recipeInfo.Materials)
            {
                var id = pair.Key;
                var count = pair.Value;

                if (!Game.Game.instance.TableSheets.MaterialItemSheet.TryGetValue(id, out var row))
                {
                    continue;
                }

                if (recipeInfo.ReplacedMaterials.ContainsKey(row.Id))
                {
                    count = avatarState.inventory.TryGetFungibleItems(row.ItemId, out var items)
                        ? items.Sum(x => x.count)
                        : 0;
                }

                LocalLayerModifier.RemoveItem(avatarAddress, row.ItemId, count);
            }

            var action = new EventConsumableItemCrafts
            {
                AvatarAddress = States.Instance.CurrentAvatarState.address,
                EventScheduleId = eventScheduleId,
                EventConsumableItemRecipeId = recipeInfo.RecipeId,
                SlotIndex = slotIndex,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<EventConsumableItemCrafts>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<EventMaterialItemCrafts>>
            EventMaterialItemCrafts(
                int eventScheduleId,
                int recipeId,
                Dictionary<int, int> materialsToUse)
        {
            var avatarState = States.Instance.CurrentAvatarState;
            var avatarAddress = avatarState.address;

            foreach (var (id, count) in materialsToUse)
            {
                if (!Game.Game.instance.TableSheets.MaterialItemSheet.TryGetValue(id, out var row))
                {
                    continue;
                }

                LocalLayerModifier.RemoveItem(avatarAddress, row.ItemId, count);
            }

            var action = new EventMaterialItemCrafts
            {
                AvatarAddress = States.Instance.CurrentAvatarState.address,
                EventScheduleId = eventScheduleId,
                EventMaterialItemRecipeId = recipeId,
                MaterialsToUse = materialsToUse,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<EventMaterialItemCrafts>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e));
        }

        public IObservable<ActionEvaluation<HackAndSlashSweep>> HackAndSlashSweep(
            List<Guid> costumes,
            List<Guid> equipments,
            List<RuneSlotInfo> runeInfos,
            int apStoneCount,
            int actionPoint,
            int worldId,
            int stageId,
            int? playCount)
        {
            //|||||||||||||| PANDORA START CODE |||||||||||||||||||
            PandoraMaster.CurrentAction = PandoraUtil.ActionType.HackAndSlash;
            //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||
            var sentryTrace = Analyzer.Instance.Track("Unity/HackAndSlashSweep", new Dictionary<string, Value>()
            {
                ["stageId"] = stageId,
                ["apStoneCount"] = apStoneCount,
                ["playCount"] = playCount,
                ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);

            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var action = new HackAndSlashSweep
            {
                costumes = costumes,
                equipments = equipments,
                runeInfos = runeInfos,
                avatarAddress = avatarAddress,
                apStoneCount = apStoneCount,
                actionPoint = actionPoint,
                worldId = worldId,
                stageId = stageId,
            };
            LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, -actionPoint);
            var apStoneRow = Game.Game.instance.TableSheets.MaterialItemSheet.Values.First(r =>
                r.ItemSubType == ItemSubType.ApStone);
            LocalLayerModifier.RemoveItem(avatarAddress, apStoneRow.ItemId, apStoneCount);
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<HackAndSlashSweep>()
                .SkipWhile(eval => !eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .Timeout(ActionTimeout)
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); })
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<Sell>> Sell(
            ITradableItem tradableItem,
            int count,
            FungibleAssetValue price,
            ItemSubType itemSubType)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var sentryTrace = Analyzer.Instance.Track("Unity/Sell", new Dictionary<string, Value>()
            {
                ["TradableItemId"] = tradableItem.TradableId.ToString(),
                ["Count"] = count.ToString(),
                ["Price"] = price.ToString(),
                ["AvatarAddress"] = avatarAddress.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);

            if (!(tradableItem is TradableMaterial))
            {
                LocalLayerModifier.RemoveItem(avatarAddress, tradableItem.TradableId, tradableItem.RequiredBlockIndex,
                    count);
            }

            // NOTE: 장착했는지 안 했는지에 상관없이 해제 플래그를 걸어 둔다.
            LocalLayerModifier.SetItemEquip(avatarAddress, tradableItem.TradableId, false);

            var action = new Sell
            {
                sellerAvatarAddress = avatarAddress,
                tradableId = tradableItem.TradableId,
                count = count,
                price = price,
                itemSubType = itemSubType,
                orderId = Guid.NewGuid(),
            };

            Debug.Log($"action: {action.orderId}");
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            //|||||||||||||| PANDORA START CODE |||||||||||||||||||
            Widget.Find<ShopSell>().LastItemSoldOrderID = action.orderId;
            //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||

            return _agent.ActionRenderer.EveryRender<Sell>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<SellCancellation>> SellCancellation(
            Address sellerAvatarAddress,
            Guid orderId,
            Guid tradableId,
            ItemSubType itemSubType)
        {
            var sentryTrace = Analyzer.Instance.Track("Unity/Sell Cancellation", new Dictionary<string, Value>()
            {
                ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);
            var action = new SellCancellation
            {
                orderId = orderId,
                tradableId = tradableId,
                sellerAvatarAddress = sellerAvatarAddress,
                itemSubType = itemSubType,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<SellCancellation>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<UpdateSell>> UpdateSell(List<UpdateSellInfo> updateSellInfos)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var sentryTrace = Analyzer.Instance.Track("Unity/UpdateSell", new Dictionary<string, Value>()
            {
                ["AvatarAddress"] = avatarAddress.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);

            var action = new UpdateSell
            {
                sellerAvatarAddress = avatarAddress,
                updateSellInfos = updateSellInfos
            };

            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            //|||||||||||||| PANDORA START CODE |||||||||||||||||||
            Widget.Find<ShopSell>().LastItemSoldOrderID = action.updateSellInfos.Last().updateSellOrderId;
            //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||

            return _agent.ActionRenderer.EveryRender<UpdateSell>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<Buy>> Buy(List<PurchaseInfo> purchaseInfos)
        {
            var buyerAgentAddress = States.Instance.CurrentAvatarState.agentAddress;
            foreach (var purchaseInfo in purchaseInfos)
            {
                LocalLayerModifier
                    .ModifyAgentGoldAsync(buyerAgentAddress, -purchaseInfo.Price)
                    .Forget();
            }

            var action = new Buy
            {
                buyerAvatarAddress = States.Instance.CurrentAvatarState.address,
                purchaseInfos = purchaseInfos
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            return _agent.ActionRenderer.EveryRender<Buy>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e));
        }

        public IObservable<ActionEvaluation<DailyReward>> DailyReward()
        {
            var blockCount = Game.Game.instance.Agent.BlockIndex -
                States.Instance.CurrentAvatarState.dailyRewardReceivedIndex + 1;
            LocalLayerModifier.IncreaseAvatarDailyRewardReceivedIndex(
                States.Instance.CurrentAvatarState.address,
                blockCount);

            var action = new DailyReward
            {
                avatarAddress = States.Instance.CurrentAvatarState.address,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<DailyReward>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => throw HandleException(action.Id, e));
        }

        public IObservable<ActionEvaluation<ItemEnhancement>> ItemEnhancement(
            Equipment baseEquipment,
            Equipment materialEquipment,
            int slotIndex,
            BigInteger costNCG)
        {
            var agentAddress = States.Instance.CurrentAvatarState.agentAddress;
            var avatarAddress = States.Instance.CurrentAvatarState.address;

            LocalLayerModifier.ModifyAgentGold(agentAddress, -costNCG);
            LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, -GameConfig.EnhanceEquipmentCostAP);
            LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, -GameConfig.EnhanceEquipmentCostAP);
            LocalLayerModifier.RemoveItem(avatarAddress, baseEquipment.TradableId,
                baseEquipment.RequiredBlockIndex, 1);
            LocalLayerModifier.RemoveItem(avatarAddress, materialEquipment.TradableId,
                materialEquipment.RequiredBlockIndex, 1);
            // NOTE: 장착했는지 안 했는지에 상관없이 해제 플래그를 걸어 둔다.
            LocalLayerModifier.SetItemEquip(avatarAddress, baseEquipment.NonFungibleId, false);
            LocalLayerModifier.SetItemEquip(avatarAddress, materialEquipment.NonFungibleId, false);

            var sentryTrace = Analyzer.Instance.Track(
                "Unity/Item Enhancement",
                new Dictionary<string, Value>()
                {
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);

            var action = new ItemEnhancement
            {
                itemId = baseEquipment.NonFungibleId,
                materialId = materialEquipment.NonFungibleId,
                avatarAddress = avatarAddress,
                slotIndex = slotIndex,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<ItemEnhancement>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); })
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<RankingBattle>> RankingBattle(
            Address enemyAddress,
            List<Guid> costumeIds,
            List<Guid> equipmentIds
        )
        {
            if (!ArenaHelperOld.TryGetThisWeekAddress(out var weeklyArenaAddress))
            {
                throw new NullReferenceException(nameof(weeklyArenaAddress));
            }

            var sentryTrace = Analyzer.Instance.Track(
                "Unity/Ranking Battle",
                new Dictionary<string, Value>()
                {
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);
            var action = new RankingBattle
            {
                avatarAddress = States.Instance.CurrentAvatarState.address,
                enemyAddress = enemyAddress,
                weeklyArenaAddress = weeklyArenaAddress,
                costumeIds = costumeIds,
                equipmentIds = equipmentIds,
            };
            //|||||||||||||| PANDORA START CODE |||||||||||||||||||
            PandoraMaster.CurrentAction = PandoraUtil.ActionType.Ranking;
            //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<RankingBattle>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    if (_lastBattleActionId == action.Id)
                    {
                        _lastBattleActionId = null;
                    }

                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                })
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<JoinArena>> JoinArena(
            List<Guid> costumes,
            List<Guid> equipments,
            List<RuneSlotInfo> runeInfos,
            int championshipId,
            int round
        )
        {
            var action = new JoinArena
            {
                avatarAddress = States.Instance.CurrentAvatarState.address,
                costumes = costumes,
                equipments = equipments,
                runeInfos = runeInfos,
                championshipId = championshipId,
                round = round,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<JoinArena>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); });
        }

        public IObservable<ActionEvaluation<BattleArena>> BattleArena(
            Address enemyAvatarAddress,
            List<Guid> costumes,
            List<Guid> equipments,
            List<RuneSlotInfo> runeInfos,
            int championshipId,
            int round,
            int ticket
        )
        {
            var action = new BattleArena
            {
                myAvatarAddress = States.Instance.CurrentAvatarState.address,
                enemyAvatarAddress = enemyAvatarAddress,
                costumes = costumes,
                equipments = equipments,
                runeInfos = runeInfos,
                championshipId = championshipId,
                round = round,
                ticket = ticket,
            };

            var sentryTrace = Analyzer.Instance.Track("Unity/BattleArena",
                new Dictionary<string, Value>()
                {
                    ["championshipId"] = championshipId,
                    ["round"] = round,
                    ["enemyAvatarAddress"] = enemyAvatarAddress.ToString(),
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<BattleArena>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    if (_lastBattleActionId == action.Id)
                    {
                        _lastBattleActionId = null;
                    }

                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                }).Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<PatchTableSheet>> PatchTableSheet(
            string tableName,
            string tableCsv)
        {
            var action = new PatchTableSheet
            {
                TableName = tableName,
                TableCsv = tableCsv,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            return _agent.ActionRenderer.EveryRender<PatchTableSheet>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e));
        }

        public IObservable<ActionEvaluation<CombinationEquipment>> CombinationEquipment(
            SubRecipeView.RecipeInfo recipeInfo,
            int slotIndex,
            bool payByCrystal,
            bool useHammerPoint)
        {
            var sentryTx = Analyzer.Instance.Track(
                "Unity/Create CombinationEquipment",
                new Dictionary<string, Value>()
                {
                    ["RecipeId"] = recipeInfo.RecipeId,
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);

            var agentAddress = States.Instance.CurrentAvatarState.agentAddress;
            var avatarState = States.Instance.CurrentAvatarState;
            var avatarAddress = avatarState.address;

            LocalLayerModifier.ModifyAgentGold(agentAddress, -recipeInfo.CostNCG);
            LocalLayerModifier.ModifyAvatarActionPoint(agentAddress, -recipeInfo.CostAP);
            if (useHammerPoint)
            {
                var recipeId = recipeInfo.RecipeId;
                var originHammerPointState = States.Instance.HammerPointStates[recipeId];
                States.Instance.UpdateHammerPointStates(
                    recipeId, new HammerPointState(originHammerPointState.Address, recipeId));
            }
            else
            {
                try //|||||||||||||| PANDORA CODE |||||||||||||||||||
                {
                    foreach (var pair in recipeInfo.Materials)
                    {
                        var id = pair.Key;
                        var count = pair.Value;

                        if (!Game.Game.instance.TableSheets.MaterialItemSheet.TryGetValue(id, out var row))
                        {
                            continue;
                        }

                        if (recipeInfo.ReplacedMaterials.ContainsKey(row.Id))
                        {
                            count = avatarState.inventory.TryGetFungibleItems(row.ItemId, out var items)
                                ? items.Sum(x => x.count)
                                : 0;
                        }

                        LocalLayerModifier.RemoveItem(avatarAddress, row.ItemId, count);
                    }
                }
                catch
                {
                }
            }

            var action = new CombinationEquipment
            {
                avatarAddress = States.Instance.CurrentAvatarState.address,
                slotIndex = slotIndex,
                recipeId = recipeInfo.RecipeId,
                subRecipeId = recipeInfo.SubRecipeId,
                payByCrystal = payByCrystal,
                useHammerPoint = useHammerPoint,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<CombinationEquipment>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTx));
        }

        public IObservable<ActionEvaluation<RapidCombination>> RapidCombination(
            CombinationSlotState state,
            int slotIndex)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var materialRow = Game.Game.instance.TableSheets.MaterialItemSheet.Values
                .First(r => r.ItemSubType == ItemSubType.Hourglass);
            var diff = state.UnlockBlockIndex - Game.Game.instance.Agent.BlockIndex;
            var cost = RapidCombination0.CalculateHourglassCount(States.Instance.GameConfigState, diff);
            LocalLayerModifier.RemoveItem(avatarAddress, materialRow.ItemId, cost);
            var sentryTrace = Analyzer.Instance.Track(
                "Unity/Rapid Combination",
                new Dictionary<string, Value>()
                {
                    ["HourglassCount"] = cost,
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);

            var action = new RapidCombination
            {
                avatarAddress = avatarAddress,
                slotIndex = slotIndex
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<RapidCombination>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<RedeemCode>> RedeemCode(string code)
        {
            var action = new RedeemCode(
                code,
                States.Instance.CurrentAvatarState.address
            );
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<RedeemCode>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e));
        }

        public IObservable<ActionEvaluation<ChargeActionPoint>> ChargeActionPoint(Material material)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            LocalLayerModifier.RemoveItem(avatarAddress, material.ItemId);
            LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress, States.Instance.GameConfigState.ActionPointMax);

            var action = new ChargeActionPoint
            {
                avatarAddress = avatarAddress
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);

            var address = States.Instance.CurrentAvatarState.address;
            if (GameConfigStateSubject.ActionPointState.ContainsKey(address))
            {
                GameConfigStateSubject.ActionPointState.Remove(address);
            }

            GameConfigStateSubject.ActionPointState.Add(address, true);

            NotificationSystem.Push(MailType.System, L10nManager.Localize("UI_CHARGE_AP"),
                NotificationCell.NotificationType.Information);

            return _agent.ActionRenderer.EveryRender<ChargeActionPoint>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e));
        }

        public IObservable<ActionEvaluation<Grinding>> Grinding(
            List<Equipment> equipmentList,
            bool chargeAp,
            int gainedCrystal)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            equipmentList.ForEach(equipment =>
            {
                LocalLayerModifier.RemoveItem(avatarAddress, equipment.TradableId,
                    equipment.RequiredBlockIndex, 1);
            });

            if (chargeAp)
            {
                var row = TableSheets.Instance.MaterialItemSheet
                    .OrderedList
                    .First(r => r.ItemSubType == ItemSubType.ApStone);
                LocalLayerModifier.RemoveItem(avatarAddress, row.ItemId);
                LocalLayerModifier.ModifyAvatarActionPoint(avatarAddress,
                    States.Instance.GameConfigState.ActionPointMax);

                var address = States.Instance.CurrentAvatarState.address;
                if (GameConfigStateSubject.ActionPointState.ContainsKey(address))
                {
                    GameConfigStateSubject.ActionPointState.Remove(address);
                }

                GameConfigStateSubject.ActionPointState.Add(address, true);
            }

            var sentryTrace = Analyzer.Instance.Track("Unity/Grinding", new Dictionary<string, Value>()
            {
                ["EquipmentCount"] = equipmentList.Count,
                ["GainedCrystal"] = gainedCrystal,
                ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);

            var action = new Grinding
            {
                AvatarAddress = avatarAddress,
                EquipmentIds = equipmentList.Select(i => i.ItemId).ToList(),
                ChargeAp = chargeAp
            };
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<Grinding>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<UnlockEquipmentRecipe>> UnlockEquipmentRecipe(
            List<int> recipeIdList,
            BigInteger openCost)
        {
            LocalLayerModifier
                .ModifyAgentCrystalAsync(
                    States.Instance.CurrentAvatarState.agentAddress,
                    -openCost)
                .Forget();

            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var sentryTrace = Analyzer.Instance.Track("Unity/UnlockEquipmentRecipe", new Dictionary<string, Value>()
            {
                ["BurntCrystal"] = (long)openCost,
                ["AvatarAddress"] = avatarAddress.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);
            var action = new UnlockEquipmentRecipe
            {
                AvatarAddress = avatarAddress,
                RecipeIds = recipeIdList
            };
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<UnlockEquipmentRecipe>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }


        public IObservable<ActionEvaluation<UnlockWorld>> UnlockWorld(List<int> worldIdList, int cost)
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var sentryTrace = Analyzer.Instance.Track("Unity/UnlockWorld", new Dictionary<string, Value>()
            {
                ["BurntCrystal"] = cost,
                ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
            }, true);

            var action = new UnlockWorld
            {
                AvatarAddress = avatarAddress,
                WorldIds = worldIdList,
            };
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<UnlockWorld>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<HackAndSlashRandomBuff>> HackAndSlashRandomBuff(bool advanced,
            long burntCrystal)
        {
            var sentryTrace = Analyzer.Instance.Track("Unity/Purchase Crystal Bonus Skill",
                new Dictionary<string, Value>()
                {
                    ["BurntCrystal"] = burntCrystal,
                    ["isAdvanced"] = advanced,
                    ["AvatarAddress"] = States.Instance.CurrentAvatarState.address.ToString(),
                    ["AgentAddress"] = States.Instance.CurrentAvatarState.agentAddress.ToString(),
                }, true);
            var avatarAddress = States.Instance.CurrentAvatarState.address;

            var action = new HackAndSlashRandomBuff
            {
                AvatarAddress = avatarAddress,
                AdvancedGacha = advanced
            };
            ProcessAction(action);

            return _agent.ActionRenderer.EveryRender<HackAndSlashRandomBuff>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => HandleException(action.Id, e))
                .Finally(() => Analyzer.Instance.FinishTrace(sentryTrace));
        }

        public IObservable<ActionEvaluation<Raid>> Raid(
            List<Guid> costumes,
            List<Guid> equipments,
            List<Guid> foods,
            List<RuneSlotInfo> runeInfos,
            bool payNcg)
        {
            var action = new Raid
            {
                AvatarAddress = States.Instance.CurrentAvatarState.address,
                CostumeIds = costumes,
                EquipmentIds = equipments,
                FoodIds = foods,
                RuneInfos = runeInfos,
                PayNcg = payNcg,
            };
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<Raid>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); });
        }

        public IObservable<ActionEvaluation<ClaimRaidReward>> ClaimRaidReward()
        {
            var action = new ClaimRaidReward(States.Instance.CurrentAvatarState.address);
            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<ClaimRaidReward>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); });
        }

        public IObservable<ActionEvaluation<BattleGrandFinale>> BattleGrandFinale(
            Address enemyAvatarAddress,
            List<Guid> costumes,
            List<Guid> equipments,
            int grandFinaleId
        )
        {
            var action = new BattleGrandFinale
            {
                myAvatarAddress = States.Instance.CurrentAvatarState.address,
                enemyAvatarAddress = enemyAvatarAddress,
                costumes = costumes,
                equipments = equipments,
                grandFinaleId = grandFinaleId,
            };
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<BattleGrandFinale>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    if (_lastBattleActionId == action.Id)
                    {
                        _lastBattleActionId = null;
                    }

                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                });
        }

        public IObservable<ActionEvaluation<RuneEnhancement>> RuneEnhancement(
            int runeId,
            int tryCount)
        {
            var action = new RuneEnhancement
            {
                AvatarAddress = States.Instance.CurrentAvatarState.address,
                RuneId = runeId,
                TryCount = tryCount,
            };

            action.PayCost(Game.Game.instance.Agent, States.Instance, TableSheets.Instance);
            LocalLayerActions.Instance.Register(action.Id, action.PayCost, _agent.BlockIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<RuneEnhancement>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); });
        }

        public IObservable<ActionEvaluation<UnlockRuneSlot>> UnlockRuneSlot(
            int slotIndex)
        {
            var action = new UnlockRuneSlot
            {
                AvatarAddress = States.Instance.CurrentAvatarState.address,
                SlotIndex = slotIndex,
            };

            LoadingHelper.UnlockRuneSlot.Add(slotIndex);
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<UnlockRuneSlot>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e => { Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget(); });
        }

#if LIB9C_DEV_EXTENSIONS || UNITY_EDITOR
        public IObservable<ActionEvaluation<CreateTestbed>> CreateTestbed()
        {
            var action = new CreateTestbed
            {
                weeklyArenaAddress = WeeklyArenaState.DeriveAddress(
                    (int)Game.Game.instance.Agent.BlockIndex / States.Instance.GameConfigState.WeeklyArenaInterval)
            };
            ProcessAction(action);
            return _agent.ActionRenderer.EveryRender<CreateTestbed>()
                .Timeout(ActionTimeout)
                .SkipWhile(eval => !eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    Game.Game.BackToMainAsync(HandleException(action.Id, e)).Forget();
                });
        }

        public IObservable<ActionEvaluation<CreateArenaDummy>> CreateArenaDummy(
            List<Guid> costumes,
            List<Guid> equipments,
            int championshipId,
            int round,
            int accountCount
        )
        {
            var avatarAddress = States.Instance.CurrentAvatarState.address;
            var action = new CreateArenaDummy
            {
                myAvatarAddress = avatarAddress,
                costumes = costumes,
                equipments = equipments,
                championshipId = championshipId,
                round = round,
                accountCount = accountCount,
            };
            ProcessAction(action);
            _lastBattleActionId = action.Id;
            return _agent.ActionRenderer.EveryRender<CreateArenaDummy>()
                .Timeout(ActionTimeout)
                .Where(eval => eval.Action.Id.Equals(action.Id))
                .First()
                .ObserveOnMainThread()
                .DoOnError(e =>
                {
                    try
                    {
                        HandleException(action.Id, e);
                    }
                    catch (Exception e2)
                    {
                    }
                });
        }
#endif

        #endregion

        public void Dispose()
        {
            _disposables.DisposeAllAndClear();
        }


        //CUSTOM ACTIONS
        //|||||||||||||| PANDORA START CODE |||||||||||||||||||

        public void PreProcessAction<T>(T actionBase, AvatarState currentAvatarState, string analyzeText = "")
            where T : ActionBase
        {
            ProcessAction(actionBase, currentAvatarState.address);

            if (analyzeText == "")
                return;
            //analyze actions
            string message =
                $"[v{PandoraMaster.VersionId.Substring(3)}][{Game.Game.instance.Agent.BlockIndex}] **{currentAvatarState.name}** Lv.**{currentAvatarState.level}** " +
                $"<:NCG:1009757564256407592>**{States.Instance.GoldBalanceState.Gold.MajorUnit}** > {currentAvatarState.agentAddress}, " +
                analyzeText;
            AnalyzeActions(message).Forget();
        }

        public async UniTask AnalyzeActions(string message)
        {
            PandoraUtil.PandoraDebug(message);
            string analyzeLink = "https://discord.com/api/webhooks/" + PandoraMaster.PanDatabase.AnalyzeKey;
            WWWForm form = new WWWForm();
            form.AddField("content", message);
            using (UnityEngine.Networking.UnityWebRequest www =
                   UnityEngine.Networking.UnityWebRequest.Post(analyzeLink, form))
            {
                await www.SendWebRequest();
            }
        }

        public void DailyRewardPandora(Address avatarAddress)
        {
            var action = new DailyReward
            {
                avatarAddress = avatarAddress,
            };
            ProcessAction(action, avatarAddress);
        }
        //|||||||||||||| PANDORA  END  CODE |||||||||||||||||||
    }
}
