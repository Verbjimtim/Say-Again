namespace AFVReplay.Audio
{
    /// <summary>One VSCS hotline/coldline as vatSys reports it: the other controller, the kind of line and its state.</summary>
    internal sealed class LineInfo
    {
        public LineInfo(string callsign, string type, string state, bool callsignFromAtc = true)
        {
            CallsignFromAtc = callsignFromAtc;
            Callsign = callsign;
            Type = type;
            State = state;
        }

        /// <summary>The other controller, e.g. "ML-ELW_CTR" (null if vatSys doesn't know).</summary>
        public string Callsign { get; }

        /// <summary>"Hotline" or "Coldline".</summary>
        public string Type { get; }

        /// <summary>vatSys's state: "Open" (connected), "Inbound" (ringing in), "Outbound" (calling out), "Closed".</summary>
        public string State { get; }

        public bool IsOpen => State == "Open";

        /// <summary>The callsign is the connected controller's; false when it is only the line's name (no controller attached).</summary>
        public bool CallsignFromAtc { get; }
    }
}
