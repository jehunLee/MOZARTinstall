using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using Mozart.SeePlan.Semicon.Simulation;
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
using Mozart.SeePlan.Semicon.DataModel;
using Mozart.RuleFlow;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class APPLY_QT_WORKLOAD_CONTROL_DEF
    {
        public List<Mozart.SeePlan.Semicon.DataModel.QtEqp> INITIALIZE_QT_EQPS0(Mozart.SeePlan.Semicon.Simulation.QtManager qtManager, ref bool handled, List<Mozart.SeePlan.Semicon.DataModel.QtEqp> prevReturnValue)
        {
            List<QtEqp> qtEqps = new List<QtEqp>();
            foreach (var item in InputMart.Instance.FabSemiconEqp.Rows)
            {
                QtEqp eqp = new QtEqp(item.ResID);
                eqp.Eqps.Add(item);

                item.QtEqp = eqp;

                qtEqps.Add(eqp);
            }

            return qtEqps;
        }

        public List<QtCategory> INITIALIZE_QT_CATEGORIES0(QtManager qtManager, QtEqp eqp, ref bool handled, List<QtCategory> prevReturnValue)
        {
            List<QtCategory> ctgs = new List<QtCategory>();

            HashedSet<IQtLoop> totalLoops = new HashedSet<IQtLoop>();
            var arrs = InputMart.Instance.EqpArrangeEqpView.FindRows(eqp.ID);
            foreach (var arr in arrs)
            {
                var loops = QtManager.Current.GetQtLoopsWith(arr.PartID, arr.StepID, false);
                totalLoops.AddRange(loops);
            }

            foreach (var item in totalLoops)
            {
                if (item.ConstType == QtType.MIN)
                    continue;

                if (ctgs.Any(x => x.CategoryHours == item.LimitTime.TotalHours))
                    continue;

                QtCategory ctg = new QtCategory(eqp, item.LimitTime.TotalHours);
                ctgs.Add(ctg);
            }

            return ctgs;
        }

        public List<QtEqp> GET_QT_EQPS0(SemiconLot lot, IQtLoop loop, ref bool handled, List<QtEqp> prevReturnValue)
        {
            var fLot = lot as FabSemiconLot;

            List<QtEqp> qtEqpList = new List<QtEqp>();

            fLot.TargetStepTemp = null;

            var reservationInfo = fLot.ReservationInfos.SafeGet(loop.EndStepID);

            fLot.TargetStepTemp = loop.EndStepID;

            var arrs = EntityHelper.GetTargetStepArranges(fLot, loop.EndStepID);
            foreach (var arr in arrs)
            {
                var eqp = arr.Eqp.QtEqp;
                if (eqp == null)
                    continue;

                if (eqp.Categories.IsNullOrEmpty())
                    continue;

                if (reservationInfo != null)
                {
                    if (reservationInfo.Eqp.EqpID != eqp.ID)
                        continue;
                }

                qtEqpList.Add(eqp);
            }

            return qtEqpList;
        }

        public double GET_PROCESS_UNIT_SIZE0(SemiconLot lot, QtEqp eqp, ref bool handled, double prevReturnValue)
        {
            double constant = lot.UnitQtyDouble;
            var res = eqp.Eqps.FirstOrDefault();
#if true
            var fLot = lot as FabSemiconLot;
            if ((res as FabSemiconEqp).SimObject.IsBatchType())
            {
                var arrs = EntityHelper.GetTargetStepArranges(fLot, fLot.TargetStepTemp);
                var arr = arrs.FirstOrDefault(x => x.Eqp == res);

                double maxWafer = res.SimType == Mozart.SeePlan.DataModel.SimEqpType.LotBatch ? Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMaxBatchSizeLotBatch 
                    : Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMaxBatchSizeBatchInline;
                if (arr != null && arr.BatchSpec != null)
                    maxWafer = arr.BatchSpec.MaxWafer;

                constant = Math.Round(lot.UnitQtyDouble / maxWafer, 2);
            }
#else
            if (res.SimType == Mozart.SeePlan.DataModel.SimEqpType.BatchInline || res.SimType == Mozart.SeePlan.DataModel.SimEqpType.LotBatch)
            {
                var maxWafer = 50;
                constant = Math.Round(lot.UnitQtyDouble / maxWafer, 2);
            } 
#endif

            return constant;
        }

        public TimeSpan GET_TACT_TIME0(SemiconLot lot, QtEqp eqp, IQtLoop loop, ref bool handled, TimeSpan prevReturnValue)
        {
            string tagetStepId = loop.EndStepID;

            var procTime = ResourceHelper.GetProcessTime((eqp.Eqps.First() as FabSemiconEqp).SimObject, lot, tagetStepId);
            return procTime.TactTime;
        }

        public List<QtCategory> GET_QT_CATEGORIES0(SemiconLot lot, QtEqp eqp, IQtLoop loop, ref bool handled, List<QtCategory> prevReturnValue)
        {
            return eqp.Categories.Values.ToList();
        }


        public void ON_UPDATE_WORKLOAD0(SemiconLot lot, QtEqp eqp, QtCategory ctg, double workload, string type, ref bool handled)
        {
            OutputHelper.WriteEqpWorkload(lot as FabSemiconLot, eqp.Eqps.First() as FabSemiconEqp, ctg, workload, type);
        }

        public void ASSIGN_QT_WORKLOAD0(SemiconLot lot, QtEqp eqp, IQtLoop loop, QtCategory ctg, double lotWorkload, string eventType, ref bool handled)
        {
            Time delay;
            var gap = loop.LimitTime.TotalHours - ctg.CategoryHours;
            if (gap < 0)
                delay = Time.Zero;
            else
                delay = Time.FromHours(gap);

            if (delay > Time.Zero)
            {
                var arg = new Tuple<QtEqp, double, SemiconLot>(eqp, ctg.CategoryHours, lot);
                EventHelper.AddManualEvent(delay, ManualEventTaskType.OnAssignQtWorkload, null, "ASSIGN_QT_WORKLOAD0", arg);
                //AoFactory.Current.AddTimeout(delay, QtManager.Current.OnAssignQtWorkload, arg, int.MinValue);
            }
            else
            {
                QtManager.Current.AssignQtWorkload(lot, eqp, ctg, lotWorkload, eventType);
            }
        }
    }
}