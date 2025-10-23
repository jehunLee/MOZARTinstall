using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
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

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class BucketControl
    {
        public Time GET_BUCKET_TIME0(Mozart.SeePlan.Simulation.IHandlingBatch hb, AoBucketer bucketer, ref bool handled, Time prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.ApplyPTMinsAtBOH)
            {
                lot.ApplyPTMinsAtBOH = false; // 일회용

                return lot.CurrentFabStep.RunCT;
            }

            return lot.CurrentFabStep.CT;

            //var stepCT = lot.FabProduct.GetStepCT(lot.LineID, lot.CurrentStepID);
            //if (stepCT == null)
            //    return Time.FromMinutes(Helper.GetConfig().DefaultTimeShiftingMins);

            //// Wip의 Route 가 Product의 Route와 다른경우 (e.g. REWORK)
            //return stepCT.Value;
        }
    }
}