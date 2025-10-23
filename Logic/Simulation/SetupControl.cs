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
    public partial class SetupControl
    {
        public Time GET_SETUP_TIME0(Mozart.SeePlan.Simulation.AoEquipment aeqp, IHandlingBatch hb, ref bool handled, Time prevReturnValue)
        {
            // 초기 SETUP 상태의 WIP 처리를 위해 추가 구현
            // LocateForRun에서 AddRun 호출하면 이 함수도 불리지만, Setup 시간을 리턴해도 동작을 안함
            Time setupTime;
            var lot = hb.Sample as FabSemiconLot;

            if (lot.IsWipHandle && lot.FabWipInfo.WipState == "SETUP")
            {
                setupTime = lot.FabWipInfo.WipStateTime - aeqp.NowDT;
                if (setupTime < Time.Zero)
                    setupTime = Time.FromMinutes(Helper.GetConfig(ArgsGroup.Resource_Eqp).defaultSetupTimeMinutes);

                return setupTime;
            }

            var from = (aeqp.LastPlan as FabPlanInfo)?.Arrange;
            var to = (lot.CurrentPlan as FabPlanInfo)?.Arrange;

            setupTime = ResourceHelper.GetSetupTime(aeqp, from, to);
            var eqp = aeqp.Target as FabSemiconEqp;

            Time remainTime = Time.Zero;
            if (eqp.DoSetupOnTrackOut && aeqp.IsProcessing)
            {
                var eqpProc = aeqp.Processes[0];
                remainTime = eqpProc.GetRemainTimeToEnd();
                setupTime += remainTime;
            }

            // lot의 type이 확장될 경우를 대비해 eqp가 batchType에 한정짓지 않도록 구현.
            if (lot != null)
            {
                CycleTimePeriodic periodicObj = StatisticHelper.GetOrAddPeriodicObject(lot, eqp);

                if (periodicObj != null)
                {
                    if (eqp.ToolingInfo.IsNeedReticle)
                    {
                        periodicObj.ReticleMin += GetSetupMinutesUntilSimEnd(setupTime);

                        // 셋업이 발생하지 않아도 카운트는 집계하는 것으로 변경됨.
                        if (aeqp.LastPlan != null)
                        {
                            var fromReticleID = aeqp.LastPlan.ToolID;
                            var toReticleID = hb.Sample.CurrentPlan.ToolID;

                            if (fromReticleID != toReticleID)
                                periodicObj.ReticleCnt++;
                        }
                    }

                    if (lot.CurrentFabPlan.HasChuckSwapLoss)
                    {
                        //TODO: 여러 Setup이 동시에 더해졌을 때, 알맞게 계산할 방법 필요.
                        //TODO: DoSetupOnTrackOut 과 같이 쓴다면 어떻게 계산할지 고민 필요.
                        var chuckSwapLossTime = GetSetupMinutesUntilSimEnd(eqp.ChuckSwapLossTime);

                        periodicObj.ChuckMin += chuckSwapLossTime;
                        periodicObj.ChuckCnt++;

                        setupTime += Time.FromMinutes(chuckSwapLossTime);
                    }
                }
            }

            return setupTime;

            static double GetSetupMinutesUntilSimEnd(Time setupTime)
            {
                // LOAD_STAT의 SETUP 시간 비율과 맞춰주기 위해서 값을 조정
                if (AoFactory.Current.NowDT + setupTime > ModelContext.Current.EndTime)
                {
                    return (ModelContext.Current.EndTime - AoFactory.Current.NowDT).TotalMinutes;
                }

                return setupTime.TotalMinutes;
            }
        }

        public ISet<string> GET_NEED_SETUP_CHAMBERS0(AoEquipment aeqp, ChamberInfo[] loadableChambers, IHandlingBatch hb, ref bool handled, ISet<string> prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;
            if (lot.CurrentFabPlan.NeedSetupChambers.IsNullOrEmpty())
                return null;
            return lot.CurrentFabPlan.NeedSetupChambers;
        }
    }
}