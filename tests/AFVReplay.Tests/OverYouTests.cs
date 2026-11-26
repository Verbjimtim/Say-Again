using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AFVReplay.Audio;
using AFVReplay.Handoff;
using AFVReplay.VatSys;
using GeoVR.Client;
using GeoVR.Shared;

namespace AFVReplay.Tests
{
    /// <summary>
    /// Over You Alert: a station calling while you transmit. Driven through AFV's own PTT
    /// (SoundcardSampleProvider.PTT), which mutes the receivers you transmit on and drops their packets.
    /// </summary>
    internal static partial class Tests
    {
        private const uint Bik = 125_600_000, Ini = 118_700_000;

        private static ReceiverSampleProvider Receiver(Rig rig, ushort id) => VatSysBridge.GetReceivers(rig.Ssp)[id];

        private static ReceiverTap TapOf(Rig rig, ushort id) =>
            VatSysBridge.GetSources(VatSysBridge.GetMixer(rig.Ssp)).OfType<ReceiverTap>().First(t => t.Receiver.ID == id);

        private static void PressPtt(Rig rig, params ushort[] ids)
        {
            rig.Tx = true; // vatSys's Audio.Transmitting goes true as AFV is told PTT
            rig.Ssp.PTT(true, ids.Select(i => new TxTransceiverDto { ID = i }).ToArray());
        }

        private static void ReleasePtt(Rig rig)
        {
            rig.Tx = false;
            rig.Ssp.PTT(false, new TxTransceiverDto[0]);
        }

        private static void OverYou_CaughtSilentlyAndKept()
        {
            using (var rig = new Rig((1, Bik)))
            {
                rig.Pump(200);
                PressPtt(rig, 1);
                Assert(Receiver(rig, 1).Mute, "AFV mutes the receiver you transmit on (what this feature works around)");
                rig.Pump(20);
                Assert(!Receiver(rig, 1).Mute && TapOf(rig, 1).IsSilencing, "tap takes over the mute");

                var heard = rig.Transmit("QFA9", 1.2, 500, (1, Bik, 1f));
                Assert(heard.All(x => x == 0), "nothing is heard over your transmission, rms " + Rms(heard));
                rig.Pump(100);
                Assert(rig.Hook.CalledOverFrequencies().Length == 0, "block over once they stop, though you're still transmitting");
                Assert(rig.OverYou.Count == 1 && rig.OverYou[0].Callsign == "QFA9" && rig.OverYou[0].FrequencyHz == Bik,
                    "reported once: " + string.Join(",", rig.OverYou.Select(o => o.Callsign)));

                ReleasePtt(rig);
                rig.Pump(1000);
                Assert(!TapOf(rig, 1).IsSilencing && !Receiver(rig, 1).Mute, "back to normal after release");
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));
                var clips = rig.Store.Snapshot();
                Assert(clips.Count == 1, "the call is kept: " + clips.Count);
                var c = clips[0];
                Assert(c.OverYou && !c.Blocked && !c.IsOwn && c.CallsignText == "QFA9", "kept as over you, from QFA9");
                Assert(Rms(c.Decode()) > 0.05, "with its audio, rms " + Rms(c.Decode()));

