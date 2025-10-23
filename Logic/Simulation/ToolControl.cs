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

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class ToolControl
    {
        public bool IS_NEED_TOOL_SETTINGS0(AoEquipment eqp, ILot lot, ref bool handled, bool prevReturnValue)
        {
            // Arrange에 ToolingData가 있으면 SecondResource가 요구되는 것으로 판단.

            lot.ToolSettings = null; // 지우지 않으면, 다른 디스패칭에서 세팅된 정보를 계속 담고 있음.

            var fLot = lot as FabSemiconLot;
            var arr = fLot.CurrentArranges.SafeGet(eqp.EqpID);

            if (arr == null || arr.ToolingData == null)
                return false;

            return true;
        }

        public IToolData GET_TOOL_DATA0(AoEquipment eqp, ILot lot, ref bool handled, IToolData prevReturnValue)
        {
            // ToolingData는 어떤 SecondResource가 필요한지에 대한 정보를 담고 있음.

            var fLot = lot as FabSemiconLot;
            var arr = fLot.CurrentArranges.SafeGet(eqp.EqpID);

            return arr.ToolingData;
        }

        public IEnumerable<ToolItem> BUILD_TOOL_ITEMS0(ToolSettings tool, ref bool handled, IEnumerable<ToolItem> prevReturnValue)
        {
            // 요구되는 Tooling의 종류당 ToolItem을 하나씩 생성
            // 최종 선택될 ToolingID를 ResourceKey에 담는 구조라 객체를 매번 새로 생성함.

            var toolingData = tool.Data as FabToolData;
            List<ToolItem> toolItems = new List<ToolItem>();

            // 순서대로 담아야 나중에 toolingName을 찾을 수 있음.
            foreach (var tuple in toolingData.ToolingItems)
            {
                ToolItem item = new ToolItem(tuple.Item1.ToString(), 1);

                // ResourceKey는 최종 선택될 ToolingID를 담는 용도 이지만,
                // SelectTool 단계에서 구현용이성을 위해 ToolingName을 임시로 세팅함.
                item.ResourceKey = tuple.Item2;

                toolItems.Add(item);
            }

            return toolItems;
        }

        public object SELECT_TOOL0(ToolSettings tool, ToolItem item, ILot lot, AoEquipment aeqp, ToolItem last, bool canAlt, ref bool handled, object prevReturnValue)
        {
            // 등록한 ToolItem 마다 본 함수가 호출됨.
            // TODO: Reticle 이외의 ToolingType은 추가 구현이 필요.

            if (item.ResourceType != ToolingType.Reticle.ToString())
                return null;

            var toolingData = tool.Data as FabToolData;
            var toolingArranges = toolingData.ToolingArranges.SafeGet(item.ResourceKey.ToString());
            item.ResourceKey = null; // workaround로 사용한 것이므로 초기화

            var pool = aeqp.Factory.GetResourcePool(item.ResourceType);
            if (pool == null)
                return null;

            List<FabTooling> availableToolings = new List<FabTooling>();
            foreach (var tooling in toolingArranges)
            {
                var sres = pool.GetResource(tooling.ToolingID, null); // owner는 의미 없음

                if (sres != null && sres.IsAvailable(aeqp))
                    availableToolings.Add(tooling);
            }

            // 리턴값이 item.ResourceKey에 세팅됨.
            return SelectResourceKey(aeqp, availableToolings);

            static string SelectResourceKey(AoEquipment aeqp, List<FabTooling> availableToolings)
            {
                // 1. ToolingLocation이 현재 디스패칭 설비인 경우 우선 선택.
                var locatedTooling = availableToolings.Where(x => x.ToolingLocation == aeqp.EqpID).FirstOrDefault();
                if (locatedTooling != null)
                    return locatedTooling.ToolingID;

                // 2. 다른 곳에서 가져와야 하면 Workload가 많은 것을 선택
                var feqp = aeqp as FabAoEquipment;
                foreach (var reticle in feqp.Eqp.ToolingInfo.SelectableReticleList)
                {
                    var toolingArr = availableToolings.Where(x => x.ToolingID == reticle.ToolingID).FirstOrDefault();
                    if (toolingArr != null)
                        return reticle.ToolingID;
                }

                return null;
            }
        }

        public object UPDATE_FILTER_REASON(ToolSettings tool, ToolItem item, ILot lot, AoEquipment aeqp, ToolItem last, bool canAlt, ref bool handled, object prevReturnValue)
        {
            if (prevReturnValue == null)
            {
                var reason = string.Empty;

                if (item.ResourceType == ToolingType.Reticle.ToString())
                    reason = "Need Reticle";
                else if (item.ResourceType == ToolingType.ProbeCard.ToString())
                    reason = "Need ProbeCard";

                // 여기서 null로 세팅하면 filterControl.CheckSecondResouce 내부에서 오류남.
                //lot.ToolSettings = null; 

                // INVALID_TOOL_ITEM 자동으로 세팅됨.
                //aeqp.EqpDispatchInfo.AddFilteredWipInfo(lot, "Need Reticle");
                (lot as FabSemiconLot).LastFilterReason = reason;
            }

            return prevReturnValue;
        }
    }
}