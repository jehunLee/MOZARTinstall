using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Pegging;
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

namespace FabSimulator.Logic.Pegging
{
    [FeatureBind()]
    public partial class WRITE_TARGET
    {
        public void WRITE_TARGET0(PegPart pegPart, bool isOut, ref bool handled)
        {
            var pp = pegPart as FabSemiconPegPart;

            FabSemiconStep step = pp.CurrentStep as FabSemiconStep;

            foreach (FabSemiconPegTarget pt in pp.PegTargetList)
            {
                OutputHelper.WriteStepTarget(pt, step, pp.Product.ProductID, isOut);
            }
        }

        public object GET_STEP_PLAN_KEY0(PegPart pegPart, ref bool handled, object prevReturnValue)
        {
            FabSemiconPegPart pp = pegPart as FabSemiconPegPart;

            return pp.ProductID;
        }

        public StepTarget CREATE_STEP_TARGET0(PegTarget pegTarget, object stepPlanKey, Step step, bool isRun, ref bool handled, Mozart.SeePlan.DataModel.StepTarget prevReturnValue)
        {
            FabSemiconStepTarget st = new FabSemiconStepTarget(stepPlanKey, step, pegTarget.Qty, pegTarget.DueDate, isRun);
            st.Mo = pegTarget.MoPlan as FabSemiconMoPlan;

            return st;
        }
    }
}