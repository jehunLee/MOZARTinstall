using Mozart.SeePlan.Simulation;
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
    public partial class Misc
    {
        public string[] GET_CHAMBER_IDS0(Mozart.SeePlan.Simulation.AoEquipment aeqp, ref bool handled, string[] prevReturnValue)
        {
            var feqp = aeqp as FabAoEquipment;

            return feqp.Eqp.SubEqps.Select(x => x.SubEqpID).ToArray();
        }

        public int GET_CHAMBER_CAPACITY0(AoEquipment aeqp, ref bool handled, int prevReturnValue)
        {
            var feqp = aeqp as FabAoEquipment;

            if (feqp.Eqp.HasSubEqps)
                return feqp.Eqp.SubEqpCount;

            return 1;
        }
    }
}