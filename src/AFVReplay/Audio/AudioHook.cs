using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GeoVR.Client;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Keeps a <see cref="ReceiverTap"/> in front of every AFV receiver and the replay player in the mix.
    /// AFV adds and removes receivers as frequencies change and rebuilds everything on reconnect,
    /// so this re-checks twice a second.
    /// </summary>
    internal sealed class AudioHook : IDisposable
    {
        private readonly Func<SoundcardSampleProvider> getSoundcard;
        private readonly ClipStore store;
        private readonly ClipFinalizer finalizer;
        private readonly ReplayPlayer player;
        private readonly Dictionary<ReceiverSampleProvider, ReceiverTap> taps = new Dictionary<ReceiverSampleProvider, ReceiverTap>();
        private readonly object tickLock = new object();
        private readonly Func<bool> isTransmitting;
        private readonly Func<bool> catchOverYou;
        private readonly Func<string> ownCallsign;
        private Timer timer;
        private SoundcardSampleProvider current;

        /// <param name="isTransmitting">True while you're transmitting (vatSys PTT). Null: calls over you aren't caught.</param>
        /// <param name="catchOverYou">The setting for catching calls over you.</param>
        /// <param name="ownCallsign">Your callsign.</param>
        public AudioHook(Func<SoundcardSampleProvider> getSoundcard, ClipStore store, ClipFinalizer finalizer, ReplayPlayer player,
                         Func<bool> isTransmitting = null, Func<bool> catchOverYou = null, Func<string> ownCallsign = null)
        {
            this.getSoundcard = getSoundcard;
            this.store = store;
            this.finalizer = finalizer;
            this.player = player;
            this.isTransmitting = isTransmitting;
            this.catchOverYou = catchOverYou;
            this.ownCallsign = ownCallsign;
        }

        /// <summary>A new transmission has started on some frequency (raised on the audio thread).</summary>
        public event EventHandler<uint> RxStarted;

        /// <summary>A station called on a frequency while you were transmitting on it (raised on the audio thread).</summary>
        public event EventHandler<OverYouEventArgs> OverYou;

        public bool IsHooked => current != null;

        public int TapCount { get { lock (tickLock) return taps.Count; } }

        /// <summary>True while any frequency you're listening to is receiving a call.</summary>
        public bool IsReceiving { get { lock (tickLock) return taps.Values.Any(t => t.IsRecording); } }

        /// <summary>Frequencies a station is calling on right now, over your transmission.</summary>
        public uint[] CalledOverFrequencies()
        {
            lock (tickLock) return taps.Values.Where(t => t.IsCalledOver).Select(t => t.Receiver.Frequency).Distinct().ToArray();
        }

        public void Start(TimeSpan interval)
        {
            timer = new Timer(_ => Tick(), null, TimeSpan.Zero, interval);
        }

        public void Tick()
        {
            if (!Monitor.TryEnter(tickLock)) return;
            try
            {
                store.Prune(DateTime.UtcNow);

                SoundcardSampleProvider ssp;
                try { ssp = getSoundcard(); }
                catch (Exception ex) { Diag.Warn("soundcard lookup failed: " + ex.GetType().Name); ssp = null; }

                if (!ReferenceEquals(ssp, current))
                {
                    Unhook();
                    current = ssp;
                    if (ssp != null) Diag.Info("hooked AFV soundcard");
                }
                if (current != null) Reconcile(current);
            }
            catch (Exception ex)
            {
                Diag.Warn("tick failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Monitor.Exit(tickLock);
            }
        }

        private void Reconcile(SoundcardSampleProvider ssp)
        {
            var mixer = VatSys.VatSysBridge.GetMixer(ssp);
            var sources = VatSys.VatSysBridge.GetSources(mixer);
            var live = new HashSet<ReceiverSampleProvider>(VatSys.VatSysBridge.GetReceivers(ssp).Values);

            lock (sources)
            {
                for (int i = sources.Count - 1; i >= 0; i--)
                {
                    switch (sources[i])
                    {
                        case ReceiverSampleProvider receiver:
                            var tap = new ReceiverTap(receiver, finalizer.Enqueue, OnRxStart, isTransmitting, catchOverYou, ownCallsign, OnOverYou);
                            sources[i] = tap;
                            taps[receiver] = tap;
                            break;

                        case ReceiverTap existing when !live.Contains(existing.Receiver):
                            // AFV dropped this receiver but couldn't find it in the mixer (our tap was there instead).
                            sources.RemoveAt(i);
                            existing.Detach();
                            taps.Remove(existing.Receiver);
                            break;
                    }
                }

                if (!sources.Contains(player) && player.WaveFormat.Equals(mixer.WaveFormat))
                {
                    sources.Add(player);
                }
            }
        }

        private void OnRxStart(uint frequencyHz) => RxStarted?.Invoke(this, frequencyHz);

        private void OnOverYou(uint frequencyHz, string callsign) => OverYou?.Invoke(this, new OverYouEventArgs(frequencyHz, callsign));

        /// <summary>Puts the original receivers back and removes the player.</summary>
        private void Unhook()
        {
            if (current != null)
            {
                try
                {
                    var sources = VatSys.VatSysBridge.GetSources(VatSys.VatSysBridge.GetMixer(current));
                    lock (sources)
                    {
                        for (int i = sources.Count - 1; i >= 0; i--)
                        {
                            if (sources[i] is ReceiverTap tap) sources[i] = tap.Receiver;
                            else if (ReferenceEquals(sources[i], player)) sources.RemoveAt(i);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Diag.Warn("unhook failed: " + ex.GetType().Name);
                }
            }
            foreach (var tap in taps.Values) tap.Detach();
            taps.Clear();
            current = null;
        }

        /// <summary>Drops everything recorded or in progress. Safe from any thread.</summary>
        public void ResetRecordings()
        {
            lock (tickLock)
            {
                foreach (var tap in taps.Values) tap.ResetRecording();
            }
            finalizer.DiscardPending();
            player.Stop();
            store.Clear();
        }

        public void Dispose()
        {
            timer?.Dispose();
            lock (tickLock) Unhook();
        }
    }

    internal sealed class OverYouEventArgs : EventArgs
    {
        public OverYouEventArgs(uint frequencyHz, string callsign) { FrequencyHz = frequencyHz; Callsign = callsign; }
        public uint FrequencyHz { get; }
        public string Callsign { get; }
    }
}
