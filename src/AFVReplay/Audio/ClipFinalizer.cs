using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Turns raw recordings into compressed clips on one background thread, off the audio thread.
    /// Also removes duplicates: a frequency with several transceivers gets the same transmission
    /// on more than one receiver (only one carries the audio), so overlapping recordings of the
    /// same frequency are collapsed into the loudest one.
    /// </summary>
    internal sealed class ClipFinalizer : IDisposable
    {
        /// <summary>In stored (headroom-scaled) units; roughly 0.004 of real level.</summary>
        private const double SilenceRms = 0.0005;

        private readonly ClipStore store;
        private readonly Func<uint, string> frequencyName;
        private readonly BlockingCollection<RawRecording> queue = new BlockingCollection<RawRecording>();
        private readonly Thread worker;
        private readonly Dictionary<long, double> rmsById = new Dictionary<long, double>();

        public ClipFinalizer(ClipStore store, Func<uint, string> frequencyName)
        {
            this.store = store;
            this.frequencyName = frequencyName;
            worker = new Thread(Run) { IsBackground = true, Name = "AFVReplay finalizer", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        /// <summary>Raised (on the worker thread) after a clip is stored.</summary>
        public event EventHandler<Clip> ClipAdded;

        private int inFlight;

        public void Enqueue(RawRecording rec)
        {
            if (queue.IsAddingCompleted) return;
            Interlocked.Increment(ref inFlight);
            if (!queue.TryAdd(rec)) Interlocked.Decrement(ref inFlight);
        }

        /// <summary>Waits until everything queued so far has been processed. For tests.</summary>
        public void Drain(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref inFlight) > 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        }

        public void DiscardPending()
        {
            while (queue.TryTake(out var rec))
            {
                Array.Clear(rec.Pcm, 0, rec.Length);
                Interlocked.Decrement(ref inFlight);
            }
        }

        private void Run()
        {
            foreach (var rec in queue.GetConsumingEnumerable())
            {
                try { Process(rec); }
                catch (Exception ex) { Diag.Warn("finalize failed: " + ex.GetType().Name); }
                finally
                {
                    Array.Clear(rec.Pcm, 0, rec.Length);
                    Interlocked.Decrement(ref inFlight);
                }
            }
        }

        internal void Process(RawRecording rec)
        {
            if (rec.IsSilent)
            {
                // A line call nobody spoke on: a log entry, no audio.
                var silent = new Clip(store.NextId(), rec.StartUtc, rec.EndUtc, rec.FrequencyHz, rec.Name ?? frequencyName(rec.FrequencyHz),
                                      rec.Callsigns.AsReadOnly(), false, new List<byte[]>(), 0, rec.IsOwn, isSilent: true);
                store.Add(silent);
                ClipAdded?.Invoke(this, silent);
                return;
            }
            double rms = Rms(rec.Pcm, rec.Length);
            if (rms < SilenceRms) return;

            // Collapse duplicates of the same transmission picked up by another receiver.
            // (Coord calls come from one stream per controller, so they never have duplicates.)
            if (rec.FrequencyHz != 0 && !rec.IsOwn)
            foreach (var existing in store.Snapshot())
            {
                if (existing.FrequencyHz != rec.FrequencyHz || existing.IsOwn) continue;
                bool overlaps = existing.StartUtc < rec.EndUtc && rec.StartUtc < existing.EndUtc;
                if (!overlaps || !existing.Callsigns.Intersect(rec.Callsigns).Any()) continue;
                rmsById.TryGetValue(existing.Id, out var existingRms);
                if (existingRms >= rms) return;
                store.Remove(existing);
                rmsById.Remove(existing.Id);
                break;
            }

            var frames = OpusCodec.Encode(rec.Pcm, rec.Length);
            string name;
            try { name = rec.Name ?? frequencyName(rec.FrequencyHz); }
            catch { name = FormatFrequency(rec.FrequencyHz); }

            var clip = new Clip(store.NextId(), rec.StartUtc, rec.EndUtc, rec.FrequencyHz, name,
                                rec.Callsigns.AsReadOnly(), rec.Blocked, frames, rec.Length, rec.IsOwn);
            clip.SpeechLevel = AudioLevel.Speech(rec.Pcm, rec.Length);
            rmsById[clip.Id] = rms;
            store.Add(clip);
            foreach (var id in rmsById.Keys.ToList())
            {
                if (!store.Contains(id)) rmsById.Remove(id);
            }
            ClipAdded?.Invoke(this, clip);
        }

        public static string FormatFrequency(uint hz) => (hz / 1_000_000.0).ToString("0.000");

        private static double Rms(short[] pcm, int len)
        {
            if (len == 0) return 0;
            double sum = 0;
            for (int i = 0; i < len; i++) { double s = pcm[i] / 32768.0; sum += s * s; }
            return Math.Sqrt(sum / len);
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            worker.Join(1000);
            DiscardPending();
        }
    }
}
