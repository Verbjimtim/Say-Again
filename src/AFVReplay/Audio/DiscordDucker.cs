using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;

namespace AFVReplay.Audio
{
    /// <summary>Lowers and puts back one app's volume (its Windows volume mixer level).</summary>
    internal interface IAppVolume
    {
        /// <summary>
        /// Sets the app's volume to <paramref name="level"/> (0-1) of what it was before the first call,
        /// including any of its audio that has started since.
        /// </summary>
        void Duck(float level);

        /// <summary>Puts back the volume it had before <see cref="Duck"/>.</summary>
        void Restore();
    }

    /// <summary>
    /// "Duck Discord": while the radio is busy (the same as Pause Media), Discord's volume is lowered by the
    /// chosen percentage. It comes back once the radio has been quiet for the Resume Media delay (or 2 s
    /// when that is "never"), so it doesn't bounce between a call and its readback.
    /// </summary>
    internal sealed class DiscordDucker : IDisposable
    {
        /// <summary>The restore delay when Resume Media is "never".</summary>
        public static readonly TimeSpan NeverFallback = TimeSpan.FromSeconds(2);

        /// <summary>While ducked, how often to catch Discord audio that started since (a new call, a reconnect).</summary>
        public static readonly TimeSpan Rescan = TimeSpan.FromSeconds(1);

        private readonly IAppVolume app;
        private readonly Func<bool> enabled;
        private readonly Func<bool> busy;
        private readonly Func<int> percent;
        private readonly Func<TimeSpan?> resumeDelay;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();
        private Timer timer;
        private DateTime lastBusyUtc, lastDuckUtc;
        private float level = -1;

        public DiscordDucker(IAppVolume app, Func<bool> enabled, Func<bool> busy, Func<int> percent, Func<TimeSpan?> resumeDelay, Func<DateTime> clock = null)
        {
            this.app = app;
            this.enabled = enabled;
            this.busy = busy;
            this.percent = percent;
            this.resumeDelay = resumeDelay;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>True while this has Discord lowered.</summary>
        public bool Ducked { get; private set; }

        public void Start(TimeSpan interval) => timer = new Timer(_ => Tick(), null, TimeSpan.Zero, interval);

        public void Tick()
        {
            if (!Monitor.TryEnter(sync)) return;
            try
            {
                var now = clock();
                bool on = enabled();
                bool isBusy = false;
                if (on)
                {
                    try { isBusy = busy(); }
                    catch (Exception ex) { Diag.Warn("ducking check failed: " + ex.GetType().Name); }
                }

                if (isBusy) lastBusyUtc = now;

                if (Ducked)
                {
                    var delay = resumeDelay() ?? NeverFallback;
                    if (!on || (!isBusy && now - lastBusyUtc >= delay))
                    {
                        Ducked = false;
                        level = -1;
                        Safe(app.Restore);
                        return;
                    }
                    // The amount changed, or Discord may have started more audio since.
                    float wanted = Level();
                    if (wanted != level || now - lastDuckUtc >= Rescan) Duck(wanted, now);
                }
                else if (isBusy)
                {
                    Ducked = true;
                    Duck(Level(), now);
                }
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }

        private float Level() => 1f - Math.Max(0, Math.Min(100, percent())) / 100f;

        private void Duck(float wanted, DateTime now)
        {
            level = wanted;
            lastDuckUtc = now;
            Safe(() => app.Duck(wanted));
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Diag.Warn("Discord volume change failed: " + ex.GetType().Name); }
        }

        /// <summary>Stops, and puts Discord's volume back. Windows remembers app volumes, so this matters.</summary>
        public void Dispose()
        {
            timer?.Dispose();
            timer = null;
            lock (sync)
            {
                if (Ducked)
                {
                    Ducked = false;
                    Safe(app.Restore);
                }
            }
        }
    }

    /// <summary>
    /// Discord's volume in the Windows volume mixer (every audio session of Discord, Discord PTB or
    /// Discord Canary on every playback device), through NAudio's Core Audio wrappers that vatSys ships.
    /// </summary>
    internal sealed class WindowsAppVolume : IAppVolume
    {
        private static readonly string[] Names = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };

        /// <summary>Each session's volume before it was lowered, by session instance id.</summary>
        private readonly Dictionary<string, Original> originals = new Dictionary<string, Original>();

        private struct Original
        {
            public float Volume;
            public SimpleAudioVolume Control;
        }

        public void Duck(float level)
        {
            foreach (var (id, volume) in Sessions())
            {
                if (!originals.TryGetValue(id, out var o))
                    originals[id] = o = new Original { Volume = volume.Volume, Control = volume };
                else
                    o.Control = volume; // keep the newest wrapper
                originals[id] = o;
                volume.Volume = Math.Max(0f, Math.Min(1f, o.Volume * level));
            }
        }

        public void Restore()
        {
            // Sessions that have gone since (Discord closed) are simply forgotten.
            var live = new Dictionary<string, SimpleAudioVolume>();
            try { foreach (var (id, volume) in Sessions()) live[id] = volume; }
            catch (Exception ex) { Diag.Warn("Discord sessions unavailable: " + ex.GetType().Name); }
            foreach (var kv in originals)
            {
                var control = live.TryGetValue(kv.Key, out var v) ? v : kv.Value.Control;
                try { control.Volume = kv.Value.Volume; } catch { }
            }
            originals.Clear();
        }

        private static IEnumerable<(string id, SimpleAudioVolume volume)> Sessions()
        {
            var found = new List<(string, SimpleAudioVolume)>();
            var names = new Dictionary<uint, bool>();
            using (var enumerator = new MMDeviceEnumerator())
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        uint pid = s.GetProcessID;
                        if (pid == 0) continue;
                        if (!names.TryGetValue(pid, out bool isDiscord))
                            names[pid] = isDiscord = IsDiscord(pid);
                        if (isDiscord) found.Add((s.GetSessionInstanceIdentifier, s.SimpleAudioVolume));
                    }
                }
            }
            return found;
        }

        private static bool IsDiscord(uint pid)
        {
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    foreach (var n in Names)
                        if (string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { } // exited, or not ours to see
            return false;
        }
    }
}
