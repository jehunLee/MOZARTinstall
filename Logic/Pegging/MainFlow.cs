using Mozart.SeePlan.Pegging;
using Mozart.SeePlan.DataModel;
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
    public partial class MainFlow
    {
        public Step GETLASTPEGGINGSTEP(Mozart.SeePlan.Pegging.PegPart pegPart)
        {
            FabSemiconPegPart pp = pegPart as FabSemiconPegPart;
            FabSemiconProcess proc = pp.Product.Process as FabSemiconProcess;

            return proc.LastStep;
        }

        public Step GETPREVPEGGINGSTEP(PegPart pegPart, Step currentStep)
        {
            return currentStep.GetDefaultPrevStep();
        }
    }
}