using System;
using System.IO;
using System.Media;
using System.Text;
using System.Threading.Tasks;
using AFVReplay.VatSys;

namespace AFVReplay.Handoff
{
    internal enum AlertTone { Incoming, Accepted, Refused }

    /// <summary>
    /// Plays the handoff tones (Matt's: rising for a handoff to you, falling for your handoff accepted, flat
    /// for refused). They use vatSys's alert output, or its configured primary output if the speaker route
    /// is unavailable, and Windows' default output only if vatSys audio is unavailable. Generated in memory:
    /// nothing is read from or written to disk. Calls queue in order on a background task, never blocking vatSys.
    /// </summary>
    internal sealed class AlertPlayer
    {
        private readonly object sync = new object();
        private Task queue = Task.CompletedTask;
        private DateTime playingUntilUtc;
        private bool reportedFallback;

        /// <summary>0..1, applied on top of vatSys's alert volume.</summary>
        public float Volume { get; set; } = 1f;

        /// <summary>For tests: replaces the actual playing. Gets the tone and its length.</summary>
        internal Action<AlertTone, TimeSpan> PlayOverride;

        /// <summary>True while a tone is playing (for Pause Media).</summary>
        public bool IsPlaying { get { lock (sync) return DateTime.UtcNow < playingUntilUtc; } }

        public void Play(AlertTone tone)
        {
            // Busy from now until the tone ends (or, if tones queue up, until the last one does).
            lock (sync)
            {
                var now = DateTime.UtcNow;
                playingUntilUtc = (playingUntilUtc > now ? playingUntilUtc : now) + Length;
                queue = queue.ContinueWith(_ => PlayNow(tone), TaskScheduler.Default);
            }
        }

        /// <summary>How long each tone lasts.</summary>
        public static TimeSpan Length => TimeSpan.FromMilliseconds(2 * ToneMs + GapMs);

        private void PlayNow(AlertTone tone)
        {
            try
            {
                if (PlayOverride != null) { PlayOverride(tone, Length); return; }
                float volume = Volume;
                string why = TryVatSys(tone, volume);
                if (why == null) return;
                if (!reportedFallback)
                {
                    reportedFallback = true;
                    Diag.UserError("Say Again: handoff tones are playing through Windows' default output, not vatSys audio (" + why + ").");
                }
                using (var wav = Wav(tone, 48000, 2, volume))
                using (var player = new SoundPlayer(wav))
                    player.PlaySync();
            }
            catch (Exception ex)
            {
                Diag.Warn("handoff tone failed: " + ex.GetType().Name);
            }
        }

        /// <summary>Plays through vatSys. Returns null on success, otherwise why not.</summary>
        private static string TryVatSys(AlertTone tone, float volume)
        {
            var format = VatSysBridge.GetAlertFormat();
            if (format == null) return VatSysBridge.ValidateAlertSound() ?? "vatSys audio not started";
            try
            {
                VatSysBridge.PlayAlertSound(Samples(tone, format.Value.rate, format.Value.channels, volume), format.Value.rate, format.Value.channels);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetBaseException().GetType().Name;
            }
        }

        // ------------------------------------------------------------------ the tones (Matt's)

        private const int ToneMs = 110, GapMs = 45;

        private static int[] Frequencies(AlertTone tone)
        {
            switch (tone)
            {
                case AlertTone.Accepted: return new[] { 1175, 880 }; // falling
                case AlertTone.Refused: return new[] { 440, 440 };   // flat
                default: return new[] { 880, 1175 };                 // rising
            }
        }

        /// <summary>Two 110 ms tones, 45 ms apart, with soft second and third harmonics: interleaved float samples.</summary>
        internal static float[] Samples(AlertTone tone, int sampleRate, int channels, float volume = 1f)
        {
            var freqs = Frequencies(tone);
            int toneSamples = sampleRate * ToneMs / 1000;
            int gapSamples = sampleRate * GapMs / 1000;
            int frames = freqs.Length * toneSamples + (freqs.Length - 1) * gapSamples;
            var data = new float[frames * channels];
            int frame = 0;
            for (int t = 0; t < freqs.Length; t++)
            {
                for (int i = 0; i < toneSamples; i++, frame++)
                {
                    double envelope = Math.Min(1.0, Math.Min(i / 40.0, (toneSamples - i) / 40.0));
                    double phase = 2.0 * Math.PI * freqs[t] * i / sampleRate;
                    double sample = (Math.Sin(phase) + 0.25 * Math.Sin(2 * phase) + 0.10 * Math.Sin(3 * phase)) * envelope;
                    float v = (float)(sample * 14000 / 32768.0 * volume);
                    for (int c = 0; c < channels; c++) data[frame * channels + c] = v;
                }
                if (t < freqs.Length - 1) frame += gapSamples; // silence
            }
            return data;
        }

        /// <summary>The tone as a 16-bit WAV in memory, for Windows' default output.</summary>
        internal static MemoryStream Wav(AlertTone tone, int sampleRate, short channels, float volume = 1f)
        {
            var samples = Samples(tone, sampleRate, channels, volume);
            var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                int dataLength = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLength);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write(channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataLength);
                foreach (var s in samples) w.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(s * 32768))));
            }
            stream.Position = 0;
            return stream;
        }
    }
}
