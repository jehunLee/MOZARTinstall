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

namespace FabSimulator
{
    [FeatureBind()]
    public class Bay
    {
        private Dictionary<string, Cell> cells;

        private Dictionary<string, Buffer> sideTrackBuffers;

        private Dictionary<string, Buffer> stockerBuffers;

        private MultiDictionary<string, Port> ports;

        private Dictionary<string, string> portEqpIDDic;

        public string ID { get; private set; }

        public BayType BayType { get; set; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public Dictionary<string, Cell> Cells {  get { return this.cells; } }

        public Dictionary<string, Buffer> SideTrackBuffers { get { return this.sideTrackBuffers; } }

        public Dictionary<string, Buffer> StockerBuffers { get { return this.stockerBuffers; } }

        public MultiDictionary<string, Port> Ports { get { return this.ports; } }

        public Dictionary<string, string> PortEqpIDDic { get { return this.portEqpIDDic; } }

        public Bay(string id, BayType type, double x, double y)
        {
            this.ID = id;
            this.BayType = type;
            this.X = x;
            this.Y = y;
            this.cells = new Dictionary<string, Cell>();
            this.sideTrackBuffers = new Dictionary<string, Buffer>();
            this.stockerBuffers = new Dictionary<string, Buffer>();
            this.ports = new MultiDictionary<string, Port>();
            this.portEqpIDDic = new Dictionary<string, string>();
        }

        public void AddCell(Cell cell)
        {
            this.Cells.Add(cell.ID, cell);
        }

        public Cell GetCell(string id)
        {
            if (this.cells.TryGetValue(id, out Cell cell))
                return cell;

            return null;
        }

        public void AddBuffer(Buffer buffer)
        {
            if (buffer.Bay != this)
                throw new ArgumentOutOfRangeException($"Unable To Add Buffer: Buffer({buffer.ID}) is not located in Bay({this.ID})");

            if (buffer.BufferType == BufferType.STB)
                this.SideTrackBuffers.Add(buffer.ID, buffer);
            else if (buffer.BufferType == BufferType.STK)
                this.StockerBuffers.Add(buffer.ID, buffer);
        }

        public void AddPort(Port port)
        {
            if(port.Bay != this)
                throw new ArgumentOutOfRangeException($"Unable To Add Port: Port({port.ID}) is not located in Bay({this.ID})");

            this.Ports.Add(port.EqpID, port);
            this.PortEqpIDDic.Add(port.ID, port.EqpID);
        }

        public Buffer GetBuffer(string id, BufferType type = BufferType.ANY)
        {
            Buffer buffer = null;
            if (type == BufferType.ANY || type == BufferType.STB)
                buffer = this.GetSideTrackBuffer(id);

            if ((type == BufferType.ANY && buffer == null) || type == BufferType.STK)
                buffer = this.GetStockerBuffer(id);

            return buffer;
        }

        public Buffer GetSideTrackBuffer(string id)
        {
            if (this.SideTrackBuffers.TryGetValue(id, out Buffer buffer))
                return buffer;

            return null;
        }

        public Buffer GetStockerBuffer(string id)
        {
            if (this.StockerBuffers.TryGetValue(id, out Buffer buffer))
                return buffer;

            return null;
        }

        public Buffer GetEmptySideTrackBuffer()
        {
            foreach (var buffer in this.SideTrackBuffers.Values)
            {
                if (buffer.State == LocationState.VACANT)
                    return buffer;
            }

            return null;
        }

        public Buffer GetEmptySideTrackBuffer(Cell cell)
        {
            foreach (var buffer in this.SideTrackBuffers.Values)
            {
                if (buffer.Cell != cell)
                    continue;

                if (buffer.State == LocationState.VACANT)
                    return buffer;
            }

            return null;
        }

        public Buffer GetEmptyStockerBuffer()
        {
            foreach (var buffer in this.StockerBuffers.Values)
            {
                if (buffer.State == LocationState.VACANT)
                    return buffer;
            }

            return null;
        }

        public Port GetPort(string eqpID, string id)
        {
            var ports = this.GetPorts(eqpID);
            if (ports != null)
                return ports.FirstOrDefault(x => x.ID == id);

            return null;
        }

        public ICollection<Port> GetPorts(string eqpID)
        {
            if (this.Ports.TryGetValue(eqpID, out ICollection<Port> ports))
                return ports;

            return null;
        }

        public string GetPortEqpID(string portID)
        {
            if (this.PortEqpIDDic.TryGetValue(portID, out string eqpID))
                return eqpID;

            return null;
        }
    }
}