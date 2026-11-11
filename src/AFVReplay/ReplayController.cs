using System.Linq;
using AFVReplay.Audio;

namespace AFVReplay
{
    /// <summary>
    /// What a replay key does: plays the most recent call heard (your own transmissions are skipped,
    /// since the key is for working out what someone else said). Pressing again replays that same
    /// call from the start, as many times as needed; once a newer call comes in, the key plays that
    /// one instead. Older calls are on the Say Again page.
    /// </summary>
    internal sealed class ReplayController
    {
        private readonly ClipStore store;
        private readonly ReplayPlayer player;
        private readonly object sync = new object();

        public ReplayController(ClipStore store, ReplayPlayer player)
        {
            this.store = store;
            this.player = player;
        }

        /// <summary>The call the key would play now: the newest that isn't only you, or null.</summary>
        public Clip Latest() => store.Snapshot().FirstOrDefault(c => !c.IsOwn && c.IsPlayable); // newest first; silent line calls have nothing to play

        /// <summary>Plays (or restarts) the most recent call. Returns it, or null if there is none.</summary>
        public Clip Press()
        {
            lock (sync)
            {
                var clip = Latest();
                if (clip != null) player.Play(clip);
                return clip;
            }
        }

        /// <summary>Plays a particular call (from the Say Again page).</summary>
        public void Play(Clip clip)
        {
            lock (sync) player.Play(clip);
        }

        public void Stop() => player.Stop();
    }
}
