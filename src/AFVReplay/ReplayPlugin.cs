using System;
using System.Linq;
using System.ComponentModel.Composition;
using System.Windows.Forms;
using AFVReplay.Audio;
using AFVReplay.Handoff;
using AFVReplay.Input;
using AFVReplay.UI;
using AFVReplay.VatSys;
using vatsys;
using vatsys.Plugin;

namespace AFVReplay
{
    /// <summary>
    /// vatSys plugin entry point. vatSys creates this once at startup, on the UI thread.
    /// </summary>
    [Export(typeof(IPlugin))]
    public sealed class ReplayPlugin : IPlugin
    {
        private AudioHook hook;
        private ClipFinalizer finalizer;
        private InputPoller poller;
        private KeybindInjector injector;
        private VscsInjector vscs;
        private CoordHook coordHook;
        private ClipFinalizer coordFinalizer;
        private CoordRecorder coordRecorder;
        private CoordConversation coordConversation;
        private InputPoller coordPoller;
        private string coordDisabledReason;
        private OwnTxRecorder ownTx;
        private MicHook micHook;
        private WindowsMedia media;
        private MediaPauser mediaPauser;
        private DiscordDucker ducker;
        private HandoffMonitor handoff;
        private Timer afkTimer;
        private string disabledReason;

        public string Name => "Say Again";

        internal ReplaySettings Settings { get; private set; }
        internal ClipStore Store { get; private set; }
        internal ReplayPlayer Player { get; private set; }
        internal ReplayController Controller { get; private set; }

        // Coordination (hotline/coldline) calls: separate store (10 min cap), player and key.
        internal ClipStore CoordStore { get; private set; }
        internal ReplayPlayer CoordPlayer { get; private set; }
        internal ReplayController CoordController { get; private set; }

        /// <summary>AFK: holds calls made while you're away, and requests a break.</summary>
        internal AfkMode Afk { get; private set; }

        /// <summary>Handoff Alert's tones.</summary>
        internal AlertPlayer Alerts { get; private set; }

        /// <summary>Over You Alert: a station called while you were transmitting.</summary>
        internal OverYouAlert OverYou { get; private set; }

        /// <summary>Who called over you and on what, while the VSCS should show it; otherwise null.</summary>
        internal (string Callsign, string Where)? OverYouNow => ShowOverYou ? OverYou.Current(DateTime.UtcNow) : null;

        /// <summary>Blocked on and Blocked UI on.</summary>
        private bool ShowOverYou => Settings.OverYouAlert && Settings.OverYouUi;

        /// <summary>Blocked on and Blocked Tone on.</summary>
        private bool ToneOverYou => Settings.OverYouAlert && Settings.OverYouTone;

        internal void DismissOverYou() => OverYou.Dismiss();

        /// <summary>For tests: stands in for the live AFV hook's <see cref="BlockedFrequencies"/>.</summary>
        internal Func<uint[]> BlockedFrequenciesOverride;

        /// <summary>Frequencies being called on over your transmission right now (their VSCS buttons flash red).</summary>
        internal uint[] BlockedFrequencies
        {
            get
            {
                if (!ShowOverYou) return Array.Empty<uint>();
                if (BlockedFrequenciesOverride != null) return BlockedFrequenciesOverride();
                try { return hook?.CalledOverFrequencies() ?? Array.Empty<uint>(); }
                catch { return Array.Empty<uint>(); }
            }
        }

        /// <summary>Raised when a setting is changed from outside the settings page (the Setup page's Handoff Alert button).</summary>
        internal event EventHandler SettingsChanged;

        internal string CoordStatusText
        {
            get
            {
                if (disabledReason != null || coordDisabledReason != null) return "Coord unavailable";
                if (coordHook == null || !coordHook.IsHooked) return "Coord: waiting";
                var (remote, own, dropped) = coordConversation.Counts;
                var lines = VatSysBridge.GetActiveLines();
                string lineText = lines.Count == 0 ? "none open"
                    : string.Join(", ", lines.Select(l => $"{l.Callsign ?? "?"} {l.Type.ToLowerInvariant()} {l.State.ToLowerInvariant()}"));
                int silent = coordConversation.SilentCalls;
                // One fact per line, so it fits the VSCS's width (long ones wrap rather than being cut off).
                return $"Coord: {remote} heard, {own} you" + (dropped > 0 ? $", {dropped} dup" : "") + (silent > 0 ? $", {silent} silent" : "")
                       + Environment.NewLine + "Lines: " + lineText;
            }
        }

        /// <summary>Duck Discord's view of Discord, for the Media tab; null if Duck Discord couldn't start.</summary>
        internal DiscordState? DuckState => ducker?.State;

