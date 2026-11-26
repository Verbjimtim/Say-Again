using System;
using System.Linq;
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
        /// <summary>
        /// Does ducking actually change a real app's volume? Plays a quiet tone from this process (so it has its
        /// own entry in the volume mixer), lowers it by 50%, reads the mixer back, and restores it.
        /// </summary>
        public static int Check()
        {
            var me = Process.GetCurrentProcess().ProcessName;
            var wav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sayagain-duckcheck.wav");
            WriteTone(wav);
            using (var player = new System.Media.SoundPlayer(wav))
            {
                player.PlayLooping();
                System.Threading.Thread.Sleep(800);
                var app = new WindowsAppVolume(me);
                Console.WriteLine("before: " + string.Join(", ", app.ReadVolumes()));
                app.Duck(0.5f);
                Console.WriteLine($"ducked 50% (holding {app.Held}): " + string.Join(", ", app.ReadVolumes()));
                app.Restore();
                Console.WriteLine("restored: " + string.Join(", ", app.ReadVolumes()));
                player.Stop();
            }
            System.IO.File.Delete(wav);
            return 0;
        }

        /// <summary>The same against real Discord, lowered by only 2% for 3 s so nobody hears it.</summary>
        public static int CheckDiscord()
        {
            var app = new WindowsAppVolume();
            Console.WriteLine("status: " + app.Probe() + "; Discord sessions before: " + string.Join(", ", app.ReadVolumes()));
            app.Duck(0.98f);
            Console.WriteLine($"ducked 2% (holding {app.Held}): " + string.Join(", ", app.ReadVolumes()));
            System.Threading.Thread.Sleep(3000);
            Console.WriteLine("after 3 s: " + string.Join(", ", app.ReadVolumes()));
            app.Restore();
            Console.WriteLine("restored: " + string.Join(", ", app.ReadVolumes()));
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        private sealed class BackgroundForm : System.Windows.Forms.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private static string Foreground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            try { using (var p = Process.GetProcessById((int)pid)) return p.Id == Process.GetCurrentProcess().Id ? "THIS TEST WINDOW" : p.ProcessName; }
            catch { return "?"; }
        }

        /// <summary>
        /// Does ducking Discord pull a window of the ducking app to the front? A window of this process sits behind
        /// whatever you're using while Discord is lowered by 2% and put back, five times.
        /// </summary>
        public static int CheckForeground()
        {
            using (var form = new BackgroundForm { Text = "Say Again focus test", Width = 300, Height = 120, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(40, 40) })
            {
                form.Show();
                void Pump(int ms) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); } }
                Pump(500);
                Console.WriteLine("foreground at start: " + Foreground());
                var app = new WindowsAppVolume();
                // On a background thread, as in vatSys.
                for (int i = 1; i <= 5; i++)
                {
                    System.Threading.Tasks.Task.Run(() => app.Duck(0.98f)).Wait();
                    Pump(700);
                    Console.WriteLine($"ducked {i}: foreground {Foreground()}");
                    System.Threading.Tasks.Task.Run(() => app.Restore()).Wait();
                    Pump(700);
                    Console.WriteLine($"restored {i}: foreground {Foreground()}");
                }
            }
            return 0;
        }

        /// <summary>
        /// Does Pause Media pull a window of the pausing app to the front? A window of this process sits behind
        /// whatever you're using while whatever is playing is paused and resumed, three times.
        /// </summary>
        public static int CheckMediaForeground()
        {
            using (var form = new BackgroundForm { Text = "Say Again focus test", Width = 300, Height = 120, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(40, 40) })
            {
                form.Show();
                void Pump(int ms) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); } }
                Pump(500);
                Console.WriteLine("foreground at start: " + Foreground());
                var media = new WindowsMedia();
                for (int i = 1; i <= 3; i++)
                {
                    media.PausePlaying(); media.Flush(TimeSpan.FromSeconds(5));
                    for (int k = 0; k < 4; k++) { Pump(250); Console.WriteLine($"paused {i} +{(k + 1) * 250} ms: foreground {Foreground()}"); }
                    media.ResumePaused(); media.Flush(TimeSpan.FromSeconds(5));
                    for (int k = 0; k < 4; k++) { Pump(250); Console.WriteLine($"resumed {i} +{(k + 1) * 250} ms: foreground {Foreground()}"); }
                }
            }
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

        /// <summary>
        /// Does Pause Media let a background app grab the foreground? A window of this process keeps asking to come to
        /// the front (as vatSys's ASD does when the mouse is over it), which Windows normally refuses. 6 s with no media
        /// calls first, then the playing media is paused and resumed twice.
        /// </summary>
        public static int CheckMediaGrab()
        {
            using (var form = new BackgroundForm { Text = "Say Again focus test", Width = 300, Height = 120, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(40, 40) })
            {
                form.Show();
                string phase = "control";
                void Pump(int ms)
                {
                    var end = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < end)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        bool ok = SetForegroundWindow(form.Handle);
                        string fg = Foreground();
                        if (ok || fg == "THIS TEST WINDOW") { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{phase}] GRABBED the foreground (SetForegroundWindow={ok}, foreground {fg})"); return; }
                        System.Threading.Thread.Sleep(300);
                    }
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{phase}] refused, foreground {Foreground()}");
                }
                // Wait for you to click into another app: that input takes away the right to come forward that this
                // test inherited from being started by the app you were using.
                string startFg = Foreground();
                Console.WriteLine("foreground at start: " + startFg + "; waiting for you to click into another app...");
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline && (Foreground() == startFg || Foreground() == "THIS TEST WINDOW")) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(100); }
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} now in front: {Foreground()}; starting in 1 s");
                var settle = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < settle) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Pump(6000);
                var media = new WindowsMedia();
                for (int i = 1; i <= 2; i++)
                {
                    phase = "after pause " + i; media.PausePlaying(); media.Flush(TimeSpan.FromSeconds(5)); Pump(2500);
                    phase = "after resume " + i; media.ResumePaused(); media.Flush(TimeSpan.FromSeconds(5)); Pump(2500);
                }
            }
            return 0;
        }

        /// <summary>Read-only: prints Discord's mixer volumes whenever they change, for <paramref name="seconds"/>.</summary>
        public static int WatchDiscord(int seconds)
        {
            var app = new WindowsAppVolume();
            string last = null;
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                string now = string.Join(", ", app.ReadVolumes().Select(v => v.ToString("0.00")));
                if (now != last) { Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} Discord sessions: [{now}]"); last = now; }
                System.Threading.Thread.Sleep(200);
            }
            Console.WriteLine("end");
            return 0;
        }

        /// <summary>The whole ducker as vatSys runs it (its own 100 ms timer on a background thread), at 2%.</summary>
        public static int CheckDucker()
        {
            bool busy = false;
            var watch = new WindowsAppVolume();
            using (var d = new DiscordDucker(new WindowsAppVolume(), () => true, () => busy, () => 2, () => TimeSpan.FromSeconds(1)))
            {
                d.Start(TimeSpan.FromMilliseconds(100));
                System.Threading.Thread.Sleep(2500);
                Console.WriteLine($"quiet: state {d.State}, Discord [{string.Join(", ", watch.ReadVolumes())}]");
                busy = true;
                System.Threading.Thread.Sleep(1500);
                Console.WriteLine($"busy: ducked {d.Ducked}, state {d.State}, Discord [{string.Join(", ", watch.ReadVolumes())}]");
                busy = false;
                System.Threading.Thread.Sleep(2000);
                Console.WriteLine($"quiet again: ducked {d.Ducked}, state {d.State}, Discord [{string.Join(", ", watch.ReadVolumes())}]");
            }
            return 0;
        }

        /// <summary>
        /// As inside vatSys: the main (STA) thread already holds the device enumerator, playback devices, session
        /// managers and sessions in wrappers of another .NET class (as vatSys's NAudio does). Duck Discord then
        /// looks for Discord from its background thread, ducks this test's own entry, and restores it. Afterwards
        /// the main thread's objects must all still work (Say Again must not have released vatSys's).
        /// </summary>
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class OtherEnumeratorClass { }

        public static int CheckStaHeld()
        {
            var ready = new System.Threading.ManualResetEvent(false);
            var checkAgain = new System.Threading.ManualResetEvent(false);
            var checkedAgain = new System.Threading.ManualResetEvent(false);
            string stillWorks = null;
            var sta = new System.Threading.Thread(() =>
            {
                T Shared<T>(IntPtr p) { var o = (T)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(p); System.Runtime.InteropServices.Marshal.Release(p); return o; }
                var other = new OtherEnumeratorClass();
                var enumerator = (CoreAudio.IMMDeviceEnumerator)other;
                var devicesHeld = new System.Collections.Generic.List<CoreAudio.IMMDevice>();
                var volumesHeld = new System.Collections.Generic.List<CoreAudio.ISimpleAudioVolume>();
                enumerator.EnumAudioEndpoints(CoreAudio.Render, CoreAudio.DeviceStateActive, out IntPtr dp);
                var devices = Shared<CoreAudio.IMMDeviceCollection>(dp);
                devices.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    devices.Item(i, out IntPtr devp);
                    var dev = Shared<CoreAudio.IMMDevice>(devp);
                    devicesHeld.Add(dev);
                    var iid = typeof(CoreAudio.IAudioSessionManager2).GUID;
                    if (dev.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out IntPtr mp) != 0) continue;
                    var mgr = Shared<CoreAudio.IAudioSessionManager2>(mp);
                    if (mgr.GetSessionEnumerator(out IntPtr sp) != 0) continue;
                    var sessions = Shared<CoreAudio.IAudioSessionEnumerator>(sp);
                    sessions.GetCount(out int c);
                    for (int k = 0; k < c; k++) { sessions.GetSession(k, out IntPtr ssp); volumesHeld.Add(Shared<CoreAudio.ISimpleAudioVolume>(ssp)); }
                }
                Console.WriteLine($"main thread holds the enumerator, {n} playback device(s) and {volumesHeld.Count} session(s), as NAudio would");
                ready.Set();
                while (!checkAgain.WaitOne(10)) System.Windows.Forms.Application.DoEvents();
                try
                {
                    enumerator.EnumAudioEndpoints(CoreAudio.Render, CoreAudio.DeviceStateActive, out IntPtr again);
                    System.Runtime.InteropServices.Marshal.Release(again);
                    int ok = 0;
                    foreach (var dev in devicesHeld) { var iid = typeof(CoreAudio.IAudioSessionManager2).GUID; if (dev.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out IntPtr m) == 0) { ok++; System.Runtime.InteropServices.Marshal.Release(m); } }
                    foreach (var v in volumesHeld) v.GetMasterVolume(out float _);
                    stillWorks = $"yes (enumerator, {ok}/{devicesHeld.Count} devices, {volumesHeld.Count} sessions)";
                }
                catch (Exception ex) { stillWorks = "NO: " + ex.GetType().Name + ": " + ex.Message; }
                checkedAgain.Set();
            });
            sta.SetApartmentState(System.Threading.ApartmentState.STA);
            sta.IsBackground = true;
            sta.Start();
            ready.WaitOne();

            var me = Process.GetCurrentProcess().ProcessName;
            var tone = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afvreplay-sta-tone.wav");
            WriteTone(tone);
            var player = new System.Media.SoundPlayer(tone);
            player.PlayLooping(); // gives this test its own entry in the volume mixer
            System.Threading.Thread.Sleep(800);

            int result = 0;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var discord = new WindowsAppVolume();
                    Console.WriteLine($"background thread: Discord {discord.Probe()}, sessions [{string.Join(", ", discord.ReadVolumes())}]");
                    var self = new WindowsAppVolume(me);
                    int n = self.Duck(0.5f);
                    var lowered = self.ReadVolumes();
                    self.Restore();
                    var back = self.ReadVolumes();
                    Console.WriteLine($"background thread: ducked {n} own session(s) to [{string.Join(", ", lowered)}], restored to [{string.Join(", ", back)}]");
                    if (n == 0 || lowered.Any(v => Math.Abs(v - 0.5f) > 0.01f) || back.Any(v => Math.Abs(v - 1f) > 0.01f)) result = 1;
                }
                catch (Exception ex) { Console.WriteLine("background thread FAILED: " + ex.GetType().Name + ": " + ex.Message); result = 1; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.MTA);
            t.Start();
            t.Join();
            player.Stop();
            try { System.IO.File.Delete(tone); } catch { }

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            checkAgain.Set();
            checkedAgain.WaitOne();
            Console.WriteLine("main thread's objects still work: " + stillWorks);
            if (stillWorks == null || !stillWorks.StartsWith("yes")) result = 1;
            Console.WriteLine(result == 0 ? "PASS" : "FAIL");
            return result;
        }

        private static void WriteTone(string path)
        {
            const int rate = 8000, seconds = 5;
            using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                int n = rate * seconds;
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
                for (int i = 0; i < n; i++) w.Write((short)(60 * Math.Sin(i * 2 * Math.PI * 440 / rate))); // barely audible
            }
        }

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
