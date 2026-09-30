using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BSProfiler
{
    internal readonly struct ProcessMemory
    {
        public readonly long SampleTicks, WorkingSet, PrivateBytes, PeakWorkingSet;
        public readonly string Status;
        private ProcessMemory(long ticks, long workingSet, long privateBytes, long peak, string status)
        { SampleTicks = ticks; WorkingSet = workingSet; PrivateBytes = privateBytes; PeakWorkingSet = peak; Status = status; }

        public static ProcessMemory Read()
        {
            long ticks = Stopwatch.GetTimestamp();
            try
            {
                var counters = new Counters { Size = (uint)Marshal.SizeOf(typeof(Counters)) };
                if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size))
                    return new ProcessMemory(ticks, -1, -1, -1, "Win32 error " + Marshal.GetLastWin32Error());
                long resident = checked((long)counters.WorkingSet.ToUInt64());
                long committed = checked((long)counters.PrivateUsage.ToUInt64());
                if (resident <= 0 || committed <= 0)
                    return new ProcessMemory(ticks, -1, -1, -1, "OS returned nonpositive memory counters");
                return new ProcessMemory(ticks, resident, committed,
                    checked((long)counters.PeakWorkingSet.ToUInt64()), "available");
            }
            catch (Exception ex) { return new ProcessMemory(ticks, -1, -1, -1, ex.GetType().Name + ": " + ex.Message); }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonPaged,
                QuotaNonPaged, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
    }
}
