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
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan.Pegging;
using Mozart.SeePlan;
using Mozart.SeePlan.DataModel;
using System.Text;
using Mozart.Simulation.Engine;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class OutputHelper
    {
        internal static void WriteStepTarget(FabSemiconPegTarget pt, FabSemiconStep step, string productID, bool isOut)
        {
            FabSemiconPegPart pp = pt.PegPart as FabSemiconPegPart;

            STEP_TARGET st = new STEP_TARGET();

            var shiftDt = pt.CalcDate.ShiftStartTimeOfDayT().GetMSSqlDateTime();

            st.SCENARIO_ID = InputMart.Instance.ScenarioID;
            st.VERSION_NO = ModelContext.Current.VersionNo;
            st.LINE_ID = pp.Product.LineID;
            st.PRODUCT_ID = (pp.Product as StdProduct).StdProductID;
            st.ROUTE_ID = step.RouteID;
            st.STEP_ID = step.StepID;
            st.TARGET_SHIFT = shiftDt.GetMSSqlDateTime();
            st.TARGET_DATE = pt.CalcDate.GetMSSqlDateTime();
            st.IN_QTY = 0;
            st.OUT_QTY = 0;
            st.STEP_SEQ = step.Sequence;

            double qty = Math.Ceiling(pt.CalcQty);

            if (isOut)
                st.OUT_QTY += qty;
            else
                st.IN_QTY += qty;

            //st.OrgQty += pt.CalcQty;

            st.DEMAND_ID = pt.Mo.DemandID;
            st.MO_DUE_DATE = pt.Mo.DueDate;

            if (InputMart.Instance.ExcludeOutputTables.Contains("STEP_TARGET") == false)
                OutputMart.Instance.STEP_TARGET.Add(st);

            if (isOut)
            {
                var key = Helper.CreateKey(st.DEMAND_ID, st.STEP_ID);

                if (InputMart.Instance.PegTargetInfo.ContainsKey(key) == false)
                    InputMart.Instance.PegTargetInfo.Add(key, st.TARGET_DATE);
            }
            else
            {
                if (st.STEP_ID == Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID) // STEP_SEQ는 입력에 따라 0또는 1이 아닐 수 있음.
                {
                    if (InputMart.Instance.InPlanRule == FabInPlanRule.ConstantWip)
                    {
                        var simFirstWeekStartTime = Helper.GetWeekStartTime(ModelContext.Current.StartTime);
                        var simLastWeekStartTime = Helper.GetWeekStartTime(ModelContext.Current.EndTime);

                        if (st.TARGET_DATE >= simFirstWeekStartTime && st.TARGET_DATE < simLastWeekStartTime.AddDays(7))
                        {
                            var weekStartTime = Helper.GetWeekStartTime(st.TARGET_DATE);
                            InputMart.Instance.InTargetsByWeek.Add(weekStartTime, pt);
                        }
                    }
                    else if (InputMart.Instance.InPlanRule == FabInPlanRule.Demand)
                    {
                        if (/*st.TARGET_DATE >= ModelContext.Current.StartTime &&*/ st.TARGET_DATE < ModelContext.Current.EndTime)
                            InputMart.Instance.InTargets.Add(pt);
                    }
                    else if (InputMart.Instance.InPlanRule == FabInPlanRule.FabInPlan)
                    {
                        // FabInPlanRule.FabInPlan 이더라도, DueDate를 맵핑하기 위해 세팅.
                        pt.FabInPlanRemainQty = pt.CalcQty;
                        InputMart.Instance.InTargets.Add(pt);
                    }
                }
            }
        }

        internal static void WriteEqpDownLog(FabSemiconEqp eqp, PMSchedule fs)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_DOWN_LOG"))
                return;

            EQP_DOWN_LOG row = new EQP_DOWN_LOG();
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.LINE_ID = eqp.LineID_;
            row.EQP_GROUP = eqp.ResGroup;
            row.EQP_ID = string.IsNullOrEmpty(fs.ComponentID) ? eqp.ResID : fs.ComponentID;
            row.DURATION_HRS = Math.Round(fs.Duration.TotalHours, 2);
            row.START_TIME = fs.StartTime.Floor();
            row.END_TIME = fs.EndTime.Floor();
            row.AREA_ID = eqp.StepGroup;

            EqpDownTag tag = ResourceHelper.GetEqpDownTag(eqp, fs.StartTime);

            row.EVENT_CODE = tag != null ? tag.EventCode : "-";
            row.DOWN_TYPE = tag != null ? tag.DownTypeStr : "-";

            OutputMart.Instance.EQP_DOWN_LOG.Add(row);
        }

        internal static void WriteEqpDownLogProcessInhibit(FabSemiconEqp eqp)
        {
            if (eqp.ProcessInhibitHistory.IsNullOrEmpty())
                return;

            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_DOWN_LOG") == false)
            {
                foreach (var kvp in eqp.ProcessInhibitHistory)
                {
                    var tag = kvp.Key;
                    var piStartTime = kvp.Value.Item1;
                    var piEndTime = kvp.Value.Item2;

                    EQP_DOWN_LOG row = new EQP_DOWN_LOG();
                    row.SCENARIO_ID = InputMart.Instance.ScenarioID;
                    row.VERSION_NO = ModelContext.Current.VersionNo;
                    row.LINE_ID = eqp.LineID_;
                    row.EQP_GROUP = eqp.ResGroup;
                    row.EQP_ID = eqp.ResID;

                    row.DURATION_HRS = piEndTime != DateTime.MaxValue ? Math.Round((piEndTime - piStartTime).TotalHours, 2) : 0;
                    row.START_TIME = piStartTime.Floor();
                    row.END_TIME = piEndTime.Floor();
                    row.AREA_ID = eqp.StepGroup;

                    row.EVENT_CODE = tag.EventCode;
                    row.DOWN_TYPE = "StackInhibit";

                    OutputMart.Instance.EQP_DOWN_LOG.Add(row);
                }
            }

            foreach (var kvp in eqp.SimObject.ActiveStackLotDict)
            {
                var stackGroup = kvp.Key;
                foreach (var lot in kvp.Value)
                {
                    OutputHelper.WriteErrorLogWithEqp(LogType.INFO, "ActiveStackLotOnEnd", eqp.SimObject, lot, lot.CurrentStepID, stackGroup);
                }
            }
        }

        internal static void WritePegHistory(PegTarget target, FabPlanWip wip, double pegQty)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("PEG_HISTORY"))
                return;

            if (OutputMart.Instance.PEG_HISTORY == null)
                return;

            FabSemiconPegTarget ms = target as FabSemiconPegTarget;
            FabWipInfo info = wip.Wip as FabWipInfo;

            EntityHelper.AssignLotDueDate(info, ms.Mo.DueDate);

            if (info.DemandID.IsNullOrEmpty())
                info.DemandID = ms.Mo.DemandID;
            
            if (InputMart.Instance.ExcludeOutputTables.Contains("PEG_HISTORY"))
                return;

            PEG_HISTORY hist = new PEG_HISTORY();

            hist.SCENARIO_ID = InputMart.Instance.ScenarioID;
            hist.VERSION_NO = ModelContext.Current.VersionNo;
            hist.LINE_ID = info.LineID;
            hist.LOT_ID = info.LotID;
            hist.PRODUCT_ID = (info.Product as FabProduct).StdProductID;
            hist.STEP_ID = wip.MapStep.StepID;
            hist.WAFER_QTY = info.UnitQty;
            hist.PEG_QTY = pegQty;
            hist.DEMAND_ID = ms.Mo.DemandID;
            hist.MO_DUE_DATE = ms.Mo.DueDate;
            hist.TARGET_DATE = ms.CalcDate;
            hist.STEP_SEQ = (wip.MapStep as FabSemiconStep).Sequence;

            OutputMart.Instance.PEG_HISTORY.Add(hist);
        }


        internal static void WriteUnpegHistory(FabWipInfo info, double unpegQty, string reason)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("UNPEG_HISTORY"))
                return;

            if (OutputMart.Instance.UNPEG_HISTORY == null)
                return;

            var hist = new UNPEG_HISTORY();

            hist.SCENARIO_ID = InputMart.Instance.ScenarioID;
            hist.VERSION_NO = ModelContext.Current.VersionNo;
            hist.LINE_ID = info.LineID;
            hist.LOT_ID = info.LotID;
            hist.PRODUCT_ID = (info.Product as FabProduct).StdProductID;
            hist.STEP_ID = info.InitialStep.StepID;
            hist.WAFER_QTY = info.UnitQty;
            hist.REMAIN_QTY = unpegQty;
            hist.REASON = reason;

            OutputMart.Instance.UNPEG_HISTORY.Add(hist);
        }

        internal static void WriteChamberHistory(AoEquipment aeqp, string label, FabSemiconLot lot, DateTime inTime, DateTime outTime, int units)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("CHAMBER_HISTORY"))
                return;

            var log = new Outputs.CHAMBER_HISTORY();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;
            log.EQP_ID = aeqp.EqpID;
            log.SUB_EQP_ID = label;
            log.STATUS = "BUSY";
            log.LOT_ID = lot.LotID;
            log.STEP_ID = lot.CurrentStepID;
            log.START_TIME = inTime;
            log.END_TIME = outTime;
            log.WAFER_QTY = units;

            OutputMart.Instance.CHAMBER_HISTORY.Add(log);
        }

        internal static void UpdateEqpPlanStartTime(AoEquipment aeqp, FabSemiconLot lot, DateTime inTime, ref bool updated)
        {
            var sheet = AoFactory.Current.StatManager.GetSheet<Outputs.EQP_PLAN>();
            var row = sheet.GetRow(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, aeqp.EqpID, lot.LotID, lot.CurrentProcessID, 
                lot.CurrentStep.Sequence, lot.CurrentStepID, lot.DispatchInTime);
            if (row != null)
            {
                if (updated && inTime >= row.START_TIME)
                    return;

                row.START_TIME = inTime;
                updated = true;
            }
        }

        internal static void WriteQtimeHistoryDone(FabSemiconLot lot, QtType type, string targetStepId = null)
        {
            if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 0)
                return;

            if (targetStepId == null)
                targetStepId = lot.CurrentStepID;

            ICollection<QtActivation> doneList = new List<QtActivation>();
            if (type == QtType.MAX)
            {
                if (lot.MaxQtActivations.IsNullOrEmpty())
                    return;

                doneList = lot.MaxQtActivations.SafeGet(targetStepId);
            }
            else if (type == QtType.MIN)
            {
                if (lot.MinQtActivations.IsNullOrEmpty())
                    return;

                doneList = lot.MinQtActivations.SafeGet(targetStepId);
            }

            WriteQtimeHistory(lot, doneList);
        }

        internal static void WriteQtimeHistory(FabSemiconLot lot, ICollection<QtActivation> qtActivations, bool isDone = true)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("QTIME_HISTORY"))
                return;

            if (qtActivations.IsNullOrEmpty())
                return;

            var now = AoFactory.Current.NowDT;
            foreach (var active in qtActivations)
            {
                var loop = active.Loop;

                QTIME_HISTORY hist = new QTIME_HISTORY();
                hist.SCENARIO_ID = InputMart.Instance.ScenarioID;
                hist.VERSION_NO = ModelContext.Current.VersionNo;
                hist.LOT_ID = lot.LotID;
                hist.PART_ID = lot.FabProduct.PartID;
                hist.START_STEP_ID = loop.StartStepID;
                hist.END_STEP_ID = loop.EndStepID;
                hist.LIMIT_TYPE = loop.ConstType.ToString();

                hist.SPEC_HRS = loop.LimitTime.TotalHours;
                hist.WARNING_HRS = loop.WarningTime.TotalHours;

                hist.START_TIME = active.StartTime;
                hist.END_TIME = now;

                bool qtSafe = loop.ConstType == QtType.MAX ? now <= active.BreachTime : now >= active.BreachTime;

                if (isDone)
                    hist.QTIME_RESULT = qtSafe ? "Y" : "N";
                else
                    hist.QTIME_RESULT = qtSafe ? "X" : "N";

                hist.QTIME_HRS = Math.Round((now - active.StartTime).TotalHours, 2);

                hist.CONTROL_TYPE = loop.ControlType.ToString();

                if (active is FabQtActivation)
                    hist.IS_INITIAL = "Y";
                else if (lot.FabWipInfo.CurrentState == EntityState.RUN && lot.FabWipInfo.WipStepID == loop.StartStepID)
                    hist.IS_INITIAL = "Y";
                else
                    hist.IS_INITIAL = "N";

                var chain = (loop as FabQtLoop).Chain;
                hist.CHAIN_ID = chain != null ? chain.ChainID.ToInt32() : 0;

                OutputMart.Instance.QTIME_HISTORY.Add(hist);
            }
        }

        internal static void WriteToolingHistory(FabTooling tooling, DateTime eventTime, string toolingName)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("TOOLING_HISTORY"))
                return;

            TOOLING_HISTORY hist = new TOOLING_HISTORY();
            hist.SCENARIO_ID = InputMart.Instance.ScenarioID;
            hist.VERSION_NO = ModelContext.Current.VersionNo;
            hist.EVENT_TIME = eventTime;
            hist.TOOLING_NAME = toolingName; // Seize 시점에는 Arrange에 기반해서 적어줄 수 있지만, 그 외에는 하나로 특정되지 않음.
            hist.TOOLING_TYPE = tooling.ToolingType;
            hist.TOOLING_ID = tooling.ToolingID;
            hist.TOOLING_LOCATION = tooling.ToolingLocation;

            OutputMart.Instance.TOOLING_HISTORY.Add(hist);
        }

        internal static void WriteToolingSelectionLog(FabAoEquipment aeqp, ToolingSelectionInfo info)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("TOOLING_SELECTION_LOG"))
                return;

            TOOLING_SELECTION_LOG log = new TOOLING_SELECTION_LOG();
            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;
            log.EVENT_TIME = aeqp.NowDT;
            log.EQP_ID = aeqp.EqpID;
            log.TOOLING_NAME = info.ToolingName;
            log.TOOLING_TYPE = info.Tooling.ToolingType;
            log.TOOLING_LIST = Helper.GetVarchar255(info.ToolingList);
            log.TOOLING_ID = info.Tooling.ToolingID;
            log.TOOLING_LOCATION = info.Location;

            log.WL_HRS = (float)Math.Round(info.WorkloadHrs, 2);
            log.WL_LOTS = Helper.GetVarchar255(info.LotList.Select(x => x.LotID).Join(","));
            log.REASON = info.Reason;

            log.SELECTABLE_TIME = info.SelectableTime;

            OutputMart.Instance.TOOLING_SELECTION_LOG.Add(log);
        }

        internal static void WriteEqpWorkload(FabSemiconLot lot, FabSemiconEqp eqp, QtCategory ctg, double workload, string type)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_WORKLOAD"))
                return;

            EQP_WORKLOAD log = new EQP_WORKLOAD();
            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;
            log.EVENT_TIME = AoFactory.Current.NowDT;
            log.EVENT_TYPE = type;
            log.EQP_GROUP = eqp.ResGroup;
            log.EQP_ID = eqp.ResID;

            log.CATEGORY = ctg.CategoryHours.ToString();
            
            log.EVENT_WL_HRS = (float)Math.Round(workload, 2);
            log.EVENT_LOT_ID = lot.LotID;

            log.WL_HRS = (float)Math.Round(ctg.WorkloadHours, 2);
            //log.WL_LOTS = ctg.Lots.Keys.Select(x=> x.LotID).Join(",");

            OutputMart.Instance.EQP_WORKLOAD.Add(log);
        }

        internal static void WriteErrorLog(LogType type, string category, FabSemiconLot lot)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("ERROR_LOG"))
                return;

            // TODO: 이런 형태의 Key중복 방지는 개선이 필요함.
            if (OutputMart.Instance.ERROR_LOG.Find(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, category, lot.CurrentPartID, lot.CurrentStepID, "-", lot.LotID) != null)
                return;

            ERROR_LOG log = new ERROR_LOG();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;

            log.LOG_TYPE = type.ToString();
            log.CATEGORY = category;

            log.PART_ID = lot.CurrentPartID;
            log.STEP_ID = lot.CurrentStepID;

            log.RECIPE_ID = "-";
            log.EQP_ID = "-";
            log.EQP_GROUP = "-";
            log.LOT_ID = lot.LotID;

            log.REASON = "Missing Arrange";

            OutputMart.Instance.ERROR_LOG.Add(log);
        }

        internal static void WriteErrorLogWithArrange(LogType type, string category, EqpArrange arr, string reason)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("ERROR_LOG"))
                return;

            if (OutputMart.Instance.ERROR_LOG.Find(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, category, arr.PartID, arr.StepID, arr.EqpID, "-") != null)
                return;

            ERROR_LOG log = new ERROR_LOG();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;

            log.LOG_TYPE = type.ToString();
            log.CATEGORY = category;

            log.PART_ID = arr.PartID;
            log.STEP_ID = arr.StepID;

            log.RECIPE_ID = arr.RecipeID;
            log.EQP_ID = arr.EqpID;
            log.EQP_GROUP = arr.Eqp.ResGroup;
            log.LOT_ID = "-";

            log.REASON = reason;

            OutputMart.Instance.ERROR_LOG.Add(log);
        }

        internal static void WriteErrorLogWithEqp(LogType type, string category, FabAoEquipment eqp, FabSemiconLot lot, string targetStepId, string reason)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("ERROR_LOG"))
                return;

            var partID = lot != null ? lot.FabProduct.PartID : "-";
            var lotID = lot != null ? lot.LotID : "-";

            if (OutputMart.Instance.ERROR_LOG.Find(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, category, partID, targetStepId, eqp.EqpID, lotID) != null)
                return;

            ERROR_LOG log = new ERROR_LOG();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;

            log.LOG_TYPE = type.ToString();
            log.CATEGORY = category;

            log.PART_ID = partID;
            log.STEP_ID = targetStepId;

            log.RECIPE_ID = "-";
            log.EQP_ID = eqp.EqpID;
            log.EQP_GROUP = eqp.Target.ResGroup;
            log.LOT_ID = lotID;

            log.REASON = reason;

            OutputMart.Instance.ERROR_LOG.Add(log);
        }

        internal static void WritePersistLog(LogType type, string category, string data, string reason)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("PERSIST_LOG")) 
                return;

            if (data == null)
                return; // DATA is not nullable

            if (OutputMart.Instance.PERSIST_LOG.Find(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, category, data, reason) != null)
                return;

            PERSIST_LOG log = new PERSIST_LOG();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;

            log.LOG_TYPE = type.ToString();
            log.CATEGORY = category;

            log.DATA = data;
            log.REASON = reason;

            OutputMart.Instance.PERSIST_LOG.Add(log);
        }

        internal static void WriteWipLog(LogType type, string category, FabSemiconLot lot, DateTime eventTime, string reason, int logSeq = 1, FabSemiconStep step = null)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("WIP_LOG"))
                return;

            WIP_LOG log = new WIP_LOG();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;

            log.LOG_TYPE = type.ToString();
            log.CATEGORY = category;
            log.EVENT_TIME = eventTime;

            log.LINE_ID = lot.FabProduct.LineID;
            log.LOT_ID = lot.LotID;
            log.PART_ID = lot.FabProduct.PartID;
            log.PRODUCT_ID = lot.FabProduct.StdProductID;
            log.MFG_PART_ID = lot.FabProduct.ProductID;
            log.STEP_ID = step != null ? step.StepID : lot.CurrentStepID;
            log.ROUTE_ID = step != null ? step.RouteID : lot.CurrentProcessID;
            log.WAFER_QTY = lot.UnitQty;

            log.REASON = reason;
            log.LOG_SEQ = logSeq;

            OutputMart.Instance.WIP_LOG.Add(log);
        }

        internal static void UpdateCurrentWipLogSeq(DateTime now)
        {
            if (InputMart.Instance.CurrentWipLogSeq != null && InputMart.Instance.CurrentWipLogSeq.Item1 == now)
                InputMart.Instance.CurrentWipLogSeq = new Tuple<DateTime, int>(now, InputMart.Instance.CurrentWipLogSeq.Item2 + 1);
            else
                InputMart.Instance.CurrentWipLogSeq = new Tuple<DateTime, int>(now, 1);
        }


        public static void WriteExceptionLog(this Exception e, string callerName, string equipId, string wips)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("EXCEPTION_LOG"))
                return;

            string stackTrace = e.StackTrace;
            if (callerName == "DB DownLoad" || callerName == "DB UpLoad")
                stackTrace = equipId + " - " + e.StackTrace;

            if (OutputMart.Instance.EXCEPTION_LOG.Find(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, stackTrace) != null)
                return;

            EXCEPTION_LOG log = new EXCEPTION_LOG();

            DateTime eventTime = ModelContext.Current.StartTime;
            if (AoFactory.Current == null)
            {
                var eqps = InputMart.Instance.FabSemiconEqp;
                if (eqps != null && eqps.Rows.Count > 0 && eqps.Rows.First().SimObject != null)
                    eventTime = eqps.Rows.First().SimObject.NowDT;
            }
            else
                eventTime = AoFactory.Current.NowDT;

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;
            log.EVENT_TIME = eventTime;
            log.CALLER_METHOD = callerName;
            log.EXCEPTION_METHOD = (e.TargetSite != null) ? e.TargetSite.Name : string.Empty;
            log.EQP_ID = equipId;
            log.EXCEPTION_TYPE = e.GetType().FullName;
            log.EXCEPTION_IDENTITY = wips ?? string.Empty;
            log.EXCEPTION_MESSAGE = e.Message;
            log.STACK_TRACE = stackTrace.IsNullOrEmpty() ? "StackTraceIsNull_" + OutputMart.Instance.EXCEPTION_LOG.TotalCount.ToString() : stackTrace;

            AddExceptionLog(log, e, false);
        }

        private static void AddExceptionLog(Outputs.EXCEPTION_LOG log, Exception e, bool writeMsgInLogger)
        {
            if (writeMsgInLogger && log.EXCEPTION_MESSAGE != null)
                Logger.MonitorInfo(string.Format("Exception Occurred - {0}", log.EXCEPTION_MESSAGE));

            if (e != null && e.TargetSite != null)
                Logger.MonitorInfo(string.Format("Exception Occurred - {0}", e.TargetSite.Name));

            OutputMart.Instance.EXCEPTION_LOG.Add(log);
        }

        internal static void WriteStackResult(FabSemiconLot lot)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STACK_RESULT"))
                return;

            string resGroup = lot.CurrentFabPlan.LoadedResource.ResGroup;
            string stackEqpGroup = Helper.GetConfig(ArgsGroup.Logic_Photo).stackEqpGroup;
            if (lot.CurrentActiveStackInfo == null && (stackEqpGroup != null && !stackEqpGroup.Contains(resGroup)))
                return;

            var row = new STACK_RESULT();

            row.SCENARIO_ID = InputMart.Instance.GlobalParameters.scenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.LOT_ID = lot.LotID;
            row.PART_ID = lot.FabProduct.PartID;
            row.STEP_ID = lot.CurrentStepID;
            var currentFabStep = lot.CurrentStep as FabSemiconStep;
            row.LAYER_ID = currentFabStep.LayerID;
            row.WAFER_QTY = lot.UnitQty;
            row.EQP_ID = lot.CurrentFabPlan.EqpID;
            row.ARRIVAL_TIME = lot.DispatchInTime;
            row.START_TIME = lot.CurrentPlan.StartTime;

            var arr = lot.CurrentFabPlan.Arrange;

            // BOH RUN 에 대해서도 Arrange 등록하도록 개선함.
            // BackupArrange의 경우에도 ON_TRACK_IN0 에서 세팅함.
            // 그럼에도 arr가 null인 경우는 Arrange 에 없는 설비로 투입된 BohRUN

            row.END_TIME = arr == null ? row.START_TIME : lot.CurrentPlan.StartTime.AddSeconds(arr.ProcTime.TactTime.TotalSeconds * lot.UnitQty);
            row.STACK_TYPE = lot.CurrentActiveStackInfo != null ? lot.CurrentActiveStackInfo.StackStepInfo.StackType.ToString() : "D";
            row.STACK_GROUP = lot.CurrentActiveStackInfo != null ? lot.CurrentActiveStackInfo.StackGroupID : "";

            OutputMart.Instance.STACK_RESULT.Add(row);
        }


        internal static void WriteFabOutResult(FabSemiconLot lot, DateTime now)
        {
            if (Helper.GetConfig(ArgsGroup.Simulation_Run).modules == 1)
            {
                // Forward Only의 경우, Demand가 있으면 Forward Pegging 수행

                PegHelper.DoForwardPeggingWithDemand(lot);
            }

            if (lot.ForwardPegInfoList.IsNullOrEmpty())
            {
                var fhb = lot as FabHandlingBatch;
                if (fhb == null)
                {
                    WriteFabOutResultRow(lot, now, null);
                }
                else
                {
                    fhb.mergedContents.ForEach(x => WriteFabOutResultRow(x, now, null));
                }
            }
            else
            {
                foreach (var pegInfo in lot.ForwardPegInfoList)
                {
                    WriteFabOutResultRow(lot, now, pegInfo);
                }
            }
        }

        private static void WriteFabOutResultRow(FabSemiconLot lot, DateTime now, ForwardPegInfo info)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("FAB_OUT_RESULT"))
                return;

            var row = new Outputs.FAB_OUT_RESULT();

            row.SCENARIO_ID = InputMart.Instance.GlobalParameters.scenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.LOT_ID = lot.LotID;
            row.PRODUCT_ID = lot.Product.StdProductID;
            row.PART_ID = lot.FabProduct.PartID;

            row.WAFER_QTY = lot.UnitQty;

            //TODO: BOM이 있을 경우, 완제품의 FabInTime 처리 개선 필요.
            row.FAB_IN_TIME = lot.FabWipInfo.FabInTime.GetMSSqlDateTime();

            if (Helper.GetConfig(ArgsGroup.Simulation_Run).modules == 1)
            {
                var info2 = info as FabForwardPegInfo;
                row.DUE_DATE = info2 != null ? info2.Demand.DueDate : lot.FabWipInfo.DueDate;
                row.DEMAND_ID = info2 != null ? info2.Demand.DemandID : "-";
                row.PEG_QTY = info2 != null ? (float)info2.PegQty : 0;
            }
            else
            {
                row.DUE_DATE = info != null ? info.StepTarget.DueDate : lot.FabWipInfo.DueDate;
                row.DEMAND_ID = info != null ? (info.StepTarget as FabSemiconStepTarget).Mo.DemandID : "-";
                row.PEG_QTY = info != null ? (float)info.Qty : 0;
            }

            if (now < DateTime.MaxValue)
            {
                row.FAB_OUT_TIME = now.GetMSSqlDateTime();
                if (lot.FabWipInfo.FabInTime > DateTime.MinValue)
                    row.CT_DAYS = Convert.ToSingle((row.FAB_OUT_TIME - row.FAB_IN_TIME).TotalDays);
            }
            else
            {
                row.STEP_ID = lot.CurrentStepID;
                row.FAB_OUT_TIME = DateTime.MinValue.GetMSSqlDateTime();
            }

            OutputMart.Instance.FAB_OUT_RESULT.Add(row);
        }

        internal static void WriteQualityLoss(FabSemiconLot lot, FabSemiconLot content, FabSemiconEqp eqp, QualityLossType type, 
            Tuple<double, double, double> rateTuple, ReworkInfo reworkInfo = null)
        {
            var row = new QUALITY_LOSS();

            if (!InputMart.Instance.ExcludeOutputTables.Contains("QUALITY_LOSS"))
            {
                row.SCENARIO_ID = InputMart.Instance.GlobalParameters.scenarioID;
                row.VERSION_NO = ModelContext.Current.VersionNo;

                row.LOT_ID = content.LotID;
                row.PART_ID = lot.CurrentPartID;
                row.STEP_ID = lot.CurrentStepID;

                row.WAFER_QTY = content.UnitQty;
                row.EQP_ID = lot.CurrentFabPlan.EqpID;
                row.START_TIME = lot.CurrentFabPlan.StartTime;
                row.END_TIME = lot.CurrentFabPlan.EndTime;
                row.LOSS_REASON = type.ToString();
                row.RULE_DESCRIPTION = GetRuleDescription(rateTuple, type, reworkInfo);
                row.LOSS_HRS = GetLossHours(lot, content.UnitQty, row.EQP_ID);

                OutputMart.Instance.QUALITY_LOSS.Add(row);
            }

            CollectPeriodicData(lot, eqp, type, row);

            static string GetRuleDescription(Tuple<double, double, double> rateTuple, QualityLossType type, ReworkInfo info)
            {
                bool isYield = type == QualityLossType.STEP_YIELD_SCRAP;

                StringBuilder sb = new StringBuilder();

                if (rateTuple != null)
                {
                    sb.Append("lossRate=");
                    sb.Append(Math.Round(rateTuple.Item1, 4));

                    if (isYield)
                    {
                        sb.Append("(");
                        sb.Append("eqpYield=");
                        sb.Append(Math.Round(rateTuple.Item2, 4));
                        sb.Append(",");
                        sb.Append("stepYield=");
                        sb.Append(Math.Round(rateTuple.Item3, 4));
                        sb.Append(")");
                    }
                }

                if (info != null && isYield == false)
                {
                    if (rateTuple != null)
                        sb.Append(";");

                    sb.Append("Rework " + info.ProcessingType.ToString());
                }

                return sb.ToString();
            }

            static float GetLossHours(FabSemiconLot lot, int lossQty, string eqpID)
            {
                if (lot.FabWipInfo.IsBohRun && lot.CurrentFabPlan.EndTime <= ModelContext.Current.StartTime)
                    return 0;

                var lossHours = lot.CurrentFabStep.RunCT.TotalHours * (lossQty / InputMart.Instance.LotSize);
                var eqp = ResourceHelper.GetEqp(eqpID);

                if (eqp != null)
                {
                    var procTime = ResourceHelper.GetProcessTime(eqp.SimObject, lot);

                    var unitTact = procTime.TactTime.TotalHours;
                    if (eqp.SimObject.IsBatchType())
                        unitTact = unitTact / lot.CurrentFabPlan.Arrange.BatchSpec.MaxWafer;

                    if (eqp.SimObject.IsParallelChamber)
                        unitTact = unitTact / eqp.SubEqpCount;

                    if (eqp.SimType == SimEqpType.UnitBatch)
                    {
                        // ## LotBatch의 경우 loss Hr 계산시 MaxWafer 대비 loss 비율을 측정하는게 의미가 있어보이지만, 
                        // UnitBatch의 경우는 필요 Port수에 따라 unitTact을 증가시킨 상태이고, 해당하는 분량 1회 전체를 손해보는 셈.
                        lossQty = 1;

                        if (eqp.UnitBatchInfo.HasFinitePort == false) // Infinite Capa
                            unitTact = 0; // 설비 입장에서는 Loss 없는 셈.
                        else
                            unitTact = unitTact / (double)eqp.UnitBatchInfo.MaxPortCount;
                    }

                    lossHours = unitTact * lossQty;
                }

                return (float)lossHours;
            }

            static void CollectPeriodicData(FabSemiconLot lot, FabSemiconEqp eqp, QualityLossType type, QUALITY_LOSS row)
            {
                if (lot.CurrentFabStep.AreaID == Helper.GetConfig(ArgsGroup.Simulation_Report).photoArea)
                {
                    var periodicObj = StatisticHelper.GetOrAddPeriodicObject(lot, eqp);
                    if (periodicObj == null)
                        return;

                    if (type == QualityLossType.STEP_YIELD_SCRAP)
                    {
                        periodicObj.ScrapMin += row.LOSS_HRS * 60;
                        periodicObj.ScrapCnt++;
                    }
                    else if (type == QualityLossType.STEP_REWORK || type == QualityLossType.BOH_REWORK || type == QualityLossType.EQP_REWORK)
                    {
                        periodicObj.ReworkMin += row.LOSS_HRS * 60;
                        periodicObj.ReworkCnt++;
                    }
                }
            }
        }

        internal static void WriteArrangeLog(EqpArrange arr, DateTime eventTime, string eventName, bool isAdd)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("ARRANGE_LOG"))
                return;

            ARRANGE_LOG row = new ARRANGE_LOG();
            row.SCENARIO_ID = InputMart.Instance.GlobalParameters.scenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            row.CATEGORY = isAdd ? "ARRANGE_ADD" : "ARRANGE_EXCEPT";
            row.EVENT_TIME = eventTime;
            row.EVENT_NAME = eventName;

            row.LINE_ID = arr.LineID;
            row.PART_ID = arr.PartID;
            row.STEP_ID = arr.StepID;
            row.EQP_ID = arr.EqpID;

            OutputMart.Instance.ARRANGE_LOG.Add(row);
        }

        internal static void WriteEqpPlanHB(FabHandlingBatch fhb, FabSemiconLot lot, AoEquipment aeqp)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_PLAN"))
                return;

            var row = new EQP_PLAN();
            // PK
            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.EQP_ID = "-"; // default
            row.LOT_ID = lot.LotID;
            row.ROUTE_ID = fhb.Route.RouteID;

            if (lot.CurrentRework != null && lot.CurrentRework.Info.ProcessingType == ReworkProcessingType.Step)
                row.ROUTE_ID = Helper.CreateKey(fhb.Route.RouteID, "REWORK");

            row.STEP_SEQ = fhb.CurrentStep.Sequence;
            row.STEP_ID = fhb.CurrentStepID;
            row.ARRIVAL_TIME = fhb.DispatchInTime;
            // 
          
            lot.CurrentFabPlan.Row = row;

            row.LINE_ID = fhb.LineID;

            row.START_TIME = fhb.CurrentFabPlan.StartTime;
            row.END_TIME = AoFactory.Current.NowDT;
            row.PART_ID = lot.FabProduct.PartID;
            row.WAFER_QTY = lot.UnitQty;
            row.LOT_PRIORITY = lot.FabWipInfo.LotPriorityStatus;

            var eqp = aeqp != null ? aeqp.Target as FabSemiconEqp : null;
            if (eqp != null)
            {
                row.EQP_ID = eqp.ResID;
                row.AREA_ID = eqp.StepGroup;
                row.EQP_GROUP = eqp.ResGroup;
            }

            var arr = fhb.CurrentFabPlan.Arrange ?? 
                InputMart.Instance.EqpArrangePartStepEqpView.FindRows(fhb.FabProduct.PartID, fhb.CurrentStepID, row.EQP_ID).FirstOrDefault();

            if (arr != null)
            {
                row.RECIPE_ID = arr.RecipeID;
                row.TOOLING_ID = fhb.CurrentFabPlan.ToolID;
                row.SIM_TYPE = arr.Eqp.SimType.ToString();
            }

            OutputMart.Instance.EQP_PLAN.Add(row);
        }

        public static void UpdateEqpPlanOnDoneFactory(ISimEntity item)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_PLAN"))
                return;

            var fhb = item as FabHandlingBatch;

            if (fhb != null && fhb.CurrentFabStep.IsSimulationStep && fhb.CurrentState == EntityState.RUN)
            {
                foreach (var lot in fhb.mergedContents)
                {
                    var row = new EQP_PLAN();

                    row.SCENARIO_ID = InputMart.Instance.ScenarioID;

                    row.VERSION_NO = ModelContext.Current.VersionNo;  
                    
                    row.LOT_ID = lot.LotID;
                    row.ROUTE_ID = fhb.Route.RouteID;

                    if (lot.CurrentRework != null && lot.CurrentRework.Info.ProcessingType == ReworkProcessingType.Step)
                        row.ROUTE_ID = Helper.CreateKey(fhb.Route.RouteID, "REWORK");

                    row.STEP_SEQ = fhb.CurrentStep.Sequence;
                    row.STEP_ID = fhb.CurrentStepID;
                    row.EQP_ID = fhb.CurrentFabPlan.EqpID;
                    row.AREA_ID = lot.CurrentFabStep.AreaID;
                    row.LINE_ID = fhb.LineID;

                    row.ARRIVAL_TIME = DateTime.Parse(fhb.CurrentFabPlan.ArrivalTimeStr);

                    if (fhb.CurrentFabPlan.EqpID == "-")
                        row.START_TIME = row.ARRIVAL_TIME;
                    else
                        row.START_TIME = fhb.CurrentFabPlan.StartTime;

                    row.END_TIME = new DateTime();

                    row.PART_ID = lot.FabProduct.PartID;
                    row.WAFER_QTY = lot.UnitQty;
                    row.LOT_PRIORITY = lot.FabWipInfo.LotPriorityStatus;

                    var eqp = lot.CurrentFabPlan.Arrange?.Eqp;

                    if (eqp != null && eqp.ResGroup != null)
                        row.EQP_GROUP = eqp.ResGroup;
                    else
                        row.EQP_GROUP = "-";

                    var arr = fhb.CurrentFabPlan.Arrange ??
                        InputMart.Instance.EqpArrangePartStepEqpView.FindRows(fhb.FabProduct.PartID, fhb.CurrentStepID, row.EQP_ID).FirstOrDefault();

                    if (arr != null)
                    {
                        row.RECIPE_ID = arr.RecipeID;
                        row.TOOLING_ID = fhb.CurrentFabPlan.ToolID;
                        row.SIM_TYPE = arr.Eqp.SimType.ToString();
                    }

                    OutputMart.Instance.EQP_PLAN.Add(row);
                }
            }
        }
    }
}