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
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class BatchingHelper
    {
        public static void AddUpstreamLots(FabSemiconLot lot, IQtLoop loop, bool isActive = false)
        {
            var targetStep = lot.Process.FindStep(loop.EndStepID);

            if (lot.CurrentStepID == loop.EndStepID)
                return;

            if (lot.ReservationInfos.SafeGet(loop.EndStepID) != null) // TargetStep이 같은 복수개의 OrderingLoop 존재
                return;

            var arranges = EntityHelper.GetTargetStepArranges(lot, loop.EndStepID);
            if (arranges.IsNullOrEmpty())
                return;

            foreach (var arr in arranges)
            {
                var aeqp = arr.Eqp.SimObject;
                if (aeqp.NeedUpstreamBatching == false)
                    continue;

                if (aeqp.UpstreamLots == null)
                    aeqp.UpstreamLots = new List<LotETA>();

                if (aeqp.UpstreamLots.Any(x => x.LotID == lot.LotID && x.TargetStep == targetStep))
                    continue;

                var eta = EntityHelper.CreateLotETA(lot, targetStep, aeqp);
                eta.Loop = loop as FabQtLoop;

                if (isActive)
                    eta.IsOrderingStepStarted = true;
                else if (lot.CurrentStepID == loop.StartStepID && lot.CurrentState == EntityState.RUN)
                    eta.IsOrderingStepStarted = true;

                aeqp.UpstreamLots.Add(eta);

                lot.EtaDict.Add(targetStep, aeqp);

                if (aeqp.ReservedBatch == null && aeqp.NowDT != ModelContext.Current.StartTime)
                {
                    BatchingContext ctx = new BatchingContext();
                    ctx.EventType = BatchingEventType.UpstreamLotArrival.ToString();
                    BatchingManager.BuildAndSelect(aeqp, ctx);

                    if (lot.ReservationInfos.SafeGet(targetStep.StepID) != null)
                        break;
                }
            }
        }

        public static void RemoveUpstreamLots(FabSemiconLot lot, SemiconStep targetStep)
        {
            if (lot.EtaDict.SafeGet(targetStep).IsNullOrEmpty())
                return;

            foreach (var eqp in lot.EtaDict.SafeGet(targetStep))
            {
                eqp.UpstreamLots.RemoveAll(x => x.LotID == lot.LotID);
            }

            lot.EtaDict.Remove(targetStep);
        }

        public static bool PassLoadStartTimeFilter(FabSemiconLot lot, FabQtLoop qtLoop, string stepID, AoEquipment aeqp)
        {
            // 기존에 사용되던 semiconStep을 대신해서 stepID의 string값으로 사용 
            var info = lot.ReservationInfos.SafeGet(stepID);
#if false
            var batch = info.Batch;

            if (batch.Contents.Any(x => (x as FabLotETA).IsOrderingStepStarted))
                return true;
#endif
            var feqp = aeqp as FabAoEquipment;

            AoProcess proc = feqp.ProcFirst<AoProcess>();
            var unloadingTime = proc.GetUnloadingTime(lot);

            var expectedLoadingTime = Helper.Max((DateTime)info.Eqp.GetNextInTime(), info.BatchETA);

            if (unloadingTime + qtLoop.LimitTime > expectedLoadingTime)
                return true;

            return false;
        }

        internal static FabBatchSpec GetDefaultBatchSpec(EqpArrange arr)
        {
            var spec = new FabBatchSpec();
            spec.MinLot = 1;
            spec.MaxLot = int.MaxValue; // IsFullBatch() 가 동작하려면 1 이상의 값이 필요

            spec.MinWafer = (int)Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMinBatchSizeLotBatch;
            spec.MaxWafer = (int)Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMaxBatchSizeLotBatch;

            if (arr.Eqp.SimType == SimEqpType.BatchInline)
            {
                spec.MaxLot = 2;

                spec.MinWafer = (int)Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMinBatchSizeBatchInline;
                spec.MaxWafer = (int)Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMaxBatchSizeBatchInline;
            }

            return spec;
        }

        internal static void ReserveBatch(AoEquipment aeqp, LotBatch selectBatch)
        {
            if (selectBatch == null)
                return;

            var feqp = aeqp as FabAoEquipment;

            feqp.ReservedBatch = selectBatch;

            //var batchPort = MaterialControlSystem.GetPorts(feqp, LocationState.VACANT).First() as MultiReservePort;

            foreach (SemiconLot entity in selectBatch.Contents)
            {
                var eta = entity as FabLotETA;
                var lot = eta != null ? eta.Lot as FabSemiconLot : entity as FabSemiconLot;
                var targetStep = eta != null ? eta.TargetStep : entity.CurrentStep;

                RemoveUpstreamLots(lot, targetStep);

                var info = new ReservationInfo();
                info.TargetStep = targetStep;
                info.Batch = selectBatch;
                info.Eqp = feqp;
                info.BatchETA = Helper.Max(info.BatchETA, eta != null ? eta.ArrivalTime : aeqp.NowDT);

                if (lot.ReservationInfos.ContainsKey(targetStep.StepID))
                    continue; // unexpected

                lot.ReservationInfos.Add(targetStep.StepID, info);

                if (TransportSystem.Apply)
                {
                    TransportSystem.DoneJobPreparation(feqp, lot);
                }
            }

            TransportSystem.TryFillEmptyPort(feqp);
        }
    }
}