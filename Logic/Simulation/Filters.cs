using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using FabSimulator.Persists;
using FabSimulator.Outputs;
using FabSimulator.Inputs;
using FabSimulator.DataModel;
using Mozart.Task.Execution;
using Mozart.Extensions;
using Mozart.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class Filters
    {
        [Group("JOB_PREP")]
        public bool JOB_PREP_SAMPLE(ISimEntity wip, DateTime now, ActiveObject target, IDispatchContext ctx)
        {
            // true => filter (IS_LOADABLE.. 과는 반대: DO_FILTER_DEF에 구현된 컨벤션을 따름)

            return false;
        }
    }
}