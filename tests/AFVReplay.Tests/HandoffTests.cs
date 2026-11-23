using System;
using System.Collections.Generic;
using System.Linq;
using AFVReplay.Audio;
using AFVReplay.Handoff;
using AFVReplay.VatSys;

namespace AFVReplay.Tests
{
    /// <summary>Handoff Alert (Matt's plugin, merged into Say Again).</summary>
    internal static class HandoffTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static TrackSnapshot In(string cs) => new TrackSnapshot(cs, true, false, false);
        private static TrackSnapshot Out(string cs) => new TrackSnapshot(cs, false, true, true);
        private static TrackSnapshot Mine(string cs) => new TrackSnapshot(cs, false, false, true);
        private static TrackSnapshot Theirs(string cs) => new TrackSnapshot(cs, false, false, false);

        internal static void HandoffTrackerFollowsHandoffs()
        {
            var t = new HandoffTracker();
            var now = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan? five = TimeSpan.FromSeconds(5);
            string Run(double s, bool on = true, TimeSpan? repeat = null, params TrackSnapshot[] tracks) =>
                string.Join(",", t.Update(tracks, now.AddSeconds(s), on, repeat ?? five));

            // A handoff to you: alerts at once, repeats every 5 s, stops when accepted.
            Assert(Run(0, true, null, Theirs("QFA1")) == "", "nothing happening");
            Assert(Run(1, true, null, In("QFA1")) == "Incoming", "handoff in alerts");
            Assert(Run(2, true, null, In("QFA1")) == "", "no repeat before 5 s");
            Assert(Run(6, true, null, In("QFA1")) == "Repeat", "repeats after 5 s");
            Assert(Run(8, true, null, In("QFA1"), In("VOZ2")) == "Incoming", "a second handoff in alerts at once");
            Assert(Run(10, true, null, In("QFA1"), In("VOZ2")) == "", "and restarts the repeat, no double beeps");
            Assert(Run(13, true, null, In("QFA1"), In("VOZ2")) == "Repeat", "one repeat stream for both");
            Assert(t.Waiting == 2, "two waiting");
            Assert(Run(14, true, null, Mine("QFA1"), In("VOZ2")) == "", "accepting one");
            Assert(Run(18, true, null, Mine("QFA1"), In("VOZ2")) == "Repeat", "still repeats for the other");
            Assert(Run(19, true, null, Mine("QFA1"), Theirs("VOZ2")) == "", "the other withdrawn");
            Assert(Run(40, true, null, Mine("QFA1"), Theirs("VOZ2")) == "" && t.Waiting == 0, "no repeats once none wait");

            // Repeat off: alerts once only.
            t = new HandoffTracker();
            Assert(string.Join(",", t.Update(new[] { In("JST3") }, now, true, null)) == "Incoming", "alert with repeat off");
            Assert(t.Update(new[] { In("JST3") }, now.AddMinutes(5), true, null).Count == 0, "no repeat when off");

            // Your handoff out: accepted when it's no longer yours.
            t = new HandoffTracker();
            Assert(Run(0, true, null, Mine("QFA9")) == "", "yours");
            Assert(Run(1, true, null, Out("QFA9")) == "", "handing off");
            Assert(Run(2, true, null, Theirs("QFA9")) == "Accepted", "accepted");
            Assert(Run(3, true, null, Theirs("QFA9")) == "", "only once");
            // Withdrawn or refused: back with you, no accepted tone (refused has its own, from vatSys's error).
            Assert(Run(4, true, null, Out("VOZ8")) == "", "handing off");
            Assert(Run(5, true, null, Mine("VOZ8")) == "", "refused/withdrawn: silent");
            Assert(Run(6, true, null, Theirs("VOZ8")) == "", "later losing it isn't an acceptance");
            // The plan disappears mid-handoff: nothing.
            Assert(Run(7, true, null, Out("JST7")) == "", "handing off");
            Assert(Run(8, true, null) == "", "plan gone: nothing");
            Assert(Run(9, true, null, Theirs("JST7")) == "", "and forgotten");

            // Off: follows along silently; switched on with a handoff waiting, alerts straight away.
            t = new HandoffTracker();
            Assert(Run(0, false, null, In("QFA5"), Out("VOZ5")) == "", "off: silent");
            Assert(Run(1, false, null, In("QFA5"), Theirs("VOZ5")) == "", "off: accepted silent too");
            Assert(Run(2, true, null, In("QFA5")) == "Incoming", "switched on mid-handoff alerts");
            Assert(Run(3, false, null, In("QFA5")) == "", "off again");
            Assert(Run(20, false, null, In("QFA5")) == "", "no repeats while off");

            // Refusals: vatSys's error message.
            Assert(HandoffTracker.IsRefusal("Handoff refused by ML-BIK_CTR"), "refusal recognised");
            Assert(!HandoffTracker.IsRefusal("Say Again: something else") && !HandoffTracker.IsRefusal(null), "other errors ignored");
        }

