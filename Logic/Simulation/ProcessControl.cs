using Mozart.Simulation.Engine;
using Mozart.SeePlan.Simulation;
using Mozart.SeePlan.DataModel;
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
using Mozart.Data.Entity;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class ProcessControl
    {
        public ProcTimeInfo GET_PROCESS_TIME0(Mozart.SeePlan.Simulation.AoEquipment aeqp, IHandlingBatch hb, ref bool handled, ProcTimeInfo prevReturnValue)
        {
            return ResourceHelper.GetProcessTime(aeqp, hb);
        }

        public string[] GET_LOADABLE_CHAMBERS0(AoChamberProc2 cproc, IHandlingBatch hb, ref bool handled, string[] prevReturnValue)
        {
            var lot = hb as FabSemiconLot;

            var arr = lot.CurrentFabPlan.Arrange;
            if (arr == null || arr.SubEqps.IsNullOrEmpty())
                return cproc.Chambers.Select(x => x.Label).ToArray();
            else
                return arr.SubEqps.Select(x => x.SubEqpID).ToArray();
        }

        public void ON_TRACK_IN0(AoEquipment aeqp, IHandlingBatch hb, ref bool handled)
        {
            foreach (var entity in hb)
            {
                var lot = entity as FabSemiconLot;

                var arr = lot.CurrentArranges.SafeGet(aeqp.EqpID);
                if (arr == null)
                    arr = lot.CurrentAttribute.BackupArranges.Where(x => x.EqpID == aeqp.EqpID).FirstOrDefault();

                lot.CurrentArranges.Clear();
                lot.CurrentFabPlan.Arrange = arr;

                if (arr != null)
                    lot.CurrentPlan.RecipeID = arr.RecipeID;

                ResourceHelper.UpdateCrossFabInfo(aeqp, lot);
            }
        }

        public bool IS_NEED_SETUP0(AoEquipment aeqp, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
             // DO_FILTER_DEF 에서도 불림. (IsNeedSetup 또는 IsNeedSetupCrew 호출 시)

            var lot = hb.Sample as FabSemiconLot;
            if (lot.IsWipHandle && lot.FabWipInfo.WipState == "SETUP")
                return true;

            // SemiconEqp.IsNeedSetup은 더이상 안씀
            var setupInfos = (aeqp as FabAoEquipment).Eqp.SetupInfos;
            if (setupInfos.IsNullOrEmpty())
                return false;

            if (aeqp.LastPlan == null)
                return false;

            // 둘중 하나라도 정보가 없으면, 전후가 같은지 다른지 알 수 없음.
            var from = (aeqp.LastPlan as FabPlanInfo).Arrange;
            var to = ArrangeHelper.GetCurrentEqpArrange(lot, aeqp);

            Time setupTime = ResourceHelper.GetSetupTime(aeqp, from, to);

            // ParallelChamber의 경우에는 Chamber중 하나라도 setupTime이 0초 이상이라면 SetUp이 필요하다고 판단
            if (aeqp.IsParallelChamber)
                return ResourceHelper.ChooseParallelChamberSetupList(aeqp, lot, ref handled);

            return setupTime > Time.Zero;
        }

        public void UPDATE_PM_CURRENT_COUNT(AoEquipment aeqp, AoProcess proc, IHandlingBatch hb, ref bool handled)
        {
            var feqp = aeqp as FabAoEquipment;
            var eqp = feqp.Eqp;
            var lot = hb.Sample as FabSemiconLot;
            var waferQty = hb.Select(x=> x.UnitQty).Sum();

            if (eqp.QuantityDownConfigs.IsNullOrEmpty())
                return;

            foreach (var config in eqp.QuantityDownConfigs)
            {
                config.CurrentCount += waferQty;

                if (config.CurrentCount >= config.LimitCount)
                {
                    config.CurrentCount = 0;

                    // PM 시작시각을 현재 시각에서 1초 더해서 Shift 유도하는 방식은 UnitBatch 설비에서 PM이 병렬로 처리되는 문제가 있음.
                    // 따라서 아래 adjustStartTime 계산 값으로 업데이트하도록 변경함.
                    var lotProcessingTime = ResourceHelper.GetLotProcessingTimeSeconds(lot, eqp);
                    var adjustStartTime = aeqp.NowDT.AddSeconds(lotProcessingTime);
                    var tag = ResourceHelper.CreateEqpDownTagConditional(adjustStartTime, config);

                    // PdfFlow로 동작하려고 구현위치를 GetProcessTime보다 뒤로 옮김.
                    var adjustEndTime = adjustStartTime.AddSeconds(tag.DurationSecond);

                    // 새로 발생시킬 CBM과 구간이 겹치는 TBM들을 저장해둘 List
                    List<PMSchedule> overlapPM = new List<PMSchedule>();

                    // 기존에 등록되어있는 PM_TBM중에서 겹치는 PM이 있는지 확인.
                    foreach (var sched in feqp.DownManager.ScheduleTable)
                    {
                        var pm = sched.Tag as PMSchedule;

                        // tbm의 endTime보다 tag의 시작시간이 크다면 다음 tbm을 가져오자
                        if (pm.EndTime < adjustStartTime)
                            continue;

                        // ScheduleTable은 정렬이 되어있기에 tbm의 시작시간이 tag의 endTime보다 크면 이후 tbm들도 조건에 부적합하다.
                        if (pm.StartTime > adjustEndTime)
                            break;

                        overlapPM.Add(pm); 
                    }

                    if (overlapPM.Count > 0)
                    {
                        // overlapPM에 저장되어 있는 가장 긴 TBM이 새로 발생되는 CBM보다 짧으면 스킵
                        var maxOverlapItem = overlapPM.Max(item => item.Duration.TotalSeconds);
                        if (maxOverlapItem >= tag.DurationSecond)
                            continue;

                        // 기 등록된 Event 중, 새로 발생시킬 CBM과 겹치고 기간이 더 짧은 PM들은 Cancel 처리.
                        foreach (var overlapItem in overlapPM)
                        {
                            ResourceHelper.CancelOverlappedPM(feqp, overlapItem);
                        }
                    }

                    var newPM = ResourceHelper.CreatePMSchedule(tag);
                    feqp.DownManager.AddEvent(newPM);
                }
            }
        }

        public double GET_PROCESS_UNIT_SIZE0(AoEquipment aeqp, IHandlingBatch hb, ref bool handled, double prevReturnValue)
        {
            // UnitBatch는 Misc/IsBatchType의 BatchType으로 취급하지 않음. (BatchBuilding 도 하지 않음)
            if (aeqp.IsBatchType()) // BatchInline/LotBatch
                return 1;
            else if (aeqp.Target.SimType == SimEqpType.UnitBatch)
                return 1;

            return hb.UnitQty;
        }

        public bool IS_NEED_CHUCK_SWAP(AoEquipment aeqp, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            var lot = hb.Sample as FabSemiconLot;
            if (lot == null)
                return prevReturnValue;

            // 현재 [1] apply chuck swap rate만 구현된 상태이고, [2] apply chuck swap rule은 설계된 로직이 없음.
            if (Helper.GetConfig(ArgsGroup.Logic_Photo).chuckSwapRule == (int)ChuckSwapRule.Ignore)
                return prevReturnValue;

            var eqp = aeqp.Target as FabSemiconEqp;
            var lossRate = eqp.ChuckLossRate;

            if (lossRate <= 0)
                return prevReturnValue;

            //TODO: SetupShift 사용할 경우, 선행 판단과 실제 판단 결과가 달라질 수 있기 때문에 개선이 필요.
            bool loss = Helper.GetBernoulliTrialResult(lossRate);
            if (loss)
            {
                lot.CurrentFabPlan.HasChuckSwapLoss = true;
                return true;
            }
            return prevReturnValue;
        }

        public bool IS_NEED_RETICLE_CHANGE(AoEquipment aeqp, IHandlingBatch hb, ref bool handled, bool prevReturnValue)
        {
            // Setup 시간은 발생하지 않을 수 있으나, SUMMARY 집계 위치를 유지하기 위해 GetSetupTime 액션으로 보내도록 구현.

            var eqp = aeqp.Target as FabSemiconEqp;
            if (eqp.ToolingInfo.IsNeedReticle == false || aeqp.LastPlan == null)
                return prevReturnValue;

            var fromReticleID = aeqp.LastPlan.ToolID;
            var toReticleID = hb.Sample.CurrentPlan.ToolID;

            if (fromReticleID != toReticleID)
                return true;

            return prevReturnValue;
        }

        public IHandlingBatch[] INTERCEPT_MOVE_TRANSPORT(AoEquipment aeqp, IHandlingBatch hb, ref bool handled, IHandlingBatch[] prevReturnValue)
        {
            if (TransportSystem.Apply == false)
                return new IHandlingBatch[] { hb }; // 리턴한 객체에 대해서 MoveNext 발생시킴..

            var control = EntityControl.Instance;
            var nexts = control.MoveNext(hb, aeqp.NowDT);
            foreach (var n in nexts)
            {
                var lot = n.Sample as FabSemiconLot;

                if (control.IsDone(n))
                {
                    control.OnDone(hb);
                    continue;
                }

                if (lot.MovingState == LocationType.PORT)
                    TransportSystem.WhereNext(n);
            }

            return null; // MoveNext 수행했으므로 null을 리턴
        }
    }
}