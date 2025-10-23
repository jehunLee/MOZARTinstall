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
    public partial class PREPARE_TARGET
    {
        public PegPart PREPARE_TARGET0(PegPart pegPart, ref bool handled, PegPart prevReturnValue)
        {
            PegHelper.PrepareTarget(pegPart);

            return pegPart;
        }
    }
}