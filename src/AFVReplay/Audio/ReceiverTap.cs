using System;
using System.Collections.Generic;
using GeoVR.Client;
using GeoVR.Shared;
using NAudio.Wave;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Sits in AFV's soundcard mixer in place of one receiver. Passes its audio straight through
    /// unchanged and copies it to a <see cref="ClipRecorder"/>.
    /// <para>
    /// Calls over you: while you transmit, AFV mutes the receivers you're transmitting on, and a muted
    /// receiver drops every packet (SoundcardSampleProvider.AddOpusSamples), so a station calling at the same
    /// time is never heard or seen. When <c>isTransmitting</c> says you're transmitting and AFV has muted this
    /// receiver, the tap un-mutes it and outputs silence itself instead: you hear exactly what AFV would have
    /// given you (nothing), but the call is recorded and reported through <c>onOverYou</c>. When you stop
    /// transmitting the silence ends, so the rest of the call plays as it normally would.
    /// </para>
    /// </summary>
    internal sealed class ReceiverTap : ISampleProvider
    {
        private readonly ClipRecorder recorder;
        private readonly Func<bool> isTransmitting;
        private readonly Func<bool> catchOverYou;
        private readonly Func<string> ownCallsign;
        private readonly Action<uint, string> onOverYou;
        private volatile bool detached;

        private volatile bool silencing;   // we un-muted this receiver for your transmission: output nothing
        private bool reported;             // audio thread only: a call over this transmission has been reported
        private volatile bool calledOver;  // someone is calling on it right now, over your transmission

        /// <param name="isTransmitting">True while you're transmitting; null leaves AFV's mute alone.</param>
        /// <param name="catchOverYou">Whether to catch calls over you (the setting), checked as each transmission starts.</param>
        /// <param name="ownCallsign">Your callsign: never counted as a call over you.</param>
        /// <param name="onOverYou">A station started calling on this frequency while you were transmitting
        /// (frequency, their callsign). Once per transmission; raised on the audio thread.</param>
        public ReceiverTap(ReceiverSampleProvider receiver, Action<RawRecording> onComplete, Action<uint> onRxStart,
                           Func<bool> isTransmitting = null, Func<bool> catchOverYou = null,
                           Func<string> ownCallsign = null, Action<uint, string> onOverYou = null)
        {
            Receiver = receiver;
            recorder = new ClipRecorder(onComplete, onRxStart);
            this.isTransmitting = isTransmitting;
            this.catchOverYou = catchOverYou ?? (() => true);
            this.ownCallsign = ownCallsign;
            this.onOverYou = onOverYou;
            receiver.ReceivingCallsignsChanged += Receiver_ReceivingCallsignsChanged;
        }

        public ReceiverSampleProvider Receiver { get; }

        public WaveFormat WaveFormat => Receiver.WaveFormat;

        public bool IsRecording => recorder.IsRecording;

        /// <summary>True while this receiver is held silent for your transmission (see the class remarks).</summary>
        public bool IsSilencing => silencing;

        /// <summary>True while a station is calling on this frequency over your transmission (the block is happening now).</summary>
        public bool IsCalledOver => calledOver && silencing;

        public int Read(float[] buffer, int offset, int count)
        {
            if (!detached) FollowTransmit();
            int n = Receiver.Read(buffer, offset, count);
            if (!detached)
            {
                try { recorder.Process(buffer, offset, n); }
                catch (Exception ex) { Diag.Warn("tap process failed: " + ex.GetType().Name); recorder.RequestReset(); }
            }
            // What AFV itself would have played while you transmit: nothing.
            if (silencing && n > 0) Array.Clear(buffer, offset, n);
            return n;
        }

        private void FollowTransmit()
        {
            if (isTransmitting == null) return;
            try
            {
                bool tx = isTransmitting();
                // Once silencing, keep it up until you stop transmitting (even if the setting changes meanwhile):
                // the receiver is un-muted, so stopping early would let the call through over your transmission.
                if (tx && Receiver.Mute && (silencing || catchOverYou()))
                {
                    // AFV muted this receiver for your transmission. Silence it ourselves so its packets still arrive.
                    if (!silencing) reported = false;
                    silencing = true;
                    Receiver.Mute = false;
                }
                else if (!tx && silencing)
                {
                    silencing = false;
                    calledOver = false;
                }
            }
            catch (Exception ex)
            {
                Diag.Warn("tap transmit check failed: " + ex.GetType().Name);
                try { GiveMuteBack(); } catch { }
            }
        }

        // AFV raises this from inside Receiver.Read, on the audio thread, before producing the block.
        private void Receiver_ReceivingCallsignsChanged(object sender, TransceiverReceivingCallsignsChangedEventArgs e)
        {
            if (detached) return;
            try
            {
                IList<string> callsigns = e.ReceivingCallsigns;
                if (silencing && callsigns != null && callsigns.Count > 0)
                {
                    // In case AFV ever sends your own transmission back: you aren't calling over yourself.
                    string own = SafeOwnCallsign();
                    var others = new List<string>(callsigns.Count);
                    foreach (var c in callsigns)
                    {
                        if (string.IsNullOrWhiteSpace(c)) continue;
                        if (!string.IsNullOrEmpty(own) && string.Equals(c, own, StringComparison.OrdinalIgnoreCase)) continue;
                        others.Add(c);
                    }
                    recorder.OnCallsignsChanged(others, Receiver.Frequency);
                    calledOver = others.Count > 0;
                    if (others.Count > 0)
                    {
                        recorder.MarkOverYou();
                        if (!reported)
                        {
                            reported = true;
                            try { onOverYou?.Invoke(Receiver.Frequency, others[0]); }
                            catch (Exception ex) { Diag.Warn("over-you report failed: " + ex.GetType().Name); }
                        }
                    }
                    return;
                }
                calledOver = false;
                recorder.OnCallsignsChanged(callsigns, Receiver.Frequency);
            }
            catch (Exception ex) { Diag.Warn("tap event failed: " + ex.GetType().Name); }
        }

        private string SafeOwnCallsign()
        {
            try { return ownCallsign?.Invoke(); }
            catch { return null; }
        }

        /// <summary>If we took AFV's mute for a transmission still going on, puts it back (AFV's own half-duplex again).</summary>
        private void GiveMuteBack()
        {
            if (!silencing) return;
            silencing = false;
            calledOver = false;
            bool tx;
            try { tx = isTransmitting?.Invoke() ?? false; }
            catch { tx = true; } // can't tell: err on the side of hearing nothing over your transmission
            if (tx) Receiver.Mute = true;
        }

        public void ResetRecording() => recorder.RequestReset();

        public void Detach()
        {
            detached = true;
            Receiver.ReceivingCallsignsChanged -= Receiver_ReceivingCallsignsChanged;
            recorder.RequestReset();
            // The bare receiver goes back in the mix: it must not be left un-muted mid-transmission.
            try { GiveMuteBack(); } catch (Exception ex) { Diag.Warn("tap re-mute failed: " + ex.GetType().Name); }
        }
    }
}
