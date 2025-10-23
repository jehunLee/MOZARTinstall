using Mozart.Task.Execution.Persists;
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
using System.Diagnostics;
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using Mozart.SeePlan;
using Mozart.Data.Entity;

namespace FabSimulator.Logic
{
    [FeatureBind()]
    public partial class PersistInputs
    {
        public bool OnAfterLoad_PRODUCT(PRODUCT entity)
        {
            try
            {
                FabProduct mfgPart = CreateHelper.CreateMfgPart(entity);
                
                StdProduct stdProd = BopHelper.GetOrAddStdProduct(mfgPart, entity);

                if (mfgPart.Process == null || stdProd == null)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "PRODUCT", entity.MFG_PART_ID, "Missing Route");

                    return false;
                }

                // TODO: PartID에 해당하는 StdRoute도 하나일 것으로 가정되어 있는데, BOM 구조와 맞지 않을 수 있음.
                if (InputMart.Instance.PartRouteMap.ContainsKey(entity.PART_ID) == false)
                    InputMart.Instance.PartRouteMap.Add(entity.PART_ID, stdProd.Route);

                InputMart.Instance.FabProduct.ImportRow(mfgPart);

                return true;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public bool OnAfterLoad_ROUTE(ROUTE entity)
        {
            try
            {
                FabSemiconProcess proc = CreateHelper.CreateFabSemiconProcess(entity);

                List<SemiconStep> steps = BopHelper.GetRouteSteps(proc);
                if (steps.IsNullOrEmpty())
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "ROUTE", entity.ROUTE_ID, "Missing RouteStep");

                    return false;
                }

                BopHelper.BuildBop(proc, steps);

                InputMart.Instance.FabSemiconProcess.ImportRow(proc);

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public void OnAction_EQP(IPersistContext context)
        {
            //Frame설비의 PARENT_EQP_ID 값이 string.Empty가 되어야 Sub보다 먼저 로딩되서 정상 동작함.
            foreach (var entity in InputMart.Instance.EQP.Rows.OrderBy(x => x.PARENT_EQP_ID))
            {
                try
                {
                    FabSemiconEqp parent = ResourceHelper.GetEqp(entity.PARENT_EQP_ID);
                    if (parent != null)
                    {
                        CreateHelper.CreateSubEqp(entity, parent);
                        continue;
                    }

                    FabSemiconEqp eqp = CreateHelper.CreateEqp(entity);
                }
                catch (Exception e)
                {
                    string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                    e.WriteExceptionLog(methodname, "-", "Input Persist Error in Loop");
                }
            }
        }

        public void OnAction_ARRANGE(IPersistContext context)
        {
            List<ARRANGE> subArranges = new List<ARRANGE>();
            foreach (var group in InputMart.Instance.ARRANGE.Rows.GroupBy(x => new { x.PART_ID, x.STEP_ID }))
            {
                PartStepAttribute attr = ArrangeHelper.GetOrAddPartStepAttribute(group.Key.PART_ID, group.Key.STEP_ID);

                subArranges.Clear();
                foreach (var entity in group)
                {
                    if (entity.START_DATETIME > ModelContext.Current.EndTime || entity.END_DATETIME <= ModelContext.Current.StartTime)
                        continue;

                    var sub = ResourceHelper.GetSubEqp(entity.EQP_ID);
                    if (sub == null)
                    {
                        var arr = ArrangeHelper.CreateEqpArrange(entity);
                        if (arr == null)
                            continue;

                        attr.ArrangeDict.Add(arr.StartTime, arr);
                    }
                    else
                    {
                        subArranges.Add(entity);
                    }
                }

                SetSubArrange(subArranges, attr);

                // Backup Arrange는 Single Arrange에 대해서만 구성. (Arrange 가용시간까지는 고려하지 않음)
                // Backup 사용여부 체크는, DOWN EVENT 발생시점, LOT이 해당 Step에 도착한 시점에 수행.
                if (group.Count() == 1)
                {
                    SetBackupArrange(attr, group.First());
                }
            }

            // STEP_TIME loading 전에 미리 체크 필요함.
            ValidateParallelChamberArrange();

            WriteMissingPartStepArrange();

            static void SetSubArrange(List<ARRANGE> subArranges, PartStepAttribute attr)
            {
                foreach (var entity in subArranges)
                {
                    try
                    {
                        var sub = ResourceHelper.GetSubEqp(entity.EQP_ID);

                        var frameArr = InputMart.Instance.EqpArrangePartStepEqpView.FindRows(entity.PART_ID, entity.STEP_ID, sub.Parent.ResID).FirstOrDefault();
                        if (frameArr == null)
                        {
                            frameArr = ArrangeHelper.CreateEqpArrange(entity, sub.Parent.ResID);

                            // SubArrange의 시간을 각각 다르게 적용하려면 추가구현 필요.
                            attr.ArrangeDict.Add(frameArr.StartTime, frameArr);

                            OutputHelper.WritePersistLog(LogType.WARNING, "ARRANGE", Helper.CreateKey(entity.PART_ID, entity.STEP_ID, sub.Parent.ResID), "Missing Frame Arrange");
                        }

                        if (frameArr != null)
                        {
                            frameArr.SubEqps.Add(sub);
                        }
                            
                    }
                    catch (Exception e)
                    {
                        string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                        e.WriteExceptionLog(methodname, "-", "Input Persist Error in Loop");
                    }
                }
            }

            static void SetBackupArrange(PartStepAttribute attr, ARRANGE arr)
            {
                var eqp = ResourceHelper.GetEqp(arr.EQP_ID);
                if (eqp == null || eqp.BackupEqps.IsNullOrEmpty())
                    return;

                List<EqpArrange> backupArranges = new List<EqpArrange>();
                foreach (var backupEqp in eqp.BackupEqps)
                {
                    var backupArr = CreateHelper.CreateBackupArrange(attr, backupEqp, arr.RECIPE_ID, arr.TOOLING_NAME);

                    backupArranges.Add(backupArr);
                }

                attr.BackupArranges = backupArranges;
            }

            static void ValidateParallelChamberArrange()
            {
                foreach (var arr in InputMart.Instance.EqpArrange.Rows)
                {
                    if (arr.Eqp.OrgSimType == "ParallelChamber")
                    {
                        if (arr.SubEqps.IsNullOrEmpty())
                        {
                            arr.Eqp.SubEqps.ForEach(x => arr.SubEqps.Add(x as FabSemiconSubEqp));

                            OutputHelper.WritePersistLog(LogType.WARNING, "ARRANGE", Helper.CreateKey(arr.PartID, arr.StepID, arr.EqpID), "Missing SubEqp Arranges");
                        }
                    }
                }
            }

            static void WriteMissingPartStepArrange()
            {
                foreach (var attr in InputMart.Instance.PartStepAttribute.Rows)
                {
                    if (attr.MaxStepLevel >= 3)
                        continue;

                    if (attr.ArrangeDict.IsNullOrEmpty())
                        OutputHelper.WritePersistLog(LogType.WARNING, "ARRANGE", Helper.CreateKey(attr.PartID, attr.StepID), "Missing");
                }
            }
        }

