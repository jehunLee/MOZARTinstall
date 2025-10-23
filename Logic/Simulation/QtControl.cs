using Mozart.SeePlan.Simulation;
using Mozart.SeePlan.Semicon.Simulation;
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
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.Simulation.Engine;
using Mozart.SeePlan.DataModel;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class QtControl
    {
        public List<Mozart.SeePlan.Semicon.DataModel.IQtLoop> LOAD_QT_LOOPS0(Mozart.SeePlan.Semicon.Simulation.QtManager mgr, ref bool handled, List<Mozart.SeePlan.Semicon.DataModel.IQtLoop> prevReturnValue)
        {
            var applyQtime = Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime;

            // [0]과 [1]의 결과는 동일하도록, [0]일 때도 Loop은 로딩하도록 변경
            // Loop을 로딩하면 QTIME_FACTOR값이 계산되면서 결과에 영향을 줌.
            //if (applyQtime <= 0)
            //    return null;

            List<IQtLoop> result = new List<IQtLoop>();
            var orderingThreshold = Helper.GetConfig(ArgsGroup.Logic_Qtime).maxGatingHour;

            foreach (var item in InputMart.Instance.QTIME_LIMIT.Rows)
            {
                FabQtLoop loop = new FabQtLoop();
                loop.ProductID = item.PART_ID;
                loop.StartStepID = item.START_STEP_ID;
                loop.EndStepID = item.END_STEP_ID;
                loop.ConstType = item.LIMIT_TYPE == "MAX" ? QtType.MAX : QtType.MIN;
                loop.LimitTime = TimeSpan.FromHours(item.SPEC_HRS);
                loop.WarningTime = TimeSpan.FromHours(item.WARNING_HRS);
                loop.GatePct = Helper.GetConfig(ArgsGroup.Logic_Qtime).defaultGatePct;

                if (loop.ConstType == QtType.MAX)
                {
                    if (applyQtime <= 1)
                    {
                        // [0] 모든 Loop 투입제어 안 함.
                        // [1] 모든 Loop 투입제어 안 함. QTIME_HISTORY는 출력.
                        loop.ControlType = QtControlType.None;
                    }
                    else if (applyQtime == 2)
                    {
                        // [2] 모든 Loop을 Workload Gating 방식으로 투입제어.
                        loop.ControlType = QtControlType.Workload;
                    }
                    else if (applyQtime >= 3)
                    {
                        // [3] Ordering 대상 Loop만 Ordering 방식으로 투입제어,
                        //      나머지 Loop은 투입제어 안 함.

                        // [4] Ordering 대상 Loop은 Ordering 방식으로 투입제어,
                        //      나머지 Loop은 Workload Gating 방식으로 투입제어.

                        // * Ordering 대상 Loop: EndStep의 모든 Arrange가 LotBatchType이고, SPEC_HRS ≤ maxGatingHour인 Loop

                        var arrs = InputMart.Instance.EqpArrangeView.FindRows(loop.ProductID, loop.EndStepID);
                        bool isOrderingCond1 = arrs.IsNullOrEmpty() == false && arrs.All(x => x.Eqp.SimType == SimEqpType.LotBatch);
                        bool isOrderingCond2 = loop.LimitTime.TotalHours <= orderingThreshold;

                        if (isOrderingCond1 && isOrderingCond2)
                            loop.ControlType = QtControlType.Ordering;
                        else
                            loop.ControlType = applyQtime == 3 ? QtControlType.None : QtControlType.Workload;
                    }
                }
                else
                {
                    // * MIN Qtime은 applyQtime >= 2 일 경우, 투입제어 됨 (Hold 판단).
                    loop.ControlType = QtControlType.None;
                }
                
                var paramList = InputMart.Instance.QTIME_PARAMPartStepView.FindRows(loop.ProductID, loop.StartStepID, loop.EndStepID,
                        loop.ConstType.ToString());
                if (paramList.IsNullOrEmpty() == false)
                {
                    loop.AttrSet = new AttributeSet();

                    foreach (var row in paramList)
                    {
                        if (row.PARAM_NAME == "qt_dummy" && row.PARAM_VALUE == "Y")
                        {
                            loop.IsDummyLoop = true;
                            loop.ControlType = QtControlType.None;
                        }

                        if (row.PARAM_NAME == "qt_gate_pct")
                        {
                            loop.GatePct = Helper.FloatParse(row.PARAM_VALUE, loop.GatePct);
                        }

                        if (loop.AttrSet.Attributes.ContainsKey(row.PARAM_NAME) == false)
                            loop.AttrSet.Attributes.Add(row.PARAM_NAME, row.PARAM_VALUE);
                    }
                }

                result.Add(loop);
            }

            return result;
        }

        public List<Mozart.SeePlan.Semicon.DataModel.QtActivation> GET_QT_ACTIVATIONS0(QtManager mgr, SemiconLot lot, ref bool handled, List<Mozart.SeePlan.Semicon.DataModel.QtActivation> prevReturnValue)
        {
            List<QtActivation> result = new List<QtActivation>();

            var actives = InputMart.Instance.QTIME_ACTIVE_LOTView.FindRows(lot.LotID);

            foreach (var item in actives)
            {
                var type = item.LIMIT_TYPE == "MAX" ? QtType.MAX : QtType.MIN;
                var loops = mgr.GetQtLoopsWith((lot.Product as FabProduct).PartID, item.START_STEP_ID).Where(x=> x.ConstType == type);
                var loop = loops.FirstOrDefault(x => x.EndStepID == item.END_STEP_ID);
                if (loop == null)
                    continue;

                var currentStep = lot.Process.FindStep(lot.CurrentStepID);
                var startStep = lot.Process.FindStep(loop.StartStepID);
                var endStep = lot.Process.FindStep(loop.EndStepID);

                if (currentStep != null && startStep != null && endStep != null)
                {
                    if (currentStep.Sequence < startStep.Sequence || currentStep.Sequence > endStep.Sequence)
                        continue;
                }

                var startTime = item.EXPIRATION_DATETIME - loop.LimitTime;

                FabQtActivation active = new FabQtActivation(lot, loop, startTime);

                result.Add(active);

                if (loop.ControlType == QtControlType.Ordering)
                    BatchingHelper.AddUpstreamLots(lot as FabSemiconLot, loop, true);
            }

            return result;
        }

        public void REGISTER_QTIME1(SemiconLot lot, SemiconStep startStep, ref bool handled)
        {
            var loops = QtManager.Current.GetQtLoopsWith((lot.Product as FabProduct).PartID, startStep.StepID);

            foreach (var loop in loops)
            {
                var endStep = lot.Process.FindStep(loop.EndStepID);
                if (endStep == null)
                    continue;

                if (loop.ConstType == QtType.MAX)
                    lot.MaxWaitQtLoops.Add(loop);
                else
                    lot.MinWaitQtLoops.Add(loop);
            }
        }

        public List<IQtLoop> GET_QT_CONTROL_LOOPS0(SemiconLot lot, ref bool handled, List<IQtLoop> prevReturnValue)
        {
            return lot.MaxWaitQtLoops;
        }

        public bool IS_QT_LOADABLE_WORKLOAD(AoEquipment aeqp, LotBatch batch, IHandlingBatch hb, IQtLoop loop, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            var qtLoop = loop as FabQtLoop;

            if (qtLoop.ControlType == QtControlType.Workload)
            {
                FabSemiconLot lot;
                if (batch == null)
                    lot = hb as FabSemiconLot;
                else
                    lot = (hb as FabLotETA).Lot as FabSemiconLot;

                if (lot.MaxQtActivations.IsNullOrEmpty() == false)
                {
                    var breachTime = lot.MaxQtActivations.Values.Min(x => x.BreachTime);
                    if ((breachTime - AoFactory.Current.NowDT).TotalHours < Helper.GetConfig(ArgsGroup.Logic_Qtime).semiBlockAllowHrs)
                        return true;
                }

                var targetStep = lot.Process.FindStep(qtLoop.EndStepID) as FabSemiconStep;

                if (targetStep.IsSimulationStep == false)
                    return true;

                ICollection<EqpArrange> arrs = EntityHelper.GetTargetStepArranges(lot, qtLoop.EndStepID);

                double cumWorkload = 0;
                double count = 0;
                foreach (var arr in arrs)
                {
                    var qtEqp = arr.Eqp.QtEqp;
                    if (qtEqp == null)
                        continue;

                    var ctg = qtEqp.Categories.SafeGet(loop.LimitTime.TotalHours);

                    if (ctg.Lots.ContainsKey(lot))
                        return true; // Already occupied by Timer Event

                    cumWorkload += ctg.WorkloadHours;
                    count++;

                    var runningWorkload = (arr.Eqp.SimObject.GetNextInTime() - aeqp.NowDT).TotalHours;
                    cumWorkload += runningWorkload;
                }

                var avg = Math.Round(cumWorkload / count, 2);
                var threshold = Math.Round(loop.LimitTime.TotalHours * qtLoop.GatePct, 2);
                if (avg > threshold)
                {
                    var reason = string.Format("Qt workload filtering({0} > {1})", avg, threshold);
                    aeqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;

                    FabLotETA eta = null;
                    if (batch != null)
                    {
                        eta = batch.BatchingData.RemainCandidates.First(x => x.Lot == lot) as FabLotETA;
                        eta.FilterReason = reason;
                    }

                    handled = true;
                    return false;
                }
            }

            return true;
        }

        public bool IS_QT_LOADABLE_ORDERING(Mozart.SeePlan.Simulation.AoEquipment aeqp, LotBatch batch, IHandlingBatch hb, IQtLoop loop, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            var qtLoop = loop as FabQtLoop;

            if (qtLoop.ControlType == QtControlType.Ordering)
            {
                FabSemiconLot lot;
                if (batch == null)
                    lot = hb as FabSemiconLot;
                else
                    lot = (hb as FabLotETA).Lot as FabSemiconLot;

                if (lot.ReservationInfos.SafeGet(qtLoop.EndStepID) == null)
                {
                    var reason = "Need Ordering";
                    aeqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;

                    FabLotETA eta = null;
                    if (batch != null)
                    {
                        eta = batch.BatchingData.RemainCandidates.First(x => x.Lot == lot) as FabLotETA;
                        eta.FilterReason = "Need Ordering";
                    }

                    handled = true;
                    return false;
                }
                else if (BatchingHelper.PassLoadStartTimeFilter(lot, qtLoop, qtLoop.EndStepID, aeqp) == false)
                {
                    var reason = "LoadStartTime filter";
                    aeqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;

                    FabLotETA eta = null;
                    if (batch != null)
                    {
                        eta = batch.BatchingData.RemainCandidates.First(x => x.Lot == lot) as FabLotETA;
                        eta.FilterReason = "LoadStartTime filter";
                    }

                    handled = true;
                    return false;
                }
            }

            return true;
        }

        public bool IS_QT_HOLD0(SemiconLot lot, ref bool handled, bool prevReturnValue)
        {
            var fLot = lot as FabSemiconLot;

            var minQts = fLot.MinQtActivations.SafeGet(lot.CurrentStepID);
            if (minQts.IsNullOrEmpty())
                return false;

            double maxRemainTime = 0;
            foreach (var qt in minQts)
            {
                var elapsed = (AoFactory.Current.NowDT - qt.StartTime).TotalHours;
                var remain = qt.Loop.LimitTime.TotalHours - elapsed;
                maxRemainTime = Math.Max(maxRemainTime, remain);
            }
            if (maxRemainTime > 0)
            {
                fLot.LastFilterReason = "MinQtime Hold";

                fLot.QtHoldTime = Time.FromHours(maxRemainTime).Floor() + 1; // Ceiling 은 밀리초 삭제가 안되서 1초 추가로 대체

                OutputHelper.WriteWipLog(LogType.INFO, "HOLD", fLot, AoFactory.Current.NowDT, fLot.LastFilterReason);

                return true;
            }

            return false;
        }

        public bool IS_QT_BLOCK(SemiconLot lot, ref bool handled, bool prevReturnValue)
        {
            var fLot = lot as FabSemiconLot;

            SemiconStep targetStep = EntityHelper.GetQtBlockCheckTargetStep(fLot);

            if (targetStep == null)
                return false;

            var step = lot.CurrentStep;
            bool tryNextStep = false;
            while (step != targetStep)
            {
                if (tryNextStep)
                {
                    step = EntityHelper.GetNextRouteStep(fLot, step) as FabSemiconStep;
                    if (step == null)
                        break;
                }

                tryNextStep = true;

                if ((step as FabSemiconStep).IsSimulationStep == false)
                    continue;

                var arrs = EntityHelper.GetTargetStepArranges(fLot, step.StepID);
                if (arrs.IsNullOrEmpty() || arrs.All(x=> x.Eqp.State == ResourceState.Down))
                {
                    if (fLot.MaxQtActivations.IsNullOrEmpty())
                    {
                        fLot.LastFilterReason = string.Format("QtimeBlock({0})", step.StepID);

                        fLot.QtHoldTime = Time.FromHours(9999);
                    }
                    else
                    {
                        var breachTime = fLot.MaxQtActivations.Values.Min(x => x.BreachTime);
                        var semiBlockHoldHrs = Math.Round(Math.Max(0, (breachTime - AoFactory.Current.NowDT).TotalHours - Helper.GetConfig(ArgsGroup.Logic_Qtime).semiBlockAllowHrs), 1);

                        if (semiBlockHoldHrs == 0)
                            return false;

                        fLot.LastFilterReason = string.Format("QtimeSemiBlock({0})", step.StepID);

                        fLot.QtHoldTime = Time.FromHours(semiBlockHoldHrs);
                    }

                    OutputHelper.WriteWipLog(LogType.INFO, "HOLD", fLot, AoFactory.Current.NowDT, fLot.LastFilterReason);

                    handled = true;
                    return true;
                }
            }

            return false;
        }

        public void REGISTER_ORDERING(SemiconLot lot, SemiconStep startStep, ref bool handled)
        {
            var fLot = lot as FabSemiconLot;
            var orderingLoops = lot.MaxWaitQtLoops.Where(x => x.ControlType == QtControlType.Ordering).ToList();

            if (orderingLoops.IsNullOrEmpty())
                return;

            orderingLoops.ForEach(loop => BatchingHelper.AddUpstreamLots(fLot, loop));
        }

        public bool IS_CHECK_POINT(AoEquipment aeqp, LotBatch batch, IHandlingBatch hb, IQtLoop loop, IDispatchContext ctx, ref bool handled, bool prevReturnValue)
        {
            if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 1)
                handled = true; // Qtime Control 적용 안하겠다는 의미.

            var feqp = aeqp as FabAoEquipment;

            //TODO: 코드 최적화 필요.
            if (feqp.IsBatchType() && batch == null)
                handled = true; // BatchingControl의 CanAddLot에서 호출되도록 Pass

            if (feqp.Eqp.SimType == SimEqpType.LotBatch)
                handled = true;

            return true;
        }

        public void BUILD_QTCHAIN(QtManager mgr, ref bool handled)
        {
            var threshold = TimeSpan.FromHours(Helper.GetConfig(ArgsGroup.Logic_Qtime).maxGatingHour);
            int chainId = 1;
            foreach (var loop in mgr.QtLoops)
            {
                if ((loop as FabQtLoop).IsDummyLoop)
                    continue;

                if (loop.ConstType != QtType.MAX || loop.LimitTime > threshold)
                    continue;

                var prevs = mgr.GetQtLoopsWith(loop.ProductID, loop.StartStepID, false)
                    .Where(x=> x.ConstType == QtType.MAX && x.LimitTime <= threshold);
                if (prevs.IsNullOrEmpty() == false)
                    continue;

                var nexts = mgr.GetQtLoopsWith(loop.ProductID, loop.EndStepID, true)
                    .Where(x => x.ConstType == QtType.MAX && x.LimitTime <= threshold);
                if (nexts.IsNullOrEmpty())
                    continue;

                QtChain chain = new QtChain(chainId.ToString());
                chainId++;

                (loop as FabQtLoop).Chain = chain;

                chain.Loops.Add(loop);

                var next = nexts.OrderByDescending(x => x.LimitTime).First();
                while (next != null)
                {
                    chain.Loops.Add(next);
                    (next as FabQtLoop).Chain = chain;
                    next = mgr.GetQtLoopsWith(next.ProductID, next.EndStepID, true)
                        .Where(x => x.ConstType == QtType.MAX && x.LimitTime <= threshold)
                        .OrderByDescending(x => x.LimitTime).FirstOrDefault();
                }

                if (InputMart.Instance.ExcludeOutputTables.Contains("QTIME_CHAIN_LOG"))
                    continue;

                int seq = 1;
                foreach (var item in chain.Loops)
                {
                    QTIME_CHAIN_LOG log = new QTIME_CHAIN_LOG();
                    log.SCENARIO_ID = InputMart.Instance.ScenarioID;
                    log.VERSION_NO = ModelContext.Current.VersionNo;
                    log.CHAIN_ID = chain.ChainID.ToInt32();
                    log.PART_ID = item.ProductID;
                    log.START_STEP_ID = item.StartStepID;
                    log.END_STEP_ID = item.EndStepID;
                    log.SPEC_HRS = (float)item.LimitTime.TotalHours;
                    log.CONTROL_TYPE = item.ControlType.ToString();
                    log.LOOP_SEQ = seq++;

                    OutputMart.Instance.QTIME_CHAIN_LOG.Add(log);
                }
            }
        }

        public void ON_END_INITIALIZE0(QtManager mgr, ref bool handled)
        {
            if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 1)
                handled = true;
        }
    }
}