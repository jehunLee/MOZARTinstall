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
    public partial class WRITE_UNPEG
    {
        public void WRITE_UNPEG0(Mozart.SeePlan.Pegging.PegPart pegPart, ref bool handled)
        {
            foreach (FabPlanWip wip in InputMart.Instance.FabPlanWip.Rows)
            {
                if (wip.Qty == 0)
                    continue;

                if (wip.MapCount == 0)
                {
                    OutputHelper.WriteUnpegHistory(wip.Wip as FabWipInfo, wip.Qty, "No Target");
                }
                else if (wip.Qty > 0)
                {
                    OutputHelper.WriteUnpegHistory(wip.Wip as FabWipInfo, wip.Qty, "Excess");
                }
            }
        }
    }
}