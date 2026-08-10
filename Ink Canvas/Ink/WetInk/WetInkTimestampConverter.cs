using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 把系统时间戳统一到微秒。WM_POINTER 用 QueryPerformanceCounter，
    /// WM_INPUT/GetMessageTime 用 tick count——两套时钟要在同一空间可比。
    /// </summary>
    internal static class WetInkTimestampConverter
    {
        private static readonly long CounterFrequency;
        private static readonly long InitialCounter;
        private static readonly long InitialTickMs;
        private static readonly long InitialUtcUs;
        private static readonly bool IsAvailable;

        static WetInkTimestampConverter()
        {
            try
            {
                if (QueryPerformanceFrequency(out var freq) && freq > 0)
                {
                    CounterFrequency = freq;
                    QueryPerformanceCounter(out InitialCounter);
                    InitialTickMs = Environment.TickCount64;
                    InitialUtcUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
                    IsAvailable = true;
                }
            }
            catch
            {
                IsAvailable = false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long frequency);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long count);

        /// <summary>QPC 读数 → 微秒（相对进程启动）。</summary>
        public static long QueryPerformanceCounterToMicroseconds(long counter)
        {
            if (!IsAvailable || CounterFrequency == 0)
                return counter;
            return (long)((counter - InitialCounter) * 1_000_000.0 / CounterFrequency);
        }

        /// <summary>当前 QPC → 微秒。</summary>
        public static long NowMicroseconds()
        {
            QueryPerformanceCounter(out var counter);
            return QueryPerformanceCounterToMicroseconds(counter);
        }

        /// <summary>tick count（毫秒）→ 微秒（相对进程启动）。</summary>
        public static long TickCountToMicroseconds(long tickMs)
        {
            if (!IsAvailable)
                return tickMs * 1000L;
            return (tickMs - InitialTickMs) * 1000L;
        }

        /// <summary>当前真实墙钟微秒（UTC）。</summary>
        public static long NowUtcMicroseconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        }

        public static long NowUtcMicrosecondsFromEpoch() => NowUtcMicroseconds();

        /// <summary>诊断：打印时钟校准基线。</summary>
        [Conditional("DEBUG")]
        public static void LogCalibration()
        {
            Debug.WriteLine(
                $"[WetInk] timestamp converter: avail={IsAvailable} " +
                $"freq={CounterFrequency} initCounter={InitialCounter} initTick={InitialTickMs}");
        }
    }
}