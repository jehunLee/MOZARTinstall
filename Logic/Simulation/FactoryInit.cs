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
    public partial class FactoryInit
    {
        public IEnumerable<Mozart.SeePlan.DataModel.WeightPreset> GET_WEIGHT_PRESETS0(Mozart.SeePlan.Simulation.AoFactory factory, ref bool handled, IEnumerable<Mozart.SeePlan.DataModel.WeightPreset> prevReturnValue)
        {
            return InputMart.Instance.FabWeightPreset.Rows.ToList();
        }

        public IList<SecondResourcePool> GET_SECOND_RESOURCE_POOLS0(AoFactory factory, ref bool handled, IList<SecondResourcePool> prevReturnValue)
        {
            List<SecondResourcePool> pools = new List<SecondResourcePool>();

            // ToolingName 별로 ResourcePool을 따로 만드는 방법도 있겠으나,
            // 하나의 Tooling을 다수의 Pool에 포함시켰을 때, 의도대로 동작할지는 미지수
            // 그래서 기존처럼 ResourceType 별로 Pool을 생성함.

            SecondResourcePool pool = new SecondResourcePool(factory, ToolingType.Reticle.ToString());
            foreach (var reticle in InputMart.Instance.FabReticle.Rows)
            {
                SecondResource res = new SecondResource(reticle.ToolingID, reticle);
                res.Capacity = 1;
                res.Uses = 0;
                res.Pool = pool;
                pool.Add(res);
            }
            pools.Add(pool);

            return pools;
        }

        public void INITIALIZE_WIP_GROUP0(AoFactory factory, IWipManager wipManager, ref bool handled)
        {
            factory.WipManager.AddGroup(Constants.WIP_GROUP_STEP_WIP, "CurrentPartID", "CurrentStepID");
        }
    }
}