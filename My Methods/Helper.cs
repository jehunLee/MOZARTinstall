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
using Mozart.SeePlan.DataModel;
using Mozart.Text;
using System.Data.SqlTypes;
using Mozart.SeePlan.Semicon.Simulation;
using Mozart.SeePlan.Semicon.DataModel;
using System.Globalization;
using Mozart.SeePlan;
using Mozart.Data.Validation;
using System.Reflection;
using System.Diagnostics;
using System.Numerics;

namespace FabSimulator
{
    [FeatureBind()]
    public static partial class Helper
    {
        public static T Parse<T>(string input, T defaultValue) where T : struct
        {
            // Enum만 가능
            T value;

            if (typeof(T).IsEnum)
                return EnumParse<T>(input, defaultValue);

            if (!Enum.TryParse<T>(input, out value))
                return defaultValue;

            return value;
        }

        public static int IntParse(string input, int defaultValue)
        {
            int result;

            if (Int32.TryParse(input, out result))
                return result;

            return defaultValue;
        }

        public static float FloatParse(string input, float defaultValue)
        {
            float result;

            if (float.TryParse(input, out result))
                return result;

            return defaultValue;
        }

        public static double DoubleParse(string input, double defaultValue)
        {
            double result;

            if (double.TryParse(input, out result))
                return result;

            return defaultValue;
        }


        private static T EnumParse<T>(string input, T defaultValue) where T : struct
        {
            T value;
            if (!Enum.TryParse(input, true, out value))
                return defaultValue;

            return value;
        }

        public static DateTime Min(DateTime x, DateTime y)
        {
            return x < y ? x : y;
        }
        public static DateTime Max(DateTime x, DateTime y)
        {
            return x > y ? x : y;
        }

        public static TimeSpan Min(TimeSpan x, TimeSpan y)
        {
            return x < y ? x : y;
        }

        public static TimeSpan Max(TimeSpan x, TimeSpan y)
        {
            return x > y ? x : y;
        }

        internal static string CreateKey(params string[] args)
        {
            return string.Join("@", args);
        }

        internal static string CreateKey2(params string[] args)
        {
            return string.Join("/", args);
        }

        private static Random GetRandomNumberGenerator(RNGType rngType)
        {
            if (rngType == RNGType.Isolation)
            {
                return InputMart.Instance.RandomNumberGenerator2;
            }

            return InputMart.Instance.RandomNumberGenerator;
        }

        internal static bool GetBernoulliTrialResult(double p, RNGType rngType = 0)
        {
            var rng = GetRandomNumberGenerator(rngType);
            double r = rng.NextDouble();

            return r < p;
        }

        internal static double GetExpDistributionRandomNumber(double lamda, RNGType rngType = 0)
        {
            var rng = GetRandomNumberGenerator(rngType);

            //Random r = new Random(Guid.NewGuid().GetHashCode());
            double u = rng.NextDouble();
            double x = -Math.Log(u) / lamda;

            return x;
        }

        internal static double GetUniformDistributionRandomNumberWithMargin(double mean, double margin, RNGType rngType = 0)
        {
            var rng = GetRandomNumberGenerator(rngType);
            double u = rng.NextDouble();

            var a = mean * (1 - margin);
            var b = mean * (1 + margin);

            double x = a + (u * (b - a));

            return x;
        }
        internal static double GetUniformDistributionRandomNumber(double min, double max, RNGType rngType = 0)
        {
            var rng = GetRandomNumberGenerator(rngType);
            double u = rng.NextDouble();

            double x = min + (u * (max - min));

            return x;
        }

        internal static double GetNormalDistributionRandomNumber(double mean, double sd, RNGType rngType = 0)
        {
            var rng = GetRandomNumberGenerator(rngType);
            double u = rng.NextDouble();

            double x = mean + (sd * Math.Sqrt(2) * ErfInv(2 * u - 1));

            return x;
        }

