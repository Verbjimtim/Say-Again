using System.Collections.Generic;
using System.Linq;
using AFVReplay.Audio;

namespace AFVReplay
{
    /// <summary>
    /// What the Say Again page's buttons do: frequency and coord calls in one list, newest first
    /// (the table order). Older steps down the list, Newer steps up, Play/Pause pauses or carries on.
    /// A call that finishes just stops. The replay keys are separate (see <see cref="ReplayController"/>).
    /// </summary>
    internal sealed class SayAgainPlaylist
    {
        private readonly ReplayPlugin plugin;
        private Clip current;

        public SayAgainPlaylist(ReplayPlugin plugin) { this.plugin = plugin; }

        /// <summary>Frequency and coord calls together, newest first.</summary>
        public List<Clip> Items() =>
            plugin.Store.Snapshot().Concat(plugin.CoordStore.Snapshot()).OrderByDescending(c => c.EndUtc).ToList();

        /// <summary>The call playing or paused, else the last one played from the page (if still kept).</summary>
        public Clip Current
        {
            get
            {
                var active = ActivePlayer()?.PlayingClip;
                if (active != null) return active;
                if (current != null && !current.IsWiped && Items().Contains(current)) return current;
                return null;
            }
        }

        public bool IsPlaying => ActivePlayer() is ReplayPlayer p && !p.IsPaused;
        public bool IsPaused => ActivePlayer() is ReplayPlayer p && p.IsPaused;

        private ReplayPlayer ActivePlayer() =>
            plugin.Player.IsPlaying ? plugin.Player : plugin.CoordPlayer.IsPlaying ? plugin.CoordPlayer : null;

        /// <summary>Pauses, carries on, or (when nothing is loaded) plays the selected call, else the newest.</summary>
        public void PlayPause(Clip selected)
        {
            var active = ActivePlayer();
            if (active != null)
            {
                // A different row picked while playing: play that instead.
                if (selected != null && selected != active.PlayingClip) { Play(selected); return; }
                if (active.IsPaused) active.Resume(); else active.Pause();
                return;
            }
            var items = Items();
            Play(selected ?? Current ?? items.FirstOrDefault(c => c.IsPlayable));
        }

        /// <summary>One call older (down the table). From nothing, the newest call.</summary>
        public void Older(Clip selected) => Step(selected, +1);

        /// <summary>One call newer (up the table). Stays on the newest.</summary>
        public void Newer(Clip selected) => Step(selected, -1);

        private void Step(Clip selected, int direction)
        {
            var items = Items();
            if (items.Count == 0) return;
            var from = Current ?? selected;
            int at = from == null ? -1 : items.IndexOf(from);
            // Silent line calls are listed but have nothing to play: step over them.
            if (at < 0) { at = -1; direction = +1; }
            int next = at + direction;
            while (next >= 0 && next < items.Count && !items[next].IsPlayable) next += direction;
            if (next < 0 || next >= items.Count) { if (at >= 0) Play(items[at]); return; } // at the end: replay the current one
            Play(items[next]);
        }

        public void Play(Clip clip)
        {
            if (clip == null || !clip.IsPlayable) return;
            current = clip;
            (clip.IsCoord ? plugin.CoordController : plugin.Controller).Play(clip);
        }

        public void Stop() => plugin.StopAll();
    }
}
