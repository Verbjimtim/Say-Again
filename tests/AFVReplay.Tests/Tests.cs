using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AFVReplay.Audio;
using AFVReplay.Input;
using AFVReplay.VatSys;
using Concentus.Enums;
using Concentus.Structs;
using GeoVR.Client;
using GeoVR.Shared;
using NAudio.Wave;

namespace AFVReplay.Tests
{
    internal static class Tests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static int failures;
        private static int passes;

        public static int RunAll()
        {
            Diag.Echo = true;
            Run(nameof(BridgeMatchesInstalledVatSys), BridgeMatchesInstalledVatSys);
            Run(nameof(OpusRoundTrip), OpusRoundTrip);
            Run(nameof(RecorderSegmentsOnCallsignEvents), RecorderSegmentsOnCallsignEvents);
            Run(nameof(RecorderIgnoresBlips), RecorderIgnoresBlips);
            Run(nameof(RecorderSplitsVeryLongTransmission), RecorderSplitsVeryLongTransmission);
            Run(nameof(StoreExpiresAndWipes), StoreExpiresAndWipes);
            Run(nameof(InputBindingReadsVatSysFormat), InputBindingReadsVatSysFormat);
            Run(nameof(EndToEnd_RealAfvPipeline), EndToEnd_RealAfvPipeline);
            Run(nameof(EndToEnd_TwoFrequenciesAtOnce), EndToEnd_TwoFrequenciesAtOnce);
            Run(nameof(EndToEnd_DuplicateTransceiversCollapsed), EndToEnd_DuplicateTransceiversCollapsed);
            Run(nameof(EndToEnd_ReceiverChangesAndUnhook), EndToEnd_ReceiverChangesAndUnhook);
            Run(nameof(ReplayRepeatsNewestAndPlaysIntoMix), ReplayRepeatsNewestAndPlaysIntoMix);
            Run(nameof(ReplayKeySkipsOwnAndFollowsNewest), ReplayKeySkipsOwnAndFollowsNewest);
            Run(nameof(SettingsRoundTripHasNoAudio), SettingsRoundTripHasNoAudio);
            Run(nameof(TimeStretchKeepsPitch), TimeStretchKeepsPitch);
            Run(nameof(ReplaySpeedShortensPlayback), ReplaySpeedShortensPlayback);
            Run(nameof(VscsButtonShowsReplay), VscsButtonShowsReplay);
            Run(nameof(SayAgainPlaysInTableOrder), SayAgainPlaysInTableOrder);
            Run(nameof(SecondCopyDisablesItself), SecondCopyDisablesItself);
            Run(nameof(SayAgainListKeepsScrollAndSelection), SayAgainListKeepsScrollAndSelection);
            Run(nameof(SayAgainListShowsCallsFromBeforeOpening), () =>
            {
                const string profile = @"C:\Users\jamie\Documents\vatSys Files\Profiles\Australia";
                if (System.IO.Directory.Exists(profile)) SayAgainListShowsCallsFromBeforeOpening(profile);
            });
            Run(nameof(RecordsAtEightKilohertz), RecordsAtEightKilohertz);
            Run(nameof(CoordLevelsQuietMic), CoordLevelsQuietMic);
            Run(nameof(CoordRecorderCutsUtterances), CoordRecorderCutsUtterances);
            Run(nameof(CoordRecorderIgnoresNoiseAndLoopback), CoordRecorderIgnoresNoiseAndLoopback);
            Run(nameof(CoordRecorderEndsWhenLineCloses), CoordRecorderEndsWhenLineCloses);
            Run(nameof(CoordStoreKeepsTenMinutes), CoordStoreKeepsTenMinutes);
            Run(nameof(OnlyOneReplayAtATime), OnlyOneReplayAtATime);
            Run(nameof(EndToEnd_RealMumbleCoord), EndToEnd_RealMumbleCoord);
            Run(nameof(OwnTxRecordsWhilePttHeld), OwnTxRecordsWhilePttHeld);
            Run(nameof(CoordBleedDropsDuplicates), CoordBleedDropsDuplicates);
            Run(nameof(ConversationMixesBothSides), ConversationMixesBothSides);
            Run(nameof(EndToEnd_RealVatSysMicInputs), EndToEnd_RealVatSysMicInputs);

            Console.WriteLine();
            Console.WriteLine($"{passes} passed, {failures} failed");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                passes++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
                Console.WriteLine(string.Join("\n", (ex.StackTrace ?? "").Split('\n').Take(6).Select(l => l.Trim())));
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        // ---------------------------------------------------------------- unit-level

        private static void BridgeMatchesInstalledVatSys()
        {
            var missing = VatSysBridge.Validate();
            Assert(missing == null, "bridge missing: " + missing);
            var coordMissing = VatSysBridge.ValidateCoord();
            Assert(coordMissing == null, "coord bridge missing: " + coordMissing);
            var micMissing = VatSysBridge.ValidateMic();
            Assert(micMissing == null, "mic bridge missing: " + micMissing);
            Console.WriteLine("     vatSys " + VatSysBridge.VatSysVersion);
        }

        private static void OpusRoundTrip()
        {
            var pcm = Tone(440, 1.0, 0.5f).Select(f => (short)(f * short.MaxValue)).ToArray();
            var frames = OpusCodec.Encode(pcm, pcm.Length);
            int bytes = frames.Sum(f => f.Length);
            var decoded = OpusCodec.Decode(frames, pcm.Length);
            Assert(decoded.Length == pcm.Length, $"length {decoded.Length} != {pcm.Length}");
            double rms = Rms(decoded.Skip(4800).ToArray());
            Assert(rms > 0.25 && rms < 0.45, "decoded rms " + rms);
            Assert(bytes < 5000, "1s of audio used " + bytes + " bytes");
            Console.WriteLine($"     1 s = {bytes} bytes");
        }

        private static void RecorderSegmentsOnCallsignEvents()
        {
            var done = new List<RawRecording>();
            int rxStarts = 0;
            var rec = new ClipRecorder(done.Add, f => rxStarts++);
            var block = new float[960];
            Fill(block, 0.2f);

            Feed(rec, block, 20);                        // idle noise -> pre-roll
            rec.OnCallsignsChanged(new[] { "QFA1" }, 125_600_000);
            Feed(rec, block, 50);                        // 1 s of speech
            rec.OnCallsignsChanged(new[] { "QFA1", "VOZ2" }, 125_600_000); // second station keys up
            Feed(rec, block, 5);
            rec.OnCallsignsChanged(new string[0], 125_600_000);
            Feed(rec, block, 30);                        // hangover then idle

            Assert(done.Count == 1, "expected 1 clip, got " + done.Count);
            Assert(rxStarts == 1, "rx starts " + rxStarts);
            var r = done[0];
            double secs = r.Length / 48000.0;
            Assert(secs > 1.2 && secs < 1.7, "clip length " + secs);
            Assert(r.Callsigns.SequenceEqual(new[] { "QFA1", "VOZ2" }), "callsigns " + string.Join(",", r.Callsigns));
            Assert(r.Blocked, "should be marked blocked");
            Assert(r.FrequencyHz == 125_600_000, "freq");
            Assert(!rec.IsRecording, "should be idle");
        }

        private static void RecorderIgnoresBlips()
        {
            var done = new List<RawRecording>();
            var rec = new ClipRecorder(done.Add, null);
            var block = new float[960];
            rec.OnCallsignsChanged(new[] { "QFA1" }, 1);
            Feed(rec, block, 5); // 100 ms
            rec.OnCallsignsChanged(new string[0], 1);
            Feed(rec, block, 30);
            Assert(done.Count == 0, "blip should be dropped");
        }

        private static void RecorderSplitsVeryLongTransmission()
        {
            var done = new List<RawRecording>();
            var rec = new ClipRecorder(done.Add, null);
            var block = new float[960];
            Fill(block, 0.1f);
            rec.OnCallsignsChanged(new[] { "STUCK" }, 1);
            Feed(rec, block, (int)(ClipRecorder.MaxClipSamples / 960) + 100);
            Assert(done.Count == 1, "expected a split at the max length, got " + done.Count);
            Assert(rec.IsRecording, "should continue recording after the split");
            Assert(done[0].Length <= ClipRecorder.MaxClipSamples + 960, "first part too long");
        }

        private static void StoreExpiresAndWipes()
        {
            var store = new ClipStore { Retention = TimeSpan.FromMinutes(9) };
            Assert(store.Retention == ClipStore.MaxRetention, "retention must be capped at 5 min");
            store.Retention = TimeSpan.FromMinutes(2);
            var now = DateTime.UtcNow;
            var old = MakeClip(store, now.AddMinutes(-3));
            var fresh = MakeClip(store, now.AddSeconds(-10));
            store.Add(old);
            store.Add(fresh);
            store.Prune(now);
            Assert(store.Count == 1 && store.Snapshot()[0] == fresh, "old clip should expire");
            Assert(old.IsWiped && old.Decode() == null, "expired clip should be wiped");
            store.Clear();
            Assert(fresh.IsWiped && store.Count == 0, "clear should wipe");
        }

        private static void InputBindingReadsVatSysFormat()
        {
            var asm = typeof(vatsys.AFV).Assembly;
            var mapType = asm.GetType("vatsys.InputMap");
            var serialize = mapType.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

            object key = Activator.CreateInstance(mapType, BindingFlags.Instance | BindingFlags.Public, null, new object[] { SharpDX.DirectInput.Key.F8 }, null);
            var json = (string)serialize.Invoke(key, null);
            var b = InputBinding.Parse(json);
            Assert(b != null && b.InputType == InputBinding.Kind.Keyboard && b.Key == SharpDX.DirectInput.Key.F8, "keyboard: " + json);
            Assert(b.ToString() == key.ToString(), $"text '{b}' vs vatSys '{key}'");

            object mouse = Activator.CreateInstance(mapType, BindingFlags.Instance | BindingFlags.Public, null, new object[] { SharpDX.DirectInput.MouseOffset.Buttons3 }, null);
            json = (string)serialize.Invoke(mouse, null);
            b = InputBinding.Parse(json);
            Assert(b != null && b.InputType == InputBinding.Kind.Mouse && b.MouseButton == SharpDX.DirectInput.MouseOffset.Buttons3, "mouse: " + json);
            Assert(b.ToString() == mouse.ToString(), $"text '{b}' vs vatSys '{mouse}'");

            var g = Guid.NewGuid();
            object joy = Activator.CreateInstance(mapType, BindingFlags.Instance | BindingFlags.Public, null, new object[] { g, SharpDX.DirectInput.JoystickOffset.Buttons5 }, null);
            json = (string)serialize.Invoke(joy, null);
            b = InputBinding.Parse(json);
            Assert(b != null && b.DeviceGuid == g && b.JoystickButton == SharpDX.DirectInput.JoystickOffset.Buttons5, "joystick: " + json);
            Assert(b.ToString() == joy.ToString(), $"text '{b}' vs vatSys '{joy}'");

            Assert(InputBinding.Parse("") == null && InputBinding.Parse("garbage") == null, "bad input should give null");
        }

        // ---------------------------------------------------------------- end to end, real AFV classes

        private sealed class Rig : IDisposable
        {
            public readonly SoundcardSampleProvider Ssp;
            public readonly ClipStore Store = new ClipStore();
            public readonly ReplayPlayer Player = new ReplayPlayer();
            public readonly ClipFinalizer Finalizer;
            public readonly AudioHook Hook;
            public readonly List<TransceiverDto> Transceivers = new List<TransceiverDto>();
            private readonly float[] block = new float[960];

            public Rig(params (ushort id, uint freq)[] receivers)
            {
                AudioConfig.Instance.VhfEqualizer = EqualizerPresets.VHFEmulation2; // as vatSys sets it
                Ssp = new SoundcardSampleProvider(48000, receivers.Select(r => r.id).ToList(), (s, e) => { });
                foreach (var r in receivers) Transceivers.Add(new TransceiverDto { ID = r.id, Frequency = r.freq });
                Ssp.UpdateTransceivers(Transceivers);
                Finalizer = new ClipFinalizer(Store, ClipFinalizer.FormatFrequency);
                Hook = new AudioHook(() => Ssp, Store, Finalizer, Player);
                Hook.Tick();
            }

            /// <summary>Reads the soundcard mix like the audio device would. Returns the samples.</summary>
            public float[] Pump(int ms)
            {
                var all = new List<float>();
                for (int i = 0; i < ms / 20; i++)
                {
                    int n = Ssp.Read(block, 0, block.Length);
                    all.AddRange(block.Take(n));
                }
                return all.ToArray();
            }

            /// <summary>One packet every 20 ms block, as AFV delivers them, with the mix read in between.</summary>
            public float[] Transmit(string callsign, double seconds, double toneHz, params (ushort id, uint freq, float ratio)[] rx) =>
                TransmitTogether(Packets(callsign, seconds, toneHz, rx));

            /// <summary>Several stations transmitting at the same time.</summary>
            public float[] TransmitTogether(params IEnumerable<RadioRxDto>[] streams)
            {
                var all = new List<float>();
                var enumerators = streams.Select(s => s.GetEnumerator()).ToList();
                bool any = true;
                while (any)
                {
                    any = false;
                    foreach (var e in enumerators)
                    {
                        if (!e.MoveNext()) continue;
                        Ssp.AddOpusSamples(e.Current, e.Current.Transceivers);
                        any = true;
                    }
                    int n = Ssp.Read(block, 0, block.Length);
                    all.AddRange(block.Take(n));
                }
                return all.ToArray();
            }

            public void Dispose()
            {
                Hook.Dispose();
                Finalizer.Dispose();
            }
        }

        private static IEnumerable<RadioRxDto> Packets(string callsign, double seconds, double toneHz, (ushort id, uint freq, float ratio)[] rx)
        {
            var enc = OpusEncoder.Create(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            var tone = Tone(toneHz, seconds, 0.4f).Select(f => (short)(f * short.MaxValue)).ToArray();
            int count = tone.Length / 960;
            var buf = new byte[1275];
            for (int i = 0; i < count; i++)
            {
                int len = enc.Encode(tone, i * 960, 960, buf, 0, buf.Length);
                yield return new RadioRxDto
                {
                    Callsign = callsign,
                    SequenceCounter = (uint)(i + 1),
                    Audio = buf.Take(len).ToArray(),
                    LastPacket = i == count - 1,
                    Transceivers = rx.Select(r => new RxTransceiverDto { ID = r.id, Frequency = r.freq, DistanceRatio = r.ratio }).ToList(),
                };
            }
        }

        private static void EndToEnd_RealAfvPipeline()
        {
            using (var rig = new Rig((1, 125_600_000)))
            {
                Assert(rig.Hook.IsHooked && rig.Hook.TapCount == 1, "should tap the receiver");
                var sources = VatSysBridge.GetSources(VatSysBridge.GetMixer(rig.Ssp));
                Assert(sources.OfType<ReceiverTap>().Count() == 1 && !sources.OfType<ReceiverSampleProvider>().Any(), "receiver should be replaced by tap");
                Assert(sources.Contains(rig.Player), "player should be in the mix");

                rig.Pump(300);
                var heard = rig.Transmit("QFA123", 1.5, 500, (1, 125_600_000, 1f));
                rig.Pump(1000);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));

                // Same packets through an untouched AFV pipeline: what the controller hears must be identical.
                var plain = new SoundcardSampleProvider(48000, new List<ushort> { 1 }, (s, e) => { });
                plain.UpdateTransceivers(new List<TransceiverDto> { new TransceiverDto { ID = 1, Frequency = 125_600_000 } });
                var blk = new float[960];
                for (int i = 0; i < 15; i++) plain.Read(blk, 0, 960);
                var baseline = new List<float>();
                foreach (var dto in Packets("QFA123", 1.5, 500, new (ushort, uint, float)[] { (1, 125_600_000, 1f) }))
                {
                    plain.AddOpusSamples(dto, dto.Transceivers);
                    int n = plain.Read(blk, 0, 960);
                    baseline.AddRange(blk.Take(n));
                }
                double heardRms = Rms(heard), baseRms = Rms(baseline.ToArray());
                Assert(heardRms > 0.05, "live audio should reach the headset, rms " + heardRms);
                Assert(Math.Abs(heardRms - baseRms) / baseRms < 0.05, $"tap changed the live audio: rms {heardRms:0.000} vs untouched {baseRms:0.000}");
                Console.WriteLine($"     live rms {heardRms:0.000}, untouched AFV {baseRms:0.000}");
                var clips = rig.Store.Snapshot();
                Assert(clips.Count == 1, "expected 1 clip, got " + clips.Count);
                var c = clips[0];
                Assert(c.CallsignText == "QFA123", "callsign " + c.CallsignText);
                Assert(c.FrequencyHz == 125_600_000 && c.FrequencyName == "125.600", "frequency " + c.FrequencyName);
                Assert(c.Duration.TotalSeconds > 1.5 && c.Duration.TotalSeconds < 2.3, "duration " + c.Duration.TotalSeconds);
                Assert(!c.Blocked, "not blocked");

                // Replay should sound as loud as it did live (no clipping, no level change).
                double liveMid = Rms(heard.Skip(12000).Take(48000).ToArray());
                double replayMid = Rms(c.Decode().Skip(12000 + 7200).Take(48000).ToArray());
                Assert(Math.Abs(replayMid - liveMid) / liveMid < 0.15, $"replay level {replayMid:0.000} vs live {liveMid:0.000}");
                Console.WriteLine($"     clip {c.Duration.TotalSeconds:0.00}s, live level {liveMid:0.000}, replay level {replayMid:0.000}");
            }
        }

