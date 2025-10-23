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
    public partial class SHIFT_TAT
    {
        public TimeSpan GET_TAT0(Mozart.SeePlan.Pegging.PegPart pegPart, bool isRun, ref bool handled, TimeSpan prevReturnValue)
        {
            FabSemiconStep step = pegPart.CurrentStep as FabSemiconStep;

            var waitCT = step.WaitCT + (TimeSpan)step.StepSkipTime;

            return isRun ? step.RunCT : waitCT;
        }
    }
}