                // Next transmission: reported again.
                PressPtt(rig, 1);
                rig.Pump(20);
                rig.Transmit("VOZ4", 0.8, 600, (1, Bik, 1f));
                ReleasePtt(rig);
                Assert(rig.OverYou.Count == 2 && rig.OverYou[1].Callsign == "VOZ4", "each transmission reports its own");
            }
        }

        private static void OverYou_RestPlaysAfterRelease()
        {
            using (var rig = new Rig((1, Bik)))
            {
                rig.Pump(200);
                PressPtt(rig, 1);
                rig.Pump(20);
                var block = new float[960];
                var before = new List<float>();
                var after = new List<float>();
                int i = 0;
                foreach (var dto in Packets("JST7", 2.0, 450, new (ushort, uint, float)[] { (1, Bik, 1f) }))
                {
                    if (i == 30)
                    {
                        Assert(rig.Hook.CalledOverFrequencies().SequenceEqual(new[] { Bik }), "125.6 is blocked right now");
                        ReleasePtt(rig); // 0.6 s in
                    }
                    if (i++ == 32) Assert(rig.Hook.CalledOverFrequencies().Length == 0, "not blocked once you release");
                    rig.Ssp.AddOpusSamples(dto, dto.Transceivers);
                    int n = rig.Ssp.Read(block, 0, block.Length);
                    (rig.Tx ? before : after).AddRange(block.Take(n));
                }
                Assert(before.All(x => x == 0), "silent while you transmit");
                double rest = Rms(after.Skip(9600).ToArray());
                Assert(rest > 0.05, "the rest of the call plays once you release, rms " + rest);
                rig.Pump(1000);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));
                var clips = rig.Store.Snapshot();
                Assert(clips.Count == 1 && clips[0].OverYou && clips[0].Duration.TotalSeconds > 1.8,
                    "one clip, the whole call: " + string.Join(";", clips.Select(c => c.Duration.TotalSeconds.ToString("0.0"))));
            }
        }

        private static void OverYou_OffLeavesAfvAlone()
        {
            using (var rig = new Rig((1, Bik)))
            {
                rig.CatchOverYou = false;
                rig.Pump(200);
                PressPtt(rig, 1);
                rig.Pump(100);
                Assert(Receiver(rig, 1).Mute && !TapOf(rig, 1).IsSilencing, "setting off: AFV's mute is left alone");
                var heard = rig.Transmit("QFA9", 1.0, 500, (1, Bik, 1f));
                Assert(heard.All(x => x == 0), "nothing heard");

                // Turning it on mid-transmission doesn't matter either way; turning it off mid-transmission
                // must not let the call through.
                rig.CatchOverYou = true;
                rig.Pump(20);
                Assert(TapOf(rig, 1).IsSilencing, "picked up while still transmitting");
                rig.CatchOverYou = false;
                heard = rig.Transmit("VOZ4", 0.6, 500, (1, Bik, 1f));
                Assert(heard.All(x => x == 0), "turning it off mid-transmission keeps you from hearing it");
                ReleasePtt(rig);
                rig.Pump(1000);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));
                Assert(rig.Store.Snapshot().All(c => c.CallsignText != "QFA9"), "nothing caught while off");
            }
        }

        private static void OverYou_OwnEchoAndOtherFrequenciesIgnored()
        {
            using (var rig = new Rig((1, Bik), (2, Ini)))
            {
                rig.Pump(200);
                PressPtt(rig, 1); // transmitting on 125.6 only
                rig.Pump(20);
                Assert(!TapOf(rig, 2).IsSilencing && !Receiver(rig, 2).Mute, "not transmitting on 118.7: untouched");

                var echo = rig.Transmit(rig.OwnCallsign, 1.0, 500, (1, Bik, 1f));
                Assert(echo.All(x => x == 0) && rig.OverYou.Count == 0, "your own callsign is never a call over you");

                var other = rig.Transmit("QLK2", 1.0, 700, (2, Ini, 1f));
                Assert(Rms(other) > 0.05, "a call on a frequency you're only receiving is heard as normal");
                Assert(rig.OverYou.Count == 0, "and isn't over you");
                ReleasePtt(rig);
                rig.Pump(1000);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));
                var clips = rig.Store.Snapshot();
                Assert(clips.Count == 1 && clips[0].CallsignText == "QLK2" && !clips[0].OverYou,
                    "only QLK2 kept, normally: " + string.Join(",", clips.Select(c => c.CallsignText)));
            }
        }

        private static void OverYou_UnhookMidTransmissionRemutes()
        {
            using (var rig = new Rig((1, Bik)))
            {
                rig.Pump(200);
                PressPtt(rig, 1);
                rig.Pump(20);
                Assert(!Receiver(rig, 1).Mute, "un-muted by the tap");
                var receiver = Receiver(rig, 1);
                rig.Hook.Dispose();
                Assert(receiver.Mute, "the bare receiver goes back muted while you still transmit");
                ReleasePtt(rig);
                Assert(!receiver.Mute, "AFV un-mutes it on release as usual");
            }
        }

        private static void OverYou_AlertShowsAndTonesOnRelease()
        {
            var t = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
            var a = new OverYouAlert();
            Assert(a.Current(t) == null && !a.TransmitEnded(t), "nothing yet");
            Assert(!a.Report("QFA9", "125.600", stillTransmitting: true, nowUtc: t), "no tone while you transmit");
            Assert(a.Current(t.AddSeconds(30))?.Callsign == "QFA9", "shown for as long as you transmit");
            Assert(a.TransmitEnded(t.AddSeconds(31)), "tone on release");
            Assert(!a.TransmitEnded(t.AddSeconds(40)), "only once");
            Assert(a.Current(t.AddSeconds(36))?.Where == "125.600", "still shown just after release");
            Assert(a.Current(t.AddSeconds(31) + OverYouAlert.ShowFor) == null, "gone after " + OverYouAlert.ShowFor.TotalSeconds + " s");
            Assert(a.Report("VOZ4", "118.700", stillTransmitting: false, nowUtc: t.AddMinutes(1)), "caught just after release: tone now");
            a.Dismiss();
            Assert(a.Current(t.AddMinutes(1)) == null, "right click dismisses");
            Assert(a.Count == 2, "counted");

            // Through the plugin: the settings, the tone, the VSCS text.
            var d = new ReplaySettings();
            Assert(d.OverYouAlert && d.OverYouUi && !d.OverYouTone && d.OverYouToneDuring,
                "defaults: Blocked on, Blocked UI on, Blocked Tone off, Tone Timing During");
            var plugin = new ReplayPlugin(new ReplaySettings());
            var tones = new List<AlertTone>();
            plugin.Alerts.PlayOverride = (tone, len) => { lock (tones) tones.Add(tone); };
            plugin.BlockedFrequenciesOverride = () => new[] { Bik };
            int Tones(int expect) { for (int i = 0; i < 50 && tones.Count < expect; i++) Thread.Sleep(20); Thread.Sleep(100); lock (tones) return tones.Count; }
            var qfa = new OverYouEventArgs(Bik, "QFA9");

            // Defaults: shown, no tone.
            plugin.OnOverYou(qfa, stillTransmitting: true);
            Assert(plugin.OverYouNow?.Callsign == "QFA9" && plugin.OverYouNow?.Where == "125.600", "VSCS shows who and where");
            Assert(UI.VscsInjector.OverYouText(plugin.OverYouNow.Value) == "QFA9 125.600", "VSCS text");
            Assert(plugin.BlockedFrequencies.SequenceEqual(new[] { Bik }), "frequency flashes");
            plugin.OnTransmitEnded();
            Assert(Tones(1) == 0, "Blocked Tone off by default: no tone");
            Assert(plugin.OverYou.Count == 1, "counted for the status line");

            // Tone on, During: the tone as soon as you're blocked, none again at release.
            plugin.Settings.OverYouTone = true;
            plugin.OnOverYou(qfa, stillTransmitting: true);
            Assert(Tones(1) == 1, "During: tone while still transmitting");
            plugin.OnTransmitEnded();
            Assert(Tones(2) == 1, "During: not again at release");

            // Tone on, After: nothing while transmitting, the tone at release.
            plugin.Settings.OverYouToneDuring = false;
            plugin.OnOverYou(qfa, stillTransmitting: true);
            Assert(Tones(2) == 1, "After: no tone while transmitting");
            plugin.OnTransmitEnded();
            Assert(Tones(2) == 2, "After: tone at release");
            plugin.OnOverYou(qfa, stillTransmitting: false);
            Assert(Tones(3) == 3, "After: caught just after release, tone straight away");
            Assert(tones.All(t => t == AlertTone.OverYou), "the over-you tone");

            // Blocked UI off: no VSCS display, the tone still follows its own setting.
            plugin.Settings.OverYouUi = false;
            plugin.OnOverYou(qfa, stillTransmitting: false);
            Assert(plugin.OverYouNow == null && plugin.BlockedFrequencies.Length == 0, "Blocked UI off: nothing on the VSCS");
            Assert(Tones(4) == 4, "Blocked UI off: tone still plays");

            // Blocked off: nothing at all, whatever the others say.
            plugin.Settings.OverYouUi = true;
            plugin.Settings.OverYouAlert = false;
            plugin.OnOverYou(qfa, stillTransmitting: false);
            Assert(plugin.OverYouNow == null && plugin.BlockedFrequencies.Length == 0, "Blocked off: nothing shown");
            Assert(Tones(5) == 4, "Blocked off: no tone");

            var over = AlertPlayer.Samples(AlertTone.OverYou, 48000, 1);
            var refused = AlertPlayer.Samples(AlertTone.Refused, 48000, 1);
            Assert(over.Length == refused.Length && Crossings(over, 0, over.Length) > 2 * Crossings(refused, 0, refused.Length),
                "over-you tone: same length as the handoff tones, clearly higher than Refused");
        }

        /// <summary>Set to a folder to save what the flashing looks like (on and off phase).</summary>
        internal static string FlashPngPrefix = Environment.GetEnvironmentVariable("SAYAGAIN_FLASH_PNG");

        private static void OverYou_BlockedFrequencyFlashesRed(string profile)
        {
            try { Preview.LoadProfileColours(profile); } catch (Exception) { } // already loaded by an earlier test
            var asm = typeof(vatsys.AFV).Assembly;
            // vatSys's frequency list lives on its Audio object, which only exists once vatSys starts its audio:
            // stand in a bare one holding our own list (restored afterwards).
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var instanceField = typeof(vatsys.Audio).GetField("Instance", any);
            var previousAudio = instanceField.GetValue(null);
            var audio = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(vatsys.Audio));
            var freqs = new List<vatsys.VSCSFrequency>();
            typeof(vatsys.Audio).GetField("vscsFrequencies", any).SetValue(audio, freqs);
            instanceField.SetValue(null, audio);
            vatsys.VSCSFrequency Freq(string name, uint hz) => (vatsys.VSCSFrequency)Activator.CreateInstance(typeof(vatsys.VSCSFrequency),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new object[] { name, hz }, null);
            var added = new[] { Freq("ML-BIK_CTR", Bik), Freq("ML-INL_CTR", Ini), Freq("YBBN_ATIS", 124_400_000) };
            foreach (var f in added) freqs.Add(f);
            added[0].Transmit = added[0].Receive = true;
            added[1].Transmit = added[1].Receive = true;
            var mmiField = typeof(vatsys.MMI).GetField("VSCSWindow", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var previous = mmiField.GetValue(null);
            var vscs = (System.Windows.Forms.Form)Activator.CreateInstance(asm.GetType("vatsys.VSCSWindow"), true);
            try
            {
                mmiField.SetValue(null, vscs);
                vscs.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                vscs.Location = new System.Drawing.Point(-4000, -4000);
                vscs.ShowInTaskbar = false;
                vscs.Show();
                var plugin = new ReplayPlugin(new ReplaySettings());
                uint[] blocked = { Ini };
                plugin.BlockedFrequenciesOverride = () => blocked;
                var t = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
                t = t.AddTicks(-(t.Ticks % (2 * UI.VscsInjector.FlashPhase.Ticks))); // start of an "on" phase
                using (var injector = new UI.VscsInjector(plugin))
                {
                    injector.Clock = () => t;
                    injector.Start();
                    Preview.Pump(10);
                    injector.Sync();
                    Preview.Pump(5);
                    int index = freqs.IndexOf(added[1]);
                    int rows = UI.VscsLayout.Rows(VatSysBridge.VscsPanel.ClientSize.Height);
                    var b = injector.BlockedButtons.SingleOrDefault();
                    Assert(b != null && b.Visible, "the blocked frequency's button shows red");
                    Assert(b.Bounds == UI.VscsLayout.Cell(index / rows, index % rows), $"over vatSys's button for it: {b.Bounds}");
                    Assert(b.Face == UI.VscsButton.Faces.Alarm && b.SubText == "BLOCKED" && b.Text == "118.7", $"red, vatSys's own text (118.7) / BLOCKED: {b.Text} / {b.SubText}");
                    Assert(b.ClickThrough, "clicks go through to vatSys's button");
                    if (FlashPngPrefix != null) Preview.Capture(vscs, FlashPngPrefix + "-on.png");

                    t += UI.VscsInjector.FlashPhase;
                    injector.Sync(); Preview.Pump(5);
                    Assert(!b.Visible, "flashes: hidden for the off phase");
                    if (FlashPngPrefix != null) Preview.Capture(vscs, FlashPngPrefix + "-off.png");
                    t += UI.VscsInjector.FlashPhase;
                    injector.Sync();
                    Assert(b.Visible, "and back on");

                    blocked = new uint[0]; // the call over you ended (or you released PTT)
                    injector.Sync();
                    Assert(injector.BlockedButtons.All(x => !x.Visible), "stops when the block ends");

                    blocked = new[] { Ini };
                    plugin.Settings.OverYouUi = false;
                    injector.Sync();
                    Assert(injector.BlockedButtons.All(x => !x.Visible), "nothing with Blocked UI off");
                    plugin.Settings.OverYouUi = true;

                    // Settings → Alerts: Blocked, Blocked UI, Blocked Tone, Tone Timing; each greyed out without the one it needs.
                    injector.OpenPage();
                    injector.Page.ShowSettings(true);
                    injector.Page.ShowSettingsPage(2);
                    Preview.Pump(10);
                    var tg = injector.Page.BlockedToggles;
                    Assert(tg.All(x => x != null), "four Blocked rows");
                    Assert(tg[0].Checked && tg[1].Checked && !tg[2].Checked && tg[3].Checked, "defaults on the page");
                    Assert(tg[3].Text == "During" && tg[1].Enabled && tg[2].Enabled && !tg[3].Enabled, "Tone Timing greyed out while Blocked Tone is off");
                    tg[2].Checked = true;
                    Assert(plugin.Settings.OverYouTone && tg[3].Enabled, "Blocked Tone on: Tone Timing available");
                    tg[3].Checked = false;
                    Assert(!plugin.Settings.OverYouToneDuring && tg[3].Text == "After", "Tone Timing: After");
                    if (FlashPngPrefix != null) Preview.Capture(vscs, FlashPngPrefix + "-settings.png");
                    tg[0].Checked = false;
                    Assert(!plugin.Settings.OverYouAlert && !tg[1].Enabled && !tg[2].Enabled && !tg[3].Enabled, "Blocked off greys out the rest");
                    injector.Sync();
                    Assert(injector.BlockedButtons.All(x => !x.Visible), "nothing with Blocked off");
                }
            }
            finally
            {
                vscs.Close();
                vscs.Dispose();
                mmiField.SetValue(null, previous);
                instanceField.SetValue(null, previousAudio);
            }
        }

        private static int Crossings(float[] a, int from, int to)
        {
            int n = 0;
            for (int i = Math.Max(1, from); i < to; i++) if ((a[i - 1] < 0) != (a[i] < 0)) n++;
            return n;
        }
    }
}
