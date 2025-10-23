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
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.SeePlan;
using Mozart.Simulation.Engine;
using System.Text;
using System.Diagnostics;
using Mozart.Data.Entity;
using Mozart.Task.Framework;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class DispatcherControl
    {
        public Type GET_LOT_BATCH_TYPE0(ref bool handled, Type prevReturnValue)
        {
            return typeof(LotBatch);
        }

        public bool IS_WRITE_DISPATCH_LOG0(AoEquipment aeqp, ref bool handled, bool prevReturnValue)
        {
            // 이 FeAction은 한 번의 Dispatching 에서 최대 3번 불림
            // [1] 디스패칭 이전
            // [2] DO_SELECT_DEF 에서 호출
            // [3] 디스패칭 이후

            // Lot이 모두 필터되어 DoSelect 단계로 넘어가지 않으면, [2], [3]은 불리지 않음.

            // 때문에 이 위치에 ADD_WAKEUP_TIMEOUT 을 구현하는 것은 적절치 않으며
            // DoFilter 쪽으로 구현 위치를 변경합니다.

            if (Helper.GetConfig(ArgsGroup.Simulation_Output).writeEqpDispatchLog == 0)
                return false;

            if (TransportSystem.Apply)
                return false;

            var write = aeqp.IsBatchType() == false;

            return write;
        }

        [ThreadStatic]
        System.Text.StringBuilder _stringBuilder = new System.Text.StringBuilder(1000);

        public void WRITE_DISPATCH_LOG0(DispatchingAgent da, EqpDispatchInfo info, ref bool handled)
        {
            // IsWriteDispatchLog가 false인데, 이 함수를 타면 info.DispatchTime 값이 세팅되지 않으면서,
            // Output Key중복 -> Memory Release안되는 이슈가 발생할 수 있음.

            if (Helper.GetConfig(ArgsGroup.Simulation_Output).writeEqpDispatchLog == 0)
                return;

            if (InputMart.Instance.ExcludeOutputTables.Contains("EQP_DISPATCH_LOG"))
                return;

            var log = new EQP_DISPATCH_LOG();

            FabSemiconEqp eqp = info.TargetEqp as FabSemiconEqp;

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;
            log.EQP_ID = eqp.ResID;
            log.EVENT_TIME = AoFactory.Current.NowDT;// info.DispatchTime;

            if (Helper.GetConfig(ArgsGroup.Simulation_Output).writeEqpDispatchLog != 2)
            {
                _stringBuilder.Clear();
                var sb = _stringBuilder;

                if (info.FilterInfos != null)
                {
                    bool firstReason = true;
                    foreach (KeyValuePair<string, EntityFilterInfo> it in info.FilterInfos)
                    {
                        log.FILTERED_WIP_COUNT += it.Value.FilterWips.Count;

                        if (!firstReason)
                            sb.Append(';');
                        else firstReason = false;

                        sb.Append(it.Key + " : ");

                        bool first = true;
                        foreach (ILot lot in it.Value.FilterWips)
                        {
                            if (!first)
                                sb.Append(',');
                            else first = false;

                            FabSemiconLot flot = lot as FabSemiconLot;

                            sb.Append(string.Join("/", flot.LotID, flot.FabProduct.PartID, flot.CurrentStepID, flot.UnitQty.ToString()));
                        }
                    }
                }

                var filteredWipLog = sb.ToString();

                log.FILTERED_WIP_LOG = Helper.GetVarchar255(filteredWipLog);
                log.DISPATCH_WIP_LOG = eqp.Preset != null ? Helper.GetVarchar255(info.DispatchWipLog) : string.Empty;
            }

            log.INIT_WIP_CNT = info.InitialWipCount;
            log.SELECTED_WIP_COUNT = StringUtility.IsEmptyID(info.SelectedWipLog) == false ? info.SelectedWipLog.Split(';').Length : 0;
            log.SELECTED_WIP = info.SelectedWipLog.IsNullOrEmpty() ? "-" : Helper.GetVarchar255(info.SelectedWipLog);

            OutputMart.Instance.EQP_DISPATCH_LOG.Add(log);
        }

        public void UPDATE_CONTEXT0(IDispatchContext dc, AoEquipment aeqp, IList<IHandlingBatch> wips, ref bool handled)
        {
            if (aeqp.IsBatchType() || aeqp.Dispatcher is FifoDispatcher || wips.IsNullOrEmpty())
                return;

            WeightHelper.SetDispatchContext(dc, aeqp);
        }

        public string ADD_DISPATCH_WIP_LOG1(Mozart.SeePlan.DataModel.Resource eqp, EntityDispatchInfo info, ILot lot, WeightPreset wp, ref bool handled, string prevReturnValue)
        {
            var sb = StringBuilderCache.Acquire();

            var fLot = lot as FabSemiconLot;

            sb.Append(lot.LotID);
            sb.Append('/' + fLot.FabProduct.PartID);
            sb.Append('/' + fLot.CurrentStepID);
            sb.Append('/');
            sb.Append(fLot.UnitQty);
            //sb.Append('/' + fLot.UnitQty);

            if (wp != null)
            {
                foreach (var factor in wp.FactorList)
                {
                    var value = lot.WeightInfo.GetValue(factor);
                    if (value == double.MinValue)
                        value = 0;

                    sb.Append('/');
                    sb.Append(value);
                }
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        public IList<IHandlingBatch> EVALUATE1(DispatcherBase db, IList<IHandlingBatch> wips, IDispatchContext ctx, ref bool handled, IList<IHandlingBatch> prevReturnValue)
        {
            if (TransportSystem.Apply)
                return wips;

            if (db is FifoDispatcher)
                return wips;

            if (db.Comparer == null)
                return wips;

            var n = Helper.GetConfig(ArgsGroup.Logic_Dispatching).evaluationLotCount;
            if (wips.Count > n && n > 0)
            {
                // wips에는 EvaluatePriority가 가장 높은 재공만 넘겨받은 상태.
                List<IHandlingBatch> sortedWips = new List<IHandlingBatch>();
                int CompareTieBreak(IHandlingBatch a, IHandlingBatch b)
                {
                    var lotA = a as FabSemiconLot;
                    var lotB = b as FabSemiconLot;

                    var cmp = lotA.DispatchInTime.CompareTo(lotB.DispatchInTime);

                    if (cmp == 0)
                        cmp = lotA.LotID.CompareTo(lotB.LotID); // 재현이 안되는 상황을 예방.

                    return cmp;
                }

                foreach (var hb in wips)
                {
                    var lot = hb as FabSemiconLot;
                    sortedWips.AddSort(lot, CompareTieBreak);
                }

                var evaluateWips = sortedWips.Take(n).ToList();
                var result = db.WeightEval.Evaluate(evaluateWips, ctx);

                var rest = sortedWips.Skip(n);
                rest.ForEach(x => (x as FabSemiconLot).WeightInfo.Reset());
                //result.AddRange(rest);

                return result;
            }
            else
                return db.WeightEval.Evaluate(wips, ctx);
        }

        public IHandlingBatch[] DO_SELECT_BATCH(DispatcherBase db, AoEquipment aeqp, IList<IHandlingBatch> wips, IDispatchContext ctx, ref bool handled, IHandlingBatch[] prevReturnValue)
        {
            // DO_SELECT_BATCH_DEF는 이 Definition으로 대체되었지만,
            // -Predefined- 액션이 유지되도록 disable 상태로 둠.
            var control = BatchLoadingControl.Instance;

            if (control.IsNeedBatchLoading(aeqp, wips))
            {
                handled = true;

                LotBatch selected = control.GetLoadableBatch(aeqp, wips);

                if (selected != null)
                {
                    selected = control.ImproveBatch(aeqp, wips, selected);

                    control.OnLoading(aeqp, wips, selected);

                    var result = selected != null ? selected.Contents.Select(x => EntityHelper.GetLot(x)).ToArray() : null;

                    Helper.ClearLotBatchMemory(selected);

                    return result;
                }

                return null;
            }

            return prevReturnValue;
        }

        public IHandlingBatch[] SELECT1(DispatcherBase db, AoEquipment aeqp, IList<IHandlingBatch> wips, ref bool handled, IHandlingBatch[] prevReturnValue)
        {
            try
            {
                var eqp = aeqp.Target as FabSemiconEqp;
                if (eqp.MaxWafersAvoidSwitch > 0)
                {
                    var selected = wips[0] as FabSemiconLot;
                    var setupName = ArrangeHelper.GetCurrentEqpArrange(selected, aeqp).SetupName;
                    if (setupName.IsNullOrEmpty() == false)
                    {
                        var avoidances = wips.Select(x => (x as FabSemiconLot)).Where(x => ArrangeHelper.GetCurrentEqpArrange(x, aeqp).SetupName == setupName).ToArray();
                        int avoidQty = 0;
                        int i = 0;
                        while (avoidQty < eqp.MaxWafersAvoidSwitch)
                        {
                            if (i >= avoidances.Length)
                                break;

                            var lot = avoidances[i++];
                            avoidQty += lot.UnitQty;
                        }
                        var avoidanceCount = Math.Max(1, i - 1); // 적어도 1개는 선택하고, MaxWafersAvoidSwitch를 넘지는 않도록
                        avoidances = avoidances.Take(avoidanceCount).ToArray();

                        return avoidances;
                    }
                }

                if (aeqp.DownManager.ScheduleTable.IsNullOrEmpty())
                    return new IHandlingBatch[] { wips[0] };

                var nextDown = aeqp.DownManager.GetNextStartScheduleItem();
                if (nextDown == null)
                    return new IHandlingBatch[] { wips[0] };

                EqpDownTag tag = ResourceHelper.GetEqpDownTag(aeqp.Target as FabSemiconEqp, nextDown.EventTime);

                if (tag == null)
                    return new IHandlingBatch[] { wips[0] };

                // Unsched Down 시각이 Shift되지 않도록 Lot 투입을 억제
                // 특정 고객의 테스트 시나리오에 특화된 요구사항이 반영된 것임.
                // Unschedule Down은 "BM"으로 표준화 되었으며, 여전히 일반적으로 적용할만한 로직인지는 재검토가 필요할 수 있음.
                if (tag != null && tag.DownType == EqpDownType.BM)
                {
                    var selected = wips[0];
                    var unloadingTime = aeqp.Processes.First().GetUnloadingTime(selected);
                    if (unloadingTime > nextDown.EventTime)
                    {
                        (aeqp as FabAoEquipment).IsWaitingUD = true;
                        return new IHandlingBatch[] { };
                    }
                }

                return new IHandlingBatch[] { wips[0] };
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, aeqp.EqpID, "-");
                return new IHandlingBatch[] { wips[0] };
            }
        }

        public void ON_DISPATCHED0(DispatchingAgent da, AoEquipment aeqp, IHandlingBatch[] wips, ref bool handled)
        {
            var selected = wips[0] as FabSemiconLot;

            if (selected.ToolSettings != null)
                selected.CurrentFabPlan.ToolID = selected.ToolSettings.Items.Select(x => x.ResourceKey).Join(",");

            if (selected.CurrentBOM != null && selected.CurrentBOM.ProcessedSteps != null)
                selected.CurrentBOM.ProcessedSteps.Add(selected.CurrentStepID);

            var eqp = aeqp.Target as FabSemiconEqp;

            //TODO: WaitSetupTimeValue 세팅해도 동작 안하는 이유??
            //if (eqp.DoSetupOnTrackOut && aeqp.IsProcessing)
            //{
            //    var eqpProc = aeqp.Processes[0];
            //    eqpProc.WaitSetupTimeValue = eqpProc.GetRemainTimeToEnd();
            //}
        }
    }
}