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
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class WipInit
    {

        public int COMPARE_WIP1(IHandlingBatch x, IHandlingBatch y, ref bool handled, int prevReturnValue)
        {
            var xWip = (x.Sample as FabSemiconLot).FabWipInfo;
            var yWip = (y.Sample as FabSemiconLot).FabWipInfo;

            var xStatePriority = EntityHelper.GeteWipStatePriority(xWip.WipState);
            var yStatePriority = EntityHelper.GeteWipStatePriority(yWip.WipState);

            var cmp = xStatePriority.CompareTo(yStatePriority);

            if (cmp == 0)
                cmp = xWip.WipStateTime.CompareTo(yWip.WipStateTime);

            return cmp;
        }

        public DateTime FIX_START_TIME0(AoEquipment aeqp, IHandlingBatch hb, ref bool handled, DateTime prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;

            if (lot.FabWipInfo.WipState == "RUN") // SimulationStep에서 RUN중일때만 의미 있음. ex)BOH PHOTO
                return lot.FabWipInfo.WipStateTime;

            // PST보다 미래의 값으로 할당할 수는 없음.
            return aeqp.NowDT;
        }

        public void ON_BEGIN_INIT0(AoFactory factory, IList<IHandlingBatch> wips, ref bool handled)
        {
            InputMart.Instance.FabInLimit = Math.Round((InputMart.Instance.FabWipInfo.Values.Sum(x => x.UnitQty) / Helper.GetConfig(ArgsGroup.Lot_InPlan).targetCT));

            //if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 0)
            //    handled = true;
        }

        public bool CHECK_TRACK_OUT0(AoFactory factory, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            // true : eqp.AddOutBuffer(hb);
            // false : eqp.AddRun(hb);
            // 일괄 false로 줘도 TrackOut인지 판단해서 결과적으로 동일하게 동작함

            return false;
        }

        public bool IS_SPLIT_BATCH_FOR_NOT_RUN0(IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            // 디스패칭 참여시 hb를 항상 FabSemcionLot으로 캐스팅하는 편의성을 위해
            // Staging상태의 LotBatch도 쪼개서 등록

            return true;
        }

        public void SET_LOT_INDEX(AoFactory factory, IList<IHandlingBatch> wips, ref bool handled)
        {
            bool useWip = Helper.GetConfig(ArgsGroup.Lot_Wip).useWIP == "Y";
            if (useWip)
            {
                // LotID 중복 방지
                var maxIndex = 0;

                foreach (var lotID in InputMart.Instance.FabWipInfo.Keys)
                {
                    if (int.TryParse(lotID.Split('_').LastOrDefault(), out int index))
                        maxIndex = Math.Max(index, maxIndex);
                }

                InputMart.Instance.WaferStartLotIndex = maxIndex + 1;
            }
        }

        public void LOCATE_FOR_DISPATCH_TRANSPORT(AoFactory factory, IHandlingBatch hb, ref bool handled)
        {
            if (TransportSystem.Apply == false)
                return;

            // Batch 상태의 WAIT/MOVE 미지원
            // Location 정보만 세팅한 뒤 Default 함수 태움
            hb.Apply((x, _) => TransportSystem.SetInitialLocation(x as FabSemiconLot, true));

            var lot = hb.Sample as FabSemiconLot;
            if (lot.ReservedLocation != null) // MOVE 상태
                handled = true;
        }

        public void LOCATE_FOR_RUN_TRANSPORT(AoFactory factory, IHandlingBatch hb, ref bool handled)
        {
            if (TransportSystem.Apply == false)
                return;

            // Location 정보만 세팅한 뒤 Default 함수 태움
            hb.Apply((x, _) => TransportSystem.SetInitialLocation(x as FabSemiconLot, true));
        }
    }
}