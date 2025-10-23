using Mozart.SeePlan.Semicon.Simulation;
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
    public partial class DO_SELECT_BATCH_DEF
    {
        public bool IS_NEED_BATCH_LOADING0(Mozart.SeePlan.Simulation.AoEquipment aeqp, IList<IHandlingBatch> wips, ref bool handled, bool prevReturnValue)
        {
            return aeqp.IsBatchType();// && MaterialControlSystem.Apply == false;
        }

        public LotBatch GET_LOADABLE_BATCH0(AoEquipment aeqp, IList<IHandlingBatch> wips, ref bool handled, LotBatch prevReturnValue)
        {
            var feqp = aeqp as FabAoEquipment;

            LotBatch selected = null;
            if (feqp.ReservedBatch == null)
            {
                BatchingContext ctx = new BatchingContext();
                if (feqp.LastPlan != null && feqp.LastPlan.EndTime == feqp.NowDT)
                    ctx.EventType = BatchingEventType.LoadingEnd.ToString();
                else
                    ctx.EventType = BatchingEventType.AtStepLotArrival.ToString();

                selected = BatchingManager.BuildAndSelect(aeqp, ctx);
            }
            else
                selected = feqp.ReservedBatch;

            if (selected.IsNullOrEmpty())
            {
                if (TransportSystem.Apply == false)
                {
                    if (feqp.Eqp.SimType == SimEqpType.BatchInline) // Port 사용시, IdleTimer로 인한 빌딩에서 예약 배치가 덮어써져서 없어질 수 있음.
                        EventHelper.AddManualEvent(Time.FromMinutes(Helper.GetConfig(ArgsGroup.Resource_Eqp).wakeUpEventTime), ManualEventTaskType.CallBatchBuild, feqp, "GET_LOADABLE_BATCH0");
                }

                return null;
            }

            foreach (var entity in selected.Contents)
            {
                var lot = EntityHelper.GetLot(entity);

                if (TransportSystem.Apply)
                {
                    bool readyToLoad = (lot.Location is MultiReservePort port) && port.readyToLoad;
                    if (readyToLoad == false)
                        return null; // not arrived yet
                }

                if (wips.Contains(lot) == false)
                {
                    if (feqp.Eqp.SimType == SimEqpType.BatchInline)
                        Logger.MonitorInfo(string.Format("BatchInline Loading Failed... {0} {1}", feqp.EqpID, feqp.NowDT.ToShortTimeString()));

                    return null; // not arrvied yet
                }
            }

            if (selected != null && feqp.NeedUpstreamBatching)
            {
                feqp.ReservedBatch = null;
                BatchingContext ctx = new BatchingContext();
                ctx.EventType = BatchingEventType.LoadingStart.ToString();
                BatchingManager.BuildAndSelect(aeqp, ctx);
            }

            return selected;
        }

        public void ON_LOADING0(AoEquipment aeqp, IList<IHandlingBatch> wips, LotBatch selectBatch, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;

            if (feqp.NeedUpstreamBatching == false)
                feqp.ReservedBatch = null;
        }
    }
}