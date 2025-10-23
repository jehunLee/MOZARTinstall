using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mozart.Task.Execution;

namespace FabSimulator
{
    public class ElapsedTimeChecker
    {
        public ElapsedTimeChecker()
        {
            this.elapsedTimeByType = new Dictionary<string, long>();
            this.calledCountByType = new Dictionary<string, int>();
        }

        public static ElapsedTimeChecker Instance
        {
            get
            {
                return ServiceLocator.Resolve<ElapsedTimeChecker>();
            }
        }

        /// <summary>   Type of the elapsed time by. </summary>
        Dictionary<string, long> elapsedTimeByType;
        /// <summary>   Type of the called count by. </summary>
        Dictionary<string, int> calledCountByType;
        /// <summary>   Set the timer belongs to. </summary>
        Dictionary<string, System.Diagnostics.Stopwatch> timerSet = new Dictionary<string, System.Diagnostics.Stopwatch>();
        
        private System.Diagnostics.Stopwatch GetTimer(string typeKey)
        {
            //if (MicronInputMart.Instance.IsThreadedEvaluatingNow)
            //    return null;

            if (typeKey.StartsWith("*"))
                return null;

            System.Diagnostics.Stopwatch timer;

            if (!this.timerSet.TryGetValue(typeKey, out timer))
                this.timerSet.Add(typeKey, timer = new System.Diagnostics.Stopwatch());

            return timer;
        }

        public void ResetTimer(string typeKey)
        {
            //if (MicronInputMart.Instance.GlobalParameters.DoRunTimeCheck == false)
            //    return;

            //if (MicronInputMart.Instance.IsThreadedEvaluatingNow || MicronInputMart.Instance.IsThreadedIsLoadableNow)
            //    return;

            //if (HasRunningTimer(typeKey))
            //    return;

            var timer = GetTimer(typeKey);
            if (timer == null)
                return;

            timer.Reset();
            timer.Start();
        }

        public void AddElapsedTime(string typeKey)
        {
            //if (MicronInputMart.Instance.GlobalParameters.DoRunTimeCheck == false)
            //    return;

            //if (MicronInputMart.Instance.IsThreadedEvaluatingNow || MicronInputMart.Instance.IsThreadedIsLoadableNow)
            //    return;

            var timer = GetTimer(typeKey);
            if (timer == null)
                return;

            if (timer.IsRunning == false)
                return;

            timer.Stop();

            if (this.elapsedTimeByType.ContainsKey(typeKey) == false)
            {
                this.elapsedTimeByType.Add(typeKey, 0);
                this.calledCountByType.Add(typeKey, 0);
            }

            this.elapsedTimeByType[typeKey] += timer.ElapsedTicks;
            this.calledCountByType[typeKey]++;

            timer.Reset();
        }

        public void ResetTimerforMultiThreading(string typeKey)
        {
            System.Diagnostics.Stopwatch timer;

            if (!this.timerSet.TryGetValue(typeKey, out timer))
                this.timerSet.Add(typeKey, timer = new System.Diagnostics.Stopwatch());

            if (timer == null)
                return;

            timer.Reset();
            timer.Start();
        }

        public void AddElapsedTimeforMutiThreading(string typeKey)
        {
            System.Diagnostics.Stopwatch timer;

            if (!this.timerSet.TryGetValue(typeKey, out timer))
                this.timerSet.Add(typeKey, timer = new System.Diagnostics.Stopwatch());

            if (timer == null)
                return;

            timer.Stop();

            if (this.elapsedTimeByType.ContainsKey(typeKey) == false)
            {
                this.elapsedTimeByType.Add(typeKey, 0);
                this.calledCountByType.Add(typeKey, 0);
            }

            this.elapsedTimeByType[typeKey] += timer.ElapsedTicks;
            this.calledCountByType[typeKey]++;

            timer.Reset();
        }

