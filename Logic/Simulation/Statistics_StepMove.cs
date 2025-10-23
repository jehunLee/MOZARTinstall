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
    public partial class Statistics_StepMove
    {
        public STEP_MOVE GET_ROW(Mozart.SeePlan.StatModel.StatSheet<STEP_MOVE> sheet, Mozart.Simulation.Engine.ISimEntity entity, bool isTrackIn)
        {
            if (AoFactory.Current.NowDT == ModelContext.Current.EndTime)
                return null;

            var hb = entity as IHandlingBatch;
            var lot = hb.Sample as FabSemiconLot;
            var eqp = lot.CurrentFabPlan.LoadedResource as FabSemiconEqp;

            if (lot.CurrentFabStep.IsStatisticalAnalysisStep() == false)
                return null;

            //if (eqp == null) // BOH RUN case
            //    return null;

            // FabIn 은 날짜가 당겨지는 문제가 있어서 1초 차감하지 않도록 수정함. -> 철회
            //var targetDate = lot.CurrentStepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID ?
            //    Helper.GetTargetDate(AoFactory.Current.NowDT, false) : Helper.GetTargetDate(AoFactory.Current.NowDT, true);

            //TODO:
            // 현재 Statistics 구조상, Rolling 경계에 속할 경우 이전 구간에 포함해야만 키중복을 피할 수 있음.
            // 결국에는 경계 이전, 이후 선택이 지원되어야 할 것으로 생각됨
            // 라이브러리 개선 요청을 하던지, STEP_MOVE 집계를 따로 구현하던지 의사결정이 필요
            // fabInStepID에 대한 보정처리는 필요시 일단 WebUI에서 처리하는 것으로 정리.
            var targetDate = Helper.GetTargetDate(AoFactory.Current.NowDT, true);

            CollectFabInOutMove(lot, targetDate);

            if (InputMart.Instance.ExcludeOutputTables.Contains("STEP_MOVE"))
                return null;

            var resId = eqp != null ? eqp.ResID : "-";
            var row = sheet.GetRow(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, targetDate, lot.FabProduct.PartID, lot.CurrentStepID, resId);

            var currentFabStep = lot.CurrentStep as FabSemiconStep;
            row.AREA_ID = currentFabStep.AreaID; // eqp != null ? eqp.StepGroup : "F1-PC";
            row.LAYER_ID = currentFabStep.LayerID;
            row.STEP_SEQ = currentFabStep.Sequence;

            row.TARGET_WEEK = Helper.GetTargetWeek(targetDate);

            row.LINE_ID = lot.LineID;

            return row;

            static void CollectFabInOutMove(FabSemiconLot lot, DateTime targetDate)
            {
                var startEnd = lot.CurrentStepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID ? "start" :
                    lot.CurrentStepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabOutStepID ? "end" : null;

                if (startEnd != null)
                {
                    if (InputMart.Instance.FabInOutInfo.TryGetValue(lot.CurrentPartID, targetDate, out FabInOutInfo info) == false)
                    {
                        info = new FabInOutInfo();
                        info.TARGET_DATE = targetDate;
                        info.TARGET_WEEK = Helper.GetFormattedTargetWeek(targetDate);
                        info.TARGET_MONTH = Helper.GetFormattedTargetMonth(targetDate);
                        info.PART_ID = lot.CurrentPartID;

                        InputMart.Instance.FabInOutInfo.Add(lot.CurrentPartID, targetDate, info);
                    }

                    if (startEnd == "start")
                        info.FABIN_QTY += lot.GetBOMContributionQty();
                    else
                        info.FABOUT_QTY += lot.GetBOMContributionQty();
                }
            }
        }

        public void ON_TRACK_OUT(StatSheet<STEP_MOVE> sheet, ISimEntity entity, STEP_MOVE row)
        {
            // OnEndTask보다 나중에 불림.
            // StepMove 출력 안해도, CycleTime 집계는 하도록, 집계 위치를 OnEndTask로 옮김.
            // StepMove 출력시에 값은 나오도록, 이 위치에서는 업데이트만 수행.
            
            // Periodic CT를 출력하는 시점에는 STEP_MOVE 데이터가 rolling 되어 메모리에서 지워진 이후기 때문에,
            // 비효율적이더라도, 이 시점에 지속적으로 찾아서 업데이트하는 방법을 사용.

            var lot = entity as FabSemiconLot;

            if (BopHelper.IsFabInOrFabOut(row.STEP_ID))
                return;

            UpdateCycleTimeInfo(sheet, row, lot);

            static void UpdateCycleTimeInfo(StatSheet<STEP_MOVE> sheet, STEP_MOVE row, FabSemiconLot lot)
            {
                var plan = lot.CurrentFabPlan;

                if (lot.DispatchInTime >= ModelContext.Current.StartTime && plan.EndTime > ModelContext.Current.StartTime)
                {
                    var arr = plan.Arrange;
                    var eqp = arr != null ? arr.Eqp : null;

                    // Periodic CT를 Weekly/Monthly로 집계시에도, STEP_MOVE 기록을 위해서는 Daily로 쪼개서 가지고 있어야 함.
                    var periodicObject = StatisticHelper.GetOrAddPeriodicObject(lot, eqp);
                    if (periodicObject != null)
                    {
                        row.RUN_HOUR = periodicObject.RunTAT;
                        row.WAIT_HOUR = periodicObject.WaitTAT;
                    }
                }
            }
        }
    }
}