        public static double ErfInv(double x)
        {
            //Quick & dirty, tolerance under +-6e-3.Work based on "A handy approximation for the error function and its inverse" by Sergei Winitzki.

            double tt1, tt2, lnx, sgn;
            sgn = (x < 0) ? -1.0d : 1.0d;

            x = (1 - x) * (1 + x);        // x = 1 - x*x;
            lnx = Math.Log(x);

            tt1 = 2 / (Math.PI * 0.147) + 0.5f * lnx;
            tt2 = 1 / (0.147) * lnx;

            return (sgn * Math.Sqrt(-tt1 + Math.Sqrt(tt1 * tt1 - tt2)));
        }

        public static DateTime GetMSSqlDateTime(this DateTime x)
        {
            DateTime minValue = SqlDateTime.MinValue.Value;

            return Max(x, minValue);
        }

        public static string GetScenarioID()
        {
            return InputMart.Instance.GlobalParameters.scenarioID;
        }

        public static List<int> GetSimulationStepLevels()
        {
            string param = Helper.GetConfig(ArgsGroup.Bop_Step).applyStepLevel;
            List<int> stepLevels = param.Split(',').Select(x => Int32.Parse(x)).ToList();

            return stepLevels;

            //return Helper.GetConfig(ArgsGroup.Bop_Step).applyStepLevel;
        }

        public static ConfigParameters GetConfig(ArgsGroup key)
        {
            return InputMart.Instance.GetConfigParameters(key.ToString());
        }
        public static DateTime RoundUp(DateTime dt, TimeSpan d)
        {
            return new DateTime((dt.Ticks + d.Ticks - 1) / d.Ticks * d.Ticks, dt.Kind);
        }

        public static void ClearBatchingDataMemory(LotBatch batch)
        {
            if (batch.BatchingData == null)
                return;

            if (batch.BatchingData.RemainCandidates != null)
                batch.BatchingData.RemainCandidates.Clear();

            if (batch.BatchingData.Attributes != null)
                batch.BatchingData.Attributes.Clear();

            batch.BatchingData.RemainCandidates = null;
            batch.BatchingData.Attributes = null;
        }

        internal static void ClearFabPlanInfoMemory(FabSemiconLot lot)
        {
            if (lot.CurrentFabPlan.ArrivalTimeDict != null)
                lot.CurrentFabPlan.ArrivalTimeDict.Clear();

            if (lot.CurrentFabPlan.DeliveryDict != null)
                lot.CurrentFabPlan.DeliveryDict.Clear();

            lot.CurrentFabPlan.ArrivalTimeDict = null;
            lot.CurrentFabPlan.DeliveryDict = null;
        }
        internal static void ClearLotBatchMemory(LotBatch batch)
        {
            foreach (SemiconLot lot in batch.Contents)
            {
                var eta = lot as LotETA;
                if (eta == null)
                    continue;

                eta.Lot = null;
                eta.Batch = null;
            }
            batch.Contents.Clear();

            ClearBatchingDataMemory(batch);
        }

        internal static void ClearLotCollectionMemory(FabSemiconLot lot)
        {
            lot.Plans.Clear();

            if (lot.ReservationInfos.IsNullOrEmpty() == false)
            {
                foreach (var item in lot.ReservationInfos.Values)
                {
                    ClearLotBatchMemory(item.Batch);
                    item.Batch = null;
                }
            }

            if (lot.ReservationInfos.IsNullOrEmpty() == false)
            {
                lot.ReservationInfos.Clear();
                lot.ReservationInfos = null;
            }

            if (lot.CurrentArranges.IsNullOrEmpty() == false)
            {
                lot.CurrentArranges.Clear();
                lot.CurrentArranges = null;
            }

            if (lot.TargetStepArranges.IsNullOrEmpty() == false)
            {
                lot.TargetStepArranges.Clear();
                lot.TargetStepArranges = null;
            }
            
            if (lot.ActiveStackDict.IsNullOrEmpty() == false)
            {
                lot.ActiveStackDict.Clear();
                lot.ActiveStackDict = null;
            }
        }

        internal static int GetTargetYear(DateTime targetDate)
        {
            var year = targetDate.Year;
            var targetWeek = GetTargetWeek(targetDate);
            if (targetWeek == 52 && targetDate.Month == 1)
                year--;

            return year;
        }

