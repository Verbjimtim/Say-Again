using System;
using System.Collections.Generic;
using AFVReplay.Audio;

namespace AFVReplay.Tests
{
    /// <summary>Duck Discord.</summary>
    internal static class DuckTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private sealed class FakeApp : IAppVolume
        {
            public readonly List<string> Calls = new List<string>();
            public float Volume = 1f; // as a fraction of what it was before ducking
            public void Duck(float level) { Calls.Add("duck " + level.ToString("0.0")); Volume = level; }
            public void Restore() { Calls.Add("restore"); Volume = 1f; }
        }

        /// <summary>
        /// Lowers Discord by the chosen % while the radio is busy, holds through short gaps, comes back after the
        /// Resume Media delay (2 s when that is Never), follows a change of amount, does nothing while off,
        /// comes back if switched off mid-call, and comes back on shutdown.
        /// </summary>
        internal static void DiscordDucksWhileRadioBusy()
        {
            var app = new FakeApp();
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            bool on = false, busy = false;
            int percent = 50;
            TimeSpan? delay = TimeSpan.FromSeconds(2);
            var d = new DiscordDucker(app, () => on, () => busy, () => percent, () => delay, () => now);
            void Step(double seconds) { for (int i = 0; i < seconds * 10; i++) { now = now.AddMilliseconds(100); d.Tick(); } }

            busy = true; Step(1);
            Assert(app.Calls.Count == 0, "off: Discord left alone");
            busy = false; Step(1);

            on = true; Step(1);
            Assert(app.Calls.Count == 0, "quiet radio: Discord left alone");
            busy = true; Step(0.1);
            Assert(d.Ducked && Math.Abs(app.Volume - 0.5f) < 0.001, "lowered by 50%: " + app.Volume);
            Step(3);
            Assert(app.Calls.Count >= 2 && app.Calls.TrueForAll(c => c == "duck 0.5"), "re-checked for new Discord audio while ducked: " + string.Join(",", app.Calls));
            busy = false; Step(1.5);
            busy = true; Step(1); // readback within the delay
            busy = false; Step(1.5);
            Assert(d.Ducked, "still ducked through short gaps");
            Step(1);
            Assert(!d.Ducked && app.Calls[app.Calls.Count - 1] == "restore" && app.Volume == 1f, "back 2 s after quiet");

            // Changing the amount while ducked applies straight away.
            busy = true; Step(0.2);
            percent = 100; Step(0.1);
            Assert(app.Volume == 0f, "100% = silent, applied while ducked: " + app.Volume);
            percent = 50;

            // Switching off mid-call puts it back.
            on = false; Step(0.1);
            Assert(!d.Ducked && app.Volume == 1f, "switching off mid-call restores");

            // Resume Media "never": comes back after 2 s instead of staying quiet.
            on = true; delay = null;
            busy = true; Step(1);
            busy = false; Step(1.9);
            Assert(d.Ducked, "never: still ducked at 1.9 s");
            Step(0.2);
            Assert(!d.Ducked && app.Volume == 1f, "never: back after 2 s");

            // A 5 s Resume Media delay.
            delay = TimeSpan.FromSeconds(5);
            busy = true; Step(1);
            busy = false; Step(4.5);
            Assert(d.Ducked, "5 s: still ducked at 4.5 s");
            Step(1);
            Assert(!d.Ducked, "5 s: back");

            // Shutdown puts it back (Windows would otherwise remember the lowered volume).
            busy = true; Step(0.2);
            d.Dispose();
            Assert(!d.Ducked && app.Volume == 1f && app.Calls[app.Calls.Count - 1] == "restore", "shutdown restores");
            int n = app.Calls.Count;
            d.Dispose();
            Assert(app.Calls.Count == n, "nothing more after shutdown");
        }

        internal static void DuckSettings()
        {
            var s = new ReplaySettings();
            Assert(!s.DuckDiscord && s.DuckPercent == 50, "off by default, 50%");
            s.DuckPercent = 0; s.Normalise();
            Assert(s.DuckPercent == 10, "at least 10%");
            s.DuckPercent = 250; s.Normalise();
            Assert(s.DuckPercent == 100, "at most 100%");
            s.DuckPercent = 34; s.Normalise();
            Assert(s.DuckPercent == 30, "in tens: " + s.DuckPercent);

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afvreplay-test-" + Guid.NewGuid() + ".xml");
            try
            {
                new ReplaySettings { DuckDiscord = true, DuckPercent = 70 }.Save(path);
                var loaded = ReplaySettings.Load(path);
                Assert(loaded.DuckDiscord && loaded.DuckPercent == 70, "saved");
            }
            finally { System.IO.File.Delete(path); }
        }
    }
}
