using Mozart.SeePlan.DataModel;
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
using System.Diagnostics;
using Mozart.Simulation.Engine;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class EqpInit
    {
        public IEnumerable<Mozart.SeePlan.DataModel.Resource> GET_EQP_LIST0(ref bool handled, IEnumerable<Mozart.SeePlan.DataModel.Resource> prevReturnValue)
        {
            try
            {
                return InputMart.Instance.FabSemiconEqp.Rows;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "-");
                return null;
            }
        }

        public void INITIALIZE_EQUIPMENT0(Mozart.SeePlan.Simulation.AoEquipment aeqp, ref bool handled)
        {
            aeqp.UseProcessingTime = false;

            var eqp = ResourceHelper.GetEqp(aeqp.EqpID);
            var feqp = aeqp as FabAoEquipment;

            if (TransportSystem.Apply && eqp.HasPort)
            {
                feqp.JobPrepCandidates = new List<IHandlingBatch>();
                feqp.JobPrepLotList = new List<IHandlingBatch>();
            }

            feqp.CurrentState = eqp.State == ResourceState.Up ? LoadingStates.IDLE : LoadingStates.DOWN;

            eqp.SimObject = feqp;

            if (eqp.ToolingInfo.IsNeedReticle)
                eqp.ToolingInfo.SelectableReticleList = new List<FabReticle>();

            if (eqp.SimType == SimEqpType.UnitBatch)
            {
                feqp.Processes[0].Capacity = eqp.UnitBatchInfo.MaxPortCount;
            }

            ResourceHelper.SetProcessInhibit(feqp);

            if (InputMart.Instance.ApplyDeliveryTime && InputMart.Instance.DELIVERY_TIMEView.IsNullOrEmpty() == false)
            {
                if (eqp.LocationInfo != null)
                {
                    var deliveryTimes = InputMart.Instance.DELIVERY_TIMEView.FindRows(eqp.LocationInfo.Bay);
                    if (deliveryTimes.IsNullOrEmpty() == false)
                    {
                        eqp.DeliveryTimeDict = new Dictionary<string, DeliveryTimeInfo>();
                        foreach (var item in deliveryTimes)
                        {
                            DeliveryTimeInfo info = CreateHelper.CreateDeliveryTimeInfo(item);

                            eqp.DeliveryTimeDict.Add(info.ToBayID, info);
                        }
                    }
                }
            }

            if (Helper.GetConfig(ArgsGroup.Logic_Photo).forceStandbyRule == 1)
            {
                if (eqp.ForceStandbyRate > 0)
                {
                    var avgTact = InputMart.Instance.ResourceProcTimeDict.SafeGet(aeqp.EqpID).TactTime;
                    var avgDispatchPerDay = 1440 / (avgTact.TotalMinutes * InputMart.Instance.LotMergeSize);

                    var forceStandbyMinutesPerDay = 1440 * eqp.ForceStandbyRate;
                    var standbyDuration = 14.4; // = 1440 * 0.01 (하루 1% 고정값)
                    var forceStandbyCountPerDay = forceStandbyMinutesPerDay / standbyDuration;
                    eqp.ForceStandbyProbability = forceStandbyCountPerDay / avgDispatchPerDay;
                }
            }

            if (eqp.SimType == SimEqpType.LotBatch)
            {
                feqp.NeedUpstreamBatching = true;
                EventHelper.AddManualEvent(Time.Zero, ManualEventTaskType.CallBatchBuild, feqp, "INITIALIZE_EQUIPMENT0");
            }
        }

        public DateTime GET_EQP_UP_TIME0(Resource resource, DateTime stateChangeTime, ref bool handled, DateTime prevReturnValue)
        {
            var eqp = resource as FabSemiconEqp;

            // EndTime에 디스패칭 발생하는 결과가 나타남.
            if (eqp.State == ResourceState.Down)
                return ModelContext.Current.EndTime;

            return DateTime.Now;
        }

        public DateTime SET_EQP_UP_TIME(Resource resource, DateTime stateChangeTime, ref bool handled, DateTime prevReturnValue)
        {
            var eqp = resource as FabSemiconEqp;
            eqp.EqpUpTime = prevReturnValue;

            return prevReturnValue;
        }
    }
}