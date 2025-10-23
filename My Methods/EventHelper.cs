using FabSimulator.DataModel;
using Mozart.Task.Execution;
using System.Collections.Generic;
using System;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.Extensions;
using Mozart.SeePlan.Semicon.DataModel;
using System.Linq;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class EventHelper
    {
        public static void AddManualEvent(Time delayTime, ManualEventTaskType taskType, FabAoEquipment feqp, string note = null, object arg = null)
        {
            delayTime = delayTime.Floor();

            if (delayTime.TotalDays > InputMart.Instance.GlobalParameters.period)
                return;

            DateTime nowDT = AoFactory.Current == null ? ModelContext.Current.StartTime : AoFactory.Current.NowDT;
            DateTime eventTime = nowDT.AddSeconds(delayTime.TotalSeconds).Floor();

            if (eventTime > ModelContext.Current.EndTime)
                return;

            List<ManualEventInfo> mEventList = AddManualEventTimeout(delayTime, eventTime);

            AddManualEventInfo(taskType, feqp, note, arg, eventTime, mEventList);

            SetWakeupEventContext(taskType, feqp, eventTime);

            static List<ManualEventInfo> AddManualEventTimeout(Time delayTime, DateTime eventTime)
            {
                if (InputMart.Instance.ManualEvents.TryGetValue(eventTime, out List<ManualEventInfo> mEventList) == false)
                {
                    InputMart.Instance.ManualEvents.Add(eventTime, mEventList = new List<ManualEventInfo>());

                    InputMart.Instance.ManualEventAo.AddTimeout(delayTime, DoManualEventTasks, "DoManualEventTasks", int.MinValue);
                }

                return mEventList;
            }

            static void AddManualEventInfo(ManualEventTaskType taskType, FabAoEquipment feqp, string note, object arg, DateTime eventTime, List<ManualEventInfo> mEventList)
            {
                ManualEventInfo mEvent = new ManualEventInfo();
                mEvent.EventTime = eventTime;
                mEvent.TaskType = taskType;
                mEvent.Eqp = feqp;
                mEvent.Argument = arg;
                mEvent.Note = note;

                mEventList.Add(mEvent);
            }

            static void SetWakeupEventContext(ManualEventTaskType taskType, FabAoEquipment feqp, DateTime eventTime)
            {
                if (taskType == ManualEventTaskType.WakeUpEqp)
                {
                    if (eventTime > feqp.NowDT && eventTime < feqp.NextManualWakeUpTime)
                        feqp.NextManualWakeUpTime = eventTime;

                    List<DateTime> dtList;
                    if (InputMart.Instance.ManualEventCtx.WakeUpTimesByEqp.TryGetValue(feqp.EqpID, out dtList) == false)
                        InputMart.Instance.ManualEventCtx.WakeUpTimesByEqp.Add(feqp.EqpID, dtList = new List<DateTime>());

                    if (dtList.Contains(eventTime) == false)
                        dtList.Add(eventTime);
                }
            }
        }

        private static void DoManualEventTasks(object sender, object args)
        {
            List<ManualEventInfo> mEventList;
            if (InputMart.Instance.ManualEvents.TryGetValue(AoFactory.Current.NowDT, out mEventList) == false)
                return;

            InputMart.Instance.ManualEventCtx.WakeUpEqpsNow = new List<string>();

            int cnt = mEventList.Count;

            for (int i = 0; i < cnt; i++)
            {
                ManualEventInfo mEventInfo = mEventList[i];

                switch (mEventInfo.TaskType)
                {
                    case ManualEventTaskType.WakeUpEqp:
                        WakeUpEqp(mEventInfo);
                        break;
                    case ManualEventTaskType.OnEqpUpStartTime:
                        OnEqpUpStartTime(mEventInfo);
                        break;
                    case ManualEventTaskType.CallBatchBuild:
                        CallBatchBuild(mEventInfo);
                        break;
                    case ManualEventTaskType.ActivateProcessInhibit:
                        ActivateProcessInhibit(mEventInfo);
                        break;
                    case ManualEventTaskType.ExpireProcessInhibit:
                        ExpireProcessInhibit(mEventInfo);
                        break;
                    case ManualEventTaskType.ExpireReworkEffective:
                        ExpireReworkEffective(mEventInfo);
                        break;
                    case ManualEventTaskType.CallRemoveAndReEnter:
                        CallRemoveAndReEnter(mEventInfo);
                        break;
                    case ManualEventTaskType.HandleInitialTransfer:
                        HandleInitalTransfer(mEventInfo);
                        break;
                    case ManualEventTaskType.CallEnqueue:
                        CallEnqueue(mEventInfo);
                        break;
                    case ManualEventTaskType.CallRemoveFromQueue:
                        CallRemoveFromQueue(mEventInfo);
                        break;
                    case ManualEventTaskType.UpdatePartStepArrange:
                        UpdatePartStepArrange(mEventInfo);
                        break;
                    case ManualEventTaskType.OnAssignQtWorkload:
                        OnAssignQtWorkload(mEventInfo);
                        break;
                }
            }

            InputMart.Instance.ManualEvents.Remove(AoFactory.Current.NowDT);
        }

        private static void WakeUpEqp(ManualEventInfo mEventInfo)
        {
            // DelayTime이 존재하는 WakeUp 이벤트를 걸때는 이 함수를 쓰도록 하자

            if (mEventInfo.Note == "FORCE_STANDBY")
                mEventInfo.Eqp.IsWaitingFS = false;

            if (InputMart.Instance.ManualEventCtx.WakeUpEqpsNow.Contains(mEventInfo.Eqp.EqpID))
                return;

            WakeUpWithManualEventSetting(mEventInfo.Eqp);

            InputMart.Instance.ManualEventCtx.WakeUpEqpsNow.Add(mEventInfo.Eqp.EqpID);

            static void WakeUpWithManualEventSetting(AoEquipment aeqp)
            {
                FabAoEquipment feqp = aeqp as FabAoEquipment;
                if (feqp.NextManualWakeUpTime <= aeqp.NowDT)
                {
                    List<DateTime> dtList;
                    if (InputMart.Instance.ManualEventCtx.WakeUpTimesByEqp.TryGetValue(aeqp.EqpID, out dtList))
                    {
                        dtList.Remove(aeqp.NowDT);

                        feqp.NextManualWakeUpTime = DateTime.MaxValue;
                        List<DateTime> passedTimes = new List<DateTime>();
                        foreach (DateTime eventTm in dtList)
                        {
                            if (eventTm <= aeqp.NowDT)
                                passedTimes.Add(eventTm);
                            else if (eventTm < feqp.NextManualWakeUpTime)
                                feqp.NextManualWakeUpTime = eventTm;
                        }

                        if (passedTimes.Count > 0)
                            passedTimes.ForEach(x => dtList.Remove(x));
                    }
                }

                aeqp.WakeUp();
            }
        }

        private static void OnEqpUpStartTime(ManualEventInfo mEventInfo)
        {
            FabAoEquipment feqp = mEventInfo.Eqp;

            if (feqp.CurrentState == LoadingStates.PM)
                return;

            feqp.CurrentState = LoadingStates.IDLE;

            StandbyHelper.InsertIdleRow(feqp);
        }

        private static void CallBatchBuild(ManualEventInfo mEventInfo)
        {
            FabAoEquipment feqp = mEventInfo.Eqp;

            // StagingBatch는 이벤트 호출 없이 먼저 처리되어야 함.
            //if (feqp.Eqp.StagingLots.IsNullOrEmpty() == false)
            //    return;

            BatchingContext ctx = new BatchingContext();
            if (feqp.NowDT == ModelContext.Current.StartTime)
                ctx.EventType = BatchingEventType.SimStart.ToString();
            else
            {
                if (feqp.Eqp.SimType == SimEqpType.BatchInline && feqp.IsProcessing)
                    return;

                ctx.EventType = BatchingEventType.IdleTimer.ToString();
            }

            var batch = BatchingManager.BuildAndSelect(feqp, ctx);

            // IDLE 상태에서 배치 빌드후 즉시 투입을 진행하기 위해 호출.
            if (batch != null && ctx.EventType == BatchingEventType.IdleTimer.ToString())
                AddManualEvent(Time.Zero, ManualEventTaskType.WakeUpEqp, feqp, "CallBatchBuild");
        }

        private static void ActivateProcessInhibit(ManualEventInfo mEventInfo)
        {
            FabAoEquipment feqp = mEventInfo.Eqp;
            var tuple = mEventInfo.Argument as Tuple<PMSchedule, EqpDownTag>;

            feqp.OnProcessInhibit = true;

            if (feqp.ActiveStackLotDict.IsNullOrEmpty())
                return;

            // Inhibit 시점에 EvaluatePriority를 업데이트
            var queue = feqp.DispatchingAgent.GetDestination(feqp.EqpID).Queue;
            foreach (FabSemiconLot lot in queue)
            {
                ArrangeHelper.GetActiveStack(lot, lot.CurrentAttribute);
            }

            var pm = tuple.Item1;

            // ProcessInhibit 적용 대상인 StackPM만 Delay 반영 (Lot이 계속 흘러들어오면 의미 없으므로)
            // preStackPmDays = 0 을 줘도 Delay동안 Inhibit 적용하게 됨.
            feqp.DownManager.CancelEvent(pm.StartTime);
            feqp.DelayedStackPM = tuple;
        }

        private static void ExpireProcessInhibit(ManualEventInfo mEventInfo)
        {
            FabAoEquipment feqp = mEventInfo.Eqp;

            feqp.OnProcessInhibit = false;
        }

        private static void ExpireReworkEffective(ManualEventInfo mEventInfo)
        {
            FabAoEquipment feqp = mEventInfo.Eqp;

            // 먼저 발생한 EqpRework 기간이 끝나야 다른 PmCode로 발동할 수 있음 (동시 적용은 불허)
            feqp.IsReworkEffective = false;
            feqp.Eqp.ReworkInfos.ForEach(x => x.IsActive = false);
        }

        private static void CallRemoveAndReEnter(ManualEventInfo mEventInfo)
        {
            var tuple = mEventInfo.Argument as Tuple<IHandlingBatch, FabSemiconStep>;
            var lot = tuple.Item1 as FabSemiconLot;
            var step = tuple.Item2;

            if (lot.IsVanishing)
                return;

            if (lot.CurrentStep == step && lot.CurrentState == EntityState.WAIT)
            {
                lot.CurrentArranges.Clear();

                var da = AoFactory.Current.GetDispatchingAgent("-");

                da.Remove(lot);
                da.Take(lot);
            }
        }

        private static void HandleInitalTransfer(ManualEventInfo mEventInfo)
        {
            var hb = mEventInfo.Argument as IHandlingBatch;

            var da = AoFactory.Current.GetDispatchingAgent("-");
            da.Take(hb);
        }

        private static void CallEnqueue(ManualEventInfo mEventInfo)
        {
            var tuple = mEventInfo.Argument as Tuple<IHandlingBatch, FabSemiconStep, FabAoEquipment, string>;
            var lot = tuple.Item1 as FabSemiconLot;
            var step = tuple.Item2;
            var feqp = tuple.Item3;
            //var callType = tuple.Item4;

            if (lot.IsVanishing)
                return;

            if (lot.OnHandlingBatch)
                return; // BackupDelay로 Enqueue Timer가 걸린 도중 FHB 로 Merge된 경우는 큐에 다시 담으면 안됨.

            if (lot.CurrentStep == step && lot.CurrentState == EntityState.WAIT)
            {
                var da = AoFactory.Current.GetDispatchingAgent("-");

                DispatchingInfo destination = da.GetDestination(feqp.EqpID);
                if (destination != null && destination.Queue.Contains(lot) == false)
                {
                    destination.Queue.Enqueue(lot);
                    da.AddLoadable(lot, feqp);

                    // GetLoadableEqpList가 다시 호출되지는 않으므로
                    // CurrentArranges를 업데이트 해야 에러를 방지할 수 있음.
                    var attr = lot.CurrentFabStep.PartStepDict.SafeGet(lot.FabProduct.PartID);
                    var arr = attr?.CurrentArranges.Where(x => x.EqpID == feqp.EqpID).FirstOrDefault();
                    if (arr != null && lot.CurrentArranges.ContainsKey(arr.EqpID) == false)
                        lot.CurrentArranges.Add(arr.EqpID, arr);

                    // 위 함수들을 사용했어도 설비 직접 깨워야 함.
                    feqp.WakeUp();

                    // Timer로 불린 함수에서 ZeroTimer는 추가해도 동작 안함.
                    //AddManualEvent(Time.Zero, ManualEventTaskType.WakeUpEqp, feqp, "CallEnqueue");
                }
            }
        }

        private static void CallRemoveFromQueue(ManualEventInfo mEventInfo)
        {
            var tuple = mEventInfo.Argument as Tuple<IHandlingBatch, FabSemiconStep, FabAoEquipment, string>;
            var lot = tuple.Item1 as FabSemiconLot;
            var step = tuple.Item2;
            var feqp = tuple.Item3;

            if (lot.IsVanishing)
                return;

            if (lot.CurrentStep == step && lot.CurrentState == EntityState.WAIT)
            {
                var da = AoFactory.Current.GetDispatchingAgent("-");

                DispatchingInfo destination = da.GetDestination(feqp.EqpID);
                if (destination != null)
                {
                    destination.Queue.Remove(lot);
                    da.RemoveLoadable(lot, feqp);
                }
            }
        }

        private static void UpdatePartStepArrange(ManualEventInfo mEventInfo)
        {
            var schedule = mEventInfo.Argument as ArrangeSchedule;

            var eventName = "-";

            foreach (var arr in schedule.ArrangesToAdd)
            {
                schedule.PartStep.CurrentArranges.Add(arr);

                OutputHelper.WriteArrangeLog(arr, AoFactory.Current.NowDT, eventName, true);

                //var queue = arr.Eqp.SimObject.DispatchingAgent.GetDestination(arr.EqpID).Queue;
                // TODO? : queue에 이미 들어있는 재공도 ReArrange 필요?
            }

            foreach (var arr in schedule.ArrangesToRemove)
            {
                // PST 에 Event를 걸어도, LocateForDispatch가 먼저 불려서 제외할 Arrange로 재공이 등록되는 문제가 생김.
                // Log는 여기서 처리하되, RecipeInhibit에 대한 제외 처리는 별도로 사전 수행.
                if (arr.IsRecipeInhibit)
                    eventName = "RecipeInhibit";

                schedule.PartStep.CurrentArranges.Remove(arr);

                OutputHelper.WriteArrangeLog(arr, AoFactory.Current.NowDT, eventName, false);

                //var queue = arr.Eqp.SimObject.DispatchingAgent.GetDestination(arr.EqpID).Queue;
                // TODO? : queue에 이미 들어있는 재공도 ReArrange 필요?
            }
        }

        private static void OnAssignQtWorkload(ManualEventInfo mEventInfo)
        {
            Tuple<QtEqp, double, SemiconLot> tuple = (Tuple<QtEqp, double, SemiconLot>)mEventInfo.Argument;
            QtEqp item = tuple.Item1;
            QtCategory ctg = item.Categories[tuple.Item2];
            SemiconLot item2 = tuple.Item3;
            double value = 0.0;
            bool flag = false;
            foreach (QtCategory value2 in item.Categories.Values)
            {
                if (value2.Lots.TryGetValue(item2, out value))
                {
                    flag = true;
                    break;
                }
            }

            if (flag)
            {
                AssignQtWorkload(item2, item, ctg, value, "Timer");
            }

            static void AssignQtWorkload(SemiconLot lot, QtEqp eqp, QtCategory ctg, double lotWorkload, string eventType)
            {
                if (!(lotWorkload <= 0.0) && !ctg.Lots.ContainsKey(lot))
                {
                    ctg.Lots.Add(lot, lotWorkload);
                    ctg.WorkloadHours += lotWorkload;
                    QtWorkloadControl.Instance.OnUpdateWorkload(lot, eqp, ctg, lotWorkload, eventType);
                }
            }
        }
    }
}