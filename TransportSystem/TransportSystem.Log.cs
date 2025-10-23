using FabSimulator.DataModel;
using FabSimulator.Outputs;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using Mozart.Task.Execution;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FabSimulator
{
    public static partial class TransportSystem
    {
        public static void WriteTransferLog(IHandlingBatch hb, Location toLocation, Time moveTime)
        {
            var lot = hb.Sample as FabSemiconLot;

            var row = new TRANSFER_LOG();

            row.SCENARIO_ID = InputMart.Instance.ScenarioID;
            row.VERSION_NO = ModelContext.Current.VersionNo;
            row.LINE_ID = lot.LineID;
            row.LOT_ID = lot.LotID;
            row.PART_ID = lot.CurrentPartID;
            row.ROUTE_ID = lot.CurrentProcessID;
            row.STEP_ID = lot.CurrentStepID;
            row.EVENT_TYPE = "TRANSFER";
            row.TRANSFER_START_TIME = AoFactory.Current.NowDT;

            var fromLocation = lot.LastLocation;    // 이미 이전 Location에서 Detach 된 상태
            if (fromLocation != null)
            {
                row.FROM_BAY_ID = fromLocation.Bay.ID;
                row.FROM_CELL_ID = fromLocation.Cell.ID;
                row.FROM_LOCATION_ID = fromLocation.ID;
                row.FROM_LOCATION_TYPE = fromLocation.LocationType.ToString();
            }

            row.TO_BAY_ID = toLocation.Bay.ID;
            row.TO_CELL_ID = toLocation.Cell.ID;
            row.TO_LOCATION_ID = toLocation.ID;
            row.TO_LOCATION_TYPE = toLocation.LocationType.ToString();

            row.TRANSFER_TIME_MIN = moveTime.TotalMinutes;

            OutputMart.Instance.TRANSFER_LOG.Add(row);
        }

        public static void WriteCustomDispatchLog(FabAoEquipment feqp, IHandlingBatch selected, CustomDispatchInfo info)
        {
            CUSTOM_DISPATCH_LOG log = new CUSTOM_DISPATCH_LOG();

            log.SCENARIO_ID = InputMart.Instance.ScenarioID;
            log.VERSION_NO = ModelContext.Current.VersionNo;

            log.DISPATCH_TYPE = info.DispatchType.ToString();

            log.EQP_ID = feqp.EqpID;
            log.SIM_TYPE = feqp.Eqp.SimType.ToString();
            log.PORT_ID = info.PortID;

            log.EVENT_TIME = feqp.NowDT;

            log.INIT_WIP_COUNT = info.InitialWipCount;
            log.FILTERED_WIP_COUNT = info.FilteredWipCount;
            log.FILTERED_WIP_LOG = info.FilteredWipLog;

            if (selected != null)
            {
                var lot = selected as FabSemiconLot;
                log.SELECTED_WIP_COUNT = 1;
                log.SELECTED_WIP = lot.LotID;
                log.DISPATCH_WIP_LOG = GetCustomDispatchWipLog(feqp, info);
            }

            OutputMart.Instance.CUSTOM_DISPATCH_LOG.Add(log);
        }

        private static string GetCustomDispatchWipLog(FabAoEquipment feqp, CustomDispatchInfo info)
        {
            var sb = StringBuilderCache.Acquire();

            List<IHandlingBatch> candidates = info.DispatchType == CustomDispatchType.JOB_PREP ? feqp.JobPrepCandidates : feqp.JobPrepLotList;

            foreach (var hb in candidates)
            {
                var lot = hb as FabSemiconLot;

                sb.Append(lot.LotID);
                sb.Append('/' + lot.FabProduct.PartID);
                sb.Append('/' + lot.CurrentStepID);
                sb.Append('/' + lot.Location.ID);
                sb.Append('/');
                sb.Append(lot.UnitQty);

                var wp = info.Preset;
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
                sb.Append(";");
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }
    }
}
