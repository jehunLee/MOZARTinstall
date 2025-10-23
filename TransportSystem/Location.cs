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
using Mozart.Simulation.Engine;
using Mozart.SeePlan;

namespace FabSimulator
{
    [FeatureBind()]
    public abstract class Location : ILocation
    {
        public string ID { get; private set; }

        public abstract LocationType LocationType { get; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public Bay Bay { get; private set; }

        public Cell Cell { get; private set; }

        public FabSemiconLot Lot { get; protected set; }

        public LocationState State { get; protected set; }

        public DateTime StateChangeTime { get; protected set; }

        public InitialLocationInfo InitialInfo { get; private set; }

        public Location(string id, double x, double y, Bay bay, Cell cell)
        {
            this.ID = id;
            this.X = x;
            this.Y = y;
            this.Bay = bay;
            this.Cell = cell;
        }

        public virtual void Attach(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.Location != null)
                throw new InvalidOperationException($"Unable To Attach Lot: Lot({lot.LotID}) is attached in different Location({lot.Location.ID})");

            if (this.State == LocationState.RESERVED && this.Lot != lot)
                throw new InvalidOperationException($"Unable To Attach Lot: Lot({this.Lot.LotID}) is reserved in {this.GetType().Name}({this.ID}), but Different Lot({lot.LotID}) is attached");

            lot.ReservedLocation = null;
            lot.LastLocation = lot.Location;
            lot.Location = this;
            this.Lot = lot;
            this.State = LocationState.OCCUPIED;
            this.StateChangeTime = AoFactory.Current.NowDT;
        }

        public virtual void Detach(IHandlingBatch hb)
        {
            var lot = this.Lot;
            if (lot == null)
                throw new InvalidOperationException($"Unable To Detach Lot: {this.GetType().Name}({this.ID}) is not occupied");

            if (hb != this.Lot)
                throw new InvalidOperationException($"Unable To Detach Lot: {this.GetType().Name}({this.ID}) is not occupied by Lot({hb.Sample.LotID})");

            if (lot.Location != this)
                throw new InvalidOperationException($"Unable To Detach Lot: Lot({this.Lot.LotID}) is in different {this.GetType().Name}({this.Lot.Location.ID})");

            var location = lot.Location;

            lot.Location = null;
            lot.LastLocation = location;
            lot.MovingState = LocationType.MOVING;
            lot.MovingStateChangeTime = AoFactory.Current.NowDT;
            this.Lot = null;
            this.State = LocationState.VACANT;
            this.StateChangeTime = AoFactory.Current.NowDT;
        }

        public virtual void Reserve(IHandlingBatch hb)
        {
            if (this.Lot != null)
                throw new InvalidOperationException($"Unable To Reserve Lot: {this.GetType().Name}({this.ID}) is already occupied by Lot({this.Lot.LotID})");

            var lot = hb.Sample as FabSemiconLot;
            lot.ReservedLocation = this;
            this.Lot = lot;
            this.State = LocationState.RESERVED;
            this.StateChangeTime = AoFactory.Current.NowDT;
        }

        public void SetInitialInfo(InitialLocationInfo info)
        {
            this.InitialInfo = info;
            TransportSystem.AddInitialLocation(info.LotID, this);
        }

        public virtual void SetInitialLot(IHandlingBatch hb)
        {
            var lot = hb.Sample as FabSemiconLot;
            var info = this.InitialInfo;
            if (info != null && info.LotID == lot.LotID)
            {
                this.Lot = lot;
                this.State = info.State;
                this.StateChangeTime = info.StateChangeTime;

                if (this.State == LocationState.RESERVED)
                {
                    //ETA
                    var remainTime = info.StateChangeTime - AoFactory.Current.NowDT;
                    if (remainTime > TimeSpan.Zero)
                    {
                        lot.MovingState = LocationType.MOVING;
                        lot.ReservedLocation = this;
                        lot.RemainTransferTime = remainTime;
                        AoFactory.Current.Transfer.Take(hb);

                        return;
                    }
                }

                this.Attach(hb);
            }
        }
    }
}
