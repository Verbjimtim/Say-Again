using System;
using System.Linq;
using System.ComponentModel.Composition;
using System.Windows.Forms;
using AFVReplay.Audio;
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

        internal string CoordStatusText
        {
            get
            {
                if (disabledReason != null || coordDisabledReason != null) return "Coord unavailable";
                if (coordHook == null || !coordHook.IsHooked) return "Coord: waiting";
                var (remote, own, dropped) = coordConversation.Counts;
                var lines = VatSysBridge.GetActiveLines();
                string lineText = lines.Count == 0 ? "no lines open"
                    : string.Join(", ", lines.Select(l => $"{l.Callsign ?? "?"} {l.Type.ToLowerInvariant()} {l.State.ToLowerInvariant()}"));
                return $"Coord: {remote} heard, {own} you" + (dropped > 0 ? $", {dropped} dup" : "") + "; " + lineText;
            }
        }

        internal string StatusText =>
            disabledReason != null ? "Disabled: " + disabledReason
            : hook != null && hook.IsHooked ? $"Listening on {hook.TapCount} receiver(s)"
            : "Waiting for AFV to connect";

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
            hook = new AudioHook(VatSysBridge.GetSoundcardSampleProvider, Store, finalizer, Player);
            hook.RxStarted += (s, freq) => { if (Settings.StopOnNewCall) Player.Stop(); };
            hook.Start(TimeSpan.FromMilliseconds(500));

            // Never talk over yourself.
            vatsys.Audio.TransmittingChanged += (s, e) => { if (vatsys.Audio.Transmitting) StopAll(); };

            StartCoord();
            StartMic();

            // Privacy: nothing survives a disconnect or shutdown.
            AFV.Disconnected += (s, e) => { hook.ResetRecordings(); ownTx?.Reset(); };
            Network.Disconnected += (s, e) => { hook.ResetRecordings(); ResetCoord(); };
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
            if (save) Settings.Save();
        }

        private void Shutdown()
        {
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
