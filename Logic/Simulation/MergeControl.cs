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
using Mozart.SeePlan.Simulation;

namespace FabSimulator.Logic.Simulation
{
    [FeatureBind()]
    public partial class MergeControl
    {
        public object GET_MERGEABLE_KEY0(Mozart.Simulation.Engine.ISimEntity entity, ref bool handled, object prevReturnValue)
        {
            var lot = EntityHelper.GetLot(entity);

            if (lot.IsWaitForKitting)
            {
                return lot.CurrentBOM;
            }

            return lot.FabWipInfo.MergeDict.SafeGet(lot.CurrentStepID);
        }

        public List<ISimEntity> MERGE0(object key, List<ISimEntity> entitys, ref bool handled, List<ISimEntity> prevReturnValue)
        {
            var info = key as MergeInfo;
            if (info == null)
                return prevReturnValue;

            handled = true;

            var requireCount = info.Childs.Count + 1;

            if (entitys.Count != requireCount)
                return null;

            info.Parent.MergeDict.Remove(info.MergeStep.StepID);
            foreach (var child in info.Childs)
            {
                info.Parent.UnitQty += child.UnitQty;
                info.Parent.Lot.UnitQty += (int)child.UnitQty;
                child.MergeDict.Remove(info.MergeStep.StepID);
                OutputHelper.WriteWipLog(LogType.INFO, "MERGE", child.Lot, AoFactory.Current.NowDT, string.Format("Merged to Parent {0}", info.Parent.LotID));
                child.IsMergeOut = true;
                AoFactory.Current.Out(child.Lot);
            }

            var result = new List<ISimEntity>();
            result.Add(info.Parent.Lot);

            return result;
        }

