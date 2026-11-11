using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GeoVR.Client;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AFVReplay.VatSys
{
    /// <summary>
    /// Every use of vatSys / AFV internals lives here, so a vatSys update only breaks one file.
    /// <see cref="Validate"/> checks all of it up front so the plugin can switch itself off cleanly.
    /// </summary>
    internal static class VatSysBridge
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly Assembly VatSysAssembly = typeof(vatsys.AFV).Assembly;

        // AFV audio path: AFV.instance -> Client -> Soundcards[MainSoundcardIndex] -> soundcardSampleProvider -> mixer / receiverInputs
        private static readonly FieldInfo AfvInstance = typeof(vatsys.AFV).GetField("instance", Stat);
        private static readonly FieldInfo AfvClient = typeof(vatsys.AFV).GetField("Client", Inst);
        private static readonly FieldInfo AfvMainIndex = typeof(vatsys.AFV).GetField("MainSoundcardIndex", Inst);
        private static readonly FieldInfo SoundcardProvider = typeof(Soundcard).GetField("soundcardSampleProvider", Inst);
        internal static readonly FieldInfo SspMixer = typeof(SoundcardSampleProvider).GetField("mixer", Inst);
        internal static readonly FieldInfo SspReceivers = typeof(SoundcardSampleProvider).GetField("receiverInputs", Inst);
        internal static readonly FieldInfo MixerSources = typeof(MixingSampleProvider).GetField("sources", Inst);

        // Input mapping, reused so the Replay key captures exactly like PTT.
        private static readonly Type DirectInputCaptureType = VatSysAssembly.GetType("vatsys.DirectInputCapture");
        private static readonly MethodInfo DicStart = DirectInputCaptureType?.GetMethod("Start", Stat);
        private static readonly MethodInfo DicStop = DirectInputCaptureType?.GetMethod("Stop", Stat);
        private static readonly MethodInfo DicMapInput = DirectInputCaptureType?.GetMethod("MapInput", Stat);
        private static readonly Type InputMapType = VatSysAssembly.GetType("vatsys.InputMap");
        private static readonly MethodInfo InputMapSerialize = InputMapType?.GetMethod("Serialize", Inst, null, Type.EmptyTypes, null);
        private static readonly FieldInfo MmiMainForm = typeof(vatsys.MMI).GetField("MainForm", Stat);
        // Optional (cosmetic): the VSCS window, for the Replay button. Not part of Validate().
        private static readonly FieldInfo MmiVscsWindow = typeof(vatsys.MMI).GetField("VSCSWindow", Stat);

        // Coordination (hotline/coldline) audio: vatSys's own Mumble client. Optional: if any of this
        // changes, only coord replay is switched off (see ValidateCoord).
        private static readonly Type MumbleType = VatSysAssembly.GetType("vatsys.Mumble");
        private static readonly FieldInfo MumbleInstance = MumbleType?.GetField("instance", Stat);
        private static readonly FieldInfo MumbleProtocolField = MumbleType?.GetField("protocol", Inst);
        private static readonly FieldInfo MumbleOutputField = MumbleType?.GetField("outputProvider", Inst);
        private static readonly Type MumbleProtocolType = VatSysAssembly.GetType("vatsys.MumbleProtocol");
        private static readonly EventInfo MumbleAudioReceived = MumbleProtocolType?.GetEvent("AudioReceived", Inst);
        private static readonly Type MumbleAudioArgsType = VatSysAssembly.GetType("vatsys.MumbleAudioReceivedEventArgs");
        private static readonly FieldInfo MumbleArgsUser = MumbleAudioArgsType?.GetField("User", Inst);
        private static readonly FieldInfo MumbleArgsAudio = MumbleAudioArgsType?.GetField("Audio", Inst);
        private static readonly Type MumbleOutputType = VatSysAssembly.GetType("vatsys.MumbleOutputProvider");
        private static readonly FieldInfo MumbleOutputMixer = MumbleOutputType?.GetField("mixer", Inst);

        // Your own mic: vatSys's AFV input (sent while PTT is held) and its coord input (sent on
        // open lines). Optional: if any of this changes, only recording your own voice switches off.
        private static readonly FieldInfo AfvInputMix = typeof(vatsys.AFV).GetField("inputMix", Inst);
        private static readonly FieldInfo MumbleAudioIn = MumbleType?.GetField("audioIn", Inst);
        private static readonly FieldInfo MumbleTargets = MumbleType?.GetField("targetSessionIds", Inst);
        private static readonly PropertyInfo MumbleIsConnected = MumbleType?.GetProperty("IsConnected", Stat);
        private static readonly PropertyInfo MumbleSelfMute = MumbleType?.GetProperty("SelfMute", Stat);
        private static readonly Type InputMixType = VatSysAssembly.GetType("vatsys.InputMix");
        private static readonly EventInfo InputMixAudio = InputMixType?.GetEvent("AudioAvailable", Inst);
        private static readonly Type InputArgsType = VatSysAssembly.GetType("vatsys.InputAudioAvailableEventArgs");
        private static readonly PropertyInfo InputArgsBuffer = InputArgsType?.GetProperty("Buffer", Inst);
        private static readonly PropertyInfo InputArgsOffset = InputArgsType?.GetProperty("Offset", Inst);
        private static readonly PropertyInfo InputArgsLength = InputArgsType?.GetProperty("Length", Inst);

        /// <summary>Returns null when your own mic can be recorded with this vatSys, otherwise what is missing.</summary>
        public static string ValidateMic()
        {
            var missing = new List<string>();
            void Check(object member, string name) { if (member == null) missing.Add(name); }
            Check(AfvInputMix, "AFV.inputMix");
            Check(MumbleAudioIn, "Mumble.audioIn");
            Check(MumbleTargets, "Mumble.targetSessionIds");
            Check(MumbleIsConnected, "Mumble.IsConnected");
            Check(MumbleSelfMute, "Mumble.SelfMute");
            Check(InputMixAudio, "InputMix.AudioAvailable");
            Check(InputArgsBuffer, "InputAudioAvailableEventArgs.Buffer");
            Check(InputArgsOffset, "InputAudioAvailableEventArgs.Offset");
            Check(InputArgsLength, "InputAudioAvailableEventArgs.Length");
            if (AfvInputMix != null && AfvInputMix.FieldType != InputMixType) missing.Add("AFV.inputMix type");
            if (MumbleAudioIn != null && MumbleAudioIn.FieldType != InputMixType) missing.Add("Mumble.audioIn type");
            if (MumbleTargets != null && !typeof(System.Collections.ICollection).IsAssignableFrom(MumbleTargets.FieldType)) missing.Add("targetSessionIds type");
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        /// <summary>vatSys's AFV mic mix (16-bit mono 48 kHz), or null before AFV starts.</summary>
        public static object GetAfvInputMix()
        {
            var afv = AfvInstance.GetValue(null);
            return afv == null ? null : AfvInputMix.GetValue(afv);
        }

        /// <summary>vatSys's coord mic mix (what it sends on open lines), or null.</summary>
        public static object GetMumbleInputMix(object mumble) => mumble == null ? null : MumbleAudioIn.GetValue(mumble);

        private static IReadOnlyList<AFVReplay.Audio.LineInfo> lastLines = Array.Empty<AFVReplay.Audio.LineInfo>();

        /// <summary>
        /// vatSys's hotlines and coldlines (public plugin API: Audio.VSCSLines) other than closed ones,
        /// with who they are to and their state. Monitor lines are left out (not calls). Read from a
        /// timer thread while vatSys may be changing the list, so a failed read returns the last good one.
        /// </summary>
        public static IReadOnlyList<AFVReplay.Audio.LineInfo> GetActiveLines()
        {
            try
            {
                var lines = vatsys.Audio.VSCSLines;
                if (lines == null) return lastLines = Array.Empty<AFVReplay.Audio.LineInfo>();
                var result = new List<AFVReplay.Audio.LineInfo>();
                foreach (var l in lines.ToArray())
                {
                    if (l == null) continue;
                    if (l.Type != vatsys.VSCSLineTypes.Hotline && l.Type != vatsys.VSCSLineTypes.Coldline) continue;
                    if (l.State == vatsys.VSCSLineStates.Closed || l.State == vatsys.VSCSLineStates.None) continue;
                    string callsign = null;
                    try { callsign = l.ATC?.Callsign; } catch { }
                    if (string.IsNullOrWhiteSpace(callsign)) { try { callsign = l.Name; } catch { } } // e.g. a coldline with no ATC attached
                    if (string.IsNullOrWhiteSpace(callsign)) callsign = null;
                    result.Add(new AFVReplay.Audio.LineInfo(callsign, l.Type.ToString(), l.State.ToString()));
                }
                return lastLines = result;
            }
            catch
            {
                return lastLines;
            }
        }

        /// <summary>True while vatSys is sending your mic to at least one open line.</summary>
        public static bool IsCoordSending(object mumble)
        {
            if (mumble == null) return false;
            if (!(bool)MumbleIsConnected.GetValue(null) || (bool)MumbleSelfMute.GetValue(null)) return false;
            return MumbleTargets.GetValue(mumble) is System.Collections.ICollection c && c.Count > 0;
        }

        /// <summary>Subscribes to an InputMix's audio (16-bit mono 48 kHz). Returns the delegate to unsubscribe with.</summary>
        public static Delegate SubscribeInput(object inputMix, Action<byte[], int, int> handler)
        {
            var relay = new InputRelay(handler);
            var d = Delegate.CreateDelegate(InputMixAudio.EventHandlerType, relay, typeof(InputRelay).GetMethod(nameof(InputRelay.OnAudio)));
            InputMixAudio.AddEventHandler(inputMix, d);
            return d;
        }

        public static void UnsubscribeInput(object inputMix, Delegate d) => InputMixAudio.RemoveEventHandler(inputMix, d);

        private sealed class InputRelay
        {
            private readonly Action<byte[], int, int> handler;
            public InputRelay(Action<byte[], int, int> handler) { this.handler = handler; }

            // Runs on vatSys's audio capture thread: must never throw back into it.
            public void OnAudio(object sender, EventArgs e)
            {
                try { handler((byte[])InputArgsBuffer.GetValue(e), (int)InputArgsOffset.GetValue(e), (int)InputArgsLength.GetValue(e)); }
                catch (Exception ex) { Diag.Warn("mic audio failed: " + ex.GetType().Name); }
            }
        }

        /// <summary>For tests: raises an InputMix's AudioAvailable as if the mic produced audio.</summary>
        internal static void RaiseInput(object inputMix, byte[] audio)
        {
            var field = InputMixType.GetField("AudioAvailable", Inst);
            var d = (Delegate)field.GetValue(inputMix);
            d?.DynamicInvoke(inputMix, Activator.CreateInstance(InputArgsType, audio, 0, audio.Length));
        }

        /// <summary>For tests: a real vatSys InputMix, not started.</summary>
        internal static object CreateInputMix() => Activator.CreateInstance(InputMixType, new NAudio.Wave.WaveFormat(48000, 16, 1));

        /// <summary>Returns null when coord replay can work with this vatSys, otherwise what is missing.</summary>
        public static string ValidateCoord()
        {
            var missing = new List<string>();
            void Check(object member, string name) { if (member == null) missing.Add(name); }
            Check(MumbleInstance, "Mumble.instance");
            Check(MumbleProtocolField, "Mumble.protocol");
            Check(MumbleOutputField, "Mumble.outputProvider");
            Check(MumbleAudioReceived, "MumbleProtocol.AudioReceived");
            Check(MumbleArgsUser, "MumbleAudioReceivedEventArgs.User");
            Check(MumbleArgsAudio, "MumbleAudioReceivedEventArgs.Audio");
            Check(MumbleOutputMixer, "MumbleOutputProvider.mixer");
            if (MumbleArgsUser != null && MumbleArgsUser.FieldType != typeof(MumbleSharp.Model.User)) missing.Add("User type");
            if (MumbleArgsAudio != null && MumbleArgsAudio.FieldType != typeof(byte[])) missing.Add("Audio type");
            if (MumbleOutputMixer != null && MumbleOutputMixer.FieldType != typeof(MixingSampleProvider)) missing.Add("Mumble mixer type");
            if (MumbleAudioReceived != null && MumbleAudioArgsType != null
                && MumbleAudioReceived.EventHandlerType != typeof(EventHandler<>).MakeGenericType(MumbleAudioArgsType)) missing.Add("AudioReceived type");
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        /// <summary>vatSys's Mumble client, or null before vatSys starts its audio.</summary>
        public static object GetMumble() => MumbleInstance.GetValue(null);

        public static object GetMumbleProtocol(object mumble) => MumbleProtocolField.GetValue(mumble);

        /// <summary>The mixer feeding the coord output device(s), before vatSys's coord volume.</summary>
        public static MixingSampleProvider GetMumbleMixer(object mumble) =>
            MumbleOutputMixer.GetValue(MumbleOutputField.GetValue(mumble)) as MixingSampleProvider;

        /// <summary>Subscribes to received coord audio. Returns the delegate to pass to Unsubscribe.</summary>
        public static Delegate SubscribeCoordAudio(object protocol, Action<MumbleSharp.Model.User, byte[]> handler)
        {
            var relay = new CoordRelay(handler);
            var d = Delegate.CreateDelegate(MumbleAudioReceived.EventHandlerType, relay,
                typeof(CoordRelay).GetMethod(nameof(CoordRelay.OnAudio)));
            MumbleAudioReceived.AddEventHandler(protocol, d);
            return d;
        }

        public static void UnsubscribeCoordAudio(object protocol, Delegate d) => MumbleAudioReceived.RemoveEventHandler(protocol, d);

        private sealed class CoordRelay
        {
            private readonly Action<MumbleSharp.Model.User, byte[]> handler;
            public CoordRelay(Action<MumbleSharp.Model.User, byte[]> handler) { this.handler = handler; }

            // Runs on vatSys's Mumble thread: must never throw back into it.
            public void OnAudio(object sender, EventArgs e)
            {
                try { handler((MumbleSharp.Model.User)MumbleArgsUser.GetValue(e), (byte[])MumbleArgsAudio.GetValue(e)); }
                catch (Exception ex) { Diag.Warn("coord audio failed: " + ex.GetType().Name); }
            }
        }

        /// <summary>For tests: raises vatSys's AudioReceived event as if a packet arrived.</summary>
        internal static void RaiseCoordAudio(object protocol, MumbleSharp.Model.User user, byte[] audio)
        {
            var field = MumbleProtocolType.GetField("AudioReceived", Inst);
            var d = (Delegate)field.GetValue(protocol);
            d?.DynamicInvoke(protocol, Activator.CreateInstance(MumbleAudioArgsType, user, audio));
        }

        public static string VatSysVersion => VatSysAssembly.GetName().Version?.ToString() ?? "?";

        /// <summary>Returns null when everything this plugin relies on is present, otherwise what is missing.</summary>
        public static string Validate()
        {
            var missing = new List<string>();
            void Check(object member, string name) { if (member == null) missing.Add(name); }

            Check(AfvInstance, "AFV.instance");
            Check(AfvClient, "AFV.Client");
            Check(AfvMainIndex, "AFV.MainSoundcardIndex");
            Check(SoundcardProvider, "Soundcard.soundcardSampleProvider");
            Check(SspMixer, "SoundcardSampleProvider.mixer");
            Check(SspReceivers, "SoundcardSampleProvider.receiverInputs");
            Check(MixerSources, "MixingSampleProvider.sources");
            Check(DicStart, "DirectInputCapture.Start");
            Check(DicStop, "DirectInputCapture.Stop");
            Check(DicMapInput, "DirectInputCapture.MapInput");
            Check(InputMapSerialize, "InputMap.Serialize");
            Check(MmiMainForm, "MMI.MainForm");

            if (AfvClient != null && !typeof(UserClient).IsAssignableFrom(AfvClient.FieldType)) missing.Add("AFV.Client type");
            if (SspMixer != null && SspMixer.FieldType != typeof(MixingSampleProvider)) missing.Add("mixer type");
            if (SspReceivers != null && SspReceivers.FieldType != typeof(ConcurrentDictionary<ushort, ReceiverSampleProvider>)) missing.Add("receiverInputs type");
            if (MixerSources != null && MixerSources.FieldType != typeof(List<ISampleProvider>)) missing.Add("sources type");

            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        /// <summary>The main soundcard's receive mixer, or null while AFV isn't running.</summary>
        public static SoundcardSampleProvider GetSoundcardSampleProvider()
        {
            var afv = AfvInstance.GetValue(null);
            if (afv == null) return null;
            if (!(AfvClient.GetValue(afv) is UserClient client) || !client.Started) return null;
            var cards = client.Soundcards;
            int index = (int)AfvMainIndex.GetValue(afv);
            if (index < 0 || index >= cards.Length) return null;
            return SoundcardProvider.GetValue(cards[index]) as SoundcardSampleProvider;
        }

        public static MixingSampleProvider GetMixer(SoundcardSampleProvider ssp) => (MixingSampleProvider)SspMixer.GetValue(ssp);

        public static ConcurrentDictionary<ushort, ReceiverSampleProvider> GetReceivers(SoundcardSampleProvider ssp) =>
            (ConcurrentDictionary<ushort, ReceiverSampleProvider>)SspReceivers.GetValue(ssp);

        /// <summary>The mixer's live input list. Lock on the list itself while changing it (NAudio does the same in Read).</summary>
        public static List<ISampleProvider> GetSources(MixingSampleProvider mixer) => (List<ISampleProvider>)MixerSources.GetValue(mixer);

        public static Form MainForm => MmiMainForm.GetValue(null) as Form;

        public static Form VscsWindow => MmiVscsWindow?.GetValue(null) as Form;

        // Optional (cosmetic): the VSCS button panel and which page it shows, for Say Again.
        private static readonly Type VscsPanelType = VatSysAssembly.GetType("vatsys.VSCSControlDX");
        private static readonly FieldInfo VscsCurrentPage = VscsPanelType?.GetField("currentPageType", Inst);

        /// <summary>The VSCS's DirectX button panel inside the VSCS window, or null.</summary>
        public static Control VscsPanel
        {
            get
            {
                var w = VscsWindow;
                if (w == null || VscsPanelType == null) return null;
                foreach (Control c in w.Controls) if (c.GetType() == VscsPanelType) return c;
                return null;
            }
        }

        /// <summary>True while the VSCS shows its main Setup page (not Add/Edit/Remove Freq).</summary>
        public static bool IsVscsSetupPage(Control panel) => VscsPageName(panel) == "Setup";

        /// <summary>The VSCS page showing (Setup, Coord, DirectText, FreqConfig, ...), or null.</summary>
        public static string VscsPageName(Control panel) =>
            VscsCurrentPage == null || panel == null ? null : VscsCurrentPage.GetValue(panel)?.ToString();

        private static readonly MethodInfo VscsSetPage = VscsPanelType?.GetMethod("SetPage", Inst);

        /// <summary>Switches the VSCS to one of its pages by name. Returns false if that isn't possible.</summary>
        internal static bool SetVscsPage(Control panel, string page)
        {
            if (VscsSetPage == null || panel == null) return false;
            var type = VscsSetPage.GetParameters()[0].ParameterType;
            if (!Enum.IsDefined(type, page)) return false;
            VscsSetPage.Invoke(panel, new[] { Enum.Parse(type, page) });
            return true;
        }

        public static string FrequencyName(uint hz)
        {
            string freq = Audio.ClipFinalizer.FormatFrequency(hz);
            try
            {
                var f = vatsys.Audio.VSCSFrequencies.FirstOrDefault(x => x.Frequency == hz);
                if (f != null && !string.IsNullOrWhiteSpace(f.Name)) return f.Name + " " + freq;
            }
            catch { }
            return freq;
        }

        /// <summary>
        /// vatSys's own on/off button (internal class vatsys.ToggleButton), so options look like the rest of vatSys.
        /// Falls back to a button-style CheckBox if the class isn't there.
        /// </summary>
        public static CheckBox CreateToggleButton()
        {
            try
            {
                var type = VatSysAssembly.GetType("vatsys.ToggleButton");
                if (type != null && typeof(CheckBox).IsAssignableFrom(type))
                {
                    return (CheckBox)Activator.CreateInstance(type, true);
                }
            }
            catch { }
            return new CheckBox { Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
        }

        public static void StopVatSysInput() => DicStop.Invoke(null, null);

        public static void StartVatSysInput() => DicStart.Invoke(null, null);

        /// <summary>
        /// Waits for the next key / mouse / joystick press using vatSys's own capture routine.
        /// vatSys's input loop must be stopped first. Returns the vatSys-format JSON, or null if cancelled.
        /// </summary>
        public static async Task<string> MapInputAsync(bool includeJoysticks, CancellationToken token)
        {
            var task = (Task)DicMapInput.Invoke(null, new object[] { includeJoysticks, token });
            await task.ConfigureAwait(false);
            var map = task.GetType().GetProperty("Result").GetValue(task);
            return map == null ? null : (string)InputMapSerialize.Invoke(map, null);
        }
    }
}
