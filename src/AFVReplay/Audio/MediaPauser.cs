using System;
using System.Threading;

namespace AFVReplay.Audio
{
    /// <summary>Pauses and resumes other apps' media (the Windows media controls).</summary>
    internal interface IMediaControl
    {
        /// <summary>Pauses whatever is playing and remembers it.</summary>
        void PausePlaying();

        /// <summary>Plays again what <see cref="PausePlaying"/> paused, if it's still paused.</summary>
        void ResumePaused();

        /// <summary>Forgets what was paused, leaving it paused.</summary>
        void Forget();
    }

    /// <summary>
    /// "Pause Media": while the radio is busy (receiving, transmitting or a hotline/coldline open),
    /// other apps' media is paused. It resumes once the radio has been quiet for the resume delay
    /// (1-5 s, so it doesn't flicker on and off between a call and its readback), or with no delay
    /// ("never") it stays paused for you to resume yourself.
    /// </summary>
    internal sealed class MediaPauser : IDisposable
    {
        private readonly IMediaControl media;
        private readonly Func<bool> enabled;
        private readonly Func<bool> busy;
        private readonly Func<TimeSpan?> resumeDelay;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();
        private Timer timer;
        private DateTime lastBusyUtc;

        public MediaPauser(IMediaControl media, Func<bool> enabled, Func<bool> busy, Func<TimeSpan?> resumeDelay, Func<DateTime> clock = null)
        {
            this.resumeDelay = resumeDelay;
            this.media = media;
            this.enabled = enabled;
            this.busy = busy;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>True while this has media paused.</summary>
        public bool Paused { get; private set; }

        public void Start(TimeSpan interval) => timer = new Timer(_ => Tick(), null, TimeSpan.Zero, interval);

        public void Tick()
        {
            if (!Monitor.TryEnter(sync)) return;
            try
            {
                var now = clock();
                bool on = enabled();
                bool isBusy = false;
                if (on)
                {
                    try { isBusy = busy(); }
                    catch (Exception ex) { Diag.Warn("media pause check failed: " + ex.GetType().Name); }
                }

                if (isBusy)
                {
                    lastBusyUtc = now;
                    if (!Paused)
                    {
                        Paused = true;
                        media.PausePlaying();
                    }
                }
                else if (Paused)
                {
                    var delay = resumeDelay();
                    if (!on || (delay.HasValue && now - lastBusyUtc >= delay.Value))
                    {
                        Paused = false;
                        media.ResumePaused();
                    }
                    else if (!delay.HasValue)
                    {
                        // Never resume: leave it paused, ready to pause whatever you play next.
                        Paused = false;
                        media.Forget();
                    }
                }
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }

        /// <summary>Stops, and puts back anything still paused.</summary>
        public void Dispose()
        {
            timer?.Dispose();
            timer = null;
            lock (sync)
            {
                if (Paused)
                {
                    Paused = false;
                    media.ResumePaused();
                }
            }
        }
    }
}
