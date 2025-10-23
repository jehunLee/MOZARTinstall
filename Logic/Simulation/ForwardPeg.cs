using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
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
    public partial class ForwardPeg
    {
        public void INIT_STEP_PLAN_MANAGER0(ref bool handled)
        {

        }

        public IEnumerable<Tuple<Step, object>> GET_STEP_PLAN_KEYS0(ILot lot, ref bool handled, IEnumerable<Tuple<Step, object>> prevReturnValue)
        {
            // B/W 돌리지 않아서 StepTarget이 없는 경우에 DEMAND 데이터로만 ForwardPeg 하는 기능이 별도로 구현되어 있음.
            // Demand Fullfilment 계산을 위함.

            if (lot.CurrentStep.StepID == Helper.GetConfig(ArgsGroup.Bop_Step).fabOutStepID)
            {
                var fLot = lot as FabSemiconLot;

                List<Tuple<Step, object>> keys = new List<Tuple<Step, object>>();
                var key = new Tuple<Step, object>(lot.CurrentStep, fLot.Product.StdProductID);

                keys.Add(key);
                return keys;
            }

            return null;
        }

        public double GET_FORWARD_PEGGING_QTY0(ILot lot, ref bool handled, double prevReturnValue)
        {
            return lot.UnitQtyDouble;
        }

        public double GET_FORWARD_PEGGING_QTY_OF_KEY0(ILot lot, object key, ref bool handled, double prevReturnValue)
        {
            return lot.UnitQtyDouble;
        }

        public int COMPARE_STEP_TARGET0(StepTarget x, StepTarget y, ref bool handled, int prevReturnValue)
        {
            return x.DueDate.CompareTo(y.DueDate);
        }

        public bool FILTER_STEP_TARGET0(ILot lot, StepTarget st, ref bool handled, bool prevReturnValue)
        {
            // Pegging 다 되었으면 잔여 StepTarget은 호출되지 않음.

            return false;
        }
    }
}