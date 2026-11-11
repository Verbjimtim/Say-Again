using System;
using System.Collections.Generic;
using System.Linq;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Keeps frequency and coord apart. vatSys lines are open mics, so the same speech can reach you
    /// both ways:
    ///  - a controller you have a line open with keys PTT: their transmission arrives on frequency
    ///    AND on the line (the same applies to you, keying PTT with a line open);
    ///  - vatSys mixes each controller's frequency audio into what they send on a line (at 1.4%),
    ///    so frequency calls can turn up faintly inside a coord stream.
    /// The frequency clip is the real one; the coord copy is dropped (checked per utterance when a
    /// conversation is put together, by which time the frequency clip has landed).
    /// </summary>
    internal static class CoordBleed
    {
        /// <summary>Coord clips of the same speaker overlapping a frequency clip by this much are duplicates.</summary>
        public const double SameSpeakerOverlap = 0.5;

        /// <summary>
        /// Loopback: vatSys mixes a controller's frequency audio into their line at 1.4% (about -37 dB).
        /// Coord audio that mostly coincides with frequency traffic and is below this fraction of that
        /// traffic's speech level (about -26 dB, a 3-4x margin over the loopback) is that copy. Real line
        /// speech, even a quiet raw mic on a busy frequency, is well above it.
        /// </summary>
        public const double LoopbackRatio = 0.05;
        public const double LoopbackOverlap = 0.6;

        /// <summary>Loopback threshold (real speech RMS) when the frequency traffic's level isn't known.</summary>
        public const double LoopbackFloor = 0.004;

        /// <param name="speechLevel">The coord utterance's speech level (<see cref="AudioLevel.Speech"/>).</param>
        public static bool IsDuplicate(RawRecording coordRec, double speechLevel, IReadOnlyList<Clip> freqClips)
        {
            string speaker = coordRec.Callsigns.FirstOrDefault();
            double total = (coordRec.EndUtc - coordRec.StartUtc).TotalSeconds;
            if (total <= 0) return false;

            double anyOverlap = 0, loudest = 0;
            foreach (var f in freqClips)
            {
                if (f.IsCoord) continue;
                double o = Overlap(coordRec.StartUtc, coordRec.EndUtc, f.StartUtc, f.EndUtc);
                if (o <= 0) continue;
                if (speaker != null && f.Callsigns.Any(c => string.Equals(c, speaker, StringComparison.OrdinalIgnoreCase))
                    && o / total >= SameSpeakerOverlap)
                {
                    return true;
                }
                anyOverlap += o;
                loudest = Math.Max(loudest, f.SpeechLevel);
            }
            double threshold = loudest > 0 ? loudest * LoopbackRatio : LoopbackFloor;
            return speechLevel < threshold && Math.Min(anyOverlap, total) / total >= LoopbackOverlap;
        }

        private static double Overlap(DateTime a0, DateTime a1, DateTime b0, DateTime b1)
        {
            var start = a0 > b0 ? a0 : b0;
            var end = a1 < b1 ? a1 : b1;
            return end > start ? (end - start).TotalSeconds : 0;
        }
    }
}
