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
using Mozart.SeePlan.Semicon.DataModel;

namespace FabSimulator.Logic.Pegging
{
    [FeatureBind()]
    public partial class PREPARE_WIP
    {
        public PegPart PREPARE_WIP0(PegPart pegPart, ref bool handled, PegPart prevReturnValue)
        {
            var demandList = InputMart.Instance.FabProdPlan.Rows.Select(x => x.Product.StdProductID).Distinct().ToList();

            foreach (FabWipInfo info in InputMart.Instance.FabWipInfo.Values)
            {
                if (PegHelper.CheckNoDemandWip(info, demandList))
                {
                    var reason = "Missing Demand";
                    OutputHelper.WriteUnpegHistory(info, info.UnitQty, reason);
                    continue;
                }

                SemiconStep stdStep = info.FabProduct.MainRoute.FindStep(info.WipStepID);

                if (stdStep == null)
                {
                    var reason = "Missing StdStep";
                    OutputHelper.WriteUnpegHistory(info, info.UnitQty, reason);
                    continue;
                }

                if (info.InitialStep == null)
                {
                    var reason = "Missing step";
                    OutputHelper.WriteUnpegHistory(info, info.UnitQty, reason);
                    continue;
                }

                FabPlanWip wip = PegHelper.CreatePlanWip(info, stdStep);

                InputMart.Instance.FabPlanWip.ImportRow(wip);
            }

            return pegPart;
        }
    }
}