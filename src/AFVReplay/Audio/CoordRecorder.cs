using System;
using System.Collections.Generic;
using System.Linq;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Cuts hotline/coldline audio into utterances, one stream per controller.
    /// vatSys streams an open line continuously (no push-to-talk and no gate: mic noise plus a faint
    /// copy of the other controller's frequency audio), so speech is found by level against each
    /// controller's own noise floor rather than by start/stop events.
    /// Called on vatSys's Mumble network thread; <see cref="Tick"/> on a timer.
    /// </summary>
    internal sealed class CoordRecorder
    {
        public const int BlockSamples = 480;                 // 10 ms analysis block
        public static readonly int PreRollSamples = Ms(300);
        public static readonly int HangoverSamples = Ms(1200);
        public static readonly int OnsetBlocks = 3;          // 30 ms of speech before a clip starts
        public static readonly int MinSpeechSamples = Ms(300);
        public static readonly int MaxClipSamples = AudioFormat.SampleRate * 90;
        public static readonly TimeSpan StreamTimeout = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// Real-level RMS below which nothing counts as speech, whatever the noise floor. Low, because
        /// vatSys sends other controllers' raw mic on lines (often quiet); the noise-floor test and the
        /// loopback check in <see cref="CoordBleed"/> keep line noise and frequency loopback out.
        /// </summary>
        public const float AbsoluteSpeechRms = 0.005f;

        /// <summary>Speech must be this many times the noise floor (about 12 dB).</summary>
        public const float FloorRatio = 4f;

        private readonly object sync = new object();
        private readonly Dictionary<string, Stream> streams = new Dictionary<string, Stream>();
        private readonly Action<RawRecording> onComplete;
        private readonly Action<string> onSpeechStart;
        private readonly Func<DateTime> clock;

        public CoordRecorder(Action<RawRecording> onComplete, Action<string> onSpeechStart, Func<DateTime> clock = null)
        {
            this.onComplete = onComplete;
            this.onSpeechStart = onSpeechStart;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        private static int Ms(int ms) => AudioFormat.SampleRate * ms / 1000;

        /// <summary>
        /// True while any line is open: vatSys streams an open line continuously (silence included),
        /// so a stream that is still receiving packets means the call is still connected.
        /// </summary>
        public bool LineOpen
        {
            get
            {
                var now = clock();
                lock (sync) return streams.Values.Any(s => now - s.LastPacketUtc < StreamTimeout);
            }
        }

        public bool IsRecording(string user)
        {
            lock (sync) return streams.TryGetValue(user, out var s) && s.InClip;
        }

        /// <summary>16-bit little-endian mono 48 kHz PCM, as vatSys's Mumble client decodes it.</summary>
        public void Feed(string user, byte[] pcm16, int byteCount, bool own = false) => Feed(user, pcm16, 0, byteCount, own);

        public void Feed(string user, byte[] pcm16, int offset, int byteCount, bool own)
        {
            if (string.IsNullOrEmpty(user) || pcm16 == null) return;
            var now = clock();
            List<RawRecording> done = null;
            List<string> started = null;
            lock (sync)
            {
                if (!streams.TryGetValue(user, out var s))
                {
                    s = new Stream(user, own);
                    streams[user] = s;
                }
                s.LastPacketUtc = now;
                int samples = byteCount / 2;
                for (int i = 0; i < samples; i++)
                {
                    short v = (short)(pcm16[offset + 2 * i] | (pcm16[offset + 2 * i + 1] << 8));
                    var result = s.Push(v, now);
                    if (result.Started) (started ?? (started = new List<string>())).Add(user);
                    if (result.Finished != null) (done ?? (done = new List<RawRecording>())).Add(result.Finished);
                }
            }
            if (started != null) foreach (var u in started) onSpeechStart?.Invoke(u);
            if (done != null) foreach (var r in done) onComplete(r);
        }

        /// <summary>Ends clips for streams that stopped (line closed or connection lost). Call regularly.</summary>
        public void Tick()
        {
            var now = clock();
            List<RawRecording> done = null;
            lock (sync)
            {
                foreach (var key in new List<string>(streams.Keys))
                {
                    var s = streams[key];
                    if (now - s.LastPacketUtc < StreamTimeout) continue;
                    var r = s.Finish(now);
                    if (r != null) (done ?? (done = new List<RawRecording>())).Add(r);
                    s.Wipe();
                    streams.Remove(key);
                }
            }
            if (done != null) foreach (var r in done) onComplete(r);
        }

        /// <summary>Drops everything in progress (privacy wipe).</summary>
        public void Reset()
        {
            lock (sync)
            {
                foreach (var s in streams.Values) s.Wipe();
                streams.Clear();
            }
        }

        private struct PushResult
        {
            public bool Started;
            public RawRecording Finished;
        }

        private sealed class Stream
        {
            private readonly string user;
            private readonly bool own;
            private readonly short[] preRoll = new short[PreRollSamples];
            private int preRollPos;
            private bool preRollFull;

            private readonly short[] block = new short[BlockSamples];
            private int blockLen;
            private double floor = -1;
            private int speechRun;

            private short[] clip;
            private int clipLen;
            private int speechSamples;
            private int sinceSpeech;
            private DateTime clipStartUtc;

            public DateTime LastPacketUtc;
            public bool InClip => clip != null;

            public Stream(string user, bool own) { this.user = user; this.own = own; }

            public PushResult Push(short v, DateTime now)
            {
                var result = default(PushResult);
                block[blockLen++] = v;
                if (blockLen < BlockSamples) return result;
                blockLen = 0;

                double sum = 0;
                for (int i = 0; i < BlockSamples; i++) { double x = block[i] / 32768.0; sum += x * x; }
                double rms = Math.Sqrt(sum / BlockSamples);

                // Noise floor follows quiet quickly and loud only very slowly, so speech doesn't raise it.
                if (floor < 0) floor = rms;
                else if (rms < floor) floor += (rms - floor) * 0.2;
                else floor += (rms - floor) * 0.002;
                floor = Math.Max(floor, 1e-5);

                bool speech = rms > AbsoluteSpeechRms && rms > floor * FloorRatio;
                speechRun = speech ? speechRun + 1 : 0;

                if (clip == null)
                {
                    if (speechRun >= OnsetBlocks)
                    {
                        Begin(now);
                        result.Started = true;
                        // The onset blocks are already in the pre-roll; this block follows them.
                        Append(block, BlockSamples);
                        speechSamples = OnsetBlocks * BlockSamples;
                        sinceSpeech = 0;
                    }
                    else
                    {
                        AddPreRoll(block);
                    }
                    return result;
                }

                Append(block, BlockSamples);
                if (speech) { speechSamples += BlockSamples; sinceSpeech = 0; }
                else sinceSpeech += BlockSamples;

                if (sinceSpeech >= HangoverSamples)
                {
                    result.Finished = Finish(now);
                }
                else if (clipLen >= MaxClipSamples)
                {
                    // Very long talking: split and keep going.
                    result.Finished = Finish(now);
                    Begin(now);
                    speechSamples = 0;
                }
                return result;
            }

            private void Begin(DateTime now)
            {
                clip = new short[AudioFormat.SampleRate * 8];
                clipLen = 0;
                int count = preRollFull ? PreRollSamples : preRollPos;
                int start = preRollFull ? preRollPos : 0;
                for (int i = 0; i < count; i++) clip[clipLen++] = preRoll[(start + i) % PreRollSamples];
                Array.Clear(preRoll, 0, preRoll.Length);
                preRollPos = 0;
                preRollFull = false;
                clipStartUtc = now - TimeSpan.FromSeconds(clipLen / (double)AudioFormat.SampleRate);
            }

            private void AddPreRoll(short[] b)
            {
                for (int i = 0; i < b.Length; i++)
                {
                    preRoll[preRollPos++] = b[i];
                    if (preRollPos == PreRollSamples) { preRollPos = 0; preRollFull = true; }
                }
            }

            private void Append(short[] b, int count)
            {
                if (clipLen + count > clip.Length)
                {
                    var bigger = new short[Math.Max(clip.Length * 2, clipLen + count)];
                    Array.Copy(clip, bigger, clipLen);
                    Array.Clear(clip, 0, clipLen);
                    clip = bigger;
                }
                Array.Copy(b, 0, clip, clipLen, count);
                clipLen += count;
            }

            /// <summary>Closes the current clip. Returns null if there was none or it was too short.</summary>
            public RawRecording Finish(DateTime now)
            {
                if (clip == null) return null;
                var pcm = clip;
                int len = clipLen;
                clip = null;
                clipLen = 0;

                // Trim most of the trailing hangover, keeping a short tail.
                int tail = Math.Min(sinceSpeech, HangoverSamples) - Ms(250);
                if (tail > 0) len -= Math.Min(tail, len);

                if (speechSamples < MinSpeechSamples || len <= 0)
                {
                    Array.Clear(pcm, 0, pcm.Length);
                    return null;
                }

                // Store in the same headroom-scaled units as frequency clips.
                for (int i = 0; i < len; i++) pcm[i] = (short)(pcm[i] / AudioFormat.StorageHeadroom);
                return new RawRecording
                {
                    Pcm = pcm,
                    Length = len,
                    StartUtc = clipStartUtc,
                    EndUtc = clipStartUtc + TimeSpan.FromSeconds(len / (double)AudioFormat.SampleRate),
                    FrequencyHz = 0,
                    Callsigns = new List<string> { user },
                    Blocked = false,
                    IsOwn = own,
                };
            }

            public void Wipe()
            {
                if (clip != null) Array.Clear(clip, 0, clip.Length);
                clip = null;
                clipLen = 0;
                Array.Clear(preRoll, 0, preRoll.Length);
                Array.Clear(block, 0, block.Length);
            }
        }
    }
}