        public bool OnAfterLoad_STEP_TIME(STEP_TIME entity)
        {
            try
            {
                var arrange = InputMart.Instance.EqpArrangePartStepEqpView.FindRows(entity.PART_ID, entity.STEP_ID, entity.EQP_ID).FirstOrDefault();
                if (arrange != null)
                    ResourceHelper.SetArrangeProcTime(arrange, entity);
                else
                    OutputHelper.WritePersistLog(LogType.WARNING, "STEP_TIME", Helper.CreateKey(entity.PART_ID, entity.STEP_ID, entity.EQP_ID), "Missing StepTime");

                return true;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public bool OnAfterLoad_WIP(WIP entity)
        {
            try
            {
                if (Helper.GetConfig(ArgsGroup.Lot_Wip).useWIP == "N")
                    return false;

                if (InputMart.Instance.FabWipInfo.ContainsKey(entity.LOT_ID))
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "WIP", entity.LOT_ID, "Duplicated Lot");
                    return false;
                }

                if (entity.WAFER_QTY <= 0)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "WIP", entity.LOT_ID, "WAFER_QTY error");
                    return false;
                }

                FabProduct prod = InputMart.Instance.FabProductMfgPartView.FindRows(entity.MFG_PART_ID).FirstOrDefault();
                if (prod == null)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "WIP", entity.LOT_ID, "Missing Product");
                    return false;
                }

                // 1. LotRoute를 최우선 사용.
                // 2. 없으면 입력된 RouteID로 조회
                // 3. 오입력시에 동작하도록 MfgPart의 Route를 Default로 사용.
                FabSemiconProcess proc = InputMart.Instance.FabSemiconProcessView.FindRows(entity.LOT_ID).FirstOrDefault();
                if (proc == null)
                    proc = InputMart.Instance.FabSemiconProcessView.FindRows(entity.ROUTE_ID).FirstOrDefault();

                if (proc == null)
                    proc = prod.Process as FabSemiconProcess;

                if (proc == null)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "WIP", entity.LOT_ID, "Missing Route");
                    return false;
                }

                FabSemiconStep initialStep = proc.FindStep(entity.STEP_ID) as FabSemiconStep;
                if (initialStep == null && prod.BOMInfo != null)
                {
                    if (entity.STEP_ID == prod.BOMInfo.MergeStep.StepID)
                    {
                        initialStep = prod.BOMInfo.MergeStep;
                        proc = prod.BOMInfo.ToPart.MainRoute;
                    }
                }

                if (initialStep == null)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "WIP", entity.LOT_ID, "Missing StartStep");
                    return false;
                }

                if (proc.RouteType == RouteType.LOT)
                {
                    foreach (FabSemiconStep step in proc.Steps)
                    {
                        BopHelper.SetPartStepAttributes(prod, step);
                    }
                }

                FabWipInfo wip = CreateHelper.CreateFabWipInfo(entity, prod, proc, initialStep);

                InputMart.Instance.FabWipInfo.Add(wip.LotID, wip);

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, entity.EQP_ID, entity.LOT_ID);
                return false;
            }
        }

        public void OnAction_ARRANGE_PARAM(IPersistContext context)
        {
            string prevResource = string.Empty;
            string prevRecipe = string.Empty;
            IEnumerable<EqpArrange> arrs = null;
            foreach (var entity in InputMart.Instance.ARRANGE_PARAM.Rows.OrderBy(x => x.EQP_ID).ThenBy(x => x.RECIPE_ID))
            {
                try
                {
                    if (entity.EQP_ID != prevResource || entity.RECIPE_ID != prevRecipe)
                    {
                        arrs = InputMart.Instance.EqpArrangeEqpRecipeView.FindRows(entity.EQP_ID, entity.RECIPE_ID);
                        prevResource = entity.EQP_ID;
                        prevRecipe = entity.RECIPE_ID;
                    }

                    if (arrs.IsNullOrEmpty())
                    {
                        OutputHelper.WritePersistLog(LogType.WARNING, "ARRANGE_PARAM", entity.EQP_ID + "@" + entity.RECIPE_ID, "Missing Arrange");
                        continue;
                    }

                    foreach (var arr in arrs)
                    {
                        if (arr.BatchSpec == null)
                        {
                            arr.BatchSpec = BatchingHelper.GetDefaultBatchSpec(arr);
                        }

                        if (entity.PARAM_NAME == "min_prod_count")
                            arr.BatchSpec.MinWafer = int.Parse(entity.PARAM_VALUE);

                        if (entity.PARAM_NAME == "max_prod_count")
                            arr.BatchSpec.MaxWafer = int.Parse(entity.PARAM_VALUE);

                        if (entity.PARAM_NAME == "max_lot_count")
                            arr.BatchSpec.MaxLot = int.Parse(entity.PARAM_VALUE);

                        if (entity.PARAM_NAME == "recipe_inhibit" && entity.PARAM_VALUE == "Y")
                        {
                            arr.IsRecipeInhibit = true;
                            arr.StartTime = DateTime.MinValue;
                            arr.EndTime = ModelContext.Current.StartTime;
                        }

                        // 셋업 설정에 대한 식별자로, SWITCH_TIME과 함께 작업 변경 시간을 지정할 수 있습니다.
                        if (entity.PARAM_NAME == "setup_name")
                            arr.SetupName = entity.PARAM_VALUE;

                        if (entity.PARAM_NAME == "min_lots_to_switch")
                            arr.MinLotsToSwitch = int.Parse(entity.PARAM_VALUE);

                        if (entity.PARAM_NAME == "min_wafers_to_switch")
                            arr.MinWafersToSwitch = int.Parse(entity.PARAM_VALUE);

                        if (entity.PARAM_NAME == "min_runs_after_switch")
                            arr.MinRunsAfterSwitch = int.Parse(entity.PARAM_VALUE);


                        //if (entity.PARAM_NAME == "reworkRateStackPm")
                        //{
                        //    if (arr.Eqp.ReworkInfo != null)
                        //    {
                        //        // reworkEffectDaysStackPm 값을 더이상 사용하지 않고, Pre-Inhibit 만큼의 기간동안 적용하도록 로직 변경.
                        //        // 일반적으로 Post-Inhibit 기간이 더 짧으므로, Post-Inhibit이 끝난 이후에도 ReworkEffective 상태를 좀 더 유지하게 됨.

                        //        if (double.TryParse(entity.PARAM_VALUE, out double value))
                        //        {
                        //            arr.Eqp.ReworkInfo.ReworkRateDict.Add(arr.RecipeID, value / 100);
                        //        }            
                        //    }
                        //}
                    }
                }
                catch (Exception e)
                {
                    string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                    e.WriteExceptionLog(methodname, "-", "Input Persist Error in Loop");
                }
            }
        }

        public bool OnAfterLoad_SWITCH_TIME(SWITCH_TIME entity)
        {
            try
            {
                if (entity.EQP_ID == "*")
                {
                    InputMart.Instance.FabSemiconEqp.Rows.ForEach(x => SetEqpSetupInfo(entity, x));

                    return false;
                }

                var eqp = ResourceHelper.GetEqp(entity.EQP_ID);
                if (eqp == null)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "SWITCH_TIME", entity.EQP_ID, "Missing Eqp");
                    return false;
                }

                SetEqpSetupInfo(entity, eqp);

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }

            static void SetEqpSetupInfo(SWITCH_TIME entity, FabSemiconEqp eqp)
            {
                if (eqp.SetupInfos.ContainsKey(entity.FROM_SETUP, entity.TO_SETUP) == false)
                {
                    SwitchTimeInfo info = new SwitchTimeInfo();
                    info.EqpID = eqp.ResID;
                    info.FromSetup = entity.FROM_SETUP;
                    info.ToSetup = entity.TO_SETUP;
                    info.SwitchTime = Time.FromMinutes(entity.SWITCH_MINS).Floor();

                    if (Helper.GetConfig(ArgsGroup.Simulation_Run).stochasticTables.Contains("SWITCH_TIME"))
                    {
                        info.SwitchTimePdfConfig = CreateHelper.CreateStochasticConfig(entity.SWITCH_MINS_PDF);
                    }

                    eqp.SetupInfos.Add(entity.FROM_SETUP, entity.TO_SETUP, info);
                }
            }
        }

        public bool OnAfterLoad_PRESET(PRESET entity)
        {
            try
            {
                var presetFactors = InputMart.Instance.WEIGHT_PRESETSView.FindRows(entity.PRESET_ID);
                if (presetFactors.IsNullOrEmpty())
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "PRESET", entity.PRESET_ID, "Missing Factors");
                    return false;
                }

                FabWeightPreset preset = InputMart.Instance.FabWeightPresetView.FindRows(entity.PRESET_ID).FirstOrDefault();

                if (preset != null)
                    return true;

                preset = new FabWeightPreset();
                preset.Name = entity.PRESET_ID;
                preset.DispatcherType = DispatcherType.Fifo;
                if (entity.DISPATCHER_TYPE == DispatcherType.WeightSum.ToString())
                    preset.DispatcherType = DispatcherType.WeightSum;
                else if (entity.DISPATCHER_TYPE == DispatcherType.WeightSorted.ToString())
                    preset.DispatcherType = DispatcherType.WeightSorted;

                foreach (var presetEntity in presetFactors)
                {
                    if (presetEntity.FACTOR_WEIGHT == 0)
                        continue;

                    WEIGHT_FACTOR factorEntity = InputMart.Instance.WEIGHT_FACTORView.FindRows(presetEntity.FACTOR_ID).FirstOrDefault();
                    if (factorEntity == null)
                        continue;

                    if (factorEntity.IS_ACTIVE == false)
                        continue;

                    WeightFactor factor = CreateHelper.CreateWeightFactor(presetEntity, factorEntity);

                    preset.FactorList.Add(factor);
                }

                InputMart.Instance.FabWeightPreset.ImportRow(preset);

                return true;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public bool OnAfterLoad_DEMAND(DEMAND entity)
        {
            try
            {
                string stdProductID = entity.PRODUCT_ID;

                if (entity.WAFER_QTY <= 0)
                    return false;

                StdProduct prod = BopHelper.GetStdProduct(stdProductID);
                if (prod == null || prod.Route == null)
                    return false;

                FabProdPlan plan = new FabProdPlan()
                {
                    Product = prod,
                    Process = prod.Route,
                    PlanQty = entity.WAFER_QTY,
                    EndDate = entity.DUE_DATE,
                    StartDate = DateUtility.StartDayOfWeekTF(entity.DUE_DATE),
                    Yield = entity.LINE_YIELD,
                    WWSequence = entity.WW_SEQUENCE
                };

                InputMart.Instance.FabProdPlan.ImportRow(plan);

                FabSemiconMoMaster mm = PegHelper.FindMoMaster(prod);

                if (mm == null)
                {
                    mm = PegHelper.CreateMoMaster(prod);

                    InputMart.Instance.FabSemiconMoMaster.ImportRow(mm);
                }

                FabSemiconMoPlan mo = PegHelper.CreateMoPlan(mm, entity);
                mm.AddMoPlan(mo);

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public void OnAction_WIP_PARAM(IPersistContext context)
        {
            var group = InputMart.Instance.WIP_PARAM.Rows.GroupBy(x => x.LOT_ID);
            foreach (var entity in group)
            {
                try
                {
                    var wip = InputMart.Instance.FabWipInfo.SafeGet(entity.Key);
                    if (wip == null)
                        continue;

                    foreach (var item in entity)
                    {
                        if (item.PARAM_NAME == "return_step_id")
                        {
                            var routeRow = entity.Where(x => x.PARAM_NAME == "main_route_id").FirstOrDefault();
                            if (routeRow == null)
                                continue;

                            var route = InputMart.Instance.FabSemiconProcessView.FindRows(routeRow.PARAM_VALUE).FirstOrDefault();
                            if (route == null)
                                continue;

                            MergeInfo info = null;
                            var returnStepId = item.PARAM_VALUE;

                            var returnStep = route.FindStep(returnStepId) as FabSemiconStep;
                            if (returnStep == null)
                                continue;

                            wip.ReturnStep = returnStep;

                            var parentRow = entity.Where(x => x.PARAM_NAME == "parent_lot_id").FirstOrDefault();
                            if (parentRow == null)
                                continue;

                            var parentWip = InputMart.Instance.FabWipInfo.SafeGet(parentRow.PARAM_VALUE);
                            if (parentWip == null)
                                continue;

                            info = parentWip.MergeDict.SafeGet(returnStepId);

                            if (info == null)
                            {
                                info = new MergeInfo();
                                info.MergeStep = returnStep;
                                info.Parent = parentWip;

                                parentWip.MergeDict.Add(returnStepId, info);
                            }

                            if (info.Childs.Contains(wip))
                                continue;

                            info.Childs.Add(wip);

                            wip.MergeDict.Add(returnStepId, info);

                            wip.ParentWip = parentWip;
                        }
                        else if (item.PARAM_NAME == "due_date")
                        {
                            var dueDate = DateTime.MaxValue;
                            // B/W 수행하지 않거나, Unpeg될 경우에만 이 값이 사용됨.
                            DateTime.TryParse(item.PARAM_VALUE, out dueDate);

                            EntityHelper.AssignLotDueDate(wip, dueDate);
                        }
                        else if (item.PARAM_NAME == "bom_parent_lot_id")
                        {
                            BopHelper.SetBOMParentChildMap(item.LOT_ID, item.PARAM_VALUE);
                        }

                        wip.WipParamDict.Add(item.PARAM_NAME, item.PARAM_VALUE);
                    }
                }
                catch (Exception e)
                {
                    string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                    e.WriteExceptionLog(methodname, "-", "Input Persist Error in Loop");
                }
            }
        }

        public void OnAction_STEP_TIME(IPersistContext context)
        {
            try
            {
                //TODO: BatchToInline, ChamberToInline에 부합하는 Alternative를 계산하려면 추가 설계가 필요함.
                //TODO: 최소 요구 FlowTime은 Arrange가 특정되지 않은 상태에서 Utilization을 모르기 때문에 여기서 계산 불가.

                var resourceGroup = InputMart.Instance.STEP_TIME.Rows.GroupBy(x => x.EQP_ID);
                foreach (var group in resourceGroup)
                {
                    ProcTimeInfo resourceAvg = new ProcTimeInfo();
                    resourceAvg.TactTime = TimeSpan.FromSeconds(group.Average(x => x.TACT_TIME).Floor());
                    resourceAvg.FlowTime = TimeSpan.FromSeconds(group.Average(x => x.FLOW_TIME).Floor());

                    if (resourceAvg.TactTime > TimeSpan.Zero)
                        InputMart.Instance.ResourceProcTimeDict.Add(group.Key, resourceAvg);
                }

                var wsGroup = InputMart.Instance.STEP_TIME.Rows.GroupBy
                    (x => ResourceHelper.GetEqp(x.EQP_ID) != null ? ResourceHelper.GetEqp(x.EQP_ID).ResGroup : null);
                foreach (var group in wsGroup)
                {
                    if (group.Key == null)
                        continue;

                    ProcTimeInfo wsAvg = new ProcTimeInfo();
                    wsAvg.TactTime = TimeSpan.FromSeconds(group.Average(x => x.TACT_TIME).Floor());
                    wsAvg.FlowTime = TimeSpan.FromSeconds(group.Average(x => x.FLOW_TIME).Floor());

                    if (wsAvg.TactTime > TimeSpan.Zero)
                        InputMart.Instance.WorkstationProcTimeDict.Add(group.Key, wsAvg);
                }

                var defaultAltProcTime = new ProcTimeInfo();
                defaultAltProcTime.TactTime = TimeSpan.FromSeconds(Helper.GetConfig(ArgsGroup.Arrange_StepTime).defaultTactTimeSec);
                defaultAltProcTime.FlowTime = TimeSpan.FromSeconds(Helper.GetConfig(ArgsGroup.Arrange_StepTime).defaultFlowTimeSec);

                InputMart.Instance.DefaultAltProcTime = defaultAltProcTime;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                throw new System.InvalidOperationException(string.Format("{0} - {1}", methodname, e.GetType().FullName));
            }
        }

        public bool OnAfterLoad_EQP_LOCATION(EQP_LOCATION entity)
        {
            try
            {
                var eqp = ResourceHelper.GetEqp(entity.EQP_ID);

                if (eqp == null)
                    return false;

                eqp.LocationInfo = new EqpLocation();
                eqp.LocationInfo.Bay = entity.BAY_ID;
                eqp.LocationInfo.Cell = entity.CELL_ID;
                eqp.LocationInfo.Region = entity.REGION;
                eqp.LocationInfo.Building = entity.BUILDING;
                eqp.LocationInfo.Floor = entity.FLOOR;

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public void OnAction_EQP_PARAM(IPersistContext context)
        {
            foreach (var entity in InputMart.Instance.EQP_PARAM.Rows.GroupBy(x => x.EQP_ID))
            {
                try
                {
                    FabSemiconEqp eqp = ResourceHelper.GetEqp(entity.Key);
                    if (eqp == null)
                        continue;

                    foreach (var item in entity)
                    {
                        if (item.PARAM_NAME == "backup_eqp")
                        {
                            if (eqp.BackupEqps == null)
                                eqp.BackupEqps = new List<FabSemiconEqp>();

                            var split = item.PARAM_VALUE.Split(',');
                            foreach (var eqpId in split)
                            {
                                var backupEqp = ResourceHelper.GetEqp(eqpId);
                                if (backupEqp != null)
                                    eqp.BackupEqps.Add(backupEqp);
                            }
                        }
                        else if (item.PARAM_NAME == "post_pm_rework")
                        {
                            SetEqpReworkInfo(eqp, item.PARAM_VALUE);
                        }
                        else if (item.PARAM_NAME == "daily_limit_post_stack_pm")
                        {
                            if (double.TryParse(item.PARAM_VALUE, out double value))
                                eqp.DailyLimitPostStackPm = value;
                        }
                        else if (item.PARAM_NAME == "port_count")
                        {
                            if (int.TryParse(item.PARAM_VALUE, out int value))
                            {
                                if (eqp.UnitBatchInfo != null) // UnitBatch의 Port는 물류로직에서 사용되는 개념과 다름.
                                    eqp.UnitBatchInfo.MaxPortCount = value;

                                var bay = TransportSystem.GetBay(eqp.LocationInfo.Bay);
                                if (bay == null)
                                    throw new InvalidDataException($"Bay({eqp.LocationInfo.Bay}) does not exists");

                                var cell = bay.GetCell(eqp.LocationInfo.Cell);
                                if (cell == null)
                                    throw new InvalidDataException($"Cell({eqp.LocationInfo.Cell}) does not exists in Bay({eqp.LocationInfo.Cell})");

                                if (eqp.Ports == null)
                                    eqp.Ports = new List<Port>();

                                for (var i = 1; i <= value; i++)
                                {
                                    var portID = $"{eqp.ResID}_{i}";
                                    CreatePort(portID, eqp, bay, cell);
                                }

                                eqp.HasPort = eqp.Ports.IsNullOrEmpty() == false;
                            }
                        }
                        else if (item.PARAM_NAME == "unit_batch_size")
                        {
                            // ## 투입된 하나의 lot은 Ceiling(wafer qty/unit_batch_size) 만큼의 Port가 요구 됩니다.
                            // 설비 PortCount 제약이 없다면(>=9999) Port 점유 방식은 고려하지 않습니다. (Tact = OrgTact)
                            // 설비 PortCount 제약이 있고, 다수의 Port가 요구될 때, 엔진에서는 항상 직렬로 처리하며 Processing Time을 늘려줍니다. (NewTact = OrgTact * PortRequireCount)
                            // 즉, 하나의 lot은 항상 하나의 Port만 점유합니다.
                            if (int.TryParse(item.PARAM_VALUE, out int value))
                            {
                                if (eqp.UnitBatchInfo != null)
                                    eqp.UnitBatchInfo.UnitBatchSize = value;
                            }
                        }
                        else if (item.PARAM_NAME == "eqp_yield")
                        {
                            if (double.TryParse(item.PARAM_VALUE, out double value))
                                eqp.EqpYieldRate = value;
                        }
                        else if (item.PARAM_NAME == "setup_info")
                        {
                            InputMart.Instance.EqpSetupDic[eqp.ResID] = item.PARAM_VALUE;
                        }
                        else if (item.PARAM_NAME == "setup_event")
                        {
                            if (item.PARAM_VALUE == "OnTrackOut")
                                eqp.DoSetupOnTrackOut = true;
                        }
                        else if (item.PARAM_NAME == "max_wafers_avoid_switch")
                        {
                            eqp.MaxWafersAvoidSwitch = Helper.IntParse(item.PARAM_VALUE, 0);
                        }
                        else if (item.PARAM_NAME == "chuck_swap_loss")
                        {
                            var chuckSwapValue = item.PARAM_VALUE.Split(',');

                            // PARAM_VALUE가 미기입이 아니라면
                            if (chuckSwapValue.Length == 2)
                            {
                                if (double.TryParse(chuckSwapValue[0], out double rate))
                                    eqp.ChuckLossRate = rate;
                                if (int.TryParse(chuckSwapValue[1], out int value))
                                    eqp.ChuckSwapLossTime = TimeSpan.FromSeconds(value);
                            }
                        }
                        else if (item.PARAM_NAME == "force_standby_rate")
                        {
                            if (double.TryParse(item.PARAM_VALUE, out double value))
                                eqp.ForceStandbyRate = value;
                        }
                        else if (item.PARAM_NAME == "standby_reason")
                        {
                            if (item.PARAM_VALUE == "Y")
                                eqp.WriteStandbyTime = true;
                        }
                        else if (item.PARAM_NAME == "tool_limit_on_eqp")
                        {
                            if (int.TryParse(item.PARAM_VALUE, out int value))
                            {
                                eqp.ToolingInfo.ToolLimitCount = value;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                    e.WriteExceptionLog(methodname, "-", "Input Persist Error in Loop");
                }
            }

            void SetEqpReworkInfo(FabSemiconEqp eqp, string paramValue)
            {
                var reworkInfo = new EqpReworkInfo();
                reworkInfo.ProcessingType = ReworkProcessingType.Time;

                var groupSplits = paramValue.Split(new string[] { "//" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var group in groupSplits)
                {
                    var argSplits = group.Split(';');
                    foreach (var arg in argSplits)
                    {
                        var keyValueSplits = arg.Split('=');
                        var key = keyValueSplits.First();
                        var value = keyValueSplits.Last();

                        if (key == "pm_codes")
                        {
                            reworkInfo.PmCodes = value.Split(',').ToList();
                        }
                        else if (key == "rework_period_days")
                        {
                            reworkInfo.ReworkPeriodDays = Convert.ToDouble(value);
                        }
                        else if (key == "rework_rate")
                        {
                            reworkInfo.ReworkRate = Convert.ToDouble(value);
                        }
                        else if (key == "rework_hold_hr")
                        {
                            reworkInfo.ReworkHoldTime = TimeSpan.FromHours(Convert.ToDouble(value));
                        }
                    }

                    var isValid = reworkInfo.PmCodes.IsNullOrEmpty() == false && reworkInfo.ReworkPeriodDays > 0
                    && reworkInfo.ReworkRate > 0 && reworkInfo.ReworkHoldTime > TimeSpan.Zero;

                    if (eqp.ReworkInfos == null)
                        eqp.ReworkInfos = new List<EqpReworkInfo>();

                    if (isValid)
                        eqp.ReworkInfos.Add(reworkInfo);
                }
            }

            static Port CreatePort(string portID, FabSemiconEqp eqp, Bay bay, Cell cell)
            {
                Port port;
                var useMultiReservePort = eqp.SimType == SimEqpType.LotBatch || eqp.SimType == SimEqpType.BatchInline;
                if (useMultiReservePort)
                    port = new MultiReservePort(portID, cell.X, cell.Y, bay, cell, eqp.ResID);
                else
                    port = new Port(portID, cell.X, cell.Y, bay, cell, eqp.ResID);

                bay.AddPort(port);
                eqp.Ports.Add(port);
                return port;
            }
        }

        public void OnAction_ROUTE_STEP_PARAM(IPersistContext context)
        {
            foreach (var group in InputMart.Instance.ROUTE_STEP_PARAM.Rows.GroupBy(x => new { x.ROUTE_ID, x.STEP_ID }))
            {
                var sample = group.First();
                var route = InputMart.Instance.FabSemiconProcessView.FindRows(sample.ROUTE_ID).FirstOrDefault();
                if (route == null)
                    continue;

                var step = route.FindStep(sample.STEP_ID) as FabSemiconStep;
                if (step == null)
                    continue;

                foreach (var item in group)
                {
                    if (item.PARAM_NAME == "rework_process")
                    {
                        SetStepReworkInfo(step, item.PARAM_VALUE);
                    }
                    else if (item.PARAM_NAME == "sample_rate")
                    {
                        step.SampleRate = Helper.GetValidRate(double.Parse(item.PARAM_VALUE));
                    }
                    else if (item.PARAM_NAME == "capa_key")
                    {
                        step.CapaInfo = InputMart.Instance.StepCapaInfoView.FindRows(item.PARAM_VALUE).FirstOrDefault();
                    }
                    else if (item.PARAM_NAME == "capa_multiplier")
                    {
                        step.CapaMultiplier = Helper.DoubleParse(item.PARAM_VALUE, 0);
                    }
                }
            }

            void SetStepReworkInfo(FabSemiconStep step, string paramValue)
            {
                var reworkInfo = new StepReworkInfo();
                reworkInfo.ReturnStep = step;

                var argSplits = paramValue.Split(';');
                foreach (var arg in argSplits)
                {
                    var keyValueSplits = arg.Split('=');
                    var key = keyValueSplits.First();
                    var value = keyValueSplits.Last();

                    if (key == "rework_rate")
                    {
                        reworkInfo.ReworkRate = Convert.ToDouble(value);
                    }
                    else if (key == "rework_route_id")
                    {
                        var reworkRoute = InputMart.Instance.FabSemiconProcessView.FindRows(value).FirstOrDefault();
                        if (reworkRoute == null)
                            continue;

                        reworkInfo.ProcessingType = ReworkProcessingType.Route;
                        reworkInfo.ReworkRoute = reworkRoute;

                    }
                    else if (key == "rework_step_id")
                    {
                        var reworkStep = step.Process.FindStep(value);
                        if (reworkStep == null)
                            continue;

                        reworkInfo.ProcessingType = ReworkProcessingType.Step;
                        reworkInfo.ReworkStep = reworkStep as FabSemiconStep;
                    }
                    else if (key == "rework_hold_hr")
                    {
                        reworkInfo.ProcessingType = ReworkProcessingType.Time;
                        reworkInfo.ReworkHoldTime = TimeSpan.FromHours(Convert.ToDouble(value));
                    }
                }

                var isValid = reworkInfo.ReworkRate > 0
                    && (reworkInfo.ReworkRoute != null || reworkInfo.ReworkStep != null || reworkInfo.ReworkHoldTime > TimeSpan.Zero);

                if (isValid)
                    step.ReworkInfo = reworkInfo;
            }
        }

        public void OnAction_EQP_DOWN_COND(IPersistContext context)
        {
            var groupByResource = InputMart.Instance.EQP_DOWN_COND.Rows.GroupBy(x => x.EQP_ID);

            foreach (var group in groupByResource)
            {
                try
                {
                    var eqp = ResourceHelper.GetEqp(group.Key);
                    if (eqp == null)
                        continue;

                    if (eqp.QuantityDownConfigs == null)
                        eqp.QuantityDownConfigs = new List<EqpDownConditionalConfig>();

                    foreach (var item in group)
                    {
                        var config = ResourceHelper.CreateConditionalDownConfig(item);
                        if (config == null)
                            continue;

                        if (config.DownCondType == EqpDownCondType.TBM)
                        {
                            // LastEventTime이 설정되지 않으면 PST로
                            if (config.LastEventTime == DateTime.MinValue)
                                config.LastEventTime = ModelContext.Current.StartTime;

                            InputMart.Instance.RandomNumberGenerator2 = new Random(Helper.GetConfig(ArgsGroup.Simulation_Run).randomSeed++);

                            var period = TimeSpan.FromHours(Helper.GetDistributionRandomNumber(config.MttfPdfConfig, RNGType.Isolation));

                            var startTime = config.LastEventTime + period;
                            while (startTime < ModelContext.Current.EndTime)
                            {
                                EqpDownTag tag = ResourceHelper.CreateEqpDownTagConditional(startTime, config);
                                if (tag.StartTime >= ModelContext.Current.StartTime)
                                    ResourceHelper.CreateAvailablePM(tag);

                                // Down간의 간격을 매번 새로 생성.
                                period = TimeSpan.FromHours(Helper.GetDistributionRandomNumber(config.MttfPdfConfig, RNGType.Isolation));
                                startTime += period;
                            }
                        }
                        else if (config.DownCondType == EqpDownCondType.CBM)
                        {
                            eqp.QuantityDownConfigs.Add(config);
                        }
                    }
                }
                catch (Exception e)
                {
                    string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                    e.WriteExceptionLog(methodname, "-", "Input Persist Error in Loop");
                }
            }
        }

        public bool OnAfterLoad_EQP_DOWN_SCHED(EQP_DOWN_SCHED entity)
        {
            try
            {
                var tag = ResourceHelper.CreateEqpDownTagSchedule(entity);
                if (tag == null)
                    return false;

                if (tag.Eqp == null)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "EQP_DOWN_SCHED", entity.EQP_ID, "Missing Eqp");
                    return false;
                }
                else if (tag.DurationSecond <= 0)
                {
                    OutputHelper.WritePersistLog(LogType.WARNING, "EQP_DOWN_SCHED", entity.EQP_ID, "Invalid Duration");
                    return false;
                }
                else if (tag.EndTime <= ModelContext.Current.StartTime || tag.StartTime >= ModelContext.Current.EndTime)
                {
                    if (tag.IsStackPm == false)
                        return false;

                    // StackPM은 Inhibit 걸어야 되서 로딩

                    //OutputHelper.WritePersistLog(LogType.WARNING, "EQP_DOWN_SCHED", entity.EQP_ID, "Out of PlanHorizon");
                    //return false;
                }

                ResourceHelper.CreateAvailablePM(tag);

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public void OnAction_STACK_INFO(IPersistContext context)
        {
            if (InputMart.Instance.ApplyStacking == false)
                return;

            // KEY: Part,Route
            // Part의 Stack정보가 MultiGroup일 때, Part-Route 상 맨처음 S Step을 판별하기 위함.
            DoubleDictionary<string, SemiconProcess, StackStepInfo> veryFirstLayerDict = new DoubleDictionary<string, SemiconProcess, StackStepInfo>();

            Dictionary<SemiconProcess, StackStepInfo> lastLayerDict = new Dictionary<SemiconProcess, StackStepInfo>();
            Dictionary<SemiconProcess, StackStepInfo> lastYLayerDict = new Dictionary<SemiconProcess, StackStepInfo>();
            foreach (var group in TempMart.Instance.STACK_INFO.Rows.GroupBy(x => x.STACK_GROUP))
            {
                if (group.Key == null)
                    continue;

                foreach (var entity in group)
                {
                    CreateStackStepinfo(entity, group, veryFirstLayerDict, lastLayerDict, lastYLayerDict);
                }

                lastLayerDict.Values.ForEach(x => x.IsLastLayer = true);
                lastLayerDict.Clear();

                lastYLayerDict.Values.ForEach(x => x.IsLastYLayer = true);
                lastYLayerDict.Clear();
            }

            veryFirstLayerDict.Values.ForEach(x => x.Values.ForEach(y => y.IsVeryFirstLayer = true));
            veryFirstLayerDict.Clear();

            static void CreateStackStepinfo(STACK_INFO entity, IGrouping<string, STACK_INFO> group
                , DoubleDictionary<string, SemiconProcess, StackStepInfo> veryFirstLayerDict
                , Dictionary<SemiconProcess, StackStepInfo> lastLayerDict, Dictionary<SemiconProcess, StackStepInfo> lastYLayerDict)
            {
                var attr = InputMart.Instance.PartStepAttributeView.FindRows(entity.PART_ID, entity.STEP_ID).FirstOrDefault();
                if (attr == null)
                    return;

                foreach (FabSemiconStep step in attr.RouteSteps)
                {
                    StackStepInfo info = new StackStepInfo();
                    info.PartID = entity.PART_ID;
                    info.StackGroupID = entity.STACK_GROUP;
                    info.StackStep = step;
                    info.StackType = StackType.D;
                    if (entity.STACK_TYPE == "Y")
                        info.StackType = StackType.Y;
                    else if (entity.STACK_TYPE == "S")
                    {
                        info.StackType = StackType.S;
                        info.IsFirstLayer = true;

                        //var firstStep = veryFirstLayerDict.SafeGet(info.PartID)?.SafeGet(step.Process);
                        if (veryFirstLayerDict.ContainsKey(info.PartID, step.Process))
                        {
                            if (step.Sequence < veryFirstLayerDict[info.PartID][step.Process].StackStep.Sequence)
                                veryFirstLayerDict[info.PartID][step.Process] = info;
                        }
                        else
                        {
                            veryFirstLayerDict.Add(info.PartID, step.Process, info);
                        }
                    }

                    if (attr.StackInfoDict.ContainsKey(step) == false)
                        attr.StackInfoDict.Add(step, info);

                    //var lastStep = lastLayerDict.SafeGet(step.Process);
                    if (lastLayerDict.ContainsKey(step.Process))
                    {
                        if (step.Sequence > lastLayerDict[step.Process].StackStep.Sequence)
                            lastLayerDict[step.Process] = info;
                    }
                    else
                    {
                        lastLayerDict.Add(step.Process, info);
                    }

                    if (entity.STACK_TYPE == "Y" || entity.STACK_TYPE == "S") // S와 D만 존재하는 Group이 존재하면 S가 LastYLayer가 됨.
                    {
                        //var lastYStep = lastYLayerDict.SafeGet(step.Process);
                        if (lastYLayerDict.ContainsKey(step.Process))
                        {
                            if (step.Sequence > lastYLayerDict[step.Process].StackStep.Sequence)
                                lastYLayerDict[step.Process] = info;
                        }
                        else
                        {
                            lastYLayerDict.Add(step.Process, info);
                        }
                    }
                }
            }
        }

        public void OnAction_SIM_CONFIG(IPersistContext context)
        {
            SetExcludeOutputTables();

            SetFactoryTime();

            SetStackPMInfo();

            SetInPlanRule();

            InputMart.Instance.RandomNumberGenerator = new Random(Helper.GetConfig(ArgsGroup.Simulation_Run).randomSeed);
            //InputMart.Instance.RandomNumberGenerator = new Random(Helper.GetConfig(ArgsGroup.Simulation_Run).randomSeed);

            // simulation period에 따라 Generator 호출 횟수가 Input Persist 단계에서 달라지는 경우, 시뮬레이션 도중 Random값에 차이가 발생하여
            // 결과적으로 시뮬레이션 output이 달라지는 결과를 초래 할 수 있어, 별도로 사용할 generator를 추가.
            InputMart.Instance.RandomNumberGenerator2 = new Random(Helper.GetConfig(ArgsGroup.Simulation_Run).randomSeed);

            var considerEqpLocation = Helper.GetConfig(ArgsGroup.Resource_EqpLocation).considerEqpLocation;
            InputMart.Instance.ApplyCrossFabLimit = considerEqpLocation == 1 || considerEqpLocation == 3;
            InputMart.Instance.ApplyDeliveryTime = considerEqpLocation >= 2;

            float configlotMergeSize = GetConfigLotMergeSize();

            InputMart.Instance.LotSize = Helper.GetConfig(ArgsGroup.Lot_Default).lotSize;
            InputMart.Instance.LotMergeSize = Math.Max(configlotMergeSize, InputMart.Instance.LotSize);

            InputMart.Instance.ApplyStepYield = Helper.GetConfig(ArgsGroup.Bop_Step).applyStepYield == "Y";
            InputMart.Instance.ApplyStepRework = Helper.GetConfig(ArgsGroup.Bop_Step).applyStepRework == "Y";
            InputMart.Instance.ApplyStepSampling = Helper.GetConfig(ArgsGroup.Bop_Step).applySampling == "Y";
            InputMart.Instance.ApplyStacking = Helper.GetConfig(ArgsGroup.Logic_Photo).applyStacking == "Y";

            InputMart.Instance.TransferTimePdfConfig = CreateHelper.CreateStochasticConfig(Helper.GetConfig(ArgsGroup.Resource_EqpLocation).transferTimePdf);

            InputMart.Instance.ApplyTransportSystem = Helper.GetConfig(ArgsGroup.Resource_EqpLocation).applyTransportSystem == "Y";

            string configStr = string.Empty;
            foreach (var item in InputMart.Instance.SIM_CONFIG.Rows.OrderBy(x => x.PARAM_GROUP))
                configStr += string.Format("{0}.{1}={2};\n", item.PARAM_GROUP, item.PARAM_NAME, item.PARAM_VALUE);

            OutputHelper.WritePersistLog(LogType.INFO, "CONFIG", configStr, "-");

            static void SetExcludeOutputTables()
            {
                string exTables = Helper.GetConfig(ArgsGroup.Simulation_Output).excludeOutputTables;
                if (string.IsNullOrEmpty(exTables) == false)
                    InputMart.Instance.ExcludeOutputTables.AddRange(exTables.Split(',').Select(x => x.Trim()));
            }

            static void SetFactoryTime()
            {
                var param = Helper.GetConfig(ArgsGroup.Simulation_Run).factoryTime;

                var target = FactoryConfiguration.Current;
                FactoryTimeInfo customTimeInfo = new FactoryTimeInfo();

                var itemSplits = param.Split(';');
                foreach (var item in itemSplits)
                {
                    if (item.IsNullOrEmpty())
                        continue;

                    var keyValueSplit = item.Split('=');

                    if (keyValueSplit.Length != 2)
                    {
                        OutputHelper.WritePersistLog(LogType.WARNING, "SIM_CONFIG", param, "Invalid factoryTime");
                        continue;
                    }

                    var key = keyValueSplit[0];
                    var value = keyValueSplit[1];

                    if (key == "start_day_of_week")
                    {
                        if (value == DayOfWeek.Sunday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Sunday;
                        else if (value == DayOfWeek.Monday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Monday;
                        else if (value == DayOfWeek.Tuesday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Tuesday;
                        else if (value == DayOfWeek.Wednesday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Wednesday;
                        else if (value == DayOfWeek.Thursday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Thursday;
                        else if (value == DayOfWeek.Friday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Friday;
                        else if (value == DayOfWeek.Saturday.ToString())
                            customTimeInfo.StartOfWeek = DayOfWeek.Saturday;
                        else
                            OutputHelper.WritePersistLog(LogType.WARNING, "SIM_CONFIG", param, "Invalid startDayOfWeek");
                    }
                    else if (key == "start_time")
                    {
                        if (Helper.GetTimeAsFractionalHours(value, out var startHour))
                            customTimeInfo.StartOffset = TimeSpan.FromHours(startHour);
                        else
                            OutputHelper.WritePersistLog(LogType.WARNING, "SIM_CONFIG", param, "Invalid startTime");
                    }
                    else if (key == "shift_hour")
                    {
                        if (float.TryParse(value, out var shiftHours))
                            customTimeInfo.ShiftHours = shiftHours;
                        else
                            OutputHelper.WritePersistLog(LogType.WARNING, "SIM_CONFIG", param, "Invalid shiftHour");
                    }
                    else if (key == "shift_names")
                    {
                        customTimeInfo.ShiftNames = value.Split(',');
                    }
                    else
                    {
                        OutputHelper.WritePersistLog(LogType.WARNING, "SIM_CONFIG", param, "Invalid factoryTime");
                    }
                }

                target.TimeInfo = customTimeInfo;
            }

            static void SetStackPMInfo()
            {
                var stackPMCode = Helper.GetConfig(ArgsGroup.Logic_Photo).stackPmCode;
                if (stackPMCode == null)
                    return;

                var semiColSplits = stackPMCode.Split(';');
                foreach (var split in semiColSplits)
                {
                    var commaSplits = split.Split(',');
                    if (commaSplits.Length != 3)
                        continue;

                    double.TryParse(commaSplits[1], out double preInhibit);
                    double.TryParse(commaSplits[2], out double postInhibit);

                    StackPMInfo stackPMInfo = new StackPMInfo();
                    stackPMInfo.PMCode = commaSplits[0];
                    stackPMInfo.PreInhibitDays = preInhibit;
                    stackPMInfo.PostInhibitDays = postInhibit;

                    InputMart.Instance.StackPMInfo.Rows.Add(stackPMInfo);
                }
            }

            static void SetInPlanRule()
            {
                var module = Helper.GetConfig(ArgsGroup.Simulation_Run).modules;
                var fabInPlanRule = Helper.GetConfig(ArgsGroup.Lot_InPlan).fabInPlanRule;

                if (module == 1 && fabInPlanRule == 1)
                {
                    // B/W를 수행하지 않으면서 Demand로 부터 InPlan을 생성할 수는 없으므로,
                    // FAB_IN_PLAN을 사용하는 방식으로 자동 대체.
                    InputMart.Instance.InPlanRule = FabInPlanRule.FabInPlan;
                    Logger.MonitorInfo("fabInPlanRule is automatically set to 2 from 1 when module == 1");
                }
                else
                {
                    InputMart.Instance.InPlanRule = (FabInPlanRule)fabInPlanRule;
                }
            }

            static float GetConfigLotMergeSize()
            {
                var configlotMergeSize = Helper.GetConfig(ArgsGroup.Lot_Wip).lotMergeSize;
                if (configlotMergeSize > 0)
                    InputMart.Instance.DoLotSizeMerge = true;

                // Qtime 적용 할 거면 LotSizeMerge는 Off 처리함.
                // 동시에 쓰려면 Merge하더라도 개별 Lot에 대한 Qtime을 Handling 할 수 있도록 고도화 시켜야 함.
                if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime >= 1)
                {
                    configlotMergeSize = 0;
                    InputMart.Instance.DoLotSizeMerge = false;

                    Logger.MonitorInfo("lotMergeSize is automatically set to 0 when applyQtime >= 1");
                }

                // DeliveryTime 적용 할 거면 LotSizeMerge는 Off 처리함.
                // MinDeliveryTime을 적용해서 대기 중인 상태에서 FHB로 Merge해버리면, 개별 Lot에 대한 Delivery Time을 모사할 수 없음.
                if (InputMart.Instance.ApplyDeliveryTime)
                {
                    configlotMergeSize = 0;
                    InputMart.Instance.DoLotSizeMerge = false;

                    // [0] Ignore location [1] Apply quantity limit between locations [2] Apply transfer time [3] Apply quantity limit and transfer time
                    Logger.MonitorInfo("lotMergeSize is automatically set to 0 when considerEqpLocation >= 2");
                }

                return configlotMergeSize;
            }
        }

        public bool OnAfterLoad_STACK_ACTIVE_LOT(STACK_ACTIVE_LOT entity)
        {
            if (InputMart.Instance.ApplyStacking == false)
                return false;

            return true;
        }

        public void OnAction_PRODUCT(IPersistContext context)
        {
            foreach (var mfgPart in InputMart.Instance.FabProduct.Rows)
            {
                foreach (FabSemiconStep step in mfgPart.MainRoute.Steps)
                {
                    BopHelper.SetPartStepAttributes(mfgPart, step);
                }
            }
        }

        public bool OnAfterLoad_TOOLING(TOOLING entity)
        {
            try
            {
                if (entity.TOOLING_TYPE == ToolingType.Reticle.ToString())
                {
                    if (Helper.GetConfig(ArgsGroup.Resource_Tooling).controlReticle <= 0)
                        return false;

                    FabReticle reticle = InputMart.Instance.FabReticleView.FindRows(entity.TOOLING_ID).FirstOrDefault();
                    if (reticle == null)
                    {
                        reticle = new FabReticle();
                        reticle.ToolingType = entity.TOOLING_TYPE;
                        reticle.ToolingID = entity.TOOLING_ID;
                        reticle.ToolingStatus = entity.TOOLING_STATUS;
                        reticle.ToolingLocation = entity.TOOLING_LOCATION;
                        reticle.LastUpdateDateTime = entity.LAST_UPDATE_DATETIME;

                        var eqp = ResourceHelper.GetEqp(entity.TOOLING_LOCATION);

                        if (eqp != null)
                            ResourceHelper.KeepReticleOnEqp(eqp, reticle, entity.TOOLING_NAME, true);
                        else
                            ResourceHelper.KeepReticleOnStocker(reticle, entity.TOOLING_NAME, null, true);

                        InputMart.Instance.FabReticle.ImportRow(reticle);
                    }

                    InputMart.Instance.ToolingMap.Add(entity.TOOLING_NAME, reticle);
                }
                else
                {
                    if (Helper.GetConfig(ArgsGroup.Resource_Tooling).controlProbeCard <= 0)
                        return false;

                    // TODO: Reticle 이외의 ToolingType은 추가 구현이 필요.
                }

                return false;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "Input Persist Error");
                return false;
            }
        }

        public bool OnAfterLoad_CROSS_FAB(CROSS_FAB entity)
        {
            if (InputMart.Instance.ApplyCrossFabLimit == false)
                return false;

            if (entity.LIMIT_QTY == null)
                return false;

            CrossFabLimit info = new CrossFabLimit();
            info.Level = entity.POINT_LEVEL;
            info.From = entity.START_POINT;
            info.To = entity.END_POINT;
            info.LimitLotQty = entity.LIMIT_QTY;
            info.InitLotQty = entity.INIT_QTY;

            InputMart.Instance.CrossFabLimit.Rows.Add(info);

            return false;
        }

        public void OnAction_DEMAND(IPersistContext context)
        {
            if (Helper.GetConfig(ArgsGroup.Simulation_Run).modules == 1)
            {
                // ForwardPegging을 수행하기 위한 준비.
                // DEMAND에 STOCK 반영해서 moPlan에 적용.
                PegHelper.PrepareTarget(null);
            }
        }

        public void OnAction_ROUTE_STEP(IPersistContext context)
        {
            BopHelper.SetStandardPotDays();
        }

        public void OnAction_BOM(IPersistContext context)
        {
            if (TempMart.Instance.BOM.Rows.IsNullOrEmpty() == false)
                InputMart.Instance.UseBOM = true;

            foreach (var group in TempMart.Instance.BOM.Rows.GroupBy(x => x.TO_PART_ID))
            {
                var toPart = InputMart.Instance.FabProductMfgPartView.FindRows(group.Key).FirstOrDefault(); // MfgPartID에 맵핑됨
                if (toPart == null || toPart.MainRoute == null)
                    continue;

                BOMInfo info = CreateHelper.CreateBOMInfo(group, toPart);

                if (info.BOMLevel == 0)
                {
                    var nextBom = TempMart.Instance.BOM.Rows.Where(x => x.FROM_PART_ID == group.Key).FirstOrDefault();
                    if (nextBom == null)
                        info.BOMLevel = 1; // ToPart가 완제품
                }

                InputMart.Instance.BOMInfo.ImportRow(info);
            }

            // BOMLevel을 데이터로 입력받았더라도, BOMContributionRatio 계산을 위해 진입.
            // Recursive
            foreach (var info in InputMart.Instance.BOMInfo.Where(x => x.BOMLevel == 1))
            {
                SetPrevBomLevel(info, 1);
            }

            static void SetPrevBomLevel(BOMInfo info, int bomLevel, float parentBomContributionRatio = 1)
            {
                bomLevel++;

                var fromQtySum = info.FromParts.Values.Sum();
                foreach (var fromPart in info.FromParts.Keys)
                {
                    fromPart.BOMContributionRatio = (info.FromParts[fromPart] / fromQtySum) * parentBomContributionRatio;

                    var prevInfo = InputMart.Instance.BOMInfoView.FindRows(fromPart).FirstOrDefault();
                    if (prevInfo != null)
                    {
                        if (prevInfo.BOMLevel == 0)
                            prevInfo.BOMLevel = bomLevel;

                        SetPrevBomLevel(prevInfo, bomLevel, fromPart.BOMContributionRatio);
                    }
                }
            }
        }

        public bool OnAfterLoad_STEP_CAPA(STEP_CAPA entity)
        {
            var loadingRule = Helper.GetConfig(ArgsGroup.Bop_Step).loadingRule;

            if (loadingRule == 2)
            {
                var info = CreateHelper.CreateStepCapaInfo(entity);

                InputMart.Instance.StepCapaInfo.ImportRow(info);
            }

            return false;
        }

        public void OnAction_ROUTE_STEP_LOT(IPersistContext context)
        {
            foreach (var group in TempMart.Instance.ROUTE_STEP_LOT.Rows.GroupBy(x => x.ROUTE_ID))
            {
                var sample = group.First();
                FabSemiconProcess proc = new FabSemiconProcess();
                proc.LineID = sample.LINE_ID;
                proc.ProcessID = sample.ROUTE_ID;
                proc.RouteType = RouteType.LOT;

                List<SemiconStep> steps = BopHelper.GetRouteSteps(proc, true);

                BopHelper.BuildBop(proc, steps);

                InputMart.Instance.FabSemiconProcess.ImportRow(proc);
            }
        }

        public bool OnAfterLoad_BUFFER(BUFFER entity)
        {
            var bay = TransportSystem.GetBay(entity.BAY_ID);
            if (bay == null)
                throw new InvalidDataException($"Bay({entity.BAY_ID}) does not exists");

            var cell = bay.GetCell(entity.CELL_ID);
            if (cell == null)
                throw new InvalidDataException($"Cell({entity.CELL_ID}) does not exists in Bay({entity.BAY_ID})");

            var type = Helper.Parse<BufferType>(entity.BUFFER_TYPE, default(BufferType));
            if (entity.CAPACITY == 1)
            {
                var buffer = new Buffer(entity.BUFFER_ID, cell.X, cell.Y, bay, cell, type);
                bay.AddBuffer(buffer);
            }
            else if (entity.CAPACITY > 1)
            {
                for (var i = 0; i < entity.CAPACITY; i++)
                {
                    var bufferID = $"{entity.BUFFER_ID}_{i}";
                    var buffer = new Buffer(bufferID, cell.X, cell.Y, bay, cell, type);
                    bay.AddBuffer(buffer);
                }
            }

            return false;
        }

        public void OnAction_LOCATION(IPersistContext context)
        {
            //BAY -> CELL 순으로 로딩
            var bays = InputMart.Instance.LOCATION.Rows.Where(x => x.LOCATION_TYPE.Contains("BAY")).ToList();
            var cells = InputMart.Instance.LOCATION.Rows.Where(x => x.LOCATION_TYPE.Contains("CELL")).ToList();

            foreach (var entity in bays)
            {
                var bayType = Helper.Parse<BayType>(entity.LOCATION_TYPE, default(BayType));
                var bay = new Bay(entity.LOCATION_ID, bayType, entity.POS_X_L, entity.POS_Y_D);
                TransportSystem.AddBay(bay);
            }

            foreach (var entity in cells)
            {
                var bay = TransportSystem.GetBay(entity.PARENT_ID);
                if (bay == null)
                    continue;

                var cell = new Cell(entity.LOCATION_ID, bay, entity.POS_X_L, entity.POS_Y_D);
                bay.AddCell(cell);
            }
        }
    }
}