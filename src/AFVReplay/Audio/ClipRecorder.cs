using System;
using System.Collections.Generic;
using System.Linq;

namespace AFVReplay.Audio
{
    /// <summary>A finished recording handed from the audio thread to the finalizer.</summary>
    internal sealed class RawRecording
    {
        public short[] Pcm;
        public int Length;
        public DateTime StartUtc;
        public DateTime EndUtc;
        public uint FrequencyHz;
        public List<string> Callsigns;
        public bool Blocked;

        /// <summary>Your own voice (PTT on frequency, or your side of a line).</summary>
        public bool IsOwn;

        /// <summary>Overrides the frequency name (coord calls: "Hotline" or "Coldline").</summary>
        public string Name;
    }

    /// <summary>
    /// Cuts one receiver's audio into transmissions using AFV's own "who is transmitting" events.
    /// Everything except <see cref="RequestReset"/> runs on the audio thread, so it does no locking.
    /// </summary>
    internal sealed class ClipRecorder
    {
        public static readonly int PreRollSamples = Ms(150);
        public static readonly int HangoverSamples = Ms(350);
        public static readonly int MinRxSamples = Ms(250);
        public static readonly int MaxClipSamples = AudioFormat.SampleRate * 90;
        private static readonly int InitialCapacity = AudioFormat.SampleRate * 8;

        private enum State { Idle, Receiving, Hangover }

        private readonly Action<RawRecording> onComplete;
        private readonly Action<uint> onRxStart;
        private readonly Func<DateTime> clock;

        private readonly short[] preRoll = new short[PreRollSamples];
        private int preRollPos;
        private bool preRollFull;

        private State state = State.Idle;
        private short[] buffer;
        private int length;
        private int rxSamples;
        private int hangoverLeft;
        private DateTime startUtc;
        private uint frequency;
        private readonly List<string> callsigns = new List<string>();
        private int maxSimultaneous;
        private volatile bool resetRequested;

        public ClipRecorder(Action<RawRecording> onComplete, Action<uint> onRxStart, Func<DateTime> clock = null)
        {
            this.onComplete = onComplete;
            this.onRxStart = onRxStart;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public bool IsRecording => state != State.Idle;

        /// <summary>Called from AFV's receiver, on the audio thread, just before that block of audio is read.</summary>
        public void OnCallsignsChanged(IList<string> current, uint frequencyHz)
        {
            if (current != null && current.Count > 0)
            {
                if (state == State.Idle)
                {
                    Start(frequencyHz);
                }
                state = State.Receiving;
                foreach (var c in current)
                {
                    if (!string.IsNullOrWhiteSpace(c) && !callsigns.Contains(c)) callsigns.Add(c);
                }
                maxSimultaneous = Math.Max(maxSimultaneous, current.Count);
            }
            else if (state == State.Receiving)
            {
                state = State.Hangover;
                hangoverLeft = HangoverSamples;
            }
        }

        /// <summary>Called with every block the receiver produces (on the audio thread).</summary>
        public void Process(float[] samples, int offset, int count)
        {
            if (resetRequested)
            {
                resetRequested = false;
                DiscardAll();
            }
            if (count <= 0) return;

            if (state == State.Idle)
            {
                WritePreRoll(samples, offset, count);
                return;
            }

            Append(samples, offset, count);
            if (state == State.Receiving) rxSamples += count;

            if (state == State.Hangover)
            {
                hangoverLeft -= count;
                if (hangoverLeft <= 0) Finish(continueAfter: false);
            }
            else if (length >= MaxClipSamples)
            {
                // Stuck mic or a very long call: close this clip and keep going in a new one.
                Finish(continueAfter: true);
            }
        }

        /// <summary>Thread-safe: drops anything in progress on the next audio block.</summary>
        public void RequestReset() => resetRequested = true;

        private void Start(uint frequencyHz)
        {
            frequency = frequencyHz;
            startUtc = clock() - TimeSpan.FromSeconds(PreRollLength / (double)AudioFormat.SampleRate);
            buffer = new short[InitialCapacity];
            length = 0;
            rxSamples = 0;
            callsigns.Clear();
            maxSimultaneous = 0;
            CopyPreRollInto();
            onRxStart?.Invoke(frequencyHz);
        }

        private void Finish(bool continueAfter)
        {
            var carryCallsigns = continueAfter ? callsigns.ToList() : null;
            if (rxSamples >= MinRxSamples)
            {
                onComplete(new RawRecording
                {
                    Pcm = buffer,
                    Length = length,
                    StartUtc = startUtc,
                    EndUtc = clock(),
                    FrequencyHz = frequency,
                    Callsigns = callsigns.ToList(),
                    Blocked = maxSimultaneous > 1,
                });
            }
            else
            {
                Array.Clear(buffer, 0, length);
            }
            buffer = null;
            length = 0;
            state = State.Idle;
            ClearPreRoll();

            if (continueAfter)
            {
                Start(frequency);
                state = State.Receiving;
                callsigns.AddRange(carryCallsigns);
                maxSimultaneous = 1;
            }
        }

        private void DiscardAll()
        {
            if (buffer != null) Array.Clear(buffer, 0, length);
            buffer = null;
            length = 0;
            rxSamples = 0;
            callsigns.Clear();
            state = State.Idle;
            ClearPreRoll();
        }

        private void Append(float[] samples, int offset, int count)
        {
            if (length + count > buffer.Length)
            {
                int newSize = Math.Max(buffer.Length * 2, length + count);
                var bigger = new short[newSize];
                Array.Copy(buffer, bigger, length);
                Array.Clear(buffer, 0, length);
                buffer = bigger;
            }
            for (int i = 0; i < count; i++) buffer[length + i] = ToShort(samples[offset + i]);
            length += count;
        }

        private int PreRollLength => preRollFull ? preRoll.Length : preRollPos;

        private void WritePreRoll(float[] samples, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                preRoll[preRollPos++] = ToShort(samples[offset + i]);
                if (preRollPos == preRoll.Length) { preRollPos = 0; preRollFull = true; }
            }
        }

        private void CopyPreRollInto()
        {
            if (preRollFull)
            {
                int tail = preRoll.Length - preRollPos;
                Array.Copy(preRoll, preRollPos, buffer, 0, tail);
                Array.Copy(preRoll, 0, buffer, tail, preRollPos);
                length = preRoll.Length;
            }
            else
            {
                Array.Copy(preRoll, 0, buffer, 0, preRollPos);
                length = preRollPos;
            }
        }

        private void ClearPreRoll()
        {
            Array.Clear(preRoll, 0, preRoll.Length);
            preRollPos = 0;
            preRollFull = false;
        }

        private static short ToShort(float f)
        {
            // AFV's mix runs well above +/-1.0 before the output volume stage, so store with headroom.
            f /= AudioFormat.StorageHeadroom;
            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
            return (short)(f * short.MaxValue);
        }

        private static int Ms(int ms) => AudioFormat.SampleRate * ms / 1000;
    }
}