        public List<ISimEntity> MERGE_BOM(object key, List<ISimEntity> entitys, ref bool handled, List<ISimEntity> prevReturnValue)
        {
            var info = key as BOMInfo;
            if (info == null)
                return prevReturnValue;

            handled = true;

            var results = new List<ISimEntity>();
            MultiDictionary<(FabProduct, float), FabSemiconLot> partsCandidate = GetPartsCandidate(info, entitys, out int unitQty);
#if true
            if (partsCandidate != null)
            {
                // 필요한 FromParts가 모두 충족된 것이 확인되면, Parent 생성
                var parentLotID = info.MergeableKey != "*" ? info.MergeableKey : EntityHelper.GetNewLotID(info.ToPart);
                FabSemiconLot parentLot = CreateHelper.CreateInstancingLot(parentLotID, info.ToPart, info.ToPart.MainRoute, unitQty, info.MergeStep,
                    AoFactory.Current.NowDT, DateTime.MaxValue);

                var bomParentStr = partsCandidate.Values.First().FabWipInfo.WipParamDict.SafeGet("bom_parent_lot_id");
                EntityHelper.SetCurrentBOMInfo(parentLot, bomParentStr);

                parentLot.SetCurrentPlan(CreateHelper.CreateFabPlanInfo(parentLot, info.MergeStep, false));
                results.Add(parentLot);

                entitys = DoKittingAndGetRemainEntitys(partsCandidate, parentLot);

                // 한번에 최대수량으로 Merge하도록 변경해서 반복문은 필요 없음.
                // 하지만 모든 도착 재공을 기다렸다가 Merge시도하는 것은 아니기 때문에,
                // 동일한 Simulation Time에 가용한 최대치가 한번에 만들어진다고 보장할 수 없음 (즉, 200매가 아니라 100매 + 100매로 따로 만들어 질 수 있음)
                // 추후, Config.lotSize를 반영하는 니즈도 있을 것으로 생각되어
                // 분할생성은 불가피할 것으로 보고, logSeq를 추가 개선하여 WipLog의 키중복이 발생하지 않도록 조치함.
            }
#else
            while (partsCandidate.IsNullOrEmpty() == false)
            {
                // 필요한 FromParts가 모두 충족된 것이 확인되면, Parent 생성
                var parentLotID = EntityHelper.GetNewLotID(info.ToPart);
                FabSemiconLot parentLot = CreateHelper.CreateInstancingLot(parentLotID, info.ToPart, info.ToPart.MainRoute, 1, info.MergeStep,
                    AoFactory.Current.NowDT, DateTime.MaxValue);

                parentLot.SetCurrentPlan(CreateHelper.CreateFabPlanInfo(parentLot, info.MergeStep, false));
                results.Add(parentLot);

                entitys = DoKittingAndGetRemainEntitys(partsCandidate, parentLot, logSeq++);

                // 잔류 시킬 때 MergeableKey의 entitys에 남아는 있는데,
                // 같은 Key로 새로운 Lot이 도착하지 않으면 Merge가 재호출 되지 않는 문제가 있어서
                // 이미 존재하는 entitys로 복수번의 Merge가 발생할 수 있는 경우를 위해 반복문으로 처리.
                partsCandidate = GetPartsCandidate(info, entitys);
            } 
#endif

            if (results.IsNullOrEmpty())
            {
                // null을 return해야 merge 대기중인 entitys가 유지됨. -> DisposeEntities FEAction 구현하기 나름.
                return null;
            }

            return results;

            static MultiDictionary<(FabProduct, float), FabSemiconLot> GetPartsCandidate(BOMInfo info, List<ISimEntity> entitys, out int unitQty)
            {
                var waitingParts = entitys.Select(x => x as FabSemiconLot).GroupBy(x => x.FabProduct);

                unitQty = 0;
                int maxPair = int.MaxValue;
                var partsCandidate = new MultiDictionary<(FabProduct, float), FabSemiconLot>();
                foreach (var required in info.FromParts)
                {
                    var part = waitingParts.Where(x => x.Key == required.Key).FirstOrDefault();
                    if (part == null)
                        return null;

                    var partSum = part.Select(x => x.UnitQtyDouble).Sum();
                    if (partSum < required.Value) // unsatisfied
                        return null;

                    // Pair로 만들 수 있는 최대 수량은, 각 FromPart 요구량의 최소 배수
                    maxPair = Math.Min((partSum / required.Value).Floor(), maxPair);

                    part.ForEach(lot => partsCandidate.Add((required.Key, required.Value), lot));
                }

                if (InputMart.Instance.BOMParentChildMap.IsNullOrEmpty() == false)
                {
                    var children = InputMart.Instance.BOMParentChildMap.SafeGet(info.MergeableKey);
                    if (children.IsNullOrEmpty() == false)
                    {
                        // ParentLotID가 지정된 경우, child 모두 도착한 경우에만 Merge 진행 (동일한 ParentLotID로 여러개가 쪼개져서 생성되는 것을 방지)
                        foreach (var childLotID in children)
                        {
                            var childLot = waitingParts.SelectMany(x => x).Where(x => x.LotID == childLotID).FirstOrDefault();
                            if (childLot == null)
                                return null;
                        }
                    }
                }

                // 최대 Pair 수량만큼 한번에 Merge하기 위함.
                var partsPairCandidate = new MultiDictionary<(FabProduct, float), FabSemiconLot>();
                partsCandidate.ForEach(x => partsPairCandidate.AddMany((x.Key.Item1, x.Key.Item2 * maxPair), x.Value));

                unitQty = maxPair;

                return partsPairCandidate;
            }

            static List<ISimEntity> DoKittingAndGetRemainEntitys(MultiDictionary<(FabProduct, float), FabSemiconLot> partsCandidate, FabSemiconLot parent)
            {
                List<ISimEntity> remains = new List<ISimEntity>();
                foreach (var kvp in partsCandidate)
                {
                    var requiredQty = kvp.Key.Item2;

                    foreach (var childLot in kvp.Value)
                    {
                        float mergedQty = (float)childLot.UnitQtyDouble;
                        string reason = "Merged to Parent " + parent.LotID;
                        if (requiredQty < childLot.UnitQtyDouble)
                            mergedQty = requiredQty;

                        requiredQty -= mergedQty;
                        childLot.UnitQtyDouble -= mergedQty;

                        reason += ", Qty=" + mergedQty;

                        OutputHelper.UpdateCurrentWipLogSeq(AoFactory.Current.NowDT);
                        OutputHelper.WriteWipLog(LogType.INFO, "BOM_CHILD", childLot, AoFactory.Current.NowDT, reason, InputMart.Instance.CurrentWipLogSeq.Item2);

                        if (childLot.UnitQtyDouble > 0)
                            break; // 잔여 수량이 남은 child는 다음 Merge를 위해, 잔류

                        childLot.FabWipInfo.IsMergeOut = true;
                        AoFactory.Current.Out(childLot);
                        //TODO: out 된 lot의 memory disposing 잘 되는지?

                        if (requiredQty <= 0)
                            break;
                    }
                }

                remains.AddRange(partsCandidate.Values.Where(x => x.FabWipInfo.IsMergeOut == false));

                return remains;
            }
        }

        public List<ISimEntity> DISPOSE_ENTITIES0(List<ISimEntity> entitys, ref bool handled, List<ISimEntity> prevReturnValue)
        {
            return entitys.Where(x => (x as FabSemiconLot).FabWipInfo.IsMergeOut).ToList();
        }
    }
}