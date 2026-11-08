using System;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Speeds speech up without changing its pitch (WSOLA: overlap-add of short windows, each one
    /// nudged to line up with the waveform of the last so there is no warble). Cheap enough to run
    /// on a whole clip when it starts playing.
    /// </summary>
    internal static class TimeStretch
    {
        private const int Window = 960;          // 20 ms at 48 kHz
        private const int Hop = Window / 2;      // output step, 50% overlap
        private const int Search = 240;          // ±5 ms alignment search
        private const int Stride = 2;            // correlation decimation, plenty for speech

        private static readonly float[] Hann = MakeHann();

        private static float[] MakeHann()
        {
            var w = new float[Window];
            for (int i = 0; i < Window; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Window));
            return w;
        }

        /// <summary>Returns a new buffer <paramref name="speed"/> times shorter. speed 1 returns the input.</summary>
        public static float[] Apply(float[] input, double speed)
        {
            if (input == null || speed <= 1.001 || input.Length < Window * 4) return input;

            int outLen = (int)(input.Length / speed);
            var output = new float[outLen + Window];
            double analysisHop = Hop * speed;

            // Seed with the first window unchanged.
            for (int i = 0; i < Window; i++) output[i] = input[i] * Hann[i];
            int prevSrc = 0; // where the window just written came from

            for (int k = 1; ; k++)
            {
                int outPos = k * Hop;
                if (outPos + Window > output.Length) break;

                // The natural continuation of the last window is what we try to match.
                int natural = prevSrc + Hop;
                int nominal = (int)(k * analysisHop);
                int lo = Math.Max(0, nominal - Search);
                int hi = Math.Min(input.Length - Window, nominal + Search);
                if (lo > hi || natural + Hop > input.Length) break;

                int best = Math.Max(lo, Math.Min(hi, nominal));
                double bestScore = double.NegativeInfinity;
                for (int cand = lo; cand <= hi; cand += Stride)
                {
                    double score = 0;
                    for (int i = 0; i < Hop; i += Stride) score += input[natural + i] * input[cand + i];
                    if (score > bestScore) { bestScore = score; best = cand; }
                }

                for (int i = 0; i < Window; i++) output[outPos + i] += input[best + i] * Hann[i];
                prevSrc = best;
            }

            Array.Resize(ref output, outLen);
            return output;
        }
    }
}
