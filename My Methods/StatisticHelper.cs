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
using Mozart.Simulation.Engine;
using Mozart.SeePlan;
using Mozart.Data.Entity;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class StatisticHelper
    {
        public static void CollectStepWip(ITimerAgent agent, object sender)
        {
#if true
            DateTime targetDate = Helper.GetTargetDate(AoFactory.Current.NowDT, false);

            float interval = 24f;
            
            agent.Add(sender, CollectStepWip, targetDate.AddHours(interval) - AoFactory.Current.NowDT);
#else
            DateTime targetDate = InputMart.Instance.IsSimulatorMode ? ShopCalendar.StartTimeOfDayT(AoFactory.Current.NowDT)
        : ShopCalendar.ShiftStartTimeOfDayT(AoFactory.Current.NowDT); 

            float interval = InputMart.Instance.IsSimulatorMode ? 24f : ShopCalendar.ShiftHours;

            agent.Add(sender, CollectStepWip, targetDate.AddHours(interval) - AoFactory.Current.NowDT);
#endif

            var mgr = AoFactory.Current.WipManager;

            var group = mgr.GetGroup(Constants.WIP_GROUP_STEP_WIP);

            // KEY : Part Step
            var stepWipDict = new Dictionary<Tuple<string, string>, STEP_WIP>();
            var transitDict = new MultiDictionary<Tuple<string, string>, FabSemiconLot>();

            // KEY : RouteStep
            var lotSizeMergeDict = new MultiDictionary<Tuple<FabProduct, FabSemiconStep, string, bool>, FabSemiconLot>();

            // SUMMARY_FABIO_WIP 출력을 위한 데이터를 STEP_WIP에 의존하여 집계하지 않도록 별도로 수집.
            var fabIODict =
                new Dictionary<(string partId, string stepId), (DateTime targetDate, float waitQty, float runQty, float totalQty)>();
         
            foreach (FabSemiconLot sample in group.UniqueValues())
            {
                // WARNING : 동일한 조건에서 실행해도, sample 값은 재현되지 않고 매번 다르게 나옴.
                float waitQty = 0f;
                float runQty = 0f;
                float hotQty = 0f;

                foreach (FabSemiconLot entity in group.Find(sample))
                {
                    if (entity.CurrentFabStep.IsStatisticalAnalysisStep() == false || entity.CurrentState == Mozart.SeePlan.Simulation.EntityState.MOVE)
                    {
                        var step = GetNextWipMoveCollectStep(entity.CurrentFabStep);
                        if (step != null)
                        {
                            var transitKey = new Tuple<string, string>(entity.CurrentPartID, step.StepID);
                            transitDict.Add(transitKey, entity);
                        }

                        continue;
                    }

                    // 통계 집계대상 Step이면서 MOVE 상태가 아닌 경우 (ATSTEP)
                    if (entity.CurrentState == Mozart.SeePlan.Simulation.EntityState.RUN)
                        runQty += entity.GetBOMContributionQty();
                    else
                    {
                        waitQty += entity.GetBOMContributionQty();

                        if (InputMart.Instance.DoLotSizeMerge)
                        {
                            if (entity.CurrentState == Mozart.SeePlan.Simulation.EntityState.WAIT)
                            {
                                string activeStackStr = string.Empty;
                                if (entity.ActiveStackDict.IsNullOrEmpty() == false)
                                {
                                    activeStackStr = entity.ActiveStackDict.Values.Where(x => x.StackEqp != null)
                                        .Select(x => Helper.CreateKey(x.StackGroupID, x.StackEqp.ResID)).Join(",");
                                }
                                var lotSizeMergeKey = new Tuple<FabProduct, FabSemiconStep, string, bool>
                                    (entity.FabProduct, entity.CurrentStep as FabSemiconStep, activeStackStr, entity.CurrentRework != null);
                                lotSizeMergeDict.Add(lotSizeMergeKey, entity);
                            }
                        }
                    }

                    if (entity.IsHotLot)
                        hotQty += entity.GetBOMContributionQty();
                }

                var row = WriteAtStepWip(targetDate, sample, waitQty, runQty, hotQty);

                if (row != null)
                {
                    var key = new Tuple<string, string>(sample.CurrentPartID, sample.CurrentFabStep.StepID);
                    stepWipDict.Add(key, row);
                }

                //TODO: WIP_QTY로 TOTAL을 쓸지 ATSTEP을 쓸지
                if ((sample.CurrentStep as FabSemiconStep).IsPhotoStep)
                    CollectPhotoStepWip(sample, waitQty, runQty);

                CollectFabIODict(targetDate, fabIODict, sample, waitQty, runQty);
            }

            foreach (var item in transitDict)
            {
                UpdateStepWip(item, stepWipDict);

                UpdateFabIOWip(item, targetDate, fabIODict);
            }

            foreach (var item in fabIODict)
            {
                CollectFabIOWip(item);
            }

            EntityHelper.DoLotSizeMerge(lotSizeMergeDict);

            static void CollectPhotoStepWip(FabSemiconLot sample, float waitQty, float runQty)
            {
                int totalQty = (int)(waitQty + runQty);
                if (InputMart.Instance.PhotoStepWipDict.ContainsKey(sample.CurrentStepID) == false)
                    InputMart.Instance.PhotoStepWipDict.Add(sample.CurrentStepID, totalQty);
                else
                    InputMart.Instance.PhotoStepWipDict[sample.CurrentStepID] += totalQty;
            }

            static void CollectFabIODict(DateTime targetDate, Dictionary<(string partId, string stepId), (DateTime targetDate, float waitQty, float runQty, float totalQty)> fabIODict, FabSemiconLot sample, float waitQty, float runQty)
            {
                (string partId, string stepId) fabIOKey = new(sample.CurrentPartID, sample.CurrentStepID);

                // 이 단계에서 키 중복은 발생할 수 없음. (Wip Group의 Key가 PartStep이므로)
                if (fabIODict.ContainsKey(fabIOKey))
                    return; // unexpected

                var fabIOValue = (targetDate, waitQty, runQty, waitQty + runQty);
                fabIODict[fabIOKey] = fabIOValue;
            }

            static void UpdateStepWip(KeyValuePair<Tuple<string, string>, ICollection<FabSemiconLot>> item, Dictionary<Tuple<string, string>, STEP_WIP> stepWipDict)
            {
                STEP_WIP row = GetStepWipRow(item, stepWipDict);

                if (row == null)
                    return;

                // TRANSIT 재공은 StepWip 집계대상 step의 재공으로 집계하되, TOTAL_QTY에 포함되고, ATSTEP_QTY에서는 제외
                row.TRANSIT_QTY = Math.Round(item.Value.Select(x => x.GetBOMContributionQty()).Sum(), 2);
                row.TOTAL_QTY = Math.Round(row.TRANSIT_QTY + row.WAIT_QTY + row.RUN_QTY, 2);
                row.HOT_QTY += Math.Round(item.Value.Where(x => x.IsHotLot).Select(x => x.GetBOMContributionQty()).Sum(), 2);
            }

            static void UpdateFabIOWip(KeyValuePair<Tuple<string, string>, ICollection<FabSemiconLot>> item, DateTime targetDate, Dictionary<(string partId, string stepId), (DateTime targetDate, float waitQty, float runQty, float totalQty)> fabIODict)
            {
                (string partId, string stepId) key = (item.Key.Item1, item.Key.Item2);

                // TRANSIT 재공만 있는 경우.
                if (fabIODict.TryGetValue(key, out var fabIoResult) == false)
                    fabIoResult.targetDate = targetDate;

                fabIoResult.totalQty = item.Value.Select(x => x.GetBOMContributionQty()).Sum() + fabIoResult.waitQty + fabIoResult.runQty;
                fabIODict[key] = fabIoResult;
            }

            static void CollectFabIOWip(KeyValuePair<(string partId, string stepId),
                (DateTime targetDate, float waitQty, float runQty, float totalQty)> item)
            {
                if (InputMart.Instance.FabInOutInfo.TryGetValue(item.Key.partId, item.Value.targetDate, out FabInOutInfo info) == false)
                {
                    info = new FabInOutInfo();
                    info.TARGET_DATE = item.Value.targetDate;
                    info.TARGET_WEEK = Helper.GetFormattedTargetWeek(item.Value.targetDate);
                    info.TARGET_MONTH = Helper.GetFormattedTargetMonth(item.Value.targetDate);
                    info.PART_ID = item.Key.partId;

                    InputMart.Instance.FabInOutInfo.Add(item.Key.partId, item.Value.targetDate, info);
                }

                info.WIP_QTY += item.Value.totalQty;
            }
        }

        private static FabSemiconStep GetNextWipMoveCollectStep(FabSemiconStep currentFabStep)
        {
            if (currentFabStep.IsStatisticalAnalysisStep())
                return currentFabStep;

            var step = currentFabStep.NextStep as FabSemiconStep;
            while (step != null)
            {
                if (currentFabStep.IsStatisticalAnalysisStep())
                    break;

                step = step.NextStep as FabSemiconStep;
            }

            return step;
        }

        private static STEP_WIP GetStepWipRow(KeyValuePair<Tuple<string, string>, ICollection<FabSemiconLot>> kvp, 
            Dictionary<Tuple<string, string>, STEP_WIP> stepWipDict)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STEP_WIP"))
                return null;

            var key = kvp.Key;

            var row = stepWipDict.SafeGet(key);
            if (row == null)
            {
                row = CreateStepWipRow(Helper.GetTargetDate(AoFactory.Current.NowDT, false), kvp.Value.First());
                stepWipDict.Add(key, row);

                OutputMart.Instance.STEP_WIP.Add(row);
            }

            return row;
        }

        private static STEP_WIP WriteAtStepWip(DateTime targetDate, FabSemiconLot sample, float waitQty, float runQty, float hotQty)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STEP_WIP"))
                return null;

            if (sample.CurrentFabStep.IsStatisticalAnalysisStep() == false)
                return null;

            STEP_WIP row = CreateStepWipRow(targetDate, sample);

            row.WAIT_QTY = Math.Round(waitQty, 2);
            row.RUN_QTY = Math.Round(runQty, 2);
            row.ATSTEP_QTY = Math.Round(waitQty + runQty, 2);
            row.TOTAL_QTY = Math.Round(row.TRANSIT_QTY + row.ATSTEP_QTY, 2);
            row.HOT_QTY = Math.Round(hotQty, 2);

            OutputMart.Instance.STEP_WIP.Add(row);

            return row;
        }

        private static STEP_WIP CreateStepWipRow(DateTime targetDate, FabSemiconLot sample)
        {
            STEP_WIP row = new STEP_WIP();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.TARGET_DATE = targetDate;
            row.TARGET_WEEK = Helper.GetTargetWeek(targetDate);

            row.LINE_ID = sample.LineID;
            row.PART_ID = sample.CurrentPartID;
            row.STEP_ID = sample.CurrentStepID;

            row.AREA_ID = sample.CurrentFabStep.AreaID;
            row.LAYER_ID = sample.CurrentFabStep.LayerID;
            row.STEP_SEQ = sample.CurrentFabStep.Sequence;

            return row;
        }

        public static CycleTimePeriodic GetOrAddPeriodicObject(FabSemiconLot lot, FabSemiconEqp eqp)
        {
            if (lot == null)
                return null; // unexpected

            var step = lot.CurrentFabStep;
            if (step == null || step.IsStatisticalAnalysisStep(false) == false)
                return null; // Standard Route에 있는 Step에 대해서만 통계량 집계 (BOM을 사용할 경우 예외적으로 모든 Step에 대해서 집계)

            var eqpId = eqp == null ? lot.CurrentFabPlan.EqpID : eqp.ResID;

            var cyclePeriod = InputMart.Instance.CycleTimePeriodic.Rows.Find(lot.FabProduct.PartGroup, lot.CurrentPartID, lot.CurrentStepID,
                                    Helper.ExtractNumber(step.LayerID), step.AreaID, eqpId);

            if (cyclePeriod == null)
            {
                cyclePeriod = new CycleTimePeriodic();
                cyclePeriod.PartGroup = lot.FabProduct.PartGroup;
                cyclePeriod.PartID = lot.CurrentPartID;
                cyclePeriod.StepID = lot.CurrentStepID;
                cyclePeriod.LayerID = Helper.ExtractNumber(lot.CurrentFabStep.LayerID);
                cyclePeriod.AreaID = lot.CurrentFabStep.AreaID;
                cyclePeriod.EqpID = eqpId;

                // MemoryClear 발생해도 기록할 수 있도록, 경계에 속하면 뒷 구간에 포함.
                cyclePeriod.TargetDate = Helper.GetTargetDate(AoFactory.Current.NowDT, false);  

                cyclePeriod.Eqp = eqp;
                cyclePeriod.PhotoGen = eqp != null ? eqp.ScannerGeneration : null;
                cyclePeriod.StackType = lot.CurrentActiveStackInfo?.StackStepInfo.StackType.ToString();

                InputMart.Instance.CycleTimePeriodic.Rows.Add(cyclePeriod);
            }

            return cyclePeriod;
        }

        public static void CalculateTotalSummary(EntityCollection<CycleTimeInfo> entityCollection)
        {
            foreach (var partItems in entityCollection.GroupBy(x => x.PartID))
            {
                double cumCT = 0;
                foreach (var layerItems in partItems.GroupBy(x => x.LayerID).OrderBy(x => x.Key))
                {
                    TATSet layerTAT = new TATSet();
                    foreach (var stepItems in layerItems.GroupBy(x => x.StepID))
                    {
                        var stepTAT = GetProperTATSet(stepItems);

                        layerTAT.AccumulateSet(stepTAT);
                    }
                    var layerCT = layerTAT.TotalTAT;

                    cumCT += layerCT;

                    WriteSummaryLayerCT(layerItems.First(), cumCT);
                }

                foreach (var areaItems in partItems.GroupBy(x => x.AreaID))
                {
                    TATSet partAreaTAT = new TATSet();
                    foreach (var stepItems in areaItems.GroupBy(x => x.StepID))
                    {
                        var tatSet = GetProperTATSet(stepItems);

                        partAreaTAT.AccumulateSet(tatSet);
                    }
                    
                    WriteSummaryDynamicCT(areaItems.First(), partAreaTAT);
                }
            }
        }

        public static void CalculatePeriodicSummary(EntityCollection<CycleTimePeriodic> entityCollection)
        {
            if (Helper.GetConfig(ArgsGroup.Simulation_Output).writePeriodicSummary == "N")
                return;

            IncludeMissingSteps(true);

            foreach (var partItems in entityCollection.GroupBy(x => x.PartID))
            {
                double cumCT = 0;
                foreach (var layerItems in partItems.GroupBy(x => x.LayerID).OrderBy(x => x.Key))
                {
                    TATSet layerTAT = new TATSet();
                    foreach (var stepItems in layerItems.GroupBy(x => x.StepID))
                    {
                        var stepTAT = GetProperTATSet(stepItems);

                        WriteSummaryDynamicCT2(stepItems.First(), stepTAT);

                        layerTAT.AccumulateSet(stepTAT);
                    }
                    var layerCT = layerTAT.TotalTAT;

                    cumCT += layerCT;

                    WriteSummaryLayerCT2(layerItems.First(), cumCT);
                }
            }

            foreach (var eqpItems in entityCollection.Where(x => x.Eqp != null).GroupBy(x => x.EqpID))
            {
                var tatSet = GetProperTATSet(eqpItems, true);

                if (eqpItems.Where(x => x.AreaID == Helper.GetConfig(ArgsGroup.Simulation_Report).photoArea).Any()) 
                {
                    WriteSummaryPhotoWPD(eqpItems.First(), tatSet);
                    WriteSummaryPhotoEQP(eqpItems, tatSet);
                }

                WriteSummaryEqpWaitCT(eqpItems.First(), tatSet);

                // Stacking Step에 대해서 별도의 Category로 추가 집계
                foreach (var eqpCategoryItems in eqpItems.GroupBy(x => x.StackType))
                {
                    if (eqpCategoryItems.Key.IsNullOrEmpty())
                        continue;

                    tatSet = GetProperTATSet(eqpCategoryItems, true);

                    WriteSummaryEqpWaitCT(eqpCategoryItems.First(), tatSet, true);
                }
            }

            foreach (var stepItems in entityCollection.GroupBy(x => x.StepID))
            {
                var photoGen = stepItems.First().PhotoGen;
                if (photoGen.IsNullOrEmpty())
                    continue;

                var tatSet = GetProperTATSet(stepItems);

                WriteSummaryPhotoWip(stepItems.First(), tatSet);
            }
        }

        private static TATSet GetProperTATSet(IGrouping<string, CycleTimeInfo> items, bool calcEqpWaitCT = false)
        {
            var moveQtySum = items.Select(x => x.MoveQtySum).Sum();

            var tatSet = new TATSet();
            tatSet.MoveQty = (int)moveQtySum;

            if (tatSet.MoveQty > 0) // 가중평균 사용
            {
                tatSet.TotalTAT = items.Select(x => x.TotalTATWeightedSum).Sum() / moveQtySum;
                tatSet.WaitTAT = items.Select(x => x.WaitTATWeightedSum).Sum() / moveQtySum;
                tatSet.RunTAT = items.Select(x => x.RunTATWeightedSum).Sum() / moveQtySum;
            }
            else // 단순평균 사용
            {
                tatSet.TotalTAT = items.Select(x => x.TotalTATWeightedSum).Average();
                tatSet.WaitTAT = items.Select(x => x.WaitTATWeightedSum).Average();
                tatSet.RunTAT = items.Select(x => x.RunTATWeightedSum).Average();
            }

            if (calcEqpWaitCT)
            {
                var lotCount = items.Select(x => x.LotCount).Sum();
                if (lotCount > 0)
                    tatSet.WaitTATSimple = items.Select(x => x.WaitTATSum).Sum() / lotCount;
            }

            return tatSet;
        }

        private static TATSet GetProperTATSet(List<TATSet> items)
        {
            var moveQtySum = items.Select(x => x.MoveQty).Sum();

            var tatSet = new TATSet();
            tatSet.MoveQty = (int)moveQtySum;

            if (tatSet.MoveQty > 0) // 가중평균 사용
            {
                tatSet.TotalTAT = items.Select(x => x.TotalTAT * x.MoveQty).Sum() / moveQtySum;
                tatSet.WaitTAT = items.Select(x => x.WaitTAT * x.MoveQty).Sum() / moveQtySum;
                tatSet.RunTAT = items.Select(x => x.RunTAT * x.MoveQty).Sum() / moveQtySum;
            }
            else // 단순평균 사용
            {
                tatSet.TotalTAT = items.Select(x => x.TotalTAT).Average();
                tatSet.WaitTAT = items.Select(x => x.WaitTAT).Average();
                tatSet.RunTAT = items.Select(x => x.RunTAT).Average();
            }

            return tatSet;
        }

        private static void AccumulateSet(this TATSet routeSet, TATSet stepSet)
        {
            routeSet.TotalTAT += stepSet.TotalTAT;
            routeSet.RunTAT += stepSet.RunTAT;
            routeSet.WaitTAT += stepSet.WaitTAT;
            routeSet.MoveQty += stepSet.MoveQty;
        }

        private static void WriteSummaryDynamicCT(CycleTimeInfo item, TATSet set)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_DYNAMIC_CT"))
                return;

            SUMMARY_DYNAMIC_CT row = new SUMMARY_DYNAMIC_CT();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.AREA_ID = item.AreaID.IsNullOrEmpty() ? "-" : item.AreaID; // PK
            row.PART_ID = item.PartID;

            row.TOTAL_TAT = set.TotalTAT;
            row.WAIT_TAT = set.WaitTAT;
            row.RUN_TAT = set.RunTAT;

            row.MOVE_QTY = set.MoveQty;

            OutputMart.Instance.SUMMARY_DYNAMIC_CT.Add(row);
        }

        private static void WriteSummaryDynamicCT2(CycleTimePeriodic item, TATSet set)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_DYNAMIC_CT2"))
                return;

            SUMMARY_DYNAMIC_CT2 row = new SUMMARY_DYNAMIC_CT2();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            var targetDate = CalculateTargetDateInPeriod(item.TargetDate);

            row.TARGET_DATE = targetDate;
            row.AREA_ID = item.AreaID.IsNullOrEmpty() ? "-" : item.AreaID; // PK;
            row.PART_ID = item.PartID;
            row.STEP_ID = item.StepID;

            row.TOTAL_TAT = set.TotalTAT;
            row.WAIT_TAT = set.WaitTAT;
            row.RUN_TAT = set.RunTAT;

            row.MOVE_QTY = set.MoveQty;

            OutputMart.Instance.SUMMARY_DYNAMIC_CT2.Add(row);
        }

        private static void WriteSummaryLayerCT(CycleTimeInfo item, double cumCT)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_LAYER_CT"))
                return;

            SUMMARY_LAYER_CT row = new SUMMARY_LAYER_CT();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.PART_GROUP = item.PartGroup;
            row.PART_ID = item.PartID;
            row.LAYER_ID = item.LayerID;
            row.EQP_GROUP = InputMart.Instance.PartLayerEqpGroup.SafeGet(item.PartID).SafeGet(item.LayerID);
            row.CUM_CT = Math.Round(cumCT, 3);

            OutputMart.Instance.SUMMARY_LAYER_CT.Add(row);
        }

        private static void WriteSummaryLayerCT2(CycleTimePeriodic item, double cumCT)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_LAYER_CT2"))
                return;

            SUMMARY_LAYER_CT2 row = new SUMMARY_LAYER_CT2();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            var targetDate = CalculateTargetDateInPeriod(item.TargetDate);

            row.TARGET_DATE = targetDate;
            row.PART_GROUP = item.PartGroup;
            row.PART_ID = item.PartID;
            row.LAYER_ID = item.LayerID;
            row.EQP_GROUP = InputMart.Instance.PartLayerEqpGroup.SafeGet(item.PartID).SafeGet(item.LayerID);
            row.CUM_CT = Math.Round(cumCT, 3);

            OutputMart.Instance.SUMMARY_LAYER_CT2.Add(row);
        }

        private static void WriteSummaryEqpWaitCT(CycleTimePeriodic item, TATSet set, bool writeCategory = false)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_EQP_WAIT_CT"))
                return;

            SUMMARY_EQP_WAIT_CT row = new SUMMARY_EQP_WAIT_CT();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            var targetDate = CalculateTargetDateInPeriod(item.TargetDate);

            row.TARGET_DATE = targetDate;
            row.AREA_ID = item.AreaID;
            row.SIM_TYPE = item.Eqp.SimType.ToString();
            row.EQP_GROUP = item.Eqp.ResGroup;
            row.EQP_ID = item.EqpID;
            row.CATEGORY = writeCategory ? item.StackType : "-";
            row.MOVE_QTY = set.MoveQty;
            row.WAIT_HOUR = Math.Round(set.WaitTATSimple, 3);

            OutputMart.Instance.SUMMARY_EQP_WAIT_CT.Add(row);
        }

        private static void WriteSummaryPhotoWip(CycleTimePeriodic item, TATSet set)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_PHOTO_WIP"))
                return;

            SUMMARY_PHOTO_WIP row = new SUMMARY_PHOTO_WIP();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            var targetDate = CalculateTargetDateInPeriod(item.TargetDate);

            row.TARGET_DATE = targetDate;
            row.TARGET_WEEK = Helper.GetFormattedTargetWeek(item.TargetDate);
            row.TARGET_MONTH = Helper.GetFormattedTargetMonth(item.TargetDate);
            row.STEP_ID = item.StepID;
            row.PHOTO_GEN = item.PhotoGen;

            row.WAIT_TAT = Math.Round(set.WaitTAT, 3);
            row.RUN_TAT = Math.Round(set.RunTAT, 3);
            row.WIP_QTY = InputMart.Instance.PhotoStepWipDict.SafeGet(row.STEP_ID);

            OutputMart.Instance.SUMMARY_PHOTO_WIP.Add(row);
        }

        private static void WriteSummaryPhotoWPD(CycleTimePeriodic item, TATSet set)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_PHOTO_WPD"))
                return;

            SUMMARY_PHOTO_WPD row = new SUMMARY_PHOTO_WPD();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            var targetDate = CalculateTargetDateInPeriod(item.TargetDate);

            row.TARGET_DATE = targetDate;
            row.TARGET_WEEK = Helper.GetFormattedTargetWeek(item.TargetDate);
            row.TARGET_MONTH = Helper.GetFormattedTargetMonth(item.TargetDate);
            row.EQP_ID = item.EqpID;
            row.PHOTO_GEN = item.PhotoGen;

            row.WAIT_TAT = Math.Round(set.WaitTAT, 3);
            row.RUN_TAT = Math.Round(set.RunTAT, 3);

            row.MOVE_QTY = set.MoveQty;

            OutputMart.Instance.SUMMARY_PHOTO_WPD.Add(row);
        }

        internal static void IncludeMissingSteps(bool isPeriodic = false)
        {
            var targetDate = Helper.GetTargetDate(AoFactory.Current.NowDT, true); // 직전 TargetDate

            List<CycleTimeInfo> missingSteps = new List<CycleTimeInfo>();
            foreach (var kvp in InputMart.Instance.PartRouteMap)
            {
                var partID = kvp.Key;
                var prod = InputMart.Instance.FabProductPartView.FindRows(partID).FirstOrDefault();
                if (prod == null)
                    continue;

                var stdRoute = kvp.Value;
                if (stdRoute == null)
                    continue;

                foreach (FabSemiconStep step in stdRoute.Steps)
                {
                    if (BopHelper.IsFabInOrFabOut(step.StepID))
                        continue;

                    var layerID = Helper.ExtractNumber(step.LayerID);
                    var stepCT = isPeriodic ? InputMart.Instance.CycleTimePeriodicPartStepView.FindRows(prod.PartID, step.StepID)
                        : InputMart.Instance.CycleTimeInfoPartStepView.FindRows(prod.PartID, step.StepID);

                    if (stepCT.IsNullOrEmpty() == false)
                        continue;

                    var missing = isPeriodic ? new CycleTimePeriodic() : new CycleTimeInfo();
                    missing.PartGroup = prod.PartGroup;
                    missing.PartID = prod.PartID;
                    missing.StepID = step.StepID;
                    missing.LayerID = layerID;

                    missing.AreaID = step.AreaID;
                    missing.EqpID = "-";

                    missing.TotalTATWeightedSum = step.CT.TotalHours;
                    missing.MoveQtySum = 0; // Moving이 0일 때는 나누지 않도록 처리함.

                    if (isPeriodic)
                        (missing as CycleTimePeriodic).TargetDate = targetDate;

                    missingSteps.Add(missing);
                }
            }

            if (isPeriodic)
                missingSteps.ForEach(x => InputMart.Instance.CycleTimePeriodic.Rows.Add(x as CycleTimePeriodic));
            else
                missingSteps.ForEach(InputMart.Instance.CycleTimeInfo.Rows.Add);
        }

        //private static void IncludeDailyMissingSteps()
        //{
        //    var targetDate = Helper.GetTargetDate(AoFactory.Current.NowDT.AddSeconds(-1)); // 직전 TargetDate
        //    List<CycleTimePeriodic> missingSteps = new List<CycleTimePeriodic>();
        //    foreach (var kvp in InputMart.Instance.PartRouteMap)
        //    {
        //        var partID = kvp.Key;
        //        var prod = InputMart.Instance.FabProductPartView.FindRows(partID).FirstOrDefault();
        //        if (prod == null)
        //            continue;

        //        var stdRoute = kvp.Value;
        //        foreach (FabSemiconStep step in stdRoute.Steps)
        //        {
        //            if (BopHelper.IsFabInOrFabOut(step.StepID))
        //                continue;

        //            var layerID = Helper.IntParse(step.LayerID, 0);
        //            var stepCT = InputMart.Instance.CycleTimePeriodicPartRouteStepView.FindRows(prod.PartID, stdRoute.RouteID, step.StepID);
        //            if (stepCT.IsNullOrEmpty() == false)
        //                continue;

        //            var missing = new CycleTimePeriodic();
        //            missing.PartGroup = prod.PartGroup;
        //            missing.PartID = prod.PartID;
        //            missing.RouteID = stdRoute.RouteID;
        //            missing.StepID = step.StepID;
        //            missing.LayerID = layerID;

        //            missing.AreaID = step.AreaID;
        //            missing.EqpID = "-";

        //            missing.TotalTATWeightedSum = step.CT.TotalHours;
        //            missing.MoveQtySum = 0; // Moving이 0일 때는 나누지 않도록 처리함.
        //            missing.TargetDate = targetDate;

        //            missingSteps.Add(missing);
        //        }
        //    }

        //    missingSteps.ForEach(InputMart.Instance.CycleTimePeriodic.Rows.Add);
        //}

        //internal static void IncludeTotalMissingSteps()
        //{
        //    List<CycleTimeInfo> missingSteps = new List<CycleTimeInfo>();
        //    foreach (var kvp in InputMart.Instance.PartRouteMap)
        //    {
        //        var partID = kvp.Key;
        //        var prod = InputMart.Instance.FabProductPartView.FindRows(partID).FirstOrDefault();
        //        if (prod == null)
        //            continue;

        //        var stdRoute = kvp.Value;
        //        foreach (FabSemiconStep step in stdRoute.Steps)
        //        {
        //            if (BopHelper.IsFabInOrFabOut(step.StepID))
        //                continue;

        //            var layerID = Helper.IntParse(step.LayerID, 0);
        //            var stepCT = InputMart.Instance.CycleTimeInfoPartRouteStepView.FindRows(prod.PartID, stdRoute.RouteID, step.StepID);
        //            if (stepCT.IsNullOrEmpty() == false)
        //                continue;

        //            var missing = new CycleTimeInfo();
        //            missing.PartGroup = prod.PartGroup;
        //            missing.PartID = prod.PartID;
        //            missing.RouteID = stdRoute.RouteID;
        //            missing.StepID = step.StepID;
        //            missing.LayerID = layerID;

        //            missing.AreaID = step.AreaID;
        //            missing.EqpID = "-";

        //            missing.TotalTATWeightedSum = step.CT.TotalHours;
        //            missing.MoveQtySum = 0; // Moving이 0일 때는 나누지 않도록 처리함.

        //            missingSteps.Add(missing);
        //        }
        //    }

        //    missingSteps.ForEach(InputMart.Instance.CycleTimeInfo.Rows.Add);
        //}

        internal static void DoPeriodicSummary(bool onDone = false)
        {
            if (AoFactory.Current.NowDT == ModelContext.Current.StartTime)
                return;

            var writePeriodicSummary = Helper.GetConfig(ArgsGroup.Simulation_Output).writePeriodicSummary;
            var today = AoFactory.Current.NowDT;

            if (writePeriodicSummary == "Daily")
            {
                SettlePeriodicSummary();
            }
            else if (writePeriodicSummary == "Weekly")
            {
                // 만일 오늘이 결산하는 요일이거나 simulation의 endtime이라면 실행
                if ((int)today.DayOfWeek == (int)(ShopCalendar.StartWeek) || onDone)
                {
                    SettlePeriodicSummary();
                }
            }
            else if (writePeriodicSummary == "Monthly")
            {
                // 만일 첫번째 날이거나 simulation의 endtime이라면 실행
                if (today.Day == 1 || onDone)
                {
                    SettlePeriodicSummary();
                }
            }
        }

        public static void WriteSummaryPhotoEQP(IGrouping<string, CycleTimePeriodic> items, TATSet set)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_PHOTO_EQP"))
                return;

            var item = items.First();

            int reworkCnt = items.Select(x => x.ReworkCnt).Sum();
            double reworkMin = items.Select(x => x.ReworkMin).Sum();
            int scrapCnt = items.Select(x => x.ScrapCnt).Sum();
            double scrapMin = items.Select(x => x.ScrapMin).Sum();
            int chuckCnt = items.Select(x => x.ChuckCnt).Sum();
            double chuckMin = items.Select(x => x.ChuckMin).Sum();
            int reticleCnt = items.Select(x => x.ReticleCnt).Sum();
            double reticleMin = items.Select(x => x.ReticleMin).Sum();
            var targetDate = CalculateTargetDateInPeriod(item.TargetDate);

            SUMMARY_PHOTO_EQP row = new SUMMARY_PHOTO_EQP();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.TARGET_DATE = targetDate;
            row.TARGET_WEEK = Helper.GetFormattedTargetWeek(item.TargetDate);
            row.TARGET_MONTH = Helper.GetFormattedTargetMonth(item.TargetDate);
            row.EQP_ID = item.EqpID;
            row.PHOTO_GEN = item.PhotoGen;
            row.WAIT_TAT = Math.Round(set.WaitTAT, 3);
            row.RUN_TAT = Math.Round(set.RunTAT, 3);
            row.MOVE_QTY = set.MoveQty;

            row.REWORK_CNT = reworkCnt;
            row.REWORK_MIN = Math.Round(reworkMin, 3);
            row.SCRAP_CNT = scrapCnt;
            row.SCRAP_MIN = Math.Round(scrapMin, 3);
            row.CHUCK_CNT = chuckCnt;
            row.CHUCK_MIN = Math.Round(chuckMin, 3);
            row.RETICLE_CNT = reticleCnt;
            row.RETICLE_MIN = Math.Round(reticleMin, 3);

            OutputMart.Instance.SUMMARY_PHOTO_EQP.Add(row);
            
        }

        public static void SettlePeriodicSummary()
        {
            CalculatePeriodicSummary(InputMart.Instance.CycleTimePeriodic.Rows);

            // Total에 반영하고 주기적 집계는 Clear
            InputMart.Instance.CycleTimePeriodic.Rows.ForEach(ApplyToSummaryTotalInfo);

            // TargetDate만 바꾸고 값을 초기화하는 방식은, Missing Step 발견이 어려워지므로 Clear후 새로 만드는 방식을 사용.
            // Memory 점유양상은 거의 차이 없음.
            InputMart.Instance.CycleTimePeriodic.Clear(); // 어떤 조건? 에서는 이걸 호출해야 메모리를 놓아줌.
            InputMart.Instance.CycleTimePeriodic.Rows.Clear();
            InputMart.Instance.PhotoStepWipDict.Clear();

            static void ApplyToSummaryTotalInfo(CycleTimeInfo item)
            {
                if (item.MoveQtySum == 0)
                    return; // MissingStep의 WeightedSum을 누적해버리면 TotalSummary 값이 왜곡되므로, Daily 값은 누적시키면 안되고, SimEnd시점에 재판단.

                var summaryInfo = InputMart.Instance.CycleTimeInfo.Rows.Find(item.PartGroup, item.PartID, item.StepID, item.LayerID, item.AreaID, item.EqpID);
                if (summaryInfo == null)
                {
                    summaryInfo = new CycleTimeInfo();
                    summaryInfo.AreaID = item.AreaID;
                    summaryInfo.PartGroup = item.PartGroup;
                    summaryInfo.PartID = item.PartID;
                    summaryInfo.StepID = item.StepID;
                    summaryInfo.LayerID = item.LayerID;
                    summaryInfo.EqpID = item.EqpID;

                    InputMart.Instance.CycleTimeInfo.Rows.Add(summaryInfo);
                }
                summaryInfo.WaitTATWeightedSum += item.WaitTATWeightedSum;
                summaryInfo.RunTATWeightedSum += item.RunTATWeightedSum;
                summaryInfo.TotalTATWeightedSum += item.TotalTATWeightedSum;
                summaryInfo.MoveQtySum += item.MoveQtySum;

                summaryInfo.WaitTATSum += item.WaitTATSum;
                summaryInfo.LotCount += item.LotCount;
            }
        }

        public static DateTime CalculateTargetDateInPeriod(DateTime item)
        {
            var today = AoFactory.Current.NowDT;

            if (Helper.GetConfig(ArgsGroup.Simulation_Output).writePeriodicSummary == "Daily")
            {
                return item;
            }
            else if (Helper.GetConfig(ArgsGroup.Simulation_Output).writePeriodicSummary == "Weekly")
            {
                // onDone 시점
                if (today.AddSeconds(1) >= ModelContext.Current.EndTime)
                {
                    // 이번 주의 시작 날짜로 옮기기
                    return Helper.GetWeekStartTime(today);
                }

                // 현재 날짜가 현재까지 작업한 기간의 다음 날짜이니 targetDate는 일주일 전으로 옮기자
                return today.AddDays(-7);
            }
            else if (Helper.GetConfig(ArgsGroup.Simulation_Output).writePeriodicSummary == "Monthly")
            {
                //onDone 시점
                if (today.AddSeconds(1) >= ModelContext.Current.EndTime)
                {
                    // 이번달 시작 날짜로 옮기기.
                    return Helper.GetMonthStartTime(today);
                }
                
                // 현재 요일이 1일이니 저번달로 옮기자
                return today.AddMonths(-1);
            }

            return today;
        }
    }
}