        internal string StatusText =>
            disabledReason != null ? "Disabled: " + disabledReason
            : (hook != null && hook.IsHooked ? $"Listening: {hook.TapCount} receiver" + (hook.TapCount == 1 ? "" : "s") : "Waiting for AFV") + Environment.NewLine + TxRxText;

        /// <summary>Frequency calls kept since vatSys started: your transmissions / calls received.</summary>
        internal string TxRxText =>
            finalizer == null ? "TX/RX: unavailable" : $"TX/RX: {finalizer.Transmitted}/{finalizer.Received}" + (OverYou.Count > 0 ? $", {OverYou.Count} over you" : "");

        public ReplayPlugin()
        {
            // vatSys loads every plugin DLL it finds, so two installs (say a vatSys Launcher one in
            // Plugins\Say-Again and an old manual one) would record and replay everything twice.
            // The first copy to load runs; any other switches itself off and says where it is.
            const string key = "AFVReplay.LoadedFrom";
            string here = typeof(ReplayPlugin).Assembly.Location;
            if (AppDomain.CurrentDomain.GetData(key) is string first && !string.Equals(first, here, StringComparison.OrdinalIgnoreCase))
            {
                disabledReason = "installed twice";
                Diag.UserError($"Say Again is installed twice. This copy is disabled: {here}. The one in use: {first}. Delete one of them.");
                return;
            }
            AppDomain.CurrentDomain.SetData(key, here);
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                disabledReason = "failed to start";
                Diag.UserError("Say Again failed to start and has been disabled.", ex);
            }
        }

        /// <summary>For the test harness: the plugin's parts without hooking vatSys.</summary>
        internal ReplayPlugin(ReplaySettings settings)
        {
            Settings = settings;
            CreateParts();
            ApplySettings(save: false);
        }

        private void CreateParts()
        {
            Store = new ClipStore();
            Player = new ReplayPlayer();
            Controller = new ReplayController(Store, Player);
            CoordStore = new ClipStore(ClipStore.CoordMaxRetention);
            CoordPlayer = new ReplayPlayer();
            CoordController = new ReplayController(CoordStore, CoordPlayer);
            Alerts = new AlertPlayer();
            OverYou = new OverYouAlert();
            Afk = new AfkMode(hold => { Store.HoldNew = hold; CoordStore.HoldNew = hold; },
                VatSysBridge.CanRequestBreak, VatSysBridge.RequestBreakOn, VatSysBridge.ToggleRequestBreak);
            // Only one replay at a time, so it's always clear what you're hearing.
            Player.PlaybackStarted += (s, e) => CoordPlayer.Stop();
            CoordPlayer.PlaybackStarted += (s, e) => Player.Stop();
        }

        /// <summary>Stops any replay, frequency or coord.</summary>
        internal void StopAll()
        {
            Player.Stop();
            CoordPlayer.Stop();
        }

