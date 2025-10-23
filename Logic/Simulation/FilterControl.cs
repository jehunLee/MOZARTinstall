using Mozart.Simulation.Engine;
using Mozart.SeePlan.Simulation;
using FabSimulator.Persists;
using FabSimulator.Outputs;
using FabSimulator.Inputs;
using FabSimulator.DataModel;
using Mozart.Task.Execution;
using Mozart.Extensions;
using Mozart.Collections;
using Mozart.Common;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using Mozart.SeePlan.Semicon.Simulation;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class FilterControl
    {
        public bool IS_LOADABLE_CHAMBER(Mozart.SeePlan.Simulation.AoEquipment eqp, IHandlingBatch hb, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            if (Helper.GetConfig(ArgsGroup.Resource_SimType).chamberToInline == "Y")
                return true;

            if ((eqp.Target as FabSemiconEqp).SimType != Mozart.SeePlan.DataModel.SimEqpType.ParallelChamber)
                return true;

            var lot = hb as FabSemiconLot;
            var arr = lot.CurrentArranges.SafeGet(eqp.EqpID);
            if (arr == null)
            {
                handled = true;
                return false;
            }

            var now = eqp.Now;

            var proc = eqp.Processes.First() as AoChamberProc2;
            foreach (var item in proc.Chambers)
            {
                if (item.GetAvailableTime() <= now)
                {
                    if (arr.SubEqps.Select(x => x.SubEqpID).Contains(item.Label))
                        return true;
                }
            }

            var reason = "No Available Chamber";
            eqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
            lot.LastFilterReason = reason;

            handled = true;
            return false;
        }

        public void SELECT_RETICLE(AoEquipment eqp, IList<IHandlingBatch> wips, IDispatchContext ctx, ref bool handled)
        {
            var feqp = eqp as FabAoEquipment;
            var eqpModel = eqp.Target as FabSemiconEqp;
            if (eqpModel.ToolingInfo.IsNeedReticle == false)
                return;

            eqpModel.ToolingInfo.SelectableReticleList.Clear();

            var reticleInfos = ResourceHelper.GetReticleSelectionInfos(feqp, wips);
            ResourceHelper.SetSelectableReticleList(feqp, reticleInfos, true);
        }

        public IList<IHandlingBatch> DO_FILTER_NORMAL(AoEquipment eqp, IList<IHandlingBatch> wips, IDispatchContext ctx, ref bool handled, IList<IHandlingBatch> prevReturnValue)
        {
            var eqpModel = eqp.Target as FabSemiconEqp;
            //if (eqpModel.StagingLots.IsNullOrEmpty() == false)
            //{
            //    return HandleStagingLots(wips, eqpModel);
            //}

            if (eqp.IsBatchType()) // Batch설비는 BatchControl의 CanAddLot에서 다룸.
            {
                handled = true;
                return wips;
            }

            // Mozart.SeePlan.Simulation.EqpDispatchInfo.DispatchInfoParallelEnabled
            // 위 옵션에 의해, wips 수량이 256개를 초과하면 evaluation이 MultiThread로 진행됨.

            var filterControl = DispatchFilterControl.Instance;
            var filterManager = AoFactory.Current.Filters;

            filterControl.SetFilterContext(eqp, wips, ctx);

            bool IsSkippable(IHandlingBatch hb)
            {
                filterControl.SetLotCondition(eqp, hb, ctx);

                //if (!filterControl.CheckSetupCrew(eqp, hb, ctx))
                //    return true;

                var filterKey = filterControl.GetFilterSetKey(eqp, hb, ctx);
                if (string.IsNullOrEmpty(filterKey))
                {
                    if (!filterControl.IsLoadable(eqp, hb, ctx))
                        return true;
                }
                else
                {
                    if (filterManager.Filter(filterKey, hb, AoFactory.Current.NowDT, eqp, ctx))
                        return true;
                }

                // Reticle 체크를 IsLoadable 이후로 변경
                if (!filterControl.CheckSecondResouce(eqp, hb, ctx))
                    return true;

                return false;
            }

            IHandlingBatch[] testWips = wips.ToArray();
            List<FabSemiconLot> loadableLots = new List<FabSemiconLot>();
            wips.Clear();

            int highestPriority = int.MaxValue;
            for (var i = 0; i < testWips.Length; i++)
            {
                var hb = testWips[i];

                if (IsSkippable(hb))
                    continue;

                var lot = hb as FabSemiconLot;
                loadableLots.Add(lot);

                // PartStepAttribute의 Arrange는 가용시간에 따라 못찾는 상황이 발생할 수 있음.

                // STACK 로직을 탔으면 설비까지 Key로 넘겨서 해당 값을 가져옴 (Min or Max Value)
                // 아닐 경우 Step으로만 조회 (forceLotPriority=Y 일 때만 값이 존재)
                var key = lot.EvaluatePriority.Values.Any(x => x == int.MinValue || x == int.MaxValue) ?
                    Helper.CreateKey(lot.CurrentStepID, eqp.EqpID) : Helper.CreateKey(lot.CurrentStepID, "-");

                lot.CurrentEvaluatePriority = lot.EvaluatePriority.SafeGet(key, 0);

                highestPriority = Math.Min(highestPriority, lot.CurrentEvaluatePriority); // smaller value is higher priority
            }

            foreach (var lot in loadableLots)
            {
                if (lot.CurrentEvaluatePriority > highestPriority)
                    continue;

                // Filter 통과한 Lot들 중 highestPriority만 Evaluation단계로 넘겨 줌.
                wips.Add(lot);
            }

            if (wips.IsNullOrEmpty())
            {
                DispatchControl.Instance.WriteDispatchLog(null, eqp.EqpDispatchInfo);

                FabAoEquipment feqp = eqp as FabAoEquipment;

                Time delayTime = Time.FromMinutes(Helper.GetConfig(ArgsGroup.Resource_Eqp).wakeUpEventTime);
                Time nextEventTime = eqp.Now + delayTime;

                // 이미 등록된 NextManualWakeupTime이 지금 등록하려는 EventTime보다 빠르면, 새로 등록하지 않음.
                if (feqp.NextManualWakeUpTime > nextEventTime)
                    EventHelper.AddManualEvent(delayTime, ManualEventTaskType.WakeUpEqp, feqp, "ADD_WAKEUP_TIMEOUT");
            }

            return wips;

            //static IList<IHandlingBatch> HandleStagingLots(IList<IHandlingBatch> wips, FabSemiconEqp eqp)
            //{
            //    // TODO: Staging은 FHB 로 묶이지 않도록 보완
            //    var stagingToRun = eqp.StagingLots.First();
            //    var batch = stagingToRun as Mozart.SeePlan.Semicon.Simulation.LotBatch;

            //    if (batch != null)
            //    {
            //        if (batch.Any(x => wips.Contains(x) == false))
            //            return null;
            //    }
            //    else
            //    {
            //        if (wips.Contains(stagingToRun) == false)
            //            return null;
            //    }

            //    wips.Clear();
            //    var feqp = eqp.SimObject;
            //    if (feqp.IsBatchType())
            //    {
            //        BatchingHelper.ReserveBatch(feqp, batch);
            //        batch.ForEach(wips.Add);

            //        eqp.StagingLots.Remove(batch);
            //    }
            //    else
            //    {
            //        wips.Add(stagingToRun);
            //    }    

            //    return wips;
            //}
        }

        public bool IS_PREVENT_DISPATCHING0(AoEquipment aeqp, IList<IHandlingBatch> wips, Mozart.Simulation.Engine.Time waitDownTime, ref bool handled, bool prevReturnValue)
        {
            var feqp = aeqp as FabAoEquipment;

            ResourceHelper.RefreshCrossFabInfo();

            if (feqp.IsWaitingUD || feqp.IsWaitingFS)
                return true;

            if (feqp.Eqp.ForceStandbyProbability > 0)
            {
                var forceStandby = Helper.GetBernoulliTrialResult(feqp.Eqp.ForceStandbyProbability);
                if (forceStandby)
                {
                    feqp.IsWaitingFS = true;

                    Time delayTime = Time.FromMinutes(14.4); // 14분 24초
                    EventHelper.AddManualEvent(delayTime, ManualEventTaskType.WakeUpEqp, feqp, "FORCE_STANDBY");

                    return true;
                }
            }

            return false;
        }

        public bool IS_LOADABLE_PROCESS_INHIBIT(AoEquipment eqp, IHandlingBatch hb, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            var feqp = eqp as FabAoEquipment;
            var lot = hb as FabSemiconLot;

            if (feqp.OnProcessInhibit && lot.CurrentActiveStackInfo != null)
            {
                // 1. FirstLayer에서 못들어 오도록 함.
                // 2. 기준정보 누락으로 StackEqp가 없는 경우에도 ProcessInhibit 중인 설비로는 진행 못하도록 함.
                if (lot.CurrentActiveStackInfo.StackStepInfo.IsFirstLayer || lot.CurrentActiveStackInfo.StackEqp == null)
                {
                    var reason = "StackInhibit";
                    eqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;

                    handled = true;
                    return false;
                }
            }

            if (feqp.IsReworkEffective && lot.CurrentActiveStackInfo != null && lot.CurrentActiveStackInfo.StackStepInfo.IsVeryFirstLayer)
            {
                if (feqp.DailyFirstLayerQty + lot.UnitQty > feqp.Eqp.DailyLimitPostStackPm && feqp.Eqp.DailyLimitPostStackPm > 0)
                {
                    var reason = "DailyLimitPostStackPm";
                    eqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;

                    handled = true;
                    return false;
                }
            }

            return prevReturnValue;
        }

        public bool IS_LOADABLE_SWITCH_TIME(AoEquipment aeqp, IHandlingBatch hb, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            var eqp = aeqp.Target as FabSemiconEqp;
            var lot = hb.Sample as FabSemiconLot;

            if (eqp.MinRunsAfterSwitch.IsNullOrEmpty())
                return true;

            if (aeqp.LastPlan == null)
                return true;

            var arr = lot.CurrentArranges.SafeGet(aeqp.EqpID);
            if (arr == null)
                return true;

            // 둘중 하나라도 정보가 없으면, 전후가 같은지 다른지 알 수 없음.
            var from = (aeqp.LastPlan as FabPlanInfo).Arrange;
            var to = arr;

            if (from.SetupName == null || to.SetupName == null || from.SetupName == to.SetupName)
                return true;

            int recipeCnt;
            if (eqp.MinRunsAfterSwitch.TryGetValue(from.SetupName, out recipeCnt) == false)
                return true;

            if (from.MinRunsAfterSwitch > recipeCnt)
            {
                string reason = "MinRunsAfterSwitch";
                aeqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                lot.LastFilterReason = reason;

                handled = true;
                return false;
            }

            return true;
        }

        public bool IS_LOADABLE_CROSS_FAB(AoEquipment eqp, IHandlingBatch hb, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            return ResourceHelper.CanLoadableCrossFab(eqp, hb, ref handled);
        }

        public void SET_SETUP_GROUPS(AoEquipment eqp, IList<IHandlingBatch> wips, IDispatchContext ctx, ref bool handled)
        {
            var eqpModel = eqp.Target as FabSemiconEqp;
            if (eqpModel.SetupInfos.IsNullOrEmpty())
                return;

            Dictionary<string, (int, int)> setupGroups = new Dictionary<string, (int, int)>();

            var setupNames = wips.Select(x => x as FabSemiconLot).GroupBy(x => ArrangeHelper.GetCurrentEqpArrange(x, eqp)?.SetupName);
            foreach (var items in setupNames)
            {
                var setupName = items.Key;
                if (setupName == null)
                    continue;

                var lotCount = items.Count();
                var waferCount = items.Select(x => x.UnitQty).Sum();

                //TODO: recipe별 MinLotsToSwitch가 동일하다고 보장할 수 있으면, IsNeedSetup 호출을 일부 생략할 수 있어서 퍼포먼스 이득을 가져갈 수 있음.
                if (eqp.IsNeedSetup(items.First()))
                    setupGroups.Add(setupName, (lotCount, waferCount));
            }

            ctx.Set("SETUP_GROUPS", setupGroups);
        }

        public bool IS_LOADABLE_SETUP(AoEquipment eqp, IHandlingBatch hb, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            var lot = hb as FabSemiconLot;

            var setupGroups = ctx.Get<Dictionary<string, (int, int)>>("SETUP_GROUPS", null);
            if (setupGroups.IsNullOrEmpty())
                return true;

            var arr = ArrangeHelper.GetCurrentEqpArrange(lot, eqp);
            if (arr != null && arr.SetupName != null)
            {
                var group = setupGroups.SafeGet(arr.SetupName, (-1, -1));

                var groupLotCount = group.Item1;
                var groupWaferCount = group.Item2;

                var reason = groupLotCount > 0 && groupLotCount < arr.MinLotsToSwitch ? "MinLotsToSwitch"
                    : (groupWaferCount > 0 && groupWaferCount < arr.MinWafersToSwitch ? "MinWafersToSwitch" : null);

                if (reason != null)
                {
                    eqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;

                    handled = true;
                    return false;
                }
            }

            return true;
        }

        public IList<IHandlingBatch> DO_FILTER_TRANSPORT(AoEquipment eqp, IList<IHandlingBatch> wips, IDispatchContext ctx, ref bool handled, IList<IHandlingBatch> prevReturnValue)
        {
            // Port에 도착한 재공을 FIFO로 설비에 로딩시키는 역할만 수행

            if (TransportSystem.Apply == false)
                return prevReturnValue;

            handled = true;

            if (eqp.IsBatchType()) // Batch설비는 BatchingControl에서 다룸.
            {
                return wips;
            }

            // 점유 포트 로딩만 시도.
            var oports = TransportSystem.GetPorts(eqp, LocationState.OCCUPIED);
            if (oports.IsNullOrEmpty() == false)
            {
                foreach (var port in oports.OrderBy(x => x.StateChangeTime)) // 정렬하지 않으면 PORT에서 계속 대기 발생할 수 있음.
                {
                    if (!port.Lot.CurrentPlan.IsStarted && wips.Contains(port.Lot))
                    {
                        return [port.Lot];
                    }
                }
            }

            // 빈 포트 디스패칭은 별도로 구현
            return Array.Empty<IHandlingBatch>();
        }
    }
}