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
using Mozart.SeePlan.Simulation;
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.Simulation.Engine;
using Mozart.SeePlan.DataModel;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class EntityHelper
    {
        internal static int GeteWipStatePriority(string wipState)
        {
            if (TransportSystem.Apply)
            {
                // LocateForDispatch를 먼저 불러서, 대기재공이 Buffer/Port를 먼저 점유하게 하기 위함.
                // LocateForRun이 먼저 불리면, PST에 WhereNext 호출되는 재공이 대기재공이 점유한 Buffer/Port를 침범하는 문제가 발생함.

                if (wipState == "WAIT")
                    return 0;
                if (wipState == "HOLD")
                    return 1;
                if (wipState == "MOVE")
                    return 2;
                if (wipState == "RUN")
                    return 3;
                if (wipState == "SETUP")
                    return 4;
                if (wipState == "STAGED")
                    return 5;

                return 6;
            }
            else
            {
                if (wipState == "RUN")
                    return 0;
                if (wipState == "SETUP")
                    return 1;
                if (wipState == "STAGED")
                    return 2;
                if (wipState == "WAIT")
                    return 3;
                if (wipState == "HOLD")
                    return 4;
                if (wipState == "MOVE")
                    return 5;

                return 6;
            }
        }

        public static FabSemiconLot GetLot(Mozart.Simulation.Engine.ISimEntity entity)
        {
            if (entity is LotBatch)
            {
                LotBatch batch = (LotBatch)entity;

                return batch.Sample as FabSemiconLot;
            }
            else if (entity is FabLotETA)
            {
                FabLotETA eta = (FabLotETA)entity;

                return eta.Lot as FabSemiconLot;
            }
            else
            {
                return entity as FabSemiconLot;
            }
        }

        internal static FabLotETA CreateLotETA(FabSemiconLot lot, SemiconStep targetStep, AoEquipment aeqp)
        {
            FabLotETA eta = new FabLotETA(lot, targetStep);

            var arrs = EntityHelper.GetTargetStepArranges(lot, targetStep.StepID);
            if (arrs.IsNullOrEmpty())
                return null;

            var arr = arrs.FirstOrDefault(x => x.Eqp.ResID == aeqp.EqpID);
            if (arr == null)
                return null;

            eta.Arrange = arr;

            UpdateETA(eta);

            return eta;
        }

        private static Time GetRemainCycleTime(FabSemiconLot lot, SemiconStep targetStep)
        {
            var step = lot.CurrentStep as FabSemiconStep;
            Time time = Time.Zero;
            while (step != null)
            {
                if (step == targetStep)
                    break;

                time += step.CT;

                step = GetNextRouteStep(lot, step) as FabSemiconStep;
            }

            return time;
        }

        internal static void DoLotSizeMerge(MultiDictionary<Tuple<FabProduct, FabSemiconStep, string, bool>, FabSemiconLot> lotSizeMergeDict)
        {
            if (InputMart.Instance.DoLotSizeMerge == false)
                return;

            // 재현을 위해 Sorting 처리함. (WipManager로부터 추출한 sample값이 무작위로 바뀌기 때문)
            foreach (var kvp in lotSizeMergeDict.OrderBy(x => x.Key.Item1.ProductID).ThenBy(x => x.Key.Item2.StepID))
            {
                var prod = kvp.Key.Item1;
                var step = kvp.Key.Item2;

                if (BopHelper.IsFabInOrFabOut(step.StepID))
                    continue;

                var lotSizeMergeGroup = kvp.Value.OrderByDescending(x => x.UnitQty).ToArray();

                if (lotSizeMergeGroup.Count() <= 1)
                    continue;

                FabHandlingBatch fhb = null;

                int i = 0;
                while (i < lotSizeMergeGroup.Count())
                {
                    var lot = lotSizeMergeGroup[i++];

                    if (FabHandlingBatch.IsAllowLotSizeMerge(lot) == false)
                        continue;

                    if (fhb == null)
                    {
                        if (lot is FabHandlingBatch)
                            fhb = lot as FabHandlingBatch;
                        else
                            fhb = new FabHandlingBatch(lot);

                        continue;
                    }

                    // 더 이상 추가할 수 없으면, 가득차지 않았더라도 완료 처리.
                    bool isComplete = fhb.TryMergeLot(lot) == false;

                    if (isComplete)
                    {
                        fhb.Launching();
                        fhb = null;
                        i--; // Merge 안된 lot은 다시 돌아가서 HB 구성 시도.
                    }
                    else if (i == lotSizeMergeGroup.Count())
                    {
                        fhb.Launching(); // 마지막 HB는 가득 차지 않았더라도 2Lot 이상이면 Launch
                    }
                }
            }
        }

        internal static void AnalyzeWip(ILot lot)
        {
            FabSemiconLot fLot = lot as FabSemiconLot;

            if (fLot.FabWipInfo.IsMergeOut)
                return;

            if (fLot.CurrentStep == fLot.FabWipInfo.InitialStep)
            {
                if (fLot.CurrentState == EntityState.WAIT || fLot.CurrentState == EntityState.HOLD)
                {
                    string reason = fLot.LastFilterReason ?? string.Empty;
                    OutputHelper.WriteWipLog(LogType.INFO, "UNSCHED", fLot, ModelContext.Current.EndTime, reason);
                }
            }

            //var stateInfo = fLot.IsNoArrangeWait ? "No Arrange" : lot.CurrentState.ToString();
            //string lastLocationLog = string.Format("Last location - {0}", stateInfo);
            // -- FAB_OUT_RESULT에 기록되고 있음
            //OutputHelper.WriteWipLog(LogType.INFO, "Location", fLot, ModelContext.Current.EndTime, lastLocationLog);

            OutputHelper.WriteQtimeHistory(fLot, fLot.MaxQtActivations.Values, false);

            OutputHelper.WriteFabOutResult(fLot, DateTime.MaxValue);

        }
        internal static void BuildProductMix(double yesterdayFabOut)
        {
            var weekStartTime = Helper.GetWeekStartTime(AoFactory.Current.NowDT);
            var inTargets = InputMart.Instance.InTargetsByWeek.SafeGet(weekStartTime);
            if (inTargets.IsNullOrEmpty())
                return;

            var targetSum = inTargets.Sum(x => Math.Ceiling(x.CalcQty));

            ProductMixInfo pMix = new ProductMixInfo();
            pMix.Date = AoFactory.Current.NowDT;
            pMix.RemainQty = yesterdayFabOut;

            foreach (var group in inTargets.GroupBy(x => x.Mo.ProductID))
            {
                pMix.ProductMix.Add(group.Key, group.Sum(x => Math.Ceiling(x.CalcQty)) / targetSum);
                pMix.ProductPriority.Add(group.Key, (int)group.Select(x => x.MoPlan.Priority).First());
            }

            pMix.DueDate = inTargets.First().MoPlan.DueDate;

            InputMart.Instance.ProductMixInfo.ImportRow(pMix);
        }

        internal static List<ILot> BuildTodayFabIn()
        {
            List<ILot> instancingLots = new List<ILot>();
            var todayRemain = Helper.GetConfig(ArgsGroup.Lot_InPlan).applyInputSmoothing == "Y" ? InputMart.Instance.FabInLimit : double.MaxValue;

            List<ProductMixInfo> removable = new List<ProductMixInfo>();

            foreach (var item in InputMart.Instance.ProductMixInfo.Rows)
            {
                var allocation = Math.Min(todayRemain, item.RemainQty);
                todayRemain -= allocation;
                item.RemainQty -= allocation;
                foreach (var kvp in item.ProductMix)
                {
                    var inQty = Math.Round(allocation * kvp.Value);
                    int priority = item.ProductPriority.SafeGet(kvp.Key);

                    var stdProd = BopHelper.GetStdProduct(kvp.Key);
                    var fabProduct = stdProd?.Products.FirstOrDefault();

                    var waferStarts = CreateWaferStarts(fabProduct,
                        AoFactory.Current.NowDT, item.DueDate, inQty, InputMart.Instance.LotMergeSize, priority);

                    if (waferStarts.IsNullOrEmpty())
                        continue;

                    instancingLots.AddRange(waferStarts);
                }

                if (item.RemainQty <= 0)
                    removable.Add(item);

                if (todayRemain <= 0)
                    break;
            }

            removable.ForEach(x => InputMart.Instance.ProductMixInfo.Rows.Remove(x));

            return instancingLots;
        }

        internal static ICollection<EqpArrange> GetTargetStepArranges(FabSemiconLot lot, string stepID)
        {
            var arrs = lot.TargetStepArranges.SafeGet(stepID);
            if (arrs.IsNullOrEmpty())
            {
                arrs = InputMart.Instance.EqpArrangeView.FindRows((lot.Product as FabProduct).PartID, stepID).ToList();
                lot.TargetStepArranges.AddMany(stepID, arrs);
            }

            return arrs;
        }

        internal static void UpdateETA(FabLotETA eta)
        {
            if (eta.OnUpstream == false)
                return;

            if (eta.CTCache != null)
            {
                if (eta.Lot.CurrentStep == eta.CTCache.Item1)
                    return;
            }

            Time time = GetRemainCycleTime(eta.Lot as FabSemiconLot, eta.TargetStep);
            eta.CTCache = new Tuple<SemiconStep, double>(eta.Lot.CurrentStep, time.TotalMinutes);

            eta.ArrivalTime = AoFactory.Current.NowDT.AddMinutes(time.TotalMinutes);
        }

        public static SemiconStep GetQtBlockCheckTargetStep(FabSemiconLot fLot)
        {
            List<SemiconStep> candidates = new List<SemiconStep>();
            foreach (FabQtLoop loop in fLot.MaxWaitQtLoops)
            {
                if (loop.Chain == null)
                    continue;
                var chainLoops = loop.Chain.Loops;
                var chainLoopsCount = chainLoops.Count;


                if (loop != chainLoops[0])
                    continue;

                var targetStep = fLot.Process.FindStep(chainLoops[chainLoopsCount - 1].EndStepID);
                if (targetStep != null)
                    candidates.Add(targetStep);
            }

            var maxLoop = fLot.MaxWaitQtLoops.OrderByDescending(x => x.LimitTime).FirstOrDefault();
            if (maxLoop != null)
            {
                var targetStep = fLot.Process.FindStep(maxLoop.EndStepID);
                if (targetStep != null)
                    candidates.Add(targetStep);
            }

            return candidates.OrderByDescending(x => x.Sequence).FirstOrDefault();
        }

        internal static List<FabSemiconLot> CreateWaferStartWithDemand()
        {
            List<FabSemiconLot> instancingLots = new List<FabSemiconLot>();

            foreach (var pt in InputMart.Instance.InTargets)
            {
                var stdProd = BopHelper.GetStdProduct(pt.ProductID);
                var fabProduct = stdProd?.Products.FirstOrDefault(); // TODO: 로직 필요
                
                List<FabSemiconLot> waferStarts = CreateWaferStarts(fabProduct,
                    pt.CalcDate, pt.MoPlan.DueDate, Math.Ceiling(pt.CalcQty), InputMart.Instance.LotMergeSize, (int)pt.MoPlan.Priority);

                if (waferStarts.IsNullOrEmpty())
                    continue;

                waferStarts.ForEach(x => x.FabWipInfo.DemandID = pt.Mo.DemandID);

                instancingLots.AddRange(waferStarts);
            }

            return instancingLots;
        }

        internal static List<FabSemiconLot> CreateWaferStartWithFabInPlan()
        {
            List<FabSemiconLot> instancingLots = new List<FabSemiconLot>();

            foreach (var entity in InputMart.Instance.FAB_IN_PLAN.Rows.OrderBy(x => x.START_DATETIME).ThenBy(x => x.MFG_PART_ID))
            {
                if (entity.START_DATETIME < ModelContext.Current.StartTime || entity.START_DATETIME >= ModelContext.Current.EndTime)
                    continue;

                var fabProudct = InputMart.Instance.FabProductMfgPartView.FindRows(entity.MFG_PART_ID).FirstOrDefault();
                if (fabProudct == null || fabProudct.Process == null)
                    continue;

                var dueDate = GetPeggingDueDateWithFabInPlan(entity);

                List<FabSemiconLot> waferStarts = CreateWaferStarts(fabProudct,
                    entity.START_DATETIME, dueDate, entity.WAFER_QTY, entity.LOT_SIZE, entity.PRIORITY);

                if (waferStarts.IsNullOrEmpty())
                    continue;

                instancingLots.AddRange(waferStarts);
            }

            return instancingLots;
        }

        public static List<FabSemiconLot> CreateWaferStarts(FabProduct fabProduct,
            DateTime startTime, DateTime dueDate, double waferStartQty, double lotSize, int priority)
        {
            if (fabProduct == null || fabProduct.Process == null)
                return null;

            List<FabSemiconLot> fabInLots = new List<FabSemiconLot>();

            var initialStep = (fabProduct.Process as FabSemiconProcess).FirstStep;

#if false // WIP의 LotID Index가 불연속적이면 (FabOut 순서 역전 등의 이유), 여전히 중복가능성이 있어서 다른 방식으로 변경.
            bool useWip = Helper.GetConfig(ArgsGroup.Lot_Wip).useWIP == "Y";
            if (useWip)
            {
                // LotID 중복 방지
                while (InputMart.Instance.FabWipInfo.ContainsKey(GetNewLotID(fabProduct, false)))
                {
                    InputMart.Instance.WaferStartLotIndex++;
                }
            } 
#endif

            double inQty = waferStartQty;
            while (inQty > 0)
            {
                string lotID = GetNewLotID(fabProduct);
                double waferQty = Math.Min(inQty, lotSize);
                inQty -= waferQty;

                var releaseDate = Helper.Max(startTime, ModelContext.Current.StartTime);

                FabSemiconLot lot = CreateHelper.CreateInstancingLot(lotID, fabProduct, fabProduct.Process as FabSemiconProcess, waferQty,
                    initialStep, releaseDate, dueDate, priority);

                SetCurrentBOMInfo(lot);

                fabInLots.Add(lot);
            }

            return fabInLots;
        }

        internal static string GetNewLotID(FabProduct fabProduct, bool doIncrement = true)
        {
            var newLotID = fabProduct.ProductID + "_" + InputMart.Instance.WaferStartLotIndex;

            if (doIncrement)
                InputMart.Instance.WaferStartLotIndex++;

            return newLotID;
        }

        internal static int GetQualityLossWaferCount(FabSemiconLot lot, double lossRate)
        {
            int unitCount = (int)Math.Max(Math.Ceiling(lot.UnitQtyDouble / InputMart.Instance.LotSize), 1);
            int lossUnitCount = 0;
            for (int i = 0; i < unitCount; i++)
            {
                bool loss = Helper.GetBernoulliTrialResult(lossRate);

                if (loss)
                    lossUnitCount++;
            }

            return (int)Math.Min(lossUnitCount * InputMart.Instance.LotSize, lot.UnitQty);
        }

        internal static void InitiateLots(List<ILot> instancingLots)
        {
            if (instancingLots.IsNullOrEmpty())
                return;

            BatchInitiator batchInitiator = ServiceLocator.Resolve<BatchInitiator>();

            int initIndex = 0;
            while (initIndex < instancingLots.Count)
            {
                batchInitiator.ReserveOne(instancingLots, ref initIndex);
            }
        }

        public static int GetLotPriorityValue(string lotPriorityStatus)
        {
            // 0=SUPER;1=HOT;6=NORMAL;
            string[] semiColSplits = Helper.GetConfig(ArgsGroup.Lot_Default).lotPriorityStatus.Split(';');

            foreach (var split in semiColSplits)
            {
                var eqSplits = split.Split('=');
                if (eqSplits.Length != 2)
                    continue;

                var str = eqSplits[1].Trim();
                if (str == lotPriorityStatus)
                {
                    if (int.TryParse(eqSplits[0].Trim(), out int value))
                        return value;
                }
            }

            return 6; // NORMAL
        }

        public static string GetLotPriorityStatus(int lotPriorityValue)
        {
            // 0=SUPER;1=HOT;6=NORMAL;
            string[] semiColSplits = Helper.GetConfig(ArgsGroup.Lot_Default).lotPriorityStatus.Split(';');

            foreach (var split in semiColSplits)
            {
                var eqSplits = split.Split('=');
                if (eqSplits.Length != 2)
                    continue;

                int.TryParse(eqSplits[0].Trim(), out int ival);
                if (ival == lotPriorityValue)
                    return eqSplits[1].Trim();
            }

            return "NORMAL";
        }

        public static bool IsHotLot(string lotPriorityStatus)
        {
            // SUPER,HOT
            var semiColSplits = Helper.GetConfig(ArgsGroup.Lot_Default).hotPriorityStatus.Split(',').Select(x => x.Trim());
            return semiColSplits.Contains(lotPriorityStatus);
        }

        public static bool IsQualityLossProcessing(FabSemiconLot lot)
        {
            if ((lot.Route as FabSemiconProcess).RouteType == RouteType.REWORK)
                return true;

            bool cond1 = lot.CurrentRework != null;
            bool cond2 = cond1 && lot.CurrentStep != lot.CurrentRework.ReworkTriggerStep;
            bool cond3 = lot.CurrentFabStep.IsReworkDummyStep == false;

            if (cond1 && cond2 && cond3)
                return true;

            return false;
        }

        public static bool IsReworkEnd(FabSemiconLot lot, FabSemiconStep targetStep = null)
        {
            if (targetStep == null)
                targetStep = lot.CurrentFabStep;

            return lot.CurrentRework != null && (lot.CurrentRework.IsStarted && targetStep == lot.CurrentRework.ReworkTriggerStep) || targetStep.IsReworkDummyStep;
        }

        internal static Step GetReturnStep(FabSemiconLot lot)
        {
            // Rework Route Type: Rework Route 종료 후, FabWipInfo.ReturnStep에 세팅된, ReworkTriggerStep을 다시 진행하게 됨.
            // Rework Step Type: 기존 Route에서 벗어나지 않으므로 이곳과 무관하게 동작함.
            // Rework Time Type: Dummy Route 종료 후, currentFabStep.ReworkNextStep을 진행하게 됨. (ReworkTriggerStep을 DummyRouteStep으로 이미 진행함)
            var returnStep = lot.CurrentFabStep.ReworkNextStep != null ? lot.CurrentFabStep.ReworkNextStep : lot.FabWipInfo.ReturnStep;
            if (returnStep != null)
            {
                lot.FabWipInfo.ReturnStep = null;
                if (lot.Route == returnStep.Route)
                    return null;

                lot.Route = returnStep.Route;
                return returnStep;
            }

            return null;
        }

        internal static Step GetNextRouteStep(FabSemiconLot lot, Step step)
        {
            return step.GetDefaultNextStep() ?? GetReturnStep(lot);
        }

        internal static bool NeedGetNextStepRework(FabSemiconLot lot)
        {
            return lot.CurrentRework != null && lot.CurrentRework.IsStarted == false;
        }

        internal static Step GetNextStepWithSampling(FabSemiconLot lot, Step next)
        {
            if (next != null && InputMart.Instance.ApplyStepSampling)
            {
                // check sampling
                bool again = false;
                do
                {
                    again = false;
                    var semiconStep = next as FabSemiconStep;

                    // SimulationStep(설비 Loading시도하는 Step)에만 SampleRate 적용.
                    // 참고: SimulationStep이라도 loadingRule에 따라 Bucketing 처리될 수 있음.
                    if (semiconStep.SampleRate < 1 && semiconStep.IsSimulationStep)
                    {
                        // if not sampled
                        if (Helper.GetBernoulliTrialResult(semiconStep.SampleRate) == false)
                        {
                            string reason = string.Format("{0} is skipped by sampling rate {1:0.000}", next.StepID, semiconStep.SampleRate);
                            OutputHelper.WriteWipLog(LogType.INFO, "APPLY_SAMPLING", lot, AoFactory.Current.NowDT, reason, 1, semiconStep);

                            if (IsReworkEnd(lot, semiconStep))
                                lot.CurrentRework = null;

                            HandleQtimeForSamplingSkip(lot, semiconStep);

                            // skip
                            next = GetNextRouteStep(lot, semiconStep);

                            if (next != null)
                                again = true;
                        }
                    }
                } while (again);
            }

            return next;
        }

        private static void HandleQtimeForSamplingSkip(FabSemiconLot lot, FabSemiconStep skipStep)
        {
            #region OnStartTask/WRITE_QTIME_HISTORY 대체
            OutputHelper.WriteQtimeHistoryDone(lot, QtType.MAX, skipStep.StepID);
            OutputHelper.WriteQtimeHistoryDone(lot, QtType.MIN, skipStep.StepID);
            SetOrderingStepStarted(lot);
            #endregion

            #region OnStartTask/ON_START_TASK_QTIME_DEF 대체
            //** QtManager.Current.FinishQtime(lot, now);
            lot.MaxQtActivations.Remove(skipStep.StepID);
            lot.MinQtActivations.Remove(skipStep.StepID);

            //** QtManager.Current.UpdateQtWorkloadOnStart(lot, aeqp);
            // -> 대체구현 분량이 많아서 생략함
            // -> SourceStep에서 SamplingSkip하는 Loop이 있다면 추가로 처리 필요.

            //** QtManager.Current.UpdateQtWorkloadOnEnd(lot, aeqp); 
            HashSet<QtEqp> hashSet = lot.QtDestStepEqpCache.SafeGet(skipStep.StepID);
            if (hashSet != null)
            {
                foreach (QtEqp item in hashSet)
                {
                    foreach (QtCategory value2 in item.Categories.Values)
                    {
                        if (value2.Lots.TryGetValue(lot, out var value))
                        {
                            value2.WorkloadHours -= value;
                            value2.Lots.Remove(lot);
                            QtWorkloadControl.Instance.OnUpdateWorkload(lot, item, value2, value, "OUT");
                        }
                    }
                }
            }
            #endregion

            #region OnEndTask/ON_END_TASK_QTIME_DEF 대체
            //** QtManager.Current.ActivateQtime(lot, now); 
            // -> 대체구현 분량이 많아서 생략함
            // -> SourceStep에서 SamplingSkip하는 Loop이 있다면 추가로 처리 필요.
            #endregion
        }

        internal static void SetOrderingStepStarted(FabSemiconLot lot)
        {
            if (lot.ReservationInfos.IsNullOrEmpty() == false)
            {
                var orderingLoops = lot.MaxWaitQtLoops.Where(x => x.ControlType == QtControlType.Ordering);

                foreach (var loop in orderingLoops)
                {
                    var resvInfo = lot.ReservationInfos.SafeGet(loop.EndStepID);
                    if (resvInfo == null)
                        continue;

                    var batch = lot.ReservationInfos.SafeGet(loop.EndStepID).Batch;
                    if (batch.IsNullOrEmpty())
                        continue;

                    var eta = batch.Contents.FirstOrDefault(x => (x as FabLotETA).Lot == lot) as FabLotETA;
                    if (eta == null)
                        continue;

                    eta.IsOrderingStepStarted = true;
                }
            }
        }

        internal static void AssignLotDueDate(FabWipInfo wip, DateTime? dueDate = null)
        {
            // ## WIP의 DueDate
            // Pegging된 경우, WRITE_PEG 시점에 wip.DueDate 값이 업데이트 되었음.
            // UnPeg될 경우, WIP_PARAM의 due_date 값을 그대로 유지하고 있음.

            // ## InPlan의 DueDate
            // B/W 수행 시, InTarget의 DueDate 값으로 업데이트 되었음.
            // B/W 수행하지 않지만 Demand가 있으면 Demand와 맵핑하여 업데이트
            // Demand와 맵핑이 안되면, FAB_IN_PLAN의 DUE_DATE값 사용.

            if (dueDate != null)
            {
                wip.DueDate = (DateTime)dueDate;
            }

            // ## 공통사항
            // 지정값이 없는 경우 StartTime(FabInTime)으로 부터 TAT를 적용해서 DueDate 산출 (InitialStep의 POT_DAYS값 사용)
            if (wip.DueDate == DateTime.MaxValue)
            {
                if (wip.FabInTime > DateTime.MinValue && wip.FabInTime < DateTime.MaxValue)
                {
                    wip.DueDate = wip.FabInTime.AddDays((wip.InitialStep as FabSemiconStep).PotDays);
                }
            }
        }

        private static DateTime GetPeggingDueDateWithFabInPlan(FAB_IN_PLAN entity)
        {
            var defaultDueDate = entity.DUE_DATE > DateTime.MinValue ? entity.DUE_DATE : DateTime.MaxValue;

            if (Helper.GetConfig(ArgsGroup.Simulation_Run).modules == 2) // B/W + F/W
            {
                // 지정된 값이 있어도, InTarget에 별도 Pegging한 DueDate를 우선 사용.

                var fabProudct = InputMart.Instance.FabProductMfgPartView.FindRows(entity.MFG_PART_ID).First();

                var inTargets = InputMart.Instance.InTargets.Where(x => x.ProductID == fabProudct.StdProductID).ToList();
                if (inTargets.IsNullOrEmpty())
                    return defaultDueDate;

                var firstInTarget = inTargets.First();
                var remainQty = (double)entity.WAFER_QTY;
                while (remainQty > 0 && inTargets.IsNullOrEmpty() == false)
                {
                    var currentTarget = inTargets.First();

                    var pegQty = Math.Min(remainQty, currentTarget.FabInPlanRemainQty);
                    remainQty -= pegQty;
                    currentTarget.FabInPlanRemainQty -= pegQty;

                    if (currentTarget.FabInPlanRemainQty == 0)
                        inTargets.Remove(currentTarget);
                }

                return firstInTarget.MoPlan.DueDate;
            }

            return defaultDueDate;
        }

        internal static void SetCurrentBOMInfo(FabSemiconLot lot, string bomParentStr = null)
        {
            // MergeableKey == null 인 기본 값
            lot.CurrentBOM = lot.FabProduct.BOMInfo;
            if (lot.CurrentBOM == null)
                return;

            if (lot.FabWipInfo.InitialStep == lot.CurrentBOM.MergeStep)
                lot.IsWaitForKitting = true;

            if (bomParentStr == null) // 초기 Wip
                bomParentStr = lot.FabWipInfo.WipParamDict.SafeGet("bom_parent_lot_id");
            else
                lot.FabWipInfo.WipParamDict.Add("bom_parent_lot_id", bomParentStr); // NextBOMLevel에서 찾을 수 있도록 세팅

            if (bomParentStr.IsNullOrEmpty())
                return;

            var bomParentSplit = bomParentStr.Split(';');
            var currentBomLevelStr = bomParentSplit.Where(x => x.StartsWith(lot.CurrentBOM.BOMLevel.ToString())).FirstOrDefault();
            if (currentBomLevelStr.IsNullOrEmpty())
                return;

            var split = currentBomLevelStr.Split('=');
            if (split.Length != 2)
                return;

            var parentLotId = split[1];

            var targetBomInfo = InputMart.Instance.BOMInfoMergeKeyView.FindRows(parentLotId).FirstOrDefault();
            if (targetBomInfo == null)
            {
                targetBomInfo = lot.CurrentBOM.ShallowCopy();
                targetBomInfo.MergeableKey = parentLotId;
                targetBomInfo.ProcessedSteps = new HashSet<string>();

                InputMart.Instance.BOMInfo.ImportRow(targetBomInfo);
            }

            lot.CurrentBOM = targetBomInfo;
        }

        internal static float GetBOMContributionQty(this FabSemiconLot lot)
        {
            return lot.UnitQty * lot.FabProduct.BOMContributionRatio;
        }
    }
}