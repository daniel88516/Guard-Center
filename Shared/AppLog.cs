using System;
using System.IO;
using System.Text;

namespace GuardCenter
{
    internal static class AppLog
    {
        private static readonly object Sync = new object();

        public static void Write(string area, string message)
        {
            try
            {
                AppPaths.EnsureRoot();
                string safeArea = string.IsNullOrWhiteSpace(area) ? "Guard Center" : area.Trim();
                string safeMessage = message ?? string.Empty;
                lock (Sync)
                {
                    File.AppendAllText(AppPaths.LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + safeArea + "] "
                        + safeMessage + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never make a system operation fail.
            }
        }
    }
}
