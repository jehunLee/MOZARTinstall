using Mozart.SeePlan.Semicon.Simulation;
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
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan.DataModel;
using static Mozart.SeePlan.Simulation.WipTags;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class BatchingControl
    {
        public bool IS_NEED_BATCH_BUILDING0(Mozart.SeePlan.Simulation.AoEquipment aeqp, Mozart.SeePlan.Semicon.Simulation.BatchingContext ctx, ref bool handled, bool prevReturnValue)
        {
            if (TransportSystem.Apply)
                return TransportSystem.NeedJobPreparation(aeqp as FabAoEquipment);

            return true;
        }

        public List<Mozart.SeePlan.Semicon.DataModel.LotETA> GET_WIPS0(AoEquipment aeqp, BatchingContext ctx, ref bool handled, List<Mozart.SeePlan.Semicon.DataModel.LotETA> prevReturnValue)
        {
            var queue = aeqp.DispatchingAgent.GetDestination(aeqp.EqpID).Queue;
            var feqp = aeqp as FabAoEquipment;

            List<LotETA> list = new List<LotETA>();

            if (TransportSystem.Apply)
            {
                foreach (var hb in feqp.JobPrepCandidates)
                {
                    var lot = hb as FabSemiconLot;
                    FabLotETA eta = EntityHelper.CreateLotETA(lot, lot.CurrentStep, aeqp);
                    if (eta == null)
                        continue;

                    list.Add(eta);
                }
            }
            else
            {
                foreach (FabSemiconLot lot in queue)
                {
                    if (lot.ReservationInfos.SafeGet(lot.CurrentStepID) != null)
                        continue;

                    FabLotETA eta = EntityHelper.CreateLotETA(lot, lot.CurrentStep, aeqp);
                    if (eta == null)
                        continue;

                    // 초기화 시점에 이미 Ordering Loop안에 들어왔는데 AtStep 도달까지 예약되지 못한 상황
                    var activations = lot.MaxQtActivations.SafeGet(lot.CurrentStepID);
                    if (activations.IsNullOrEmpty() == false)
                    {
                        var OrderingQtActivation = lot.MaxQtActivations.SafeGet(lot.CurrentStepID).FirstOrDefault(x => x.Loop.ControlType == QtControlType.Ordering);
                        if (OrderingQtActivation != null)
                        {
                            eta.Loop = OrderingQtActivation.Loop as FabQtLoop;
                            eta.IsOrderingStepStarted = true;
                        }
                    }
                    eta.IsAtStepLoadable = true;

                    list.Add(eta);
                }

                if (feqp.UpstreamLots.IsNullOrEmpty() == false)
                {
                    feqp.UpstreamLots.ForEach(x => (x as FabLotETA).FilterReason = null);

                    list.AddRange(feqp.UpstreamLots);
                }
            }

            return list;
        }

        public string GET_BATCHING_KEY0(AoEquipment aeqp, LotETA lot, BatchingContext ctx, ref bool handled, string prevReturnValue)
        {
            var eta = lot as FabLotETA;
            var batchingKey = eta.Arrange.RecipeID;
            eta.BatchingKey = batchingKey;

            return batchingKey;
        }

        public List<LotETA> EVALUATE0(AoEquipment aeqp, List<LotETA> lots, BatchingContext ctx, ref bool handled, List<LotETA> prevReturnValue)
        {
            if (aeqp.Preset == null)
                return lots;

            var dispatcherType = (aeqp.Preset as FabWeightPreset).DispatcherType;
            if (dispatcherType == DispatcherType.Fifo)
                return lots;

            WeightEvaluator eval = new WeightEvaluator(aeqp, aeqp.Preset);

            if (dispatcherType == DispatcherType.WeightSum)
                eval.Comparer = new WeightSumComparer();
            else if (dispatcherType == DispatcherType.WeightSorted)
                eval.Comparer = new WeightPriorityComparer(eval.FactorList);

            var dispatchContext = new DispatchContext();

            var lotAgeKey = "LOT_AGE";
            var waitTimeKey = "STEP_WAIT";
            dispatchContext.Set(lotAgeKey, ctx.Attributes.SafeGet(lotAgeKey));
            dispatchContext.Set(waitTimeKey, ctx.Attributes.SafeGet(waitTimeKey));

            var n = Helper.GetConfig(ArgsGroup.Logic_Dispatching).evaluationLotCount;
            IList<IHandlingBatch> result;
            if (lots.Count > n && n > 0)
            {
                // Batch의 경우 특정 Priority만 Evaluate 시키지는 않음.
                // TODO? : Sorting을 적용하려면 Upstream이 섞여있으므로 단순히 arrivalTime이 아니라 다른 로직이 필요.

                result = eval.Evaluate(lots.Take(n).ToList<IHandlingBatch>(), dispatchContext);

                var rest = lots.Skip(n);
                rest.ForEach(x => (x as FabLotETA).WeightInfo.Reset());
                //result.AddRange(rest);
            }
            else
            {
                result = eval.Evaluate(lots.ToList<IHandlingBatch>(), dispatchContext);
            }

            List<LotETA> sorted = new List<LotETA>();
            result.ForEach(x => x.Apply((lot, _) => sorted.Add(lot as LotETA)));

            return sorted;
        }

        public IBatchSpec GET_BATCH_SPEC0(AoEquipment aeqp, LotBatch batch, BatchingContext ctx, ref bool handled, IBatchSpec prevReturnValue)
        {
            var sample = batch.BatchingData.RemainCandidates.First() as FabLotETA;

            var spec = sample.Arrange.BatchSpec;
            if (spec == null)
            {
                sample.Arrange.BatchSpec = BatchingHelper.GetDefaultBatchSpec(sample.Arrange);
            }

            return sample.Arrange.BatchSpec;
        }

        public bool CAN_ADD_LOT0(AoEquipment eqp, LotBatch batch, IHandlingBatch hb, BatchingContext ctx, ref bool handled, bool prevReturnValue)
        {
            var eta = hb as FabLotETA;

            if (eta.Lot.CurrentState == EntityState.HOLD)
            {
                eta.FilterReason = "On Hold";

                handled = true;
                return false;
            }

            // IsFullBatch() 는 MaxLot 및 MaxWafer 모두 만족했을 경우만 True 리턴함
            // MaxLot 이나 MaxWafer 둘중 하나만 만족해도 더 이상 Add 하면 안되므로 추가 구현
            if (batch.Count == batch.Spec.MaxLot)
            {
                eta.FilterReason = "MaxLotCount";

                handled = true;
                return false;
            }

            if (batch.UnitQty + hb.UnitQty > batch.Spec.MaxWafer)
            {
                eta.FilterReason = "MaxProdCount";

                handled = true;
                return false;
            }

            if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 1)
                handled = true;

            return true;
        }

        public bool IS_BATCH_VALID0(LotBatch batch, BatchingContext ctx, ref bool handled, bool prevReturnValue)
        {
            var isValid = batch.UnitQty >= batch.Spec.MinWafer && batch.Count >= batch.Spec.MinLot;
            if (isValid == false)
                Helper.ClearBatchingDataMemory(batch);

            return isValid;
        }

        public LotBatch SELECT_BATCH0(AoEquipment aeqp, IEnumerable<LotBatch> batchs, BatchingContext ctx, ref bool handled, LotBatch prevReturnValue)
        {
            if (batchs.IsNullOrEmpty())
                return null;

            var eqp = aeqp.Target as FabSemiconEqp;
            var programId = "BATCH_SELECTION";
            var bsPreset = eqp.PresetDict.SafeGet(programId);
            if (bsPreset == null)
            {
                // 개편된 PROGRAM_ID 미반영 시나리오를 위한 보완조치.
                var old = eqp.PresetDict.Keys.Where(x => x.Contains(programId)).FirstOrDefault();
                bsPreset = old != null ? eqp.PresetDict.SafeGet(old) : null;
            }

            LotBatch selectBatch = null;

            var dispatcherType = bsPreset != null ? bsPreset.DispatcherType : DispatcherType.Fifo;
            ctx.Attributes.Add("bsPreset", bsPreset);
            if (dispatcherType == DispatcherType.Fifo)
            {
                selectBatch = batchs.First();
            }
            else
            {
                WeightEvaluator eval = new WeightEvaluator(aeqp, bsPreset);

                if (dispatcherType == DispatcherType.WeightSum)
                    eval.Comparer = new WeightSumComparer();
                else if (dispatcherType == DispatcherType.WeightSorted)
                    eval.Comparer = new WeightPriorityComparer(eval.FactorList);

                var dispatchContext = new DispatchContext();

                var lotAgeKey = "LOT_AGE";
                var waitTimeKey = "STEP_WAIT";
                dispatchContext.Set(lotAgeKey, ctx.Attributes.SafeGet(lotAgeKey));
                dispatchContext.Set(waitTimeKey, ctx.Attributes.SafeGet(waitTimeKey));

                var list = eval.Evaluate(batchs.ToList<IHandlingBatch>(), dispatchContext);
                selectBatch = list.First() as LotBatch;
            }

            if (eqp.SimType == SimEqpType.LotBatch)
            {
                if (aeqp.IsProcessing || ctx.EventType == BatchingEventType.LoadingStart.ToString())
                {
                    var test = ctx.EventType;

                    // 당장 로딩가능하지 않으면 AtStepOnly Batch는 선택하지 않도록
                    if (selectBatch.All(x => (x as FabLotETA).IsAtStepLoadable))
                        return null;
                }
            }

            return selectBatch;
        }

        public void RESERVE_BATCH(AoEquipment aeqp, LotBatch selectBatch, BatchingContext ctx, ref bool handled)
        {
            BatchingHelper.ReserveBatch(aeqp, selectBatch);
        }

        public void WRITE_SELECTION_LOG(AoEquipment aeqp, LotBatch selectBatch, BatchingContext ctx, ref bool handled)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("BATCH_SELECT_LOG"))
            {
                ctx.CandidateBatches.ForEach(Helper.ClearBatchingDataMemory);
                return;
            }

            var feqp = aeqp as FabAoEquipment;
            var eqp = aeqp.Target as FabSemiconEqp;
            //var programId = eqp.SimType == Mozart.SeePlan.DataModel.SimEqpType.LotBatch ? "DIFF_BATCH_SELECTION" : "WET_BATCH_SELECTION";
            //var bsPreset = (aeqp.Target as FabSemiconEqp).PresetDict.SafeGet(programId);

            var bsPreset = ctx.Attributes.SafeGet("bsPreset") as WeightPreset;

            foreach (var batch in ctx.CandidateBatches)
            {
                BATCH_SELECT_LOG log = new BATCH_SELECT_LOG();
                log.SCENARIO_ID = InputMart.Instance.ScenarioID;
                log.VERSION_NO = ModelContext.Current.VersionNo;
                log.EQP_ID = aeqp.EqpID;
                log.BATCHING_KEY = batch.BatchingData.BatchingKey;
                log.EVENT_TIME = aeqp.NowDT;
                log.LOT_CNT = batch.Contents.Count;
                log.WAFER_QTY = batch.Contents.Sum(x => (x as FabLotETA).UnitQty);
                log.LOTS = batch.Contents.Select(x => (x as FabLotETA).Lot.LotID).ToList().Join(",");

                log.SELECT_YN = batch == selectBatch ? "Y" : "N";

                log.EVALUATE_LOG = WeightHelper.GetBatchingEvaluationLog(batch.Sample as LotETA, bsPreset, out double totalValue);
                log.WEIGHT_VALUE = totalValue;

                log.BB_TRY_SEQUENCE = feqp.BBTrySeq;
                log.MIN_PROD_COUNT = batch.Spec.MinWafer;
                log.MAX_PROD_COUNT = batch.Spec.MaxWafer;
                log.DISPATCH_EVENT = ctx.EventType;// ctx.Attributes.SafeGet("DISPATCH_EVENT").ToString();
                log.PRESET_ID = bsPreset == null ? string.Empty : bsPreset.Name;

                OutputMart.Instance.BATCH_SELECT_LOG.Add(log);

                Helper.ClearBatchingDataMemory(batch);
            }
        }

        public List<LotETA> UPDATE_CONTEXT(AoEquipment aeqp, BatchingContext ctx, ref bool handled, List<LotETA> prevReturnValue)
        {
            if (aeqp.Preset == null)
                return prevReturnValue;

            var lotAgeFactor = aeqp.Preset.FactorList.Where(x => x.Name == "LOT_AGE_FACTOR").FirstOrDefault();
            var stepWaitFactor = aeqp.Preset.FactorList.Where(x => x.Name == "STEP_WAIT_FACTOR").FirstOrDefault();

            if (lotAgeFactor == null && stepWaitFactor == null)
                return prevReturnValue;

            var lotWeightFactor = lotAgeFactor as FabWeightFactor;
            var stepWeightFactor = stepWaitFactor as FabWeightFactor;

            var feqp = aeqp as FabAoEquipment;
            double lotAgeDenominatorHrs = 0;
            double stepWaitDenominatorHrs = 0;
            DateTime now = aeqp.NowDT;

            if (lotWeightFactor != null)
            {
                // 기존 코드에서 Helper.GetDurationHoursWithChar를 쓰는 부분을 제거하고 List를 호출하도록 변경
                var criteriaList = lotWeightFactor.criteriaList as List<double>;
                if (criteriaList.IsNullOrEmpty() == false)
                    lotAgeDenominatorHrs = criteriaList.FirstOrDefault();
            }

            if (stepWeightFactor != null)
            {
                var criteriaList = stepWeightFactor.criteriaList as List<double>;
                if (criteriaList.IsNullOrEmpty() == false)
                    stepWaitDenominatorHrs = criteriaList.FirstOrDefault();
            }

            bool isNeedMaxLotAgeCalc = lotAgeDenominatorHrs <= 0;
            bool isNeedMaxStepWaitCalc = stepWaitDenominatorHrs <= 0;

            // Factor Max 계산이 필요 없더라도, Upstream Lot에 대해 UpdateETA 호출하기 위해 반복문 진입.
            foreach (LotETA eta in prevReturnValue)
            {
                EntityHelper.UpdateETA(eta as FabLotETA);

                FabSemiconLot lot = eta.Lot as FabSemiconLot;

                if (isNeedMaxLotAgeCalc)
                {
                    var thisLotAgeHrs = WeightHelper.GetLotAgeHours(lot);
                    lotAgeDenominatorHrs = Math.Max(thisLotAgeHrs, lotAgeDenominatorHrs);
                }

                // Upstream 에서의 StepWait도 같이 비교하도록 변경하였음.
                if (isNeedMaxStepWaitCalc)
                {
                    var thisLotStepWaitHrs = WeightHelper.GetStepWaitHours(lot, feqp);
                    stepWaitDenominatorHrs = Math.Max(thisLotStepWaitHrs, stepWaitDenominatorHrs);
                }
            }

            ctx.Attributes.Add("LOT_AGE", lotAgeDenominatorHrs);
            ctx.Attributes.Add("STEP_WAIT", stepWaitDenominatorHrs);

            return prevReturnValue;
#if false
            DateTime minStartTime = DateTime.MaxValue;
            DateTime now = aeqp.NowDT;

            Dictionary<FabSemiconLot, double> waitTimeDict = new Dictionary<FabSemiconLot, double>();
            FabAoEquipment feqp = aeqp as FabAoEquipment;
            foreach (LotETA eta in prevReturnValue)
            {
                FabSemiconLot lot = eta.Lot as FabSemiconLot;

                minStartTime = Helper.Min(minStartTime, lot.ReleaseTime);

                EntityHelper.UpdateETA(eta as FabLotETA);

                if ((eta as FabLotETA).IsAtStepLoadable == false)
                    continue;

                var arrivalTime = lot.DispatchInTime;
                if (lot.CurrentFabPlan.ArrivalTimeDict != null)
                {
                    if (lot.CurrentFabPlan.ArrivalTimeDict.ContainsKey(feqp.Eqp.BayID))
                        arrivalTime = lot.CurrentFabPlan.ArrivalTimeDict.SafeGet(feqp.Eqp.BayID);
                }

                var waitTimeHrs = (now - arrivalTime).TotalHours;
                waitTimeDict.Add(lot, waitTimeHrs);
            }

            TimeSpan maxAge = now - minStartTime;

            double i = 0;
            var waitTimeOrdered = waitTimeDict.OrderBy(x => x.Value).ToDictionary(x => x.Key, x => i++);

            ctx.Attributes.Add("LOT_AGE", maxAge.TotalHours);
            ctx.Attributes.Add("STEP_WAIT", waitTimeOrdered);

            return prevReturnValue; 
#endif
        }

        public void WRITE_BUILDING_LOG(AoEquipment aeqp, LotBatch batch, BatchingContext ctx, ref bool handled)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("BATCH_BUILD_LOG"))
                return;

            var feqp = aeqp as FabAoEquipment;

            foreach (FabLotETA eta in ctx.CandidateLots)
            {
                if (eta.BatchingKey != batch.BatchingData.BatchingKey)
                    continue;

                BATCH_BUILD_LOG log = new BATCH_BUILD_LOG();

                var lot = eta.Lot as FabSemiconLot;

                log.SCENARIO_ID = InputMart.Instance.ScenarioID;
                log.VERSION_NO = ModelContext.Current.VersionNo;
                log.EQP_ID = aeqp.EqpID;
                log.BATCHING_KEY = batch.BatchingData.BatchingKey;

                log.PART_ID = lot.FabProduct.PartID;
                log.CURRENT_STEP_ID = lot.CurrentStepID;
                log.BATCH_STEP_ID = eta.TargetStep.StepID;
                log.UPSTREAM_YN = (eta as FabLotETA).OnUpstream ? "Y" : "N";

                log.EVENT_TIME = aeqp.NowDT;
                log.WAFER_QTY = eta.UnitQty;
                log.LOT_ID = eta.LotID;

                log.SELECT_YN = batch.Contents.Contains(eta) ? "Y" : "N";

                log.BB_TRY_SEQUENCE = feqp.BBTrySeq;
                log.MIN_PROD_COUNT = batch.Spec.MinWafer;
                log.MAX_PROD_COUNT = batch.Spec.MaxWafer;
                log.LOT_SEQ = batch.Contents.IndexOf(eta) + 1;
                log.FILTERING_REASON = eta.FilterReason;
                log.ETA = eta.ArrivalTime;
                log.DISPATCH_EVENT = ctx.EventType;// ctx.Attributes.SafeGet("DISPATCH_EVENT").ToString();
                log.PRESET_ID = aeqp.Preset != null ? aeqp.Preset.Name : "FIFO";

                var evalLog = WeightHelper.GetBatchingEvaluationLog(eta, aeqp.Preset, out double totalValue);

                if (double.IsNegativeInfinity(totalValue) == false)
                {
                    log.EVALUATE_LOG = evalLog;
                    log.WEIGHT_VALUE = totalValue;
                }

                OutputMart.Instance.BATCH_BUILD_LOG.Add(log);
            }
        }

        public void ON_BEGIN_BATCH_BUILDING0(AoEquipment aeqp, BatchingContext ctx, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;

            if (feqp.NowDT > feqp.LastBBTryTime)
                feqp.BBTrySeq = 1;
        }

        public void ON_END_BATCH_SELECTION0(AoEquipment aeqp, LotBatch selectBatch, BatchingContext ctx, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;

            feqp.LastBBTryTime = feqp.NowDT;
            feqp.BBTrySeq++;
        }

        public bool CAN_ADD_LOT_CROSS_FAB(AoEquipment eqp, LotBatch batch, IHandlingBatch hb, BatchingContext ctx, ref bool handled, bool prevReturnValue)
        {
            return ResourceHelper.CanLoadableCrossFab(eqp, hb, ref handled);
        }
    }
}