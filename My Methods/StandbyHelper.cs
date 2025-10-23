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
using System.Text;
using Mozart.SeePlan.Simulation;
using System.Data.SqlTypes;
using Mozart.SeePlan.DataModel;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class StandbyHelper
    {
        public static void AddIdleReason(FabAoEquipment aeqp, string status, string idleCode,
            DateTime startTime, DateTime endTime, FabSemiconLot lot, string desc = "")
        {
            if (InputMart.Instance.StandbyTimeOutputs[aeqp].Any(x => x.START_TIME == startTime))
                return;

            STANDBY_TIME row = new STANDBY_TIME();
            row.AREA_ID = aeqp.Eqp.StepGroup;
            row.SIM_TYPE = aeqp.Eqp.SimType.ToString();
            row.EQP_ID = aeqp.EqpID;
            row.STATUS = status;
            row.IDLE_CODE = idleCode;
            row.START_TIME = startTime.GetMSSqlDateTime();
            row.END_TIME = endTime.GetMSSqlDateTime();
            row.LOT_ID = lot != null ? lot.LotID : string.Empty;
            row.STEP_ID = lot != null ? lot.CurrentStepID : string.Empty;
            row.DESCRIPTION = desc;

            var di = aeqp.DispatchingAgent.GetDestination(aeqp.EqpID);
            if (di != null)
            {
                string details = string.Empty;

                StringBuilder sb = new StringBuilder();
                foreach (FabSemiconLot l in di.Queue)
                {
                    sb.Append(string.Join("/", l.LotID, l.CurrentProductID, l.CurrentStepID, l.UnitQty.ToString()));
                    sb.Append(",");
                }
                details = sb.ToString();

                //foreach (FabSemiconLot l in di.Queue)
                //    details += l.LotID + "/" + l.CurrentProductID + "/" + l.CurrentStepID + "/" + l.UnitQty.ToString() + ",";

                if (string.IsNullOrEmpty(details) == false)
                    row.DETAILS = details.Substring(0, details.Length - 1);
            }

            InputMart.Instance.StandbyTimeOutputs.Add(aeqp, row);
            ClearReasonContext(aeqp);
        }

        public static void InsertStandbyRow(FabAoEquipment feqp, LoadingStates state)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STANDBY_TIME"))
                return;

            var now = feqp.NowDT;
            var status = state.ToString();
            string reason = "NoReason"; // state == LoadingStates.IDLE ? "NoReason" : "ClusterIneffceincy";

            if (feqp.Loader.IsBlocked()) // PM 발생예정으로 LoadStat에서 IDLERUN으로 집계하지 않고 있기 때문에 StandbyTime에서도 집계에서 제외
                return;

            if (feqp.DispatchingAgent.GetDestination(feqp.EqpID).Queue.Count == 0)
            {
                AddIdleReason(feqp, status, "NoWip", feqp.NowDT, DateTime.MaxValue, null, "No Product Scheduled At Step");
                return;
            }

            var wips = feqp.GetWaitingWips2();

            if (feqp.Eqp.ToolingInfo.IsNeedReticle)
            {
                feqp.Eqp.ToolingInfo.SelectableReticleList.Clear();

                var reticleInfos = ResourceHelper.GetReticleSelectionInfos(feqp, wips);
                ResourceHelper.SetSelectableReticleList(feqp, reticleInfos);

                if (feqp.Eqp.ToolingInfo.SelectableReticleList.IsNullOrEmpty())
                {
                    ToolingSelectionInfo info = reticleInfos.IsNullOrEmpty() ? null : reticleInfos.First();
                    AddIdleReason(feqp, state.ToString(), "WaitForReticle", now, DateTime.MaxValue, info?.Lot, info?.Tooling.ToolingID);
                    return;
                }
            }

            string desc = string.Empty;
            if (feqp.IsWaitingFS)
                desc = "Force Standby";

            AddIdleReason(feqp, state.ToString(), reason, feqp.NowDT, DateTime.MaxValue, null, desc);
        }

        public static IList<IHandlingBatch> GetWaitingWips2(this FabAoEquipment aeqp)
        {
            DispatchingInfo info = GetDispatchingInfo(aeqp);
            if (info == null)
                return null;

            var list = new List<IHandlingBatch>();
            info.Queue.ForEach(x => list.Add(x as IHandlingBatch));
            return list;
        }

        public static DispatchingInfo GetDispatchingInfo(FabAoEquipment aeqp)
        {
            if (aeqp.DispatchingInfo == null)
                aeqp.DispatchingInfo = aeqp.DispatchingAgent.GetDestination(aeqp.EqpID);

            return aeqp.DispatchingInfo;
        }

        public static void UpdateEndTimeofPrevRow(FabAoEquipment feqp, DateTime endTime)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STANDBY_TIME"))
                return;

            STANDBY_TIME delRow = null;

            ICollection<STANDBY_TIME> rows = null;
            if (InputMart.Instance.StandbyTimeOutputs.TryGetValue(feqp, out rows))
            {
                foreach (var row in rows)
                {
                    if (row.START_TIME == endTime)
                        delRow = row;

                    var properEndTime = row.START_TIME > endTime ? feqp.NowDT : endTime;

                    if (row.END_TIME == SqlDateTime.MaxValue && row.START_TIME != properEndTime)
                    {
                        row.END_TIME = properEndTime;

                        if (feqp.IsWaitingFS)
                            row.DESCRIPTION = "Force Standby";
                    }
                }

                rows.Remove(delRow);
            }
        }

        public static void ClearReasonContext(FabAoEquipment feqp)
        {
            //feqp.GermHoldSample = null;
            //feqp.StackingSample = null;
        }

        public static void InsertIdleRow(FabAoEquipment feqp)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STANDBY_TIME"))
                return;

            if (feqp.Eqp.WriteStandbyTime == false)
                return;

            InsertStandbyRow(feqp, LoadingStates.IDLE);
        }
    }
}