        /// <summary>
        /// <para>
        /// 요약:
        ///     해당 주의 공장 시작시간을 반환하는 함수
        /// </para><para>
        /// 설명:
        ///     작업 시작 시간이 월요일이고 start-time이 6시일 경우
        ///     2020/01/10(월요일) 06:00:00 => 2020/01/10 06:00:00,
        ///     2020/01/11(화요일) 06:00:00 => 2020/01/10 06:00:00,
        ///     2020/01/10(월요일) 04:00:00 => 2020/01/03(월요일) 06:00:00,
        ///     2020/01/10(월요일) 09:00:00 => 2020/01/10 06:00:00,
        /// </para></summary>
        public static DateTime GetWeekStartTime(this DateTime dt)
        {
            var adjustedDate = ShopCalendar.StartTimeOfDayT(dt);

            int diff = (7 + (adjustedDate.DayOfWeek - ShopCalendar.StartWeek)) % 7;

            return adjustedDate.AddDays(-1 * diff);
        }

        /// <summary>
        /// <para>
        /// 요약:
        ///     해당 달의 공장 시작시간을 반환하는 함수
        /// </para><para>
        /// 설명:
        ///     공장 시작시간이 4시이고 매개변수가 2015/05/22 05:00:00 이면 return 값은 2015/05/01 04:00:00
        /// </para></summary>
        public static DateTime GetMonthStartTime(this DateTime dt)
        {
            // dt가 7월 12일 이라면 7월 1일의 시작 시간으로 보내자.
            return dt.AddDays(1 - dt.Day).Date + ShopCalendar.StartTime;
        }

        internal static int GetTargetWeek(DateTime targetDate)
        {
            CultureInfo myCI = new CultureInfo("en-US");
            Calendar myCal = myCI.Calendar;

            return myCal.GetWeekOfYear(targetDate, CalendarWeekRule.FirstFullWeek, ShopCalendar.StartWeek);
        }

        internal static DateTime GetTargetDate(DateTime nowDT, bool applyMargin = false)
        {
            // 경계에 속할경우 어느구간에 포함시킬건지 결정 필요 (일변경시점에 딱 맞춰 끝나는 경우 StepMove 집계시 키중복을 유발)
            // 앞구간에 포함하기 위해 - 1초 처리 
            if (applyMargin)
                nowDT = nowDT.AddSeconds(-1);

            // SIM_CONFIG/factoryTime: start_time=06; 일 때,
            // StartTimeOfDay => Day값은 변경 없이 공장 시작시간을 반환하는 함수 => 2015/05/22 05:00:00 ==> 2015/05/22 06:00:00
            // StartTimeOfDayT => factoryTime을 고려하여 공장 시작시간을 반환하는 함수 => 2015/05/22 05:00:00 ==> 2015/05/21 06:00:00
            return ShopCalendar.StartTimeOfDayT(nowDT);
        }

        internal static List<int> GetWipMoveCollectStepLevels()
        {
            string param = Helper.GetConfig(ArgsGroup.Simulation_Output).writeStepWipMove;
            List<int> stepLevels = param.Split(',').Select(x => Int32.Parse(x)).ToList();

            return stepLevels;
        }

        public static void SafeAdd(this MultiDictionary<string, FabSemiconLot> dict, string stackGroupID, FabSemiconLot lot)
        {
            if (dict.ContainsKey(stackGroupID) && dict[stackGroupID].Contains(lot))
                return;

            dict.Add(stackGroupID, lot);
        }

        public static DateTime GetLinkerTime(this Assembly assembly)
        {
            const string BuildVersionMetadataPrefix = "+build";

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute?.InformationalVersion != null)
            {
                var value = attribute.InformationalVersion;
                var index = value.IndexOf(BuildVersionMetadataPrefix);
                if (index > 0)
                {
                    value = value.Substring(index + BuildVersionMetadataPrefix.Length);
                    if (DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                    {
                        return result;
                    }
                }
            }

            return default;
        }

