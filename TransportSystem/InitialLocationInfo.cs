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

namespace FabSimulator
{
    [FeatureBind()]
    public class InitialLocationInfo
    {
        public string LotID { get; private set; }

        public LocationState State { get; private set; }

        public DateTime StateChangeTime { get; private set; }

        //public LocationType DepartureType { get; private set; }

        //public string DepartureBayID { get; private set; }

        //public string DepartureID { get; private set; }

        public InitialLocationInfo(string lotID, LocationState state, DateTime stateChangeTime)
        {
            this.LotID = lotID;
            this.State = state;
            this.StateChangeTime = stateChangeTime;
        }

        //public void SetDeparture(LocationType type, string bayID, string locationID)
        //{
        //    this.DepartureType = type;
        //    this.DepartureBayID = bayID;
        //    this.DepartureID = locationID;
        //}
    }
}