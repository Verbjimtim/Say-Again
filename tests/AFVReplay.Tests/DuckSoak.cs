using System;
using System.Diagnostics;
using AFVReplay.Audio;

namespace AFVReplay.Tests
{
    /// <summary>
    /// Leak check for Duck Discord against the real Windows volume mixer: re-applies 100% (no audible change)
    /// as the ducker's once-a-second rescan does, and reports this process's handle count and memory.
    /// </summary>
    internal static class DuckSoak
    {
        public static int Run(int scans)
        {
            var app = new WindowsAppVolume();
            var me = Process.GetCurrentProcess();
            void Report(int i) { me.Refresh(); Console.WriteLine($"scan {i,5}: handles {me.HandleCount,6}, private {me.PrivateMemorySize64 / 1024 / 1024} MB"); }
            Report(0);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i <= scans; i++)
            {
                app.Duck(1f);
                if (i % (scans / 10) == 0) Report(i);
            }
            Console.WriteLine($"{scans} scans in {sw.ElapsedMilliseconds} ms, Discord sessions held {app.Held}");
            sw.Restart();
            app.Restore();
            Console.WriteLine($"restore {sw.ElapsedMilliseconds} ms");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Report(scans);
            return 0;
        }
    }
}
