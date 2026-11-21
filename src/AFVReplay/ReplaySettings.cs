using System;
using System.IO;
using System.Xml.Serialization;

namespace AFVReplay
{
    /// <summary>
    /// User preferences only. No audio, callsigns or anything about transmissions is ever stored here.
    /// </summary>
    public sealed class ReplaySettings
    {
        /// <summary>vatSys-format InputMap JSON for the Replay key, or empty.</summary>
        public string ReplayBinding { get; set; } = "";

        /// <summary>vatSys-format InputMap JSON for the Replay Coord key, or empty.</summary>
        public string CoordReplayBinding { get; set; } = "";

        /// <summary>Hotline/coldline calls: 1-10 minutes.</summary>
        public int CoordRetentionMinutes { get; set; } = 10;

        /// <summary>1-5 minutes.</summary>
        public int RetentionMinutes { get; set; } = 5;

        /// <summary>Stop a replay as soon as a new call comes in.</summary>
        public bool StopOnNewCall { get; set; } = true;

        /// <summary>Highlight the selected aircraft's calls in the Say Again list.</summary>
        public bool HighlightSelectedPilot { get; set; } = true;

        /// <summary>Pause other apps' media (Spotify, browser, etc.) while the radio is busy. Off by default.</summary>
        public bool PauseMedia { get; set; }

        /// <summary>Seconds of quiet before paused media resumes: 1-5, or <see cref="ResumeNever"/>.</summary>
        public int MediaResumeSeconds { get; set; } = 2;

        /// <summary><see cref="MediaResumeSeconds"/> value for "never resume".</summary>
        public const int ResumeNever = 6;

        /// <summary>The resume delay, or null for never.</summary>
        public TimeSpan? MediaResumeDelay => MediaResumeSeconds >= ResumeNever ? (TimeSpan?)null : TimeSpan.FromSeconds(MediaResumeSeconds);

        /// <summary>Handoff Alert: tones for a handoff to you, your handoff accepted, and your handoff refused. Off by default.</summary>
        public bool HandoffAlert { get; set; }

        /// <summary>Seconds between repeats while a handoff to you waits: 3-10, or <see cref="RepeatOff"/>.</summary>
        public int HandoffRepeatSeconds { get; set; } = 5;

        /// <summary><see cref="HandoffRepeatSeconds"/> value for "don't repeat".</summary>
        public const int RepeatOff = 2;

        /// <summary>The repeat interval, or null for no repeat.</summary>
        public TimeSpan? HandoffRepeat => HandoffRepeatSeconds <= RepeatOff ? (TimeSpan?)null : TimeSpan.FromSeconds(HandoffRepeatSeconds);

        /// <summary>Replay volume, 0-200 percent, on top of the vatSys output volume.</summary>
        public int VolumePercent { get; set; } = 100;

        /// <summary>Replay speed, 100-200 percent, pitch unchanged.</summary>
        public int SpeedPercent { get; set; } = 100;

        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFVReplay", "settings.xml");

        public void Normalise()
        {
            RetentionMinutes = Math.Max(1, Math.Min(5, RetentionMinutes));
            VolumePercent = Math.Max(0, Math.Min(200, VolumePercent));
            SpeedPercent = Math.Max(100, Math.Min(200, SpeedPercent));
            CoordRetentionMinutes = Math.Max(1, Math.Min(10, CoordRetentionMinutes));
            MediaResumeSeconds = Math.Max(1, Math.Min(ResumeNever, MediaResumeSeconds));
            HandoffRepeatSeconds = Math.Max(RepeatOff, Math.Min(10, HandoffRepeatSeconds));
            ReplayBinding = ReplayBinding ?? "";
            CoordReplayBinding = CoordReplayBinding ?? "";
        }

        public static ReplaySettings Load(string path = null)
        {
            path = path ?? DefaultPath;
            try
            {
                if (File.Exists(path))
                {
                    using (var s = File.OpenRead(path))
                    {
                        var loaded = (ReplaySettings)new XmlSerializer(typeof(ReplaySettings)).Deserialize(s);
                        loaded.Normalise();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Warn("settings load failed: " + ex.GetType().Name);
            }
            return new ReplaySettings();
        }

        public void Save(string path = null)
        {
            path = path ?? DefaultPath;
            try
            {
                Normalise();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                using (var s = File.Create(tmp))
                {
                    new XmlSerializer(typeof(ReplaySettings)).Serialize(s, this);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Diag.UserError("Could not save Say Again settings.", ex);
            }
        }
    }
}
