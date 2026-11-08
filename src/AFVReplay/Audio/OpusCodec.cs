using System;
using System.Collections.Generic;
using Concentus.Enums;
using Concentus.Structs;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Compresses clips in memory with the Opus codec vatSys already ships (Concentus).
    /// Clips are recorded at 8 kHz (radio/telephone bandwidth, which is all AFV voice carries in
    /// practice): audio is filtered and taken down from 48 kHz before encoding, and brought back up
    /// to 48 kHz for playback. About 1.5 KB per second, so 10 minutes is under 1 MB.
    /// </summary>
    internal static class OpusCodec
    {
        public const int StoredRate = 8000;
        private const int Factor = AudioFormat.SampleRate / StoredRate;     // 6
        private const int StoredFrame = StoredRate / 50;                    // 20 ms = 160 samples
        private const int Bitrate = 12000;
        private const int MaxPacket = 1275;

        // Low-pass at 3.6 kHz (48 kHz design) for the rate change both ways: Blackman-windowed sinc.
        private const int Taps = 121;
        private static readonly float[] Lowpass = MakeLowpass(3600.0 / AudioFormat.SampleRate, Taps);

        private static float[] MakeLowpass(double cutoff, int taps)
        {
            var h = new float[taps];
            int mid = taps / 2;
            double sum = 0;
            for (int i = 0; i < taps; i++)
            {
                int n = i - mid;
                double sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
                h[i] = (float)(sinc * w);
                sum += h[i];
            }
            for (int i = 0; i < taps; i++) h[i] = (float)(h[i] / sum);
            return h;
        }

        /// <summary>48 kHz -> 8 kHz, filtered, no delay.</summary>
        internal static short[] Down(short[] pcm, int count)
        {
            int outCount = (count + Factor - 1) / Factor;
            var output = new short[outCount];
            int mid = Taps / 2;
            for (int k = 0; k < outCount; k++)
            {
                int center = k * Factor;
                double acc = 0;
                for (int j = 0; j < Taps; j++)
                {
                    int idx = center + mid - j;
                    if (idx >= 0 && idx < count) acc += Lowpass[j] * pcm[idx];
                }
                output[k] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(acc)));
            }
            return output;
        }

        /// <summary>8 kHz -> 48 kHz, filtered, no delay.</summary>
        internal static float[] Up(float[] low, int lowCount, int outCount)
        {
            var output = new float[outCount];
            int mid = Taps / 2;
            for (int n = 0; n < outCount; n++)
            {
                double acc = 0;
                // Only every 6th input position is non-zero after zero-stuffing.
                int first = n + mid - (Taps - 1);
                int k0 = first <= 0 ? 0 : (first + Factor - 1) / Factor;
                for (int k = k0; k < lowCount; k++)
                {
                    int j = n + mid - k * Factor;
                    if (j < 0) break;
                    if (j < Taps) acc += Lowpass[j] * low[k];
                }
                output[n] = (float)(acc * Factor);
            }
            return output;
        }

        /// <param name="pcm">48 kHz 16-bit mono.</param>
        public static List<byte[]> Encode(short[] pcm, int count)
        {
            var low = Down(pcm, count);
            var encoder = OpusEncoder.Create(StoredRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = Bitrate;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;

            var frames = new List<byte[]>(low.Length / StoredFrame + 1);
            var frame = new short[StoredFrame];
            var packet = new byte[MaxPacket];
            for (int pos = 0; pos < low.Length; pos += StoredFrame)
            {
                int n = Math.Min(StoredFrame, low.Length - pos);
                Array.Copy(low, pos, frame, 0, n);
                if (n < frame.Length) Array.Clear(frame, n, frame.Length - n);
                int len = encoder.Encode(frame, 0, StoredFrame, packet, 0, MaxPacket);
                var copy = new byte[len];
                Buffer.BlockCopy(packet, 0, copy, 0, len);
                frames.Add(copy);
            }
            Array.Clear(low, 0, low.Length);
            Array.Clear(frame, 0, frame.Length);
            Array.Clear(packet, 0, packet.Length);
            return frames;
        }

        /// <returns>48 kHz float audio, <paramref name="sampleCount"/> samples long.</returns>
        public static float[] Decode(List<byte[]> frames, int sampleCount)
        {
            var decoder = OpusDecoder.Create(StoredRate, 1);
            var low = new float[frames.Count * StoredFrame];
            int pos = 0;
            foreach (var packet in frames)
            {
                pos += decoder.Decode(packet, 0, packet.Length, low, pos, StoredFrame);
            }
            int outCount = Math.Min(sampleCount, pos * Factor);
            var output = Up(low, pos, outCount);
            Array.Clear(low, 0, low.Length);
            return output;
        }
    }
}
