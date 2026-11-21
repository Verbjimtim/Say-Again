using System;
using System.Collections.Generic;
using System.Linq;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Holds recent clips in memory only. Clips older than the retention window are wiped.
    /// Nothing here is ever written to disk, logged, or exported.
    /// </summary>
    internal sealed class ClipStore
    {
        /// <summary>Hard upper bound on frequency-call retention, whatever the settings say.</summary>
        public static readonly TimeSpan MaxRetention = TimeSpan.FromMinutes(5);

        /// <summary>Hard upper bound on coordination (hotline/coldline) retention.</summary>
        public static readonly TimeSpan CoordMaxRetention = TimeSpan.FromMinutes(10);

        /// <summary>Safety cap so a stuck stream can't grow memory without limit.</summary>
        public const int MaxClips = 200;

        private readonly object sync = new object();
        private readonly List<Clip> clips = new List<Clip>();
        private readonly TimeSpan cap;
        private TimeSpan retention;
        private long nextId;

        public ClipStore() : this(MaxRetention) { }

        public ClipStore(TimeSpan cap)
        {
            this.cap = cap;
            retention = cap;
        }

        /// <summary>This store's hard retention limit.</summary>
        public TimeSpan Cap => cap;

        public event EventHandler Changed;

        public TimeSpan Retention
        {
            get { lock (sync) return retention; }
            set
            {
                if (value <= TimeSpan.Zero) value = TimeSpan.FromMinutes(1);
                if (value > cap) value = cap;
                lock (sync) retention = value;
                Prune(DateTime.UtcNow);
            }
        }

        private bool holdNew;

        /// <summary>
        /// AFK: while on, calls added are held (never expire). Turned off, the held calls' keep time starts
        /// from then, so they go the usual retention after AFK ends. Calls from before AFK expire as normal.
        /// </summary>
        public bool HoldNew
        {
            get { lock (sync) return holdNew; }
            set => SetHoldNew(value, DateTime.UtcNow);
        }

        internal void SetHoldNew(bool hold, DateTime nowUtc)
        {
            lock (sync)
            {
                if (holdNew == hold) return;
                holdNew = hold;
                if (!hold)
                {
                    foreach (var c in clips.Where(c => c.Held))
                    {
                        c.Held = false;
                        c.KeepFromUtc = nowUtc;
                    }
                }
            }
            Prune(nowUtc);
        }

        public long NextId() => System.Threading.Interlocked.Increment(ref nextId);

        public void Add(Clip clip)
        {
            lock (sync)
            {
                if (holdNew) clip.Held = true;
                clips.Add(clip);
                clips.Sort((a, b) => a.EndUtc.CompareTo(b.EndUtc));
                while (clips.Count > MaxClips)
                {
                    clips[0].Wipe();
                    clips.RemoveAt(0);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(Clip clip)
        {
            bool removed;
            lock (sync) removed = clips.Remove(clip);
            clip.Wipe();
            if (removed) Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool Contains(long id)
        {
            lock (sync) return clips.Exists(c => c.Id == id);
        }

        /// <summary>Newest first.</summary>
        public IReadOnlyList<Clip> Snapshot()
        {
            lock (sync) return clips.AsEnumerable().Reverse().ToList();
        }

        public int Count { get { lock (sync) return clips.Count; } }

        public void Prune(DateTime nowUtc)
        {
            bool changed = false;
            lock (sync)
            {
                var cutoff = nowUtc - retention;
                for (int i = clips.Count - 1; i >= 0; i--)
                {
                    var c = clips[i];
                    if (!c.Held && (c.KeepFromUtc > c.EndUtc ? c.KeepFromUtc : c.EndUtc) < cutoff)
                    {
                        clips[i].Wipe();
                        clips.RemoveAt(i);
                        changed = true;
                    }
                }
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            lock (sync)
            {
                foreach (var c in clips) c.Wipe();
                clips.Clear();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
