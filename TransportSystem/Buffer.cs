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
using Mozart.SeePlan.Simulation;

namespace FabSimulator
{
    [FeatureBind()]
    public class Buffer : Location
    {
        public override LocationType LocationType { get { return LocationType.BUFFER; } }

        public BufferType BufferType { get; private set; }

        public Buffer(string id, double x, double y, Bay bay, Cell cell, BufferType type)
            : base(id, x, y, bay, cell)
        {
            this.BufferType = type;
        }

        public override void Attach(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;
            if (lot.Location != this)
            {
                base.Attach(hb);

                lot.MovingState = LocationType.BUFFER;
                lot.MovingStateChangeTime = AoFactory.Current.NowDT;
            }

            if (lot.IsWipHandle && lot.FabWipInfo.IsJobPrep)
                return;

            if (lot.CurrentFabStep.IsSimulationStep) // Bucket Time 끝난 후에 Attach 및 PortDispatching 참여
            {
                ResourceHelper.GetLoadableEqpList(hb, false);
                var arrangedEqps = lot.CurrentArranges.Values.Select(x => x.Eqp.SimObject).ToList();

                TransportSystem.AddJobPrepCandidates(lot, arrangedEqps);
            }
            else
            {
                // Buffer에 도착은 했지만, 아직 Bucketing Step인 경우, SimulationStep에서 Attach가 재호출 되기를 기다림.
            }
            

            // LocateForDispatch에서도 따로 da에 담고 있음..
#if false// 결과에 영향없음: OnTransfered에서 불렸을 때 Add해도 즉시 반영이 안되고, DispatchIn 상태까지 가서 디스패칭 참여됨..
            var control = EntityControl.Instance;
            var key = control.GetLotDispatchingKey(lot);
            var da = AoFactory.Current.GetDispatchingAgent(key);
            da.Add(lot); 
#endif
        }

        public override void SetInitialLot(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;
            if (lot.CurrentState == EntityState.WAIT && lot.FabWipInfo.InitialEqp != null)
            {
                // Initial JobPrep State
                lot.FabWipInfo.IsJobPrep = true;
                var feqp = (lot.FabWipInfo.InitialEqp as FabSemiconEqp).SimObject;
                feqp.JobPrepLotList.Add(lot);
            }

            base.SetInitialLot(hb);
        }
    }
}