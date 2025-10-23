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
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan.Semicon;
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
using Mozart.Data.Entity;
using Mozart.Simulation.Engine;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class BopHelper
    {
        internal static List<SemiconStep> GetRouteSteps(FabSemiconProcess proc, bool isLotRoute = false)
        {
            object[] steps;
            List<SemiconStep> results;
            if (isLotRoute)
            {
                steps = TempMart.Instance.ROUTE_STEP_LOTView.FindRows(proc.ProcessID).OrderBy(x => x.STEP_SEQ).ToArray();
                SetProcessPotDays(proc, steps, isLotRoute);
                results = CreateSemiconStepsLot(proc, steps as ROUTE_STEP_LOT[]);
            }
            else
            {
                steps = TempMart.Instance.ROUTE_STEPView.FindRows(proc.ProcessID).OrderBy(x => x.STEP_SEQ).ToArray();
                SetProcessPotDays(proc, steps, isLotRoute);
                results = CreateSemiconSteps(proc, steps as ROUTE_STEP[]);
            }

            return results;
        }

        private static List<SemiconStep> CreateSemiconSteps(FabSemiconProcess proc, ROUTE_STEP[] steps)
        {
            List<SemiconStep> results = new List<SemiconStep>();

            for (int i = 0; i < steps.Length; i++)
            {
                var currentStep = steps[i];

                FabSemiconStep semiconStep = CreateHelper.CreateFabSemiconStep(currentStep, proc);

                var nextStep = i == steps.Length - 1 ? null : steps[i + 1];
                if (nextStep != null)
                    semiconStep.StepYieldRate = currentStep.CUM_YIELD / nextStep.CUM_YIELD;

                if (i > 0)
                {
                    var prevStep = steps[i - 1];

                    // ## StepSeq와 무관하게, 항상 아래 규칙을 적용.
                    // Step간 이동시간(FW) = Transfer Time(or DeliveryTime) + StepSkipTime
                    // Step간 이동시간(BW) = Wait CT + StepSkipTime

                    semiconStep.StepSkipTime = ((Time)(TimeSpan.FromDays(prevStep.POT_DAYS - currentStep.POT_DAYS) - TimeSpan.FromMinutes(currentStep.CT_MINS))).Floor();

                    if (semiconStep.StepSkipTime < TimeSpan.Zero)
                        semiconStep.StepSkipTime = TimeSpan.Zero;

                    SetCurrentStadardizedPotDays(semiconStep, proc);
                }

                results.Add(semiconStep);
            }

            return results;
        }

        private static List<SemiconStep> CreateSemiconStepsLot(FabSemiconProcess proc, ROUTE_STEP_LOT[] steps)
        {
            List<SemiconStep> results = new List<SemiconStep>();

            for (int i = 0; i < steps.Length; i++)
            {
                var currentStep = steps[i];

                FabSemiconStep semiconStep = CreateHelper.CreateFabSemiconStepLot(currentStep, proc);

                var nextStep = i == steps.Length - 1 ? null : steps[i + 1];
                if (nextStep != null)
                    semiconStep.StepYieldRate = currentStep.CUM_YIELD / nextStep.CUM_YIELD;

                if (i > 0)
                {
                    var prevStep = steps[i - 1];

                    // ## StepSeq와 무관하게, 항상 아래 규칙을 적용.
                    // Step간 이동시간(FW) = Transfer Time(or DeliveryTime) + StepSkipTime
                    // Step간 이동시간(BW) = Wait CT + StepSkipTime

                    semiconStep.StepSkipTime = ((Time)(TimeSpan.FromDays(prevStep.POT_DAYS - currentStep.POT_DAYS) - TimeSpan.FromMinutes(currentStep.CT_MINS))).Floor();

                    if (semiconStep.StepSkipTime < TimeSpan.Zero)
                        semiconStep.StepSkipTime = TimeSpan.Zero;

                    SetCurrentStadardizedPotDays(semiconStep, proc);
                }

                results.Add(semiconStep);
            }

            return results;
        }

        public static List<FabSemiconStep> GetRouteSteps(string partID, string stepID)
        {
            List<FabSemiconStep> steps = new List<FabSemiconStep>();
            var procs = InputMart.Instance.FabProductPartView.FindRows(partID).Select(x => x.Process).ToList();

            // rework route의 main route는 WIP_PARAM에 정의되므로, lot에 따라 PART가 달라질 수 있음.
            // => 어떤 PART로 쓰일지 알 수 없으므로 stepID가 존재하면 일단 PartStepAttribute를 생성하여 arrange를 찾을 수 있도록 함.
            // LotRoute 또한 FabProduct에 정의되어 있지 않으므로, 찾도록 추가 함.
            InputMart.Instance.FabSemiconProcess.Where(x => x.RouteType == RouteType.REWORK || x.RouteType == RouteType.LOT).ForEach(procs.Add);

            foreach (FabSemiconProcess proc in procs)
            {
                var step = proc.FindStep(stepID) as FabSemiconStep;
                if (step != null)
                    steps.Add(step);
            }

            return steps;
        }

        internal static void BuildBop(SemiconProcess proc, IList<SemiconStep> steps)
        {
            BopBuilder bb = new BopBuilder(BopType.SINGLE_FLOW);

            bb.CompareSteps = CompareSteps;

            bb.BuildBop(proc, steps);
        }
        public static int CompareSteps(SemiconStep x, SemiconStep y)
        {
            if (object.ReferenceEquals(x, y))
                return 0;

            return x.Sequence.CompareTo(y.Sequence);
        }

        internal static StdProduct GetStdProduct(string stdProductID)
        {
            if (stdProductID == null)
                return null;

            return InputMart.Instance.StdProductView.FindRows(stdProductID).FirstOrDefault();
        }

        internal static StdProduct GetOrAddStdProduct(FabProduct mfgPart, PRODUCT entity)
        {
            var stdProd = GetStdProduct(mfgPart.StdProductID);
            if (stdProd == null)
            {
                stdProd = CreateHelper.CreateStdProduct(mfgPart, entity);
            }

            if (stdProd == null)
                return null;

            mfgPart.StdProduct = stdProd;
            stdProd.Products.Add(mfgPart);

            return stdProd;
        }

        internal static bool IsFabInOrFabOut(string stepID)
        {
            return stepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID || stepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabOutStepID;
        }

        internal static FabSemiconStep GetReworkDummyRouteStep(FabSemiconLot lot, FabSemiconLot content, FabSemiconStep returnStep)
        {
            var reworkDummyRoute = new FabSemiconProcess();
            reworkDummyRoute.RouteID = Helper.CreateKey(lot.CurrentProcessID, "REWORK");

            var reworkDummyStep = CreateHelper.CreateReworkDummyStep(lot, lot.CurrentFabStep, returnStep);
            List<SemiconStep> steps = new List<SemiconStep>
                {
                    reworkDummyStep
                };

            BopHelper.BuildBop(reworkDummyRoute, steps);

            // GET_NEXT_STEP_REWORK 에서 처리.
            //content.Route = reworkDummyRoute;

            return reworkDummyStep;
        }

        internal static void SetStandardPotDays()
        {
            var fabInStepId = Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID;
            var fabInSteps = TempMart.Instance.ROUTE_STEP.Where(x => x.STEP_ID == fabInStepId).ToList();

            if (fabInSteps.IsNullOrEmpty() == false)
                InputMart.Instance.StandardPotDays = fabInSteps.Select(x => x.POT_DAYS).Max();

            // 기준정보 누락에 대한 보완처리
            if (InputMart.Instance.StandardPotDays <= 0)
                InputMart.Instance.StandardPotDays = Helper.GetConfig(ArgsGroup.Lot_InPlan).targetCT;
        }

        private static void SetProcessPotDays(FabSemiconProcess proc, object[] steps, bool isLotRoute = false)
        {
            var fabInStepId = Helper.GetConfig(ArgsGroup.Bop_Step).fabInStepID;

            object fabInStep;
            if (isLotRoute)
            {
                fabInStep = steps.Select(x => x as ROUTE_STEP_LOT).Where(x => x.STEP_ID == fabInStepId).FirstOrDefault();
                if (fabInStep != null)
                    proc.PotDays = (fabInStep as ROUTE_STEP_LOT).POT_DAYS;
            }
            else
            {
                fabInStep = steps.Select(x => x as ROUTE_STEP).Where(x => x.STEP_ID == fabInStepId).FirstOrDefault();
                if (fabInStep != null)
                    proc.PotDays = (fabInStep as ROUTE_STEP).POT_DAYS;
            }

            // 기준정보 누락에 대한 보완처리
            if (proc.PotDays <= 0)
                proc.PotDays = Helper.GetConfig(ArgsGroup.Lot_InPlan).targetCT;
        }

        private static void SetCurrentStadardizedPotDays(FabSemiconStep semiconStep, FabSemiconProcess proc)
        {
            // 아직 step에 proc은 할당되지 않은 상태라 proc을 파라미터로 받음.
            var standardized = semiconStep.PotDays * (InputMart.Instance.StandardPotDays / proc.PotDays);

            semiconStep.StandardizedPotDays = standardized;
        }

        internal static void SetPartStepAttributes(FabProduct mfgPart, FabSemiconStep step)
        {
            if (step.IsStdStep == false)
            {
                if (mfgPart.StdRoute != null)
                {
                    // Route에 StepID가 중복이 있으면 아래 FindStep함수에서 키 중복이 발생할 수 있음.
                    // StepSeq까지 PK로 잡혀있기는 하지만, Unique한 StepID를 사용하는 것을 원칙으로 함.
                    var stdStep = mfgPart.StdRoute.FindStep(step.StepID);
                    if (stdStep != null)
                        step.IsStdStep = true;
                }
            }

            if (IsFabInOrFabOut(step.StepID))
                return;

            //TODO: PRODUCT OnAction에서 호출할 때, 속도 개선의 여지가 있어 보이나, 여기서 생략하면 ARRANGE 로딩시에 대신 오래 걸리게 됨.
            var attr = ArrangeHelper.GetOrAddPartStepAttribute(mfgPart.PartID, step.StepID);

            attr.LayerID = Helper.ExtractNumber(step.LayerID);
            attr.MaxStepLevel = Math.Max(attr.MaxStepLevel, step.StepLevel);
        }

        internal static bool IsStatisticalAnalysisStep(this FabSemiconStep step, bool checkStepLevel = true)
        {
            if (InputMart.Instance.UseBOM)
                return true;

            if (checkStepLevel == false)
                return step.IsStdStep;

            return step.IsStdStep && step.IsWipMoveCollectStepLevel;
        }

        internal static void SetBOMParentChildMap(string childLotID, string paramValue)
        {
            try
            {
                if (InputMart.Instance.BOMParentChildMap == null)
                    InputMart.Instance.BOMParentChildMap = new MultiDictionary<string, string>();

                var paramSplit = paramValue.Split(';');
                var bomSplit = paramSplit.Where(x => x.IsNullOrEmpty() == false).Select(x => x.Split('=')).OrderByDescending(x => x[0]).ToList();

                for (int i = 0; i < bomSplit.Count; i++)
                {
                    if (i == 0)
                        InputMart.Instance.BOMParentChildMap.Add(bomSplit[i][1], childLotID);
                    else
                        InputMart.Instance.BOMParentChildMap.Add(bomSplit[i][1], bomSplit[i - 1][1]);
                }
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}