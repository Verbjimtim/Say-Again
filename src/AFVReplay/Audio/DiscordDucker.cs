using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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
        public static readonly TimeSpan Rescan = TimeSpan.FromSeconds(5);

        /// <summary>The longest shutdown waits for Discord's volume to be put back, so vatSys's exit is never held up.</summary>
        public static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(1);

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
            bool restore;
            lock (sync)
            {
                restore = Ducked;
                Ducked = false;
            }
            if (!restore) return;
            // On its own thread with a time limit: if Windows audio is slow, vatSys still closes on time.
            var t = new Thread(() => Safe(app.Restore)) { IsBackground = true, Name = "Say Again Discord restore" };
            t.Start();
            t.Join(ShutdownLimit);
        }
    }

    /// <summary>
    /// Discord's volume in the Windows volume mixer: every audio session of Discord, Discord PTB or Canary on
    /// every playback device. Uses Windows' Core Audio directly and releases every Windows audio object as soon
    /// as it's done with it (NAudio's wrappers left them for the garbage collector, which piled up hundreds of
    /// open audio objects at a time). Only Discord's own volume controls are held, and only while ducked.
    /// </summary>
    internal sealed class WindowsAppVolume : IAppVolume
    {
        private static readonly string[] Names = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };

        /// <summary>Discord sessions lowered, by session instance id: the volume control and the volume before.</summary>
        private readonly Dictionary<string, (CoreAudio.ISimpleAudioVolume control, float original)> held =
            new Dictionary<string, (CoreAudio.ISimpleAudioVolume, float)>();

        /// <summary>Which process ids are Discord, so each process is looked up once.</summary>
        private readonly Dictionary<uint, bool> isDiscord = new Dictionary<uint, bool>();

        /// <summary>For the leak check: Discord volume controls held right now.</summary>
        internal int Held => held.Count;

        public void Duck(float level)
        {
            level = Math.Max(0f, Math.Min(1f, level));
            foreach (var (id, control) in DiscordSessions())
            {
                if (held.ContainsKey(id))
                {
                    Release(control); // already holding this session's control
                }
                else
                {
                    if (control.GetMasterVolume(out float original) != 0) { Release(control); continue; }
                    held[id] = (control, original);
                }
            }
            foreach (var h in held.Values)
            {
                var ctx = Guid.Empty;
                try { h.control.SetMasterVolume(h.original * level, ref ctx); } catch { } // the session may have gone
            }
        }

        public void Restore()
        {
            foreach (var h in held.Values)
            {
                var ctx = Guid.Empty;
                try { h.control.SetMasterVolume(h.original, ref ctx); } catch { }
                Release(h.control);
            }
            held.Clear();
            isDiscord.Clear(); // process ids get reused
        }

        /// <summary>Discord's sessions on every active playback device. Everything else found is released straight away.</summary>
        private List<(string id, CoreAudio.ISimpleAudioVolume control)> DiscordSessions()
        {
            var found = new List<(string, CoreAudio.ISimpleAudioVolume)>();
            CoreAudio.IMMDeviceEnumerator enumerator = null;
            CoreAudio.IMMDeviceCollection devices = null;
            try
            {
                enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumeratorComObject();
                Check(enumerator.EnumAudioEndpoints(CoreAudio.Render, CoreAudio.DeviceStateActive, out devices));
                Check(devices.GetCount(out uint count));
                for (uint d = 0; d < count; d++)
                {
                    CoreAudio.IMMDevice device = null;
                    object managerObject = null;
                    CoreAudio.IAudioSessionEnumerator sessions = null;
                    try
                    {
                        if (devices.Item(d, out device) != 0 || device == null) continue;
                        var iid = typeof(CoreAudio.IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out managerObject) != 0 || managerObject == null) continue;
                        var manager = (CoreAudio.IAudioSessionManager2)managerObject;
                        if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                        if (sessions.GetCount(out int n) != 0) continue;
                        for (int i = 0; i < n; i++)
                        {
                            CoreAudio.IAudioSessionControl2 session = null;
                            try
                            {
                                if (sessions.GetSession(i, out session) != 0 || session == null) continue;
                                if (session.GetProcessId(out uint pid) != 0 || pid == 0 || !IsDiscord(pid)) continue;
                                if (session.GetSessionInstanceIdentifier(out string id) != 0 || id == null) continue;
                                found.Add((id, (CoreAudio.ISimpleAudioVolume)session)); // the same object's volume interface
                                session = null; // kept: handed over above
                            }
                            finally { Release(session); }
                        }
                    }
                    finally
                    {
                        Release(sessions);
                        Release(managerObject);
                        Release(device);
                    }
                }
            }
            finally
            {
                Release(devices);
                Release(enumerator);
            }
            return found;
        }

        private bool IsDiscord(uint pid)
        {
            if (isDiscord.TryGetValue(pid, out bool yes)) return yes;
            yes = false;
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    foreach (var n in Names)
                        if (string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase)) { yes = true; break; }
            }
            catch { } // exited, or not ours to see
            return isDiscord[pid] = yes;
        }

        private static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
                try { Marshal.ReleaseComObject(com); } catch { } // one reference: the same session can come back as the RCW already held
        }
    }

    /// <summary>The few Windows Core Audio interfaces Duck Discord needs (method order must match Windows').</summary>
    internal static class CoreAudio
    {
        public const int Render = 0;
        public const uint DeviceStateActive = 1;
        public const uint ClsCtxAll = 0x17;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
            // (later methods not used)
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            // IAudioSessionManager
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
            // IAudioSessionManager2
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
        }
    }
}
