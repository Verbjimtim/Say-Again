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
        /// <returns>How many of the app's audio sessions it lowered.</returns>
        int Duck(float level);

        /// <summary>Puts back the volume it had before <see cref="Duck"/>.</summary>
        void Restore();

        /// <summary>Whether the app is running and has audio to lower, without changing anything.</summary>
        DiscordState Probe();
    }

    /// <summary>What Duck Discord can see, for the status line on the Media tab.</summary>
    internal enum DiscordState
    {
        /// <summary>Duck Discord is switched off.</summary>
        Off,
        /// <summary>Not looked yet, or the last look failed.</summary>
        Unknown,
        /// <summary>No Discord app running.</summary>
        NotRunning,
        /// <summary>Discord is running but has no audio in the volume mixer yet (it adds it once it first plays something).</summary>
        NoAudio,
        /// <summary>Discord found, ready to lower.</summary>
        Ready,
        /// <summary>Discord is lowered right now.</summary>
        Lowered,
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

        /// <summary>While not ducked, how often to look for Discord for the status line.</summary>
        public static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(2);

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
        private DateTime lastBusyUtc, lastDuckUtc, lastProbeUtc = DateTime.MinValue;
        private volatile DiscordState state = DiscordState.Off;
        private int duckedPercent = -1; // compared as a whole number: on 32-bit vatSys a float like 0.1 can differ from itself
        private int lastCount = -1;

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

        /// <summary>What it can see of Discord right now (updated every couple of seconds while switched on).</summary>
        public DiscordState State => state;

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

                if (!on && !Ducked)
                {
                    state = DiscordState.Off;
                    lastProbeUtc = DateTime.MinValue; // look straight away when switched on
                    return;
                }

                if (Ducked)
                {
                    var delay = resumeDelay() ?? NeverFallback;
                    if (!on || (!isBusy && now - lastBusyUtc >= delay))
                    {
                        Ducked = false;
                        duckedPercent = -1;
                        lastCount = -1;
                        Safe(app.Restore);
                        Diag.Info("Duck Discord: restored");
                        state = on ? DiscordState.Unknown : DiscordState.Off;
                        lastProbeUtc = DateTime.MinValue;
                        return;
                    }
                    // The amount changed, or Discord may have started more audio since.
                    int wanted = Percent();
                    if (wanted != duckedPercent || now - lastDuckUtc >= Rescan) Duck(wanted, now);
                }
                else if (isBusy)
                {
                    Ducked = true;
                    Duck(Percent(), now);
                }
                else if (now - lastProbeUtc >= ProbeEvery)
                {
                    lastProbeUtc = now;
                    try { state = app.Probe(); }
                    catch (Exception ex) { state = DiscordState.Unknown; Diag.Warn("Discord check failed: " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }

        private int Percent() => Math.Max(0, Math.Min(100, percent()));

        private void Duck(int byPercent, DateTime now)
        {
            duckedPercent = byPercent;
            float wanted = 1f - byPercent / 100f;
            lastDuckUtc = now; // a failed attempt is tried again at the next rescan, not every tick
            int n = -1;
            Safe(() => n = app.Duck(wanted));
            if (n >= 0 && n != lastCount) Diag.Info($"Duck Discord: lowered to {wanted:P0} ({n} Discord audio session(s))");
            if (n >= 0) lastCount = n;
            // "Ducked now" only when Discord really was lowered.
            state = n > 0 ? DiscordState.Lowered : n == 0 ? DiscordState.NoAudio : DiscordState.Unknown;
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Diag.Warn("Discord volume change failed: " + ex.GetType().Name + ": " + ex.Message); }
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
    /// Discord's volume in the Windows volume mixer: every audio session of Discord (stable, PTB, Canary) or a third-party client (Vesktop, Legcord, WebCord) on
    /// every playback device. Uses Windows' Core Audio directly and releases every Windows audio object as soon
    /// as it's done with it (NAudio's wrappers left them for the garbage collector, which piled up hundreds of
    /// open audio objects at a time). Only Discord's own volume controls are held, and only while ducked.
    /// </summary>
    internal sealed class WindowsAppVolume : IAppVolume
    {
        /// <summary>The Discord app (stable, PTB, Canary, Development) and third-party clients (Vesktop, Legcord / ArmCord, WebCord).</summary>
        private static readonly string[] DiscordNames =
            { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment", "Vesktop", "Legcord", "ArmCord", "WebCord" };
        private readonly string[] names;

        /// <param name="processNames">For tests: the apps to lower instead of Discord.</param>
        public WindowsAppVolume(params string[] processNames) => names = processNames.Length > 0 ? processNames : DiscordNames;

        /// <summary>For tests: the volume of each matching session right now, read afresh.</summary>
        internal List<float> ReadVolumes()
        {
            var v = new List<float>();
            foreach (var (_, control) in DiscordSessions())
            {
                if (control.GetMasterVolume(out float level) == 0) v.Add(level);
                Release(control);
            }
            return v;
        }

        /// <summary>Discord sessions lowered, by session instance id: the volume control and the volume before.</summary>
        private readonly Dictionary<string, (CoreAudio.ISimpleAudioVolume control, float original)> held =
            new Dictionary<string, (CoreAudio.ISimpleAudioVolume, float)>();

        /// <summary>Which process ids are Discord, so each process is looked up once.</summary>
        private readonly Dictionary<uint, bool> isDiscord = new Dictionary<uint, bool>();

        /// <summary>For the leak check: Discord volume controls held right now.</summary>
        internal int Held => held.Count;

        public int Duck(float level)
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
                int hr = h.control.SetMasterVolume(h.original * level, ref ctx);
                if (hr != 0) Diag.Warn($"Duck Discord: volume change refused (0x{hr:X8})");
            }
            return held.Count;
        }

        public DiscordState Probe()
        {
            var sessions = DiscordSessions();
            foreach (var (_, control) in sessions) Release(control);
            if (sessions.Count > 0) return DiscordState.Ready;
            foreach (var n in names)
            {
                var running = Process.GetProcessesByName(n);
                bool any = running.Length > 0;
                foreach (var p in running) p.Dispose();
                if (any) return DiscordState.NoAudio;
            }
            return DiscordState.NotRunning;
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
                enumerator = CoreAudio.CreateEnumerator();
                Check(enumerator.EnumAudioEndpoints(CoreAudio.Render, CoreAudio.DeviceStateActive, out IntPtr devicesPtr));
                devices = CoreAudio.Own<CoreAudio.IMMDeviceCollection>(devicesPtr);
                Check(devices.GetCount(out uint count));
                for (uint d = 0; d < count; d++)
                {
                    CoreAudio.IMMDevice device = null;
                    CoreAudio.IAudioSessionManager2 manager = null;
                    CoreAudio.IAudioSessionEnumerator sessions = null;
                    try
                    {
                        if (devices.Item(d, out IntPtr devicePtr) != 0) continue;
                        device = CoreAudio.Own<CoreAudio.IMMDevice>(devicePtr);
                        if (device == null) continue;
                        var iid = typeof(CoreAudio.IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out IntPtr managerPtr) != 0) continue;
                        manager = CoreAudio.Own<CoreAudio.IAudioSessionManager2>(managerPtr);
                        if (manager == null || manager.GetSessionEnumerator(out IntPtr sessionsPtr) != 0) continue;
                        sessions = CoreAudio.Own<CoreAudio.IAudioSessionEnumerator>(sessionsPtr);
                        if (sessions == null) continue;
                        if (sessions.GetCount(out int n) != 0) continue;
                        for (int i = 0; i < n; i++)
                        {
                            CoreAudio.IAudioSessionControl2 session = null;
                            try
                            {
                                if (sessions.GetSession(i, out IntPtr sessionPtr) != 0) continue;
                                session = CoreAudio.Own<CoreAudio.IAudioSessionControl2>(sessionPtr);
                                if (session == null) continue;
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
                        Release(manager);
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
                    foreach (var n in names)
                        if (string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase)) { yes = true; break; }
            }
            catch { } // exited, or not ours to see
            return isDiscord[pid] = yes;
        }

        private static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

        /// <summary>Every wrapper here is Say Again's own (see <see cref="CoreAudio.Own{T}"/>), so releasing it never touches vatSys's.</summary>
        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
                try { Marshal.FinalReleaseComObject(com); } catch { }
        }
    }

    /// <summary>The few Windows Core Audio interfaces Duck Discord needs (method order must match Windows').</summary>
    internal static class CoreAudio
    {
        public const int Render = 0;
        public const uint DeviceStateActive = 1;
        public const uint ClsCtxAll = 0x17;

        private static Guid EnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsCtx, ref Guid iid, out IntPtr instance);

        /// <summary>
        /// Windows hands every caller in a process the same device enumerator (and often the same devices), and
        /// vatSys's NAudio already holds them. .NET normally reuses the wrapper it made for vatSys, typed as NAudio's
        /// own class, so Say Again's cast failed on every check, and releasing that shared wrapper would break
        /// vatSys's. So every Windows audio object here gets a wrapper of Say Again's own instead.
        /// </summary>
        public static IMMDeviceEnumerator CreateEnumerator()
        {
            var iid = typeof(IMMDeviceEnumerator).GUID;
            int hr = CoCreateInstance(ref EnumeratorClsid, IntPtr.Zero, 1 /* in-process */, ref iid, out IntPtr p);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            return Own<IMMDeviceEnumerator>(p);
        }

        /// <summary>Wraps a Windows object Say Again was just given in a wrapper of its own (never one vatSys holds), and drops the raw reference.</summary>
        public static T Own<T>(IntPtr p) where T : class
        {
            if (p == IntPtr.Zero) return null;
            object o;
            try { o = Marshal.GetUniqueObjectForIUnknown(p); }
            finally { Marshal.Release(p); }
            if (o is T t) return t;
            Marshal.FinalReleaseComObject(o);
            throw new InvalidCastException("Windows audio object has no " + typeof(T).Name);
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
            // (later methods not used)
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IntPtr device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            // IAudioSessionManager
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
            // IAudioSessionManager2
            [PreserveSig] int GetSessionEnumerator(out IntPtr sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IntPtr session);
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
