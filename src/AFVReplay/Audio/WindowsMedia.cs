using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace AFVReplay.Audio
{
    /// <summary>
    /// <see cref="IMediaControl"/> on the Windows media controls (the media panel in the volume flyout):
    /// Spotify, browser tabs, Media Player and anything else that shows there. Only sessions that are
    /// playing get paused, and on resume only those, and only if they're still paused (so anything you
    /// started or stopped yourself in the meantime is left alone).
    /// Calls run in order on a background task, never on vatSys's threads. Windows 10 1809 or later;
    /// on anything older it logs once and does nothing.
    /// </summary>
    internal sealed class WindowsMedia : IMediaControl
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

        private readonly object sync = new object();
        private readonly List<GlobalSystemMediaTransportControlsSession> paused = new List<GlobalSystemMediaTransportControlsSession>();
        private Task queue = Task.CompletedTask;
        private GlobalSystemMediaTransportControlsSessionManager manager;
        private bool unavailable;

        public void PausePlaying() => Enqueue(PauseNow);

        public void ResumePaused() => Enqueue(ResumeNow);

        /// <summary>Waits for queued calls (used on shutdown so paused media is resumed first).</summary>
        public void Flush(TimeSpan timeout)
        {
            Task t;
            lock (sync) t = queue;
            try { t.Wait(timeout); } catch { }
        }

        private void Enqueue(Func<Task> work)
        {
            lock (sync)
            {
                queue = queue.ContinueWith(async _ =>
                {
                    try { await work().ConfigureAwait(false); }
                    catch (Exception ex) { Diag.Warn("media control failed: " + ex.GetType().Name); }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        private async Task<GlobalSystemMediaTransportControlsSessionManager> Manager()
        {
            if (manager != null || unavailable) return manager;
            try
            {
                manager = await WithTimeout(GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Older Windows, or the media controls service isn't there.
                unavailable = true;
                Diag.Warn("Pause Media unavailable: Windows media controls not found (" + ex.GetType().Name + ")");
            }
            return manager;
        }

        private async Task PauseNow()
        {
            var m = await Manager().ConfigureAwait(false);
            if (m == null) return;
            foreach (var session in m.GetSessions())
            {
                try
                {
                    if (session.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                    if (await WithTimeout(session.TryPauseAsync().AsTask()).ConfigureAwait(false))
                        paused.Add(session);
                }
                catch (Exception ex)
                {
                    Diag.Warn("media pause failed: " + ex.GetType().Name); // that app closed, say; others still pause
                }
            }
        }

        private async Task ResumeNow()
        {
            var toResume = paused.ToArray();
            paused.Clear();
            foreach (var session in toResume)
            {
                try
                {
                    if (session.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) continue;
                    await WithTimeout(session.TryPlayAsync().AsTask()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Diag.Warn("media resume failed: " + ex.GetType().Name);
                }
            }
        }

        private static async Task<T> WithTimeout<T>(Task<T> task)
        {
            if (await Task.WhenAny(task, Task.Delay(CallTimeout)).ConfigureAwait(false) != task) throw new TimeoutException();
            return await task.ConfigureAwait(false);
        }
    }
}
