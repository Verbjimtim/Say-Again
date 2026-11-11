using System;
using NAudio.Wave;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Plays a clip into AFV's own mix, so it comes out of the same headset and extra outputs at the vatSys volume.
    /// Always returns a full block (silence when idle) so the mixer never drops it.
    /// </summary>
    internal sealed class ReplayPlayer : ISampleProvider
    {
        private readonly object sync = new object();
        private float[] pcm;
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, 1);

        /// <summary>0..2, applied on top of the vatSys output volume.</summary>
        public float Volume { get; set; } = 1f;

        /// <summary>1..2, playback speed (pitch unchanged). Applies from the next call played.</summary>
        public float Speed { get; set; } = 1f;

        public long? PlayingClipId { get; private set; }

        /// <summary>The clip now playing, or null.</summary>
        public Clip PlayingClip { get; private set; }

        public event EventHandler PlaybackStarted;

        public bool IsPlaying { get { lock (sync) return pcm != null; } }

        private bool paused;

        /// <summary>How far through the loaded clip, 0..1 (0 when nothing is loaded).</summary>
        public double Progress { get { lock (sync) return pcm == null || pcm.Length == 0 ? 0 : position / (double)pcm.Length; } }

        /// <summary>A clip is loaded but held where it stopped.</summary>
        public bool IsPaused { get { lock (sync) return pcm != null && paused; } }

        public event EventHandler PausedChanged;

        /// <summary>Holds the current clip where it is. Does nothing if nothing is playing.</summary>
        public void Pause() => SetPaused(true);

        /// <summary>Carries on from where it was paused.</summary>
        public void Resume() => SetPaused(false);

        private void SetPaused(bool value)
        {
            bool changed;
            lock (sync)
            {
                changed = pcm != null && paused != value;
                if (changed) paused = value;
            }
            if (changed) PausedChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler PlaybackStopped;

        public void Play(Clip clip)
        {
            if (clip == null || clip.IsSilent) return;
            var decoded = clip.Decode();
            if (decoded == null || decoded.Length == 0) return;
            var stretched = TimeStretch.Apply(decoded, Speed);
            if (!ReferenceEquals(stretched, decoded)) Array.Clear(decoded, 0, decoded.Length);
            lock (sync)
            {
                Wipe();
                pcm = stretched;
                position = 0;
                paused = false;
                PlayingClipId = clip.Id;
                PlayingClip = clip;
            }
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            bool was;
            lock (sync)
            {
                was = pcm != null;
                Wipe();
            }
            if (was) PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            bool finished = false;
            lock (sync)
            {
                int written = 0;
                if (pcm != null && !paused)
                {
                    float vol = Volume;
                    int n = Math.Min(count, pcm.Length - position);
                    for (int i = 0; i < n; i++) buffer[offset + i] = pcm[position + i] * vol;
                    position += n;
                    written = n;
                    if (position >= pcm.Length)
                    {
                        Wipe();
                        finished = true;
                    }
                }
                if (written < count) Array.Clear(buffer, offset + written, count - written);
            }
            if (finished) PlaybackStopped?.Invoke(this, EventArgs.Empty);
            return count;
        }

        private void Wipe()
        {
            if (pcm != null) Array.Clear(pcm, 0, pcm.Length);
            pcm = null;
            position = 0;
            paused = false;
            PlayingClipId = null;
            PlayingClip = null;
        }
    }
}
