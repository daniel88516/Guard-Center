using System;
using System.Collections.Generic;
using System.Text;

namespace GuardCenter
{
    internal static class FocusedRefreshPolicy
    {
        public const int IntervalMilliseconds = 1000;

        public static bool ShouldPoll(bool isVisible, bool isActive, bool isMinimized)
        {
            return isVisible && isActive && !isMinimized;
        }

        public static bool ShouldRefreshDisplayOnFocus(bool isVisible, bool isActive,
            bool isMinimized, bool isDisplayPage)
        {
            return isDisplayPage && ShouldPoll(isVisible, isActive, isMinimized);
        }

        public static bool ShouldRefreshDisplayOnNavigation(bool wasDisplayPage, bool isDisplayPage)
        {
            return !wasDisplayPage && isDisplayPage;
        }

        public static string GetAudioFingerprint(IList<AudioAppVolume> apps)
        {
            var records = new List<string>();
            if (apps != null)
            {
                for (int i = 0; i < apps.Count; i++)
                {
                    AudioAppVolume app = apps[i];
                    if (app == null)
                    {
                        continue;
                    }

                    var record = new StringBuilder();
                    Append(record, app.Key);
                    Append(record, app.Name);
                    Append(record, app.Description);
                    Append(record, app.IconPath);
                    Append(record, app.IsSystemSounds);
                    Append(record, app.VolumePercent);
                    Append(record, app.Muted);
                    Append(record, app.Active);
                    records.Add(record.ToString());
                }
            }

            records.Sort(StringComparer.Ordinal);
            return string.Join("|", records.ToArray());
        }

        public static string GetDisplayFingerprint(IList<DisplayGuardMonitorInfo> monitors)
        {
            var records = new List<string>();
            if (monitors != null)
            {
                for (int i = 0; i < monitors.Count; i++)
                {
                    DisplayGuardMonitorInfo monitor = monitors[i];
                    if (monitor == null)
                    {
                        continue;
                    }

                    var record = new StringBuilder();
                    Append(record, monitor.RuntimeId);
                    Append(record, monitor.StableId);
                    Append(record, monitor.StableIdReliable);
                    Append(record, monitor.DisplayName);
                    Append(record, monitor.SourceName);
                    Append(record, monitor.IsPrimary);
                    Append(record, monitor.ControlKind);
                    Append(record, monitor.SupportsBrightness);
                    Append(record, monitor.SupportsContrast);
                    Append(record, monitor.BrightnessPercent);
                    Append(record, monitor.ContrastPercent);
                    Append(record, monitor.BrightnessMinimum);
                    Append(record, monitor.BrightnessMaximum);
                    Append(record, monitor.ContrastMinimum);
                    Append(record, monitor.ContrastMaximum);
                    Append(record, monitor.LastError);
                    Append(record, monitor.IsBusy);
                    records.Add(record.ToString());
                }
            }

            records.Sort(StringComparer.Ordinal);
            return string.Join("|", records.ToArray());
        }

        private static void Append(StringBuilder builder, string value)
        {
            string safe = value ?? string.Empty;
            builder.Append(safe.Length).Append(':').Append(safe).Append(';');
        }

        private static void Append(StringBuilder builder, bool value)
        {
            builder.Append(value ? '1' : '0').Append(';');
        }

        private static void Append(StringBuilder builder, int value)
        {
            builder.Append(value).Append(';');
        }
    }
}
