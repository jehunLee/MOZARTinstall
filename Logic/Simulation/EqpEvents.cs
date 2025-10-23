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
using Mozart.SeePlan.DataModel;
using Mozart.Simulation.Engine;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class EqpEvents
    {
        public void WRITE_CHAMBER_HISTORY(Mozart.SeePlan.Simulation.AoEquipment aeqp, IHandlingBatch hb, LoadingStates state, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;
            var eqpModel = feqp.Target as FabSemiconEqp;

            if (feqp.IsParallelChamber == false)
                return;

            if (state != LoadingStates.BUSY && state != LoadingStates.SETUP)
                return;

            var lotSample = hb.Sample as FabSemiconLot;
            var planSample = lotSample.CurrentFabPlan;
            var arrange = planSample.Arrange;

            //var status = lotSample != null ? "BUSY" : "PM";
            //var lotId = lotSample != null ? lotSample.LotID : string.Empty;
            //var stepName = lotSample != null ? lotSample.CurrentStepID : string.Empty;

            var cproc = aeqp.FirstProcess<AoChamberProc2>();

            if (cproc.EntityState == ProcessStates.EndSetup || (lotSample.IsRunWipHandle == false && cproc.EntityState == ProcessStates.FirstUnloading))
                return;

            bool updated = false;
            foreach (var chamber in cproc.Chambers)
            {
                var list = chamber.List;
                var workInfo = list.FirstOrDefault(x => (x.Entity as FabSemiconLot).LotID == lotSample.LotID);

                if (workInfo != null)
                {
                    int prevIndex = list.IndexOf(workInfo) - 1;

                    var inTime = prevIndex < 0 ? planSample.StartTime : (DateTime)list[prevIndex].Time;
                    var outTime = (DateTime)workInfo.Time;

                    FabSemiconSubEqp subEqp = arrange.SubEqps.FirstOrDefault(x => x.SubEqpID == chamber.Label);
                    subEqp.LastPlan = (workInfo.Entity as FabSemiconLot).CurrentFabPlan;

                    OutputHelper.WriteChamberHistory(aeqp, chamber.Label, lotSample, inTime, outTime, workInfo.Units);

                    if (lotSample.IsRunWipHandle)
                        OutputHelper.UpdateEqpPlanStartTime(aeqp, lotSample, inTime, ref updated);
                }

            }
        }

        public void INIT_CURRENT_STATE(AoEquipment aeqp, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;
            var eqpModel = aeqp.Target as FabSemiconEqp;

            if (eqpModel.State == ResourceState.Down)
            {
                feqp.CurrentState = LoadingStates.DOWN;

                if (eqpModel.EqpUpTime != DateTime.MinValue)
                {
                    Time delayTime = eqpModel.EqpUpTime - feqp.NowDT;
                    EventHelper.AddManualEvent(delayTime, ManualEventTaskType.OnEqpUpStartTime, feqp, "INIT_CURRENT_STATE");
                }

                return;
            }

            if (feqp.IsProcessing)
                return;

            EventHelper.AddManualEvent(Time.Zero, ManualEventTaskType.OnEqpUpStartTime, feqp, "INIT_CURRENT_STATE");
        }

        public void DETECT_STANDBY(AoEquipment aeqp, IHandlingBatch hb, LoadingStates state, ref bool handled)
        {
            if (InputMart.Instance.ExcludeOutputTables.Contains("STANDBY_TIME"))
                return;

            var feqp = aeqp as FabAoEquipment;
            var eqpModel = feqp.Target as FabSemiconEqp;
            var prevState = feqp.CurrentState;
            var now = feqp.NowDT;

            if (eqpModel.WriteStandbyTime == false)
                return;

            if (prevState == LoadingStates.IDLE || prevState == LoadingStates.IDLERUN)
                StandbyHelper.UpdateEndTimeofPrevRow(feqp, now);

            if (state == LoadingStates.IDLE || state == LoadingStates.IDLERUN)
                StandbyHelper.InsertStandbyRow(feqp, state);
        }

        public void SET_CURRENT_STATE(AoEquipment aeqp, IHandlingBatch hb, LoadingStates state, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;
            var now = feqp.NowDT;

            if (feqp.CurrentState == LoadingStates.DOWN
                || feqp.CurrentState == LoadingStates.PM)
            {
                bool isBlock = feqp.Loader.IsBlocked();

                if (isBlock)
                    return;
            }

            //if (state == LoadingStates.BUSY || state == LoadingStates.IDLE || state == LoadingStates.IDLERUN)
            //    SetIdleStartTime(aeqp, hb, state);

            feqp.CurrentState = state;
        }
    }
}