        internal static double GetDurationHoursWithChar(string str)
        {
            try
            {
                var unitChar = str.Last();
                var durationStr = str.Remove(str.Length - 1, 1);
                double durationHrs = 0;

                if (float.TryParse(durationStr, out float durationFloat))
                {
                    if (unitChar == 'y')
                        durationHrs = durationFloat * 365 * 24;
                    else if (unitChar == 'M')
                        durationHrs = durationFloat * 30 * 24;
                    else if (unitChar == 'w')
                        durationHrs = durationFloat * 7 * 24;
                    else if (unitChar == 'd')
                        durationHrs = durationFloat * 24;
                    else if (unitChar == 'h')
                        durationHrs = durationFloat;
                    else if (unitChar == 'm')
                        durationHrs = durationFloat / 60f;
                    else if (unitChar == 's')
                        durationHrs = durationFloat / 60f / 60f;
                }

                return durationHrs;
            }
            catch (Exception e)
            {
                string methodname = new StackTrace().GetFrame(0).GetMethod().Name;
                e.WriteExceptionLog(methodname, "-", "-");
                return 0;
            }
        }

        internal static string GetFormattedTargetWeek(DateTime targetDate)
        {
            var year = targetDate.Year;
            var targetWeek = GetTargetWeek(targetDate);
            if (targetWeek == 52 && targetDate.Month == 1)
                year--;

            return year + "-" + targetWeek.ToString().PadLeft(2, '0');
        }

        internal static string GetFormattedTargetMonth(DateTime targetDate)
        {
            return targetDate.Year + "-" + targetDate.Month.ToString().PadLeft(2, '0');
        }

        internal static double GetDistributionRandomNumber(StochasticConfig pdfConfig, RNGType rngType = 0, bool positiveOnly = true)
        {
            double r = 0;

            if (pdfConfig.DistFunction == DistFunctionType.Normal)
            {
                r = GetNormalDistributionRandomNumber(pdfConfig.Mean, pdfConfig.StdDev, rngType);
            }
            else if (pdfConfig.DistFunction == DistFunctionType.Exponential)
            {
                r = GetExpDistributionRandomNumber(1.0 / pdfConfig.Mean, rngType);
            }
            else if (pdfConfig.DistFunction == DistFunctionType.Uniform)
            {
                r = GetUniformDistributionRandomNumber(pdfConfig.Min, pdfConfig.Max, rngType);
            }
            else
            {
                r = pdfConfig.Mean;
            }

            if (positiveOnly)
                r = Math.Max(r, 0);

            return r;
        }
        public static DateTime Floor(this DateTime t)
        {
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
        }

        public static double GetValidRate(double rate)
        {
            // 0과 1사이의 값을 보장하고 싶을 때 사용.
            
            if (rate <= 0 || rate > 1 || double.IsNaN(rate))
                return 1; // 예외의 경우 일괄적으로 1을 리턴

            return rate;
        }

        public static int ExtractNumber(string str)
        {
            if (str == null)
                return 0;

            string result = "";
            foreach(char c in str)
            {
                if (c >= '0' && c <= '9')
                    result += c;
            }
            return result == "" ? 0 : Convert.ToInt32(result);
        }

        public static string GetVarchar255(string str)
        {
            // WorkerService에 DB Insert 구문이 있어서, 모델의 Query는 의미가 없음.
            // vdat 생성시부터 길이 제한이 필요.

            var length = Math.Min(str.Length, 255);

            return str.Substring(0, length);
        }

        /// <summary>
        /// <para>
        /// 요약:
        ///     문자열 시간을 소수 형태의 시각으로 변환하는 함수
        /// </para><para>
        /// 설명:
        ///     start-time이 06일 경우 => 6,
        ///     start-time이 0630일 경우 => 6.5
        /// </para></summary>
        public static bool GetTimeAsFractionalHours(string str, out double result)
        {
            if (str.Length == 4)
            {
                string hours = str.Substring(0, 2);
                string minutes = str.Substring(2, 2);
                result = Convert.ToDouble(hours) + Convert.ToDouble(minutes) / 60;
                return true;
            }
            else if (str.Length == 3)
            {
                string hours = str.Substring(0, 1);
                string minutes = str.Substring(1, 2);
                result = Convert.ToDouble(hours) + Convert.ToDouble(minutes) / 60;
                return true;
            }
            else if (str.Length > 0 && str.Length < 3)
            {
                result = Convert.ToDouble(str);
                return true;
            }
            result = 0;
            return false;
        }
    }
}