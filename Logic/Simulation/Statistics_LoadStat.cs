using Mozart.Simulation.Engine;
using Mozart.SeePlan.StatModel;
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
    public partial class Statistics_LoadStat
    {
        public LOAD_STAT GET_ROW(Mozart.SeePlan.StatModel.StatSheet<LOAD_STAT> sheet, Mozart.Simulation.Engine.ActiveObject aeqp, int index, DateTime now)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("LOAD_STAT"))
                return null;

            var eqp = aeqp as FabAoEquipment;
            var eqpModel = eqp.Target as FabSemiconEqp;

            string equipId = index == 0 ? eqp.EqpID : eqpModel.SubEqps[index - 1].SubEqpID;
            DateTime targetDate = now;

            var row = sheet.GetRow(InputMart.Instance.ScenarioID, ModelContext.Current.VersionNo, equipId, targetDate);

            return row;
        }
    }
}