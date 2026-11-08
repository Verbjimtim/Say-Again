using System;
using System.Collections.Generic;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Records your own frequency transmissions: your AFV mic while PTT is held, one clip per press.
    /// Called on vatSys's mic capture thread with the same audio AFV encodes and sends.
    /// </summary>
    internal sealed class OwnTxRecorder
    {
        public static readonly int MinSamples = AudioFormat.SampleRate / 4;      // 250 ms
        public static readonly int MaxClipSamples = AudioFormat.SampleRate * 90;
        public static readonly TimeSpan StreamTimeout = TimeSpan.FromMilliseconds(400);

        private readonly object sync = new object();
        private readonly Action<RawRecording> onComplete;
        private readonly Func<bool> isTransmitting;
        private readonly Func<(uint hz, string callsign)> describe;
        private readonly Func<DateTime> clock;
        private readonly Func<float> gainDb;
        private float gain = 1f;

        private short[] clip;
        private int clipLen;
        private DateTime startUtc;
        private uint frequencyHz;
        private string callsign;
        private DateTime lastAudioUtc;
        private bool ignoringPress; // PTT held but nothing is being transmitted (no transmit frequency)

        /// <param name="describe">The frequency you're transmitting on and your callsign, read when PTT goes down.</param>
        public OwnTxRecorder(Action<RawRecording> onComplete, Func<bool> isTransmitting,
                             Func<(uint hz, string callsign)> describe, Func<DateTime> clock = null, Func<float> gainDb = null)
        {
            this.gainDb = gainDb;
            this.onComplete = onComplete;
            this.isTransmitting = isTransmitting;
            this.describe = describe;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public bool IsRecording { get { lock (sync) return clip != null; } }

        /// <summary>16-bit little-endian mono 48 kHz mic audio.</summary>
        public void Feed(byte[] pcm16, int offset, int byteCount)
        {
            var now = clock();
            bool tx = isTransmitting();
            var done = new List<RawRecording>(1);
            lock (sync)
            {
                lastAudioUtc = now;
                if (!tx)
                {
                    ignoringPress = false;
                    var r = Finish(now);
                    if (r != null) done.Add(r);
                }
                else if (clip == null && (ignoringPress || !Begin(now)))
                {
                    // PTT held but this press transmits nothing (no frequency set to transmit): wait for release.
                    ignoringPress = true;
                }
                else
                {
                    int samples = byteCount / 2;
                    Ensure(samples);
                    for (int i = 0; i < samples; i++)
                    {
                        short v = (short)(pcm16[offset + 2 * i] | (pcm16[offset + 2 * i + 1] << 8));
                        float x = v * gain / AudioFormat.StorageHeadroom;
                        clip[clipLen++] = (short)(x > short.MaxValue ? short.MaxValue : x < short.MinValue ? short.MinValue : x);
                    }
                    if (clipLen >= MaxClipSamples)
                    {
                        var r = Finish(now);
                        if (r != null) done.Add(r);
                    }
                }
            }
            foreach (var r in done) onComplete(r);
        }

        /// <summary>Closes the clip if PTT was released or the mic stopped. Call regularly.</summary>
        public void Tick()
        {
            var now = clock();
            RawRecording r = null;
            lock (sync)
            {
                if (clip != null && (!isTransmitting() || now - lastAudioUtc > StreamTimeout)) r = Finish(now);
            }
            if (r != null) onComplete(r);
        }

        public void Reset()
        {
            lock (sync)
            {
                if (clip != null) Array.Clear(clip, 0, clip.Length);
                clip = null;
                clipLen = 0;
            }
        }

        /// <summary>Starts a clip. Returns false (and records nothing) when no frequency is set to transmit.</summary>
        private bool Begin(DateTime now)
        {
            clip = new short[AudioFormat.SampleRate * 8];
            clipLen = 0;
            startUtc = now;
            // AFV boosts or cuts the mic by the vatSys input volume (dB) before sending: match what was sent.
            try { gain = gainDb == null ? 1f : (float)Math.Pow(10, Math.Max(-18f, Math.Min(18f, gainDb())) / 20.0); }
            catch { gain = 1f; }
            try
            {
                var d = describe();
                frequencyHz = d.hz;
                callsign = string.IsNullOrEmpty(d.callsign) ? "You" : d.callsign;
            }
            catch
            {
                frequencyHz = 0;
                callsign = "You";
            }
            if (frequencyHz == 0)
            {
                // PTT pressed but nothing goes out (e.g. connected as an observer): not a transmission.
                Array.Clear(clip, 0, clip.Length);
                clip = null;
                return false;
            }
            return true;
        }

        private void Ensure(int more)
        {
            if (clipLen + more <= clip.Length) return;
            var bigger = new short[Math.Max(clip.Length * 2, clipLen + more)];
            Array.Copy(clip, bigger, clipLen);
            Array.Clear(clip, 0, clipLen);
            clip = bigger;
        }

        private RawRecording Finish(DateTime now)
        {
            if (clip == null) return null;
            var pcm = clip;
            int len = clipLen;
            clip = null;
            clipLen = 0;
            if (len < MinSamples)
            {
                Array.Clear(pcm, 0, pcm.Length);
                return null;
            }
            return new RawRecording
            {
                Pcm = pcm,
                Length = len,
                StartUtc = startUtc,
                EndUtc = startUtc + TimeSpan.FromSeconds(len / (double)AudioFormat.SampleRate),
                FrequencyHz = frequencyHz,
                Callsigns = new List<string> { callsign },
                Blocked = false,
                IsOwn = true,
            };
        }
    }
}
