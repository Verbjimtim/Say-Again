using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AFVReplay.Audio;

namespace AFVReplay
{
    /// <summary>
    /// AFK (the button under ▼ on the Say Again page):
    /// <list type="bullet">
    /// <item>Calls and coord made while AFK is on are kept until the usual keep time after AFK is turned off.
    /// Calls from before expire as normal; disconnecting still wipes everything.</item>
    /// <item>Turns on vatSys's Request Break (Tools menu), as soon as you're connected as ATC if you aren't yet.
    /// Turning AFK off turns Request Break off.</item>
    /// <item>Turning Request Break off yourself (in Tools) turns AFK off too.</item>
    /// </list>
    /// Always off when vatSys starts.
    /// </summary>
    internal sealed class AfkMode
    {
        private readonly Action<bool> holdCalls;
        private readonly Func<bool> canRequestBreak;
        private readonly Func<bool> breakOn;
        private readonly Action toggleBreak;
        private bool attempted, breakSeen;
        private readonly object sync = new object();
        private readonly List<AfkPeriod> periods = new List<AfkPeriod>();
        private readonly Func<DateTime> clock;

        /// <param name="holdCalls">Starts/stops holding new calls.</param>
        /// <param name="canRequestBreak">Connected as ATC (vatSys only sends a break request then).</param>
        /// <param name="breakOn">vatSys's Request Break is on.</param>
        /// <param name="toggleBreak">Clicks vatSys's Request Break.</param>
        public AfkMode(Action<bool> holdCalls, Func<bool> canRequestBreak, Func<bool> breakOn, Action toggleBreak, Func<DateTime> clock = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.holdCalls = holdCalls;
            this.canRequestBreak = canRequestBreak;
            this.breakOn = breakOn;
            this.toggleBreak = toggleBreak;
        }

        public bool On { get; private set; }

        public event EventHandler Changed;

        public void Toggle() => Set(!On);

        public void Set(bool on)
        {
            if (On == on) return;
            On = on;
            var now = clock();
            lock (sync)
            {
                if (on) periods.Add(new AfkPeriod(now, null));
                else if (periods.Count > 0 && periods[periods.Count - 1].EndUtc == null)
                    periods[periods.Count - 1] = new AfkPeriod(periods[periods.Count - 1].StartUtc, now);
                // Older ones can't have calls left next to them (coord is kept 10 minutes at most).
                periods.RemoveAll(p => p.EndUtc.HasValue && now - p.EndUtc.Value > ClipStore.CoordMaxRetention + TimeSpan.FromMinutes(1));
            }
            holdCalls(on);
            attempted = breakSeen = false;
            if (on) Tick();
            else if (canRequestBreak() && breakOn()) toggleBreak();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>When AFK was turned on and off (recent ones only), for the markers in the Say Again list.</summary>
        public IReadOnlyList<AfkPeriod> Periods { get { lock (sync) return periods.ToList(); } }

        /// <summary>For previews: a past time away, without holding calls or touching Request Break.</summary>
        internal void RecordPeriod(AfkPeriod period) { lock (sync) periods.Add(period); }

        /// <summary>Keeps Request Break in step with AFK. Call regularly on vatSys's UI thread.</summary>
        public void Tick()
        {
            if (!On) return;
            if (!canRequestBreak())
            {
                // Not connected (or not as ATC): request the break once you are.
                attempted = breakSeen = false;
                return;
            }
            if (breakOn()) { breakSeen = true; return; }
            if (breakSeen)
            {
                // It was on and you turned it off in Tools: that ends AFK.
                Set(false);
                return;
            }
            if (!attempted)
            {
                attempted = true; // once per connection, so it never keeps clicking if vatSys won't
                toggleBreak();
                if (breakOn()) breakSeen = true;
            }
        }
    }

    /// <summary>One time away: when AFK was turned on, and off (null while it's still on).</summary>
    internal struct AfkPeriod
    {
        public readonly DateTime StartUtc;
        public readonly DateTime? EndUtc;
        public AfkPeriod(DateTime start, DateTime? end) { StartUtc = start; EndUtc = end; }
    }

    /// <summary>A thin line in the Say Again list: "Away from Keyboard 0402Z" or "Back at Keyboard 0431Z".</summary>
    internal sealed class AfkMarker : IEquatable<AfkMarker>
    {
        public AfkMarker(bool away, DateTime atUtc) { Away = away; AtUtc = atUtc; }

        public bool Away { get; }
        public DateTime AtUtc { get; }

        public string Text => (Away ? "Away from Keyboard " : "Back at Keyboard ") + AtUtc.ToString("HHmm", CultureInfo.InvariantCulture) + "Z";

        public bool Equals(AfkMarker other) => other != null && other.Away == Away && other.AtUtc == AtUtc;
        public override bool Equals(object obj) => Equals(obj as AfkMarker);
        public override int GetHashCode() => AtUtc.GetHashCode() ^ (Away ? 1 : 0);

        /// <summary>
        /// The list's rows: the calls (newest first) with a marker where each time away started and ended.
        /// A marker shows only with a call on each side of it, so it goes once the calls next to it expire.
        /// </summary>
        public static List<object> Place(IReadOnlyList<Clip> newestFirst, IEnumerable<AfkPeriod> periods)
        {
            var markers = new List<AfkMarker>();
            foreach (var p in periods)
            {
                markers.Add(new AfkMarker(true, p.StartUtc));
                if (p.EndUtc.HasValue) markers.Add(new AfkMarker(false, p.EndUtc.Value));
            }
            // Newest first, like the calls; Back above Away for the same moment.
            markers = markers.OrderByDescending(m => m.AtUtc).ThenBy(m => m.Away).ToList();

            var rows = new List<object>(newestFirst.Count + markers.Count);
            int mi = 0;
            for (int i = 0; i <= newestFirst.Count; i++)
            {
                // Markers newer than this call go above it.
                while (mi < markers.Count && (i == newestFirst.Count || markers[mi].AtUtc > newestFirst[i].EndUtc))
                {
                    if (i > 0 && i < newestFirst.Count) rows.Add(markers[mi]); // a call above and below
                    mi++;
                }
                if (i < newestFirst.Count) rows.Add(newestFirst[i]);
            }
            return rows;
        }
    }
}
