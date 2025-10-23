using Mozart.Simulation.Engine;
using Mozart.SeePlan.StatModel;
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
using Mozart.SeePlan;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class Statistics_LoadingHistory
    {
        public LOAD_HISTORY GET_ROW(Mozart.SeePlan.StatModel.StatSheet<LOAD_HISTORY> sheet, Mozart.Simulation.Engine.ActiveObject aeqp, int index, DateTime now)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("LOAD_HISTORY"))
                return null;

            var eqp = aeqp as FabAoEquipment;
            var eqpModel = eqp.Target as FabSemiconEqp;

            string subID = index == 0 ? "-" : eqpModel.SubEqps[index - 1].SubEqpID;
            DateTime targetDate = ShopCalendar.SplitDate(now);
            
            var row = sheet.GetRow(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, eqp.EqpID, subID, targetDate);

            return row;
        }
    }
}