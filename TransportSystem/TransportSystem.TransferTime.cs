using FabSimulator.DataModel;
using Mozart.SeePlan.DataModel;
using Mozart.SeePlan.Simulation;
using Mozart.Simulation.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FabSimulator
{
    public static partial class TransportSystem
    {
        public static double LoadTime = 7d;
        public static double UnloadTime = 8d;
        public static double OhtSpeed = 2.7d;

        internal static Time GetTransferTimeTransport(Location from, Location to)
        {
            if (from == null || to == null)
                return Time.Zero;

            var cellToCell = InputMart.Instance.DELIVERY_TIMEFromToView.FindRows(from.Cell.ID, to.Cell.ID).FirstOrDefault();
            if (cellToCell != null)
                return Time.FromMinutes(cellToCell.DELIVERY_MIN + cellToCell.PENALTY_MIN).Floor();

            var bayToBay = InputMart.Instance.DELIVERY_TIMEFromToView.FindRows(from.Bay.ID, to.Bay.ID).FirstOrDefault();
            if (bayToBay != null)
                return Time.FromMinutes(bayToBay.DELIVERY_MIN + bayToBay.PENALTY_MIN).Floor();

            return GetTransferTimeWithLocation(from, to);
        }

        public static Time GetTransferTimeWithLocation(Location from, Location to)
        {
            if (from.Cell == to.Cell)
                return GetTransferTimeInCell(from, to);

            if (from.Bay == to.Bay)
                return GetTransferTimeInBay(from, to);

            return GetTransferTimeInterBay(from, to);
        }

        private static Time GetTransferTimeInCell(Location from, Location to)
        {
            var movingLength = 0d;
            var fx = from.X;
            var fy = from.Y;
            var tx = to.X;
            var ty = to.Y;

            if (IsLeftCell(from))
                movingLength = (fy > ty) ? (640 - Math.Abs(fy - ty)) : Math.Abs(fy - ty);
            else
                movingLength = (fy > ty) ? Math.Abs(fy - ty) : (640 - Math.Abs(fy - ty));

            var time = (movingLength / OhtSpeed) + LoadTime + UnloadTime;

            return time;
        }

        private static Time GetTransferTimeInBay(Location from, Location to)
        {
            if (IsLeftCell(from) == IsLeftCell(to))
                return GetTransferTimeInCell(from, to);
            else
            {
                var bay = from.Bay;
                var bayTop = bay.Y + 300;
                var bx = bay.X;
                var by = bay.Y;
                var fx = from.X;
                var fy = from.Y;
                var tx = to.X;
                var ty = to.Y;
                var movingLength = IsLeftCell(from) ? (bayTop - fy) + 20 + (bayTop - ty) : (ty - by) + 20 + (fy - by);
                var time = (movingLength / OhtSpeed) + LoadTime + UnloadTime;

                return time;
            }
        }

        private static Time GetTransferTimeInterBay(Location from, Location to)
        {
            var isNext = true;
            var routeLength = 0d;
            var fx = from.X;
            var fy = from.Y;
            var tx = to.X;
            var ty = to.Y;
            var cx = fx;
            var cy = fy;

            var cbay = from.Bay;

            while (isNext)
            {
                var nbay = GetNextBay(cbay, cx, cy, to);

                if (nbay == null) //목적지까지 온 경우
                {
                    var len = GetRouteLengthSameBay(cbay, cx, cy, tx, ty);
                    routeLength += len;
                    isNext = false;
                    continue;
                }
                else
                {
                    // 현재위치에서 to bay 까지 이동후
                    // current 위치를 변경합니다. 
                    var len = GetRouteLengthNextBay(cbay, cx, cy, nbay, out double nextX, out double nextY);
                    routeLength += len;
                    cx = nextX;
                    cy = nextY;
                    cbay = nbay;
                }
            }

            var time = (routeLength / OhtSpeed) + LoadTime + UnloadTime;
            return time;
        }

        private static bool IsLeftCell(Location location)
        {
            return location.Cell.ID.Contains("_L_");
        }

        private static Bay GetNextBay(Bay cbay, double fx, double fy, Location toLocation)
        {
            var tbay = toLocation.Bay;
            if (cbay == tbay)
                return null;

            var tx = toLocation.X;
            var ty = toLocation.Y;

            if (cbay.BayType == BayType.INTERBAY)
            {
                var targetPosX = tx;

                if (tbay.BayType == BayType.INTERBAY)
                {
                    //위로가면 항상 가장 왼쪽 Bay 이용, 아래쪽일때는 오른쪽 Bay 이용
                    targetPosX = (tbay.Y >= cbay.Y) ? 10 : 758;
                }

                if (tbay.Y >= cbay.Y) // 위방향
                {
                    if (cbay.Y == 0)
                        return FindBay("B", targetPosX, toLocation);
                    else
                        return FindBay("A", targetPosX, toLocation);
                }
                else
                {
                    if (cbay.Y == 660)
                        return FindBay("A", targetPosX, toLocation);
                    else
                        return FindBay("B", targetPosX, toLocation);
                }

            }
            else
            {
                if (tbay.Y > cbay.Y) // 위방향
                {
                    if (cbay.Y == 20)
                        return GetBay("InterBay_VMS_2");
                    else
                        return GetBay("InterBay_VMS_3");
                }
                else
                {
                    if (cbay.Y == 20)
                        return GetBay("InterBay_VMS_1");
                    else
                        return GetBay("InterBay_VMS_2");
                }
            }
        }

        private static Bay FindBay(string level, double pos, Location toLocation)
        {
            if (toLocation.Bay.ID.StartsWith(level))
                return toLocation.Bay;

            var bays = Bays.Values.Where(x => x.ID.StartsWith(level));
            foreach (var bay in bays)
            {
                if (bay.X <= pos && bay.X + 44 > pos)
                    return bay;
            }

            return null;
        }

        private static double GetRouteLengthSameBay(Bay bay, double fx, double fy, double tx, double ty)
        {
            var movingLength = 0d;
            var bayTop = bay.Y + 300;

            if (bay.BayType == BayType.INTRABAY)
            {
                if (fx == tx) // 동일 라인
                {
                    if (bay.X == fx) // left side
                        movingLength = (fy > ty) ? (640 - Math.Abs(fy - ty)) : Math.Abs(fy - ty);
                    else
                        movingLength = (fy > ty) ? Math.Abs(fy - ty) : (640 - Math.Abs(fy - ty));
                }
                else
                    movingLength = (bay.X == fx) ? (bayTop - fy) + 20 + (bayTop - ty) : (ty - bay.Y) + 20 + (fy - bay.Y);
            }
            else //interbay (횡축)
            {
                if (fy == ty) // 동일 라인
                {
                    if (bay.Y != fy) // left side
                        movingLength = (fx > tx) ? (1580 - Math.Abs(fx - tx)) : Math.Abs(fx - tx);
                    else
                        movingLength = (fx < tx) ? Math.Abs(fx - tx) : (1580 - Math.Abs(fx - tx));
                }
                else
                    movingLength = (bay.Y != fy) ? (790 - fx) + 20 + (790 - tx) : (tx - bay.X) + 20 + (fx - bay.X);
            }

            return movingLength;
        }

        private static double GetRouteLengthNextBay(Bay bay, double fx, double fy, Bay next, out double nx, out double ny)
        {
            var tx = 0d;
            var ty = 0d;

            if (bay.BayType == BayType.INTRABAY)
            {
                if (bay.Y > next.Y) // 아래로 이동
                {
                    ty = next.Y + 20;
                    tx = (bay.X == fx) ? fx + 22 : fx;
                }
                else // 위로 이동
                {
                    ty = next.Y;
                    tx = (bay.X == fx) ? fx : fx - 22;
                }
            }
            else // intterbay - 횡이동
            {
                if (bay.Y > next.Y) //아래로
                {
                    tx = next.X + 22;
                    ty = next.Y + 310;
                }
                else
                {
                    tx = next.X;
                    ty = next.Y;
                }
            }

            var len = GetRouteLengthSameBay(bay, fx, fy, tx, ty);
            nx = tx;
            ny = ty;

            return len;
        }
    }
}
