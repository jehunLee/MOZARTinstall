using FabSimulator.DataModel;
using Microsoft.VisualBasic;
using Mozart.Extensions;
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FabSimulator
{
    internal partial class FabForwardPegInfo : ForwardPegInfo
    {
        public FabSemiconMoPlan Demand;

        public double PegQty;

        public FabForwardPegInfo(FabSemiconMoPlan demand, double qty, StepTarget stepTarget = null) : base(stepTarget, qty)
        {
            PegQty = qty;
            Demand = demand;
        }
    }
}
