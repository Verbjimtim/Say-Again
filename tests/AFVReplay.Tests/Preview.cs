using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AFVReplay.Audio;
using AFVReplay.UI;
using AFVReplay.VatSys;

namespace AFVReplay.Tests
{
    /// <summary>
    /// Renders the AFV Replay window offscreen (no focus taken) with sample data and saves a PNG,
    /// using vatSys's real window class, controls, fonts and the profile's colours.
    /// Reads Colours.xml only; vatSys settings are changed in memory and never saved.
    /// </summary>
    internal static class Preview
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        private static ListView FindList(Control c)
        {
            if (c is ListView lv) return lv;
            foreach (Control child in c.Controls) { var f = FindList(child); if (f != null) return f; }
            return null;
        }

        /// <summary>Loads the profile's colours into vatSys (settings changed in memory only, never saved).</summary>
        internal static void LoadProfileColours(string profileDir)
        {
            var asm = typeof(vatsys.AFV).Assembly;
            var settingsType = asm.GetType("vatsys.Properties.Settings");
            var settings = settingsType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            ((System.Configuration.ApplicationSettingsBase)settings)["DatasetPath"] = profileDir;
            typeof(vatsys.Colours).GetMethod("LoadColours", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }

        public static int Run(string outPng, string profileDir)
        {
            Console.WriteLine("The AFV Replay window has moved into the VSCS: use --preview-vscs.");
            return 0;
        }

        /// <summary>Renders vatSys's real VSCS window with the Replay button, idle and playing.</summary>
        public static int RunVscs(string outPrefix, string profileDir)
        {
            var asm = typeof(vatsys.AFV).Assembly;
            var settingsType = asm.GetType("vatsys.Properties.Settings");
            var settings = settingsType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            ((System.Configuration.ApplicationSettingsBase)settings)["DatasetPath"] = profileDir; // in memory only
            typeof(vatsys.Colours).GetMethod("LoadColours", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

            var plugin = new ReplayPlugin(new ReplaySettings { SpeedPercent = 100 });
            var now = DateTime.UtcNow;
            var pcm = new short[48000 * 3];
            Clip F(string who, double ago, bool own = false) => new Clip(plugin.Store.NextId(), now.AddSeconds(-ago - 3), now.AddSeconds(-ago), 118_500_000, "Gingin T 118.500",
                new[] { who }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length, own);
            Clip C(string[] who, double ago, string line = "Hotline") => new Clip(plugin.CoordStore.NextId(), now.AddSeconds(-ago - 3), now.AddSeconds(-ago), 0, line,
                who, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length);
            plugin.CoordStore.Add(C(new[] { "GIG_GND" }, 20));
            plugin.CoordStore.Add(C(new[] { "ML-BIK_CTR", "You" }, 70, "Coldline"));
            plugin.Store.Add(F("QFA123", 75));
            plugin.Store.Add(F("GIG_TWR", 130, own: true));
            plugin.Store.Add(F("QFA123", 140));
            plugin.CoordStore.Add(C(new[] { "GIG_GND", "You" }, 200, "Coord"));
            plugin.Store.Add(F("VOZ456", 250));

            var vscs = (Form)Activator.CreateInstance(asm.GetType("vatsys.VSCSWindow"), true);
            typeof(vatsys.MMI).GetField("VSCSWindow", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SetValue(null, vscs);
            vscs.StartPosition = FormStartPosition.Manual;
            vscs.Location = new Point(-4000, -4000);
            vscs.ShowInTaskbar = false;
            vscs.Show();
            using (var injector = new VscsInjector(plugin))
            {
                injector.Start();
                Pump(10);
                // Say Again under Group; the VSCS window grows to its new minimum height by itself.
                int before = vscs.Height;
                VatSysBridge.SetVscsPage(VatSysBridge.VscsPanel, "Setup");
                injector.Sync(); Pump(15);
                Console.WriteLine($"VSCS window height {before} -> {vscs.Height}, minimum {vscs.MinimumSize.Height}, panel {VatSysBridge.VscsPanel.Height}");
                Capture(vscs, outPrefix + "-1-menu.png");
                vscs.Height = 200; Pump(10); // try to make it shorter than the minimum
                Console.WriteLine($"after shrinking: {vscs.Height}");
                injector.OpenPage(); Pump(15);
                if (injector.LastError != null) Console.WriteLine("VSCS page error: " + injector.LastError);
                Console.WriteLine($"page open: {injector.PageOpen}, vatSys page underneath: {VatSysBridge.VscsPageName(VatSysBridge.VscsPanel)}");
                Capture(vscs, outPrefix + "-3-page.png");
                injector.Page.Playlist.Older(null); injector.Page.Playlist.Older(null); injector.Page.Playlist.Older(null);
                { var buf = new float[48000]; plugin.Player.Read(buf, 0, buf.Length); } // 1 s into the call
                injector.Page.RefreshView(); Pump(15);
                Capture(vscs, outPrefix + "-4-playing.png");
                injector.Page.Playlist.PlayPause(null);
                injector.Page.ShowSettings(true); Pump(15);
                Capture(vscs, outPrefix + "-5-settings.png");
                injector.Page.ShowSettings(false);
                injector.Page.Playlist.PlayPause(null);
                Pump(10); injector.Sync(); injector.Sync();
                Console.WriteLine($"still open after a while on its own: {injector.PageOpen}");
                VatSysBridge.SetVscsPage(VatSysBridge.VscsPanel, "Coord"); // user clicks Coord
                injector.Sync();
                Console.WriteLine($"open after vatSys switched to Coord: {injector.PageOpen}");
                VatSysBridge.SetVscsPage(VatSysBridge.VscsPanel, "Setup");
                injector.Sync(); Pump(15);
                Capture(vscs, outPrefix + "-6-playing-from-setup.png");
                plugin.StopAll();

                // Very long callsign and name: how the call-outs, list and status button cope.
                const string longCs = "QLK1234567890X";
                AFVReplay.UI.SayAgainPage.PilotOverride = () => (longCs, "Bartholomew Maximilian Featherstonehaugh-Smythe III");
                foreach (var ago in new[] { 5.0, 40, 95 })
                    plugin.Store.Add(new Clip(plugin.Store.NextId(), DateTime.UtcNow.AddSeconds(-ago - 3), DateTime.UtcNow.AddSeconds(-ago), 132_000_000,
                        "Melbourne Centre 132.000", new[] { longCs }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length));
                plugin.CoordStore.Add(new Clip(plugin.CoordStore.NextId(), DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow.AddSeconds(-1), 0, "Coord",
                    new[] { "ML-BIK_CTR_LONGNAME", "You" }, false, OpusCodec.Encode(pcm, pcm.Length), pcm.Length));
                injector.OpenPage(); injector.Page.RefreshView(); Pump(15);
                Capture(vscs, outPrefix + "-7-long-selected.png");
                injector.Page.Playlist.PlayPause(null);
                { var buf = new float[24000]; plugin.Player.Read(buf, 0, buf.Length); }
                injector.Page.RefreshView(); Pump(15);
                Capture(vscs, outPrefix + "-8-long-playing.png");
                AFVReplay.UI.SayAgainPage.PilotOverride = null;
                injector.Page.RefreshView(); Pump(15);
                Capture(vscs, outPrefix + "-9-long-unfiltered.png");
                plugin.StopAll();
            }
            vscs.Close();
            Console.WriteLine("vscs previews saved to " + outPrefix + "-*.png");
            return 0;
        }

        private static void Pump(int n)
        {
            for (int i = 0; i < n; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
        }

        private static void Capture(Form w, string png)
        {
            using (var bmp = new Bitmap(w.Width, w.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    PrintWindow(w.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(png, ImageFormat.Png);
            }
        }
    }
}
