using System;

namespace AFVReplay
{
    /// <summary>
    /// AFK (the button right of Handoff Alert on the VSCS Setup page):
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

        /// <param name="holdCalls">Starts/stops holding new calls.</param>
        /// <param name="canRequestBreak">Connected as ATC (vatSys only sends a break request then).</param>
        /// <param name="breakOn">vatSys's Request Break is on.</param>
        /// <param name="toggleBreak">Clicks vatSys's Request Break.</param>
        public AfkMode(Action<bool> holdCalls, Func<bool> canRequestBreak, Func<bool> breakOn, Action toggleBreak)
        {
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
            holdCalls(on);
            attempted = breakSeen = false;
            if (on) Tick();
            else if (canRequestBreak() && breakOn()) toggleBreak();
            Changed?.Invoke(this, EventArgs.Empty);
        }

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
}
