using System;
using System.IO;
using System.Media;
using System.Text;
using System.Threading.Tasks;
using AFVReplay.VatSys;

namespace AFVReplay.Handoff
{
    internal enum AlertTone { Incoming, Accepted, Refused, OverYou, Quack }

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
                playingUntilUtc = (playingUntilUtc > now ? playingUntilUtc : now) + LengthOf(tone);
                queue = queue.ContinueWith(_ => PlayNow(tone), TaskScheduler.Default);
            }
        }

        /// <summary>How long each handoff tone lasts.</summary>
        public static TimeSpan Length => TimeSpan.FromMilliseconds(2 * ToneMs + GapMs);

        /// <summary>How long <paramref name="tone"/> lasts.</summary>
        public static TimeSpan LengthOf(AlertTone tone) => tone == AlertTone.Quack ? QuackLength : Length;

        private void PlayNow(AlertTone tone)
        {
            try
            {
                if (PlayOverride != null) { PlayOverride(tone, LengthOf(tone)); return; }
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
                case AlertTone.OverYou: return new[] { 1320, 1320 }; // flat, high: someone called over you
                default: return new[] { 880, 1175 };                 // rising
            }
        }

        /// <summary>Two 110 ms tones, 45 ms apart, with soft second and third harmonics: interleaved float samples.</summary>
        internal static float[] Samples(AlertTone tone, int sampleRate, int channels, float volume = 1f)
        {
            if (tone == AlertTone.Quack) return QuackSamples(sampleRate, channels, volume);
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

        // ------------------------------------------------------------------ the quack (an easter egg: see Duck Discord)

        /// <summary>
        /// The recorded quack ("Duck Quack - Sound Effect (HD)" by Tabby+Gus. on Freesound, CC0), built into the
        /// DLL as a mono 16-bit WAV and read once into memory. Empty if it can't be read.
        /// </summary>
        private static readonly Lazy<(float[] mono, int rate)> Quack = new Lazy<(float[], int)>(LoadQuack);

        private static TimeSpan QuackLength =>
            Quack.Value.rate > 0 ? TimeSpan.FromSeconds(Quack.Value.mono.Length / (double)Quack.Value.rate) : TimeSpan.Zero;

        private static (float[], int) LoadQuack()
        {
            try
            {
                using (var s = typeof(AlertPlayer).Assembly.GetManifestResourceStream("AFVReplay.quack.wav"))
                    return s == null ? (new float[0], 0) : ReadMonoWav(s);
            }
            catch (Exception ex)
            {
                Diag.Warn("quack unreadable: " + ex.GetType().Name);
                return (new float[0], 0);
            }
        }

        /// <summary>Reads a 16-bit PCM WAV, mixing it to mono floats.</summary>
        internal static (float[] mono, int rate) ReadMonoWav(Stream stream)
        {
            using (var r = new BinaryReader(stream, Encoding.ASCII, true))
            {
                if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not RIFF");
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not WAVE");
                int channels = 0, rate = 0, bits = 0;
                while (stream.Position + 8 <= stream.Length)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    long next = stream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        if (r.ReadInt16() != 1) throw new InvalidDataException("not PCM");
                        channels = r.ReadInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32();
                        r.ReadInt16();
                        bits = r.ReadInt16();
                    }
                    else if (id == "data")
                    {
                        if (bits != 16 || channels < 1) throw new InvalidDataException("not 16-bit");
                        int frames = size / 2 / channels;
                        var mono = new float[frames];
                        for (int i = 0; i < frames; i++)
                        {
                            int sum = 0;
                            for (int c = 0; c < channels; c++) sum += r.ReadInt16();
                            mono[i] = sum / (float)channels / 32768f;
                        }
                        return (mono, rate);
                    }
                    stream.Position = next;
                }
                throw new InvalidDataException("no data");
            }
        }

        /// <summary>The quack at <paramref name="sampleRate"/> (linear resampling), interleaved float samples.</summary>
        internal static float[] QuackSamples(int sampleRate, int channels, float volume = 1f)
        {
            var (mono, rate) = Quack.Value;
            if (rate <= 0 || mono.Length == 0) return new float[0];
            int frames = (int)((long)mono.Length * sampleRate / rate);
            var data = new float[frames * channels];
            double step = (double)rate / sampleRate;
            for (int i = 0; i < frames; i++)
            {
                double pos = i * step;
                int j = (int)pos;
                double f = pos - j;
                double a = mono[Math.Min(j, mono.Length - 1)], b = mono[Math.Min(j + 1, mono.Length - 1)];
                float v = (float)((a + (b - a) * f) * volume);
                for (int c = 0; c < channels; c++) data[i * channels + c] = v;
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
