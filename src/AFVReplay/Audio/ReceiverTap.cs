using System;
using GeoVR.Client;
using GeoVR.Shared;
using NAudio.Wave;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Sits in AFV's soundcard mixer in place of one receiver. Passes its audio straight through
    /// unchanged and copies it to a <see cref="ClipRecorder"/>.
    /// </summary>
    internal sealed class ReceiverTap : ISampleProvider
    {
        private readonly ClipRecorder recorder;
        private volatile bool detached;

        public ReceiverTap(ReceiverSampleProvider receiver, Action<RawRecording> onComplete, Action<uint> onRxStart)
        {
            Receiver = receiver;
            recorder = new ClipRecorder(onComplete, onRxStart);
            receiver.ReceivingCallsignsChanged += Receiver_ReceivingCallsignsChanged;
        }

        public ReceiverSampleProvider Receiver { get; }

        public WaveFormat WaveFormat => Receiver.WaveFormat;

        public bool IsRecording => recorder.IsRecording;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Receiver.Read(buffer, offset, count);
            if (!detached)
            {
                try { recorder.Process(buffer, offset, n); }
                catch (Exception ex) { Diag.Warn("tap process failed: " + ex.GetType().Name); recorder.RequestReset(); }
            }
            return n;
        }

        // AFV raises this from inside Receiver.Read, on the audio thread, before producing the block.
        private void Receiver_ReceivingCallsignsChanged(object sender, TransceiverReceivingCallsignsChangedEventArgs e)
        {
            if (detached) return;
            try { recorder.OnCallsignsChanged(e.ReceivingCallsigns, Receiver.Frequency); }
            catch (Exception ex) { Diag.Warn("tap event failed: " + ex.GetType().Name); }
        }

        public void ResetRecording() => recorder.RequestReset();

        public void Detach()
        {
            detached = true;
            Receiver.ReceivingCallsignsChanged -= Receiver_ReceivingCallsignsChanged;
            recorder.RequestReset();
        }
    }
}
