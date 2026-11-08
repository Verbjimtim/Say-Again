using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Joins coord utterances (the other controller's and yours) into one conversation clip, mixed
    /// on a shared timeline, so a coord replay plays both sides as you heard them.
    /// A conversation ends after a few seconds with nobody talking, or when it gets very long.
    /// Each utterance is checked for frequency bleed before it's mixed in (see <see cref="CoordBleed"/>).
    /// </summary>
    internal sealed class CoordConversation
    {
        public static readonly TimeSpan Gap = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(120);

        /// <summary>Speech level each side is brought up to (real RMS), and the most it is boosted.</summary>
        public const double TargetSpeechLevel = 0.1;
        public const double MaxBoost = 8;

        private readonly object sync = new object();
        private readonly Action<RawRecording> onComplete;
        private readonly Func<IReadOnlyList<Clip>> freqClips;
        private readonly Func<DateTime> clock;
        private readonly List<RawRecording> parts = new List<RawRecording>();
        private readonly List<string> speakers = new List<string>();

        private int heardRemote, heardOwn, dropped;

        public CoordConversation(Action<RawRecording> onComplete, Func<IReadOnlyList<Clip>> freqClips, Func<DateTime> clock = null)
        {
            this.onComplete = onComplete;
            this.freqClips = freqClips ?? (() => Array.Empty<Clip>());
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Utterances heard from other controllers / from you, and copies dropped as frequency bleed.</summary>
        public (int remote, int own, int dropped) Counts
        {
            get { lock (sync) return (heardRemote, heardOwn, dropped); }
        }

        public bool InProgress { get { lock (sync) return parts.Count > 0; } }

        /// <summary>An utterance from <see cref="CoordRecorder"/>.</summary>
        public void Add(RawRecording utterance)
        {
            RawRecording flushed = null;
            lock (sync)
            {
                if (utterance.IsOwn) heardOwn++; else heardRemote++;
                if (parts.Count > 0)
                {
                    var end = parts.Max(p => p.EndUtc);
                    var start = parts.Min(p => p.StartUtc);
                    if (utterance.StartUtc - end > Gap || utterance.EndUtc - start > MaxLength)
                        flushed = Flush();
                }
                parts.Add(utterance);
            }
            if (flushed != null) onComplete(flushed);
        }

        /// <summary>Ends the conversation once nobody has spoken for <see cref="Gap"/>. Call regularly.</summary>
        public void Tick()
        {
            RawRecording flushed = null;
            lock (sync)
            {
                if (parts.Count > 0 && clock() - parts.Max(p => p.EndUtc) > Gap) flushed = Flush();
            }
            if (flushed != null) onComplete(flushed);
        }

        public void Reset()
        {
            lock (sync)
            {
                foreach (var p in parts) Array.Clear(p.Pcm, 0, p.Pcm.Length);
                parts.Clear();
            }
        }

        private RawRecording Flush()
        {
            // Bleed check now, a few seconds after the talking: any frequency clip it duplicates has landed by then.
            var freq = freqClips();
            var kept = new List<RawRecording>();
            foreach (var p in parts)
            {
                if (CoordBleed.IsDuplicate(p, Level(p), freq))
                {
                    Array.Clear(p.Pcm, 0, p.Pcm.Length);
                    dropped++;
                }
                else kept.Add(p);
            }
            parts.Clear();
            if (kept.Count == 0) return null;

            var start = kept.Min(p => p.StartUtc);
            var end = kept.Max(p => p.EndUtc);
            int length = (int)Math.Ceiling((end - start).TotalSeconds * AudioFormat.SampleRate);
            var mix = new int[length];
            foreach (var p in kept)
            {
                int at = (int)Math.Round((p.StartUtc - start).TotalSeconds * AudioFormat.SampleRate);
                int n = Math.Min(p.Length, length - at);
                double g = BoostFor(p);
                for (int i = 0; i < n; i++) mix[at + i] += (int)(p.Pcm[i] * g);
            }
            var pcm = new short[length];
            for (int i = 0; i < length; i++) pcm[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mix[i]));
            Array.Clear(mix, 0, mix.Length);

            // Other controllers first (in the order they spoke), then you.
            var names = kept.Where(p => !p.IsOwn).Select(p => p.Callsigns.FirstOrDefault()).Where(n => n != null).Distinct().ToList();
            bool you = kept.Any(p => p.IsOwn);
            foreach (var p in kept) Array.Clear(p.Pcm, 0, p.Pcm.Length);

            return new RawRecording
            {
                Pcm = pcm,
                Length = length,
                StartUtc = start,
                EndUtc = end,
                FrequencyHz = 0,
                Callsigns = names.Concat(you ? new[] { CoordYou } : Array.Empty<string>()).ToList(),
                IsOwn = names.Count == 0,
            };
        }

        /// <summary>How you appear in a conversation's callsign list.</summary>
        public const string CoordYou = "You";

        /// <summary>
        /// Gain that brings an utterance's speech (its louder half, so pauses don't count) up to
        /// <see cref="TargetSpeechLevel"/>. Only ever boosts, at most <see cref="MaxBoost"/>.
        /// </summary>
        internal static double BoostFor(RawRecording r)
        {
            const int block = 480;
            int blocks = r.Length / block;
            if (blocks == 0) return 1;
            var levels = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                double sum = 0;
                for (int i = b * block; i < (b + 1) * block; i++) { double s = r.Pcm[i] / 32768.0; sum += s * s; }
                levels[b] = sum / block;
            }
            Array.Sort(levels);
            double top = 0;
            int count = 0;
            for (int b = blocks / 2; b < blocks; b++) { top += levels[b]; count++; }
            double speech = Math.Sqrt(top / Math.Max(1, count)) * AudioFormat.StorageHeadroom;
            if (speech <= 1e-6) return 1;
            return Math.Max(1, Math.Min(MaxBoost, TargetSpeechLevel / speech));
        }

        /// <summary>Real-level RMS of an utterance (stored audio is headroom-scaled).</summary>
        private static double Level(RawRecording r)
        {
            if (r.Length == 0) return 0;
            double sum = 0;
            for (int i = 0; i < r.Length; i++) { double s = r.Pcm[i] / 32768.0; sum += s * s; }
            return Math.Sqrt(sum / r.Length) * AudioFormat.StorageHeadroom;
        }
    }
}
