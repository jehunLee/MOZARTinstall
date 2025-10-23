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
    public partial class APPLY_YIELD
    {
        public double GET_YIELD0(Mozart.SeePlan.Pegging.PegPart pegPart, ref bool handled, double prevReturnValue)
        {
            FabSemiconPegPart pp = pegPart as FabSemiconPegPart;

            double stepYield = PegHelper.FindYield((pp.Product as StdProduct).StdProductID, pp.CurrentStep);

            return stepYield;
        }
    }
}