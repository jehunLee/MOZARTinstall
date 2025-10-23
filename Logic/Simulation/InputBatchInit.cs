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
    public partial class InputBatchInit
    {
        public IEnumerable<Mozart.SeePlan.Simulation.ILot> INSTANCING0(ref bool handled, IEnumerable<Mozart.SeePlan.Simulation.ILot> prevReturnValue)
        {
            List<FabSemiconLot> instancingLots = new List<FabSemiconLot>();

            if (InputMart.Instance.InPlanRule == FabInPlanRule.Demand)
            {
                instancingLots = EntityHelper.CreateWaferStartWithDemand();
            }
            else if (InputMart.Instance.InPlanRule == FabInPlanRule.FabInPlan)
            {
                // Use recursively UI_FAB_IN_PLAN 부분은 구현되지 않음
                // 현재 UI_FAB_IN_PLAN의 데이터는, Conifg UI 상에서 FAB_IN_PLAN으로 Convert할 수 있고, 입력된 그대로만 사용함.
                instancingLots = EntityHelper.CreateWaferStartWithFabInPlan();
            }
            else
            {
                // ConstantWip -> OnDayChanged에서 처리됨
                return prevReturnValue;
            }

            return instancingLots;
        }
    }
}