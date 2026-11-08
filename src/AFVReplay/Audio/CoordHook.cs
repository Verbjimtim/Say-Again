using System;
using System.Threading;
using AFVReplay.VatSys;
using NAudio.Wave.SampleProviders;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Listens to the hotline/coldline audio vatSys receives (only lines you have open: vatSys
    /// mutes everyone else) and puts the coord replay player into vatSys's coord mixer, so a coord
    /// replay comes out wherever coord audio normally goes, at the vatSys coord volume.
    /// Read-only towards vatSys: it adds one event handler and one mixer input, both removed on Unhook.
    /// </summary>
    internal sealed class CoordHook : IDisposable
    {
        private readonly Func<object> getMumble;
        private readonly CoordRecorder recorder;
        private readonly ReplayPlayer player;
        private readonly ClipStore store;
        private readonly Action onTick;
        private readonly object sync = new object();
        private Timer timer;
        private object protocol;
        private Delegate subscription;
        private MixingSampleProvider mixer;

        public CoordHook(Func<object> getMumble, CoordRecorder recorder, ReplayPlayer player, ClipStore store, Action onTick = null)
        {
            this.onTick = onTick;
            this.store = store;
            this.getMumble = getMumble;
            this.recorder = recorder;
            this.player = player;
        }

        public bool IsHooked { get { lock (sync) return subscription != null; } }

        public void Start(TimeSpan interval)
        {
            timer = new Timer(_ => { try { Tick(); } catch (Exception ex) { Diag.Warn("coord tick failed: " + ex.GetType().Name); } },
                              null, TimeSpan.Zero, interval);
        }

        public void Tick()
        {
            lock (sync)
            {
                if (subscription == null)
                {
                    var mumble = getMumble();
                    if (mumble != null)
                    {
                        var p = VatSysBridge.GetMumbleProtocol(mumble);
                        var m = VatSysBridge.GetMumbleMixer(mumble);
                        if (p != null && m != null)
                        {
                            subscription = VatSysBridge.SubscribeCoordAudio(p, OnAudio);
                            protocol = p;
                            m.AddMixerInput(player);
                            mixer = m;
                            Diag.Info("hooked coord audio");
                        }
                    }
                }
            }
            recorder.Tick();
            onTick?.Invoke();
            store.Prune(DateTime.UtcNow);
        }

        private void OnAudio(MumbleSharp.Model.User user, byte[] audio)
        {
            if (user == null || audio == null) return;
            recorder.Feed(user.Name, audio, audio.Length);
        }

        public void Unhook()
        {
            lock (sync)
            {
                if (subscription != null)
                {
                    try { VatSysBridge.UnsubscribeCoordAudio(protocol, subscription); } catch { }
                    try { mixer?.RemoveMixerInput(player); } catch { }
                }
                subscription = null;
                protocol = null;
                mixer = null;
            }
            recorder.Reset();
        }

        public void Dispose()
        {
            timer?.Dispose();
            Unhook();
        }
    }
}
