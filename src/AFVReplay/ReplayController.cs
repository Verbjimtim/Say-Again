using System;
using System.Linq;
using AFVReplay.Audio;

namespace AFVReplay
{
    /// <summary>
    /// What the Replay key does: the first press plays the newest call; pressing again while it
    /// plays (or shortly after) steps back one call each time.
    /// </summary>
    internal sealed class ReplayController
    {
        private readonly ClipStore store;
        private readonly ReplayPlayer player;
        private readonly Func<ReplaySettings> settings;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();
        private int cursor = -1;
        private long lastClipId;
        private DateTime lastPressUtc = DateTime.MinValue;

        public ReplayController(ClipStore store, ReplayPlayer player, Func<ReplaySettings> settings, Func<DateTime> clock = null)
        {
            this.store = store;
            this.player = player;
            this.settings = settings;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Returns the clip now playing, or null if there was nothing to play.</summary>
        public Clip Press()
        {
            lock (sync)
            {
                var now = clock();
                var clips = store.Snapshot(); // newest first
                if (clips.Count == 0)
                {
                    cursor = -1;
                    return null;
                }

                bool stepping = cursor >= 0
                    && (player.IsPlaying || (now - lastPressUtc).TotalSeconds <= settings().StepBackWindowSeconds);

                int next = 0;
                if (stepping)
                {
                    // Find where we were by id, so new calls arriving meanwhile don't shift the position.
                    int at = clips.ToList().FindIndex(c => c.Id == lastClipId);
                    next = at < 0 ? 0 : at + 1;
                    if (next >= clips.Count) next = clips.Count - 1; // stay on the oldest
                }

                var clip = clips[next];
                player.Play(clip);
                cursor = next;
                lastClipId = clip.Id;
                lastPressUtc = now;
                return clip;
            }
        }

        public void Play(Clip clip)
        {
            lock (sync)
            {
                player.Play(clip);
                cursor = 0;
                lastClipId = clip.Id;
                lastPressUtc = clock();
            }
        }

        public void Stop() => player.Stop();
    }
}
