using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
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

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class GET_WIPS_BATCH
    {
        public IEnumerable<Mozart.SeePlan.Semicon.DataModel.IWipInfo> GET_WIP_INFOS0(ref bool handled, IEnumerable<Mozart.SeePlan.Semicon.DataModel.IWipInfo> prevReturnValue)
        {
            // product가 세팅되지 않은 wip은 lib에서 걸러지는듯

            return InputMart.Instance.FabWipInfo.Values;
        }

        public SemiconLot CREATE_LOT0(IWipInfo wip, ref bool handled, SemiconLot prevReturnValue)
        {
            FabSemiconLot lot = CreateHelper.CreateLot(wip);

            EntityHelper.SetCurrentBOMInfo(lot);

            TransportSystem.SetInitialTransferInfo(lot);

            return lot;
        }

        public bool IS_NEED_CREATE_BATCH0(IWipInfo wip, SemiconEqp initEqp, ref bool handled, bool prevReturnValue)
        {
            if (Helper.GetConfig(ArgsGroup.Resource_SimType).batchToInline == "Y")
                return false;

            var eqp = initEqp as FabSemiconEqp;
            if (eqp == null)
                return false;

            return eqp.SimObject.IsBatchType();
        }

        public string GET_BATCH_ID0(IWipInfo wip, int index, ref bool handled, string prevReturnValue)
        {
            return Helper.CreateKey(wip.InitialEqp.ResID.ToString(), wip.WipStateTime.ToString());
        }

        public LotBatch CREATE_LOT_BATCH0(string batchID, IWipInfo sample, ref bool handled, LotBatch prevReturnValue)
        {
            var lotBatch = new LotBatch();
            lotBatch.BatchID = batchID;

            return lotBatch;
        }
    }
}