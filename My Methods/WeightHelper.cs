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
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
using System.Text;
using System.Diagnostics;
using Mozart.Simulation.Engine;
using static Mozart.SeePlan.Simulation.WipTags;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class WeightHelper
    {
        internal static string GetBatchingEvaluationLog(LotETA lot, WeightPreset wp, out double totalValue)
        {
            //wook
            totalValue = 0;

            if (wp == null || lot == null)
                return null;

            var str = new StringBuilder();

            bool first = true;

            foreach (var factor in wp.FactorList)
            {
                var value = lot.WeightInfo.GetValue(factor);

                if (first == false)
                    str.Append('/');

                str.Append(value.ToString());

                totalValue += value;

                first = false;
            }

            return str.ToString();
        }

        internal static double GetStepWaitFactorRawValue(FabSemiconLot lot, FabAoEquipment feqp, IDispatchContext ctx)
        {
            if (ctx == null)
                return 0;

            var thisStepWaitHrs = WeightHelper.GetStepWaitHours(lot, feqp);

            double stepWaitDenominatorHrs = ctx.Get<double>("STEP_WAIT", 0d);

            if (stepWaitDenominatorHrs == 0)
                return 0;

            // 1보다 크면 1.0
            double rawValue = Math.Min(1.0, Math.Round(thisStepWaitHrs / stepWaitDenominatorHrs, 2));

            return rawValue;
        }

        internal static double GetMaxPartPrefFactorRawValue(LotBatch batch, WeightFactor factor)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetPartPreferFactorRawValue(eta.Lot as FabSemiconLot, factor);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetPartPreferFactorRawValue(FabSemiconLot lot, WeightFactor factor)
        {
            var mfgProductID = lot.FabProduct.PartID;
            var fabWeightFactor = factor as FabWeightFactor;
            var criteriaList = fabWeightFactor.criteriaList as List<string>;

            // criteriaList가 null값인지 체크한 이후, mfgProductID가 criteriaList안에 들어있는지 확인            
            if (criteriaList != null && criteriaList.Contains(mfgProductID))
                return 1d;
            else
                return 0d;

            //    double rawValue = 0;

            //    var preferList = factor.Criteria.Select(x=> x as string).ToList();

            //    if (preferList.Any(x => x == lot.FabProduct.MfgProductID))
            //        rawValue = 1;

            //    return rawValue;
        }

        internal static double GetLayerPreferFactorRawValue(FabSemiconLot lot, WeightFactor factor)
        {
            var layerID = (lot.CurrentStep as FabSemiconStep).LayerID;

            var fabWeightFactor = factor as FabWeightFactor;
            var criteriaDict = fabWeightFactor.criteriaDict as Dictionary<double, double>;

            // layerID = value의 형태로 criteriaDict 안에 내용이 있다면 해당 Data를 사용하자.
            if (criteriaDict != null && criteriaDict.TryGetValue(Convert.ToDouble(layerID), out var weightOfLayerID))
                return Convert.ToDouble(weightOfLayerID);
            else
                return 0d;
        }

        internal static double GetMaxStepWaitFactorRawValue(LotBatch batch, FabAoEquipment feqp, IDispatchContext ctx)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetStepWaitFactorRawValue(eta.Lot as FabSemiconLot, feqp, ctx);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetMaxQtimeFactorRawValue(LotBatch batch, WeightFactor factor)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetQtimeFactorRawValue(eta.Lot as FabSemiconLot, factor);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetQtimeFactorRawValue(FabSemiconLot lot, WeightFactor factor)
        {
            if (lot.MaxQtActivations.IsNullOrEmpty())
                return 0;

            double rawValue = 0;

            var fabWeightFactor = factor as FabWeightFactor;
            var criteriaList = fabWeightFactor.criteriaList as List<double>;
            
            // Weight_Presets Criteria 미입력시 기본 값은 0.9, 5.0, 0.7, 0.0
            double w = criteriaList[0];
            double x = criteriaList[1];
            double y = criteriaList[2];
            double z = criteriaList[3];

            foreach (QtActivation qtActivation in lot.MaxQtActivations.Values)
            {
                var remainTimeValue = GetRemainTimeValue(qtActivation, w, z);
                var orderingValue = GetOrderingValue(qtActivation, x, y);

                rawValue = Math.Max(rawValue, Math.Max(remainTimeValue, orderingValue));
            }

            return rawValue;
        }

        private static double GetRemainTimeValue(QtActivation item, double w, double z)
        {
            var nowDt = AoFactory.Current.NowDT;

            var loop = item.Loop as FabQtLoop;
            var remainTime = item.BreachTime - nowDt;
            var remainTimetoWarning = loop.WarningTime - (nowDt - item.StartTime);

            // ## 아래 두 경우는, 잔여시간의 비율을 통한 계산을 진행하지 않고 0점 처리.
            if (remainTime.TotalHours >= 12)
            {
                // 0: 잔여시간이 12 시간 이상인 경우
                return 0;
            }
            else if (remainTimetoWarning.TotalHours >= z)
            {
                // 0: 경고시간까지 z 시간 이상 남은 경우
                return 0;
            }

            // ## 0점 조건이 아닌 경우만 아래 계산 진행.
            if (loop.AttrSet?.Attributes.SafeGet("qt_priority") == Enum.GetName(typeof(QtimePriority), QtimePriority.CRITICAL))
            {
                if (remainTime.TotalSeconds < 0)
                {
                    // 1.0: Criticlal Qt Loop이 이미 시간 초과된 경우
                    return 1.0;
                }
                else
                {
                    // w ~ 1.0: Criticlal Qt Loop이 안전한 상태  = w + ((LimitTime - 남은 분) / LimitTime) * (1 - w)
                    return w + ((loop.LimitTime.TotalMinutes - remainTime.TotalMinutes) / loop.LimitTime.TotalMinutes) * (1 - w);
                }
            }
            else
            {
                if (remainTime.TotalSeconds < 0)
                {
                    // 0.9: Criticlal Qt Loop이 아니고, 이미 시간 초과된 경우
                    return 0.9;
                }
                else
                {
                    // 0.2 ~ 0.8: Criticlal Qt Loop이 아니고, 안전한 상태  = 0.2 + ((LimitTime - 남은 분)/LimitTime) * 0.6
                    return 0.2 + ((loop.LimitTime.TotalMinutes - remainTime.TotalMinutes) / loop.LimitTime.TotalMinutes) * 0.6;
                }
            }
        }

        private static double GetOrderingValue(QtActivation item, double x, double y)
        {
            var loop = item.Loop as FabQtLoop;
            if (loop.ControlType != QtControlType.Ordering)
                return 0;

            var lot = item.Lot as FabSemiconLot;

            var reservation = lot.ReservationInfos.SafeGet(loop.EndStepID);
            if (reservation == null)
            {
                // 0: 예약되지 않음
                return 0;
            }
            else
            {
                var batch = lot.ReservationInfos.SafeGet(loop.EndStepID).Batch;
                if (batch.IsNullOrEmpty())
                    return 0; // unexpected

                var eta = batch.Contents.FirstOrDefault(x => (x as FabLotETA).Lot == lot) as FabLotETA;
                if (eta == null)
                    return 0; // unexpected

                if (eta.IsOrderingStepStarted)
                {
                    // y: 시작되었으며, Ordering Loop의 제약 시간이 x이하
                    // 0.85: 시작되었으며, Ordering Loop의 제약 시간이 x초과

                    if (loop.LimitTime.TotalHours <= x)
                        return y; 
                    else
                        return 0.85;
                }
                else
                {
                    // 0.2: 예약되었지만 Qt 시작 공정에서 시작되지 않음
                    return 0.2;
                }
            }
        }

        //internal static double GetAtStepFactorRawValue(FabLotETA eta)
        //{
        //    double rawValue = 0;

        //    if (eta.Lot.CurrentStep == eta.TargetStep)
        //        rawValue = 1;
        //    else
        //        rawValue = 0;

        //    return rawValue;
        //}

        //wook
        internal static double GetAtStepFactorRawValue(FabLotETA eta) => (eta.Lot.CurrentStep == eta.TargetStep) ? 1 : 0;

        internal static double GetMaxAtStepFactorRawValue(LotBatch batch)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetAtStepFactorRawValue(eta);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetSameRecipeFactorRawValue(FabSemiconLot lot, FabAoEquipment aeqp)
        {
            double rawValue = 0;

            if (aeqp.LastPlan == null)
                return 0;

            var arr = lot.CurrentArranges.SafeGet(aeqp.EqpID);
            if (arr == null)
                return 0;

            if (aeqp.LastPlan.RecipeID == arr.RecipeID)
                rawValue = 1;

            return rawValue;
        }

        internal static double GetLotPriorityFactorRawValue(FabSemiconLot lot)
        {
            if (lot.FabWipInfo.LotPriorityValue < 0)
                return 1;

            return 1 / (double)(lot.FabWipInfo.LotPriorityValue + 1);
        }

        internal static double GetLotAgeFactorRawValue(FabSemiconLot lot, IDispatchContext ctx)
        {
            if (ctx == null)
                return 0;

            double thisLotAgeHrs = WeightHelper.GetLotAgeHours(lot);

            double lotAgeDenominatorHrs = ctx.Get<double>("LOT_AGE", 0d);

            if (lotAgeDenominatorHrs == 0)
                return 0;

            // 1보다 크면 1.0
            double rawValue = Math.Min(1.0, Math.Round(thisLotAgeHrs / lotAgeDenominatorHrs, 2));

            return rawValue;
        }

        internal static double GetStableFactorRawValue(FabSemiconLot lot, FabAoEquipment aeqp)
        {
            if (lot.CurrentStepID != lot.WipInfo.WipStepID)
                return 0;

            double rawValue = 0;

            if (lot.FabWipInfo.StableEqp == aeqp.Target as FabSemiconEqp)
                rawValue = 1;

            return rawValue;
        }

        internal static double GetMaxStableFactorRawValue(LotBatch batch, FabAoEquipment aeqp)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetStableFactorRawValue(eta.Lot as FabSemiconLot, aeqp);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetDiffStableFactorRawValue(LotETA eta, FabAoEquipment aeqp)
        {
            // TODO: 재설계 필요

            double rawValue = 0;
            //var lot = eta.Lot as FabSemiconLot;

            //if (lot.FabWipInfo.StableDiffStepEqp == null)
            //    return 0;

            //if (lot.FabWipInfo.StableDiffStepEqp.Item1 == eta.TargetStep.StepID)
            //{
            //    if (lot.FabWipInfo.StableDiffStepEqp.Item2 == aeqp.Target as FabSemiconEqp)
            //        rawValue = 1;
            //}

            return rawValue;
        }

        internal static double GetMaxDiffStableFactorRawValue(LotBatch batch, FabAoEquipment aeqp)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetDiffStableFactorRawValue(eta, aeqp);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetStepETAFactorRawValue(FabLotETA eta)
        {
            if (eta.Loop == null)
                return 0;

            double rawValue = 0;

            if (eta.Lot.CurrentStepID == eta.Loop.StartStepID)
            {
                if (eta.Lot.CurrentState == EntityState.WAIT)
                    rawValue = 0.3;
                else if (eta.Lot.CurrentState == EntityState.RUN)
                    rawValue = 0.6;
            }
            else
            {
                var startStep = eta.Lot.Process.FindStep(eta.Loop.StartStepID);
                var endStep = eta.Lot.Process.FindStep(eta.Loop.EndStepID);

                int lotDistanceToDest = endStep.Sequence - eta.Lot.CurrentStep.Sequence;
                int maxLotDistanceToDest = endStep.Sequence - startStep.Sequence;

                if (lotDistanceToDest > maxLotDistanceToDest)
                    return 0;

                rawValue = 0.6 + (1 - (lotDistanceToDest / (maxLotDistanceToDest + 1)));
            }

            return rawValue;
        }

        internal static double GetMaxStepETAFactorRawValue(LotBatch batch)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetStepETAFactorRawValue(eta);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetResourcePreferFactorRawValue(FabSemiconLot lot, FabAoEquipment feqp)
        {
            var arr = lot.CurrentArranges.SafeGet(feqp.EqpID);
            if (arr == null)
                arr = lot.CurrentAttribute.BackupArranges.Where(x => x.EqpID == feqp.EqpID).FirstOrDefault();

            if (arr != null && arr.IsBackup)
                return 0;

            return 1;
        }

        internal static double GetWaferCountFactorRawValue(FabSemiconLot lot) => lot.UnitQtyDouble / InputMart.Instance.LotMergeSize;

        internal static double GetMaxWaferCountFactorRawValue(LotBatch batch)
        {
            if (batch == null)
                return 0;

            double maxRawValue = 0;

            foreach (FabLotETA eta in batch.Contents)
            {
                double rawValue = GetWaferCountFactorRawValue(eta.Lot as FabSemiconLot);

                maxRawValue = Math.Max(maxRawValue, rawValue);
            }

            return maxRawValue;
        }

        internal static double GetPhotoStackFactorRawValue(FabSemiconLot lot, FabAoEquipment feqp, WeightFactor factor)
        {
            if (lot.CurrentActiveStackInfo != null)
            {
                // lot이 Stacking Step에 있는 경우

                if (lot.CurrentActiveStackInfo.StackStepInfo.StackType == StackType.D)
                {
                    bool hasAnotherInhibitArrange = HasAnotherInhibitArrange(lot, feqp);

                    // -1.0 : (자신이 아닌) Inhibit 설비가 진행할 수 있는 lot
                    if (hasAnotherInhibitArrange)
                        return -1.0;
                }

                // 0.5 : Chain내 다른 호기가 stacking EQP인 lot (de-stacking)
                if (lot.CurrentActiveStackInfo.StackEqp != feqp.Eqp)
                    return 0.5;

                // 1.0 : Chain내 자신이 Stacking EQP인 lot
                return 1.0;
            }
            else if (lot.ActiveStackDict.IsNullOrEmpty() == false)
            {
                // lot이 Non-Stacking Step에 있지만, Chain내 존재하는 경우.
                // 대체로 Non-Photo 설비겠지만, Photo 설비일 수도 있음.

                // 1.0 : Inhibit 걸린 stacking EQP의 stacking lot
                if (lot.ActiveStackDict.Values.Where(x => x.StackEqp != null).Any(x => x.StackEqp.SimObject.OnProcessInhibit))
                    return 1.0;

                return 0.0;
            }
            else
            {
                // lot이 Chain내 존재하지 않는 경우
                return 0.0;
            }

            static bool HasAnotherInhibitArrange(FabSemiconLot lot, FabAoEquipment thisEqp)
            {
                foreach (var arr in lot.CurrentArranges.Values)
                {
                    var feqp = arr.Eqp.SimObject;
                    if (feqp == thisEqp)
                        continue;

                    if (feqp.OnProcessInhibit)
                        return true;
                }

                return false;
            }
        }

        internal static double GetSwitchTimeFactorRawValue(FabSemiconLot lot, FabAoEquipment feqp, double criteria)
        {
            try
            {
                var aeqp = feqp as AoEquipment;
                var setupInfos = feqp.Eqp.SetupInfos;

                if (setupInfos.IsNullOrEmpty())
                    return 1;

                if (aeqp.LastPlan == null)
                    return 1;

                var arr = lot.CurrentArranges.SafeGet(aeqp.EqpID);
                if (arr == null)
                    return 1;

                // 둘중 하나라도 정보가 없으면, 전후가 같은지 다른지 알 수 없음.
                var from = (aeqp.LastPlan as FabPlanInfo).Arrange;
                var to = arr;

                Time switchTime = ResourceHelper.GetSetupTime(aeqp, from, to);
                if (switchTime == Time.Zero)
                    return 1;

                Time maxSwitchTime = Time.FromHours(criteria);
                double value = 1 - switchTime.TotalSeconds / maxSwitchTime.TotalSeconds;

                return Math.Max(0, value);
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, feqp.EqpID, lot.LotID);
                return 0;
            }
        }
        internal static double GetCriticalRatioFactorRawValue(FabSemiconLot lot, FabAoEquipment feqp, DateTime now)
        {
            try
            {
                var step = lot.CurrentStep as FabSemiconStep;
                if (step == null)
                    return 0; // unexpected

                TimeSpan fabOutTime = TimeSpan.FromDays(step.PotDays); // lot.IsSuperHotLot ? stdStep.HotPOT : stdStep.NormalPOT;
                TimeSpan leftTimeByDueDate = lot.FabWipInfo.DueDate - now;

                if (fabOutTime.TotalSeconds == 0)
                    return 0;

                double value = 0;
                if (fabOutTime >= leftTimeByDueDate)
                    value = 1 - leftTimeByDueDate.TotalSeconds / fabOutTime.TotalSeconds;

                return Math.Max(0, value);
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, feqp.EqpID, lot.LotID);
                return 0;
            }
        }

        internal static double GetLotAgeHours(FabSemiconLot lot)
        {
            return (AoFactory.Current.NowDT - lot.ReleaseTime).TotalHours;
        }

        internal static double GetStepWaitHours(FabSemiconLot lot, FabAoEquipment feqp)
        {
            var arrivalTime = lot.DispatchInTime;
            if (lot.CurrentFabPlan.ArrivalTimeDict != null)
            {
                if (lot.CurrentFabPlan.ArrivalTimeDict.ContainsKey(feqp.Eqp.ResID))
                    arrivalTime = lot.CurrentFabPlan.ArrivalTimeDict.SafeGet(feqp.Eqp.ResID);
            }

            return (AoFactory.Current.NowDT - arrivalTime).TotalHours;
        }

        internal static double GetLotProgressFactorRawValue(FabSemiconLot lot, FabWeightFactor factor)
        {
            var criteriaDict = factor.criteriaDict as Dictionary<double, double>;
            if (criteriaDict.IsNullOrEmpty())
                return 0;

            var actualSpent = (AoFactory.Current.NowDT - lot.FabWipInfo.FabInTime).TotalDays;
            var expectedSpent = InputMart.Instance.StandardPotDays - lot.CurrentFabStep.StandardizedPotDays;

            var progressRate = expectedSpent / actualSpent;

            if (progressRate > 1)
                return 0;

            double value = 0;
            // key값이 Ascending Order로 입력될 것으로 전제됨.
            foreach (var kvp in criteriaDict)
            {
                if (progressRate <= kvp.Key)
                    value = kvp.Value;
            }

            return value;
        }

        internal static double GetLotGroupFactorRawValue(FabSemiconLot lot)
        {
            var bomInfo = lot.CurrentBOM;
            if (bomInfo == null || bomInfo.ProcessedSteps == null) // bom_parent_lot_id가 있는 경우에만 ProcessedSteps를 생성함.
                return 0;

            return bomInfo.ProcessedSteps.Contains(lot.CurrentStepID) ? 1 : 0;
        }

        internal static void SetDispatchContext(IDispatchContext dc, AoEquipment aeqp, WeightPreset preset = null)
        {
            if (preset == null)
                preset = aeqp.Preset;

            if (preset == null)
                return;

            var lotAgeFactor = aeqp.Preset.FactorList.Where(x => x.Name == "LOT_AGE_FACTOR").FirstOrDefault();
            var stepWaitFactor = aeqp.Preset.FactorList.Where(x => x.Name == "STEP_WAIT_FACTOR").FirstOrDefault();

            if (lotAgeFactor == null && stepWaitFactor == null)
                return;

            var lotWeightFactor = lotAgeFactor as FabWeightFactor;
            var stepWeightFactor = stepWaitFactor as FabWeightFactor;

            var feqp = aeqp as FabAoEquipment;
            double lotAgeDenominatorHrs = 0;
            double stepWaitDenominatorHrs = 0;
            DateTime now = aeqp.NowDT;

            if (lotWeightFactor != null)
            {
                // 기존 코드에서 Helper.GetDurationHoursWithChar를 쓰는 부분을 제거하고 List를 호출하도록 변경                    
                var criteriaList = lotWeightFactor.criteriaList as List<double>;
                if (criteriaList.IsNullOrEmpty() == false)
                    lotAgeDenominatorHrs = criteriaList.FirstOrDefault();
            }

            if (stepWeightFactor != null)
            {
                var criteriaList = stepWeightFactor.criteriaList as List<double>;
                if (criteriaList.IsNullOrEmpty() == false)
                    stepWaitDenominatorHrs = criteriaList.FirstOrDefault();
            }

            bool isNeedMaxLotAgeCalc = lotAgeDenominatorHrs <= 0;
            bool isNeedMaxStepWaitCalc = stepWaitDenominatorHrs <= 0;

            if (isNeedMaxLotAgeCalc)
            {
                var ctTime = Helper.GetConfig(ArgsGroup.Lot_InPlan).targetCT;
                lotAgeDenominatorHrs = Convert.ToDouble(ctTime) * 24;
            }
            if (isNeedMaxStepWaitCalc)
            {
                stepWaitDenominatorHrs = Helper.GetDurationHoursWithChar("7d");
            }

#if false // 상대평가는 더이상 사용하지 않기로 함.
                if (isNeedMaxLotAgeCalc || isNeedMaxStepWaitCalc)
                {
                    // Factor 둘 중 하나라도 Max를 계산해야 하는 경우만 반복문 진입
                    foreach (IHandlingBatch hb in wips)
                    {
                        FabSemiconLot lot = EntityHelper.GetLot(hb);

                        if (isNeedMaxLotAgeCalc)
                        {
                            var thisLotAgeHrs = WeightHelper.GetLotAgeHours(lot);
                            lotAgeDenominatorHrs = Math.Max(thisLotAgeHrs, lotAgeDenominatorHrs);
                        }

                        if (isNeedMaxStepWaitCalc)
                        {
                            var thisLotStepWaitHrs = WeightHelper.GetStepWaitHours(lot, feqp);
                            stepWaitDenominatorHrs = Math.Max(thisLotStepWaitHrs, stepWaitDenominatorHrs);
                        }
                    }
                } 
#endif

            dc.Set("LOT_AGE", lotAgeDenominatorHrs);
            dc.Set("STEP_WAIT", stepWaitDenominatorHrs);
        }
    }
}