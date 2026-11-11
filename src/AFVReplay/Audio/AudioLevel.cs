using System;

namespace AFVReplay.Audio
{
    internal static class AudioLevel
    {
        /// <summary>
        /// Speech level of stored (headroom-scaled) audio, in real RMS: the louder half of its 10 ms
        /// blocks, so pauses, pre-roll and hangover don't drag it down.
        /// </summary>
        public static double Speech(short[] pcm, int length)
        {
            const int block = 480;
            int blocks = Math.Min(length, pcm?.Length ?? 0) / block;
            if (blocks == 0) return 0;
            var levels = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                double sum = 0;
                for (int i = b * block; i < (b + 1) * block; i++) { double s = pcm[i] / 32768.0; sum += s * s; }
                levels[b] = sum / block;
            }
            Array.Sort(levels);
            double top = 0;
            int count = 0;
            for (int b = blocks / 2; b < blocks; b++) { top += levels[b]; count++; }
            return Math.Sqrt(top / Math.Max(1, count)) * AudioFormat.StorageHeadroom;
        }
    }
}
