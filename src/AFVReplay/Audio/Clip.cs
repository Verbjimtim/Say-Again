using System;
using System.Collections.Generic;

namespace AFVReplay.Audio
{
    /// <summary>
    /// One received transmission, held only in memory as Opus frames.
    /// </summary>
    internal sealed class Clip
    {
        private List<byte[]> frames;

        public Clip(long id, DateTime startUtc, DateTime endUtc, uint frequencyHz, string frequencyName,
                    IReadOnlyList<string> callsigns, bool blocked, List<byte[]> frames, int sampleCount, bool isOwn = false,
                    bool isSilent = false)
        {
            IsSilent = isSilent;
            IsOwn = isOwn;
            Id = id;
            StartUtc = startUtc;
            EndUtc = endUtc;
            FrequencyHz = frequencyHz;
            FrequencyName = frequencyName;
            Callsigns = callsigns;
            Blocked = blocked;
            this.frames = frames;
            SampleCount = sampleCount;
        }

        public long Id { get; }
        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }
        public uint FrequencyHz { get; }
        public string FrequencyName { get; }
        public IReadOnlyList<string> Callsigns { get; }

        /// <summary>More than one station was transmitting at once on this frequency.</summary>
        public bool Blocked { get; }

        public int SampleCount { get; }
        public TimeSpan Duration => TimeSpan.FromSeconds(SampleCount / (double)AudioFormat.SampleRate);
        public bool IsWiped => frames == null;

        /// <summary>Your own voice: a PTT transmission, or your side of a line.</summary>
        public bool IsOwn { get; }

        /// <summary>Speech level (real RMS, see <see cref="AudioLevel.Speech"/>); 0 when unknown.</summary>
        public double SpeechLevel { get; internal set; }

        /// <summary>A line call where nobody spoke: listed (who, when) but has no audio to play.</summary>
        public bool IsSilent { get; }

        /// <summary>Something to play: not silent and not yet wiped.</summary>
        public bool IsPlayable => !IsSilent && !IsWiped;

        /// <summary>A hotline/coldline call rather than a frequency transmission.</summary>
        public bool IsCoord => FrequencyHz == 0;

        // Line calls list who the line was with (see CoordConversation); "You" is for your frequency transmissions.
        public string CallsignText => IsOwn && !IsCoord ? "You" : Callsigns.Count == 0 ? "?" : string.Join(" + ", Callsigns);

        /// <summary>Decodes to float PCM for playback. Returns null once wiped.</summary>
        public float[] Decode()
        {
            var f = frames;
            if (f == null) return null;
            var pcm = OpusCodec.Decode(f, SampleCount);
            for (int i = 0; i < pcm.Length; i++) pcm[i] *= AudioFormat.StorageHeadroom;
            return pcm;
        }

        /// <summary>Overwrites the audio with zeros and drops it.</summary>
        public void Wipe()
        {
            var f = System.Threading.Interlocked.Exchange(ref frames, null);
            if (f == null) return;
            foreach (var frame in f) Array.Clear(frame, 0, frame.Length);
            f.Clear();
        }
    }

    internal static class AudioFormat
    {
        public const int SampleRate = 48000;
        public const int OpusFrameSize = 960; // 20 ms

        /// <summary>Stored audio is scaled down by this much so loud AFV audio doesn't clip in 16-bit.</summary>
        public const float StorageHeadroom = 8f;
    }
}
