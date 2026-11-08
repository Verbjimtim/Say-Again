using System;

namespace AFVReplay
{
    /// <summary>
    /// Diagnostics. Deliberately never given callsigns, frequencies or audio: privacy matters more than debug detail.
    /// </summary>
    internal static class Diag
    {
        /// <summary>Also write diagnostics to the console (test harness only).</summary>
        internal static bool Echo;

        public static void Info(string message) => Write("[AFVReplay] " + message);

        public static void Warn(string message) => Write("[AFVReplay] WARN " + message);

        private static void Write(string line)
        {
            System.Diagnostics.Debug.WriteLine(line);
            if (Echo) Console.WriteLine("     " + line);
        }

        /// <summary>Shows an error in vatSys's own error window.</summary>
        public static void UserError(string message, Exception ex = null)
        {
            Warn(message + (ex != null ? " (" + ex.GetType().Name + ": " + ex.Message + ")" : ""));
            try
            {
                vatsys.Errors.Add(new Exception(message, ex), "Say Again");
            }
            catch
            {
                // Not running inside vatSys (tests), or vatSys is shutting down.
            }
        }
    }
}
