using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace GuardCenter
{
    internal enum DisplayGuardFeature
    {
        Brightness,
        Contrast
    }

    internal sealed class DisplayGuardModeDefinition
    {
        public readonly string Key;
        public readonly string Name;
        public readonly int BrightnessDelta;
        public readonly int ContrastDelta;

        public DisplayGuardModeDefinition(string key, string name, int brightnessDelta, int contrastDelta)
        {
            Key = key;
            Name = name;
            BrightnessDelta = brightnessDelta;
            ContrastDelta = contrastDelta;
        }
    }

    internal static class DisplayGuardModes
    {
        public const string Standard = "standard";
        public const string Reading = "reading";
        public const string Scenery = "scenery";
        public const string Movie = "movie";
        public const string Game = "game";
        public const string Custom = "custom";
        public const string Live = "live";

        private static readonly DisplayGuardModeDefinition[] definitions =
        {
            new DisplayGuardModeDefinition(Standard, "Standard", 0, 0),
            new DisplayGuardModeDefinition(Reading, "Reading", -20, -5),
            new DisplayGuardModeDefinition(Scenery, "Scenery", 10, 8),
            new DisplayGuardModeDefinition(Movie, "Movie", -15, 8),
            new DisplayGuardModeDefinition(Game, "Game", 15, 5),
            new DisplayGuardModeDefinition(Custom, "Custom", 0, 0),
            new DisplayGuardModeDefinition(Live, "Live", 0, 0)
        };

        public static DisplayGuardModeDefinition[] All
        {
            get { return (DisplayGuardModeDefinition[])definitions.Clone(); }
        }

        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return Standard;
            }

            string normalized = key.Trim().ToLowerInvariant();
            for (int i = 0; i < definitions.Length; i++)
            {
                if (string.Equals(definitions[i].Key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return definitions[i].Key;
                }
            }

            return Standard;
        }

        public static DisplayGuardModeDefinition Get(string key)
        {
            string normalized = NormalizeKey(key);
            for (int i = 0; i < definitions.Length; i++)
            {
                if (string.Equals(definitions[i].Key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return definitions[i];
                }
            }

            return definitions[0];
        }
    }

    internal sealed class DisplayGuardMonitorInfo
    {
        public string RuntimeId = string.Empty;
        public string StableId = string.Empty;
        public bool StableIdReliable;
        public string DisplayName = string.Empty;
        public string SourceName = string.Empty;
        public bool IsPrimary;
        public string ControlKind = string.Empty;
        public bool SupportsBrightness;
        public bool SupportsContrast;
        public int BrightnessPercent;
        public int ContrastPercent;
        public int BrightnessMinimum;
        public int BrightnessMaximum;
        public int ContrastMinimum;
        public int ContrastMaximum;
        public string LastError = string.Empty;
        public bool IsBusy;

        public bool IsControllable
        {
            get { return SupportsBrightness || SupportsContrast; }
        }

        public DisplayGuardMonitorInfo Clone()
        {
            return new DisplayGuardMonitorInfo
            {
                RuntimeId = RuntimeId,
                StableId = StableId,
                StableIdReliable = StableIdReliable,
                DisplayName = DisplayName,
                SourceName = SourceName,
                IsPrimary = IsPrimary,
                ControlKind = ControlKind,
                SupportsBrightness = SupportsBrightness,
                SupportsContrast = SupportsContrast,
                BrightnessPercent = BrightnessPercent,
                ContrastPercent = ContrastPercent,
                BrightnessMinimum = BrightnessMinimum,
                BrightnessMaximum = BrightnessMaximum,
                ContrastMinimum = ContrastMinimum,
                ContrastMaximum = ContrastMaximum,
                LastError = LastError,
                IsBusy = IsBusy
            };
        }
    }

    internal sealed class DisplayGuardProfileStore
    {
        public int Version = 1;
        public List<DisplayGuardModeProfile> Modes = new List<DisplayGuardModeProfile>();
    }

    internal sealed class DisplayGuardModeProfile
    {
        public string Key = string.Empty;
        public List<DisplayGuardMonitorProfile> Monitors = new List<DisplayGuardMonitorProfile>();
    }

    internal sealed class DisplayGuardMonitorProfile
    {
        public string StableId = string.Empty;
        public string Name = string.Empty;
        public bool HasBrightness;
        public int BrightnessPercent;
        public bool HasContrast;
        public int ContrastPercent;
    }

    internal sealed class DisplayGuardApplyPlan
    {
        public readonly List<DisplayGuardApplyPlanItem> Items = new List<DisplayGuardApplyPlanItem>();
        public int OfflineProfileCount;
        public int NewMonitorCount;
        public int UnmatchedMonitorCount;
        public int UnsupportedFeatureCount;
    }

    internal sealed class DisplayGuardApplyPlanItem
    {
        public string RuntimeId = string.Empty;
        public string DisplayName = string.Empty;
        public bool HasBrightness;
        public int BrightnessPercent;
        public bool BrightnessClamped;
        public bool HasContrast;
        public int ContrastPercent;
        public bool ContrastClamped;
    }

    internal static class DisplayGuardProfileCodec
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        public static DisplayGuardProfileStore Decode(string encoded, out bool hadError)
        {
            hadError = false;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return new DisplayGuardProfileStore();
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                string json = Encoding.UTF8.GetString(bytes);
                DisplayGuardProfileStore store = JsonSerializer.Deserialize<DisplayGuardProfileStore>(json, JsonOptions);
                if (store == null)
                {
                    hadError = true;
                    return new DisplayGuardProfileStore();
                }

                NormalizeStore(store);
                return store;
            }
            catch
            {
                hadError = true;
                return new DisplayGuardProfileStore();
            }
        }

        public static string Encode(DisplayGuardProfileStore store)
        {
            if (store == null)
            {
                store = new DisplayGuardProfileStore();
            }

            NormalizeStore(store);
            string json = JsonSerializer.Serialize(store, JsonOptions);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static void NormalizeStore(DisplayGuardProfileStore store)
        {
            if (store.Modes == null)
            {
                store.Modes = new List<DisplayGuardModeProfile>();
            }

            store.Version = 1;
            DisplayGuardModeDefinition[] modes = DisplayGuardModes.All;
            for (int i = 0; i < modes.Length; i++)
            {
                DisplayGuardModeProfile profile = FindMode(store, modes[i].Key);
                if (profile == null)
                {
                    profile = new DisplayGuardModeProfile { Key = modes[i].Key };
                    store.Modes.Add(profile);
                }

                profile.Key = DisplayGuardModes.NormalizeKey(profile.Key);
                if (profile.Monitors == null)
                {
                    profile.Monitors = new List<DisplayGuardMonitorProfile>();
                }
            }
        }

        public static DisplayGuardModeProfile FindMode(DisplayGuardProfileStore store, string key)
        {
            if (store == null || store.Modes == null)
            {
                return null;
            }

            string normalized = DisplayGuardModes.NormalizeKey(key);
            for (int i = 0; i < store.Modes.Count; i++)
            {
                if (string.Equals(store.Modes[i].Key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return store.Modes[i];
                }
            }

            return null;
        }
    }

    internal static class DisplayGuardModeService
    {
        public static bool EnsureInitialProfiles(DisplayGuardProfileStore store, List<DisplayGuardMonitorInfo> monitors)
        {
            bool changed = false;
            if (store == null)
            {
                return false;
            }

            DisplayGuardProfileCodec.NormalizeStore(store);
            DisplayGuardModeDefinition[] modes = DisplayGuardModes.All;
            for (int i = 0; i < modes.Length; i++)
            {
                DisplayGuardModeProfile mode = DisplayGuardProfileCodec.FindMode(store, modes[i].Key);
                if (mode == null)
                {
                    continue;
                }

                for (int j = 0; j < monitors.Count; j++)
                {
                    DisplayGuardMonitorInfo monitor = monitors[j];
                    if (!CanPersist(monitor) || FindMonitor(mode, monitor.StableId) != null)
                    {
                        continue;
                    }

                    mode.Monitors.Add(CreateProfileFromMonitor(monitor, modes[i]));
                    changed = true;
                }
            }

            return changed;
        }

        public static bool CaptureCurrent(DisplayGuardProfileStore store, string modeKey,
            List<DisplayGuardMonitorInfo> monitors, out int savedCount, out int skippedCount)
        {
            savedCount = 0;
            skippedCount = 0;
            if (store == null)
            {
                return false;
            }

            DisplayGuardProfileCodec.NormalizeStore(store);
            DisplayGuardModeProfile mode = DisplayGuardProfileCodec.FindMode(store, modeKey);
            if (mode == null)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < monitors.Count; i++)
            {
                DisplayGuardMonitorInfo monitor = monitors[i];
                if (!CanPersist(monitor))
                {
                    skippedCount++;
                    continue;
                }

                DisplayGuardMonitorProfile profile = FindMonitor(mode, monitor.StableId);
                if (profile == null)
                {
                    profile = new DisplayGuardMonitorProfile();
                    mode.Monitors.Add(profile);
                }

                ApplyMonitorToProfile(profile, monitor, null);
                savedCount++;
                changed = true;
            }

            return changed;
        }

        public static bool CaptureAdjustment(DisplayGuardProfileStore store, string modeKey,
            List<DisplayGuardMonitorInfo> monitors, string runtimeId, DisplayGuardFeature feature, int percent)
        {
            if (store == null || monitors == null || string.IsNullOrWhiteSpace(runtimeId))
            {
                return false;
            }

            DisplayGuardProfileCodec.NormalizeStore(store);
            DisplayGuardModeProfile mode = DisplayGuardProfileCodec.FindMode(store, modeKey);
            if (mode == null)
            {
                return false;
            }

            for (int i = 0; i < monitors.Count; i++)
            {
                DisplayGuardMonitorInfo monitor = monitors[i];
                if (!string.Equals(monitor.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase)
                    || !CanPersist(monitor))
                {
                    continue;
                }

                DisplayGuardMonitorProfile profile = FindMonitor(mode, monitor.StableId);
                if (profile == null)
                {
                    profile = CreateProfileFromMonitor(monitor, null);
                    mode.Monitors.Add(profile);
                }

                percent = ClampPercent(percent);
                if (feature == DisplayGuardFeature.Brightness && monitor.SupportsBrightness)
                {
                    profile.HasBrightness = true;
                    profile.BrightnessPercent = percent;
                    return true;
                }

                if (feature == DisplayGuardFeature.Contrast && monitor.SupportsContrast)
                {
                    profile.HasContrast = true;
                    profile.ContrastPercent = percent;
                    return true;
                }

                return false;
            }

            return false;
        }

        public static DisplayGuardApplyPlan BuildApplyPlan(DisplayGuardProfileStore store, string modeKey,
            List<DisplayGuardMonitorInfo> monitors)
        {
            var plan = new DisplayGuardApplyPlan();
            if (store == null)
            {
                return plan;
            }

            DisplayGuardModeProfile mode = DisplayGuardProfileCodec.FindMode(store, modeKey);
            if (mode == null)
            {
                return plan;
            }

            var onlineStableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < monitors.Count; i++)
            {
                DisplayGuardMonitorInfo monitor = monitors[i];
                if (!monitor.StableIdReliable || string.IsNullOrWhiteSpace(monitor.StableId))
                {
                    if (monitor.IsControllable)
                    {
                        plan.UnmatchedMonitorCount++;
                    }
                    continue;
                }

                onlineStableIds.Add(monitor.StableId);
                DisplayGuardMonitorProfile profile = FindMonitor(mode, monitor.StableId);
                if (profile == null)
                {
                    if (monitor.IsControllable)
                    {
                        plan.NewMonitorCount++;
                    }
                    continue;
                }

                var item = new DisplayGuardApplyPlanItem
                {
                    RuntimeId = monitor.RuntimeId,
                    DisplayName = monitor.DisplayName
                };

                if (profile.HasBrightness)
                {
                    if (monitor.SupportsBrightness)
                    {
                        item.HasBrightness = true;
                        item.BrightnessPercent = ClampPercent(profile.BrightnessPercent);
                        item.BrightnessClamped = item.BrightnessPercent != profile.BrightnessPercent;
                    }
                    else
                    {
                        plan.UnsupportedFeatureCount++;
                    }
                }

                if (profile.HasContrast)
                {
                    if (monitor.SupportsContrast)
                    {
                        item.HasContrast = true;
                        item.ContrastPercent = ClampPercent(profile.ContrastPercent);
                        item.ContrastClamped = item.ContrastPercent != profile.ContrastPercent;
                    }
                    else
                    {
                        plan.UnsupportedFeatureCount++;
                    }
                }

                if (item.HasBrightness || item.HasContrast)
                {
                    plan.Items.Add(item);
                }
            }

            for (int i = 0; i < mode.Monitors.Count; i++)
            {
                DisplayGuardMonitorProfile profile = mode.Monitors[i];
                if (!string.IsNullOrWhiteSpace(profile.StableId) && !onlineStableIds.Contains(profile.StableId))
                {
                    plan.OfflineProfileCount++;
                }
            }

            return plan;
        }

        private static bool CanPersist(DisplayGuardMonitorInfo monitor)
        {
            return monitor != null
                && monitor.StableIdReliable
                && !string.IsNullOrWhiteSpace(monitor.StableId)
                && monitor.IsControllable;
        }

        private static DisplayGuardMonitorProfile CreateProfileFromMonitor(DisplayGuardMonitorInfo monitor,
            DisplayGuardModeDefinition mode)
        {
            var profile = new DisplayGuardMonitorProfile();
            ApplyMonitorToProfile(profile, monitor, mode);
            return profile;
        }

        private static void ApplyMonitorToProfile(DisplayGuardMonitorProfile profile, DisplayGuardMonitorInfo monitor,
            DisplayGuardModeDefinition mode)
        {
            profile.StableId = monitor.StableId;
            profile.Name = monitor.DisplayName;
            profile.HasBrightness = monitor.SupportsBrightness;
            profile.HasContrast = monitor.SupportsContrast;
            if (monitor.SupportsBrightness)
            {
                int delta = mode == null ? 0 : mode.BrightnessDelta;
                profile.BrightnessPercent = ClampPercent(monitor.BrightnessPercent + delta);
            }

            if (monitor.SupportsContrast)
            {
                int delta = mode == null ? 0 : mode.ContrastDelta;
                profile.ContrastPercent = ClampPercent(monitor.ContrastPercent + delta);
            }
        }

        private static DisplayGuardMonitorProfile FindMonitor(DisplayGuardModeProfile mode, string stableId)
        {
            if (mode == null || mode.Monitors == null || string.IsNullOrWhiteSpace(stableId))
            {
                return null;
            }

            for (int i = 0; i < mode.Monitors.Count; i++)
            {
                if (string.Equals(mode.Monitors[i].StableId, stableId, StringComparison.OrdinalIgnoreCase))
                {
                    return mode.Monitors[i];
                }
            }

            return null;
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }
    }

    internal static class DisplayGuardIdentity
    {
        public static string NormalizeStableId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Trim();
            normalized = normalized.Replace('/', '\\');
            normalized = normalized.Replace('#', '\\');
            if (normalized.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(4);
            }

            int guidIndex = normalized.IndexOf("\\{", StringComparison.Ordinal);
            if (guidIndex >= 0)
            {
                normalized = normalized.Substring(0, guidIndex);
            }

            if (normalized.EndsWith("_0", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - 2);
            }

            normalized = normalized.Trim('\\').ToUpperInvariant();
            return normalized;
        }
    }
}
