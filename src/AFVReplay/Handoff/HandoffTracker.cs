using System;
using System.Collections.Generic;

namespace AFVReplay.Handoff
{
    /// <summary>One flight plan's handoff state, as vatSys shows it right now.</summary>
    internal struct TrackSnapshot
    {
        /// <summary>The callsign. Only ever held in memory, never logged.</summary>
        public string Key;
        /// <summary>Being handed to you (vatSys's HandoverIn state).</summary>
        public bool HandoffIn;
        /// <summary>Being handed by you to someone else (HandoverOut).</summary>
        public bool HandoffOut;
        /// <summary>You have jurisdiction.</summary>
        public bool TrackedByMe;

        public TrackSnapshot(string key, bool handoffIn, bool handoffOut, bool trackedByMe)
        {
            Key = key;
            HandoffIn = handoffIn;
            HandoffOut = handoffOut;
            TrackedByMe = trackedByMe;
        }
    }

    internal enum HandoffEvent { Incoming, Repeat, Accepted }

    /// <summary>
    /// Handoff Alert's rules (from Matt's Handoff Alert plugin), without any vatSys calls:
    /// <list type="bullet">
    /// <item>Incoming: a plan starts being handed to you. Repeats every <c>repeat</c> while any handoff to you is
    /// still waiting, until each is accepted, refused or withdrawn.</item>
    /// <item>Accepted: a plan you were handing off is no longer yours.</item>
    /// <item>A plan you were handing off that disappears (pilot disconnected, plan dropped) plays nothing.</item>
    /// </list>
    /// Refusals come from vatSys's error message instead (<see cref="IsRefusal"/>).
    /// While switched off it keeps following handoffs but reports nothing; switched on with a handoff to
    /// you already waiting, it alerts straight away.
    /// </summary>
    internal sealed class HandoffTracker
    {
        private readonly HashSet<string> incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> outgoing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime lastAlert;
        private bool wasEnabled;

        /// <summary>Handoffs to you waiting to be accepted.</summary>
        public int Waiting => incoming.Count;

        /// <param name="tracks">Every flight plan vatSys has.</param>
        /// <param name="repeat">How often to repeat while a handoff to you waits, or null for no repeat.</param>
        public List<HandoffEvent> Update(IEnumerable<TrackSnapshot> tracks, DateTime now, bool enabled, TimeSpan? repeat)
        {
            var events = new List<HandoffEvent>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool newIncoming = false;

            foreach (var t in tracks)
            {
                if (string.IsNullOrEmpty(t.Key) || !seen.Add(t.Key)) continue;

                if (t.HandoffOut) outgoing.Add(t.Key);
                if (outgoing.Contains(t.Key) && !t.TrackedByMe)
                {
                    outgoing.Remove(t.Key);
                    events.Add(HandoffEvent.Accepted);
                }
                else if (!t.HandoffOut)
                {
                    outgoing.Remove(t.Key); // withdrawn, or refused and back with you
                }

                if (t.HandoffIn) { if (incoming.Add(t.Key)) newIncoming = true; }
                else incoming.Remove(t.Key); // accepted, refused or withdrawn
            }

            // Plans that have gone: forget them, silently.
            outgoing.RemoveWhere(k => !seen.Contains(k));
            incoming.RemoveWhere(k => !seen.Contains(k));

            bool justEnabled = enabled && !wasEnabled;
            wasEnabled = enabled;
            if (!enabled) return new List<HandoffEvent>();

            if (incoming.Count > 0 && (newIncoming || justEnabled))
            {
                events.Insert(0, HandoffEvent.Incoming);
                lastAlert = now;
            }
            else if (incoming.Count > 0 && repeat.HasValue && now - lastAlert >= repeat.Value)
            {
                events.Insert(0, HandoffEvent.Repeat);
                lastAlert = now;
            }
            return events;
        }

        /// <summary>vatSys's error when someone refuses your handoff ("Handoff refused by ML-BIK_CTR").</summary>
        public static bool IsRefusal(string errorMessage) =>
            errorMessage != null && errorMessage.StartsWith("Handoff refused by ", StringComparison.OrdinalIgnoreCase);
    }
}
