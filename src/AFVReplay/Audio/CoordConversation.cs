using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Joins coord utterances (the other controller's and yours) into one conversation clip, mixed
    /// on a shared timeline, so a coord replay plays both sides as you heard them.
    /// A conversation is one line call: it lasts as long as the line is open, pauses included (kept
    /// as silence at their real length), and ends a few seconds after the line closes. "Open" comes
    /// from vatSys's own line state (a hotline/coldline in its Open state), backed up by line audio
    /// still arriving. Without either it ends after a few seconds with nobody talking. Very long
    /// calls are split. The clip is named after the kind of line and lists the other controller
    /// even if they never spoke.
    /// Each utterance is checked for frequency bleed before it's mixed in (see <see cref="CoordBleed"/>).
    /// </summary>
    internal sealed class CoordConversation
    {
        /// <summary>How long after the line closes (or, with no line information, after the last word) a call ends.</summary>
        public static readonly TimeSpan Gap = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

        /// <summary>Speech level each side is brought up to (real RMS), and the most it is boosted.</summary>
        public const double TargetSpeechLevel = 0.1;
        public const double MaxBoost = 8;

        private readonly object sync = new object();
        private readonly Action<RawRecording> onComplete;
        private readonly Func<IReadOnlyList<Clip>> freqClips;
        private readonly Func<DateTime> clock;
        private readonly Func<bool> lineOpen;
        private readonly Func<IReadOnlyList<LineInfo>> lines;
        private DateTime lastOpenUtc = DateTime.MinValue;

        // Lines open during the current call (or just before it): who it was with, and what kind.
        private readonly List<LineInfo> callLines = new List<LineInfo>();
        private readonly List<RawRecording> parts = new List<RawRecording>();
        private readonly List<string> speakers = new List<string>();

        private int heardRemote, heardOwn, dropped;

        public CoordConversation(Action<RawRecording> onComplete, Func<IReadOnlyList<Clip>> freqClips, Func<DateTime> clock = null,
                                 Func<bool> lineOpen = null, Func<IReadOnlyList<LineInfo>> lines = null)
        {
            this.lineOpen = lineOpen ?? (() => false);
            this.lines = lines ?? (() => Array.Empty<LineInfo>());
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
                    var start = parts.Min(p => p.StartUtc);
                    // The line closing ends a call in Tick, so a gap only splits when it was closed in between.
                    if (utterance.StartUtc - LastActivity() > Gap || utterance.EndUtc - start > MaxLength)
                        flushed = Flush();
                }
                parts.Add(utterance);
            }
            if (flushed != null) onComplete(flushed);
        }

        /// <summary>Ends the conversation once the line has been closed (or silent, without line information) for <see cref="Gap"/>. Call regularly.</summary>
        public void Tick()
        {
            RawRecording flushed = null;
            lock (sync)
            {
                var now = clock();
                IReadOnlyList<LineInfo> current;
                try { current = lines() ?? Array.Empty<LineInfo>(); } catch { current = Array.Empty<LineInfo>(); }
                var open = current.Where(l => l.IsOpen).ToList();
                foreach (var l in open)
                {
                    if (!callLines.Any(c => string.Equals(c.Callsign, l.Callsign, StringComparison.OrdinalIgnoreCase) && c.Type == l.Type))
                        callLines.Add(l);
                }
                if (open.Count > 0 || lineOpen()) lastOpenUtc = now;
                if (parts.Count > 0 && now - LastActivity() > Gap) flushed = Flush();
                // A line that opened and closed with nobody talking leaves nothing for the next call.
                else if (parts.Count == 0 && now - lastOpenUtc > Gap) callLines.Clear();
            }
            if (flushed != null) onComplete(flushed);
        }

        /// <summary>The later of the last word and the last moment the line was seen open.</summary>
        private DateTime LastActivity()
        {
            var end = parts.Max(p => p.EndUtc);
            return lastOpenUtc > end ? lastOpenUtc : end;
        }

        public void Reset()
        {
            lock (sync)
            {
                foreach (var p in parts) Array.Clear(p.Pcm, 0, p.Pcm.Length);
                parts.Clear();
                callLines.Clear();
            }
        }

        private RawRecording Flush()
        {
            // Bleed check now, a few seconds after the talking: any frequency clip it duplicates has landed by then.
            var freq = freqClips();
            var kept = new List<RawRecording>();
            foreach (var p in parts)
            {
                if (CoordBleed.IsDuplicate(p, AudioLevel.Speech(p.Pcm, p.Length), freq))
                {
                    Array.Clear(p.Pcm, 0, p.Pcm.Length);
                    dropped++;
                }
                else kept.Add(p);
            }
            parts.Clear();
            var withLines = callLines.ToList();
            // Lines still open carry on into the next part of a split call; closed ones are done with.
            if (clock() - lastOpenUtc > TimeSpan.FromSeconds(1)) callLines.Clear();
            if (kept.Count == 0) return null;

            var start = kept.Min(p => p.StartUtc);
            var end = kept.Max(p => p.EndUtc);
            int length = (int)Math.Ceiling((end - start).TotalSeconds * AudioFormat.SampleRate);
            // Mixed straight into 16-bit (saturating), so a long call with long pauses stays small.
            var pcm = new short[length];
            foreach (var p in kept)
            {
                int at = (int)Math.Round((p.StartUtc - start).TotalSeconds * AudioFormat.SampleRate);
                int n = Math.Min(p.Length, length - at);
                double g = BoostFor(p);
                for (int i = 0; i < n; i++)
                {
                    int v = pcm[at + i] + (int)(p.Pcm[i] * g);
                    pcm[at + i] = (short)(v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : v);
                }
            }

            // Other controllers first (in the order they spoke), then you.
            var speakers = kept.Where(p => !p.IsOwn).Select(p => p.Callsigns.FirstOrDefault()).Where(n => n != null).Distinct().ToList();
            // The other end of the line, even if they never spoke.
            var names = speakers.ToList();
            foreach (var l in withLines)
            {
                if (!string.IsNullOrEmpty(l.Callsign) && !names.Any(n => string.Equals(n, l.Callsign, StringComparison.OrdinalIgnoreCase)))
                    names.Add(l.Callsign);
            }
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
                IsOwn = speakers.Count == 0, // only you spoke: the replay key skips it
                Name = withLines.Select(l => l.Type).FirstOrDefault(),
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
            double speech = AudioLevel.Speech(r.Pcm, r.Length);
            if (speech <= 1e-6) return 1;
            return Math.Max(1, Math.Min(MaxBoost, TargetSpeechLevel / speech));
        }

    }
}