        private static void EndToEnd_TwoFrequenciesAtOnce()
        {
            using (var rig = new Rig((1, 125_600_000), (2, 118_700_000)))
            {
                rig.Pump(200);
                rig.TransmitTogether(
                    Packets("QFA1", 1.0, 400, new (ushort, uint, float)[] { (1, 125_600_000, 1f) }),
                    Packets("VOZ2", 1.4, 700, new (ushort, uint, float)[] { (2, 118_700_000, 1f) }));
                rig.Pump(1000);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));

                var clips = rig.Store.Snapshot();
                Assert(clips.Count == 2, "expected 2 separate clips, got " + clips.Count + ": "
                    + string.Join("; ", clips.Select(c => $"{c.CallsignText}@{c.FrequencyName} {c.Duration.TotalSeconds:0.00}s"))
                    + " taps recording=" + string.Join(",", VatSysBridge.GetSources(VatSysBridge.GetMixer(rig.Ssp)).OfType<ReceiverTap>().Select(t => t.IsRecording)));
                var a = clips.FirstOrDefault(c => c.CallsignText == "QFA1" && c.FrequencyHz == 125_600_000);
                var b = clips.FirstOrDefault(c => c.CallsignText == "VOZ2" && c.FrequencyHz == 118_700_000);
                Assert(a != null && b != null, "QFA1 on 125.6 and VOZ2 on 118.7");
                Assert(b.Duration > a.Duration, "each clip should have its own length");
                Assert(!a.Blocked && !b.Blocked, "different frequencies are not a block");
            }
        }

        private static void EndToEnd_DuplicateTransceiversCollapsed()
        {
            // One frequency served by two transceivers: AFV puts the audio on one and silence on the other.
            using (var rig = new Rig((1, 125_600_000), (2, 125_600_000)))
            {
                rig.Pump(200);
                rig.Transmit("JST3", 1.0, 450, (1, 125_600_000, 0.9f), (2, 125_600_000, 0.5f));
                rig.Pump(2000);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));
                var clips = rig.Store.Snapshot();
                Assert(clips.Count == 1, "expected 1 clip after de-duplication, got " + clips.Count);
                Assert(Rms(clips[0].Decode()) > 0.05, "kept clip should be the one with audio");
            }
        }

        private static void EndToEnd_ReceiverChangesAndUnhook()
        {
            using (var rig = new Rig((1, 125_600_000), (2, 118_700_000)))
            {
                Assert(rig.Hook.TapCount == 2, "2 taps");
                rig.Ssp.UpdateReceiverInputs(new List<ushort> { 1, 3 }); // AFV drops 2, adds 3
                rig.Hook.Tick();
                var sources = VatSysBridge.GetSources(VatSysBridge.GetMixer(rig.Ssp));
                Assert(rig.Hook.TapCount == 2, "taps after change: " + rig.Hook.TapCount);
                Assert(sources.OfType<ReceiverTap>().Select(t => t.Receiver.ID).OrderBy(x => x).SequenceEqual(new ushort[] { 1, 3 }), "taps should follow AFV's receivers");
                Assert(!sources.OfType<ReceiverSampleProvider>().Any(), "no untapped receivers");

                rig.Hook.Dispose();
                Assert(sources.OfType<ReceiverTap>().Count() == 0 && sources.OfType<ReceiverSampleProvider>().Count() == 2, "unhook should restore AFV's receivers");
                Assert(!sources.Contains(rig.Player), "unhook should remove the player");
            }
        }

        private static void ReplayRepeatsNewestAndPlaysIntoMix()
        {
            using (var rig = new Rig((1, 125_600_000)))
            {
                var controller = new ReplayController(rig.Store, rig.Player);
                Assert(controller.Press() == null, "nothing to play yet");

                rig.Transmit("FIRST", 0.8, 400, (1, 125_600_000, 1f));
                rig.Pump(1600);
                rig.Transmit("SECOND", 0.8, 600, (1, 125_600_000, 1f));
                rig.Pump(1600);
                rig.Finalizer.Drain(TimeSpan.FromSeconds(5));
                Assert(rig.Store.Count == 2, "2 clips, got " + rig.Store.Count);

                var c1 = controller.Press();
                Assert(c1?.CallsignText == "SECOND", "first press plays newest, got " + c1?.CallsignText);
                var mixed = rig.Pump(400);
                Assert(Rms(mixed) > 0.05, "replay should come out of the AFV mix");

                rig.Pump(200);
                var c2 = controller.Press();
                Assert(c2?.CallsignText == "SECOND", "second press repeats the newest, got " + c2?.CallsignText);
                Assert(rig.Player.Progress < 0.05, "and restarts it from the beginning, at " + rig.Player.Progress);
                var c3 = controller.Press();
                Assert(c3?.CallsignText == "SECOND", "never steps back, got " + c3?.CallsignText);

                rig.Pump(1500);
                Assert(!rig.Player.IsPlaying, "playback should finish");
                Assert(rig.Store.Count == 2, "replay itself must not be recorded as a new call");

                bool stopped = false;
                controller.Press();
                rig.Hook.RxStarted += (s, f) => { stopped = true; rig.Player.Stop(); };
                rig.Transmit("THIRD", 0.6, 500, (1, 125_600_000, 1f));
                rig.Pump(100);
                Assert(stopped && !rig.Player.IsPlaying, "new call should stop the replay");
            }
        }

        private static void ReplayKeySkipsOwnAndFollowsNewest()
        {
            var plugin = new ReplayPlugin(new ReplaySettings());
            var pilot = ToneClip(plugin, "QFA1", 1);
            plugin.Store.Add(pilot);
            var pcm = Tone(1, 300).Select(v => (short)(v / AudioFormat.StorageHeadroom * short.MaxValue)).ToArray();
            var own = new Clip(plugin.Store.NextId(), DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1), 125_600_000, "125.600",
                new[] { "You" }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length, isOwn: true);
            plugin.Store.Add(own);
            Assert(plugin.Controller.Press() == pilot, "your own reply is skipped: the key plays the pilot's call");
            Assert(plugin.Controller.Press() == pilot, "pressing again repeats it");
            var newer = ToneClip(plugin, "VOZ2", 1);
            plugin.Store.Add(newer);
            Assert(plugin.Controller.Press() == newer, "a newer call takes over");
            plugin.StopAll();
            plugin.CoordStore.Add(CoordClip(plugin, "ML-BIK_CTR", 1));
            var latestCoord = CoordClip(plugin, "BN-ISA_CTR", 1);
            plugin.CoordStore.Add(latestCoord);
            Assert(plugin.CoordController.Press() == latestCoord && plugin.CoordController.Press() == latestCoord, "coord key: newest line call, repeated");
            plugin.StopAll();
        }

        private static void SettingsRoundTripHasNoAudio()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afvreplay-test-" + Guid.NewGuid() + ".xml");
            try
            {
                var s = new ReplaySettings { RetentionMinutes = 9, VolumePercent = 150, SpeedPercent = 250, CoordRetentionMinutes = 15, ReplayBinding = "{\"InputType\":0,\"Key\":66}" };
                s.Save(path);
                var loaded = ReplaySettings.Load(path);
                Assert(loaded.RetentionMinutes == 5, "retention capped on save/load");
                Assert(loaded.SpeedPercent == 200, "speed capped at 2x, got " + loaded.SpeedPercent);
                Assert(loaded.CoordRetentionMinutes == 10, "coord retention capped at 10, got " + loaded.CoordRetentionMinutes);
                Assert(loaded.VolumePercent == 150 && loaded.ReplayBinding == s.ReplayBinding, "round trip");
                Assert(new System.IO.FileInfo(path).Length < 2000, "settings file should be tiny");
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static Clip MakeClip(ClipStore store, DateTime end)
        {
            var pcm = Tone(300, 0.5, 0.3f).Select(f => (short)(f * short.MaxValue)).ToArray();
            return new Clip(store.NextId(), end.AddSeconds(-0.5), end, 1, "x", new[] { "T" }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
        }

        private static void Feed(ClipRecorder rec, float[] block, int blocks)
        {
            for (int i = 0; i < blocks; i++) rec.Process(block, 0, block.Length);
        }

        private static void Fill(float[] a, float v) { for (int i = 0; i < a.Length; i++) a[i] = (i % 2 == 0 ? v : -v); }

        private static float[] Tone(double hz, double seconds, float amp)
        {
            var a = new float[(int)(48000 * seconds)];
            for (int i = 0; i < a.Length; i++) a[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / 48000));
            return a;
        }

        private static float[] Tone(double seconds, double hz, double amp = 0.3)
        {
            var a = new float[(int)(AudioFormat.SampleRate * seconds)];
            for (int i = 0; i < a.Length; i++) a[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / AudioFormat.SampleRate));
            return a;
        }

        private static double ZeroCrossingHz(float[] a)
        {
            int n = 0;
            for (int i = 1; i < a.Length; i++) if ((a[i - 1] < 0) != (a[i] < 0)) n++;
            return n / 2.0 / (a.Length / (double)AudioFormat.SampleRate);
        }

        private static void TimeStretchKeepsPitch()
        {
            var input = Tone(3, 220);
            Assert(ReferenceEquals(TimeStretch.Apply(input, 1.0), input), "1x should pass through untouched");
            foreach (var speed in new[] { 1.25, 1.5, 2.0 })
            {
                var output = TimeStretch.Apply(input, speed);
                double expected = input.Length / speed;
                Assert(Math.Abs(output.Length - expected) / expected < 0.01, $"{speed}x length {output.Length} vs {expected:0}");
                // Skip the fade-in of the first window when measuring.
                var body = output.Skip(960).Take(output.Length - 1920).ToArray();
                double hz = ZeroCrossingHz(body);
                Assert(Math.Abs(hz - 220) < 220 * 0.03, $"{speed}x pitch moved to {hz:0.0} Hz");
                double rms = Rms(body), inRms = Rms(input);
                Assert(Math.Abs(rms - inRms) / inRms < 0.1, $"{speed}x level {rms:0.000} vs {inRms:0.000}");
            }
        }

        private static Clip ToneClip(ReplayPlugin plugin, string callsign, double seconds)
        {
            var pcm = Tone(seconds, 300).Select(v => (short)(v / AudioFormat.StorageHeadroom * short.MaxValue)).ToArray();
            var end = DateTime.UtcNow;
            return new Clip(plugin.Store.NextId(), end.AddSeconds(-seconds), end, 125_600_000, "125.600",
                new[] { callsign }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
        }

        private static int PlayToEnd(ReplayPlayer player)
        {
            var buf = new float[960];
            int samples = 0;
            for (int i = 0; i < 100000 && player.IsPlaying; i++) { player.Read(buf, 0, buf.Length); samples += buf.Length; }
            return samples;
        }

        private static void ReplaySpeedShortensPlayback()
        {
            var plugin = new ReplayPlugin(new ReplaySettings { SpeedPercent = 150 });
            var clip = ToneClip(plugin, "QFA1", 3);
            plugin.Store.Add(clip);
            int started = 0;
            plugin.Player.PlaybackStarted += (s, e) => started++;
            plugin.Controller.Press();
            Assert(started == 1 && plugin.Player.PlayingClip == clip, "should report the clip playing");
            double secs = PlayToEnd(plugin.Player) / (double)AudioFormat.SampleRate;
            Assert(Math.Abs(secs - 2.0) < 0.1, "3 s at 1.5x should take 2 s, took " + secs);
            Assert(plugin.Player.PlayingClip == null, "nothing playing afterwards");

            plugin.Settings.SpeedPercent = 100;
            plugin.ApplySettings(save: false);
            plugin.Controller.Play(clip);
            secs = PlayToEnd(plugin.Player) / (double)AudioFormat.SampleRate;
            Assert(Math.Abs(secs - 3.0) < 0.1, "1x should take 3 s, took " + secs);
        }

        private static void VscsButtonShowsReplay()
        {
            // The VSCS grid: 4 rows at default height, a fifth (Say Again under Group) when taller.
            Assert(AFVReplay.UI.VscsLayout.Rows(262) == 4 && AFVReplay.UI.VscsLayout.Rows(328) == 5, "rows");
            Assert(AFVReplay.UI.VscsLayout.MenuColumn(1, 262) == 1, "menu column with 1 frequency");
            Assert(AFVReplay.UI.VscsLayout.MenuColumn(5, 262) == 2, "5 frequencies take two columns at default height");
            Assert(AFVReplay.UI.VscsLayout.MenuColumn(5, 328) == 1, "5 frequencies fit one column when taller");
            Assert(AFVReplay.UI.VscsLayout.Cell(1, 4) == new System.Drawing.Rectangle(89, 267, 80, 60), "Say Again under Group " + AFVReplay.UI.VscsLayout.Cell(1, 4));
            Assert(AFVReplay.UI.VscsInjector.PanelHeightNeeded == 330 && AFVReplay.UI.VscsLayout.Rows(AFVReplay.UI.VscsInjector.PanelHeightNeeded) == 5,
                   "minimum panel height fits Say Again under Group: " + AFVReplay.UI.VscsInjector.PanelHeightNeeded);

            var plugin = new ReplayPlugin(new ReplaySettings());
            var end = DateTime.UtcNow;
            var pcm = new short[48000];
            var del = new Clip(plugin.Store.NextId(), end.AddSeconds(-1), end, 118_500_000, "Gingin T 118.500",
                new[] { "GIG_DEL" }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
            Assert(AFVReplay.UI.SayAgainPage.Who(del) == "GIG_DEL" && AFVReplay.UI.SayAgainPage.Where(del) == "118.500", "frequency who/where");
            var coord = CoordClip(plugin, "GIG_GND", 1);
            Assert(AFVReplay.UI.SayAgainPage.Where(coord) == "Coord", "coord where");
            Assert(AFVReplay.UI.SayAgainPage.Ago(TimeSpan.FromSeconds(40)) == "<1m" && AFVReplay.UI.SayAgainPage.Ago(TimeSpan.FromSeconds(130)) == "2m", "ago in minutes");
        }

        /// <summary>The Say Again page plays frequency and coord calls in table order (newest first).</summary>
        private static void SayAgainPlaysInTableOrder()
        {
            var plugin = new ReplayPlugin(new ReplaySettings());
            var now = DateTime.UtcNow;
            var pcm = new short[48000 * 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(3000 * Math.Sin(i * 0.05));
            Clip F(string who, double ago) => new Clip(plugin.Store.NextId(), now.AddSeconds(-ago - 2), now.AddSeconds(-ago), 118_500_000, "Gingin T 118.500",
                new[] { who }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
            Clip C(string who, double ago) => new Clip(plugin.CoordStore.NextId(), now.AddSeconds(-ago - 2), now.AddSeconds(-ago), 0, "Coord",
                new[] { who }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
            var f1 = F("QFA123", 10); var c1 = C("GIG_GND", 20); var f2 = F("VOZ456", 30); var c2 = C("GIG_GND", 40);
            plugin.Store.Add(f1); plugin.Store.Add(f2);
            plugin.CoordStore.Add(c1); plugin.CoordStore.Add(c2);

            var list = new SayAgainPlaylist(plugin);
            Assert(list.Items().SequenceEqual(new[] { f1, c1, f2, c2 }), "table order: newest first, frequency and coord together");

            list.PlayPause(null);
            Assert(list.Current == f1 && plugin.Player.IsPlaying, "Play with nothing selected plays the newest");
            list.Older(null);
            Assert(list.Current == c1 && plugin.CoordPlayer.IsPlaying && !plugin.Player.IsPlaying, "Older crosses into coord");
            list.Older(null);
            Assert(list.Current == f2 && plugin.Player.IsPlaying && !plugin.CoordPlayer.IsPlaying, "and back to frequency");
            list.Older(null); list.Older(null);
            Assert(list.Current == c2, "stays on the oldest");
            list.Newer(null);
            Assert(list.Current == f2, "Newer steps up the table");

            list.PlayPause(null);
            Assert(list.IsPaused && !list.IsPlaying && list.Current == f2, "Play/Pause pauses");
            var buf = new float[960];
            plugin.Player.Read(buf, 0, buf.Length);
            Assert(buf.All(v => v == 0), "paused output is silent");
            list.PlayPause(null);
            Assert(list.IsPlaying && list.Current == f2, "and carries on");
            var second = new float[48000];
            plugin.Player.Read(second, 0, second.Length);
            Assert(Math.Abs(plugin.Player.Progress - 0.5) < 0.02, "progress halfway through a 2 s call, got " + plugin.Player.Progress);
            plugin.Player.Read(buf, 0, buf.Length);
            Assert(buf.Any(v => v != 0), "playing again after resume");

            list.PlayPause(c1);
            Assert(list.Current == c1 && plugin.CoordPlayer.IsPlaying, "picking another row while playing plays that row");
            list.Stop();
            Assert(!plugin.Player.IsPlaying && !plugin.CoordPlayer.IsPlaying, "stop");

            Assert(list.Current == c1, "remembers where it was, so Older/Newer carry on from there");

            // Selected aircraft: only its calls are listed and stepped through.
            var f3 = F("QFA123", 50); plugin.Store.Add(f3);
            list.Filter = c => c.Callsigns.Contains("QFA123");
            Assert(list.Items().SequenceEqual(new[] { f1, f3 }), "filtered to QFA123's calls, newest first");
            list.Stop(); list.Older(null);
            Assert(list.Current == f1 || list.Current == f3, "stepping stays within the filter");
            list.Older(f1);
            Assert(list.Current == f3, "Previous goes to the aircraft's older call, got " + list.Current?.CallsignText);
            list.Filter = null;
            Assert(list.Items().Count == 5, "filter cleared: all calls back");
            list.Stop();
            Assert(AFVReplay.UI.SayAgainPage.AgoLong(TimeSpan.FromSeconds(20)) == "<1 min ago"
                && AFVReplay.UI.SayAgainPage.AgoLong(TimeSpan.FromSeconds(70)) == "1 min ago"
                && AFVReplay.UI.SayAgainPage.AgoLong(TimeSpan.FromSeconds(200)) == "3 mins ago", "Last Tx ago wording");
        }

        private static void RecordsAtEightKilohertz()
        {
            // Speech band passes; above 4 kHz is gone; about 1.5 KB per second.
            var voice = Tone(seconds: 2, hz: 1000, amp: 0.3).Select(v => (short)(v * short.MaxValue)).ToArray();
            var frames = OpusCodec.Encode(voice, voice.Length);
            int bytes = frames.Sum(f => f.Length);
            var back = OpusCodec.Decode(frames, voice.Length);
            Assert(back.Length == voice.Length, "length kept at 48 kHz: " + back.Length);
            double level = Rms(back.Skip(4800).Take(48000).ToArray());
            Assert(Math.Abs(level - 0.212) < 0.04, "1 kHz level " + level);
            Assert(bytes / 2.0 < 2000, "should be about 1.5 KB/s, got " + bytes / 2 + " B/s");

            var hiss = Tone(seconds: 1, hz: 6000, amp: 0.3).Select(v => (short)(v * short.MaxValue)).ToArray();
            double hissBack = Rms(OpusCodec.Decode(OpusCodec.Encode(hiss, hiss.Length), hiss.Length).Skip(4800).ToArray());
            Assert(hissBack < 0.02, "6 kHz should not survive 8 kHz recording, rms " + hissBack);
            Console.WriteLine($"     8 kHz: {bytes / 2} B/s, 1 kHz level {level:0.000}, 6 kHz left {hissBack:0.0000}");

            // Rate change both ways is lined up in time (no delay).
            var click = new short[48000];
            click[24000] = 20000;
            var up = OpusCodec.Up(OpusCodec.Down(click, click.Length).Select(v => (float)v).ToArray(), 8000, 48000);
            int peak = Array.IndexOf(up, up.Max());
            Assert(Math.Abs(peak - 24000) <= 6, "resampling shifted audio to " + peak);
        }

        private static void CoordLevelsQuietMic()
        {
            RawRecording Utter(double amp) => new RawRecording
            {
                Pcm = Tone(seconds: 1, hz: 300, amp: amp).Select(v => (short)(v / AudioFormat.StorageHeadroom * short.MaxValue)).ToArray(),
                Length = 48000,
            };
            // A quiet raw mic (vatSys applies no gain on lines) is brought up to normal speech level.
            double quiet = CoordConversation.BoostFor(Utter(0.03 * Math.Sqrt(2)));
            Assert(Math.Abs(quiet - 0.1 / 0.03) < 0.2, "quiet mic boost " + quiet);
            Assert(CoordConversation.BoostFor(Utter(0.3)) == 1, "loud speech is never cut");
            Assert(CoordConversation.BoostFor(Utter(0.001)) == CoordConversation.MaxBoost, "boost is capped");

            // Own transmissions get vatSys's AFV mic gain, as AFV applies it before sending.
            var done = new List<RawRecording>();
            bool ptt = true;
            var line = new LineStream();
            var rec = new OwnTxRecorder(done.Add, () => ptt, () => (118_500_000u, "GIG_TWR"), () => line.Now, gainDb: () => 6f);
            foreach (var p in line.Packets(1.0, 0.1)) rec.Feed(p, 0, p.Length);
            ptt = false;
            foreach (var p in line.Packets(0.1, 0)) rec.Feed(p, 0, p.Length);
            double level = 0;
            { double sum = 0; for (int i = 4800; i < 43200; i++) { double v = done[0].Pcm[i] / 32768.0 * AudioFormat.StorageHeadroom; sum += v * v; } level = Math.Sqrt(sum / 38400); }
            Assert(Math.Abs(level - 0.0707 * 1.995) < 0.01, "+6 dB mic gain applied, level " + level);
        }

        // ---------------------------------------------------------------- coord (hotline/coldline)

        private static Clip CoordClip(ReplayPlugin plugin, string callsign, double seconds)
        {
            var pcm = Tone(seconds, 300).Select(v => (short)(v / AudioFormat.StorageHeadroom * short.MaxValue)).ToArray();
            var end = DateTime.UtcNow;
            return new Clip(plugin.CoordStore.NextId(), end.AddSeconds(-seconds), end, 0, "Coord",
                new[] { callsign }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
        }

        /// <summary>A continuous open-line stream, as vatSys sends it: 20 ms PCM16 packets.</summary>
        private sealed class LineStream
        {
            private readonly Random rng = new Random(7);
            private long sample;
            public DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            /// <summary>Background noise at about -54 dBFS, plus optional speech-like tone.</summary>
            public IEnumerable<byte[]> Packets(double seconds, double toneAmp, double toneHz = 220, double noise = 0.002)
            {
                int n = (int)(seconds * 50);
                for (int p = 0; p < n; p++)
                {
                    var bytes = new byte[960 * 2];
                    for (int i = 0; i < 960; i++, sample++)
                    {
                        double v = (rng.NextDouble() * 2 - 1) * noise * 1.7
                                 + toneAmp * Math.Sin(2 * Math.PI * toneHz * sample / 48000.0);
                        short x = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v * 32767));
                        bytes[2 * i] = (byte)x;
                        bytes[2 * i + 1] = (byte)(x >> 8);
                    }
                    Now = Now.AddMilliseconds(20);
                    yield return bytes;
                }
            }
        }

        private static (CoordRecorder rec, List<RawRecording> done, List<string> starts, LineStream line) CoordRig()
        {
            var done = new List<RawRecording>();
            var starts = new List<string>();
            var line = new LineStream();
            var rec = new CoordRecorder(done.Add, starts.Add, () => line.Now);
            return (rec, done, starts, line);
        }

        private static void CoordRecorderCutsUtterances()
        {
            var (rec, done, starts, line) = CoordRig();
            void Send(double secs, double amp) { foreach (var p in line.Packets(secs, amp)) rec.Feed("ML-BIK_CTR", p, p.Length); }
            Send(1.0, 0);      // open line, nobody talking
            var speechStart = line.Now;
            Send(1.5, 0.2);    // "Bik, Sydney, request..."
            Send(2.0, 0);      // pause
            Send(1.0, 0.2);    // reply
            Send(2.0, 0);
            Assert(done.Count == 2, "expected 2 utterances, got " + done.Count);
            Assert(starts.Count == 2 && starts.All(u => u == "ML-BIK_CTR"), "speech starts " + starts.Count);
            var a = done[0];
            double secs = a.Length / 48000.0;
            Assert(a.Callsigns.Single() == "ML-BIK_CTR" && a.FrequencyHz == 0, "labelled with the controller");
            Assert(secs > 1.6 && secs < 2.3, "first utterance length " + secs);
            Assert(a.StartUtc <= speechStart && a.StartUtc > speechStart.AddMilliseconds(-400), "should start just before the speech (pre-roll)");
            double secs2 = done[1].Length / 48000.0;
            Assert(secs2 > 1.1 && secs2 < 1.8, "second utterance length " + secs2);
            Console.WriteLine($"     utterances {secs:0.00}s and {secs2:0.00}s");
        }

        private static void CoordRecorderIgnoresNoiseAndLoopback()
        {
            var (rec, done, starts, line) = CoordRig();
            // Mic hiss, then a faint copy of the other controller's frequency audio (vatSys mixes it
            // in at 1.4%), then a louder but steady hum: none of it is someone talking to you.
            foreach (var p in line.Packets(3, 0.0)) rec.Feed("SY_TWR", p, p.Length);
            foreach (var p in line.Packets(3, 0.006, 1000)) rec.Feed("SY_TWR", p, p.Length);
            foreach (var p in line.Packets(1, 0.0)) rec.Feed("SY_TWR", p, p.Length);
            foreach (var p in line.Packets(0.1, 0.2)) rec.Feed("SY_TWR", p, p.Length); // a click / blip
            foreach (var p in line.Packets(2, 0.0)) rec.Feed("SY_TWR", p, p.Length);
            Assert(done.Count == 0, "noise, loopback and blips should not make clips, got " + done.Count);
        }

        private static void CoordRecorderEndsWhenLineCloses()
        {
            var (rec, done, starts, line) = CoordRig();
            foreach (var p in line.Packets(0.5, 0)) rec.Feed("ML-BIK_CTR", p, p.Length);
            foreach (var p in line.Packets(1.2, 0.2)) rec.Feed("ML-BIK_CTR", p, p.Length);
            Assert(rec.IsRecording("ML-BIK_CTR") && done.Count == 0, "mid-utterance");
            // Line hung up mid-sentence: packets stop.
            line.Now = line.Now.AddMilliseconds(200);
            rec.Tick();
            Assert(done.Count == 0, "too early to close");
            line.Now = line.Now.AddMilliseconds(400);
            rec.Tick();
            Assert(done.Count == 1, "should close when the stream stops, got " + done.Count);
            Assert(!rec.IsRecording("ML-BIK_CTR"), "stream forgotten");
        }

        private static void CoordStoreKeepsTenMinutes()
        {
            var plugin = new ReplayPlugin(new ReplaySettings { RetentionMinutes = 5, CoordRetentionMinutes = 10 });
            Assert(plugin.CoordStore.Retention == TimeSpan.FromMinutes(10), "coord retention " + plugin.CoordStore.Retention);
            plugin.CoordStore.Retention = TimeSpan.FromMinutes(30);
            Assert(plugin.CoordStore.Retention == ClipStore.CoordMaxRetention, "coord must be capped at 10 min");
            Assert(plugin.Store.Retention == TimeSpan.FromMinutes(5), "frequency still 5 min");

            var now = DateTime.UtcNow;
            Clip At(double minutesAgo) => new Clip(plugin.CoordStore.NextId(), now.AddMinutes(-minutesAgo).AddSeconds(-2), now.AddMinutes(-minutesAgo),
                0, "Coord", new[] { "X" }, false, OpusCodec.Encode(new short[960], 960), 960);
            var eight = At(8);
            var eleven = At(11);
            plugin.CoordStore.Add(eight);
            plugin.CoordStore.Add(eleven);
            plugin.CoordStore.Prune(now);
            Assert(plugin.CoordStore.Count == 1 && plugin.CoordStore.Snapshot()[0] == eight, "8 min kept, 11 min wiped");
            Assert(eleven.IsWiped, "expired coord clip wiped");
        }

        private static void OnlyOneReplayAtATime()
        {
            var plugin = new ReplayPlugin(new ReplaySettings());
            plugin.Store.Add(ToneClip(plugin, "QFA1", 2));
            plugin.CoordStore.Add(CoordClip(plugin, "ML-BIK_CTR", 2));
            plugin.Controller.Press();
            Assert(plugin.Player.IsPlaying, "frequency replay playing");
            plugin.CoordController.Press();
            Assert(plugin.CoordPlayer.IsPlaying && !plugin.Player.IsPlaying, "coord replay should stop the frequency replay");
            plugin.Controller.Press();
            Assert(plugin.Player.IsPlaying && !plugin.CoordPlayer.IsPlaying, "and the other way round");
            plugin.StopAll();
            Assert(!plugin.Player.IsPlaying && !plugin.CoordPlayer.IsPlaying, "stop all");
        }

        /// <summary>
        /// Uses vatSys's real Mumble client objects (no network): our handler on its AudioReceived
        /// event, our player in its output mixer, and audio out of its real output provider.
        /// </summary>
        private static void EndToEnd_RealMumbleCoord()
        {
            var asm = typeof(vatsys.AFV).Assembly;
            var mumbleType = asm.GetType("vatsys.Mumble");
            var instanceField = mumbleType.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(instanceField.GetValue(null) == null, "no Mumble instance before the test");
            var mumble = Activator.CreateInstance(mumbleType, true);
            instanceField.SetValue(null, mumble);
            try
            {
                var plugin = new ReplayPlugin(new ReplaySettings());
                var finalizer = new ClipFinalizer(plugin.CoordStore, _ => "Coord");
                int starts = 0;
                var recorder = new CoordRecorder(finalizer.Enqueue, _ => starts++);
                using (var hook = new CoordHook(VatSysBridge.GetMumble, recorder, plugin.CoordPlayer, plugin.CoordStore))
                {
                    hook.Tick();
                    Assert(hook.IsHooked, "should hook vatSys's Mumble client");
                    var mixer = VatSysBridge.GetMumbleMixer(mumble);
                    Assert(mixer.MixerInputs.Contains(plugin.CoordPlayer), "coord player should be in vatSys's coord mixer");

                    var protocol = VatSysBridge.GetMumbleProtocol(mumble);
                    // (Its constructor needs a live server connection; the handler only reads Name.)
                    var user = (MumbleSharp.Model.User)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(MumbleSharp.Model.User));
                    user.Name = "ML-BIK_CTR";
                    var line = new LineStream();
                    foreach (var p in line.Packets(0.6, 0)) VatSysBridge.RaiseCoordAudio(protocol, user, p);
                    foreach (var p in line.Packets(1.5, 0.2)) VatSysBridge.RaiseCoordAudio(protocol, user, p);
                    foreach (var p in line.Packets(1.6, 0)) VatSysBridge.RaiseCoordAudio(protocol, user, p);
                    finalizer.Drain(TimeSpan.FromSeconds(5));

                    var clips = plugin.CoordStore.Snapshot();
                    Assert(clips.Count == 1, "expected 1 coord clip, got " + clips.Count);
                    var c = clips[0];
                    Assert(c.IsCoord && c.CallsignText == "ML-BIK_CTR" && c.FrequencyName == "Coord", "clip " + c.FrequencyName + " " + c.CallsignText);
                    Assert(starts == 1, "speech start events " + starts);

                    // Replay comes out of vatSys's coord output (16-bit, after the coord volume).
                    var output = (IWaveProvider)mumbleType.GetField("outputProvider", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mumble);
                    var buf = new byte[960 * 2 * 25]; // 0.5 s
                    double OutRms()
                    {
                        double sum = 0;
                        for (int i = 0; i < buf.Length / 2; i++) { double v = (short)(buf[2 * i] | (buf[2 * i + 1] << 8)) / 32768.0; sum += v * v; }
                        return Math.Sqrt(sum / (buf.Length / 2));
                    }

                    // Live: vatSys's own handler on the same event already queued the packets above for
                    // its coord output (0.6 s open line, 1.5 s speech, 1.6 s pause). Measure inside the speech.
                    output.Read(buf, 0, buf.Length);
                    output.Read(buf, 0, buf.Length);  // 1.0 s in
                    output.Read(buf, 0, buf.Length);  // 1.0-1.5 s: speech
                    double live = OutRms();
                    for (int i = 0; i < 6; i++) output.Read(buf, 0, buf.Length); // drain the rest

                    plugin.CoordController.Press();
                    output.Read(buf, 0, buf.Length);
                    output.Read(buf, 0, buf.Length);  // past the pre-roll, into the speech
                    double replay = OutRms();
                    Console.WriteLine($"     coord clip {c.Duration.TotalSeconds:0.00}s, live level {live:0.000}, replay level {replay:0.000}");
                    Assert(live > 0.05, "live coord should reach the output, rms " + live);
                    Assert(Math.Abs(replay - live) / live < 0.15, $"coord replay should match live: {replay:0.000} vs {live:0.000}");

                    hook.Unhook();
                    Assert(!hook.IsHooked && !mixer.MixerInputs.Contains(plugin.CoordPlayer), "unhook should remove the player");
                    int before = plugin.CoordStore.Count;
                    foreach (var p in line.Packets(1.0, 0.2)) VatSysBridge.RaiseCoordAudio(protocol, user, p);
                    Assert(!recorder.IsRecording("ML-BIK_CTR"), "no recording after unhook");
                }
                finalizer.Dispose();
            }
            finally
            {
                instanceField.SetValue(null, null);
            }
        }

        // ---------------------------------------------------------------- your own voice

        private static void OwnTxRecordsWhilePttHeld()
        {
            var done = new List<RawRecording>();
            bool ptt = false;
            var line = new LineStream();
            var rec = new OwnTxRecorder(done.Add, () => ptt, () => (124_400_000u, "SY_TWR"), () => line.Now);
            void Mic(double secs, double amp) { foreach (var p in line.Packets(secs, amp)) rec.Feed(p, 0, p.Length); }

            Mic(0.5, 0.2);                 // talking on coord / to yourself, PTT up: not a transmission
            ptt = true; Mic(1.5, 0.2);     // "Qantas 1, climb flight level 240"
            ptt = false; Mic(0.5, 0.0);
            Assert(done.Count == 1, "expected 1 transmission, got " + done.Count);
            var r = done[0];
            double secs = r.Length / 48000.0;
            Assert(r.IsOwn && r.FrequencyHz == 124_400_000 && r.Callsigns.Single() == "SY_TWR", "own clip on the tx frequency");
            Assert(Math.Abs(secs - 1.5) < 0.05, "transmission length " + secs);

            ptt = true; Mic(0.1, 0.2); ptt = false; Mic(0.1, 0);
            Assert(done.Count == 1, "a PTT blip should be dropped");

            // Observer (no frequency set to transmit): PTT does nothing, so nothing is recorded.
            var observer = new OwnTxRecorder(done.Add, () => ptt, () => (0u, "ML_OBS"), () => line.Now);
            int before = done.Count;
            ptt = true; foreach (var p in line.Packets(1.5, 0.2)) observer.Feed(p, 0, p.Length);
            Assert(!observer.IsRecording, "observer PTT should not record");
            ptt = false; foreach (var p in line.Packets(0.3, 0)) observer.Feed(p, 0, p.Length);
            Assert(done.Count == before, "no clip from PTT that transmits nothing");

            // Mic stops while PTT is still held (device lost): closes on Tick.
            ptt = true; Mic(1.0, 0.2);
            line.Now = line.Now.AddMilliseconds(500);
            rec.Tick();
            Assert(done.Count == 2 && !rec.IsRecording, "should close when the mic stops");
        }

        private static Clip FreqClip(string callsign, DateTime start, double secs, bool own = false) =>
            new Clip(1, start, start.AddSeconds(secs), 124_400_000, "124.400", new[] { callsign }, false,
                     OpusCodec.Encode(new short[960], 960), 960, own);

        private static RawRecording CoordRec(string speaker, DateTime start, double secs, bool own = false) =>
            new RawRecording { Pcm = new short[960], Length = 960, StartUtc = start, EndUtc = start.AddSeconds(secs),
                               FrequencyHz = 0, Callsigns = new List<string> { speaker }, IsOwn = own };

        private static void CoordBleedDropsDuplicates()
        {
            var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var bikTx = FreqClip("ML-BIK_CTR", t, 3);

            // Bik keys PTT with our line open: the line copy is a duplicate (loud, same speaker).
            Assert(CoordBleed.IsDuplicate(CoordRec("ML-BIK_CTR", t.AddSeconds(-0.3), 3.6), 0.12, new[] { bikTx }),
                   "same speaker on frequency and line at once should be dropped from coord");
            // Bik talks to us on the line while a pilot calls on frequency: both real.
            var pilot = FreqClip("QFA1", t, 3);
            Assert(!CoordBleed.IsDuplicate(CoordRec("ML-BIK_CTR", t, 3), 0.12, new[] { pilot }),
                   "real coord speech during frequency traffic must be kept");
            // Pilot audio leaking faintly through Bik's line (vatSys mixes it in at 1.4%): dropped.
            Assert(CoordBleed.IsDuplicate(CoordRec("ML-BIK_CTR", t.AddSeconds(0.2), 2.5), 0.01, new[] { pilot }),
                   "quiet coord audio matching frequency traffic is loopback");
            // Quiet but on its own (soft-spoken controller, nothing on frequency): kept.
            Assert(!CoordBleed.IsDuplicate(CoordRec("ML-BIK_CTR", t.AddSeconds(10), 2), 0.01, new[] { pilot }),
                   "quiet coord with no frequency traffic must be kept");

            // You keying PTT with a line open: your line copy is a duplicate of your transmission.
            var myTx = FreqClip("SY_TWR", t.AddSeconds(20), 2, own: true);
            Assert(CoordBleed.IsDuplicate(CoordRec("SY_TWR", t.AddSeconds(19.8), 2.4, own: true), 0.12, new[] { myTx }),
                   "your own line copy of a PTT transmission should be dropped");
            var now = DateTime.UtcNow;
            var ownClip = FreqClip("SY_TWR", now, 2, own: true);
            Assert(ownClip.CallsignText == "You", "own clips show as You, got " + ownClip.CallsignText);
        }

        /// <summary>
        /// Uses vatSys's real InputMix objects (its AFV mic and coord mic mixes) with our handlers on them,
        /// and the plugin's real finalizers and stores.
        /// </summary>
        private static void EndToEnd_RealVatSysMicInputs()
        {
            var plugin = new ReplayPlugin(new ReplaySettings());
            var freqFinalizer = new ClipFinalizer(plugin.Store, hz => "SY_TWR 124.400");
            var coordFinalizer = new ClipFinalizer(plugin.CoordStore, _ => "Coord");
            bool ptt = false, lineOpen = false;
            var line = new LineStream(); // simulated time, advancing 20 ms per packet
            var ownTx = new OwnTxRecorder(freqFinalizer.Enqueue, () => ptt, () => (124_400_000u, "SY_TWR"), () => line.Now);
            var conversation = new CoordConversation(coordFinalizer.Enqueue, () => plugin.Store.Snapshot(), () => line.Now);
            var coordRec = new CoordRecorder(conversation.Add, null, () => line.Now);

            var afvMic = VatSysBridge.CreateInputMix();
            var coordMic = VatSysBridge.CreateInputMix();
            var fakeMumble = new object();
            using (var hook = new MicHook(() => afvMic, () => fakeMumble, ownTx, coordRec, () => "SY_TWR",
                                          m => lineOpen && ReferenceEquals(m, fakeMumble)))
            {
                // MicHook asks the bridge for the Mumble client's input; give it ours via a stand-in getter.
                typeof(MicHook).GetField("getMumble", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hook, (Func<object>)(() => null));
                hook.Tick();
                Assert(hook.AfvHooked, "should hook vatSys's AFV mic");
                // Coord mic: attach directly to the real InputMix (the Mumble client isn't running offline).
                var coordSub = VatSysBridge.SubscribeInput(coordMic, (b, o, l) => { if (lineOpen) coordRec.Feed("SY_TWR", b, o, l, true); });

                void Speak(double secs, double amp)
                {
                    foreach (var p in line.Packets(secs, amp))
                    {
                        VatSysBridge.RaiseInput(afvMic, p);   // vatSys captures both all the time
                        VatSysBridge.RaiseInput(coordMic, p);
                    }
                }

                Speak(1.0, 0.2);                       // talking, no PTT, no line: nothing kept
                ptt = true; Speak(1.5, 0.2); ptt = false; Speak(0.4, 0);
                freqFinalizer.Drain(TimeSpan.FromSeconds(5));
                var tx = plugin.Store.Snapshot();
                Assert(tx.Count == 1 && tx[0].IsOwn && tx[0].CallsignText == "You", "one own transmission, got " + tx.Count);
                double level = Rms(tx[0].Decode().Skip(4800).Take(48000).ToArray());
                Console.WriteLine($"     own tx {tx[0].Duration.TotalSeconds:0.00}s, level {level:0.000} (spoken 0.141)");
                Assert(Math.Abs(level - 0.141) < 0.03, "own transmission level " + level);
                Assert(plugin.CoordStore.Count == 0, "no coord without an open line");

                // Line open: your side is recorded, cut into utterances.
                lineOpen = true;
                Speak(0.6, 0); Speak(1.4, 0.2); Speak(5.0, 0);
                conversation.Tick();
                coordFinalizer.Drain(TimeSpan.FromSeconds(5));
                var mine = plugin.CoordStore.Snapshot();
                Assert(mine.Count == 1 && mine[0].IsOwn && mine[0].IsCoord, "your side of the line, got " + mine.Count);

                // PTT with the line open: kept once, as the frequency transmission.
                ptt = true; Speak(1.5, 0.2); ptt = false; Speak(1.6, 0);
                freqFinalizer.Drain(TimeSpan.FromSeconds(5));
                Speak(5.0, 0);
                conversation.Tick();
                coordFinalizer.Drain(TimeSpan.FromSeconds(5));
                Assert(conversation.Counts.dropped == 1, "the line copy should be counted as a duplicate, counts " + conversation.Counts);
                Assert(plugin.Store.Count == 2, "second transmission recorded, got " + plugin.Store.Count);
                Assert(plugin.CoordStore.Count == 1, "the line copy of the transmission should be dropped, coord has " + plugin.CoordStore.Count);

                hook.Unhook();
                VatSysBridge.UnsubscribeInput(coordMic, coordSub);
                Assert(!hook.AfvHooked, "unhooked");
                int before = plugin.Store.Count;
                ptt = true; Speak(1.0, 0.2); ptt = false; Speak(0.4, 0);
                freqFinalizer.Drain(TimeSpan.FromSeconds(2));
                Assert(plugin.Store.Count == before, "nothing recorded after unhook");
            }
            freqFinalizer.Dispose();
            coordFinalizer.Dispose();
        }

        /// <summary>Both sides of a line call end up in one clip, mixed as you heard them.</summary>
        private static void ConversationMixesBothSides()
        {
            var done = new List<RawRecording>();
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var now = t0;
            var conv = new CoordConversation(done.Add, () => Array.Empty<Clip>(), () => now);

            RawRecording Utter(string who, double at, double secs, double hz, bool own) =>
                new RawRecording
                {
                    Pcm = Tone(secs, hz, 0.2).Select(v => (short)(v / AudioFormat.StorageHeadroom * short.MaxValue)).ToArray(),
                    Length = (int)(48000 * secs), StartUtc = t0.AddSeconds(at), EndUtc = t0.AddSeconds(at + secs),
                    FrequencyHz = 0, Callsigns = new List<string> { who }, IsOwn = own,
                };

            conv.Add(Utter("ML-BIK_CTR", 0.0, 1.5, 300, false));  // "Sydney, Bik, request higher for QFA1"
            conv.Add(Utter("SY_TWR", 2.5, 1.0, 500, true));        // "Approved"
            conv.Add(Utter("ML-BIK_CTR", 4.5, 0.8, 300, false));  // "Thanks"
            now = t0.AddSeconds(6);
            conv.Tick();
            Assert(done.Count == 0, "still within the gap: conversation open");
            now = t0.AddSeconds(10);
            conv.Tick();
            Assert(done.Count == 1, "conversation should close after the gap, got " + done.Count);

            var c = done[0];
            Assert(c.Callsigns.SequenceEqual(new[] { "ML-BIK_CTR", "You" }) && !c.IsOwn, "speakers " + string.Join(",", c.Callsigns));
            double secs = c.Length / 48000.0;
            Assert(Math.Abs(secs - 5.3) < 0.05, "conversation spans first word to last, got " + secs);
            double Seg(double a, double b)
            {
                double sum = 0; int n0 = (int)(a * 48000), n1 = (int)(b * 48000);
                for (int i = n0; i < n1; i++) { double v = c.Pcm[i] / 32768.0 * AudioFormat.StorageHeadroom; sum += v * v; }
                return Math.Sqrt(sum / (n1 - n0));
            }
            Assert(Seg(0.2, 1.3) > 0.1 && Seg(2.6, 3.4) > 0.1 && Seg(4.6, 5.2) > 0.1, "both sides audible in the mix");
            Assert(Seg(1.7, 2.3) < 0.01, "silence between turns preserved");

            // A new call later is a new conversation; you alone shows as yours.
            conv.Add(Utter("SY_TWR", 30, 1.0, 500, true));
            now = t0.AddSeconds(40);
            conv.Tick();
            Assert(done.Count == 2 && done[1].IsOwn && done[1].Callsigns.Single() == "You", "own-only conversation");
            Assert(conv.Counts.remote == 2 && conv.Counts.own == 2, "counts " + conv.Counts);
        }

        private sealed class NoFocusForm : System.Windows.Forms.Form
        {
            protected override bool ShowWithoutActivation => true; // never steal focus from vatSys
        }

        /// <summary>New calls arriving must not reset the list's scroll or selection.</summary>
        private static void SayAgainListKeepsScrollAndSelection()
        {
            const string profile = @"C:\Users\jamie\Documents\vatSys Files\Profiles\Australia";
            if (!System.IO.Directory.Exists(profile)) { Console.WriteLine("     (skipped: no vatSys profile for colours)"); return; }
            Preview.LoadProfileColours(profile);

            var plugin = new ReplayPlugin(new ReplaySettings());
            var now = DateTime.UtcNow;
            var pcm = new short[960];
            Clip F(int n, double ago) => new Clip(plugin.Store.NextId(), now.AddSeconds(-ago - 1), now.AddSeconds(-ago), 118_500_000, "Gingin T 118.500",
                new[] { "ACFT" + n }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
            for (int n = 0; n < 30; n++) plugin.Store.Add(F(n, 200 - n * 5));

            using (var form = new NoFocusForm { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(520, 340), ShowInTaskbar = false })
            {
                var page = new AFVReplay.UI.SayAgainPage(plugin) { Dock = System.Windows.Forms.DockStyle.Fill };
                form.Controls.Add(page);
                form.Show();
                void Pump() { for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); } }
                Pump();
                var list = FindControl<System.Windows.Forms.ListView>(page);
                Assert(list.Items.Count == 30, "30 calls listed, got " + list.Items.Count);

                // Scroll down and pick a call.
                list.TopItem = list.Items[10]; list.TopItem = list.Items[10];
                list.Items[12].Selected = true;
                var topClip = list.TopItem.Tag; var picked = list.Items[12].Tag;
                Pump();

                // Busy frequency: three new calls arrive.
                for (int n = 30; n < 33; n++) { plugin.Store.Add(F(n, 0.5 * (33 - n))); page.RefreshView(); Pump(); }
                Assert(list.Items.Count == 33 && ((Clip)list.Items[0].Tag).Callsigns[0] == "ACFT32", "new calls inserted at the top");
                Assert(list.TopItem.Tag == topClip, $"scroll position kept on the same call (top is now {((Clip)list.TopItem.Tag).Callsigns[0]})");
                Assert(list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag == picked, "selection kept");

                // vatSys's scroll bar replaces the Windows one, and follows the list both ways.
                var bar = FindControl<VATSYSControls.ScrollBar>(page);
                Assert(bar != null && bar.Visible, "vatSys scroll bar shown beside the list");
                Assert(list.Width > list.Parent.ClientSize.Width, "the list's Windows scroll bar is clipped off");
                int row = list.GetItemRect(0).Height;
                bar.Value = 20 * row;                       // drag the vatSys bar
                Pump();
                Assert(Math.Abs(list.TopItem.Index - 20) <= 1 || bar.Value == bar.Maximum, "bar position maps to rows, top row " + list.TopItem.Index);
                // Mid-drag, vatSys's bar moves its value and raises Scrolling (Scroll only on release):
                // the list must follow straight away.
                var sbType = typeof(VATSYSControls.ScrollBar);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                sbType.GetField("moValue", flags).SetValue(bar, 10 * row);
                ((Delegate)sbType.GetField("Scrolling", flags).GetValue(bar))?.DynamicInvoke(bar, EventArgs.Empty);
                Assert(Math.Abs(list.TopItem.Index - 10) <= 1, "list follows the thumb while dragging, top row " + list.TopItem.Index);

                list.TopItem = list.Items[5]; list.TopItem = list.Items[5]; // wheel / keys scroll the list
                for (int k = 0; k < 20; k++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                Assert(bar.Value == 5 * row, $"the bar follows the list: value {bar.Value}, expected {5 * row}");

                // At the top: new calls come into view.
                list.TopItem = list.Items[0]; list.TopItem = list.Items[0];
                plugin.Store.Add(F(99, 0.1)); page.RefreshView(); Pump();
                Assert(list.TopItem.Index == 0 && ((Clip)list.TopItem.Tag).Callsigns[0] == "ACFT99", "at the top, the newest call shows");
                int atTop = list.GetItemRect(0).Top;

                // Seen live: the list ended up scrolled above its top (Windows' top row -2) and
                // looked empty. A refresh must put it back.
                SendMessage(list.Handle, 0x1014 /* LVM_SCROLL */, IntPtr.Zero, (IntPtr)(-3 * row));
                Console.WriteLine($"     after forcing a scroll above the top, row 0 at {list.GetItemRect(0).Top} (top {atTop})");
                page.RefreshView(); Pump();
                Assert(list.GetItemRect(0).Top == atTop, "refresh pulls the list back to its top, row 0 at " + list.GetItemRect(0).Top);

                // Scrolled to the bottom, then most calls expire: the remaining calls stay in view.
                bar.Value = bar.Maximum; Pump();
                foreach (var c in plugin.Store.Snapshot().Skip(3).ToList()) plugin.Store.Remove(c);
                page.RefreshView(); Pump();
                Assert(list.Items.Count == 3 && list.GetItemRect(0).Top == atTop, $"after expiry the last {list.Items.Count} calls show from the top, row 0 at {list.GetItemRect(0).Top}");

                // A busy session: calls arriving and expiring while the list is scrolled about.
                // Whatever happens, the list must never scroll above its top or past its end.
                var rng = new Random(3);
                int id = 200;
                for (int step = 0; step < 300; step++)
                {
                    int what = rng.Next(6);
                    if (what == 5) page.Visible = !page.Visible; // another VSCS page shown, then back
                    if (what == 0 || list.Items.Count < 3) plugin.Store.Add(F(id++, 0.1));
                    else if (what == 1)
                    {
                        var kept = plugin.Store.Snapshot();
                        int drop = rng.Next(1, Math.Min(6, kept.Count));
                        foreach (var c in kept.Skip(kept.Count - drop)) plugin.Store.Remove(c); // oldest expire
                    }
                    else if (what == 2) bar.Value = rng.Next(0, Math.Max(1, bar.Maximum + 1));
                    else if (what == 3 && list.Items.Count > 0) { var i = list.Items[rng.Next(list.Items.Count)]; i.Selected = true; i.EnsureVisible(); }
                    page.RefreshView();
                    System.Windows.Forms.Application.DoEvents();
                    if (list.Items.Count == 0 || !page.Visible) continue;
                    int top0 = list.GetItemRect(0).Top;
                    int rowsShowing = (list.ClientSize.Height - atTop) / row;
                    int lowest = atTop - Math.Max(0, list.Items.Count - rowsShowing) * row;
                    Assert(top0 <= atTop && top0 >= lowest - row,
                        $"step {step}: list scrolled out of range (row 0 at {top0}, allowed {lowest}..{atTop}, {list.Items.Count} calls)");
                }
                form.Close();
            }
        }

        /// <summary>
        /// As in vatSys: the page is built hidden at its default size, calls arrive while it is
        /// hidden, then it is shown (made visible first, sized after, as the VSCS injector does).
        /// </summary>
        private static void SayAgainListShowsCallsFromBeforeOpening(string profile)
        {
            try { Preview.LoadProfileColours(profile); } catch (Exception) { } // already loaded by an earlier test
            var plugin = new ReplayPlugin(new ReplaySettings());
            var now = DateTime.UtcNow;
            var pcm = new short[960];
            Clip F(int n, double ago) => new Clip(plugin.Store.NextId(), now.AddSeconds(-ago - 1), now.AddSeconds(-ago), 118_500_000, "Gingin T 118.500",
                new[] { "ACFT" + n }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);

            using (var form = new NoFocusForm { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(620, 360), ShowInTaskbar = false })
            {
                form.Show();
                void Pump() { for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); } }
                var page = new AFVReplay.UI.SayAgainPage(plugin) { Visible = false };
                form.Controls.Add(page);
                Pump();
                for (int n = 0; n < 8; n++) { plugin.Store.Add(F(n, 60 - n * 5)); page.RefreshView(); Pump(); }

                page.Visible = true;
                page.Bounds = new System.Drawing.Rectangle(172, 0, 430, 330);
                Pump();
                page.RefreshView(); Pump();
                var list = FindControl<System.Windows.Forms.ListView>(page);
                int top0 = list.GetItemRect(0).Top, height = list.ClientSize.Height;
                Console.WriteLine($"     opened: {list.Items.Count} calls, row 0 at {top0}, list {height} px high");
                Assert(list.Items.Count == 8, "8 calls listed, got " + list.Items.Count);
                Assert(top0 > 0 && top0 < 40, "first call shows just under the header, row 0 at " + top0);
                form.Close();
            }
        }

        private static T FindControl<T>(System.Windows.Forms.Control c) where T : System.Windows.Forms.Control
        {
            if (c is T t) return t;
            foreach (System.Windows.Forms.Control child in c.Controls) { var f = FindControl<T>(child); if (f != null) return f; }
            return null;
        }

        /// <summary>Two installs (launcher + manual): the second copy must not run.</summary>
        private static void SecondCopyDisablesItself()
        {
            const string key = "AFVReplay.LoadedFrom";
            var before = AppDomain.CurrentDomain.GetData(key);
            AppDomain.CurrentDomain.SetData(key, @"C:\elsewhere\Plugins\AFVReplay\AFVReplay.dll");
            try
            {
                var second = new ReplayPlugin();
                Assert(second.StatusText.StartsWith("Disabled: installed twice"), "second copy status: " + second.StatusText);
                Assert(second.Store == null, "second copy must not start anything");
                Assert(second.Name == "Say Again", "plugin name " + second.Name);
            }
            finally
            {
                AppDomain.CurrentDomain.SetData(key, before);
            }
        }

        private static double Rms(float[] a)
        {
            if (a == null || a.Length == 0) return 0;
            double s = 0;
            foreach (var x in a) s += x * x;
            return Math.Sqrt(s / a.Length);
        }
    }
}
