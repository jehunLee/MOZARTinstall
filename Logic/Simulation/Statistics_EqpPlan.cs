using Mozart.Simulation.Engine;
using Mozart.SeePlan.StatModel;
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

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class Statistics_EqpPlan
    {
        public void ON_TRACK_IN(Mozart.SeePlan.StatModel.StatSheet<EQP_PLAN> sheet, Mozart.Simulation.Engine.ISimEntity entity, EQP_PLAN row)
        {
            var lot = entity as FabSemiconLot;
            var eqp = InputMart.Instance.FabSemiconEqpView.FindRows(row.EQP_ID).FirstOrDefault();

            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;

            row.START_TIME = sheet.NowDT;

            row.PART_ID = lot.FabProduct.PartID;
            row.WAFER_QTY = lot.UnitQty;
            row.STEP_SEQ = lot.CurrentStep.Sequence;
            row.LOT_PRIORITY = lot.FabWipInfo.LotPriorityStatus;

            row.ARRIVAL_TIME = lot.DispatchInTime;

            if (eqp != null)
            {
                row.AREA_ID = eqp.StepGroup;
                row.EQP_GROUP = eqp.ResGroup;
                row.SIM_TYPE = eqp.SimType.ToString();
            }
            else
            {
                row.SIM_TYPE = "Bucketing";

                if (lot.CurrentFabStep.CapaInfo != null)
                {
                    row.AREA_ID = lot.CurrentFabStep.CapaInfo.CapaKey;
                    row.EQP_GROUP = lot.CurrentFabStep.CapaInfo.CapaKey;
                    row.EQP_ID = lot.CurrentFabStep.CapaInfo.CapaKey + "@" + lot.CurrentFabStep.CapaInfo.RollingIndex;
                }
            }

            var arr = lot.CurrentFabPlan.Arrange ?? InputMart.Instance.EqpArrangePartStepEqpView.FindRows(lot.FabProduct.PartID, lot.CurrentStepID, row.EQP_ID).FirstOrDefault();
            if (arr != null)
            {
                row.RECIPE_ID = arr.RecipeID;
                row.TOOLING_ID = lot.CurrentFabPlan.ToolID;
            }
            
            if (lot.IsWipHandle)
            {
                if(lot.FabWipInfo.InitialEqp != null && lot.FabWipInfo.WipState != "WAIT")
                    row.IS_INIT_RUN = lot.FabWipInfo.WipState;

                if (lot.FabWipInfo.WipState == "RUN")
                    row.START_TIME = lot.FabWipInfo.WipStateTime;
            }

            if (lot.CurrentRework != null && lot.CurrentRework.Info.ProcessingType == ReworkProcessingType.Step)
            {
                row.ROUTE_ID = Helper.CreateKey(lot.Route.RouteID, "REWORK");
            }
        }

        public void ON_TRACK_OUT(StatSheet<EQP_PLAN> sheet, ISimEntity entity, EQP_PLAN row)
        {
            row.END_TIME = sheet.NowDT;

#if false
            var lot = entity as FabSemiconLot;
            if ((lot.Process as FabSemiconProcess).RouteType == "REWORK")
                return;

            if (row.STEP_ID == InputMart.Instance.GlobalParameters.StartOperation || row.STEP_ID == InputMart.Instance.GlobalParameters.EndOperation)
                return;

            if (row.ARRIVAL_TIME >= ModelContext.Current.StartTime && row.END_TIME > ModelContext.Current.StartTime)
            {
                double runHr = (row.END_TIME - row.START_TIME).TotalHours;
                var arr = lot.CurrentFabPlan.Arrange;
                if (arr == null)
                    return;

                if (InputMart.Instance.IsSimulatorMode)
                {
                    double tact, flow;
                    if (arr.Eqp.OrgSimType == "LotBatch" || arr.Eqp.OrgSimType == "BatchInline")
                    {
                        runHr = arr.OrgFlow;
                    }
                    else
                    {
                        tact = arr.ProcTime.TactTime.TotalSeconds / arr.Eqp.Utilization;
                        flow = arr.ProcTime.FlowTime.TotalSeconds;

                        double unitQty = Constants.UNIT_QTY;

                        runHr = (((unitQty - 1) * tact) + flow) / 3600;
                    }
                }

                double waitHr = (row.START_TIME - row.ARRIVAL_TIME).TotalHours;

                var targetDate = Helper.GetTargetDate(sheet.NowDT, true);

                bool isPhotoArea = row.AREA_ID == InputMart.Instance.GlobalParameters.PhotoArea;
                string photoGen = null;
                if (isPhotoArea)
                {
                    var attr = lot.CurrentAttribute;
                    if (attr != null)
                        photoGen = attr.PhotoGen;
                }

                var stepKey = Helper.CreateKey(targetDate.ToShortDateString(), row.PART_ID, row.STEP_ID);
                AddCycleTimeInfo(row, targetDate, runHr, waitHr, stepKey, InputMart.Instance.CycleTimeInfoByStep, photoGen);

                var eqpKey = Helper.CreateKey(targetDate.ToShortDateString(), row.PART_ID, row.STEP_ID, arr.Eqp.ResID);
                AddCycleTimeInfo(row, targetDate, runHr, waitHr, eqpKey, InputMart.Instance.CycleTimeInfoByEqp, photoGen);
            }

            static void AddCycleTimeInfo(EQP_PLAN row, DateTime targetDate, double runHr, double waitHr, string key,
                Dictionary<string, CycleTimeInfo> dict, string photoGen)
            {
                if (dict.TryGetValue(key, out CycleTimeInfo info))
                {
                    info.RunHourTotal += runHr;
                    info.WaitHourTotal += waitHr;
                    info.Count++;
                    info.MoveQty += row.WAFER_QTY;
                }
                else
                {
                    info = new CycleTimeInfo();
                    info.TargetDate = targetDate;
                    info.TargetWeek = Helper.GetTargetWeek(targetDate);
                    info.MfgProductID = row.PART_ID;
                    info.OperID = row.STEP_ID;
                    info.AreaID = row.AREA_ID;
                    info.RunHourTotal = runHr;
                    info.WaitHourTotal = waitHr;
                    info.Count = 1;
                    //info.SimType = arr.Eqp.OrgSimType;
                    info.ResID = row.EQP_ID;
                    info.IsPhotoArea = photoGen != null;
                    info.PhotoGen = photoGen;
                    info.MoveQty = row.WAFER_QTY;

                    dict.Add(key, info);
                }
            } 
#endif
        }

        public EQP_PLAN GET_ROW(StatSheet<EQP_PLAN> sheet, ISimEntity entity)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_PLAN"))
                return null;

            var lot = entity as FabSemiconLot;

            if (lot.CurrentFabStep.IsSimulationStep == false)
                return null;

            var fhb = entity as FabHandlingBatch;
            if (fhb != null)
                return null; // HB에 대해서는 별도 액션에서 출력

            // TODO : 속도 개선 효과 있는지? 확인
            var fplan = lot.CurrentFabPlan;
            var row = fplan.Row;
            if (row == null)
            {
                // STEP_SEQ는 동일 StepId에 대해 Seq로 구분할지 여부에 대한 정책이 확정되면 재검토할 예정
                // 그 전까지는 RouteStep을 Key로 갖는 테이블은 STEP_SEQ도 Key로 세팅함.
                // 다만, STEP_MOVE, STE_WIP과 같이 PartStep을 Key로 가질 경우, Seq는 샘플값이 되므로 Key로 취급하지 않음.

                fplan.Row = row = sheet.GetRow(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo,
                    lot.CurrentFabPlan.EqpID, lot.LotID, lot.CurrentProcessID, lot.CurrentStep.Sequence, lot.CurrentStepID, lot.DispatchInTime);
            }
            else
            {
                fplan.Row = null;
            }
            return row;
        }
    }

}

namespace FabSimulator.DataModel
{
    partial class FabPlanInfo
    {
        public EQP_PLAN Row;
    }
}