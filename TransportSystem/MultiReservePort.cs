using FabSimulator.DataModel;
using Mozart.SeePlan;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using Mozart.Task.Execution;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FabSimulator
{
    [FeatureBind()]
    public class MultiReservePort : Port
    {
        private List<FabSemiconLot> lots;

        public List<FabSemiconLot> Lots { get { return this.lots; } }

        public MultiReservePort(string id, double x, double y, Bay bay, Cell cell, string eqpID)
            : base(id, x, y, bay, cell, eqpID)
        {
            this.lots = new List<FabSemiconLot>();
        }

        public bool readyToLoad
        {
            get
            {
                return this.Lots.All(x => x.Location == this);
            }
        }

        public override void Attach(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;

            var oldMovingState = lot.MovingState;

            if (this.Lots.Contains(lot) == false)    
                this.Lots.Add(lot);

            if (lot.Location != null)
                throw new InvalidOperationException($"Unable To Attach Lot: Lot({lot.LotID}) is attached in different Location({lot.Location.ID})");

            if (this.State == LocationState.RESERVED && !this.Lots.Contains(lot))
                throw new InvalidOperationException($"Unable To Attach Lot: Lot({lot.LotID}) is not reserved in {this.GetType().Name}({this.ID})");

            lot.ReservedLocation = null;
            lot.LastLocation = lot.Location;
            lot.Location = this;
            this.State = LocationState.OCCUPIED;
            this.StateChangeTime = AoFactory.Current.NowDT;

            lot.MovingState = LocationType.PORT;
            lot.MovingStateChangeTime = AoFactory.Current.NowDT;

            if (oldMovingState == LocationType.MOVING)
                this.Eqp?.WakeUp();
        }

        public override void Detach(IHandlingBatch hb)
        {
            var lot = hb as FabSemiconLot;

            if (this.State != LocationState.OCCUPIED)
                throw new InvalidOperationException($"Unable To Detach Lot: {this.GetType().Name}({this.ID}) is not occupied");

            if (lot.MovingState != LocationType.PORT)
                throw new InvalidOperationException($"Unable To Detach Lot: {this.GetType().Name}({this.ID}) is not occupied by Lot({lot.LotID})");

            if (lot.Location != this)
                throw new InvalidOperationException($"Unable To Detach Lot: Lot({this.Lot.LotID}) is in different {this.GetType().Name}({this.Lot.Location.ID})");

            var location = lot.Location;

            lot.Location = null;
            lot.LastLocation = location;
            lot.MovingState = LocationType.MOVING;
            lot.MovingStateChangeTime = AoFactory.Current.NowDT;

            this.Lots.Remove(lot);
            this.State = this.Lots.Count == 0 ? LocationState.VACANT : this.Lots.Any(x => x.MovingState == LocationType.PORT) ? LocationState.OCCUPIED : LocationState.RESERVED;
            this.StateChangeTime = AoFactory.Current.NowDT;

            this.Eqp?.WakeUp();
        }

        public override void Reserve(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;
            lot.ReservedLocation = this;

            this.Lots.Add(lot);

            if (LocationState.RESERVED > this.State)
            {
                this.State = LocationState.RESERVED;
                this.StateChangeTime = AoFactory.Current.NowDT;
            }
        }

        public override void SetInitialLot(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;

            var info = this.InitialInfo;
            
            this.Lots.Add(lot);

            if (info.State > this.State)
            {
                this.State = info.State;
                this.StateChangeTime = info.StateChangeTime;
            }

            this.Attach(hb);
        }
    }
}
