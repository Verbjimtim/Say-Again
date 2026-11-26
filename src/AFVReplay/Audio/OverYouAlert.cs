using System;

namespace AFVReplay.Audio
{
    /// <summary>
    /// "Blocked": someone called on your frequency while you were transmitting, so neither of you heard
    /// the other. Shown on the VSCS from the moment it's caught until <see cref="ShowFor"/> after you
    /// release PTT. Tracks when a release follows a block, for a tone at release (Tone Timing: After).
    /// Thread-safe: reports come from the audio thread, PTT from vatSys.
    /// </summary>
    internal sealed class OverYouAlert
    {
        public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(6);

        private readonly object sync = new object();
        private string callsign, where;
        private DateTime? showUntilUtc;
        private bool pendingTone;

        /// <summary>Calls caught over you since vatSys started (for the status line).</summary>
        public int Count { get; private set; }

        /// <summary>
        /// A station called over your transmission. Returns true if you had already released PTT when the report
        /// arrived (so a tone due at release must play now; otherwise <see cref="TransmitEnded"/> says when).
        /// </summary>
        public bool Report(string callsign, string where, bool stillTransmitting, DateTime nowUtc)
        {
            lock (sync)
            {
                this.callsign = callsign;
                this.where = where;
                Count++;
                if (stillTransmitting)
                {
                    showUntilUtc = DateTime.MaxValue;
                    pendingTone = true;
                    return false;
                }
                showUntilUtc = nowUtc + ShowFor;
                pendingTone = false;
                return true;
            }
        }

        /// <summary>You released PTT. Returns true if a call came in over that transmission.</summary>
        public bool TransmitEnded(DateTime nowUtc)
        {
            lock (sync)
            {
                if (!pendingTone) return false;
                pendingTone = false;
                showUntilUtc = nowUtc + ShowFor;
                return true;
            }
        }

        /// <summary>Who called over you and where, while it should be shown; otherwise null.</summary>
        public (string Callsign, string Where)? Current(DateTime nowUtc)
        {
            lock (sync)
            {
                if (showUntilUtc == null || nowUtc >= showUntilUtc.Value) return null;
                return (callsign, where);
            }
        }

        /// <summary>Hides it now (right click on the VSCS). A tone still due at PTT release still plays.</summary>
        public void Dismiss()
        {
            lock (sync) showUntilUtc = null;
        }

        /// <summary>Forgets it (disconnect).</summary>
        public void Reset()
        {
            lock (sync)
            {
                showUntilUtc = null;
                pendingTone = false;
                callsign = where = null;
            }
        }
    }
}
