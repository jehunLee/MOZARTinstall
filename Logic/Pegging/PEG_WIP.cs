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
    public partial class PEG_WIP
    {
        public IList<Mozart.SeePlan.Pegging.IMaterial> GET_WIPS0(Mozart.SeePlan.Pegging.PegPart pegPart, bool isRun, ref bool handled, IList<IMaterial> prevReturnValue)
        {
            var pp = pegPart as FabSemiconPegPart;
            FabSemiconStep step = pp.CurrentStep as FabSemiconStep;

            var wips = InputMart.Instance.FabPlanWipView.FindRows(step);

            List<IMaterial> result = new List<IMaterial>();

            if (wips != null)
            {
                foreach (FabPlanWip wip in wips)
                {
                    if (wip.Qty == 0)
                        continue;

                    if (isRun != wip.IsRunWip)
                        continue;

                    if (wip.StdProduct.StdProductID == pp.ProductID)
                    {
                        wip.MapCount++;
                        result.Add(wip);
                    }
                }
            }

            return result;
        }

        public void WRITE_PEG0(PegTarget target, IMaterial m, double qty, ref bool handled)
        {
            OutputHelper.WritePegHistory(target, m as FabPlanWip, Math.Ceiling(qty));

            m.Qty = Math.Floor(m.Qty);
        }

        public bool IS_REMOVE_EMPTY_TARGET0(PegPart pegpart, ref bool handled, bool prevReturnValue)
        {
            return true;
        }
    }
}