        public void PrintElapsedTimes() //WriteElapsedTimes()
        {
            Logger.MonitorInfo("\t####     Analysis ElapsedTime     ####");

            float freq = System.Diagnostics.Stopwatch.Frequency / 10000000f;

            foreach (KeyValuePair<string, long> entry in this.elapsedTimeByType)
            {
                TimeSpan timeSpan = new TimeSpan((long)(entry.Value / freq));

                Logger.MonitorInfo(string.Format(
                    "\t\t+ {0} \tElapsed Time = {1}, CalledCount = {2}",
                    entry.Key,
                    timeSpan,
                    this.calledCountByType[entry.Key]));
            }
        }

        public void Clear()
        {
            this.elapsedTimeByType.Clear();
            this.calledCountByType.Clear();
        }

        //public void WriteRunTimeCheck(DateTime now)
        //{
        //    float freq = System.Diagnostics.Stopwatch.Frequency / 10000000f;

        //    foreach (KeyValuePair<string, long> entry in this.elapsedTimeByType)
        //    {
        //        TimeSpan timeSpan = new TimeSpan((long)(entry.Value / freq));

        //        var table = DerivedHelper.GetTable<Outputs.RunTimeCheck>();
        //        var row = (Outputs.RunTimeCheck)table.New();
        //        row.EventTime = now;

        //        row.Minutes = Math.Round(timeSpan.TotalMinutes, 3);
        //        row.CalledCount = this.calledCountByType[entry.Key];

        //        if (entry.Key.Contains('@'))
        //        {
        //            var split = entry.Key.Split('@');

        //            row.Category = split.First();
        //            row.Name = split.Last();
        //        }
        //        else
        //        {
        //            row.Name = entry.Key;
        //        }

        //        MicronOutputMart.Instance.RunTimeCheck.Add(row);
        //    }
        //}

//        private bool HasRunningTimer(string currentTypeKey)
//        {
//            if (IsDisjointKey(currentTypeKey) == false)
//                return false;

//            // only among disjoint key

//            bool hasRunning = false;
//            foreach (var timer in this.timerSet)
//            {
//                if (timer.Value.IsRunning == false)
//                    continue;

//                if (timer.Key == currentTypeKey)
//                    continue;

//                if (IsDisjointKey(timer.Key) == false)
//                    continue;

//                #region Validity Check
//#if DEBUG
//                var parentMethodName = timer.Key.Split('/').Last();
//                if (parentMethodName.Contains("."))
//                    parentMethodName = parentMethodName.Split('.').Last();

//                var frames = new System.Diagnostics.StackTrace().GetFrames();
//                bool valid = false;
//                for (int i = 0; i < frames.Length; i++)
//                {
//                    var methodName = frames[i].GetMethod().Name;
//                    if (methodName.Contains(parentMethodName))
//                    {
//                        valid = true;
//                        break;
//                    }
//                }

//                if (valid == false)
//                {
//                    Logger.MonitorInfo("Invalid timer! : " + timer.Key + "#" + currentTypeKey);
//                }   
//#endif
//                #endregion

//                hasRunning = true;
//                //int callCount;
//                //if (MicronInputMart.Instance.CallDependenceOutputs.TryGetValue(timer.Key + "#" + currentTypeKey, out callCount))
//                //    MicronInputMart.Instance.CallDependenceOutputs[timer.Key + "#" + currentTypeKey]++;
//                //else
//                //    MicronInputMart.Instance.CallDependenceOutputs.Add(timer.Key + "#" + currentTypeKey, 1);
                 
//                //MicronInputMart.Instance.CallDependencyLog.Add(timer.Key + "#" + currentTypeKey);
//            }

//            return hasRunning;
//        }

//        private bool IsDisjointKey(string typeKey)
//        {
//            bool cache;
//            if (MicronInputMart.Instance.DisjointKeyCache.TryGetValue(typeKey, out cache))
//                return cache;

//            if (typeKey.StartsWith("ENGINE_RUN") && typeKey.Contains("Total") == false)
//            {
//                MicronInputMart.Instance.DisjointKeyCache.Add(typeKey, true);
//                return true;
//            }

//            MicronInputMart.Instance.DisjointKeyCache.Add(typeKey, false);
//            return false;
//        }
    }
}
