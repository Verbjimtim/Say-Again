using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AFVReplay.VatSys;

namespace AFVReplay.Handoff
{
    /// <summary>
    /// Handoff Alert's vatSys side: four times a second, on vatSys's UI thread (where its flight plans and
    /// tracks can safely be read), feeds every plan's handoff state to <see cref="HandoffTracker"/> and plays
    /// what it reports. Refusals come from vatSys's "Handoff refused by ..." error.
    /// </summary>
    internal sealed class HandoffMonitor : IDisposable
    {
        private readonly HandoffTracker tracker = new HandoffTracker();
        private readonly AlertPlayer player;
        private readonly Func<bool> enabled;
        private readonly Func<TimeSpan?> repeat;
        private readonly Func<IEnumerable<TrackSnapshot>> snapshots;
        private readonly Timer timer = new Timer { Interval = 250 };
        private readonly DateTime started = DateTime.UtcNow;
        private bool failed, checkedForOldPlugin;

        public HandoffMonitor(AlertPlayer player, Func<bool> enabled, Func<TimeSpan?> repeat, Func<IEnumerable<TrackSnapshot>> snapshots = null)
        {
            this.player = player;
            this.enabled = enabled;
            this.repeat = repeat;
            this.snapshots = snapshots ?? VatSysBridge.GetHandoffSnapshots;
            timer.Tick += (s, e) => Tick();
        }

        public int Waiting => tracker.Waiting;

        public void Start()
        {
            vatsys.Errors.ErrorsChanged += OnErrorsChanged;
            timer.Start();
        }

        internal void Tick()
        {
            if (failed) return;
            try
            {
                CheckForOldPlugin();
                foreach (var e in tracker.Update(snapshots(), DateTime.UtcNow, enabled(), repeat()))
                    player.Play(e == HandoffEvent.Accepted ? AlertTone.Accepted : AlertTone.Incoming);
            }
            catch (Exception ex)
            {
                // Never let it affect vatSys or the rest of Say Again.
                failed = true;
                timer.Stop();
                Diag.UserError("Say Again: handoff alerts stopped working and have been switched off until vatSys restarts.", ex);
            }
        }

        private void OnErrorsChanged(object sender, vatsys.Errors.ErrorArgs e)
        {
            try
            {
                if (!failed && !e.Removed && HandoffTracker.IsRefusal(e.Error?.Message) && enabled())
                    player.Play(AlertTone.Refused);
            }
            catch (Exception ex)
            {
                Diag.Warn("handoff refusal check failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Matt's separate Handoff Alert plugin plays the same tones, so with both you'd hear each twice.
        /// Says so once (vatSys may load it a moment after Say Again, so this waits a little first).
        /// Nothing in Say Again is switched off.
        /// </summary>
        private void CheckForOldPlugin()
        {
            if (checkedForOldPlugin || DateTime.UtcNow - started < TimeSpan.FromSeconds(10)) return;
            checkedForOldPlugin = true;
            if (VatSysBridge.HandoffAlertPluginLoaded())
                Diag.UserError("Say Again now includes Handoff Alert. The separate Handoff Alert plugin is also installed, so with both on you'll hear each handoff tone twice. Remove HandoffAlertPlugin.dll from your Plugins folder.");
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            try { vatsys.Errors.ErrorsChanged -= OnErrorsChanged; } catch { }
        }
    }
}
