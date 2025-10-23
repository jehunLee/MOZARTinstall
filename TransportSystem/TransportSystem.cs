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
using Mozart.Simulation.Engine;
using Mozart.SeePlan.DataModel;
using System.ComponentModel;
using System.Text;
using Mozart.SeePlan;
using Mozart.SeePlan.Semicon.Simulation;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class TransportSystem
    {
        private static Dictionary<string, Bay> bays;
        private static Dictionary<string, Cell> cells;
        private static Dictionary<string, Location> initialLocation;

        public static bool Apply { get { return InputMart.Instance.ApplyTransportSystem; } }

        private static Dictionary<string, Bay> Bays
        {
            get
            {
                if (bays == null)
                    bays = new Dictionary<string, Bay>();

                return bays;
            }
        }
        private static Dictionary<string, Cell> Cells
        {
            get
            {
                if (cells == null)
                    cells = new Dictionary<string, Cell>();

                return cells;
            }
        }

        private static Dictionary<string, Location> InitialLocation
        {
            get
            {
                if (initialLocation == null)
                    initialLocation = new Dictionary<string, Location>();

                return initialLocation;
            }
        }

        public static void AddBay(Bay bay)
        {
            Bays.Add(bay.ID, bay);
        }

        public static Bay GetBay(string id)
        {
            if (Bays.TryGetValue(id, out Bay bay))
                return bay;

            return null;
        }

        public static void AddCell(Cell cell)
        {
            Cells.Add(cell.ID, cell);
        }

        public static Cell GetCell(string id)
        {
            if (Cells.TryGetValue(id, out Cell cell))
                return cell;

            return null;
        }

        public static Bay GetInputStockerBay()
        {
            foreach (var bay in Bays.Values)
            {
                if (bay.StockerBuffers.Count > 0)
                    return bay;
            }

            return null;
        }

        public static void AddInitialLocation(string lotID, Location location)
        {
            InitialLocation.Add(lotID, location);
        }

        public static Location GetInitialLocation(string lotID)
        {
            if (InitialLocation.TryGetValue(lotID, out Location location))
                return location;

            return null;
        }

        public static void SetInitialLocation(ILot lot, bool throwException)
        {
            var initLocation = GetInitialLocation(lot.LotID);
            if (initLocation != null)
                initLocation.SetInitialLot(lot);
            else if (throwException)
                throw new InvalidDataException($"Unable To Locate Wip({lot.LotID})");
        }

        public static void WhereNext(IHandlingBatch hb)
        {
            var lot = hb as FabSemiconLot;

            // Select Destination
            var location = GetNextLocation(hb);

            // Reserve Destination
            location.Reserve(hb);

            DoTransfer(lot);
        }

        private static void DoTransfer(FabSemiconLot lot)
        {
            lot.Location.Detach(lot);

            // Schedule Fill Port/Buffer Event
            AoFactory.Current.Transfer.Take(lot);
        }

        public static Port GetPort(string eqpID, LocationState state)
        {
            var aeqp = AoFactory.Current.GetEquipment(eqpID);
            return GetPort(aeqp, state);
        }

        public static Port GetPort(AoEquipment aeqp, LocationState state)
        {
            if (aeqp == null)
                return null;

            var eqp = aeqp.Target as FabSemiconEqp;
            var feqp = aeqp as FabAoEquipment;
            
            var bay = GetBay(eqp.LocationInfo.Bay);
            if (bay != null)
            {
                var ports = bay.GetPorts(aeqp.EqpID);
                foreach (var port in ports)
                    if (port.State == state)
                        return port;
            }

            return null;
        }

        public static ICollection<Port> GetPorts(AoEquipment aeqp)
        {
            var eqp = aeqp.Target as FabSemiconEqp;
            var bay = GetBay(eqp.LocationInfo.Bay);
            if (bay != null)
            {
                var ports = bay.GetPorts(aeqp.EqpID);
                return ports;
            }

            return null;
        }

        public static ICollection<Port> GetPorts(AoEquipment aeqp, LocationState state)
        {
            var ports = GetPorts(aeqp);
            if (ports == null)
                return null;

            var sports = ports.Where(x => x.State == state).ToList();
            if (state == LocationState.VACANT && sports.Count == 0)
                return null;

            return sports;
        }

        public static ICollection<Port> GetPorts(string eqpID)
        {
            var aeqp = AoFactory.Current.GetEquipment(eqpID);
            return GetPorts(aeqp);
        }

        public static Buffer GetEmptyBuffer(Bay bay)
        {
            return bay.GetEmptySideTrackBuffer();
        }

        public static Buffer GetEmptyBuffer(Bay bay, Cell cell)
        {
            return bay.GetEmptySideTrackBuffer(cell);
        }

        public static Buffer GetEmptyBuffer(string bayID, string cellID)
        {
            var bay = GetBay(bayID);
            var cell = bay.GetCell(cellID);
            return bay.GetEmptySideTrackBuffer(cell);
        }

        public static Buffer GetEmptyBuffer(string bayID)
        {
            var bay = GetBay(bayID);
            if (bay == null)
                throw new ArgumentOutOfRangeException($"Unable To Find Empty buffer: Bay({bayID}) does not exists.");

            return GetEmptyBuffer(bay);
        }

        public static Buffer GetEmptyBuffer()
        {
            foreach (var bay in Bays.Values)
            {
                var buffer = GetEmptyBuffer(bay);
                if (buffer != null)
                    return buffer;
            }

            return null;
        }

        private static int count = 0;

        private static Location GetNextLocation(IHandlingBatch hb)
        {
            count++;

            var loadables = ResourceHelper.GetLoadableEqpList(hb, false);

            if (loadables != null)
            {
                if (count % 2 == 1)
                    loadables.Reverse();

                #region Buffer Condition 1

                foreach (var eqpID in loadables)
                {
                    var eqp = AoFactory.Current.GetEquipment(eqpID).Target as FabSemiconEqp;
                    var buffer = GetEmptyBuffer(eqp.LocationInfo.Bay, eqp.LocationInfo.Cell);
                    if (buffer != null)
                    {
                        return buffer;
                    }
                }

                #endregion

                #region Buffer Condition 2

                foreach (var eqpID in loadables)
                {
                    var eqp = AoFactory.Current.GetEquipment(eqpID).Target as FabSemiconEqp;
                    var buffer = GetEmptyBuffer(eqp.LocationInfo.Bay);
                    if (buffer != null)
                    {
                        return buffer;
                    }
                }

                #endregion
            }

            #region Buffer Condition 3

            var abuffer = GetEmptyBuffer();
            return abuffer;

            #endregion
        }

        public static IHandlingBatch DoCustomDispatching(FabAoEquipment feqp, CustomDispatchType type, Port targetPort = null)
        {
            var customDispatchingKey = type.ToString();
            var preset = feqp.Eqp.PresetDict.SafeGet(customDispatchingKey) ?? feqp.Eqp.Preset as FabWeightPreset;

            WeightEvaluator eval = new WeightEvaluator(feqp, preset);
            if (preset.DispatcherType == DispatcherType.WeightSum)
                eval.Comparer = new WeightSumComparer();

            DispatchContext ctx = new DispatchContext();
            CustomDispatchInfo info = new CustomDispatchInfo();
            info.DispatchType = type;
            ctx.Set("CUSTOM_INFO", info);

            List<IHandlingBatch> rawCandidates = new List<IHandlingBatch>();
            if (type == CustomDispatchType.JOB_PREP)
                rawCandidates = feqp.JobPrepCandidates;
            else if (type == CustomDispatchType.PORT_RSV)
                rawCandidates = feqp.JobPrepLotList;

            info.PortID = targetPort?.ID;
            info.InitialWipCount = rawCandidates.Count;

            var filterManager = AoFactory.Current.Filters;
            List<IHandlingBatch> filterCandidates = new List<IHandlingBatch>();
            //var methods = filterManager.GetMethods(customDispatchingKey);
            //bool tryCustomFilter = methods.Count() > 0;

            foreach (var hb in rawCandidates)
            {
                if (filterManager.Filter(customDispatchingKey, hb, feqp.NowDT, feqp, ctx))
                {
                    //TODO: FilterLog 필요시 Factor 내에서 CUSTOM_INFO에 담도독 구현
                    info.FilteredWipCount++;
                    continue;
                }

                filterCandidates.Add(hb);
            }

            if (filterCandidates.IsNullOrEmpty())
                return null;

            //Weight Factor를 위한 추가 세팅
            WeightHelper.SetDispatchContext(ctx, feqp, preset);

            List<IHandlingBatch> evaluated = eval.Evaluate(filterCandidates, ctx) as List<IHandlingBatch>;

            var selected = evaluated.First();

            WriteCustomDispatchLog(feqp, selected, info);

            return selected;
        }

        public static void TryFillEmptyPort(FabAoEquipment feqp)
        {
            var emptyPorts = GetPorts(feqp, LocationState.VACANT);
            if (emptyPorts == null)
                return;

            foreach (var port in emptyPorts)
            {
                bool selected = SelectJobPrepToPort(feqp, port);

                if (selected == false)
                    break;
            }
        }

        internal static void TryJobPreparation(List<FabAoEquipment> availableEqps)
        {
            // JobPrep이 도입되면서, 설비별 예약 균형을 맞추지 않으면 물량이 많이 저하됨

            foreach (var feqp in availableEqps.OrderBy(x => x.JobPrepLotList.Count))
            {
                if (feqp.IsBatchType())
                {
                    // Arrange에 SimType 종류가 섞이면, Batch설비가 기회를 잃는 문제가 발생해서 호출 추가.
                    BatchingContext ctx = new BatchingContext();
                    ctx.EventType = BatchingEventType.AtStepLotArrival.ToString();
                    BatchingManager.BuildAndSelect(feqp, ctx);

                    continue;
                }

                if (NeedJobPreparation(feqp))
                {
                    DoJobPreparation(feqp);

                    TryFillEmptyPort(feqp);
                }
            }
            }

        public static bool NeedJobPreparation(FabAoEquipment feqp)
        {
            if (feqp.IsBatchType() && feqp.ReservedBatch != null)
                return false;

            var needPrepare = feqp.JobPrepLotList.Count < Math.Round(feqp.Eqp.Ports.Count * 1.5);
            var hasCandidates = feqp.JobPrepCandidates.IsNullOrEmpty() == false;

            return needPrepare && hasCandidates;
        }

        private static void DoJobPreparation(FabAoEquipment feqp)
        {
            var selected = DoCustomDispatching(feqp, CustomDispatchType.JOB_PREP);
            
            DoneJobPreparation(feqp, selected);
        }

        public static void DoneJobPreparation(FabAoEquipment feqp, IHandlingBatch selected)
        {
            if (selected == null)
                return;

            feqp.JobPrepLotList.Add(selected);

            var lot = selected as FabSemiconLot;
            RemoveJobPrepCandidates(lot);
        }

        internal static void AddJobPrepCandidates(FabSemiconLot lot, List<FabAoEquipment> arrangedEqps)
        {
            foreach (var feqp in arrangedEqps)
            {
                if (feqp.Eqp.HasPort && feqp.JobPrepCandidates.Contains(lot) == false)
                    feqp.JobPrepCandidates.Add(lot);
            }
        }

        internal static void RemoveJobPrepCandidates(FabSemiconLot lot)
        {
            foreach (var feqp in lot.CurrentArranges.Values.Select(x => x.Eqp.SimObject))
            {
                if (feqp.Eqp.HasPort)
                {
                    feqp.JobPrepCandidates.Remove(lot);
                }
            }
        }

        internal static bool SelectJobPrepToPort(FabAoEquipment feqp, Port targetPort)
        {
            IHandlingBatch selected = DoSelectJobPrepToPort(feqp, targetPort);
            if (selected == null)
                return false;

            foreach (var entity in selected)
            {
                var lot = EntityHelper.GetLot(entity);
                ReservePort(lot, targetPort);
            }

            return true;
        }

        private static IHandlingBatch DoSelectJobPrepToPort(FabAoEquipment feqp, Port targetPort)
        {
            if (feqp.JobPrepLotList.IsNullOrEmpty())
                return null;

            if (feqp.IsBatchType())
            {
                return feqp.ReservedBatch;
            }
            else
            {
                var selected = DoCustomDispatching(feqp, CustomDispatchType.PORT_RSV, targetPort);
                return selected;
            }
        }

        private static void ReservePort(FabSemiconLot lot, Port targetPort)
        {
            targetPort.Reserve(lot);
            DoTransfer(lot);

            targetPort.Eqp.JobPrepLotList.Remove(lot);
        }

        internal static void SetInitialTransferInfo(FabSemiconLot lot)
        {
            if (TransportSystem.Apply == false)
                return;

            var wip = lot.FabWipInfo;
            var locType = wip.WipParamDict.SafeGet("location_type");
            var locBayID = wip.WipParamDict.SafeGet("location_bay_id");
            var locID = wip.WipParamDict.SafeGet("location_id");
            if (locType == null || locBayID == null || locID == null)
                return;

            var bay = GetBay(locBayID);
            if (bay == null)
                return;

            var locationType = Helper.Parse<LocationType>(locType, default(LocationType));

            // MOVE -> Location Reserved
            // RUN or STAGED or WAIT -> Location Occupied
            var locationState = wip.CurrentState == EntityState.MOVE ? LocationState.RESERVED : LocationState.OCCUPIED;

            if (locationType == LocationType.BUFFER)
            {
                var buffer = bay.GetBuffer(locID);
                if (buffer != null)
                {
                    var info = new InitialLocationInfo(wip.LotID, locationState, wip.WipStateTime);
                    buffer.SetInitialInfo(info);
                }
            }
            else if (locationType == LocationType.PORT)
            {
                var eqpID = bay.GetPortEqpID(locID);
                if (!StringUtility.IsEmptyID(eqpID))
                {
                    var port = bay.GetPort(eqpID, locID);
                    if (port != null)
                    {
                        var info = new InitialLocationInfo(wip.LotID, locationState, wip.WipStateTime);
                        port.SetInitialInfo(info);
                    }
                }
            }
        }
    }
}