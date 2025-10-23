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
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class ResourceHelper
    {
        public static FabSemiconEqp GetEqp(string eqpId)
        {
            if (string.IsNullOrEmpty(eqpId))
                return null;

            return InputMart.Instance.FabSemiconEqpView.FindRows(eqpId).FirstOrDefault();
        }

        //private static DateTime GetDownStartTime(EqpDownStochasticConfig downInfo)
        //{
        //    var startTime = ModelContext.Current.StartTime;
        //    if (downInfo.MTTFDistFunction == DistFunctionType.Normal)
        //    {
        //        var value = Helper.GetNormalDistributionRandomNumber(downInfo.NormalMeanHour, downInfo.NormalMeanHour / 3d);

        //        if (value < 0)
        //            value = 0;
        //        if (value > downInfo.MTTFHour)
        //            value = downInfo.MTTFHour;

        //        startTime = startTime.AddHours(value * -1);
        //    }

        //    return startTime;
        //}

//        private static double GetHalfTTFValue(EqpDownStochasticConfig config)
//        {
//            double value;

//            if (config.MTTFDistFunction == DistFunctionType.Exponential)
//            {
//                double mttfLamda = 1 / (config.MTTFHour / 2d);
//                value = Helper.GetExpDistributionRandomNumber(mttfLamda);
//            }
//            else if (config.MTTFDistFunction == DistFunctionType.Uniform)
//            {
//                value = Helper.GetUniformDistributionRandomNumberWithMargin(config.MTTFHour / 2d, config.UniformMargin);
//            }
//            else // Normal Dist.
//            {
//                value = Helper.GetNormalDistributionRandomNumber(config.NormalMeanHour, config.NormalMeanHour / 3d);
//            }

//            if (value < 0)
//                value = 0;
//            if (value > config.MTTFHour)
//                value = config.MTTFHour;

//            return value;
//        }

//        private static double GetTimeToFailure(EqpDownStochasticConfig downInfo)
//        {
//            double mttfLamda = 1 / downInfo.MTTFHour;

//            if (downInfo.MTTFDistFunction == DistFunctionType.Exponential)
//            {
//                return Helper.GetExpDistributionRandomNumber(mttfLamda);
//            }
//            else if (downInfo.MTTFDistFunction == DistFunctionType.Uniform)
//            {
//                return Helper.GetUniformDistributionRandomNumberWithMargin(downInfo.MTTFHour / 2d, downInfo.UniformMargin);
//            }
//            else // Normal Dist.
//            {
//                return downInfo.MTTFHour;
//            }
//        }

//        private static double GetTimeToRepair(EqpDownStochasticConfig downInfo)
//        {
//#if true
//            return downInfo.MTTRHour;
//#else
//            double mttrLamda = 1 / downInfo.MTTRHour;

//            if (downInfo.DistFunction == DistFunctionType.Exponential)
//            {
//                return Helper.GetExpDistributionRandomNumber(mttrLamda);
//            }
//            else
//            {
//                return downInfo.MTTRHour;
//            } 
//#endif
//        }

        public static FabSemiconEqp GetEqp2(string eqpId) // Check SubEqp
        {
            var eqp = GetEqp(eqpId);
            if (eqp != null)
                return eqp;

            var sub = GetSubEqp(eqpId);
            return sub == null ? null : sub.Parent as FabSemiconEqp;
        }
        internal static FabSemiconSubEqp GetSubEqp(string subId)
        {
            if (string.IsNullOrEmpty(subId))
                return null;

            return InputMart.Instance.FabSemiconSubEqpView.FindRows(subId).FirstOrDefault();
        }

        public static ProcTimeInfo GetProcessTime(AoEquipment aeqp, IHandlingBatch hb, string targetStepId = null)
        {
            var lot = hb.Sample as FabSemiconLot;
            EqpArrange arr = null;
            if (targetStepId == null)
            {
                arr = lot.CurrentFabPlan.Arrange;
                if (arr == null || arr.Eqp.ResID != aeqp.EqpID)
                {
                    // PartStepAttribute에서 찾으면, 가용시간 반영시 못찾을 수 도 있음.
                    // 현재 구조에서는 전체 View에서 찾는 것이 안전.
                    arr = InputMart.Instance.EqpArrangePartStepEqpView.FindRows(lot.FabProduct.PartID, lot.CurrentStepID, aeqp.EqpID).FirstOrDefault();
                    lot.CurrentFabPlan.Arrange = arr;
                }
            }
            else
                arr = InputMart.Instance.EqpArrangePartStepEqpView.FindRows(lot.FabProduct.PartID, targetStepId, aeqp.EqpID).FirstOrDefault();

            if (arr == null) // BOH RUN
            {
                return GetAltProcTime(aeqp as FabAoEquipment, lot, targetStepId ?? lot.CurrentStepID);
            }

            if (arr.ProcTime.TactTime == TimeSpan.Zero || arr.ProcTime.FlowTime == TimeSpan.Zero)
            {
                if (arr.IsBackup)
                    SetBackupProcTime(arr);

                if (arr.ProcTime.TactTime == TimeSpan.Zero || arr.ProcTime.FlowTime == TimeSpan.Zero)
                    SetAltProcTime(arr);
            }

            var unitBatchInfo = arr.Eqp.UnitBatchInfo;
            if (arr.FlowTimePdfConfig != null)
            {
                lot.CurrentFabPlan.ProcTime = CreatePdfProcTimeInfo(arr);
            }
            else if (unitBatchInfo != null && unitBatchInfo.HasFinitePort)
            {
                var portRequireCount = (int)Math.Ceiling(lot.UnitQtyDouble / unitBatchInfo.UnitBatchSize);
                if (portRequireCount >= 2)
                    lot.CurrentFabPlan.ProcTime = CreateUnitBatchSerialProcTimeInfo(arr, portRequireCount);
                else
                    lot.CurrentFabPlan.ProcTime = arr.ProcTime;
            }
            else
            {
                lot.CurrentFabPlan.ProcTime = arr.ProcTime;
            }

            return lot.CurrentFabPlan.ProcTime;

            static ProcTimeInfo CreatePdfProcTimeInfo(EqpArrange arr)
            {
                ProcTimeInfo pdfProcTimeInfo = new ProcTimeInfo();
                pdfProcTimeInfo.TactTime = arr.ProcTime.TactTime;

                var pdfFlow = Helper.GetDistributionRandomNumber(arr.FlowTimePdfConfig);
                pdfProcTimeInfo.FlowTime = TimeSpan.FromSeconds(pdfFlow);

                var utilization = arr.Eqp.Utilization;
                var minRequiredFlow = (pdfProcTimeInfo.TactTime.TotalSeconds / utilization) + 1;
                if (pdfProcTimeInfo.TactTime.TotalSeconds > 0 && pdfFlow < minRequiredFlow)
                    pdfProcTimeInfo.FlowTime = TimeSpan.FromSeconds(minRequiredFlow);

                return pdfProcTimeInfo;
            }

            static ProcTimeInfo CreateUnitBatchSerialProcTimeInfo(EqpArrange arr, int portRequireCount)
            {
                ProcTimeInfo serialProcTimeInfo = new ProcTimeInfo();
                serialProcTimeInfo.TactTime = TimeSpan.FromSeconds(arr.ProcTime.TactTime.TotalSeconds * portRequireCount); // UnitBatch는 Tact을 사용.
                serialProcTimeInfo.FlowTime = arr.ProcTime.FlowTime; // 의미없음.

                return serialProcTimeInfo;
            }
        }

        private static void SetBackupProcTime(EqpArrange arr)
        {
            var stepTimes = InputMart.Instance.STEP_TIMEView.FindRows(arr.PartID, arr.StepID, arr.Eqp.ResID);
            if (stepTimes.IsNullOrEmpty())
                return;

            // Chamber의 경우 적절한 ChamberCount 사용을 위해 반복문 진행.
            foreach (var entity in stepTimes)
            {
                SetArrangeProcTime(arr, entity);
            }
        }

        internal static void SetArrangeProcTime(EqpArrange arr, STEP_TIME entity)
        {
            float validTactSec = GetValidTactSeconds(entity);

            ProcTimeInfo procTime = new ProcTimeInfo();
            procTime.TactTime = TimeSpan.FromSeconds(validTactSec);
            procTime.FlowTime = TimeSpan.FromSeconds(entity.FLOW_TIME);

            if (Helper.GetConfig(ArgsGroup.Simulation_Run).stochasticTables.Contains("STEP_TIME"))
                arr.FlowTimePdfConfig = CreateHelper.CreateStochasticConfig(entity.FLOW_TIME_PDF);

            var utilization = arr.Eqp.Utilization;
            var minRequiredFlow = (validTactSec / utilization) + 1;
            if (entity.FLOW_TIME < minRequiredFlow)
                procTime.FlowTime = TimeSpan.FromSeconds(minRequiredFlow);

            var orgTact = procTime.TactTime.TotalHours;
            var orgFlow = procTime.FlowTime.TotalHours;

            double chamberCount = Math.Max(arr.SubEqps.Count, 1);

            if (arr.Eqp.OrgSimType == "Chamber")
            {
                if (arr.SubEqps.Count == 0) // Chamber SubArrange가 없는 경우, 설비의 SubEqp를 다 사용하는 것으로 간주.
                    chamberCount = Math.Max(arr.Eqp.SubEqpCount, 1); // SubEqp 기준정보도 없는 경우 1을 사용.

                if (entity.CHAMBER_COUNT == 1 && arr.ProcTime.TactTime == TimeSpan.Zero) // 일치하는 ChamberCount가 없는 경우를 위해 ChamberCount=1의 값을 기본 세팅하도록 처리
                {
                    procTime.TactTime = TimeSpan.FromSeconds(validTactSec / chamberCount);
                }
                else if (chamberCount != entity.CHAMBER_COUNT)
                {
                    return;
                }
            }

            procTime = ResourceHelper.ConvertProcTime(arr, procTime, orgTact, orgFlow);

            arr.ProcTime = procTime;
        }

        private static float GetValidTactSeconds(STEP_TIME entity)
        {
            float validTactSec = entity.TACT_TIME > 0 ? entity.TACT_TIME : Helper.GetConfig(ArgsGroup.Arrange_StepTime).defaultTactTimeSec;
            if (validTactSec <= 0)
                validTactSec = 60; // Config값도 사용자가 오입력했을 경우 강제 변환.
            return validTactSec;
        }

        private static void SetAltProcTime(EqpArrange arr)
        {
            ProcTimeInfo altProcTime;

            string key = "Default";
            if (InputMart.Instance.ResourceProcTimeDict.TryGetValue(arr.EqpID, out altProcTime))
            {
                key = "Resource";
            }
            else if (InputMart.Instance.WorkstationProcTimeDict.TryGetValue(arr.Eqp.ResGroup, out altProcTime))
            {
                key = "Workstation";
            }
            else
            {
                altProcTime = InputMart.Instance.DefaultAltProcTime;
            }

            var utilization = arr.Eqp.Utilization;
            var minRequiredFlow = (altProcTime.TactTime.TotalSeconds / utilization) + 1;
            if (altProcTime.TactTime.TotalSeconds > 0 && altProcTime.FlowTime.TotalSeconds < minRequiredFlow)
                altProcTime.FlowTime = TimeSpan.FromSeconds(minRequiredFlow);

            OutputHelper.WriteErrorLogWithArrange(LogType.INFO, "AltStepTime", arr,
                string.Format("{0} Average Tact = {1}, Flow = {2}", key, Math.Round(altProcTime.TactTime.TotalSeconds, 2), Math.Round(altProcTime.FlowTime.TotalSeconds, 2)));

            altProcTime = ConvertProcTime(arr, altProcTime);

            arr.ProcTime = altProcTime;
        }

        private static ProcTimeInfo GetAltProcTime(FabAoEquipment eqp, FabSemiconLot lot, string targetStepId)
        {
            ProcTimeInfo altProcTime;

            string key = "None";
            if (InputMart.Instance.ResourceProcTimeDict.TryGetValue(eqp.EqpID, out altProcTime))
            {
                key = "Resource";
            }
            else if (InputMart.Instance.WorkstationProcTimeDict.TryGetValue(eqp.Target.ResGroup, out altProcTime))
            {
                key = "Workstation";
            }

            var utilization = eqp.Eqp.Utilization;
            var minRequiredFlow = (altProcTime.TactTime.TotalSeconds / utilization) + 1;
            if (altProcTime.TactTime.TotalSeconds > 0 && altProcTime.FlowTime.TotalSeconds < minRequiredFlow)
                altProcTime.FlowTime = TimeSpan.FromSeconds(minRequiredFlow);

            OutputHelper.WriteErrorLogWithEqp(LogType.INFO, "AltStepTime", eqp, lot, targetStepId,
                string.Format("{0} Average Tact = {1}, Flow = {2}", key, Math.Round(altProcTime.TactTime.TotalSeconds, 2), Math.Round(altProcTime.FlowTime.TotalSeconds, 2)));

            return altProcTime;
        }

        internal static ProcTimeInfo ConvertProcTime(EqpArrange arr, ProcTimeInfo procTime, double orgTact = 0, double orgFlow = 0)
        {
            if (orgTact == 0)
                orgTact = procTime.TactTime.TotalHours;
            if (orgFlow == 0)
                orgFlow = procTime.FlowTime.TotalHours;

            arr.OrgTact = orgTact;
            arr.OrgFlow = orgFlow;

            bool chamberToInline = Helper.GetConfig(ArgsGroup.Resource_SimType).chamberToInline == "Y";
            bool batchToInline = Helper.GetConfig(ArgsGroup.Resource_SimType).batchToInline == "Y";

            bool isLotBatch = arr.Eqp.OrgSimType == "LotBatch";
            bool isBatchInline = arr.Eqp.OrgSimType == "BatchInline";
            bool isParallelChamber = arr.Eqp.OrgSimType == "ParallelChamber";

            // UnitBatch는 전환하지 않음.
            if (batchToInline && (isLotBatch || isBatchInline))
            {
                double maxWafer = 1;
                
                if (isLotBatch)
                    maxWafer = arr.BatchSpec != null ? arr.BatchSpec.MaxWafer : Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMaxBatchSizeLotBatch;
                else 
                    maxWafer = arr.BatchSpec != null ? arr.BatchSpec.MaxWafer : Helper.GetConfig(ArgsGroup.Logic_Batching).defaultMaxBatchSizeBatchInline;

                double newTact = orgTact / maxWafer;
                procTime.TactTime = TimeSpan.FromHours(newTact);
                procTime.FlowTime = TimeSpan.FromHours(orgFlow - (newTact * (InputMart.Instance.LotSize - 1))); // 전환 전후 RunTAT를 일정하게 유지하기 위함.
            }

            // Identical Chamber는 옵션에 관계없이 항상 Inline 전환되어 있음.
            if (chamberToInline && isParallelChamber)
            {
                if (arr.SubEqps.IsNullOrEmpty())
                    arr.Eqp.SubEqps.ForEach(x => arr.SubEqps.Add(x as FabSemiconSubEqp));

                procTime.TactTime = TimeSpan.FromHours(orgTact / arr.SubEqps.Count);
                // FlowTime은 OrgFlow 그대로 사용.
                // 이것이 RunTAT를 정확하게 반영하지는 못하지만, 일반적으로 chamber별 확산이 균일하지 않은 경우 원본 설비의 RunTAT는 lotSize * newTact을 초과하기 때문에
                // newFlow 값은 newTact보다 길어야 적절하다고 생각 됨.
            }

            return procTime;
        }

        private static List<ToolingSelectionInfo> CreateReticleSelectionInfos(FabAoEquipment feqp, IList<IHandlingBatch> wips)
        {
            if (feqp.Eqp.ToolingInfo.ReticleList.IsNullOrEmpty() == false)
                feqp.Eqp.ToolingInfo.ReticleList.ForEach(x => x.AtStepWorkload = 0);

            List<ToolingSelectionInfo> infos = new List<ToolingSelectionInfo>();
            foreach (var entity in wips)
            {
                var lot = entity as FabSemiconLot;

                var arr = lot.CurrentArranges.SafeGet(feqp.EqpID);
                if (arr == null || arr.ToolingData == null)
                    continue;

                var reticleToolingNames = arr.ToolingData.ToolingItems.Where(x => x.Item1 == ToolingType.Reticle).Select(x => x.Item2);
                foreach (var name in reticleToolingNames)
                {
                    var toolingArranges = arr.ToolingData.ToolingArranges.SafeGet(name);
                    foreach (FabReticle reticle in toolingArranges)
                    {
                        var info = infos.FirstOrDefault(x => x.Tooling == reticle);
                        if (info == null)
                        {
                            info = new ToolingSelectionInfo();
                            info.Tooling = reticle;
                            info.ToolingName = name; // ToolingName별로 집계하는 경우는 고려되지 않았으며, sample 값임.
                            info.ToolingType = reticle.ToolingType;
                            info.ToolingList = toolingArranges.Select(x => x.ToolingID).Join(",");
                            info.Location = reticle.ToolingLocation;
                            info.SelectableTime = reticle.SelectableTime;
                            info.Lot = lot; // sample

                            infos.Add(info);
                        }

                        var procTimeInfo = ResourceHelper.GetProcessTime(feqp, entity);
                        var workload = procTimeInfo.TactTime.TotalHours * lot.UnitQtyDouble;

                        info.WorkloadHrs += (float)workload;
                        info.LotList.Add(lot);

                        reticle.AtStepWorkload += (float)workload;
                    }
                }
            }

            return infos;
        }

        public static void KeepReticleOnEqp(FabSemiconEqp eqp, FabReticle reticle, string toolingName, bool onPersist = false)
        {
            if (reticle.KeepingEqp != null && reticle.KeepingEqp.ToolingInfo.ReticleList != null)
                reticle.KeepingEqp.ToolingInfo.ReticleList.Remove(reticle);

            reticle.KeepingEqp = eqp;
            reticle.ToolingLocation = eqp.ResID;

#if true // .NET에서 엔진 RUN마다 값이 달라지는 문제가 발생하는데, AoFactory.Current가 null인 시점이 일정하지 않은것으로 추정됨.
            reticle.KeepStartTime = onPersist ? reticle.LastUpdateDateTime : AoFactory.Current.NowDT;
#else
            reticle.KeepStartTime = AoFactory.Current == null ? reticle.StatusChangeTime : AoFactory.Current.NowDT; 
#endif
            reticle.KeepEndTime = reticle.KeepStartTime.AddHours(Helper.GetConfig(ArgsGroup.Resource_Tooling).minReticleKeepHrs);
            reticle.SelectableTime = reticle.KeepEndTime.AddHours(Helper.GetConfig(ArgsGroup.Resource_Tooling).reticleMoveHrs);

            if (eqp.ToolingInfo.ReticleList == null)
                eqp.ToolingInfo.ReticleList = new List<FabReticle>();

            eqp.ToolingInfo.ReticleList.Add(reticle);

            OutputHelper.WriteToolingHistory(reticle, reticle.KeepStartTime, toolingName);

            if (eqp.ToolingInfo.ReticleList.Count > eqp.ToolingInfo.ToolLimitCount)
            {
                var returnReticle = eqp.ToolingInfo.ReticleList.OrderBy(x => x.KeepStartTime).First();

                KeepReticleOnStocker(returnReticle, "-", eqp, onPersist);
            }
        }

        public static void KeepReticleOnStocker(FabReticle reticle, string toolingName, FabSemiconEqp fromEqp, bool onPersist = false)
        {
            if (reticle == null)
                return;

            if (reticle.KeepingEqp != null && reticle.KeepingEqp.ToolingInfo.ReticleList != null)
                reticle.KeepingEqp.ToolingInfo.ReticleList.Remove(reticle);

            reticle.KeepingEqp = null;
            reticle.ToolingLocation = "STOCKER";

            if (fromEqp != null)
            {
                reticle.KeepStartTime = reticle.SelectableTime - TimeSpan.FromHours(Helper.GetConfig(ArgsGroup.Resource_Tooling).reticleMoveHrs);
                reticle.KeepEndTime = reticle.KeepStartTime;
            }
            else
            {
#if true// .NET에서 엔진 RUN마다 값이 달라지는 문제가 발생하는데, AoFactory.Current가 null인 시점이 일정하지 않은것으로 추정됨.
                reticle.KeepStartTime = onPersist ? reticle.LastUpdateDateTime : AoFactory.Current.NowDT;
#else
                reticle.KeepStartTime = AoFactory.Current == null ? reticle.StatusChangeTime : AoFactory.Current.NowDT; 
#endif
                reticle.KeepEndTime = reticle.KeepStartTime;
                reticle.SelectableTime = reticle.KeepEndTime.AddHours(Helper.GetConfig(ArgsGroup.Resource_Tooling).reticleMoveHrs);
            }

            OutputHelper.WriteToolingHistory(reticle, reticle.KeepStartTime, toolingName);
        }

        public static List<ToolingSelectionInfo> GetReticleSelectionInfos(FabAoEquipment aeqp, IList<IHandlingBatch> wips)
        {
            var eqpModel = aeqp.Eqp;
            if (eqpModel.ToolingInfo.IsNeedReticle == false)
                return null;

            var reticleSelectionInfos = CreateReticleSelectionInfos(aeqp, wips);

            var sorted = reticleSelectionInfos.OrderByDescending(x => x.WorkloadHrs).ToList();

            return sorted;
        }

        public static void SetSelectableReticleList(FabAoEquipment aeqp, List<ToolingSelectionInfo> reticleSelectionInfos, bool writeLog = false)
        {
            if (reticleSelectionInfos.IsNullOrEmpty())
                return;

            // Workload가 큰 순으로 소팅되어 있음.
            foreach (var info in reticleSelectionInfos)
            {
                var reticle = info.Tooling as FabReticle;

                if (info.Location == aeqp.EqpID)
                    aeqp.Eqp.ToolingInfo.SelectableReticleList.Add(reticle);
                else
                {
                    if (reticle.OnSeized)
                        info.Reason = "Seized on another Eqp";
                    else if (aeqp.NowDT < info.SelectableTime)
                        info.Reason = "Not Available Time";
                    else
                        aeqp.Eqp.ToolingInfo.SelectableReticleList.Add(reticle);
                }

                if (writeLog)
                    OutputHelper.WriteToolingSelectionLog(aeqp, info);
            }
        }

        internal static EqpDownTag CreateEqpDownTagConditional(DateTime startTime, EqpDownConditionalConfig config)
        {
            EqpDownTag tag = new EqpDownTag();

            tag.ConditionalConfig = config;

            tag.EqpID = config.EqpID;
            tag.Eqp = GetEqp2(config.EqpID);

            tag.DownTypeStr = config.DownTypeStr;
            tag.DownType = config.DownType;
            tag.DownCondType = config.DownCondType;
            tag.EventCode = config.EventCode ?? "-";

            var durationHour = Helper.GetDistributionRandomNumber(config.MttrPdfConfig, RNGType.Isolation);
            tag.DurationSecond = TimeSpan.FromHours(durationHour).TotalSeconds.Floor();

            tag.StartTime = startTime.Floor();

            //주의:이렇게 하면 밀리초 이하의 더 작은 소수점들이 남아버림.
            //tag.StartTime = startTime.Subtract(TimeSpan.FromMilliseconds(startTime.Millisecond));

            tag.EndTime = startTime.AddSeconds(tag.DurationSecond);

            return tag;
        }

        internal static EqpDownTag CreateEqpDownTagSchedule(EQP_DOWN_SCHED entity)
        {
            EqpDownTag tag = new EqpDownTag();
            tag.DownTypeStr = entity.DOWN_TYPE;

            if (entity.DOWN_TYPE == "PM")
                tag.DownType = EqpDownType.PM;
            else if (entity.DOWN_TYPE == "BM")
                tag.DownType = EqpDownType.BM;
            else
                return null;

            tag.EqpID = entity.EQP_ID;
            tag.Eqp = GetEqp2(entity.EQP_ID);

            tag.EventCode = entity.EVENT_CODE ?? "-";
            var stackPMInfo = InputMart.Instance.StackPMInfoView.FindRows(tag.EventCode).FirstOrDefault();
            if (stackPMInfo != null)
            {
                tag.IsStackPm = true;
                tag.StackPM = stackPMInfo;
            }

            tag.DurationSecond = (entity.END_DATETIME - entity.START_DATETIME).TotalSeconds.Floor();
            tag.StartTime = entity.START_DATETIME;
            tag.EndTime = entity.END_DATETIME;

            return tag;
        }

        internal static void CreateAvailablePM(EqpDownTag tag, bool doOverlapShift = false)
        {
            var eqp = tag.Eqp;
            if (eqp == null || tag.DurationSecond < 0)
                return;

            // Check Before Sim Start
            if (doOverlapShift)
            {
                tag = GetOverlapShiftDownStartTime(tag);
            }
            else if (GetLongerOverlapPM(tag) != null)
            {
                return;
            }

            if (eqp.SimType == SimEqpType.ParallelChamber)
            {
                CreateParallelChamberPMSchedule(tag);
            }
            else
            {
                CreatePMSchedule(tag);
            }
        }

        internal static List<PMSchedule> CreateParallelChamberPMSchedule(EqpDownTag tag)
        {
            var eqp = tag.Eqp;

            InitializeEqpDownCollections(eqp);

            bool createAll = eqp.ResID == tag.EqpID;

            List<PMSchedule> list = new List<PMSchedule>();
            foreach (var subEqp in eqp.SubEqps)
            {
                if (createAll == false && subEqp.SubEqpID != tag.EqpID)
                    continue;

                var pm = new PMSchedule(tag.StartTime, tag.DurationSecond);
                pm.PMType = PMType.Component;
                pm.ComponentID = subEqp.SubEqpID;
                pm.ScheduleType = DownScheduleType.ShiftBackward;

                eqp.PMList.Add(pm);
                eqp.EqpDownTags.Add(pm, tag);

                list.Add(pm);
            }

            return list;
        }

        internal static PMSchedule CreatePMSchedule(EqpDownTag tag)
        {
            var eqp = tag.Eqp;

            InitializeEqpDownCollections(eqp);

            var pm = new PMSchedule(tag.StartTime, tag.DurationSecond);
            pm.PMType = PMType.Full;
            pm.ScheduleType = DownScheduleType.ShiftBackward;

            eqp.PMList.Add(pm);
            eqp.EqpDownTags.Add(pm, tag);

            return pm;
        }

        private static void InitializeEqpDownCollections(FabSemiconEqp eqp)
        {
            if (eqp.PMList.IsNullOrEmpty())
                eqp.PMList = new List<PMSchedule>();

            if (eqp.EqpDownTags.IsNullOrEmpty())
                eqp.EqpDownTags = new Dictionary<PMSchedule, EqpDownTag>();
        }

        private static PMSchedule GetLongerOverlapPM(EqpDownTag tag)
        {
            // 기간을 입력받는 DownSchedule 및 EqpDownCondType = TBM인 Conditional PM 들은 서로 겹치면 Duration이 더 긴것만 취함.
            // EventCode가 서로 겹치면 관리가 안되기 때문에 Merge는 진행하지 않음.

            // Obsolete
            // Stochastic UD에 대해서만 특수하게, 겹쳐서 삭제될 조건일 때, 하루를 미뤄서 생성하도록 함. (UD Count를 유지하기 위함)

            var eqp = tag.Eqp;
            if (eqp.PMList.IsNullOrEmpty())
                return null;

            PMSchedule lastOverlapPM = null;
            List<PMSchedule> overlapPMList = new List<PMSchedule>();
            foreach (var exist in eqp.PMList)
            {
                if (tag.StartTime >= exist.EndTime || tag.EndTime <= exist.StartTime)
                    continue;

                overlapPMList.Add(exist);

                if (lastOverlapPM == null || exist.EndTime > lastOverlapPM.EndTime)
                    lastOverlapPM = exist;
            }

            var overlapdurationSecond = overlapPMList.Sum(x => x.Duration.TotalSeconds);
            if (tag.DurationSecond > overlapdurationSecond)
            {
                overlapPMList.ForEach(x => eqp.PMList.Remove(x));
                return null;
            }

            return lastOverlapPM;
        }

        internal static double[] GetEqpDownParameters(string str)
        {
            List<double> list = new List<double>();
            var fromIndex = str.IndexOf('(') + 1;
            while (fromIndex > 0)
            {
                var toIndex = str.IndexOf(')');
                if (toIndex <= 0)
                    break;

                var length = toIndex - fromIndex;
                if (length <= 0)
                    break;

                if (double.TryParse(str.Substring(fromIndex, length), out double value))
                {
                    list.Add(value);

                    var newIndex = toIndex + 2;
                    var newLength = str.Length - newIndex;
                    if (newLength <= 0)
                        break;

                    str = str.Substring(newIndex, str.Length - newIndex);
                    fromIndex = str.IndexOf('(') + 1;
                }
                else
                    break;
            }

            return list.ToArray();
        }

        internal static EqpDownConditionalConfig CreateConditionalDownConfig(EQP_DOWN_COND entity)
        {
            if (entity.DOWN_TYPE == null)
                return null;

            EqpDownConditionalConfig config = new EqpDownConditionalConfig();
            config.DownTypeStr = entity.DOWN_TYPE;

            if (entity.DOWN_TYPE.StartsWith("PM"))
                config.DownType = EqpDownType.PM;
            else if (entity.DOWN_TYPE.EndsWith("BM"))
                config.DownType = EqpDownType.BM;
            else
                return null;

            if (entity.DOWN_TYPE.EndsWith("TBM"))
                config.DownCondType = EqpDownCondType.TBM;
            else if (entity.DOWN_TYPE.EndsWith("CBM"))
                config.DownCondType = EqpDownCondType.CBM;
            else
                return null;

            config.EventCode = entity.EVENT_CODE;
            config.EqpID = entity.EQP_ID;

            config.CurrentCount = entity.CURRENT_COUNT;
            config.LimitCount = entity.PM_LIMIT_COUNT;
            config.LastEventTime = entity.LAST_EVENT_TIME;

            config.MttrPdfConfig = CreateHelper.CreateStochasticConfig(entity.MTTR_HRS_PDF);
            config.MttfPdfConfig = CreateHelper.CreateStochasticConfig(entity.MTTF_HRS_PDF);

            if (config.DownCondType == EqpDownCondType.TBM)
            {
                if (config.MttrPdfConfig == null || config.MttfPdfConfig == null)
                    return null;
            }
            else if (config.DownCondType == EqpDownCondType.CBM)
            {
                if (config.MttrPdfConfig == null || config.LimitCount <= 0)
                    return null;
            }

            return config;
        }

        internal static EqpDownTag GetEqpDownTag(FabSemiconEqp eqp, Time eventTime)
        {
            var feqp = eqp.SimObject;

            if (eqp.PMList == null)
            {
                Logger.MonitorInfo("PMList is null. Check ErrorLog");
                if (feqp != null)
                    OutputHelper.WriteErrorLogWithEqp(LogType.WARNING, "PM", feqp, null, null, eventTime.ToString());

                return null;
            }

            if (eqp.EqpDownTags == null)
            {
                Logger.MonitorInfo("PMConfigMap is null. Check ErrorLog");
                if (feqp != null)
                    OutputHelper.WriteErrorLogWithEqp(LogType.WARNING, "PM", feqp, null, null, eventTime.ToString());

                return null;
            }

            var orgPM = eqp.PMList.Where(x => x.StartTime <= eventTime).OrderByDescending(x => x.StartTime).FirstOrDefault();
            if (orgPM == null)
            {
                Logger.MonitorInfo("orgPM is null. Check ErrorLog");
                if (feqp != null)
                    OutputHelper.WriteErrorLogWithEqp(LogType.WARNING, "PM", feqp, null, null, eventTime.ToString());

                return null;
            }

            var config = eqp.EqpDownTags.SafeGet(orgPM);

            return config;
        }

        internal static Time GetSetupTime(AoEquipment aeqp, EqpArrange fromArr, EqpArrange toArr)
        {
            if (fromArr == null || toArr == null)
                return Time.Zero;

            string from = fromArr.SetupName;
            string to = toArr.SetupName;

            if (from == null || to == null || from == to)
                return Time.Zero;

            var switchTimeInfo = GetSwitchTimeInfo(aeqp, from, to);
            if (switchTimeInfo == null)
                return Time.Zero;

            if (switchTimeInfo.SwitchTimePdfConfig == null)
                return switchTimeInfo.SwitchTime;

            var pdfSwitchMins = Helper.GetDistributionRandomNumber(switchTimeInfo.SwitchTimePdfConfig);

            return Time.FromMinutes(pdfSwitchMins);

            static SwitchTimeInfo GetSwitchTimeInfo(AoEquipment aeqp, string from, string to)
            {
                var setupInfos = (aeqp as FabAoEquipment).Eqp.SetupInfos;

                if (setupInfos.ContainsKey(from, to))
                    return setupInfos[from, to];

                var fromSpecific = setupInfos.SafeGet(from);
                if (fromSpecific.IsNullOrEmpty() == false && fromSpecific.ContainsKey("*"))
                    return fromSpecific["*"];

                var fromWhatever = setupInfos.SafeGet("*");
                if (fromWhatever.IsNullOrEmpty() == false)
                {
                    if (fromWhatever.ContainsKey(to))
                        return fromWhatever[to];

                    if (fromWhatever.ContainsKey("*"))
                        return fromWhatever["*"];
                }

                return null;
            }
        }
        private static EqpDownTag GetOverlapShiftDownStartTime(EqpDownTag tag)
        {
            var overlap = GetLongerOverlapPM(tag);
            while (overlap != null)
            {
                tag.StartTime = overlap.EndTime.AddDays(1);
                tag.EndTime = tag.StartTime.AddSeconds(tag.DurationSecond);

                overlap = GetLongerOverlapPM(tag);
            }

            return tag;
        }

        internal static void SetProcessInhibit(FabAoEquipment feqp)
        {
            if (feqp.Eqp.EqpDownTags.IsNullOrEmpty())
                return;

            foreach (var kvp in feqp.Eqp.EqpDownTags)
            {
                var tag = kvp.Value;
                if (tag.IsStackPm)
                {
                    var inhibitStartTime = (tag.StartTime.AddDays(-1 * tag.StackPM.PreInhibitDays)).Floor();

                    if (inhibitStartTime <= ModelContext.Current.EndTime)
                    {
                        if (feqp.Eqp.ProcessInhibitHistory == null)
                            feqp.Eqp.ProcessInhibitHistory = new Dictionary<EqpDownTag, Tuple<DateTime, DateTime>>();

                        if (feqp.Eqp.ProcessInhibitHistory.ContainsKey(tag) == false)
                            feqp.Eqp.ProcessInhibitHistory.Add(tag, new Tuple<DateTime, DateTime>(inhibitStartTime, DateTime.MaxValue));

                        var delay = inhibitStartTime - ModelContext.Current.StartTime;
                        if (delay < TimeSpan.Zero)
                            delay = TimeSpan.Zero;

                        var args = new Tuple<PMSchedule, EqpDownTag>(kvp.Key, kvp.Value);
                        EventHelper.AddManualEvent(delay, ManualEventTaskType.ActivateProcessInhibit, feqp, "SetProcessInhibit", args);
                    }
                }
            }
        }

        internal static double GetLotProcessingTimeSeconds(FabSemiconLot lot, FabSemiconEqp eqp)
        {
            //TODO: CycleTime 집계시 RunHr 계산하는 부분과 중복되는 부분을 같은 함수로 묶기.
            
            var tact = lot.CurrentFabPlan.ProcTime.TactTime.TotalSeconds / eqp.Utilization;
            var flow = lot.CurrentFabPlan.ProcTime.FlowTime.TotalSeconds;

            double processingTime = 0;

            if (eqp.SimType == SimEqpType.Table)
            {
                processingTime = lot.UnitQty * tact;
            }
            else if (eqp.SimType == SimEqpType.Inline) // 설비 Type Converting이 발생한 경우를 포함하여 그대로 사용 가능.
            {
                processingTime = ((lot.UnitQty - 1) * tact) + flow;
            }
            else if (eqp.SimType == SimEqpType.BatchInline) // Convert 하지 않은 경우
            {
                processingTime = flow;
            }
            else if (eqp.SimType == SimEqpType.LotBatch || eqp.SimType == SimEqpType.UnitBatch)
            {
                processingTime = tact; // tact == flow 로 입력해야 됨. 라이브러리에서는 tact을 사용함.
            }
            else if (eqp.SimType == SimEqpType.ParallelChamber)
            {
                //TODO: ParallelChamber는 수식으로 계산 불가. 설비 태워봐야 알 수 있음.
            }

            return Math.Floor(processingTime);
        }

        internal static List<CrossFabLimit> GetCrossFabLimits(EqpLocation from, EqpLocation to)
        {
            if (from == null || to == null)
                return null;

            List<CrossFabLimit> limits = new List<CrossFabLimit>();

            limits.AddRange(InputMart.Instance.CrossFabLimitView.FindRows(from.Bay, to.Bay));
            limits.AddRange(InputMart.Instance.CrossFabLimitView.FindRows(from.Region, to.Region));
            limits.AddRange(InputMart.Instance.CrossFabLimitView.FindRows(from.Building, to.Building));
            limits.AddRange(InputMart.Instance.CrossFabLimitView.FindRows(from.Floor, to.Floor));

            // 없어도 무방
            //limits.RemoveAll(x => x == null);

            return limits;
        }

        internal static void RefreshCrossFabInfo()
        {
            if (InputMart.Instance.ApplyCrossFabLimit == false)
                return;

            DateTime anHourBefore = AoFactory.Current.NowDT.AddHours(-1);

            foreach (var info in InputMart.Instance.CrossFabLimit.Rows)
            {
                while (true)
                {
                    if (info.QueueForAnHour.Count == 0)
                        break;

                    DateTime oldest = info.QueueForAnHour.Peek();
                    if (oldest >= anHourBefore)
                        break;

                    // 1시간이 경과한 이력을 삭제
                    info.QueueForAnHour.Dequeue();
                }
            }
        }

        internal static void UpdateCrossFabInfo(AoEquipment aeqp, FabSemiconLot lot)
        {
            if (InputMart.Instance.ApplyCrossFabLimit == false)
                return;

            var prevPlan = lot.Plans.Where(x => x.LoadedResource != null).LastOrDefault() as FabPlanInfo;
            if (prevPlan == null)
                return;

            var from = (prevPlan.LoadedResource as FabSemiconEqp).LocationInfo;
            var to = (aeqp.Target as FabSemiconEqp).LocationInfo;

            List<CrossFabLimit> limits = GetCrossFabLimits(from, to);
            if (limits.IsNullOrEmpty())
                return;

            var fhb = lot as FabHandlingBatch;
            int lotQty = fhb != null ? fhb.Contents.Count : 1;

            for (int i = 0; i < lotQty; i++)
            {
                limits.ForEach(x => x.QueueForAnHour.Enqueue(aeqp.NowDT));
            }
        }

        internal static bool CanLoadableCrossFab(AoEquipment eqp, IHandlingBatch hb, ref bool handled)
        {
            // BatchBuilding과 공용
            if (InputMart.Instance.ApplyCrossFabLimit == false)
                return true;

            var lot = hb as FabSemiconLot;
            var eta = hb as FabLotETA;

            bool isBatching = eta != null;
            if (isBatching)
            {
                lot = eta.Lot as FabSemiconLot;

                // Upstream Building의 경우 Batching 시점에 CrossFab 가능 여부를 판단하지 않음.
                // Track-In 시점에 물량으로 집계는 됨.
                if (eta.IsAtStepLoadable == false)
                    return true;
            }

            var eqpModel = eqp.Target as FabSemiconEqp;

            var prevPlan = lot.Plans.Where(x => x.LoadedResource != null).LastOrDefault() as FabPlanInfo;
            if (prevPlan == null)
                return true;

            var from = (prevPlan.LoadedResource as FabSemiconEqp).LocationInfo;
            var to = eqpModel.LocationInfo;

            List<CrossFabLimit> limits = GetCrossFabLimits(from, to);
            if (limits.IsNullOrEmpty())
                return true;

            DateTime anHourBefore = eqp.NowDT.AddHours(-1);
            DateTime pst = ModelContext.Current.StartTime;
            foreach (var limit in limits)
            {
                double crossed = limit.QueueForAnHour.Count;
                if (anHourBefore < pst)
                {
                    // 보정처리
                    double beforePST = limit.InitLotQty * (pst - anHourBefore).TotalHours;
                    crossed = Math.Round(beforePST, 1) + limit.QueueForAnHour.Count;
                }

                if (crossed < limit.LimitLotQty)
                    continue;

                string reason = string.Format("Filtered by CrossFab Limit(from {0} to {1})", limit.From, limit.To);

                if (isBatching)
                {
                    eta.FilterReason = reason;
                }
                else
                {
                    eqp.EqpDispatchInfo.AddFilteredWipInfo(hb, reason);
                    lot.LastFilterReason = reason;
                }

                handled = true;
                return false;
            }

            return true;
        }

        internal static void CancelOverlappedPM(FabAoEquipment feqp, PMSchedule overlappedPM)
        {
            var eqp = feqp.Eqp;

            feqp.DownManager.CancelEvent(overlappedPM.StartTime);

            // EQP_DOWN_LOG 출력 시, 잘못된 tag를 참조하지 않도록, Cancel 된 정보는 삭제
            eqp.PMList.Remove(overlappedPM);
            eqp.EqpDownTags.Remove(overlappedPM);
        }
      
        internal static void ResetBucketingCapacity(ITimerAgent agent, object sender)
        {
            var da = AoFactory.Current.GetDispatchingAgent("-");
            foreach (var info in InputMart.Instance.StepCapaInfo.Rows)
            {
                info.CurrentCapa = 0;
                info.RollingIndex++;

                var carryOverLots = new List<FabSemiconLot>();
                foreach (var lot in info.CapaWaitingLots)
                {
                    // 한번 미뤄진 Lot이 재차 Capa가 부족하면 다다음 Bucket으로 이월.
                    // 반복문이 끝나기 전에 투입 호출이 발생되므로 미리 계산해서 판단.
                    double capaLoad = CalculateCapaload(lot);
                    if (IsStepCapable(info, capaLoad))
                    {
                        da.ReEnter(lot);
                    }
                    else
                    {
                        carryOverLots.Add(lot);
                        WriteStepCapaOverWipLog(lot, info, capaLoad);
                    }
                }

                info.CapaWaitingLots.Clear();
                info.CapaWaitingLots.AddRange(carryOverLots);
            }

            var delay = Time.FromHours(Helper.GetConfig(ArgsGroup.Resource_Bucketing).capaFreqHr);
            agent.Add(sender, ResetBucketingCapacity, delay);
        }

        internal static bool DoStepCapaBucketing(FabSemiconLot lot)
        {
            var capaInfo = lot.CurrentFabStep.CapaInfo;
            if (capaInfo == null)
                return true; // infinite capa

            double capaLoad = CalculateCapaload(lot);

            if (IsStepCapable(capaInfo, capaLoad))
            {
                capaInfo.CurrentCapa += capaLoad;
                return true;
            }

            capaInfo.CapaWaitingLots.Add(lot);
            WriteStepCapaOverWipLog(lot, capaInfo, capaLoad);

            return false;
        }

        private static bool IsStepCapable(StepCapaInfo capaInfo, double capaLoad)
        {
            return capaInfo.CurrentCapa + capaLoad <= capaInfo.CapaLimit;
        }

        private static double CalculateCapaload(FabSemiconLot lot)
        {
            return lot.UnitQtyDouble * lot.CurrentFabStep.CapaMultiplier;
        }

        private static void WriteStepCapaOverWipLog(FabSemiconLot lot, StepCapaInfo capaInfo, double capaLoad)
        {
            string reason = string.Format("StepCapa is over({0}+{1}>{2}). Wait until the next bucket starts", capaInfo.CurrentCapa, capaLoad, capaInfo.CapaLimit);
            OutputHelper.WriteWipLog(LogType.INFO, "STEP_CAPA", lot, AoFactory.Current.NowDT, reason);
        }

        public static bool ChooseParallelChamberSetupList(AoEquipment aeqp, FabSemiconLot lot, ref bool handled)
        {
            // lot.CurrentFabPlan.NeedSetupChambers에 SETUP이 필요한 Chamber를 저장해주는 함수
            if (lot.CurrentFabPlan == null)
                return false;

            // 현재 기준으로 Setup이 필요한 Chamber를 저장하기 위해 이전 정보를 Clear
            lot.CurrentFabPlan.NeedSetupChambers.Clear();

            var to = ArrangeHelper.GetCurrentEqpArrange(lot, aeqp);

            foreach (var subeqp in to.SubEqps)
            {
                if (subeqp.LastPlan != null)
                {
                    var from = subeqp.LastPlan.Arrange;
                    if (ResourceHelper.GetSetupTime(aeqp, from, to) > Time.Zero)
                        lot.CurrentFabPlan.NeedSetupChambers.Add(subeqp.SubEqpID);
                }
            }

            if (lot.CurrentFabPlan.NeedSetupChambers.Count > 0)
            {
                handled = true;
                return true;
            }

            return false;
        }

        public static IList<string> GetLoadableEqpList(IHandlingBatch hb, bool isIn)
        {
            var loadableEqpList = new List<string>();

            var lot = hb as FabSemiconLot;
            lot.CurrentArranges.Clear();

            if (TransportSystem.Apply)
            {
                // PORT가 정해지고 투입 대기중인 상황
                if (isIn && hb is FabSemiconLot flot)
                {
                    if (flot.Location != null && flot.Location.LocationType == LocationType.PORT)
                    {
                        var port = flot.Location as Port;
                        return new List<string>() { port.EqpID };
                    }
                }
            }

            var attr = lot.CurrentFabStep.PartStepDict.SafeGet(lot.FabProduct.PartID);

            //if (lot.IsWipHandle && lot.FabWipInfo.InitialEqp != null)
            //{
            //    return GetStagingEqp(lot, attr);
            //}

            if (attr == null)
                return null; // arrange input error

            StackActiveInfo activeStack = ArrangeHelper.GetActiveStack(lot, attr);

            if (activeStack != null && activeStack.StackEqp != null && activeStack.StackStepInfo.StackType == StackType.Y)
            {
                if (activeStack.CurrentStackArrange != null)
                {
                    lot.CurrentArranges.Add(activeStack.StackEqp.ResID, activeStack.CurrentStackArrange);
                }
                else
                {
                    OutputHelper.WriteErrorLogWithEqp(LogType.WARNING, "STACK", activeStack.StackEqp.SimObject, lot, lot.CurrentStepID, "Missing StackEqp Arrange");

                    attr.CurrentArranges.ForEach(arr => lot.CurrentArranges.Add(arr.EqpID, arr));
                }
            }
            else if (lot.CurrentFabPlan.DeliveryDict.IsNullOrEmpty() == false && TransportSystem.Apply == false)
            {
                // TODO : 동작 검증 필요.
                // MinDeliveryTime에 대한 설비만 GetLoadableEqpList에서 넘겨 주고 나머지 설비에 대해서는 Timer로 CallEnqueue() 호출해서 추가 됨.
                var minDeliveryTime = lot.CurrentFabPlan.DeliveryDict.Keys.Min();
                var minDeliveryEqps = lot.CurrentFabPlan.DeliveryDict[minDeliveryTime];

                foreach (var eqp in minDeliveryEqps)
                {
                    var minDeliveryArr = attr.CurrentArranges.Where(x => x.EqpID == eqp.EqpID).FirstOrDefault();

                    if (minDeliveryArr != null)
                        lot.CurrentArranges.Add(eqp.EqpID, minDeliveryArr);
                }
            }
            else
                attr.CurrentArranges.ForEach(arr => lot.CurrentArranges.Add(arr.EqpID, arr));

            loadableEqpList = lot.CurrentArranges.Values.Select(x => x.EqpID).ToList();

            if (loadableEqpList.Count > 0)
            {
                lot.IsNoArrangeWait = false;
            }
            else
            {
                lot.LastFilterReason = "No Arrange";
                return null;
            }

            return loadableEqpList;
        }
    }
}