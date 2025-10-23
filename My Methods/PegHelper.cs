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
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Pegging;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class PegHelper
    {
        public static bool CheckNoDemandWip(FabWipInfo info, List<string> demandList)
        {
            string productID = info.Product.StdProductID;

            if (string.IsNullOrEmpty(productID))
                return true;

            if (demandList.Contains(productID) == false)
                return true;

            return false;
        }

        internal static double FindYield(string stdProductID, Step currentStep)
        {
            var currentStdStep = currentStep as FabSemiconStep;
            var nextStdStep = currentStep.GetDefaultNextStep() as FabSemiconStep;

            if (currentStep == null)
                return 1;

            if (nextStdStep == null) // LastStep
                return Helper.GetValidRate(currentStdStep.CumulativeYield);

            double stepYield = currentStdStep.CumulativeYield / nextStdStep.CumulativeYield;

            return Helper.GetValidRate(stepYield);
        }

        internal static FabSemiconPegPart CreatePegPart(FabSemiconMoMaster mm, StdProduct stdProduct)
        {
            FabSemiconPegPart pp = new FabSemiconPegPart(mm, stdProduct);
            pp.Product = stdProduct;

            return pp;
        }

        internal static FabSemiconPegTarget CreatePegTarget(FabSemiconPegPart pp, FabSemiconMoPlan mo)
        {
            FabSemiconPegTarget pt = new FabSemiconPegTarget(pp, mo);

            return pt;
        }

        internal static FabSemiconMoMaster FindMoMaster(IProduct product)
        {
            return InputMart.Instance.FabSemiconMoMasterView.FindRows(product).FirstOrDefault();
        }

        internal static FabSemiconMoMaster CreateMoMaster(IProduct product)
        {
            FabSemiconMoMaster mm = new FabSemiconMoMaster();
            mm.Product = product;

            return mm;
        }

        internal static FabPlanWip CreatePlanWip(FabWipInfo wipInfo, SemiconStep step)
        {
            FabPlanWip planWip = new FabPlanWip();

            // Non-Simulation Step에서 RUN으로 올라올 가능성도 생각해서, InitialEqp 존재 여부는 체크하지 않음.
            planWip.IsRunWip = wipInfo.WipState == "RUN" || wipInfo.WipState == "SETUP" || wipInfo.WipState == "STAGED";

            planWip.AvailableTime = wipInfo.WipStateTime;  // 사용처 없음.
            planWip.MapStep = step;
            planWip.Qty = wipInfo.UnitQty;
            planWip.State = wipInfo.WipState;
            planWip.Wip = wipInfo;
            planWip.StdProduct = (wipInfo.Product as FabProduct).StdProduct;

            return planWip;
        }

        internal static FabSemiconMoPlan CreateMoPlan(FabSemiconMoMaster mm, DEMAND demand)
        {
            FabSemiconMoPlan mo = demand.ToFabSemiconMoPlan();

            // null이면 Library에 정의된 Default 값으로 세팅 되어 UI와는 틀어지게 됨. $"{LineID}/{ProductID}/{base.DueDate}";
            mo.DemandID = Helper.CreateKey2(demand.PRODUCT_ID, demand.DUE_DATE.ToString("yyyyMMddHHmmss"), demand.PRIORITY.ToString());
            InputMart.Instance.PegTargetInfo.Add(mo.DemandID, demand.DUE_DATE);
            mo.WeekNo = demand.WW_SEQUENCE.ToString();
            mo.MoMaster = mm;
            mo.Priority = demand.PRIORITY;
            mo.ForwardPegRemainQty = mo.Qty;

            return mo;
        }

        internal static DateTime GetTargetDate(string key)
        {
            if (string.IsNullOrEmpty(key) == false)
            {
                DateTime targetDate;
                if (InputMart.Instance.PegTargetInfo.TryGetValue(key, out targetDate))
                    return targetDate;
            }

            return DateTime.MaxValue;
        }

        internal static void PrepareTarget(PegPart pegPart)
        {
            MergedPegPart mergedPegPart = pegPart as MergedPegPart;

            foreach (FabSemiconMoMaster mm in InputMart.Instance.FabSemiconMoMaster.Rows)
            {
                FabSemiconPegPart pp = CreatePegPart(mm, mm.Product as StdProduct);

                var stock = InputMart.Instance.STOCKView.FindRows(mm.Product.ProductID).FirstOrDefault();

                foreach (FabSemiconMoPlan mo in mm.MoPlanList.OrderBy(x => x.DueDate))
                {
                    if (stock != null && stock.WAFER_QTY > 0)
                    {
                        var subtract = Math.Min(mo.Qty, stock.WAFER_QTY);
                        mo.Qty -= subtract;
                        stock.WAFER_QTY -= (int)subtract;
                    }

                    if (mo.Qty <= 0)
                        continue;

                    if (mergedPegPart != null)
                    {
                        FabSemiconPegTarget target = CreatePegTarget(pp, mo);

                        // Forward Only 에서 수행하면 오류 발생함.
                        pp.AddPegTarget(target);
                    }
                }

                if (mergedPegPart != null)
                {
                    mergedPegPart.Merge(pp);
                }
            }
        }

        internal static void DoForwardPeggingWithDemand(FabSemiconLot lot)
        {
            FabSemiconMoMaster mm = FindMoMaster(lot.FabProduct.StdProduct);
            if (mm == null)
                return;

            var remainQty = lot.UnitQtyDouble;
            foreach (FabSemiconMoPlan mo in mm.MoPlanList.OrderBy(x => x.DueDate))
            {
                if (mo.ForwardPegRemainQty <= 0)
                    continue;

                var pegQty = Math.Min(remainQty, mo.ForwardPegRemainQty);
                remainQty -= pegQty;
                mo.ForwardPegRemainQty -= pegQty;

                FabForwardPegInfo pegInfo = new FabForwardPegInfo(mo, pegQty);
                lot.ForwardPegInfoList.Add(pegInfo);

                if (remainQty <= 0)
                    break;
            }
        }
    }
}