        internal static void HandoffMonitorPlaysTones()
        {
            var played = new List<AlertTone>();
            var player = new AlertPlayer { PlayOverride = (tone, _) => { lock (played) played.Add(tone); } };
            bool on = false;
            var tracks = new List<TrackSnapshot> { Mine("QFA1"), Theirs("VOZ2") };
            using (var m = new HandoffMonitor(player, () => on, () => TimeSpan.FromSeconds(5), () => tracks.ToArray()))
            {
                m.Tick();
                tracks[1] = In("VOZ2");
                m.Tick();
                Assert(!player.IsPlaying, "off: nothing plays");
                on = true;
                m.Tick();
                Assert(player.IsPlaying, "a tone counts as busy for Pause Media while it plays");
                tracks[0] = Out("QFA1"); m.Tick();
                tracks[0] = Theirs("QFA1"); m.Tick();
                System.Threading.Thread.Sleep(1200); // let the queue play
                lock (played) Assert(string.Join(",", played) == "Incoming,Accepted", "played: " + string.Join(",", played));
                Assert(!player.IsPlaying, "not busy once played");
            }
        }

        internal static void HandoffTonesPlayThroughVatSys()
        {
            // The tones: two 110 ms tones 45 ms apart, rising / falling / flat.
            var rising = AlertPlayer.Samples(AlertTone.Incoming, 44100, 2);
            int frames = rising.Length / 2;
            Assert(Math.Abs(frames / 44100.0 - AlertPlayer.Length.TotalSeconds) < 0.002, "tone length " + frames / 44100.0);
            Assert(rising.Max() < 1f && rising.Max() > 0.3f, "level " + rising.Max());
            Assert(rising[0] == rising[1], "same in both channels");
            int Crossings(float[] d, int from, int to) { int n = 0; for (int i = from + 2; i < to; i += 2) if (d[i - 2] < 0 && d[i] >= 0) n++; return n; }
            int toneFrames = 44100 * 110 / 1000;
            Assert(Crossings(rising, 0, toneFrames * 2) < Crossings(rising, rising.Length - toneFrames * 2, rising.Length), "incoming rises");
            var falling = AlertPlayer.Samples(AlertTone.Accepted, 44100, 2);
            Assert(Crossings(falling, 0, toneFrames * 2) > Crossings(falling, falling.Length - toneFrames * 2, falling.Length), "accepted falls");
            var halfVolume = AlertPlayer.Samples(AlertTone.Incoming, 44100, 2, 0.5f);
            Assert(Math.Abs(halfVolume.Max() - rising.Max() / 2) < 0.0001, "alert volume scales samples");
            Assert(AlertPlayer.Samples(AlertTone.Incoming, 44100, 2, 0).All(s => s == 0), "zero alert volume is silent");

            // A sound vatSys can play: built in memory, the same shape as one vatSys loads from a file.
            Assert(VatSysBridge.ValidateAlertSound() == null, "alert bridge missing: " + VatSysBridge.ValidateAlertSound());
            var ours = VatSysBridge.ReadCachedSound(VatSysBridge.MakeCachedSound(rising, 44100, 2));
            var tone = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Program.VatSysBin), "wav", "Tone.wav");
            if (System.IO.File.Exists(tone))
            {
                var theirs = VatSysBridge.ReadCachedSound(VatSysBridge.LoadCachedSound(tone));
                Assert(ours.format.Encoding == theirs.format.Encoding && ours.format.BitsPerSample == theirs.format.BitsPerSample,
                    $"format {ours.format} vs vatSys's {theirs.format}");
                Assert(Math.Abs(theirs.duration - theirs.data.Length / (double)theirs.format.Channels / theirs.format.SampleRate) < 1e-6, "vatSys's duration is in seconds");
            }
            Assert(ours.data == rising && Math.Abs(ours.duration - AlertPlayer.Length.TotalSeconds) < 0.002 && ours.format.SampleRate == 44100 && ours.format.Channels == 2, "our sound");

            // Windows fallback: a valid 16-bit WAV.
            using (var wav = AlertPlayer.Wav(AlertTone.Refused, 48000, 2))
            using (var reader = new NAudio.Wave.WaveFileReader(wav))
                Assert(reader.WaveFormat.BitsPerSample == 16 && Math.Abs(reader.TotalTime.TotalSeconds - AlertPlayer.Length.TotalSeconds) < 0.002, "fallback wav");
        }

        internal static void HandoffSettings()
        {
            var s = new ReplaySettings();
            Assert(!s.HandoffAlert, "off by default");
            Assert(s.HandoffVolumePercent == 100, "handoff volume defaults to current level");
            Assert(s.HandoffRepeat == TimeSpan.FromSeconds(5), "repeats every 5 s by default");
            s.HandoffVolumePercent = -1; s.Normalise();
            Assert(s.HandoffVolumePercent == 0, "handoff volume has a zero minimum");
            s.HandoffVolumePercent = 101; s.Normalise();
            Assert(s.HandoffVolumePercent == 100, "handoff volume is capped at 100%");
            s.HandoffRepeatSeconds = 99; s.Normalise();
            Assert(s.HandoffRepeatSeconds == 10, "capped at 10 s");
            s.HandoffRepeatSeconds = 0; s.Normalise();
            Assert(s.HandoffRepeatSeconds == ReplaySettings.RepeatOff && s.HandoffRepeat == null, "Off");

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afvreplay-test-" + Guid.NewGuid() + ".xml");
            try
            {
                new ReplaySettings { HandoffAlert = true, HandoffVolumePercent = 65, HandoffRepeatSeconds = 7 }.Save(path);
                var loaded = ReplaySettings.Load(path);
                Assert(loaded.HandoffAlert && loaded.HandoffVolumePercent == 65 && loaded.HandoffRepeatSeconds == 7, "saved");
            }
            finally { System.IO.File.Delete(path); }

            var plugin = new ReplayPlugin(new ReplaySettings { HandoffVolumePercent = 35 });
            Assert(Math.Abs(plugin.Alerts.Volume - 0.35f) < 0.001f, "handoff volume applied to alert player");
            Assert(UI.VscsInjector.HandoffButtonBounds(2) == new System.Drawing.Rectangle(3 + 6 * 86 - 1, 3 + 66 - 1, 82, 62), "where Matt's plugin had it");
        }

        internal static void AfkHoldsCallsAndRequestsBreak()
        {
            // Calls made while AFK stay until the keep time after AFK ends; older calls expire as normal.
            var store = new ClipStore { Retention = TimeSpan.FromMinutes(1) };
            var t0 = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
            Clip Call(DateTime end) => new Clip(store.NextId(), end.AddSeconds(-3), end, 0, "Hotline", new[] { "GIG_GND" }, false, new List<byte[]>(), 0, isSilent: true);
            var before = Call(t0);
            store.Add(before);
            store.SetHoldNew(true, t0.AddSeconds(10));
            var during = Call(t0.AddSeconds(20));
            store.Add(during);
            store.Prune(t0.AddMinutes(30));
            Assert(!store.Contains(before.Id), "a call from before AFK expires as normal");
            Assert(store.Contains(during.Id), "a call made while AFK is kept however long you're away");
            var afkOff = t0.AddMinutes(40);
            store.SetHoldNew(false, afkOff);
            store.Prune(afkOff.AddSeconds(50));
            Assert(store.Contains(during.Id), "still kept just under the keep time after AFK ends");
            store.Prune(afkOff.AddSeconds(61));
            Assert(!store.Contains(during.Id), "gone the keep time after AFK ends");
            var after = Call(afkOff.AddSeconds(70));
            store.Add(after);
            store.Prune(afkOff.AddSeconds(70 + 61));
            Assert(!store.Contains(after.Id), "calls after AFK expire as normal");
            store.SetHoldNew(true, afkOff.AddMinutes(5));
            store.Add(Call(afkOff.AddMinutes(6)));
            store.Clear();
            Assert(store.Count == 0, "disconnecting still wipes held calls");

            // Request Break follows AFK.
            bool connected = false, breakOn = false, holding = false, vatsysAccepts = true;
            int clicks = 0;
            var afk = new AfkMode(h => holding = h, () => connected, () => breakOn, () => { clicks++; if (vatsysAccepts) breakOn = !breakOn; });
            afk.Set(true);
            Assert(afk.On && holding && clicks == 0, "AFK while offline: holds calls, no break yet");
            connected = true; afk.Tick();
            Assert(breakOn && clicks == 1, "break requested once connected");
            afk.Tick(); afk.Tick();
            Assert(clicks == 1, "only once");
            connected = false; breakOn = false; afk.Tick();
            Assert(afk.On, "disconnecting doesn't end AFK");
            connected = true; afk.Tick();
            Assert(breakOn && clicks == 2, "break requested again on reconnecting");
            breakOn = false; afk.Tick(); // you turn it off in Tools
            Assert(!afk.On && !holding && clicks == 2, "turning the break off ends AFK");

            breakOn = true; // already requesting a break before AFK
            afk.Set(true); afk.Tick();
            Assert(clicks == 2, "no click when the break is already on");
            afk.Set(false);
            Assert(!breakOn && clicks == 3 && !holding, "turning AFK off turns the break off");

            vatsysAccepts = false; // vatSys won't request a break (say, not a valid ATC position)
            afk.Set(true); afk.Tick(); afk.Tick(); afk.Tick();
            Assert(clicks == 4 && afk.On, "tries once, doesn't keep clicking, AFK stays on");
            afk.Set(false);
            Assert(clicks == 4, "nothing to turn off");

            Assert(new ReplayPlugin(new ReplaySettings()).Afk.On == false, "AFK starts off");
        }

        internal static void AfkMarkersInTheList()
        {
            var t = new DateTime(2026, 11, 22, 4, 31, 0, DateTimeKind.Utc);
            long id = 0;
            Clip Call(double minsAgo) => new Clip(++id, t.AddMinutes(-minsAgo).AddSeconds(-3), t.AddMinutes(-minsAgo), 0, "Hotline", new[] { "GIG_GND" }, false, new List<byte[]>(), 0, isSilent: true);
            string Rows(IReadOnlyList<Clip> calls, params AfkPeriod[] periods) =>
                string.Join(" | ", AfkMarker.Place(calls, periods).Select(r => r is AfkMarker m ? m.Text : ((Clip)r).Id.ToString()));
            var calls = new[] { Call(1), Call(8), Call(21), Call(28), Call(32) }; // ids 1-5, newest first
            var away = new AfkPeriod(t.AddMinutes(-29), t.AddMinutes(-5));

            Assert(Rows(calls) == "1 | 2 | 3 | 4 | 5", "no AFK: just the calls");
            Assert(Rows(calls, away) == "1 | Back at Keyboard 0426Z | 2 | 3 | 4 | Away from Keyboard 0402Z | 5", "both lines: " + Rows(calls, away));
            Assert(Rows(calls.Take(4).ToList(), away) == "1 | Back at Keyboard 0426Z | 2 | 3 | 4 | Away from Keyboard 0402Z",
                "latest period markers remain visible when calls on either side expire");
            Assert(Rows(calls.Skip(1).ToList(), away) == "Back at Keyboard 0426Z | 2 | 3 | 4 | Away from Keyboard 0402Z | 5",
                "latest period markers remain visible without calls on either side");
            Assert(Rows(calls, new AfkPeriod(t.AddMinutes(-29), null)) == "1 | 2 | 3 | 4 | Away from Keyboard 0402Z | 5", "still AFK: Away only");
            Assert(Rows(calls, new AfkPeriod(t.AddMinutes(-15), t.AddMinutes(-10))) == "1 | 2 | Back at Keyboard 0421Z | Away from Keyboard 0416Z | 3 | 4 | 5",
                "away with no calls: the two lines together");
            Assert(Rows(new Clip[0], away) == "Back at Keyboard 0426Z | Away from Keyboard 0402Z" &&
                   Rows(new[] { calls[0] }, away) == "1 | Back at Keyboard 0426Z | Away from Keyboard 0402Z",
                   "the latest period is shown even without calls");
            var previous = new AfkPeriod(t.AddMinutes(-60), t.AddMinutes(-40));
            var current = new AfkPeriod(t.AddMinutes(-10), null);
            Assert(Rows(new Clip[0], previous, current) == "Away from Keyboard 0421Z",
                "older periods still need calls on both sides");
            Assert(new AfkMarker(true, t).Equals(new AfkMarker(true, t)) && !new AfkMarker(true, t).Equals(new AfkMarker(false, t)), "same marker each refresh");

            // AFK records when it was on.
            var now = t;
            var afk = new AfkMode(_ => { }, () => false, () => false, () => { }, () => now);
            afk.Set(true); now = now.AddMinutes(20); afk.Set(false);
            var p = afk.Periods.Single();
            Assert(p.StartUtc == t && p.EndUtc == t.AddMinutes(20), "period recorded");
            now = now.AddMinutes(30); afk.Set(true);
            Assert(afk.Periods.Count == 1 && afk.Periods[0].EndUtc == null, "old periods forgotten");
        }
    }
}
