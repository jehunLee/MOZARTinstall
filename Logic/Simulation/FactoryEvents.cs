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
using Mozart.Simulation.Engine;
using Mozart.SeePlan.Semicon.Simulation;
using System.Data.SqlTypes;
using System.Diagnostics;
using Mozart.SeePlan;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class FactoryEvents
    {
        public void ON_DONE0(Mozart.SeePlan.Simulation.AoFactory aoFactory, ref bool handled)
        {
            IEnumerable<ISimEntity> list = aoFactory.WipManager.Snapshot();

            foreach (var item in list)
            {
                OutputHelper.UpdateEqpPlanOnDoneFactory(item);
            }

            foreach (ISimEntity entity in list)
            {
                if (entity is LotBatch)
                {
                    LotBatch batch = entity as LotBatch;

                    batch.Apply((x, y) => EntityHelper.AnalyzeWip(x));
                }
                else
                {
                    EntityHelper.AnalyzeWip(entity as FabSemiconLot);
                }
            }
        }

        public void COLLECT_STEP_WIP(AoFactory aoFactory, ref bool handled)
        {
            aoFactory.TimerAgent.Add("CollectStepWip", StatisticHelper.CollectStepWip, Time.Zero);
        }

        public void WRITE_STANDBY_TIME(AoFactory aoFactory, ref bool handled)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STANDBY_TIME"))
                return;

            foreach (var item in InputMart.Instance.StandbyTimeOutputs)
            {
                foreach (var row in item.Value)
                {
                    row.SCENARIO_ID = InputMart.Instance.ScenarioID;
                    row.VERSION_NO = ModelContext.Current.VersionNo;

                    if (row.END_TIME > ModelContext.Current.EndTime)
                        row.END_TIME = ModelContext.Current.EndTime;

                    var standbyTime = Convert.ToSingle(row.END_TIME.Subtract(row.START_TIME).TotalMinutes);

                    row.IDLE_MIN = standbyTime;

                    OutputMart.Instance.STANDBY_TIME.Add(row);
                }
            }
        }

        public void ON_DAY_CHANGED0(AoFactory aoFactory, ref bool handled)
        {
            Stopwatch watch = ModelContext.Current.Get<Stopwatch>(Constants.CTX_DAY_STOPWATCH, null);

            Logger.MonitorInfo(string.Format("Day changed at {0} ...... {1}", aoFactory.NowDT.DbToString(), watch.Elapsed));            

            watch.Restart();
        }

        public void INSTANCING_FAB_IN(AoFactory aoFactory, ref bool handled)
        {
            if (AoFactory.Current.NowDT == ModelContext.Current.EndTime)
                return;

            List<ILot> instancingLots = new List<ILot>();

            if (InputMart.Instance.InPlanRule == FabInPlanRule.ConstantWip)
            {
                DateTime yesterday = AoFactory.Current.NowDT.AddDays(-1);

                var yesterdayFabOut = InputMart.Instance.FabOutQty.SafeGet(yesterday);
                //Logger.MonitorInfo(string.Format("=> FabOutQty : {0}", yesterdayFabOut));

                if (yesterdayFabOut > 0)
                    EntityHelper.BuildProductMix(yesterdayFabOut);

                instancingLots = EntityHelper.BuildTodayFabIn();

                EntityHelper.InitiateLots(instancingLots);
            }
        }

        public void ON_BEGIN_INITIALIZE0(AoFactory aoFactory, ref bool handled)
        {
            // 아에 로딩 안하면 QtManager.Current 객체가 생성되지 않음.
            //if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 0)
            //    handled = true;
        }

        public void SET_ARRANGE_SCHEDULE(AoFactory aoFactory, ref bool handled)
        {
            foreach (var attr in InputMart.Instance.PartStepAttribute.Rows)
            {
                foreach (var kvp in attr.ArrangeDict)
                {
                    SetArrangeAddSchedule(attr, kvp.Key, kvp.Value);

                    foreach (var arr in kvp.Value)
                    {
                        SetArrangeRemoveSchedule(attr, arr);
                    }
                }

                foreach (var schedule in attr.ArrangeCalendar)
                {
                    if (schedule.EventTime < ModelContext.Current.StartTime)
                    {
                        schedule.ArrangesToAdd.ForEach(attr.CurrentArranges.Add);
                        continue;
                    }

                    var timeToApply = schedule.EventTime - ModelContext.Current.StartTime;

                    EventHelper.AddManualEvent(timeToApply, ManualEventTaskType.UpdatePartStepArrange, null, "SET_ARRANGE_SCHEDULE", schedule);
                }

                attr.PhotoGen = attr.CurrentArranges.IsNullOrEmpty() ? "-" : attr.CurrentArranges.First().Eqp.ScannerGeneration;
            }

            static void SetArrangeAddSchedule(PartStepAttribute attr, DateTime eventTime, ICollection<EqpArrange> arrs)
            {
                var schedule = GetArrangeSchedule(attr, eventTime);

                foreach (var arr in arrs)
                {
                    if (arr.IsRecipeInhibit) // Remove 시키면서 Log를 적긴 하지만, ManualEvent 호출 전에 PST에서 Arrange 사용하기 때문에, Add 에서 부터 제외
                        continue;

                    schedule.ArrangesToAdd.Add(arr);
                }
            }

            static void SetArrangeRemoveSchedule(PartStepAttribute attr, EqpArrange arr)
            {
                if (arr.EndTime > ModelContext.Current.EndTime)
                    return;

                var schedule = GetArrangeSchedule(attr, arr.EndTime);

                schedule.ArrangesToRemove.Add(arr);
            }

            static ArrangeSchedule GetArrangeSchedule(PartStepAttribute attr, DateTime eventTime)
            {
                ArrangeSchedule schedule = attr.ArrangeCalendar.Where(x => x.EventTime == eventTime).FirstOrDefault();
                if (schedule == null)
                {
                    schedule = new ArrangeSchedule();
                    schedule.EventTime = eventTime;
                    schedule.PartStep = attr;

                    attr.ArrangeCalendar.Add(schedule);
                }

                return schedule;
            }
        }

        public void START_STOP_WATCH_FOR_LOG(AoFactory aoFactory, ref bool handled)
        {
            Stopwatch watch = new Stopwatch();
            watch.Start();

            ModelContext.Current.Set(Constants.CTX_DAY_STOPWATCH, watch);
        }

        public void WRITE_PROCESS_INHIBIT(AoFactory aoFactory, ref bool handled)
        {
            foreach (var eqp in InputMart.Instance.FabSemiconEqp.Rows)
            {
                OutputHelper.WriteEqpDownLogProcessInhibit(eqp);
            }
        }

        public void RESET_DAILY_QTY(AoFactory aoFactory, ref bool handled)
        {
            foreach (var eqp in InputMart.Instance.FabSemiconEqp.Rows)
            {
                eqp.SimObject.DailyFirstLayerQty = 0;
            }
        }

        public void SET_GLOBAL_VARIABLES(AoFactory aoFactory, ref bool handled)
        {
            InputMart.Instance.ManualEventAo = new ActiveObject(aoFactory.Engine);
        }

        public void DO_PERIODIC_SUMMARY(AoFactory aoFactory, ref bool handled)
        {
            StatisticHelper.DoPeriodicSummary();
        }

        public void WRITE_SUMMARY(AoFactory aoFactory, ref bool handled)
        {
            StatisticHelper.DoPeriodicSummary(true);

            StatisticHelper.IncludeMissingSteps();

            StatisticHelper.CalculateTotalSummary(InputMart.Instance.CycleTimeInfo.Rows);

            WriteSummaryFabInOutWip();

            static void WriteSummaryFabInOutWip()
            {
                if (InputMart.Instance.ExcludeOutputTables.Contains("SUMMARY_FABIO_WIP"))
                    return;

                foreach (var info in InputMart.Instance.FabInOutInfo)
                {
                    SUMMARY_FABIO_WIP row = new SUMMARY_FABIO_WIP();
                    row.SCENARIO_ID = InputMart.Instance.ScenarioID;
                    row.VERSION_NO = ModelContext.Current.VersionNo;
                    row.TARGET_DATE = info.TARGET_DATE;
                    row.TARGET_WEEK = info.TARGET_WEEK;
                    row.TARGET_MONTH = info.TARGET_MONTH;
                    row.PART_ID = info.PART_ID;
                    row.FABIN_QTY = Math.Round(info.FABIN_QTY, 2);
                    row.FABOUT_QTY = Math.Round(info.FABOUT_QTY, 2);
                    row.WIP_QTY = Math.Round(info.WIP_QTY, 2);
                    row.SCRAP_QTY = Math.Round(info.SCRAP_QTY, 2);

                    OutputMart.Instance.SUMMARY_FABIO_WIP.Add(row);
                }
            }
        }

        public void SET_LAYER_EQP_GROUP(AoFactory aoFactory, ref bool handled)
        {
            foreach (var items in InputMart.Instance.PartStepAttribute.Rows.GroupBy(x => new { x.PartID, x.LayerID }))
            {
                var eqpGroup = "XT";

                if (items.Any(x => x.ArrangeDict.Values.Any(y => y.Eqp.ResGroup == "NXE")))
                    eqpGroup = "NXE";
                else if (items.Any(x => x.ArrangeDict.Values.Any(y => y.Eqp.ResGroup == "NXT")))
                    eqpGroup = "NXT";

                InputMart.Instance.PartLayerEqpGroup.Add(items.Key.PartID, items.Key.LayerID, eqpGroup);
            }
        }

        public void WRITE_STANDBY_TIME_DAYCHANGED(AoFactory aoFactory, ref bool handled)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STANDBY_TIME"))
                return;

            // 임시로 standByTime 정보를 담아줄 변수
            MultiDictionary<FabAoEquipment, STANDBY_TIME> tempStandbyTimeOutputs = new MultiDictionary<FabAoEquipment, STANDBY_TIME>();

            foreach (var item in InputMart.Instance.StandbyTimeOutputs)
            {
                foreach (var row in item.Value)
                {
                    row.SCENARIO_ID = InputMart.Instance.ScenarioID;
                    row.VERSION_NO = ModelContext.Current.VersionNo;

                    // 만일 EndTime이 업데이트가 안되어있다면 해당 내용은 Skip 하자.
                    if (row.END_TIME == DateTime.MaxValue)
                    {
                        tempStandbyTimeOutputs.Add(item.Key, row);
                        continue;
                    }

                    var standbyTime = Convert.ToSingle(row.END_TIME.Subtract(row.START_TIME).TotalMinutes);
                    row.IDLE_MIN = standbyTime;

                    OutputMart.Instance.STANDBY_TIME.Add(row);
                }
            }

            // inputMart 안에 있는 내용 Clear
            InputMart.Instance.StandbyTimeOutputs.Clear();

            // EndTime이 다음 날짜의 시작 시간보다 컸던 내용들을 다시 넣어주자. => 추후 UpdateEndTimeofPrevRow 쪽에서 EndTime 변화.
            foreach (var item in tempStandbyTimeOutputs)
            {
                foreach (var row in item.Value)
                {
                    InputMart.Instance.StandbyTimeOutputs.Add(item.Key, row);
                }
            }
        }

        public void ON_START0(AoFactory aoFactory, ref bool handled)
        {
            var loadingRule = Helper.GetConfig(ArgsGroup.Bop_Step).loadingRule;
            if (loadingRule == 2)
            {
                var delay = Time.FromHours(Helper.GetConfig(ArgsGroup.Resource_Bucketing).capaFreqHr);

                aoFactory.TimerAgent.Add("ResetBucketingCapacity", ResourceHelper.ResetBucketingCapacity, delay);
            }   
        }

        public int COMPARE_SAME_TIME_EVENT0(Event x, Event y, ref bool handled, int prevReturnValue)
        {
            if (x.Owner.Parent is FabAoEquipment && y.Owner.Parent is FabAoEquipment)
            {
                var xEqp = x.Owner.Parent as FabAoEquipment;
                var yEqp = y.Owner.Parent as FabAoEquipment;

                return xEqp.EqpID.CompareTo(yEqp.EqpID);
            }

            return 0;
        }

        public void SET_CUSTOM_FILTER_TRANSPORT(AoFactory aoFactory, ref bool handled)
        {
            var jobPrepKey = CustomDispatchType.JOB_PREP.ToString();
            var jobPrepFilters = new List<string>
            {
                "JOB_PREP_SAMPLE"
            };

            var portRsvKey = CustomDispatchType.PORT_RSV.ToString();
            var portRsvFilters = new List<string>();

            var filterManger = aoFactory.Filters;
            filterManger.CreateMethods(jobPrepKey, jobPrepFilters);
            filterManger.CreateMethods(portRsvKey, portRsvFilters);
        }
    }
}