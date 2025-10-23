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
    public class Port : Location
    {
        FabAoEquipment eqp;

        public override LocationType LocationType { get { return LocationType.PORT; } }

        public string EqpID { get; private set; }

        public FabAoEquipment Eqp
        {
            get
            {
                if (this.eqp == null)
                    this.eqp = AoFactory.Current.GetEquipment(this.EqpID) as FabAoEquipment;

                return this.eqp;
            }
        }

        public Port(string id, double x, double y, Bay bay, Cell cell, string eqpID)
            : base(id, x, y, bay, cell)
        {
            this.EqpID = eqpID;
        }

        public override void Attach(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;

            var oldMovingState = lot.MovingState;

            base.Attach(lot);

            lot.MovingState = LocationType.PORT;
            lot.MovingStateChangeTime = AoFactory.Current.NowDT;

            if (oldMovingState == LocationType.MOVING)
                this.Eqp?.WakeUp();
        }

        public override void Detach(IHandlingBatch hb)
        {
            base.Detach(hb);

            //MaterialControlSystem.WritePortStatus(this.Eqp);

            TransportSystem.TryFillEmptyPort(this.Eqp);

            this.Eqp?.WakeUp();
        }

        public override void Reserve(IHandlingBatch hb)
        {
            base.Reserve(hb);
        }
    }
}