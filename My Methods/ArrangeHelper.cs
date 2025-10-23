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
using Mozart.SeePlan.Simulation;
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.Simulation.Engine;
using static Mozart.SeePlan.Simulation.WipTags;
using Mozart.SeePlan.DataModel;
using System.Text;
using static LinqToDB.Sql;
using System.Diagnostics;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class ArrangeHelper
    {
        internal static EqpArrange CreateEqpArrange(ARRANGE entity, string parentEqpID = null)
        {
            var eqp = ResourceHelper.GetEqp2(entity.EQP_ID);
            if (eqp == null)
                return null;

            EqpArrange arr = entity.ToEqpArrange();
            arr.Eqp = eqp;

            if (parentEqpID != null)
                arr.EqpID = parentEqpID;

            if (entity.TOOLING_NAME != null)
            {
                var splits = entity.TOOLING_NAME.Split(',');
                arr.ToolingData = new FabToolData();
                foreach (var name in splits)
                {
                    var toolingList = InputMart.Instance.ToolingMap.SafeGet(name);

                    if (toolingList.IsNullOrEmpty() == false)
                    {
                        toolingList.ForEach(x => arr.ToolingData.ToolingArranges.Add(name, x));

                        ToolingType t = Helper.Parse(toolingList.First().ToolingType, ToolingType.Reticle);
                        arr.ToolingData.ToolingItems.Add(new Tuple<ToolingType, string>(t, name));
                    }
                }

                if (arr.ToolingData.ToolingArranges.IsNullOrEmpty())
                {
                    arr.ToolingData = null;
                }
                else
                {
                    if (arr.ToolingData.ToolingItems.Any(x => x.Item1 == ToolingType.Reticle))
                        arr.Eqp.ToolingInfo.IsNeedReticle = true;
                }
            }

            InputMart.Instance.EqpArrange.ImportRow(arr);

            string colName;
            if (InputMart.Instance.EqpSetupDic.TryGetValue(eqp.ResID, out colName))
            {
                // 퍼포먼스 이득을 위해 굳이 Reflection을 사용하지 않음.
                if (colName == "RECIPE_ID")
                    arr.SetupName = arr.RecipeID;
                else if (colName == "TOOLING_NAME")
                    arr.SetupName = arr.ToolingName;
                else if (colName == "PART_ID")
                    arr.SetupName = arr.PartID;
                else if (colName == "STEP_ID")
                    arr.SetupName = arr.StepID;
            }

            return arr;
        }

        public static void SafeAdd(this Dictionary<string, EqpArrange> dict, string eqpID, EqpArrange eqpArrange)
        {
            if (dict.ContainsKey(eqpID))
                return;

            dict.Add(eqpID, eqpArrange);
        }

        internal static StackActiveInfo GetActiveStack(FabSemiconLot lot, PartStepAttribute attr)
        {
            if (InputMart.Instance.ApplyStacking == false)
                return null;

            if (lot == null)
                return null;
            
            if (attr == null)
                attr = lot.CurrentFabStep.PartStepDict.SafeGet(lot.FabProduct.PartID);

            if (attr == null)
                return null; // unexpected

            try
            {
                var stackStepInfo = attr.StackInfoDict.SafeGet(lot.CurrentFabStep);
                if (stackStepInfo == null)
                {
                    if (lot.ActiveStackDict.IsNullOrEmpty() == false)
                    {
                        // Group내 Non-Stackin Step
                        // StackEqp가 ProcessInhibit 중이면 prioritize.
                        if (lot.ActiveStackDict.Values.Any(x => x.StackEqp != null && x.StackEqp.SimObject.OnProcessInhibit))
                        {
                            foreach (var arr in attr.CurrentArranges)
                            {
                                SetLotEvaluatePriority(lot, lot.CurrentStepID, arr.EqpID, int.MinValue);
                            }
                        }
                    }

                    return null;
                }

                if (stackStepInfo.IsFirstLayer)
                {
                    // FirstLayer를 Rework하는 경우, 별도의 조치가 없으면 Stack정보를 초기화 한 상태.
                    // StackGroup에 아직 진입하지 않은, 새로 도착한 Lot처럼 취급함 (ProcessInhibit 걸린 설비로도 진행 불가)
                    if (lot.ActiveStackDict.ContainsKey(stackStepInfo.StackGroupID))
                    {
                        // 초기화 하지 않고 StackEqp를 유지하고 싶을 때 이 코드를 호출하게 될 예정.
                        lot.CurrentActiveStackInfo = lot.ActiveStackDict.SafeGet(stackStepInfo.StackGroupID);
                        return lot.CurrentActiveStackInfo;
                    }

                    StackActiveInfo activeStack = CreateActiveStack(stackStepInfo, null);

                    lot.ActiveStackDict.Add(stackStepInfo.StackGroupID, activeStack);
                    lot.CurrentActiveStackInfo = activeStack;
                }
                else
                {
                    StackActiveInfo activeStack = lot.ActiveStackDict.SafeGet(stackStepInfo.StackGroupID);
                    if (activeStack == null)
                        activeStack = CreateActiveStack(stackStepInfo, null); // 정보 누락시 보완용도
                    else
                        activeStack.StackStepInfo = stackStepInfo;

                    // step이 바뀌면 currentStackArrange 업데이트
                    
                    activeStack.CurrentStackArrange = attr.CurrentArranges.Where(x => x.Eqp == activeStack.StackEqp).FirstOrDefault();
                    if (activeStack.CurrentStackArrange == null)
                    {
                        // 1. S Step에서 Backup설비로 진행한 경우, 원본 설비가 살아나도 Y Step.에서 BackupEqp로 진행시키기 위해 세팅.
                        // 2. 일반적으로는 OnDispatchIn에서 Backup설비로 바로 Enqueue하게 되므로, 에러방지 목적의 세팅.
                        if (attr.BackupArranges.IsNullOrEmpty() == false) 
                            activeStack.CurrentStackArrange = attr.BackupArranges.Where(x => x.Eqp == activeStack.StackEqp).FirstOrDefault();
                    }
                    lot.CurrentActiveStackInfo = activeStack;

                    if (stackStepInfo.StackType == StackType.Y && activeStack.CurrentStackArrange != null && activeStack.StackEqp.SimObject.OnProcessInhibit)
                    {
                        // ProcessInhibit 걸린 설비를 StackEqp로 갖는 lot은 Y Step에서 prioritize
                        SetLotEvaluatePriority(lot, lot.CurrentStepID, activeStack.CurrentStackArrange.EqpID, int.MinValue);
                    }
                    else if (stackStepInfo.StackType == StackType.D)
                    {
                        var hasInhibitArr = attr.CurrentArranges.Any(x => x.Eqp.SimObject.OnProcessInhibit);
                        if (hasInhibitArr)
                        {
                            // DeStacking Step에서, ProcessInhibit 걸린 설비로 진행을 장려하기 위해, 나머지 arrange를 deprioritize
                            foreach (var arr in attr.CurrentArranges)
                            {
                                if (arr.Eqp.SimObject.OnProcessInhibit)
                                {
                                    if (activeStack.StackEqp == arr.Eqp) // ProcessInhibit 설비가 StackEqp이면, D Step에서도 해당 Arrange는 prioritize.
                                        SetLotEvaluatePriority(lot, lot.CurrentStepID, arr.EqpID, int.MinValue);

                                    continue;
                                }

                                SetLotEvaluatePriority(lot, lot.CurrentStepID, arr.EqpID, int.MaxValue);
                            }
                        }
                    }
                }

                var fhb = lot as FabHandlingBatch;
                if (fhb != null)
                {
                    // 나중에 Split 될 것을 대비하여, Content에도 같은 정보를 저장.
                    foreach (var item in fhb.mergedContents)
                    {
                        item.CurrentActiveStackInfo = lot.CurrentActiveStackInfo;

                        if (lot.CurrentActiveStackInfo.StackStepInfo.IsFirstLayer)
                            item.ActiveStackDict.Add(lot.CurrentActiveStackInfo.StackGroupID, lot.CurrentActiveStackInfo);
                    }
                }

                return lot.CurrentActiveStackInfo;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", lot.LotID);

                return null;
            }
        }

        public static StackActiveInfo CreateActiveStack(StackStepInfo stackStepInfo, FabSemiconEqp stackEqp)
        {
            StackActiveInfo activeInfo = new StackActiveInfo();
            activeInfo.StackStepInfo = stackStepInfo;
            activeInfo.StackGroupID = stackStepInfo.StackGroupID;
            activeInfo.StackEqp = stackEqp;

            return activeInfo;
        }

        internal static void UpdateActiveStackOnStart(FabSemiconLot lot, FabSemiconEqp eqp)
        {
            try
            {
                var activeStack = lot.CurrentActiveStackInfo;
                if (activeStack == null)
                    return;

                if (activeStack.StackStepInfo.IsFirstLayer)
                {
                    eqp.SimObject.DailyFirstLayerQty += lot.UnitQty;
                }

                // FirstLayer 진행 하면서 StackEqp가 결정됨.
                // 만약 FirstLayer에 대한 StackEqp 정보가 누락되면, 후속 Step에서 처음 로딩된 설비를 StackEqp로 지정 (정보 누락 보완조치)
                if (activeStack.StackStepInfo.IsFirstLayer || activeStack.StackEqp == null)
                {
                    activeStack.StackEqp = eqp;
                    eqp.SimObject.ActiveStackLotDict.SafeAdd(activeStack.StackGroupID, lot);

                    if (lot.ActiveStackDict.ContainsKey(activeStack.StackGroupID) == false)
                        lot.ActiveStackDict.Add(activeStack.StackGroupID, activeStack);

                    var fhb = lot as FabHandlingBatch;
                    if (fhb != null)
                    {
                        // 나중에 Split 될 것을 대비하여, Content에도 같은 정보를 저장.
                        fhb.mergedContents.ForEach(x => x.CurrentActiveStackInfo.StackEqp = eqp);
                    }
                }

                if (eqp != null && eqp.SimObject.IsReworkEffective)
                {
                    lot.CurrentFabPlan.EqpReworkInfo = eqp.ReworkInfos.FirstOrDefault(x => x.IsActive);
                }
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, eqp.ResID, lot.LotID);

                return;
            }
        }

        internal static void UpdateActiveStackOnEnd(FabSemiconLot lot)
        {
            var activeStack = lot.CurrentActiveStackInfo;
            if (activeStack == null)
                return;

            // Transfer 동안은 초기화 상태로 유지 (이전 Step정보를 달고 들어가서 오류 일으키는 것을 방지하기 위함)
            lot.CurrentActiveStackInfo = null;

            // FirstLayer에서 Rework 필요한 경우, 기존 Stack정보는 삭제 (StackEqp는 Rework 진행한 설비로 새로 결정됨)
            // LastLayer에서 Rework 필요한 경우, Stack정보 만료하지 말고 유지 (Rework까지 끝난 후 Delay중인 StackPM을 발동시키기 위함)
            if (lot.CurrentRework != null)
            {
                if (activeStack.StackStepInfo.IsFirstLayer)
                {
                    RemoveStackInfo(lot, activeStack);
                }

                // Rework 이후 TriggerStep을 재차 진행 완료했으면 return 하지 말고 아래 로직을 타야됨.
                if (EntityHelper.IsReworkEnd(lot) == false)
                    return;
            }

            if (activeStack.StackStepInfo.IsLastYLayer)
            {
                if (activeStack.StackEqp != null)
                {
                    // Lot은 ActiveStack 정보를 들고 있고, 설비에서만 제거.
                    activeStack.StackEqp.SimObject.ActiveStackLotDict.Remove(activeStack.StackGroupID, lot);

                    var feqp = activeStack.StackEqp.SimObject;

                    // ProcessInhibit 적용 대상인 StackPM만 Delay 반영 (Lot이 계속 흘러들어오면 의미 없으므로)
                    if (feqp.OnProcessInhibit && feqp.DelayedStackPM != null && feqp.ActiveStackLotDict.IsNullOrEmpty())
                    {
                        var pm = feqp.DelayedStackPM.Item1;
                        var tag = feqp.DelayedStackPM.Item2;

                        var stackPmStartTime = feqp.NowDT;
                        if (feqp.Loader.IsBlocked())
                        {
                            var currentPM = feqp.DownManager.GetLastestPMSchedule(true);
                            if (currentPM != null && currentPM.EndTime > feqp.NowDT)
                                stackPmStartTime = currentPM.EndTime;
                        }

                        pm.StartTime = stackPmStartTime;
                        pm.EndTime = stackPmStartTime.AddSeconds(tag.DurationSecond);

                        tag.StartTime = stackPmStartTime;
                        tag.EndTime = stackPmStartTime.AddSeconds(tag.DurationSecond);

                        RemoveOtherOverlapPM(feqp, pm);

                        feqp.DownManager.AddEvent(pm);

                        feqp.DelayedStackPM = null;
                    }
                }
            }

            if (activeStack.StackStepInfo.IsLastLayer)
            {
                RemoveStackInfo(lot, activeStack);
            }

            static void RemoveOtherOverlapPM(FabAoEquipment feqp, PMSchedule stackPM)
            {
                // ## 생성한 stackPM이 미래의 다른 PM과 겹치면, 다른 PM을 캔슬함.
                // 일반적인 경우는 더 duration이 긴 것을 취하지만, stackPM은 특별 취급.

                PMSchedule overlapPM = null;
                foreach (var sched in feqp.DownManager.ScheduleTable)
                {
                    var otherPM = sched.Tag as PMSchedule;
                    if (stackPM.StartTime >= otherPM.EndTime || stackPM.EndTime <= otherPM.StartTime)
                        continue;

                    overlapPM = otherPM;
                    break;
                }

                if (overlapPM != null)
                {
                    ResourceHelper.CancelOverlappedPM(feqp, overlapPM);
                }
            }
        }

        internal static void RemoveStackInfo(FabSemiconLot lot, StackActiveInfo activeStack)
        {
            lot.ActiveStackDict.Remove(activeStack.StackGroupID);

            if (activeStack.StackEqp != null)
                activeStack.StackEqp.SimObject.ActiveStackLotDict.Remove(activeStack.StackGroupID, lot);

            var fhb = lot as FabHandlingBatch;
            if (fhb != null)
            {
                fhb.mergedContents.ForEach(x => x.ActiveStackDict.Remove(activeStack.StackGroupID));
            }
        }

        internal static void SetStackActiveInfo(FabSemiconLot lot)
        {
            var rows = InputMart.Instance.STACK_ACTIVE_LOTView.FindRows(lot.LotID);
            if (rows.IsNullOrEmpty())
                return;

            foreach (STACK_ACTIVE_LOT entity in rows)
            {
                var eqp = ResourceHelper.GetEqp(entity.EQP_ID);
                if (eqp == null)
                    continue;

                var step = lot.Process.FindStep(entity.STEP_ID) as FabSemiconStep;
                if (step == null)
                    continue;

                var attr = step.PartStepDict.SafeGet(lot.CurrentPartID);
                if (attr == null)
                    continue;

                var stackInfo = attr.StackInfoDict.SafeGet(step); // S Step에 대한 StackInfo
                if (stackInfo == null)
                    continue;

                var activeStack = CreateActiveStack(stackInfo, eqp);

                if (lot.FabWipInfo.IsBohRun)
                {
                    var currentStep = lot.FabWipInfo.InitialStep as FabSemiconStep;
                    var currentAttr = currentStep.PartStepDict.SafeGet(lot.CurrentPartID);
                    if (currentAttr != null)
                    {
                        var currentStackStepInfo = currentAttr.StackInfoDict.SafeGet(currentStep);
                        if (currentStackStepInfo != null) // 초기에 StackStep에서 Run중일 경우, 즉시 업데이트 필요.
                        {
                            activeStack.StackStepInfo = currentStackStepInfo;
                            lot.CurrentActiveStackInfo = activeStack;
                        }
                    }
                }

                lot.ActiveStackDict.Add(stackInfo.StackGroupID, activeStack);

                eqp.SimObject.ActiveStackLotDict.SafeAdd(stackInfo.StackGroupID, lot);
            }
        }

        internal static PartStepAttribute GetOrAddPartStepAttribute(string partID, string stepID)
        {
            var attr = InputMart.Instance.PartStepAttributeView.FindRows(partID, stepID).FirstOrDefault();
            if (attr == null)
                attr = CreatePartStepAttribute(partID, stepID);

            return attr;
        }

        private static PartStepAttribute CreatePartStepAttribute(string partID, string stepID)
        {
            PartStepAttribute attr = new PartStepAttribute();
            attr.PartID = partID;
            attr.StepID = stepID;

            // 아직 CurrentArrange 세팅되기 이전
            //attr.PhotoGen = attr.CurrentArranges.IsNullOrEmpty() ? "-" : attr.CurrentArranges.First().Eqp.ScannerGeneration;

            attr.RouteSteps = BopHelper.GetRouteSteps(partID, stepID);
            foreach (var step in attr.RouteSteps)
            {
                // 동일 Part의 서로다른 MfgPart가 같은 Route를 사용할 경우 Key 중복이 발생할 수 있음.
                if (step.PartStepDict.ContainsKey(attr.PartID) == false)
                    step.PartStepDict.Add(attr.PartID, attr);
            }

            InputMart.Instance.PartStepAttribute.Rows.Add(attr);

            return attr;
        }

        internal static void HandleBackupArrange(AoEquipment aeqp, PMSchedule fs, bool applyBackup,
            FabSemiconLot lot, string calltype, bool isBM)
        {
            var feqp = aeqp as FabAoEquipment;
            var eqp = feqp.Eqp;
            if (eqp.BackupEqps.IsNullOrEmpty())
                return;

            var timeToApply = TimeSpan.Zero;

            if (isBM && applyBackup)
            {
                var backupApplyTime = fs.StartTime.AddHours(Helper.GetConfig(ArgsGroup.Resource_Eqp).backupDelayHour);
                timeToApply = backupApplyTime - aeqp.NowDT;
                if (timeToApply < TimeSpan.Zero)
                    timeToApply = TimeSpan.Zero;

                if (aeqp.NowDT + timeToApply >= fs.StartTime + fs.Duration)
                    return;
            }

            // ProcessInhibit중인 설비는 BackupEqp에서 제외.
            // 제외하더라도, Inhibit 이전에 S Step을 Backup진행한 Lot들은 처리해줘야 되기 때문에, Inhibit 동안 백업이 동작할 수 있음.
            List<FabAoEquipment> backupEqps = new List<FabAoEquipment>();
            var arg = Helper.GetConfig(ArgsGroup.Resource_Eqp).backupEqpOrder;
            if (arg == 0)
            {
                // use all
                backupEqps = eqp.BackupEqps.Select(x => x.SimObject).Where(x => x.Loader.IsBlocked() == false && x.OnProcessInhibit == false).ToList();
            }
            else if (arg == 1)
            {
                // 판단시점에 살아있는 설비 한대만 Backup으로 지정
                // Delay걸렸을 때 나중에 살아있을지 여부는 미리 알 수 없으며, 고려하지 않음.
                var backupEqp = eqp.BackupEqps.Select(x => x.SimObject).Where(x => x.Loader.IsBlocked() == false && x.OnProcessInhibit == false).FirstOrDefault();
                if (backupEqp == null)
                    return;

                backupEqps.Add(backupEqp);
            }

            List<ISimEntity> enqueueList = new List<ISimEntity>();
            if (lot != null)
                enqueueList.Add(lot);
            else
                enqueueList = aeqp.DispatchingAgent.GetDestination(aeqp.EqpID).Queue.ToList(); // 반복문동안 Enqueue 호출로 Add발생하지 않도록

            foreach (FabSemiconLot entity in enqueueList)
            {
                var attr = entity.CurrentAttribute;

                if (attr == null || attr.BackupArranges.IsNullOrEmpty())
                    continue;

                var stackStepInfo = attr.StackInfoDict.SafeGet(entity.CurrentFabStep);

                // StackEqp가 있고 StackType=Y인 Step에서도 Backup설비로 진행 (원본 Arrange는 마찬가지로 하나이어야함)
                foreach (var backupEqp in backupEqps)
                {
                    if (backupEqp.OnProcessInhibit)
                    {
                        if (stackStepInfo != null && stackStepInfo.IsFirstLayer)
                            continue; // ProcessInhibit 걸린 설비는 S Step에서 다른 설비의 Backup으로 동작하지 않도록.
                    }

                    var arr = attr.BackupArranges.Where(x => x.Eqp.SimObject == backupEqp).FirstOrDefault();
                    if (arr == null) // Backup의 Backup을 사용하는 경우.
                    {
                        var orgArr = attr.CurrentArranges.FirstOrDefault();
                        if (orgArr == null)
                            continue; // unexpected

                        // PartStep 기준으로 미리 생성해둔 Arrange에는 없을 수 있기 떄문에 추가로 생성.
                        // Backup의 Backup에 대한 Arrange가 늘어나더라도, BackupArranges는 Backup 등록시가 아니라 참조용으로만 사용하므로, 사용 후 삭제 안해도 무방함.
                        arr = CreateHelper.CreateBackupArrange(attr, backupEqp.Eqp, orgArr.RecipeID, orgArr.ToolingName);
                        attr.BackupArranges.Add(arr);
                    }

                    var args = new Tuple<IHandlingBatch, FabSemiconStep, FabAoEquipment, string>(entity, entity.CurrentFabStep, backupEqp, calltype);

                    if (applyBackup)
                        EventHelper.AddManualEvent(timeToApply, ManualEventTaskType.CallEnqueue, backupEqp, "HandleBackupArrange", args);
                    else
                        EventHelper.AddManualEvent(timeToApply, ManualEventTaskType.CallRemoveFromQueue, backupEqp, "HandleBackupArrange", args);
                }
            }
        }

        internal static void SetLotEvaluatePriority(FabSemiconLot lot, string stepID, string eqpID, int priority)
        {
            var key = Helper.CreateKey(stepID, eqpID);
            if (lot.EvaluatePriority.ContainsKey(key))
                lot.EvaluatePriority[key] = priority;
            else
                lot.EvaluatePriority.Add(key, priority);
        }

        internal static void RemoveActiveStackFromEqp(FabSemiconLot lot)
        {
            if (lot.ActiveStackDict.IsNullOrEmpty())
                return;

            foreach (var activeStack in lot.ActiveStackDict.Values)
            {
                // Scrap/LotSizeMerge된 Lot 때문에 설비 PM 발생 못하는 경우를 방지
                if (activeStack.StackEqp != null)
                    activeStack.StackEqp.SimObject.ActiveStackLotDict.Remove(activeStack.StackGroupID, lot);
            }
        }

        internal static EqpArrange GetCurrentEqpArrange(FabSemiconLot lot, AoEquipment aeqp)
        {
            var eqpArrange = (lot.CurrentPlan as FabPlanInfo).Arrange;
            if (eqpArrange == null) // 선택되기 이전에 선행 판단으로 들어온 경우
            {
                eqpArrange = lot.CurrentArranges.SafeGet(aeqp.EqpID)
                    ?? lot.CurrentAttribute.BackupArranges.Where(x => x.EqpID == aeqp.EqpID).FirstOrDefault();
            }

            return eqpArrange;
        }
    }
}