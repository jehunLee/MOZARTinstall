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
using Mozart.SeePlan;

namespace FabSimulator.Logic
{
    [FeatureBind()]
    public partial class Main
    {
        public void ON_INITIALIZE0(ModelContext context, ref bool handled)
        {
            InputMart.Instance.PlanStartDay = ShopCalendar.StartTimeOfDayT(InputMart.Instance.GlobalParameters.start_time);
            InputMart.Instance.ManualEventCtx = new ManualEventContext();

            //wook
            //Mozart.SeePlan.Simulation.DispatchingAgent.EnableSkippable = true;
            Mozart.SeePlan.Simulation.EqpDispatchInfo.DispatchInfoParallelEnabled = true;
            
            System.Reflection.Assembly fabsim = System.Reflection.Assembly.GetExecutingAssembly();
            //Logger.MonitorInfo("FabSimulator dll : {0} / {1} / {2}", fabsim.GetName().Version.ToString(), fabsim.Location, fabsim.GetLinkerTime());
            Logger.MonitorInfo("***** FabSimulator dll : {0} / {1:yyyy-MM-dd HH:mm:ss} *****", fabsim.GetName().Version.ToString(), fabsim.GetLinkerTime().ToLocalTime());

        }

        public bool CAN_EXECUTE_MODULE0(ExecutionModule module, ModelContext context, ref bool handled, bool prevReturnValue)
        {
            var runModule = Helper.GetConfig(ArgsGroup.Simulation_Run).modules;

            if (runModule == 1) // Forward only
            {
                if (module.Name == "Pegging")
                    return false;
            }
            else if (runModule == 2) // Backward + Forward
            {
                return true;
            }
            else if (runModule == 3) // Backward only
            {
                if (module.Name == "Simulation")
                    return false;
            }
            else if (runModule == 4) // Persist only
            {
                return false;
            }

            return true;
        }

        public void SETUP_QUERY_ARGS1(ModelTask task, ModelContext context, ref bool handled)
        {
            InputMart.Instance.ScenarioID = task.TaskContext.Arguments["scenarioID"].ToString();

            if (context.QueryArgs.Contains("SCENARIO_ID")) // 서버에서 돌리면 Key는 자동으로 등록하는 듯.
                context.QueryArgs["SCENARIO_ID"] = InputMart.Instance.ScenarioID;
            else
                context.QueryArgs.Add("SCENARIO_ID", InputMart.Instance.ScenarioID);

            string runnerStr = string.Empty;
            foreach (var kvp in task.TaskContext.Arguments)
            {
                if (kvp.Key.StartsWith("#"))
                    break; // Input Argument에 해당하는 item만 출력하기 위함.

                runnerStr += string.Format("{0}={1};\n", kvp.Key, kvp.Value.ToString());
            }

            OutputHelper.WritePersistLog(LogType.INFO, "RUNNER", runnerStr, "-");
        }

        public void ON_DONE0(ModelContext context, ref bool handled)
        {
            if (Helper.GetConfig(ArgsGroup.Simulation_Run).modules == 4)
            {
                foreach (var eqp in InputMart.Instance.FabSemiconEqp.Rows)
                {
                    if (eqp.PMList.IsNullOrEmpty())
                        continue;

                    foreach (var pm in eqp.PMList)
                    {
                        OutputHelper.WriteEqpDownLog(eqp, pm);
                    }
                }
            } 
        }
    }
}