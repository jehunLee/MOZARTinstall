using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
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

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class TransferControl
    {
        public Time GET_TRANSFER_TIME0(Mozart.SeePlan.Simulation.IHandlingBatch hb, ref bool handled, Time prevReturnValue)
        {
            var lot = hb as FabSemiconLot;
          
            var stepTransferTime = GetStepTransferTime(hb);
            var stepSkipTime = lot.CurrentFabPlan.HasStepSkipTime ? lot.CurrentFabStep.StepSkipTime : Time.Zero;

            return stepTransferTime + stepSkipTime;

            static Time GetStepTransferTime(IHandlingBatch hb)
            {
                // if null, DELIVERY_TIME table is used
                if (InputMart.Instance.TransferTimePdfConfig != null)
                {
                    var deliveryTimePdfMins = Helper.GetDistributionRandomNumber(InputMart.Instance.TransferTimePdfConfig);
                    return Time.FromMinutes(deliveryTimePdfMins).Floor();
                }
                // considerEqpLocation (≥2)
                if (InputMart.Instance.ApplyDeliveryTime)
                    return GetMinDeliveryTime(hb);

                return Time.Zero;
            }

            static Time GetMinDeliveryTime(IHandlingBatch hb)
            {
                var lot = hb.Sample as FabSemiconLot;

                // 중간에 Non-SImulationStep이 끼어있어도, 다음 번 SimulationStep으로 넘어가는 시점에 DeliveryTime을 적용하기 위해 저장
                var fromArr = (lot.PreviousPlan as FabPlanInfo).Arrange;
                if (fromArr != null)
                    lot.CurrentDeliveryInfo = fromArr.Eqp.DeliveryTimeDict;

                if (lot.CurrentFabStep.IsSimulationStep == false)
                    return Time.Zero;

                var toArrs = InputMart.Instance.EqpArrangeView.FindRows(lot.FabProduct.PartID, lot.CurrentStepID);
                if (toArrs.IsNullOrEmpty()) // FABOUT
                    return Time.Zero;

                if (lot.CurrentDeliveryInfo == null)
                    return Time.FromMinutes(Helper.GetConfig(ArgsGroup.Resource_EqpLocation).defaultDeliveryTimeMins);

                if (lot.CurrentFabPlan.ArrivalTimeDict == null)
                    lot.CurrentFabPlan.ArrivalTimeDict = new Dictionary<string, DateTime>();
                if (lot.CurrentFabPlan.DeliveryDict == null)
                    lot.CurrentFabPlan.DeliveryDict = new MultiDictionary<Time, FabAoEquipment>();

                foreach (var toArr in toArrs)
                {
                    // TODO: ActiveStack이 있는 경우, ToArrange를 여기서 미리 제한하도록 고도화 필요.

                    Time deliveryTime = GetDeliveryTime(lot, toArr);

                    lot.CurrentFabPlan.DeliveryDict.Add(deliveryTime, toArr.Eqp.SimObject);

                    if (lot.CurrentFabPlan.ArrivalTimeDict.ContainsKey(toArr.Eqp.ResID) == false)
                        lot.CurrentFabPlan.ArrivalTimeDict.Add(toArr.Eqp.ResID, (DateTime)(AoFactory.Current.NowDT + deliveryTime));
                }

                var minDeliveryTime = lot.CurrentFabPlan.DeliveryDict.Keys.Min();

                foreach (var item in lot.CurrentFabPlan.DeliveryDict)
                {
                    if (item.Key == minDeliveryTime)
                        continue;

                    foreach (var toEqp in item.Value)
                    {
                        var args = new Tuple<IHandlingBatch, FabSemiconStep, FabAoEquipment, string>(lot, lot.CurrentFabStep, toEqp, null);
                        EventHelper.AddManualEvent(item.Key, ManualEventTaskType.CallEnqueue, toEqp, "GET_TRANSFER_TIME0", args);
                    }
                }

                return minDeliveryTime;

                static Time GetDeliveryTime(FabSemiconLot lot, EqpArrange toArr)
                {
                    var deliveryInfo = lot.CurrentDeliveryInfo.SafeGet(toArr.Eqp.LocationInfo.Bay);
                    if (deliveryInfo == null)
                        return Time.FromMinutes(Helper.GetConfig(ArgsGroup.Resource_EqpLocation).defaultDeliveryTimeMins);

                    var deliveryTime = Time.FromMinutes(deliveryInfo.DeliveryMins + deliveryInfo.PenaltyMins).Floor(); // Key로 쓰기위해 밀리초 제거
                    if (deliveryInfo.DeliveryMinsPdfConfig != null)
                    {
                        var deliveryTimePdfMins = Helper.GetDistributionRandomNumber(deliveryInfo.DeliveryMinsPdfConfig);
                        deliveryTime = Time.FromMinutes(deliveryTimePdfMins + deliveryInfo.PenaltyMins).Floor();
                    }

                    return deliveryTime;
                }
            }

#if false
            var lot = hb.Sample as FabSemiconLot;

            if ((lot.CurrentFabStep.PrevStep as FabSemiconStep).IsEqpLoadingStep == false)
                return Time.Zero;

            lot.CurrentFabPlan.TransferStartTime = AoFactory.Current.NowDT;
            var defaultTransferTime = Time.FromMinutes(SeeplanConfiguration.Instance.TransferTimeMinutes);

            if (InputMart.Instance.IsSimulatorMode)
                return defaultTransferTime;

            var fromEqpId = lot.PreviousPlan.ResID;

            if (fromEqpId.IsNullOrEmpty())
                return defaultTransferTime;

            if (lot.CurrentFabStep.IsEqpLoadingStep == false)
                return defaultTransferTime;

            var toArranges = InputMart.Instance.EqpArrangeView.FindRows(lot.LineID, (lot.Product as FabProduct).PartID, lot.CurrentStepID);
            if (toArranges.IsNullOrEmpty())
                return defaultTransferTime;

            if (lot.CurrentFabPlan.ArrivalTimeDict == null)
                lot.CurrentFabPlan.ArrivalTimeDict = new Dictionary<string, DateTime>();
            if (lot.CurrentFabPlan.DeliveryDict == null)
                lot.CurrentFabPlan.DeliveryDict = new MultiDictionary<Time, string>();

            var fromEqp = ResourceHelper.GetEqp(fromEqpId);

            foreach (var arr in toArranges)
            {
                var deliveryTime = defaultTransferTime;

                if (arr.Eqp.BayID.IsNullOrEmpty() == false)
                {
                    var orgTime = Time.FromMinutes(fromEqp.DeliveryTimeDict.SafeGet(arr.Eqp.BayID));
                    deliveryTime = Time.FromMinutes(fromEqp.DeliveryTimeDict.SafeGet(arr.Eqp.BayID)).Floor();
                    // 올림처리 하고 싶은데 ceiling()이 밀리초를 해결 못해줘서 우회구현

                    if (orgTime - deliveryTime > Time.Zero)
                        deliveryTime = deliveryTime + 1;
                }

                lot.CurrentFabPlan.DeliveryDict.Add(deliveryTime, arr.Eqp.ResID);

                if (lot.CurrentFabPlan.ArrivalTimeDict.ContainsKey(arr.Eqp.BayID) == false)
                    lot.CurrentFabPlan.ArrivalTimeDict.Add(arr.Eqp.BayID, (DateTime)(AoFactory.Current.NowDT + deliveryTime));
            }

            var minDeliveryTime = lot.CurrentFabPlan.DeliveryDict.Keys.Min();

            foreach (var item in lot.CurrentFabPlan.DeliveryDict)
            {
                if (item.Key == minDeliveryTime)
                    continue;

                var args = new Tuple<IHandlingBatch, FabSemiconStep>(hb, hb.CurrentStep as FabSemiconStep);
                AoFactory.Current.AddTimeout(item.Key, ResourceHelper.CallRemoveAndReEnter, args);
            }

            return minDeliveryTime; 
#endif
        }

        public void ON_TRANSFER0(IHandlingBatch hb, ref bool handled)
        {
            //if (Helper.GetConfig(ArgsGroup.Logic_Qtime).applyQtime <= 0)
            //    handled = true;
        }

        public Time GET_TRANSFER_TIME_TRANSPORT(IHandlingBatch hb, ref bool handled, Time prevReturnValue)
        {
            if (TransportSystem.Apply == false)
                return prevReturnValue;

            handled = true;
            var lot = hb.Sample as FabSemiconLot;

            Time moveTime = TransportSystem.GetTransferTimeTransport(lot.LastLocation, lot.ReservedLocation);
            if (moveTime <= Time.Zero)
                return Time.Zero; // Bucketing 중에 MoveNext발생해도 Buffer간 이동을 하지 않으므로 TransferTime 소요할 필요 없음.

            TransportSystem.WriteTransferLog(hb, lot.ReservedLocation, moveTime);

            return moveTime;
        }

        public void ON_TRANSFERED_TRANSPORT(IHandlingBatch hb, ref bool handled)
        {
            if (TransportSystem.Apply)
            {
                var lot = hb as FabSemiconLot;

                // Port 도착은 1회만 Attach 발생, Buffer면 SimulationStep 도달할 때 까지 반복 시도 발생 함.
                var location = lot.ReservedLocation ?? lot.Location as Buffer;
                location.Attach(lot);

                // Buffer -> Port로 이동하면서 DispatchIn이 재차 발생하기 때문에, 중복 등록 방지 필요
                var da = AoFactory.Current.GetDispatchingAgent("-");
                da.Remove(lot);
            }
        }

        public Time GET_TRANSFER_TIME_INIT(IHandlingBatch hb, ref bool handled, Time prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;
            if (lot.RemainTransferTime <= Time.Zero)
                return prevReturnValue;

            // 초기 MOVE 상태만 처리.
            handled = true;

            var moveTime = lot.RemainTransferTime;
            lot.RemainTransferTime = Time.Zero;

            return moveTime;
        }
    }
}