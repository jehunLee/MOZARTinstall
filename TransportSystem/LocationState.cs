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
    public enum LocationState
    {
        VACANT = 0,
        RESERVED = 1,
        OCCUPIED = 2,
    }
}