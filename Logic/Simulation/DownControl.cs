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
using Mozart.SeePlan.DataModel;
using Mozart.Simulation.Engine;
using static LinqToDB.SqlQuery.SqlPredicate;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class DownControl
    {
        public IEnumerable<Mozart.SeePlan.DataModel.PMSchedule> GET_PMLIST0(Mozart.SeePlan.Simulation.PMEvents fe, AoEquipment aeqp, ref bool handled, IEnumerable<Mozart.SeePlan.DataModel.PMSchedule> prevReturnValue)
        {
            return (aeqp as FabAoEquipment).Eqp.PMList;
        }

        public void WRITE_DOWN_LOG(AoEquipment aeqp, PMSchedule fs, DownEventType det, ref bool handled)
        {
            if (det == DownEventType.End)
                return;

            var eqp = (aeqp as FabAoEquipment).Eqp;

            OutputHelper.WriteEqpDownLog(eqp, fs);
        }

        public void ON_PMEVENT0(AoEquipment aeqp, PMSchedule fs, DownEventType det, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;
            feqp.IsWaitingUD = false;

            if (aeqp.SetParallelChamberPM(fs, det))
                return;

            if (det == DownEventType.Start)
            {
                EqpDownTag tag = ResourceHelper.GetEqpDownTag(aeqp.Target as FabSemiconEqp, fs.StartTime);

                aeqp.Loader.Block();

                bool isBM = false;
                if (tag != null && tag.DownType == EqpDownType.BM)
                {
                    isBM = true;
                    //Logger.MonitorInfo(string.Format("UD occurred... {0}, {1} ~ {2}", aeqp.EqpID, aeqp.NowDT.ToString("yyyy-MM-dd HH:mm:ss"), fs.EndTime.ToString("yyyy-MM-dd HH:mm:ss")));
                    aeqp.WriteHistory(LoadingStates.DOWN);
                }
                else
                    aeqp.WriteHistory(LoadingStates.PM);

                StandbyHelper.UpdateEndTimeofPrevRow(aeqp as FabAoEquipment, aeqp.NowDT);

                ArrangeHelper.HandleBackupArrange(aeqp, fs, true, null, "OnPMEvent", isBM);
            }
            else
            {
                aeqp.Loader.Unblock();
                aeqp.WriteHistoryAfterBreak();
                aeqp.SetModified();

                EqpDownTag tag = ResourceHelper.GetEqpDownTag(aeqp.Target as FabSemiconEqp, fs.StartTime);
                if (tag != null)
                {
                    if (tag.IsStackPm)
                    {
                        var postInhibitDuration = TimeSpan.FromDays(tag.StackPM.PostInhibitDays);
                        var inhibitEndTime = (aeqp.NowDT + postInhibitDuration).Floor();

                        var history = feqp.Eqp.ProcessInhibitHistory.SafeGet(tag);
                        if (history != null) // 이전까지는 pm delay 로 종료시점을 모르기 때문에 여기서 업데이트.
                            feqp.Eqp.ProcessInhibitHistory[tag] = new Tuple<DateTime, DateTime>(history.Item1, inhibitEndTime);

                        if (inhibitEndTime <= ModelContext.Current.EndTime)
                            EventHelper.AddManualEvent(postInhibitDuration, ManualEventTaskType.ExpireProcessInhibit, feqp, "ON_PMEVENT0");

                        if (feqp.IsReworkEffective == false) // 여러개의 EqpRework 조건이 겹치는 경우는 불허.
                        {
                            if (feqp.Eqp.ReworkInfos.IsNullOrEmpty() == false)
                            {
                                var validReworkInfo = feqp.Eqp.ReworkInfos.Where(x => x.PmCodes.Contains(tag.EventCode)).FirstOrDefault();
                                if (validReworkInfo != null)
                                {
                                    validReworkInfo.IsActive = true;
                                    feqp.IsReworkEffective = true;

                                    var reworkEffectiveDuration = TimeSpan.FromDays(validReworkInfo.ReworkPeriodDays);
                                    EventHelper.AddManualEvent(reworkEffectiveDuration, ManualEventTaskType.ExpireReworkEffective, feqp, "ON_PMEVENT0");
                                }
                            }
                        }
                    }
                }

                StandbyHelper.InsertIdleRow(aeqp as FabAoEquipment);

                ArrangeHelper.HandleBackupArrange(aeqp, fs, false, null, "OnPMEvent", false);
            }
        }
    }
}