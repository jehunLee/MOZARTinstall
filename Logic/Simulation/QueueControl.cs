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

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class QueueControl
    {
        public bool IS_BUCKET_PROCESSING0(Mozart.SeePlan.Simulation.DispatchingAgent da, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            var lot = hb as FabSemiconLot;

            if (lot.CurrentFabStep.IsSimulationStep)
                return false;

            return true;
        }

        public bool IS_HOLD0(DispatchingAgent da, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.CurrentState == EntityState.HOLD)
            {
                OutputHelper.WriteWipLog(LogType.INFO, "HOLD", lot, da.NowDT, lot.FabWipInfo.HoldCode);

                handled = true;
                return true;
            }
            else if (lot.CurrentRework != null && lot.CurrentRework.Info.ProcessingType == ReworkProcessingType.Time && lot.CurrentRework.IsHoldTriggered == false)
            {
                lot.CurrentRework.IsHoldTriggered = true;

                OutputHelper.WriteWipLog(LogType.INFO, "HOLD", lot, da.NowDT, "Rework Time");

                handled = true;
                return true;
            }
            else if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 1)
                handled = true;

            return false;
        }

        public Time GET_HOLD_TIME0(DispatchingAgent da, IHandlingBatch hb, ref bool handled, Time prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.CurrentRework != null && lot.CurrentRework.Info.ProcessingType == ReworkProcessingType.Time)
                return lot.CurrentRework.Info.ReworkHoldTime;
            else if (lot.QtHoldTime > Time.Zero)
                return lot.QtHoldTime;

            if (lot.FabWipInfo.HoldTimePdfConfig != null)
            {
                Time holdTime = Time.FromHours(Helper.GetDistributionRandomNumber(lot.FabWipInfo.HoldTimePdfConfig));
                if (holdTime > Time.Zero)
                    return holdTime;
            }

            return lot.FabWipInfo.HoldTime;
        }

        public void ON_HOLD_EXIT0(DispatchingAgent dispatchingAgent, IHandlingBatch hb, ref bool handled)
        {
            var lot = hb as FabSemiconLot;
            hb.CurrentState = EntityState.WAIT;
            lot.QtHoldTime = Time.Zero;

            lot.LastFilterReason = string.Empty;
            OutputHelper.WriteWipLog(LogType.INFO, "HOLD", lot, AoFactory.Current.NowDT, "Hold Exit");
        }

        public void ON_DISPATCH_IN0(DispatchingAgent da, IHandlingBatch hb, ref bool handled)
        {
            var lot = hb as FabSemiconLot;

            lot.CurrentFabPlan.ArrivalTimeStr = lot.DispatchInTime.ToString("yyyy-MM-dd HH:mm:ss");

            BatchingHelper.RemoveUpstreamLots(lot, lot.CurrentStep);

            if (Helper.GetConfig(ArgsGroup.Lot_Default).forceLotPriority == "Y")
            {
                // Non-Batch 설비에서만 force가 동작
                // Batch 설비에서는 LotPriorityFactor에 의해 동작하도록 유도해야함.

                ArrangeHelper.SetLotEvaluatePriority(lot, lot.CurrentStepID, "-", lot.FabWipInfo.LotPriorityValue);
            }
        }

        public bool INTERCEPT_IN0(DispatchingAgent da, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            var lot = hb as FabSemiconLot;

            // RemoveAndReEnter Case (deliveryTime)
            if (lot.CurrentFabPlan.IsIntercepted)
            {
                handled = true;
                return true;
            }

            var mergeInfo = lot.FabWipInfo.MergeDict.SafeGet(lot.CurrentStepID);
            if (mergeInfo == null)
                return false;

            handled = true;

            lot.CurrentFabPlan.IsIntercepted = true;
            List<ISimEntity> mergedList = da.Factory.Merge(hb);
            if (mergedList != null)
            {
                foreach (ISimEntity entity in mergedList)
                {
                    FabSemiconLot mergedlot = EntityHelper.GetLot(entity);

                    OutputHelper.WriteWipLog(LogType.INFO, "MERGE", mergedlot, da.NowDT, "Merged");
                    mergedlot.CurrentFabPlan.IsIntercepted = false;

                    // check == true 이면, EnterEntityMore() 함수를 타서
                    // OnDispatchIn 부터 다시 불리게 됨.
                    da.ReEnter(entity, true);
                }
            }
            else
            {
                var reason = lot.FabWipInfo == mergeInfo.Parent ? "Wait for Childs" : "Wait for Parent";
                OutputHelper.WriteWipLog(LogType.INFO, "MERGE", lot, AoFactory.Current.NowDT, reason);
            }

            return true;
        }

        public void ON_NOT_FOUND_DESTINATION0(DispatchingAgent da, IHandlingBatch hb, int destCount, ref bool handled)
        {
            var lot = hb as FabSemiconLot;
            if (BopHelper.IsFabInOrFabOut(lot.CurrentStepID))
            {
                // (FABIN / FABOUT step 은 loadingRule과 상관없이 설비가 있으면 loading 하고 없으면 TAT 적용)
                // ApplyStepLevel에는 영향을 받으므로 SimulationStep에는 포함되도록 세팅해야 됨.

                da.Factory.AddToBucketer(hb); 
                return;
            }

            var loadingRule = Helper.GetConfig(ArgsGroup.Bop_Step).loadingRule;
            if (loadingRule == 1) // Arrange가 없으면 진행 불가 (FABIN/FABOUT step 제외)
            {
                lot.IsNoArrangeWait = true;

                OutputHelper.WriteErrorLog(LogType.WARNING, "NoArrange", lot);

                return;
            }
            else if (loadingRule == 2) // Arrange가 없으면 Step TAT 적용 - Step Capa내에서
            {
                if (ResourceHelper.DoStepCapaBucketing(lot))
                {
                    da.Factory.AddToBucketer(hb);
                }
            }
            else if (loadingRule == 3) // Arrange가 없으면 Step TAT 적용 - Step Capa 고려 X
            {
                da.Factory.AddToBucketer(hb);
            }
        }

        public void APPLY_BACKUP_ARRANGE(DispatchingAgent da, IHandlingBatch hb, ref bool handled)
        {
            var lot = hb.Sample as FabSemiconLot;

            var attr = lot.CurrentAttribute;
            if (attr == null || attr.BackupArranges.IsNullOrEmpty())
                return;

            var aeqp = attr.CurrentArranges.First().Eqp.SimObject; // Arrange 하나인 경우만 대상이므로

            // S Step에서 BackupEqp로 진행했으면, Y Step에서 원본 Arrange가 아니라 BackupArrange의 가용여부를 체크하도록 처리
            // 이 경우, StackEqp가 Down 상태이면 Backup의 Backup으로 진행하는 상황이 전개됨.
            var activeStack = ArrangeHelper.GetActiveStack(lot, attr);
            if (activeStack != null && activeStack.StackStepInfo.StackType == StackType.Y && activeStack.StackEqp != null && activeStack.StackEqp.SimObject != aeqp)
            {
                aeqp = activeStack.StackEqp.SimObject;
            }

            if (aeqp.Loader.IsBlocked())
            {
                var pmSchedule = aeqp.DownManager.Tag as Mozart.SeePlan.DataModel.PMSchedule;
                if (pmSchedule == null)
                    return;

                EqpDownTag tag = ResourceHelper.GetEqpDownTag(aeqp.Target as FabSemiconEqp, pmSchedule.StartTime);
                bool isBM = tag != null && tag.DownType == EqpDownType.BM;

                ArrangeHelper.HandleBackupArrange(aeqp, pmSchedule, true, lot, "OnDispatchIn", isBM);
            }
        }

        public bool INTERCEPT_IN_BOM(DispatchingAgent da, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.IsWaitForKitting == false)
                return false;

            handled = true;
            lot.CurrentFabPlan.IsIntercepted = true;

            List<ISimEntity> mergedList = da.Factory.Merge(hb);
            if (mergedList != null)
            {
                foreach (ISimEntity entity in mergedList)
                {
                    FabSemiconLot mergedlot = EntityHelper.GetLot(entity);
                    mergedlot.CurrentFabPlan.IsIntercepted = false;
                    mergedlot.DispatchInTime = da.NowDT;

                    // 새로 생성되었으므로 WipManager에는 수동으로 담아야 함.
                    AoFactory.Current.WipManager.In(mergedlot);

                    var reason = "BOM Level = " + lot.CurrentBOM.BOMLevel; // child가 가지고 있던 BomInfo를 참조
                    OutputHelper.WriteWipLog(LogType.INFO, "BOM_PARENT", mergedlot, da.NowDT, reason);

                    // check == true 이면, EnterEntityMore() 함수를 타서
                    // OnDispatchIn 부터 다시 불리게 됨.
                    da.ReEnter(entity, true);
                }
            }
            else
            {
                var reason = "Wait for Kitting";
                OutputHelper.WriteWipLog(LogType.INFO, "BOM_INTERCEPT", lot, AoFactory.Current.NowDT, reason);
            }

            return true;
        }

        public IList<string> DO_CUSTOM_DISPATCH_TRANSPORT(DispatchingAgent da, IList<string> src, IHandlingBatch hb, ref bool handled, IList<string> prevReturnValue)
        {
            if (TransportSystem.Apply == false)
                return src;

            // Buffer에 Attach된 시점에 JobPreparation 시도
            // OnDispatchIn 또는 Attach 함수에 구현하지 않은 이유는,
            // 1. 재공초기화 완료를 기다린 후 수행해야 하고
            // 2. Bucketing Step에서는 호출하지 않아야 하므로 GetLoadableEqpList() 호출 직후에 시도하기 위함.

            var lot = hb.Sample as FabSemiconLot;
            if (lot.Location != null && lot.Location is Buffer)
            {
                var arrangedEqps = lot.CurrentArranges.Values.Select(x => x.Eqp.SimObject).ToList();

                TransportSystem.TryJobPreparation(arrangedEqps);
            }

            return src;
        }
    }
}