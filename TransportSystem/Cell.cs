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
    public class Cell
    {
        public string ID { get; private set; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public Bay Bay { get; private set; }

        public Cell(string id, Bay bay, double x, double y)
        {
            this.ID = id;
            this.X = x;
            this.Y = y;
            this.Bay = bay;
        }
    }
}