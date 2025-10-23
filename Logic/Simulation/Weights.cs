using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
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
using System.Diagnostics;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class Weights
    {
        public WeightValue STEP_WAIT_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);
            var feqp = target as FabAoEquipment;

            double rawValue = WeightHelper.GetStepWaitFactorRawValue(lot, feqp, ctx);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue BS_STEP_WAIT_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxStepWaitFactorRawValue(eta.Batch, aeqp, ctx);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue QTIME_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetQtimeFactorRawValue(lot, factor);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue BS_QTIME_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxQtimeFactorRawValue(eta.Batch, factor);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue ATSTEP_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            try
            {
                var aeqp = target as FabAoEquipment;
                if (aeqp.IsBatchType() == false)
                    return new WeightValue(factor, 0);

                var eta = entity as FabLotETA;

                double rawValue = WeightHelper.GetAtStepFactorRawValue(eta);

                return new WeightValue(factor, rawValue);
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, (target as FabAoEquipment).EqpID, EntityHelper.GetLot(entity).LotID);
                return new WeightValue(factor, 0);
            }
        }

        public WeightValue BS_ATSTEP_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxAtStepFactorRawValue(eta.Batch);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue LOT_PRIORITY_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetLotPriorityFactorRawValue(lot);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue LOT_AGE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetLotAgeFactorRawValue(lot, ctx);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue STABLE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);
            var aeqp = target as FabAoEquipment;

            double rawValue = WeightHelper.GetStableFactorRawValue(lot, aeqp);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue BS_STABLE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxStableFactorRawValue(eta.Batch, aeqp);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue SAME_RECIPE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);
            var aeqp = target as FabAoEquipment;

            double rawValue = WeightHelper.GetSameRecipeFactorRawValue(lot, aeqp);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue DIFF_STABLE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetDiffStableFactorRawValue(eta, aeqp);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue BS_DIFF_STABLE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxDiffStableFactorRawValue(eta.Batch, aeqp);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue STEP_ETA_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetStepETAFactorRawValue(eta);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue MAX_STEP_ETA_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxStepETAFactorRawValue(eta.Batch);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue RESOURCE_PREFERENCE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);
            var aeqp = target as FabAoEquipment;

            double rawValue = WeightHelper.GetResourcePreferFactorRawValue(lot, aeqp);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue LAYER_PREFERENCE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetLayerPreferFactorRawValue(lot, factor);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue PART_PREFERENCE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetPartPreferFactorRawValue(lot, factor);
            
            return new WeightValue(factor, rawValue);
        }

        public WeightValue BS_PART_PREFERENCE_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;

            double rawValue = WeightHelper.GetMaxPartPrefFactorRawValue(eta.Batch, factor);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue WAFER_COUNT_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetWaferCountFactorRawValue(lot);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue BS_WAFER_COUNT_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var aeqp = target as FabAoEquipment;
            if (aeqp.IsBatchType() == false)
                return new WeightValue(factor, 0);

            var eta = entity as FabLotETA;
            var batch = eta.Batch;
            double rawValue = batch.UnitQty / Convert.ToDouble(batch.Spec.MaxWafer);
            //double rawValue = WeightHelper.GetMaxWaferCountFactorRawValue(eta.Batch);


            return new WeightValue(factor, rawValue);
        }

        public WeightValue PHOTO_STACK_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            var feqp = target as FabAoEquipment;

            double rawValue = WeightHelper.GetPhotoStackFactorRawValue(lot, feqp, factor);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue CRITICAL_RATIO_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            var feqp = target as FabAoEquipment;

            double rawValue = WeightHelper.GetCriticalRatioFactorRawValue(lot, feqp, now);

            return new WeightValue(factor, rawValue);            
        }

        public WeightValue SWITCH_TIME_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            var feqp = target as FabAoEquipment;

            // factor를 fabWeightFactor로 변환
            var fabWeightFactor = factor as FabWeightFactor;
            var criteriaList = fabWeightFactor.criteriaList as List<double>;

            // criteriaList가 미입력된 값이라면 24h, 아니라면 해당값을 criteria에 넣자
            double criteria = (criteriaList == null) ? Helper.GetDurationHoursWithChar("24h") : criteriaList.FirstOrDefault();

            double rawValue = WeightHelper.GetSwitchTimeFactorRawValue(lot, feqp, criteria);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue LOT_PROGRESS_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetLotProgressFactorRawValue(lot, factor as FabWeightFactor);

            return new WeightValue(factor, rawValue);
        }

        public WeightValue LOT_GROUP_FACTOR(ISimEntity entity, DateTime now, ActiveObject target, WeightFactor factor, IDispatchContext ctx)
        {
            var lot = EntityHelper.GetLot(entity);

            double rawValue = WeightHelper.GetLotGroupFactorRawValue(lot);

            return new WeightValue(factor, rawValue);
        }
    }
}