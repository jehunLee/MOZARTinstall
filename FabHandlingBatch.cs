using FabSimulator.DataModel;
using Mozart.Extensions;
using Mozart.SeePlan.Simulation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FabSimulator
{
    // 기존 함수들에 과도한 변경점을 만들지 않기 위해 LotBatch<SemiconLot, SemiconStep> 대신 FabSemiconLot을 상속받았음.
    internal partial class FabHandlingBatch : FabSemiconLot
    {
        float lotMergeSize = InputMart.Instance.LotMergeSize;

        bool isLaunched = false;
        public bool hasChange = false;

        // this.Contents 는 ReadOnly에 null이라, 새로 생성.
        public List<FabSemiconLot> mergedContents = new List<FabSemiconLot>();

        public FabHandlingBatch(FabSemiconLot sample)
        {
            this.mergedContents.Add(sample);
            this.SetCurrentPlan(CreateHelper.CreateFabPlanInfo(sample, sample.CurrentFabStep));
            this.CurrentAttribute = sample.CurrentAttribute;
            
            var wip = sample.FabWipInfo;

            this.WipInfo = wip;
            this.LineID = wip.LineID;
            this.UnitQtyDouble = wip.UnitQty;
            this.Route = sample.Route;

            this.ReleaseTime = wip.FabInTime;
            this.Product = wip.Product;
            this.IsWipHandle = false;
            this.IsHotLot = EntityHelper.IsHotLot(sample.FabWipInfo.LotPriorityStatus); // TODO: Key로 추가해야 할 수도

            this.CurrentState = EntityState.WAIT;

            InheritActiveStackInfo(sample, this);
        }

        public static bool IsAllowLotSizeMerge(FabSemiconLot lot)
        {
            // HB 끼리도 Merge 가능하도록 함. (원본 Lot으로 add)

            if (lot is FabHandlingBatch && lot.UnitQty == InputMart.Instance.LotMergeSize)
                return false;

            if ((lot.Route as FabSemiconProcess).RouteType == RouteType.REWORK)
                return false;

            if (lot.CurrentRework != null)
                return false;

            if (lot.FabWipInfo.MergeDict.IsNullOrEmpty() == false)
                return false;

            if (lot.CurrentArranges.Values.Any(x => x.Eqp.SimType == Mozart.SeePlan.DataModel.SimEqpType.UnitBatch && x.Eqp.UnitBatchInfo.HasFinitePort))
                return false; // BOH Step에 대해서 처리해주기 위함.

            return true;
        }

        private bool CanAddMore(FabSemiconLot lot)
        {
            return this.UnitQty + lot.UnitQty <= lotMergeSize;
        }

        public bool TryMergeLot(FabSemiconLot lot)
        {
            // Launching 되지 않으면, 결국 Merge 발생하지 않으므로 여기서 확정 처리할 경우 주의.

            if (CanAddMore(lot) == false)
                return false;

            List<FabSemiconLot> list = new List<FabSemiconLot>();

            var exFhb = lot as FabHandlingBatch;
            if (exFhb != null)
            {
                list = exFhb.mergedContents.ToList(); // 기존 HB가 Merge될 경우, 원본 lot을 낱개로 추가.

                ClearHandlingBatch(exFhb);
            }
            else
                list.Add(lot);

            foreach (var item in list)
            {
                if (this.mergedContents.Contains(item))
                    return false;

                this.mergedContents.Add(item);
                this.UnitQty += item.UnitQty;

                this.hasChange = true;
            }

            return true;
        }

        public static void ClearHandlingBatch(FabHandlingBatch fhb)
        {
            // 다른 HB에 Merge되거나, FabOut 도착하여 삭제하는 HB에 대한 처리.
            // FabOut 도착하여 삭제하는 경우에는, 다음Step에 여전히 DIspatchIn 발생하여
            // IsDone에서 추가로 처리함.

            fhb.IsVanishing = true;

            HandleMergeOut(fhb);

            Helper.ClearLotCollectionMemory(fhb);

            fhb.mergedContents.Clear();
            fhb.mergedContents = null;
            
            AoFactory.Current.Out(fhb);
        }

        public void SplitLot(FabSemiconLot lot, HandlingBatchSplitType splitType)
        {
            this.hasChange = true;

            this.mergedContents.Remove(lot);
            this.UnitQty -= lot.UnitQty;

            lot.OnHandlingBatch = false;

            if (splitType == HandlingBatchSplitType.Scrap)
            {
                lot.IsYieldScrapped = true;
            }
            else if (splitType == HandlingBatchSplitType.Rework)
            {
                SplitMoveNext(lot);
            }
            else if (splitType == HandlingBatchSplitType.FabOut || splitType == HandlingBatchSplitType.UnitBatch)
            {
                SplitMoveNext(lot);
            }

            if (IsExtinct())
                this.IsVanishing = true;
        }

        private void SplitMoveNext(FabSemiconLot lot)
        {
            AoFactory.Current.WipManager.In(lot);

            InheritActiveStackInfo(this, lot);

            ArrangeHelper.UpdateActiveStackOnEnd(lot);

            this.UpdateContentPlanInfo(lot);
            lot.MoveNext(AoFactory.Current.NowDT);

            var da = AoFactory.Current.GetDispatchingAgent("-");
            da.Take(lot); // MoveNext() 호출해도, da에 수동으로 담아야 함.
        }

        private void InheritActiveStackInfo(FabSemiconLot from, FabSemiconLot to)
        {
            if (InputMart.Instance.ApplyStacking == false)
                return;

            if (from.ActiveStackDict.IsNullOrEmpty() == false)
            {
                // ToDictionary 구문을 쓰면 Value는 ShallowCopy 되어서 의도와 다르게 동작하게 됨.
                //to.ActiveStackDict = from.ActiveStackDict.ToDictionary(x => x.Key, x => x.Value);

                // 딕셔너리 value값인 StackActiveInfo까지 Deep Copy 해야, S Step에서 분리된 Lot과 HB Lot의 StackEqp가 같아지는 오류가 방지됨.
                to.ActiveStackDict.Clear();
                foreach (var kvp in from.ActiveStackDict)
                {
                    var deepCopy = ArrangeHelper.CreateActiveStack(kvp.Value.StackStepInfo, kvp.Value.StackEqp);
                    to.ActiveStackDict.Add(kvp.Key, deepCopy);
                }
            }
        }

        public bool IsValidCreation()
        {
            // 최대치에서 한참 모자라도 HB 생성하도록 함 (디스패칭 참여재공 수량 차이가 큼)

            bool cond1 = this.mergedContents.Count >= 2;
            //bool cond2 = this.UnitQty + InputMart.Instance.LotSize > lotMergeSize;
            
            // 새로 만들어졌거나, 기존에서 변경된 게 있는 경우: WipLog 적기위한 조건
            bool cond2 = this.isLaunched == false || this.hasChange;

            return cond1 && cond2;
        }

        public bool IsExtinct()
        {
            return this.mergedContents.IsNullOrEmpty();
        }

        public void Launching()
        {
            if (this.IsValidCreation() == false)
                return;

            if (this.isLaunched == false)
            {
                this.LotID = string.Format("HB{0:0000}", InputMart.Instance.HandlingBatchIndex++);
                this.CurrentFabPlan.LotID = this.LotID;
                this.CurrentFabPlan.UnitQty = this.UnitQty;

                this.isLaunched = true;

                HandleMergeIn();
            }

            foreach (var content in this.mergedContents)
            {
                HandleMergeOut(content);
            }

            WriteWipLog();
        }

        private void HandleMergeIn()
        {
            var da = AoFactory.Current.GetDispatchingAgent("-");

            da.Take(this);
            AoFactory.Current.WipManager.In(this);
        }

        private static void HandleMergeOut(FabSemiconLot lot)
        {
            var da = AoFactory.Current.GetDispatchingAgent("-");

            ArrangeHelper.RemoveActiveStackFromEqp(lot);

            da.Remove(lot);

            if (lot.OnHandlingBatch == false) // 이미 HB에 포함되서 Out된 경우는 오류가 발생해서, 조건절 추가.
                AoFactory.Current.WipManager.Out(lot);

            lot.OnHandlingBatch = true;
        }

        public void WriteWipLog()
        {
            OutputHelper.WriteWipLog(LogType.INFO, "HANDLING_BATCH", this, AoFactory.Current.NowDT, this.mergedContents.Select(x => x.LotID).Join(","));

            this.hasChange = false;
        }

        internal void UpdateContentPlanInfo(FabSemiconLot content)
        {
            content.CurrentFabPlan.Start(this.CurrentFabPlan.StartTime, this.CurrentFabPlan.LoadedResource);
            content.CurrentFabPlan.End(this.CurrentFabPlan.EndTime, this.CurrentFabPlan.LoadedResource);
        }
    }
}
