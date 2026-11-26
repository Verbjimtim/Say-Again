using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace AFVReplay.Tests
{
    /// <summary>Diagnostics: prints Say Again's own debug lines from a running vatSys (the Windows debug output channel).</summary>
    internal static class DebugCapture
    {
        public static int Run(int seconds)
        {
            using (var bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY", out bool createdA))
            using (var dataReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY", out bool createdB))
            using (var map = MemoryMappedFile.CreateOrOpen("DBWIN_BUFFER", 4096))
            using (var view = map.CreateViewAccessor(0, 4096))
            {
                if (!createdA || !createdB) Console.WriteLine("note: another debug listener may already be running");
                var end = DateTime.UtcNow.AddSeconds(seconds);
                var bytes = new byte[4092];
                bufferReady.Set();
                while (DateTime.UtcNow < end)
                {
                    if (!dataReady.WaitOne(500)) continue;
                    view.ReadArray(4, bytes, 0, bytes.Length);
                    int len = Array.IndexOf(bytes, (byte)0); if (len < 0) len = bytes.Length;
                    string text = Encoding.Default.GetString(bytes, 0, len).TrimEnd();
                    if (text.Contains("[AFVReplay]")) Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} {text}");
                    bufferReady.Set();
                }
            }
            Console.WriteLine("end");
            return 0;
        }
    }
}
