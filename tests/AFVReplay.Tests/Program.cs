using System.Linq;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AFVReplay.Tests
{
    /// <summary>
    /// Offline test harness. Loads vatSys's own libraries from the install folder and drives the real
    /// AFV receive pipeline (no network, no audio device, vatSys itself is not started).
    /// </summary>
    internal static class Program
    {
        internal static string VatSysBin = @"C:\Program Files (x86)\vatSys\bin";

        [STAThread]
        private static int Main(string[] args)
        {
            // Never show WinForms error dialogs on the user's screen: log them instead.
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += (s, e) => Console.WriteLine("UI EXCEPTION: " + e.Exception);
            bool preview = args.Length >= 3 && (args[0] == "--preview" || args[0] == "--preview-vscs");
            bool soak = args.Length >= 2 && args[0] == "--soak-duck";
            if (args.Length >= 1 && args[0] == "--check-duck") return DuckSoak.Check();
            if (args.Length >= 1 && args[0] == "--check-duck-discord") return DuckSoak.CheckDiscord();
            if (args.Length >= 1 && args[0] == "--check-foreground") return DuckSoak.CheckForeground();
            if (args.Length >= 1 && args[0] == "--check-ducker") return DuckSoak.CheckDucker();
            if (args.Length >= 1 && args[0] == "--check-sta-held") return DuckSoak.CheckStaHeld();
            if (args.Length >= 2 && args[0] == "--debug-capture") return DebugCapture.Run(int.Parse(args[1]));
            if (args.Length >= 2 && args[0] == "--watch-discord") return DuckSoak.WatchDiscord(int.Parse(args[1]));
            if (args.Length >= 1 && args[0] == "--check-media-grab") return DuckSoak.CheckMediaGrab();
            if (args.Length >= 1 && args[0] == "--check-media-foreground") return DuckSoak.CheckMediaForeground();
            if (!preview && !soak && args.Length > 0 && !args[0].StartsWith("--")) VatSysBin = args[0];
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                foreach (var ext in new[] { ".dll", ".exe" })
                {
                    var path = Path.Combine(VatSysBin, name + ext);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            if (soak) return DuckSoak.Run(int.Parse(args[1]));
            if (preview && args[0] == "--preview-vscs") return RunPreviewVscs(args[1], args[2]);
            return preview ? RunPreview(args[1], args[2]) : Run();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run() => Tests.RunAll();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunPreview(string png, string profile) => Preview.Run(png, profile);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunPreviewVscs(string prefix, string profile) => Preview.RunVscs(prefix, profile);
    }
}
