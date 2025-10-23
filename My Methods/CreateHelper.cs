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
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan.Pegging;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class CreateHelper
    {
        public static FabProduct CreateMfgPart(PRODUCT entity)
        {
            FabProduct mfgPart = new FabProduct();
            
            mfgPart.LineID = entity.LINE_ID;
            mfgPart.ProductID = entity.MFG_PART_ID;
            mfgPart.StdProductID = entity.PRODUCT_ID;
            mfgPart.ProcessID = entity.ROUTE_ID;
            mfgPart.PartGroup = entity.PART_GROUP;
            mfgPart.PartID = entity.PART_ID;

            mfgPart.Process = InputMart.Instance.FabSemiconProcessView.FindRows(entity.ROUTE_ID).FirstOrDefault();

            return mfgPart;
        }

        internal static FabSemiconProcess CreateFabSemiconProcess(ROUTE entity)
        {
            FabSemiconProcess obj = new FabSemiconProcess();

            obj.LineID = entity.LINE_ID;
            obj.ProcessID = entity.ROUTE_ID;
            obj.RouteType = RouteType.MAIN;
            if (entity.ROUTE_TYPE == RouteType.STANDARD.ToString())
                obj.RouteType = RouteType.STANDARD;
            else if (entity.ROUTE_TYPE == RouteType.REWORK.ToString())
                obj.RouteType = RouteType.REWORK;

            return obj;
        }

        internal static StdProduct CreateStdProduct(FabProduct mfgPart, PRODUCT entity)
        {
            StdProduct stdProduct = new StdProduct();
            stdProduct.StdProductID = mfgPart.StdProductID;
            stdProduct.LineID = mfgPart.LineID;

            // StdProduct (Sales Code)는 단 하나의 Standard Route를 갖는다.
            // 기존 모델도 돌아가도록, 값이 없으면 처음 찾는 MainRoute를 사용.

            // BOM 이 도입 되면서 StdRoute는 이 규칙대로 찾을 수 없음.
            // BOM 테이블을 사용할 경우에는, StdRoute를 찾지 않고, 모든 Step이 StdStep인 것으로 취급하기로 논의 됨.

            stdProduct.Process = InputMart.Instance.FabSemiconProcessView.FindRows(entity.STD_ROUTE_ID).FirstOrDefault() ?? mfgPart.Process;
            if (stdProduct.Process == null)
                return null;

            InputMart.Instance.StdProduct.ImportRow(stdProduct);

            return stdProduct;
        }

        internal static FabSemiconLot CreateLot(IWipInfo wip)
        {
            FabSemiconLot lot = new FabSemiconLot();
            FabWipInfo wipInfo = wip as FabWipInfo;

            EntityHelper.AssignLotDueDate(wipInfo);

            lot.WipInfo = wip;
            lot.LineID = wip.LineID;
            lot.LotID = wip.LotID;
            lot.UnitQtyDouble = wip.UnitQty;
            lot.Route = wip.Process;

            lot.ReleaseTime = wip.FabInTime;
            lot.Product = wip.Product;

            lot.IsWipHandle = true;
            lot.IsHotLot = EntityHelper.IsHotLot(lot.FabWipInfo.LotPriorityStatus);

            lot.CurrentState = wip.CurrentState;

            // Non-SimulationStep에서 RUN상태인 제공은 WAIT으로 만들고, BucketTime에서 PT_MINS를 적용
            var bohStep = lot.FabWipInfo.InitialStep as FabSemiconStep;
            if (lot.FabWipInfo.IsBohRun)
            {
                if (bohStep.IsSimulationStep == false)
                {
                    lot.CurrentState = EntityState.WAIT;
                    lot.ApplyPTMinsAtBOH = true;
                }
            }

            (wip as FabWipInfo).Lot = lot;

            if (InputMart.Instance.ApplyStacking)
            {
                ArrangeHelper.SetStackActiveInfo(lot);
            }

            return lot;
        }

        internal static FabSemiconLot CreateInstancingLot(string lotID, FabProduct product, FabSemiconProcess process, double waferQty, 
            SemiconStep initialStep, DateTime releaseDate, DateTime dueDate, int priority = 1)
        {
            var wipInfo = CreateInstancingFabWipInfo(lotID, product, process, initialStep, waferQty, 
                releaseDate, dueDate, priority);

            var lot = CreateLot(wipInfo);

            return lot;
        }

        private static FabWipInfo CreateInstancingFabWipInfo(string lotID, FabProduct product, FabSemiconProcess process, 
            SemiconStep initialStep, double waferQty, DateTime releaseDate, DateTime dueDate, int priority = 1)
        {
            FabWipInfo wip = new FabWipInfo();

            wip.LineID = product.LineID;
            wip.LotID = lotID;
            wip.UnitQty = waferQty;
            wip.WipStepID = initialStep.StepID;

            wip.InitialStep = initialStep;
            wip.Product = product;
            wip.Process = process;

            wip.WipStateTime = releaseDate;
            wip.FabInTime = releaseDate;
            wip.DueDate = DateTime.MaxValue; //Default

            // ## InPlan DueDate
            // [1] Create by Pegging: B/W InTarget의 DueDate를 Pegging 된 DueDate로 취급.
            // [2] Use FAB_IN_PLAN: B/W InTarget과 FAB_IN_PLAN을 맵핑시키는 별도의 작업이 필요. (지정된 값보다도 우선 사용)
            // [3] Maintain WIP Level: PMix 계산에 사용된 InTarget의 DueDate를 Pegging된 DueDate로 취급.

            EntityHelper.AssignLotDueDate(wip, dueDate);

            wip.WipState = "WAIT";
            wip.CurrentState = EntityState.WAIT;

            wip.LotPriorityValue = priority;
            wip.LotPriorityStatus = EntityHelper.GetLotPriorityStatus(priority);

            return wip;
        }

        internal static FabSemiconStep CreateFabSemiconStep(ROUTE_STEP entity, FabSemiconProcess proc)
        {
            FabSemiconStep obj = new FabSemiconStep();

            obj.Sequence = entity.STEP_SEQ;
            //obj.StepType = entity.STEP_LEVEL.ToString();
            obj.StepLevel = entity.STEP_LEVEL;
            obj.IsSimulationStep = Helper.GetSimulationStepLevels().Contains(entity.STEP_LEVEL);
            obj.IsWipMoveCollectStepLevel = Helper.GetWipMoveCollectStepLevels().Contains(entity.STEP_LEVEL);

            // 이 시점에서 아직 MAIN Route의 STD Route가 무엇인지는 알 수 없음.
            obj.IsStdStep = proc.RouteType == RouteType.STANDARD;

            obj.StepID = entity.STEP_ID;
            obj.AreaID = entity.AREA_ID;
            obj.LayerID = entity.LAYER_ID;
            obj.IsPhotoStep = entity.AREA_ID == Helper.GetConfig(ArgsGroup.Simulation_Report).photoArea;

            obj.CumulativeYield = entity.CUM_YIELD;
            obj.WaitCT = TimeSpan.FromMinutes(entity.QT_MINS);
            obj.RunCT = TimeSpan.FromMinutes(entity.PT_MINS);
            obj.CT = TimeSpan.FromMinutes(entity.CT_MINS);
            obj.PotDays = entity.POT_DAYS;

            return obj; 
        }

        internal static FabSemiconStep CreateFabSemiconStepLot(ROUTE_STEP_LOT entity, FabSemiconProcess proc)
        {
            FabSemiconStep obj = new FabSemiconStep();

            obj.Sequence = entity.STEP_SEQ;
            obj.StepLevel = entity.STEP_LEVEL;
            obj.IsSimulationStep = Helper.GetSimulationStepLevels().Contains(entity.STEP_LEVEL);
            obj.IsWipMoveCollectStepLevel = Helper.GetWipMoveCollectStepLevels().Contains(entity.STEP_LEVEL);

            // 아직 PRODUCT 테이블 로딩하기 이전.
            obj.IsStdStep = false;

            obj.StepID = entity.STEP_ID;
            obj.AreaID = entity.AREA_ID;
            obj.LayerID = entity.LAYER_ID;
            obj.IsPhotoStep = entity.AREA_ID == Helper.GetConfig(ArgsGroup.Simulation_Report).photoArea;

            obj.CumulativeYield = entity.CUM_YIELD;
            obj.WaitCT = TimeSpan.FromMinutes(entity.QT_MINS);
            obj.RunCT = TimeSpan.FromMinutes(entity.PT_MINS);
            obj.CT = TimeSpan.FromMinutes(entity.CT_MINS);
            obj.PotDays = entity.POT_DAYS;

            return obj;
        }

        internal static FabSemiconStep CreateReworkDummyStep(FabSemiconLot lot, FabSemiconStep step, FabSemiconStep returnStep)
        {
            FabSemiconStep obj = new FabSemiconStep();

            obj.IsReworkDummyStep = true;
            obj.ReworkNextStep = returnStep;
            obj.PartStepDict = step.PartStepDict;

            var attr = step.PartStepDict.SafeGet(lot.CurrentPartID);
            if (attr != null)
            {
                var orgStackInfo = attr.StackInfoDict.SafeGet(step);
                if (orgStackInfo != null)
                    attr.StackInfoDict.Add(obj, orgStackInfo); // ReworkStep에서 Stack정보를 찾도록
            }

            obj.Sequence = step.Sequence;
            //obj.StepType = entity.STEP_LEVEL.ToString();
            obj.StepLevel = step.StepLevel;
            obj.IsSimulationStep = step.IsSimulationStep;
            obj.IsWipMoveCollectStepLevel = step.IsWipMoveCollectStepLevel;

            obj.StepID = step.StepID;
            obj.AreaID = step.AreaID;
            obj.LayerID = step.LayerID;
            obj.IsPhotoStep = step.IsPhotoStep;

            obj.CumulativeYield = step.CumulativeYield;
            obj.WaitCT = step.WaitCT;
            obj.RunCT = step.RunCT;
            obj.CT = step.CT;
            obj.PotDays = step.PotDays;

            return obj;
        }

        internal static FabSemiconEqp CreateEqp(EQP entity)
        {
            FabSemiconEqp eqp = entity.ToFabSemiconEqp();

            // 여러 작업물을 동시에 처리할 수 있는 설비에서
            // 두 번째 또는 그 이후의 RUN 재공이 이전 RUN 재공의 작업 시간으로 인해 시뮬레이션 시작 시간 이후에만 투입 가능할 때 
            // 이전 작업물의 작업 시간을 무시하고, 지정한 시간이 즉시 투입할 수 있는지 여부를 설정하거나 가져옵니다.

            // v126에서 버그 수정되어 false 처리
            eqp.ForceAddRunWip = false;

            eqp.LineID_ = entity.LINE_ID; // 원본 컬럼은 readonly...

            eqp.State = entity.EQP_STATUS == "DOWN" ? ResourceState.Down : ResourceState.Up;

            if (Helper.GetConfig(ArgsGroup.Resource_Eqp).useEqpUtilization == "Y")
                eqp.Utilization = entity.UTILIZATION;

            eqp.ScannerGeneration = eqp.IsPhotoEqp ? entity.EQP_GROUP : "-";

            SetEqpSimType(entity, eqp);

            SetPresetInfo(entity, eqp);

            InputMart.Instance.FabSemiconEqp.ImportRow(eqp);

            return eqp;
        }

        private static void SetPresetInfo(EQP entity, FabSemiconEqp eqp)
        {
            var presetMode = Helper.GetConfig(ArgsGroup.Logic_Dispatching).presetMode;

            var presetList = InputMart.Instance.PRESETView.FindRows(presetMode, entity.EQP_ID);

            foreach (var info in presetList)
            {
                var preset = InputMart.Instance.FabWeightPresetView.FindRows(info.PRESET_ID).FirstOrDefault();

                if (preset == null)
                    continue;

                if (eqp.PresetDict.ContainsKey(info.PROGRAM_ID))
                    continue;

                eqp.PresetDict.Add(info.PROGRAM_ID, preset);

                if (info.PROGRAM_ID == "DEFAULT")
                    eqp.Preset = preset;
            }

            if (eqp.Preset != null && eqp.Preset.FactorList.Any())
            {
                if ((eqp.Preset as FabWeightPreset).DispatcherType == DispatcherType.WeightSum)
                    eqp.DispatcherType = DispatcherType.WeightSum;
                else if ((eqp.Preset as FabWeightPreset).DispatcherType == DispatcherType.WeightSorted)
                    eqp.DispatcherType = DispatcherType.WeightSorted;
                else
                    eqp.DispatcherType = DispatcherType.Fifo;
            }
            else
                eqp.DispatcherType = DispatcherType.Fifo;

            if (eqp.DispatcherType == DispatcherType.Fifo)
                OutputHelper.WritePersistLog(LogType.WARNING, "PRESET", Helper.CreateKey(eqp.ResID, presetMode), "Missing Preset");
        }

        private static void SetEqpSimType(EQP entity, FabSemiconEqp eqp)
        {
            bool chamberToinline = Helper.GetConfig(ArgsGroup.Resource_SimType).chamberToInline == "Y";
            bool batchToInline = Helper.GetConfig(ArgsGroup.Resource_SimType).batchToInline == "Y";

            if (entity.SIM_TYPE == "Chamber")
            {
                eqp.OrgSimType = SimEqpType.Chamber.ToString();
                eqp.SimType = SimEqpType.Inline; // Chamber설비는 config 에 관계없이 항상 Inline으로 전환.
            }
            else if (entity.SIM_TYPE == "ParallelChamber")
            {
                eqp.OrgSimType = SimEqpType.ParallelChamber.ToString();
                eqp.SimType = chamberToinline ? SimEqpType.Inline : SimEqpType.ParallelChamber;
            }
            else if (entity.SIM_TYPE == "LotBatch")
            {
                eqp.OrgSimType = SimEqpType.LotBatch.ToString();
                eqp.SimType = batchToInline ? SimEqpType.Inline : SimEqpType.LotBatch;
            }
            else if (entity.SIM_TYPE == "BatchInline")
            {
                eqp.OrgSimType = SimEqpType.BatchInline.ToString();
                eqp.SimType = batchToInline ? SimEqpType.Inline : SimEqpType.BatchInline;
            }
            else if (entity.SIM_TYPE == "UnitBatch")
            {
                // UnitBatch는 Inline 전환하지 않음.
                eqp.OrgSimType = SimEqpType.UnitBatch.ToString();
                eqp.SimType = SimEqpType.UnitBatch;

                eqp.UnitBatchInfo = new UnitBatchInfo();
            }
            else
            {
                eqp.OrgSimType = SimEqpType.Inline.ToString();
                eqp.SimType = SimEqpType.Inline;
            }
        }

        internal static void CreateSubEqp(EQP entity, FabSemiconEqp parent)
        {
            FabSemiconSubEqp sub = new FabSemiconSubEqp();
            sub.Parent = parent;
            sub.SubEqpID = entity.EQP_ID;
            sub.State = entity.EQP_STATUS == "DOWN" ? ResourceState.Down : ResourceState.Up;

            parent.AddSubEqp(sub);

            InputMart.Instance.FabSemiconSubEqp.ImportRow(sub);
        }

        public static FabWipInfo CreateFabWipInfo(WIP entity, FabProduct prod, FabSemiconProcess proc, FabSemiconStep initialStep)
        {
            FabWipInfo wip = new FabWipInfo();

            wip.LineID = entity.LINE_ID;
            wip.LotID = entity.LOT_ID;
            wip.UnitQty = entity.WAFER_QTY;
            wip.WipStepID = entity.STEP_ID;

            wip.InitialStep = initialStep;
            wip.Product = prod;
            wip.Process = proc;

            wip.LotPriorityStatus = entity.LOT_PRIORITY_STATUS;
            wip.LotPriorityValue = EntityHelper.GetLotPriorityValue(entity.LOT_PRIORITY_STATUS);

            wip.WipStateTime = entity.LOT_STATE_CHANGE_DATETIME;
            wip.FabInTime = entity.LOT_START_TIME;
            wip.DueDate = DateTime.MaxValue; //Default

            SetWipState(entity, wip);

            if (entity.EQP_ID.IsNullOrEmpty() == false)
                wip.InitialEqp = InputMart.Instance.FabSemiconEqpView.FindRows(entity.EQP_ID).FirstOrDefault();

            wip.IsBohRun = wip.InitialEqp != null && wip.CurrentState == EntityState.RUN;

            if (wip.CurrentState == EntityState.HOLD)
            {
                wip.HoldCode = entity.HOLD_CODE;

                var hold = InputMart.Instance.HOLD_TIME.Rows.Find(entity.HOLD_CODE);
                if (hold != null)
                {
                    wip.HoldTime = Time.FromHours(hold.HOLD_TIME_HRS).Floor();

                    if (Helper.GetConfig(ArgsGroup.Simulation_Run).stochasticTables.Contains("HOLD_TIME"))
                    {
                        wip.HoldTimePdfConfig = CreateStochasticConfig(hold.HOLD_TIME_HRS_PDF);
                    }
                }
                else
                {
                    var holdRemainTime = wip.WipStateTime - ModelContext.Current.StartTime;
                    if (holdRemainTime > TimeSpan.Zero)
                        wip.HoldTime = holdRemainTime;
                    else
                        wip.HoldTime = TimeSpan.FromDays(InputMart.Instance.GlobalParameters.period);
                }
            }

            return wip;
        }

        private static void SetWipState(WIP entity, FabWipInfo wip)
        {
            wip.WipState = entity.LOT_STATE;

            if (entity.LOT_STATE == "RUN" || entity.LOT_STATE == "SETUP" || entity.LOT_STATE == "STAGED")
                wip.CurrentState = EntityState.RUN;
            else if (entity.LOT_STATE == "HOLD")
                wip.CurrentState = EntityState.HOLD;
            else if (entity.LOT_STATE == "MOVE")
                wip.CurrentState = EntityState.MOVE;
            else
                wip.CurrentState = EntityState.WAIT;
        }

        internal static WeightFactor CreateWeightFactor(WEIGHT_PRESETS presetEntity, WEIGHT_FACTOR factorEntity)
        {
            FactorType t = Helper.Parse<FactorType>(factorEntity.FACTOR_TYPE, FactorType.LOTTYPE);

            FabWeightFactor factor = new FabWeightFactor(presetEntity.FACTOR_ID, presetEntity.FACTOR_WEIGHT, presetEntity.FACTOR_SEQ,
                t, OrderType.DESC, presetEntity.CRITERIA);

            return factor;
        }

        internal static EqpArrange CreateBackupArrange(PartStepAttribute attr, FabSemiconEqp backupEqp, string recipeID, string reticleID)
        {
            EqpArrange backupArr = new EqpArrange();

            backupArr.Eqp = backupEqp;
            backupArr.LineID = backupEqp.LineID_;
            backupArr.PartID = attr.PartID;
            backupArr.StepID = attr.StepID;
            backupArr.EqpID = backupEqp.ResID;
            backupArr.RecipeID = recipeID;
            backupArr.ToolingName = reticleID;
            backupArr.IsBackup = true;

            return backupArr;
        }

        internal static StochasticConfig CreateStochasticConfig(string pdfStr)
        {
            if (pdfStr.IsNullOrEmpty())
                return null;

            pdfStr = pdfStr.Remove(pdfStr.Length - 1);
            var separateIndex = pdfStr.IndexOf('(');
            if (separateIndex < 0)
                return null;

            StochasticConfig stConfig = new StochasticConfig();
            var pdf = pdfStr.Substring(0, separateIndex);
            var param = pdfStr.Substring(separateIndex + 1);
            if (pdf == DistFunctionType.Normal.ToString())
            {
                stConfig.DistFunction = DistFunctionType.Normal;

                var split = param.Split(',');
                if (split.Length != 2)
                    return null;

                if (double.TryParse(split.First(), out double mean))
                    stConfig.Mean = mean;
                else return null;

                if (double.TryParse(split.Last(), out double stddev))
                    stConfig.StdDev = stddev;
                else return null;
            }
            else if (pdf == DistFunctionType.Exponential.ToString())
            {
                stConfig.DistFunction = DistFunctionType.Exponential;

                if (double.TryParse(param, out double mean))
                    stConfig.Mean = mean;

                if (mean == 0)
                    return null;
            }
            else if (pdf == DistFunctionType.Uniform.ToString())
            {
                stConfig.DistFunction = DistFunctionType.Uniform;

                var split = param.Split(',');
                if (split.Length != 2)
                    return null;

                if (double.TryParse(split.First(), out double min))
                    stConfig.Min = min;
                else return null;

                if (double.TryParse(split.Last(), out double max))
                    stConfig.Max = max;
                else return null;

                if (min > max)
                    return null;
            }
            else
            {
                stConfig.DistFunction = DistFunctionType.Constant;

                if (double.TryParse(param, out double mean))
                    stConfig.Mean = mean;
            }

            return stConfig;
        }

        internal static DeliveryTimeInfo CreateDeliveryTimeInfo(DELIVERY_TIME item)
        {
            DeliveryTimeInfo info = new DeliveryTimeInfo();
            info.FromBayID = item.FROM_BAY_ID;
            info.ToBayID = item.TO_BAY_ID;
            info.DeliveryMins = item.DELIVERY_MIN;
            info.PenaltyMins = item.PENALTY_MIN;

            if (Helper.GetConfig(ArgsGroup.Simulation_Run).stochasticTables.Contains("DELIVERY_TIME"))
                info.DeliveryMinsPdfConfig = CreateStochasticConfig(item.DELIVERY_MIN_PDF);

            return info;
        }

        internal static LoadInfo CreateFabPlanInfo(ILot lot, FabSemiconStep currentStep, bool beforeTransfer = true)
        {
            var info = new FabPlanInfo(currentStep);

            var sLot = lot as FabSemiconLot;
            info.LotID = sLot.LotID;
            info.ProductID = sLot.CurrentProductID;
            info.PartID = sLot.FabProduct.PartID;
            info.UnitQty = sLot.UnitQty;

            // LoadInfo의 attribute는 아니지만, 유사한 속성이라서 함께 세팅
            sLot.CurrentAttribute = currentStep.PartStepDict.SafeGet(sLot.CurrentPartID);

            if (sLot.FabWipInfo.IsBohRun && sLot.FabWipInfo.InitialStep == currentStep)
                info.Arrange = sLot.CurrentAttribute.CurrentArranges.Where(x => x.EqpID == sLot.FabWipInfo.InitialEqp.ResID).FirstOrDefault();

            // HOLD 걸릴경우, Hold Exit 시점을 ArrivalTime으로 취급하기 위헤, ON_DISPATCH_IN0 시점에 업데이트.
            // Delivery Time이 여러개일 경우, 설비까지 정해진 시점에 다시 업데이트.
            //info.ArrivalTimeStr = DateTime.MinValue.ToString("yyyy-MM-dd HH:mm:ss");

            if (beforeTransfer)
            {
                // ## Step Skip 조건인지 확인: 같은 Route 일 때만
                // currentStep equal toStep
                var fromStep = sLot.CurrentStep as FabSemiconStep;
                if (fromStep.Process == currentStep.Process) // Rework등의 이유로 다른 Route에서 진입시에는 SkipTime 적용하면 안됨.
                {
                    if (currentStep.StepSkipTime > TimeSpan.Zero)
                        info.HasStepSkipTime = true;
                }
            }

            return info;
        }

        internal static BOMInfo CreateBOMInfo(IGrouping<string, BOM> group, FabProduct toPart)
        {
            BOMInfo info = new BOMInfo();
            info.ToPart = toPart;
            info.ToRoute = toPart.MainRoute;
            info.MergeStep = toPart.MainRoute.FindStep(group.First().TO_STEP_ID) as FabSemiconStep;
            info.BOMLevel = group.First().BOM_LEVEL;

            foreach (var item in group)
            {
                // FromPart가 여러 종류의 ToPart로 맵핑 될 경우는 고려하지 않음.
                var fromPart = InputMart.Instance.FabProductMfgPartView.FindRows(item.FROM_PART_ID).FirstOrDefault();
                if (fromPart == null)
                    continue;

                info.FromParts.Add(fromPart, item.FROM_QTY);

                fromPart.BOMInfo = info;
            }

            return info;
        }

        internal static StepCapaInfo CreateStepCapaInfo(STEP_CAPA entity)
        {
            StepCapaInfo info = new StepCapaInfo();
            info.CapaKey = entity.CAPA_KEY;
            info.CapaLimit = entity.CAPA_LIMIT;
            info.CurrentCapa = entity.CAPA_INIT;

            return info;
        }
    }
}