using FabSimulator.Persists;
using FabSimulator.Outputs;
using FabSimulator.Inputs;
using Mozart.SeePlan.Simulation;
using FabSimulator.DataModel;
using Mozart.Task.Execution;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class ToolEvents
    {
        public void ON_SEIZED0(ToolSettings tool, AoEquipment eqp, ToolSettings last, ref bool handled)
        {
            var feqp = eqp as FabAoEquipment;
            feqp.Eqp.ToolingInfo.SelectableReticleList.Clear();

            var toolingData = tool.Data as FabToolData;

            for (int i = 0; i < tool.ItemCount; i++)
            {
                var item = tool.Items[i];
                if (item.ResourceType == ToolingType.Reticle.ToString())
                {
                    var reticle = item.SeizedResource.Data as FabReticle;
                    if (reticle == null)
                        continue;

                    if (reticle.ToolingLocation != eqp.EqpID)
                    {
                        var toolingName = toolingData.ToolingItems[i].Item2;

                        ResourceHelper.KeepReticleOnEqp(feqp.Eqp, reticle, toolingName);
                    }

                    reticle.OnSeized = true;
                }
            }
        }

        public void ON_RELEASED0(ToolSettings tool, AoEquipment eqp, ToolSettings chg, ref bool handled)
        {
            foreach (var item in tool.Items)
            {
                if (item.ResourceType == ToolingType.Reticle.ToString())
                {
                    var reticle = item.SeizedResource.Data as FabReticle;
                    if (reticle == null)
                        continue;

                    reticle.OnSeized = false;

                    reticle.SelectableTime = Helper.Max(reticle.SelectableTime, eqp.NowDT.AddHours(Helper.GetConfig(ArgsGroup.Resource_Tooling).reticleMoveHrs));
                }
            }
        }
    }
}