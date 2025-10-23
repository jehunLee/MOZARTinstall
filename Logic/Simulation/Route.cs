using Mozart.Simulation.Engine;
using Mozart.SeePlan.Simulation;
using Mozart.SeePlan.DataModel;
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
using Mozart.SeePlan;
using System.Diagnostics;
using static Mozart.SeePlan.Simulation.WipTags;
using static Mozart.SeePlan.Simulation.AoChamberProc2;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class Route
    {
        public LoadInfo CREATE_LOAD_INFO0(Mozart.SeePlan.Simulation.ILot lot, Step task, ref bool handled, LoadInfo prevReturnValue)
        {
            LoadInfo info = CreateHelper.CreateFabPlanInfo(lot, task as FabSemiconStep, lot.CurrentStep != null);

            var fhb = lot as FabHandlingBatch;
            if (fhb != null)
            {
                foreach (var content in fhb.mergedContents)
                {
                    var contentInfo = CreateHelper.CreateFabPlanInfo(content, task as FabSemiconStep);
                    content.SetCurrentPlan(contentInfo);
                }

#if false // WipManager 오류 원인으로 추정. -> IsDone 으로 위치 변경
                if (task.StepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabOutStepID)
                {
                    // ForwardPeg위해, FabOutStep 도달 시 전부 Split 처리.
                    var mergedList = fhb.mergedContents.ToList();
                    mergedList.ForEach(x => fhb.SplitLot(x, HandlingBatchSplitType.FabOut));

                    FabHandlingBatch.ClearHandlingBatch(fhb);
                }
                else
                {
                    var attr = (task as FabSemiconStep).PartStepDict.SafeGet(fhb.FabProduct.PartID);
                    if (attr != null && attr.CurrentArranges.Any(x => x.Eqp.SimType == SimEqpType.UnitBatch && x.Eqp.UnitBatchInfo.HasFinitePort))
                    {
                        // ## HandlingBatch 사용시, PortCount 제약이 있는 Arrange를 하나라도 가질 경우, HB를 미리 Split해서 Dispatching 참여하도록 처리했습니다.
                        // HB를 확산 없이 한 port로 몰아서 진행할 경우 실제 behavior가 과도하게 왜곡됨
                        // 후처리로 Split할 경우, 디스패칭 시간과 투입시간이 달라지는 등의 추가 고려사항이 발생하여, 유지보수 차원에서 단점이 더 많을 것으로 판단됨.

                        var mergedList = fhb.mergedContents.ToList();
                        mergedList.ForEach(x => fhb.SplitLot(x, HandlingBatchSplitType.UnitBatch));

                        FabHandlingBatch.ClearHandlingBatch(fhb);
                    }
                    else
                    {
                        foreach (var content in fhb.mergedContents)
                        {
                            var contentInfo = CreateHelper.CreateFabPlanInfo(content, task as FabSemiconStep);
                            content.SetCurrentPlan(contentInfo);
                        }
                    }
                } 
#endif
            }

            return info;
        }

        public IList<string> GET_LOADABLE_EQP_LIST0(DispatchingAgent da, IHandlingBatch hb, ref bool handled, IList<string> prevReturnValue)
        {
            return ResourceHelper.GetLoadableEqpList(hb, true);

            //static IList<string> GetStagingEqp(FabSemiconLot lot, PartStepAttribute attr)
            //{
            //    // 초기 설비가 지정된 경우(STAGING) DO_FILTER에서 특수 처리.
            //    // -> STAGING Lot이 모두 투입된 이후에 Normal Dispatching 수행 함.

            //    if (attr != null)
            //    {
            //        var arr = attr.CurrentArranges.Where(x => x.Eqp == lot.FabWipInfo.InitialEqp).FirstOrDefault();
            //        if (arr != null)
            //            lot.CurrentArranges.Add(arr.EqpID, arr);
            //    }

            //    var loadableEqpList = new List<string>();
            //    loadableEqpList.Add(lot.FabWipInfo.InitialEqp.ResID);

            //    return loadableEqpList;
            //}
        }

        public Step GET_NEXT_STEP0(ILot lot, LoadInfo loadInfo, Step step, DateTime now, ref bool handled, Step prevReturnValue)
        {
            var fLot = lot as FabSemiconLot;

            // TODO : Step이 바뀔때 초기화 필요한 것들 모이면 함수화
            fLot.IsWipHandle = false;
            fLot.ToolSettings = null; // 지우지 않으면, 다른 디스패칭에서 세팅된 정보를 계속 담고 있음.
            fLot.EvaluatePriority.Clear();

            if (EntityHelper.NeedGetNextStepRework(fLot))
            {
                // GET_NEXT_STEP_REWORK 에서 처리.
                return prevReturnValue;
            }

            // Rework Start에 대한 처리가 아니면 다음 definition으로 넘어갈 필요 없고 handled 처리 함.
            handled = true;
            
            if (EntityHelper.IsReworkEnd(fLot))
                fLot.CurrentRework = null; // Rework 이후 TriggerStep을 재차 진행 완료했으면 Activation 정보 삭제.

            if (fLot.IsYieldScrapped || fLot.IsVanishing)
                return null;

            Step next = EntityHelper.GetNextRouteStep(fLot, step);

            next = EntityHelper.GetNextStepWithSampling(fLot, next);

            if (next == null && fLot.CurrentBOM != null)
            {
                // GET_NEXT_STEP_BOM 에서 처리.
                handled = false;
                return prevReturnValue;
            }

            return next;
        }

        public void WRITE_QTIME_HISTORY(IHandlingBatch hb, Mozart.Simulation.Engine.ActiveObject ao, DateTime now, ref bool handled)
        {
            foreach (var entity in hb)
            {
                var lot = entity as FabSemiconLot;

                OutputHelper.WriteQtimeHistoryDone(lot, QtType.MAX);
                OutputHelper.WriteQtimeHistoryDone(lot, QtType.MIN);

                EntityHelper.SetOrderingStepStarted(lot);
            }
        }

        public void ON_DISPATCHED0(DispatchingAgent da, AoEquipment aeqp, IHandlingBatch[] sels, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;
            foreach (var hb in sels)
            {
                var lot = hb as FabSemiconLot;

                if (lot.CurrentFabPlan.ArrivalTimeDict != null)
                {
                    DateTime arrivalTime = DateTime.MinValue;
                    if (lot.CurrentFabPlan.ArrivalTimeDict.TryGetValue(feqp.Eqp.ResID, out arrivalTime))
                        lot.DispatchInTime = arrivalTime;
                }

                lot.CurrentFabPlan.ArrivalTimeStr = lot.DispatchInTime.ToString("yyyy-MM-dd HH:mm:ss");

                Helper.ClearFabPlanInfoMemory(lot);
            }
        }

        public void ON_DONE0(IHandlingBatch hb, ref bool handled)
        {
            var lot = hb as FabSemiconLot;
            lot.IsVanishing = true;

            ArrangeHelper.RemoveActiveStackFromEqp(lot);

            Helper.ClearLotCollectionMemory(lot);

            var fhb = hb as FabHandlingBatch;
            if (fhb != null)
                return; // FabOut 도착 시 원본 Lot을 다 되살리고 난 dummy. // 또는 전부 Split 되고 빈 dummy.

            if (hb.CurrentStep.StepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabOutStepID)
            {
                DateTime targetDate = ShopCalendar.StartTimeOfDayT(AoFactory.Current.NowDT);
                if (InputMart.Instance.FabOutQty.ContainsKey(targetDate) == false)
                    InputMart.Instance.FabOutQty.Add(targetDate, hb.UnitQty);
                else
                {
                    var qty = InputMart.Instance.FabOutQty[targetDate];
                    InputMart.Instance.FabOutQty[targetDate] = qty + hb.UnitQty;
                }

                OutputHelper.WriteFabOutResult(lot, AoFactory.Current.NowDT);
            }
        }

        public void ON_END_TASK_QTIME(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            //if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 0)
            //    handled = true;
        }

        public void ON_START_TASK0(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            // OnTrackIn과 다른점
            // 1. 초기 Run중인 재공도 호출됨
            // 2. Bucketing 처리된 재공도 호출됨 (이 경우 ao는 AoBucketer)
            var feqp = ao as FabAoEquipment;
            if (feqp == null)
                return;

            foreach (var entity in hb)
            {
                var lot = entity as FabSemiconLot;

                ArrangeHelper.UpdateActiveStackOnStart(lot, feqp.Eqp);
            }
        }

        public void APPLY_QUALITY_LOSS(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            // Yield -> Rework 순으로 순차 적용
            foreach (var entity in hb)
            {
                var lot = entity as FabSemiconLot;
                var feqp = ao as FabAoEquipment;
                var eqp = feqp == null ? null : feqp.Eqp;

                if (EntityHelper.IsQualityLossProcessing(lot))
                {
                    if (lot is FabHandlingBatch)
                        continue; // unexpected

                    if (lot.CurrentRework != null)
                        OutputHelper.WriteQualityLoss(lot, lot, eqp, lot.CurrentRework.QualityLossType, null, lot.CurrentRework.Info);
                    else
                        OutputHelper.WriteQualityLoss(lot, lot, eqp, QualityLossType.BOH_REWORK, null);

                    continue; // Rework 도중 재차 발생시키지 않음.
                }

                if (InputMart.Instance.ApplyStepYield)
                    ApplyStepYieldRate(lot, feqp);
                
                // Yield 반영 후 생존한 Lot에 대해 Rework 확률 적용 (EqpRework or StepRework)
                if (InputMart.Instance.ApplyStepRework)
                    ApplyStepReworkRate(lot, feqp);
            }

            static void ApplyStepYieldRate(FabSemiconLot lot, FabAoEquipment feqp)
            {
                var eqp = feqp == null ? null : feqp.Eqp;
                var eqpYield = feqp == null ? 1 : feqp.Eqp.EqpYieldRate;
                var stepYield = lot.CurrentFabStep.StepYieldRate;

                var successRate = Helper.GetValidRate(eqpYield * stepYield);
                var lossRate = 1 - successRate;

                if (lossRate == 0)
                    return;

#if true
                var fhb = lot as FabHandlingBatch;
                var lotList = fhb != null ? fhb.mergedContents.ToList() : new List<FabSemiconLot>() { lot };

                foreach (var content in lotList)
                {
                    bool loss = Helper.GetBernoulliTrialResult(lossRate);
                    if (loss)
                    {
                        if (fhb != null)
                        {
                            fhb.SplitLot(content, HandlingBatchSplitType.Scrap);
                        }

                        Tuple<double, double, double> rateTuple = new Tuple<double, double, double>(lossRate, eqpYield, stepYield);
                        OutputHelper.WriteQualityLoss(lot, content, eqp, QualityLossType.STEP_YIELD_SCRAP, rateTuple);

                        var targetDate = Helper.GetTargetDate(lot.CurrentFabPlan.EndTime, false);

                        if (InputMart.Instance.FabInOutInfo.TryGetValue(content.CurrentPartID, targetDate, out FabInOutInfo info) == false)
                        {
                            info = new FabInOutInfo();
                            info.TARGET_DATE = targetDate;
                            info.TARGET_WEEK = Helper.GetFormattedTargetWeek(targetDate);
                            info.TARGET_MONTH = Helper.GetFormattedTargetMonth(targetDate);
                            info.PART_ID = content.CurrentPartID;

                            InputMart.Instance.FabInOutInfo.Add(content.CurrentPartID, targetDate, info);
                        }
                        info.SCRAP_QTY += content.GetBOMContributionQty();
                    }
                }

                if (fhb != null && fhb.hasChange)
                    fhb.WriteWipLog();
#else
                var scrapWaferCount = EntityHelper.GetQualityLossWaferCount(lot, lossRate);
                if (scrapWaferCount == 0)
                    return;

                if (scrapWaferCount == lot.UnitQty)
                    lot.IsYieldScrapped = true;

                lot.UnitQty -= scrapWaferCount;

                Tuple<double, double, double> rateTuple = new Tuple<double, double, double>(lossRate, eqpYield, stepYield);
                OutputHelper.WriteQualityLoss(lot, scrapWaferCount, QualityLossType.STEP_YIELD_SCRAP, rateTuple);
                OutputHelper.WriteWipLog(LogType.INFO, "APPLY_YIELD", lot, AoFactory.Current.NowDT, "Yield Scrap (ScrapQty=" + scrapWaferCount + ")"); 
#endif
            }

            static void ApplyStepReworkRate(FabSemiconLot lot, FabAoEquipment feqp)
            {
                var eqp = feqp == null ? null : feqp.Eqp;
                if (lot.IsYieldScrapped)
                    return;

                // 한번 Rework Trigger 된 Step에서 재차 발생할 수는 있도록 함. (집계 결과가 주어진 확률에 수렴하도록)
                //if (lot.CurrentRework != null && lot.CurrentFabStep == lot.CurrentRework.ReworkTriggerStep)
                //    return;

                var fhb = lot as FabHandlingBatch;
                if (fhb != null && fhb.IsExtinct())
                    return;

                // EqpRework을 우선 적용하고, 해당하지 않을 경우는 StepRework을 적용.
                ReworkInfo reworkInfo = (lot.CurrentPlan as FabPlanInfo).EqpReworkInfo;
                if (reworkInfo == null)
                    reworkInfo = lot.CurrentFabStep.ReworkInfo;

                if (reworkInfo == null)
                    return;

                bool applyEqpRework = reworkInfo is EqpReworkInfo;

                var successRate = Helper.GetValidRate(1 - reworkInfo.ReworkRate);
                var lossRate = 1 - successRate;

                if (lossRate == 0)
                    return;

#if true
                var lotList = fhb != null ? fhb.mergedContents.ToList() : new List<FabSemiconLot>() { lot };

                foreach (var content in lotList)
                {
                    bool loss = Helper.GetBernoulliTrialResult(lossRate);
                    if (loss)
                    {
                        Tuple<double, double, double> rateTuple = new Tuple<double, double, double>(lossRate, 0, 0);
                        OutputHelper.WriteQualityLoss(lot, content, eqp, applyEqpRework ? QualityLossType.EQP_REWORK : QualityLossType.STEP_REWORK, rateTuple, reworkInfo);

                        FabSemiconStep reworkStartStep = null;
                        if (reworkInfo.ProcessingType == ReworkProcessingType.Route)
                        {
                            reworkStartStep = (reworkInfo as StepReworkInfo).ReworkRoute.FirstStep as FabSemiconStep;
                            content.FabWipInfo.ReturnStep = lot.CurrentFabStep;
                        }
                        else if (reworkInfo.ProcessingType == ReworkProcessingType.Step)
                        {
                            reworkStartStep = (reworkInfo as StepReworkInfo).ReworkStep;
                        }

                        if (reworkInfo.ProcessingType == ReworkProcessingType.Time || reworkStartStep == lot.CurrentFabStep)
                        {
                            // Route에서 동일 Step 연속진행을 불허하여, Dummy RouteStep 생성 필요.
                            var returnStep = lot.CurrentStep.GetDefaultNextStep() as FabSemiconStep;
                            reworkStartStep = BopHelper.GetReworkDummyRouteStep(lot, content, returnStep);
                        }

                        var currentRework = new ReworkActivation();
                        currentRework.Info = reworkInfo;
                        currentRework.ReworkStartStep = reworkStartStep;
                        currentRework.ReworkTriggerStep = lot.CurrentFabStep;
                        currentRework.QualityLossType = applyEqpRework ? QualityLossType.EQP_REWORK : QualityLossType.STEP_REWORK;

                        content.CurrentRework = currentRework;

                        if (fhb != null)
                        {
                            fhb.SplitLot(content, HandlingBatchSplitType.Rework);
                        }
                    }
                }

                if (fhb != null && fhb.hasChange)
                    fhb.WriteWipLog();
#else
                var reworkWaferCount = EntityHelper.GetQualityLossWaferCount(lot, lossRate);
                if (reworkWaferCount == 0)
                    return;

                // Loss가 발생한 것은 원본 Lot이 므로, Split 발생한 경우에도 LotID는 원본을 적도록 함.
                Tuple<double, double, double> rateTuple = new Tuple<double, double, double>(lossRate, 0, 0);
                OutputHelper.WriteQualityLoss(lot, reworkWaferCount, applyEqpRework ? QualityLossType.EQP_REWORK : QualityLossType.STEP_REWORK, rateTuple);

                FabSemiconStep reworkStartStep = null;
                if (reworkInfo.ProcessingType == ReworkProcessingType.Route)
                {
                    reworkStartStep = (reworkInfo as StepReworkInfo).ReworkRoute.FirstStep as FabSemiconStep;
                    lot.FabWipInfo.ReturnStep = lot.CurrentFabStep;
                }
                else if (reworkInfo.ProcessingType == ReworkProcessingType.Step)
                {
                    reworkStartStep = (reworkInfo as StepReworkInfo).ReworkStep;
                }

                if (reworkInfo.ProcessingType == ReworkProcessingType.Time || reworkStartStep == lot.CurrentFabStep)
                {
                    // Route에서 동일 Step 연속진행을 불허하여, Dummy RouteStep 생성 필요.
                    var returnStep = lot.CurrentStep.GetDefaultNextStep() as FabSemiconStep;
                    reworkStartStep = BopHelper.GetReworkDummyRouteStep(lot, returnStep);
                }

                FabSemiconLot reworkingLot = null;
                bool isSplited = false;
                if (reworkWaferCount == lot.UnitQty) // 전체 lot이 Rework 대상
                {
                    reworkingLot = lot;

                    OutputHelper.WriteWipLog(LogType.INFO, "APPLY_REWORK", reworkingLot, AoFactory.Current.NowDT, "Rework " + reworkInfo.ProcessingType.ToString(), reworkStartStep);
                }
                else if (reworkWaferCount > 0) // 일부 lot만 Rework 대상이어서 split 진행
                {
                    lot.UnitQty -= reworkWaferCount;

                    isSplited = true;
                    var splitLotId = lot.LotID + "_" + InputMart.Instance.ReworkSplitIndex++ + "R";
                    reworkingLot = CreateHelper.CreateInstancingLot(splitLotId, lot.FabProduct, reworkStartStep.Process as FabSemiconProcess
                        , reworkWaferCount, reworkStartStep, AoFactory.Current.NowDT);

                    EntityHelper.InitiateSemiconLot(reworkingLot);

                    OutputHelper.WriteWipLog(LogType.INFO, "APPLY_REWORK", reworkingLot, AoFactory.Current.NowDT, "Rework " + reworkInfo.ProcessingType.ToString() + " (Split)", reworkStartStep);
                    //OutputHelper.WriteWipLog(LogType.INFO, "APPLY_REWORK_PARENT", lot, AoFactory.Current.NowDT, "Split Remain"); // 로그 간소화
                }

                var currentRework = new ReworkActivation();
                currentRework.Info = reworkInfo;
                currentRework.ReworkStartStep = reworkStartStep;
                currentRework.ReworkTriggerStep = lot.CurrentFabStep;
                if (isSplited)
                    currentRework.IsStarted = true; // GetNextStep을 거치지 않고 ReworkStep에 도달하므로 여기서 Start 처리.

                reworkingLot.CurrentRework = currentRework; 
#endif
            }
        }

        public void ON_END_TASK_STACK(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            // APPLY_QUALITY_LOSS 이후에 호출해야
            // UpdateActiveStackOnEnd가 의도대로 동작함.

            var feqp = ao as FabAoEquipment;
            if (feqp == null)
                return;

            foreach (var entity in hb)
            {
                var lot = entity as FabSemiconLot;
                OutputHelper.WriteStackResult(lot);
                ArrangeHelper.UpdateActiveStackOnEnd(lot);
            }

#if true
            var fhb = hb as FabHandlingBatch;
            if (fhb != null)
            {
                // LastLayer에 대한 처리도 동기화 시키기 위해 호출.
                foreach (var content in fhb.mergedContents)
                {
                    ArrangeHelper.UpdateActiveStackOnEnd(content);
                }
            } 
#endif
        }

        public Step GET_NEXT_STEP_REWORK(ILot lot, LoadInfo loadInfo, Step step, DateTime now, ref bool handled, Step prevReturnValue)
        {
            var fLot = lot as FabSemiconLot;

            // Rework Route Type: Rework Route의 초기 Step으로 이동
            // Rework Step Type: 현재 Route의 지정된 Step으로 이동
            // Rework Time Type: DummyStep을 Return한 뒤, 이어서 Hold 처리 되고 동일 Step 한번 더 진행하게 됨.
            if (EntityHelper.NeedGetNextStepRework(fLot))
            {
                handled = true;
                fLot.CurrentRework.IsStarted = true;

                fLot.Route = fLot.CurrentRework.ReworkStartStep.Route;

                return fLot.CurrentRework.ReworkStartStep;
            }

            return prevReturnValue;
        }

        public void PRE_PEGGING0(IHandlingBatch hb, List<PeggingInfo> infos, ref bool handled)
        {
            // Forward Pegging된 시점.
            var lot = hb as FabSemiconLot;
            lot.ForwardPegInfoList = lot.CurrentFabPlan.PegInfoList;
        }

        public void COLLECT_CT(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            foreach (var entity in hb)
            {
                var lot = entity as FabSemiconLot;

                if (BopHelper.IsFabInOrFabOut(lot.CurrentStepID))
                    return;

                CollectDailyCT(lot, ao as FabAoEquipment);

                static void CollectDailyCT(FabSemiconLot lot, FabAoEquipment feqp)
                {
                    var plan = lot.CurrentFabPlan;

                    // PST 이후 도착한 lot에 대해서만 집계
                    if (lot.DispatchInTime >= ModelContext.Current.StartTime && plan.EndTime > ModelContext.Current.StartTime)
                    {
                        var eqp = feqp != null ? feqp.Eqp : null;
                        var periodicObj = StatisticHelper.GetOrAddPeriodicObject(lot, eqp);
                        if (periodicObj == null)
                            return;

                        var step = lot.CurrentFabStep;

                        // Arrange 없이 진행한 경우, 또는 SimulationStep은 아니지만 집계만 필요한 경우의 디폴트 값
                        double runHr = step.RunCT.TotalHours;
                        double waitHr = step.WaitCT.TotalHours;
                        var arr = plan.Arrange;
                        if (arr != null) // Full LotSize 로 환산한 뒤, MoveQty로 가중평균
                        {
                            #region CT Conversion
                            var tact = plan.ProcTime.TactTime.TotalHours / arr.Eqp.Utilization;
                            var flow = plan.ProcTime.FlowTime.TotalHours;

                            if (arr.Eqp.SimType == SimEqpType.Inline) // 설비 Type Converting이 발생한 경우를 포함하여 그대로 사용 가능.
                            {
                                runHr = (((InputMart.Instance.LotSize - 1) * tact) + flow);
                            }
                            else if (arr.Eqp.SimType == SimEqpType.BatchInline) // Convert 하지 않은 경우
                            {
                                runHr = flow;
                            }
                            else if (arr.Eqp.SimType == SimEqpType.LotBatch || arr.Eqp.SimType == SimEqpType.UnitBatch)
                            {
                                runHr = tact; // tact == flow 로 입력해야 됨. 라이브러리에서는 tact을 사용함.
                            }
                            else if (arr.Eqp.SimType == SimEqpType.ParallelChamber)
                            {
                                if (lot.UnitQty == InputMart.Instance.LotSize)
                                    runHr = (plan.EndTime - plan.StartTime).TotalHours;
                                else
                                    runHr = (tact * InputMart.Instance.LotSize) / arr.SubEqps.Count; // Full LotSize로 전환하기 위해 모든 SubEqp를 사용할 것으로 가정함.
                            }

                            waitHr = (plan.StartTime - lot.DispatchInTime).TotalHours; // WaitTAT는 LotSize 환산할 필요 없음. (TODO : lotMergeSize 와의 비율은 반영해야 할지도?) 
                            #endregion
                        }

                        // TODO : Transfer Time 반영 여부에 대해 논의 후 개선이 필요할 수 있음.
                        double ctHr = waitHr + runHr;

                        periodicObj.TotalTATWeightedSum += ctHr * lot.UnitQtyDouble;
                        periodicObj.WaitTATWeightedSum += waitHr * lot.UnitQtyDouble;
                        periodicObj.RunTATWeightedSum += runHr * lot.UnitQtyDouble;

                        periodicObj.MoveQtySum += lot.UnitQtyDouble;

                        periodicObj.WaitTATSum += waitHr;
                        periodicObj.LotCount++;
                    }
                }
            }
        }

        public void ON_RELEASE0(AoFactory factory, IHandlingBatch hb, ref bool handled)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.CurrentRework != null)
            {
                handled = true;
                return;
            }

            if (lot.CurrentStepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID)
                OutputHelper.WriteWipLog(LogType.INFO, "FAB_IN", hb as FabSemiconLot, AoFactory.Current.NowDT, "OnRelease");
        }

        public void WRITE_EQP_PLAN_HB_OUT(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            var fhb = hb as FabHandlingBatch;
            if (fhb == null)
                return;

            if (fhb.CurrentFabStep.IsSimulationStep == false)
                return;

            foreach (var lot in fhb.mergedContents)
            {
                OutputHelper.WriteEqpPlanHB(fhb, lot, ao as FabAoEquipment);
            }
        }
        public bool IS_DONE1(IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            if (prevReturnValue == true)
                return true;

            var lot = hb as FabSemiconLot;

            var fhb = lot as FabHandlingBatch;
            if (fhb != null)
            {
                if (fhb.CurrentStepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabOutStepID)
                {
                    // ForwardPeg위해, FabOutStep 도달 시 전부 Split 처리.
                    var mergedList = fhb.mergedContents.ToList();
                    mergedList.ForEach(x => fhb.SplitLot(x, HandlingBatchSplitType.FabOut));

                    FabHandlingBatch.ClearHandlingBatch(fhb);
                }
                else
                {
                    var attr = fhb.CurrentFabStep.PartStepDict.SafeGet(fhb.FabProduct.PartID);
                    if (attr != null && attr.CurrentArranges.Any(x => x.Eqp.SimType == SimEqpType.UnitBatch && x.Eqp.UnitBatchInfo.HasFinitePort))
                    {
                        // ## HandlingBatch 사용시, PortCount 제약이 있는 Arrange를 하나라도 가질 경우, HB를 미리 Split해서 Dispatching 참여하도록 처리했습니다.
                        // HB를 확산 없이 한 port로 몰아서 진행할 경우 실제 behavior가 과도하게 왜곡됨
                        // 후처리로 Split할 경우, 디스패칭 시간과 투입시간이 달라지는 등의 추가 고려사항이 발생하여, 유지보수 차원에서 단점이 더 많을 것으로 판단됨.

                        var mergedList = fhb.mergedContents.ToList();
                        mergedList.ForEach(x => fhb.SplitLot(x, HandlingBatchSplitType.UnitBatch));

                        FabHandlingBatch.ClearHandlingBatch(fhb);
                    }
                }
            }

            return lot.IsVanishing;
        }

        public void UPDATE_MIN_RUN_SETUP(IHandlingBatch hb, ActiveObject ao, DateTime now, ref bool handled)
        {
            var aeqp = ao as AoEquipment;
            var lot = hb.Sample as FabSemiconLot;

            try
            {
                if (aeqp != null)
                {
                    var arr = lot.CurrentArranges.SafeGet(aeqp.EqpID);
                    if (arr == null)
                        arr = (lot.CurrentPlan as FabPlanInfo).Arrange;
                    if (arr == null || arr.SetupName.IsNullOrEmpty())
                        return;

                    var eqp = aeqp.Target as FabSemiconEqp;

                    if (eqp.MinRunsAfterSwitch.ContainsKey(arr.SetupName) == false)
                    {
                        eqp.MinRunsAfterSwitch.Clear();
                        eqp.MinRunsAfterSwitch.Add(arr.SetupName, 0);
                    }

                    eqp.MinRunsAfterSwitch[arr.SetupName]++;
                }
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, aeqp.EqpID, lot.LotID);
            }
        }

        public Step GET_NEXT_STEP_BOM(ILot lot, LoadInfo loadInfo, Step step, DateTime now, ref bool handled, Step prevReturnValue)
        {
            var fLot = lot as FabSemiconLot;

            // TO_PART의 Route로 이동해서 kitting이 맞을 때 까지 대기.
            if (fLot.CurrentBOM != null)
            {
                handled = true;

                fLot.Route = fLot.CurrentBOM.ToRoute;
                fLot.IsWaitForKitting = true;

                return fLot.CurrentBOM.MergeStep;
            }

            return prevReturnValue;
        }

        public void ON_RELEASE_TRANSPORT(AoFactory factory, IHandlingBatch hb, ref bool handled)
        {
            if (TransportSystem.Apply)
            {
                var bay = TransportSystem.GetInputStockerBay();
                if (bay == null)
                    throw new InvalidDataException("Unable To Locate Input Batch: Input Stocker does not exists");

                var buffer = bay.GetEmptyStockerBuffer();
                if (buffer == null)
                    buffer = TransportSystem.GetEmptyBuffer(bay);
                if (buffer == null)
                    buffer = TransportSystem.GetEmptyBuffer();

                if (buffer != null)
                    buffer.Attach(hb);
                else

                    throw new InvalidDataException("Unable To Locate Input Batch: Input Stocker does not exists");
            }
        }
    }
}