        private void Start()
        {
            Settings = ReplaySettings.Load();
            CreateParts();
            ApplySettings(save: false);


            string missing = VatSysBridge.Validate();
            if (missing != null)
            {
                disabledReason = $"vatSys {VatSysBridge.VatSysVersion} is not supported by this version of the plugin";
                Diag.UserError($"Say Again is disabled: vatSys {VatSysBridge.VatSysVersion} has changed internally ({missing}). Check for a plugin update.");
                return;
            }

            finalizer = new ClipFinalizer(Store, VatSysBridge.FrequencyName);
            hook = new AudioHook(VatSysBridge.GetSoundcardSampleProvider, Store, finalizer, Player,
                isTransmitting: () => vatsys.Audio.Transmitting, catchOverYou: () => Settings.OverYouAlert,
                ownCallsign: () => Network.Callsign);
            hook.RxStarted += (s, freq) => { if (Settings.StopOnNewCall) Player.Stop(); };
            hook.OverYou += (s, e) => OnOverYou(e, vatsys.Audio.Transmitting);
            hook.Start(TimeSpan.FromMilliseconds(500));

            vatsys.Audio.TransmittingChanged += (s, e) =>
            {
                if (vatsys.Audio.Transmitting) StopAll(); // never talk over yourself
                else OnTransmitEnded();
            };

            StartCoord();
            StartMic();
            StartMediaPause();
            StartHandoffAlert();
            StartAfk();

            // Privacy: nothing survives a disconnect or shutdown.
            AFV.Disconnected += (s, e) => { hook.ResetRecordings(); ownTx?.Reset(); OverYou.Reset(); };
            Network.Disconnected += (s, e) => { hook.ResetRecordings(); ResetCoord(); OverYou.Reset(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();

            poller = new InputPoller(() => VatSysBridge.MainForm, () => Controller.Press());
            poller.SetBinding(Settings.ReplayBinding);
            poller.Start();

            coordPoller = new InputPoller(() => VatSysBridge.MainForm, () => CoordController.Press());
            coordPoller.SetBinding(Settings.CoordReplayBinding);
            coordPoller.Start();

            injector = new KeybindInjector(() => Settings.Save(),
                new KeybindInjector.Binding("Replay Last", "afvReplayKeyButton", poller,
                    () => Settings.ReplayBinding, v => Settings.ReplayBinding = v),
                new KeybindInjector.Binding("Replay Coord", "afvReplayCoordKeyButton", coordPoller,
                    () => Settings.CoordReplayBinding, v => Settings.CoordReplayBinding = v));
            injector.Start();

            vscs = new VscsInjector(this);
            vscs.Start();
        }

        private void StartCoord()
        {
            string missing = VatSysBridge.ValidateCoord();
            if (missing != null)
            {
                coordDisabledReason = missing;
                Diag.UserError($"Say Again: coord replay is unavailable with vatSys {VatSysBridge.VatSysVersion} ({missing}). Frequency replay still works.");
                return;
            }
            coordFinalizer = new ClipFinalizer(CoordStore, _ => "Coord");
            // Utterances from both sides -> conversations (bleed-checked, mixed) -> encoded clips.
            coordConversation = new CoordConversation(coordFinalizer.Enqueue, () => Store.Snapshot(),
                // One clip per line call, pauses included: vatSys's line state, backed up by line audio arriving.
                lineOpen: () => coordRecorder?.LineOpen ?? false, lines: VatSysBridge.GetActiveLines);
            coordRecorder = new CoordRecorder(coordConversation.Add,
                user => { if (Settings.StopOnNewCall) CoordPlayer.Stop(); });
            coordHook = new CoordHook(VatSysBridge.GetMumble, coordRecorder, CoordPlayer, CoordStore, coordConversation.Tick);
            coordHook.Start(TimeSpan.FromMilliseconds(250));
        }

        private void StartMic()
        {
            string missing = VatSysBridge.ValidateMic();
            if (missing != null)
            {
                Diag.UserError($"Say Again: your own voice can't be recorded with vatSys {VatSysBridge.VatSysVersion} ({missing}). Received calls still are.");
                return;
            }
            ownTx = new OwnTxRecorder(finalizer.Enqueue,
                // A transmission, not just PTT: AFV connected and (checked per press) a frequency set to transmit.
                () => vatsys.Audio.Transmitting && !vatsys.Audio.SelcalTransmitting && AFV.IsConnected,
                DescribeTransmit, gainDb: () => AFV.InputVolume);
            // If coord is unavailable, your coord side has nowhere to go: give it a recorder that drops it.
            var coord = coordRecorder ?? new CoordRecorder(r => Array.Clear(r.Pcm, 0, r.Length), null);
            micHook = new MicHook(VatSysBridge.GetAfvInputMix, VatSysBridge.GetMumble, ownTx, coord, () => Network.Callsign);
            micHook.Start(TimeSpan.FromMilliseconds(250));
        }

        /// <summary>Pause Media: other apps' media pauses while the radio is busy (see <see cref="RadioBusy"/>).</summary>
        private void StartMediaPause()
        {
            try
            {
                media = new WindowsMedia();
                mediaPauser = new MediaPauser(media, () => Settings.PauseMedia, RadioBusy, () => Settings.MediaResumeDelay);
                mediaPauser.Start(TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                media = null;
                mediaPauser = null;
                Diag.Warn("Pause Media unavailable: " + ex.GetType().Name);
            }
            try
            {
                ducker = new DiscordDucker(new WindowsAppVolume(), () => Settings.DuckDiscord, RadioBusy, () => Settings.DuckPercent, () => Settings.MediaResumeDelay);
                ducker.Start(TimeSpan.FromMilliseconds(100));
                Diag.Info("Duck Discord ready");
            }
            catch (Exception ex)
            {
                ducker = null;
                Diag.UserError("Say Again: Duck Discord is unavailable. Everything else still works.", ex);
            }
        }

        /// <summary>Handoff Alert (Matt's plugin, now part of Say Again): tones for handoffs to and from you.</summary>
        private void StartHandoffAlert()
        {
            try
            {
                handoff = new HandoffMonitor(Alerts, () => Settings.HandoffAlert, () => Settings.HandoffRepeat);
                handoff.Start();
            }
            catch (Exception ex)
            {
                handoff = null;
                Diag.UserError("Say Again: handoff alerts are unavailable. Everything else still works.", ex);
            }
        }

        private void StartAfk()
        {
            string missing = VatSysBridge.ValidateRequestBreak();
            if (missing != null)
            {
                // AFK still holds calls; it just can't request a break.
                Afk = new AfkMode(hold => { Store.HoldNew = hold; CoordStore.HoldNew = hold; }, () => false, () => false, () => { });
                Diag.UserError($"Say Again: AFK can't turn on Request Break with vatSys {VatSysBridge.VatSysVersion} ({missing}). It still keeps your calls.");
                return;
            }
            afkTimer = new Timer { Interval = 500 };
            afkTimer.Tick += (s, e) =>
            {
                try { Afk.Tick(); }
                catch (Exception ex) { afkTimer.Stop(); Diag.UserError("Say Again: AFK stopped following Request Break.", ex); }
            };
            afkTimer.Start();
        }

        /// <summary>
        /// A station called over your transmission (audio thread, once per transmission). With Blocked Tone on:
        /// the tone plays now if the timing is During, otherwise when you release PTT.
        /// </summary>
        internal void OnOverYou(OverYouEventArgs e, bool stillTransmitting)
        {
            Diag.Info("call over your transmission caught");
            bool releasedAlready = OverYou.Report(e.Callsign, ClipFinalizer.FormatFrequency(e.FrequencyHz), stillTransmitting, DateTime.UtcNow);
            if (ToneOverYou && (releasedAlready || Settings.OverYouToneDuring)) Alerts.Play(AlertTone.OverYou);
        }

        /// <summary>PTT released: with Blocked Tone on and timing After, the tone if someone called over that transmission.</summary>
        internal void OnTransmitEnded()
        {
            bool blocked = OverYou.TransmitEnded(DateTime.UtcNow);
            if (blocked && ToneOverYou && !Settings.OverYouToneDuring) Alerts.Play(AlertTone.OverYou);
        }

        /// <summary>The Setup page's Handoff Alert button.</summary>
        internal void ToggleHandoffAlert()
        {
            Settings.HandoffAlert = !Settings.HandoffAlert;
            ApplySettings();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Receiving, transmitting, a hotline/coldline open, a Say Again replay playing (not paused), or a handoff tone playing.</summary>
        private bool RadioBusy() =>
            Replaying(Player) || Replaying(CoordPlayer)
            || Alerts.IsPlaying
            || (hook?.IsReceiving ?? false)
            || vatsys.Audio.Transmitting
            || (coordRecorder?.LineOpen ?? false)
            || VatSysBridge.GetActiveLines().Any(l => l.IsOpen);

        private static bool Replaying(ReplayPlayer p) => p.PlayingClip != null && !p.IsPaused;

        /// <summary>The first frequency you're transmitting on, and your callsign.</summary>
        private static (uint hz, string callsign) DescribeTransmit()
        {
            uint hz = 0;
            foreach (var f in vatsys.Audio.VSCSFrequencies.ToList())
            {
                if (f.Transmit) { hz = f.Frequency; break; }
            }
            return (hz, Network.Callsign);
        }

        private void ResetCoord()
        {
            CoordPlayer.Stop();
            coordRecorder?.Reset();
            coordConversation?.Reset();
            coordFinalizer?.DiscardPending();
            CoordStore.Clear();
        }

        internal void ApplySettings(bool save = true)
        {
            Settings.Normalise();
            Store.Retention = TimeSpan.FromMinutes(Settings.RetentionMinutes);
            CoordStore.Retention = TimeSpan.FromMinutes(Settings.CoordRetentionMinutes);
            Player.Volume = Settings.VolumePercent / 100f;
            Player.Speed = Settings.SpeedPercent / 100f;
            CoordPlayer.Volume = Player.Volume;
            CoordPlayer.Speed = Player.Speed;
            Alerts.Volume = Settings.HandoffVolumePercent / 100f;
            if (save) Settings.Save();
        }

        private void Shutdown()
        {
            try { mediaPauser?.Dispose(); media?.Flush(TimeSpan.FromSeconds(2)); } catch { } // resume anything paused
            try { ducker?.Dispose(); } catch { } // Discord's volume back (Windows would otherwise keep it low)
            try { handoff?.Dispose(); } catch { }
            try { afkTimer?.Dispose(); } catch { }
            try { poller?.Dispose(); } catch { }
            try { vscs?.Dispose(); } catch { }
            try { micHook?.Dispose(); } catch { }
            try { coordPoller?.Dispose(); } catch { }
            try { coordHook?.Dispose(); } catch { }
            try { coordFinalizer?.Dispose(); } catch { }
            try { CoordPlayer?.Stop(); } catch { }
            try { CoordStore?.Clear(); } catch { }
            try { hook?.Dispose(); } catch { }
            try { finalizer?.Dispose(); } catch { }
            try { Player?.Stop(); } catch { }
            try { Store?.Clear(); } catch { }
        }

        public void OnFDRUpdate(FDP2.FDR updated) { }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }
    }
}
