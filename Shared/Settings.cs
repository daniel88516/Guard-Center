using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GuardCenter
{
    internal sealed class AppSettings
    {
        public AudioZeroSettings Audio = new AudioZeroSettings();
        public AudioMixerSettings AudioMixer = new AudioMixerSettings();
        public DeviceGuardSettings DeviceGuard = new DeviceGuardSettings();
        public KeyboardGuardSettings Keyboard = new KeyboardGuardSettings();
        public GameHelperSettings GameHelper = new GameHelperSettings();
        public AppGuardSettings AppGuard = new AppGuardSettings();
        public LinkGuardSettings LinkGuard = new LinkGuardSettings();
        public DisplayGuardSettings DisplayGuard = new DisplayGuardSettings();
        public PowerGuardSettings PowerGuard = new PowerGuardSettings();
        public UacGuardSettings UacGuard = new UacGuardSettings();
        public AppearanceSettings Appearance = new AppearanceSettings();
        public UiLayoutSettings Layout = new UiLayoutSettings();
    }

    internal sealed class AudioZeroSettings
    {
        public bool Enabled = true;
        public bool ZeroOnEnable;
        public bool SetMute = true;
        public bool SetVolumeZero = true;
        public bool ReactToPropertyChanges = true;
        public int PollIntervalMs = 100;
        public string RetryDelaysCsv = "100,400,1000";

        public AudioZeroSettings Clone()
        {
            return new AudioZeroSettings
            {
                Enabled = Enabled,
                ZeroOnEnable = ZeroOnEnable,
                SetMute = SetMute,
                SetVolumeZero = SetVolumeZero,
                ReactToPropertyChanges = ReactToPropertyChanges,
                PollIntervalMs = PollIntervalMs,
                RetryDelaysCsv = RetryDelaysCsv
            };
        }
    }

    internal sealed class UiLayoutSettings
    {
        public int SidebarWidth = 350;
        public int ContentWidth = 900;
        public int UiScalePercent = 100;
        public string ModuleOrder = ModuleNavigationOrder.Default;
    }

    internal sealed class AppearanceSettings
    {
        public string CustomIconPath = string.Empty;
    }

    internal sealed class UacGuardSettings
    {
        public string AuthorizationMode = UacGuardModule.CodexProcessMode;
        public bool AutomaticAuthorizationPaused;
    }

    internal static class ModuleNavigationOrder
    {
        public const string Audio = "audio";
        public const string Device = "device";
        public const string Keyboard = "keyboard";
        public const string GameHelper = "game-helper";
        public const string AppGuard = "app-guard";
        public const string LinkGuard = "link-guard";
        public const string DisplayGuard = "display-guard";
        public const string VsrGuard = "vsr-guard";
        public const string PowerGuard = "power-guard";
        public const string UacGuard = "uac-guard";
        public const string Default = Audio + "," + Device + "," + Keyboard + "," + GameHelper + ","
            + AppGuard + "," + LinkGuard + "," + DisplayGuard + "," + VsrGuard + "," + PowerGuard + "," + UacGuard;

        private static readonly string[] knownIds =
        {
            Audio,
            Device,
            Keyboard,
            GameHelper,
            AppGuard,
            LinkGuard,
            DisplayGuard,
            VsrGuard,
            PowerGuard,
            UacGuard
        };

        public static List<string> Parse(string csv)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] values = (csv ?? string.Empty).Split(',');
            for (int i = 0; i < values.Length; i++)
            {
                string id = values[i].Trim();
                if (IsKnown(id) && seen.Add(id))
                {
                    result.Add(id.ToLowerInvariant());
                }
            }

            for (int i = 0; i < knownIds.Length; i++)
            {
                if (string.Equals(knownIds[i], LinkGuard, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(knownIds[i], VsrGuard, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (seen.Add(knownIds[i]))
                {
                    result.Add(knownIds[i]);
                }
            }
            if (seen.Add(LinkGuard))
            {
                int appGuardIndex = result.FindIndex(delegate(string id)
                {
                    return string.Equals(id, AppGuard, StringComparison.OrdinalIgnoreCase);
                });
                result.Insert(appGuardIndex < 0 ? result.Count : appGuardIndex + 1, LinkGuard);
            }
            if (seen.Add(VsrGuard))
            {
                int displayGuardIndex = result.FindIndex(delegate(string id)
                {
                    return string.Equals(id, DisplayGuard, StringComparison.OrdinalIgnoreCase);
                });
                result.Insert(displayGuardIndex < 0 ? result.Count : displayGuardIndex + 1, VsrGuard);
            }
            return result;
        }

        public static string Normalize(string csv)
        {
            return string.Join(",", Parse(csv).ToArray());
        }

        public static int GetInsertionIndex(double pointerY, IList<double> itemCenters)
        {
            if (itemCenters == null)
            {
                return 0;
            }

            for (int i = 0; i < itemCenters.Count; i++)
            {
                if (pointerY < itemCenters[i])
                {
                    return i;
                }
            }
            return itemCenters.Count;
        }

        public static int GetAutoScrollDirection(double pointerY, double viewportHeight,
            double verticalOffset, double scrollableHeight, double threshold)
        {
            if (pointerY < threshold && verticalOffset > 0)
            {
                return -1;
            }
            if (pointerY > viewportHeight - threshold && verticalOffset < scrollableHeight)
            {
                return 1;
            }
            return 0;
        }

        private static bool IsKnown(string id)
        {
            for (int i = 0; i < knownIds.Length; i++)
            {
                if (string.Equals(id, knownIds[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal sealed class AudioMixerSettings
    {
        public int DefaultVolumePercent = 50;
        public string DefaultedAppKeys = string.Empty;
        public string ManualAppKeys = string.Empty;
        public ListPresentationSettings AppsList = new ListPresentationSettings();
    }

    internal sealed class DeviceGuardSettings
    {
        public ListPresentationSettings DetectableDevicesList = new ListPresentationSettings();
        public ListPresentationSettings CoreHardwareList = new ListPresentationSettings();
    }

    internal sealed class KeyboardGuardSettings
    {
        public bool DisableShiftSpaceWidthToggle = false;
        public bool ShiftSpaceMicrosoftBackupCaptured = false;
        public bool ShiftSpaceMicrosoftOriginalExists = false;
        public string ShiftSpaceMicrosoftOriginalValue = string.Empty;
        public bool ShiftSpaceAsusBackupCaptured = false;
        public bool ShiftSpaceAsusOriginalFullWidth = false;
        public bool ShiftSpaceImmBackupCaptured = false;
        public string ShiftSpaceImmBackup00000011 = string.Empty;
        public string ShiftSpaceImmBackup00000071 = string.Empty;
        // Retained for backward-compatible loading of settings written by older builds.
        public bool ShiftSpaceBackupCaptured = false;
        public string ShiftSpaceBackup00000011 = string.Empty;
        public string ShiftSpaceBackup00000071 = string.Empty;
        public bool DisableStickyKeysHotkey = false;
        public bool StickyKeysHotkeyBackupCaptured = false;
        public bool StickyKeysOriginalHotkeyActive = false;
    }

    internal sealed class GameHelperSettings
    {
        public bool BlockWindowsKey = true;
        public bool KeepEnhancedPointerPrecisionOff = false;
        public bool CrosshairEnabled = false;
        public int CrosshairSize = 14;
        public int CrosshairOpacityPercent = 100;
        public string CrosshairColor = "#48FF78";
        public string CrosshairCustomColor = "#FFFFFF";
        public string CrosshairStyle = "Classic";
        public bool CrosshairRestrictToSelectedApps = false;
        public string CrosshairSelectedApps = string.Empty;
        public string ProtectedApps = string.Empty;
        public ListPresentationSettings CrosshairAppsList = new ListPresentationSettings();
        public ListPresentationSettings AddAppsList = new ListPresentationSettings();
        public ListPresentationSettings ProtectedAppsList = new ListPresentationSettings();
    }

    internal sealed class AppGuardSettings
    {
        public bool ExplorerIntegrationEnabled = false;
        public ListPresentationSettings AppsList = new ListPresentationSettings();
    }

    internal sealed class LinkGuardSettings
    {
        public string Rules = string.Empty;
        public ListPresentationSettings AllAppsList = new ListPresentationSettings();
        public ListPresentationSettings LinkedAppsList = new ListPresentationSettings();
    }

    internal sealed class PowerGuardSettings
    {
        public string DurationKind = "TwoHours";
        public int CustomDurationMinutes = 120;
        public bool KeepDisplayOn;
    }

    internal sealed class DisplayGuardSettings
    {
        public string SelectedMode = "standard";
        public string Profiles = string.Empty;
        public ListPresentationSettings MonitorsList = new ListPresentationSettings();
    }

    internal sealed class SettingsStore
    {
        private readonly string path;

        public SettingsStore(string path)
        {
            this.path = path;
        }

        public AppSettings Load()
        {
            var settings = new AppSettings();
            if (!File.Exists(path))
            {
                Save(settings);
                return settings;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                {
                    continue;
                }

                int index = trimmed.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                values[trimmed.Substring(0, index).Trim()] = trimmed.Substring(index + 1).Trim();
            }

            settings.Audio.Enabled = GetBool(values, "Audio.Enabled", settings.Audio.Enabled);
            settings.Audio.ZeroOnEnable = GetBool(values, "Audio.ZeroOnEnable",
                settings.Audio.ZeroOnEnable);
            settings.Audio.SetMute = GetBool(values, "Audio.SetMute", settings.Audio.SetMute);
            settings.Audio.SetVolumeZero = GetBool(values, "Audio.SetVolumeZero", settings.Audio.SetVolumeZero);
            settings.Audio.ReactToPropertyChanges = GetBool(values, "Audio.ReactToPropertyChanges", settings.Audio.ReactToPropertyChanges);
            settings.Audio.PollIntervalMs = GetInt(values, "Audio.PollIntervalMs", settings.Audio.PollIntervalMs, 50, 5000);
            settings.Audio.RetryDelaysCsv = GetString(values, "Audio.RetryDelaysCsv", settings.Audio.RetryDelaysCsv);
            settings.AudioMixer.DefaultVolumePercent = GetInt(values, "AudioMixer.DefaultVolumePercent", settings.AudioMixer.DefaultVolumePercent, 0, 100);
            settings.AudioMixer.DefaultedAppKeys = GetString(values, "AudioMixer.DefaultedAppKeys", settings.AudioMixer.DefaultedAppKeys);
            settings.AudioMixer.ManualAppKeys = GetString(values, "AudioMixer.ManualAppKeys", settings.AudioMixer.ManualAppKeys);
            LoadListSettings(values, "AudioMixer.AppsList", settings.AudioMixer.AppsList);
            LoadListSettings(values, "DeviceGuard.DetectableDevicesList", settings.DeviceGuard.DetectableDevicesList);
            LoadListSettings(values, "DeviceGuard.CoreHardwareList", settings.DeviceGuard.CoreHardwareList);
            settings.Keyboard.DisableShiftSpaceWidthToggle = GetBool(values, "Keyboard.DisableShiftSpaceWidthToggle", settings.Keyboard.DisableShiftSpaceWidthToggle);
            settings.Keyboard.ShiftSpaceMicrosoftBackupCaptured = GetBool(values, "Keyboard.ShiftSpaceMicrosoftBackupCaptured", settings.Keyboard.ShiftSpaceMicrosoftBackupCaptured);
            settings.Keyboard.ShiftSpaceMicrosoftOriginalExists = GetBool(values, "Keyboard.ShiftSpaceMicrosoftOriginalExists", settings.Keyboard.ShiftSpaceMicrosoftOriginalExists);
            settings.Keyboard.ShiftSpaceMicrosoftOriginalValue = GetString(values, "Keyboard.ShiftSpaceMicrosoftOriginalValue", settings.Keyboard.ShiftSpaceMicrosoftOriginalValue);
            settings.Keyboard.ShiftSpaceAsusBackupCaptured = GetBool(values, "Keyboard.ShiftSpaceAsusBackupCaptured", settings.Keyboard.ShiftSpaceAsusBackupCaptured);
            settings.Keyboard.ShiftSpaceAsusOriginalFullWidth = GetBool(values, "Keyboard.ShiftSpaceAsusOriginalFullWidth", settings.Keyboard.ShiftSpaceAsusOriginalFullWidth);
            settings.Keyboard.ShiftSpaceImmBackupCaptured = GetBool(values, "Keyboard.ShiftSpaceImmBackupCaptured", settings.Keyboard.ShiftSpaceImmBackupCaptured);
            settings.Keyboard.ShiftSpaceImmBackup00000011 = GetString(values, "Keyboard.ShiftSpaceImmBackup00000011", settings.Keyboard.ShiftSpaceImmBackup00000011);
            settings.Keyboard.ShiftSpaceImmBackup00000071 = GetString(values, "Keyboard.ShiftSpaceImmBackup00000071", settings.Keyboard.ShiftSpaceImmBackup00000071);
            settings.Keyboard.ShiftSpaceBackupCaptured = GetBool(values, "Keyboard.ShiftSpaceBackupCaptured", settings.Keyboard.ShiftSpaceBackupCaptured);
            settings.Keyboard.ShiftSpaceBackup00000011 = GetString(values, "Keyboard.ShiftSpaceBackup00000011", settings.Keyboard.ShiftSpaceBackup00000011);
            settings.Keyboard.ShiftSpaceBackup00000071 = GetString(values, "Keyboard.ShiftSpaceBackup00000071", settings.Keyboard.ShiftSpaceBackup00000071);
            settings.Keyboard.DisableStickyKeysHotkey = GetBool(values, "Keyboard.DisableStickyKeysHotkey", settings.Keyboard.DisableStickyKeysHotkey);
            settings.Keyboard.StickyKeysHotkeyBackupCaptured = GetBool(values, "Keyboard.StickyKeysHotkeyBackupCaptured", settings.Keyboard.StickyKeysHotkeyBackupCaptured);
            settings.Keyboard.StickyKeysOriginalHotkeyActive = GetBool(values, "Keyboard.StickyKeysOriginalHotkeyActive", settings.Keyboard.StickyKeysOriginalHotkeyActive);
            settings.GameHelper.BlockWindowsKey = GetBool(values, "GameHelper.BlockWindowsKey", settings.GameHelper.BlockWindowsKey);
            settings.GameHelper.KeepEnhancedPointerPrecisionOff = GetBool(values,
                "GameHelper.KeepEnhancedPointerPrecisionOff",
                settings.GameHelper.KeepEnhancedPointerPrecisionOff);
            settings.GameHelper.CrosshairEnabled = GetBool(values, "GameHelper.CrosshairEnabled", settings.GameHelper.CrosshairEnabled);
            settings.GameHelper.CrosshairSize = GetInt(values, "GameHelper.CrosshairSize", settings.GameHelper.CrosshairSize, 2, 48);
            settings.GameHelper.CrosshairOpacityPercent = GetInt(values, "GameHelper.CrosshairOpacityPercent", settings.GameHelper.CrosshairOpacityPercent, 10, 100);
            settings.GameHelper.CrosshairColor = GetString(values, "GameHelper.CrosshairColor", settings.GameHelper.CrosshairColor);
            settings.GameHelper.CrosshairCustomColor = GetString(values, "GameHelper.CrosshairCustomColor", settings.GameHelper.CrosshairCustomColor);
            settings.GameHelper.CrosshairStyle = GetString(values, "GameHelper.CrosshairStyle", settings.GameHelper.CrosshairStyle);
            settings.GameHelper.CrosshairRestrictToSelectedApps = GetBool(values,
                "GameHelper.CrosshairRestrictToSelectedApps", settings.GameHelper.CrosshairRestrictToSelectedApps);
            settings.GameHelper.CrosshairSelectedApps = GetString(values,
                "GameHelper.CrosshairSelectedApps", settings.GameHelper.CrosshairSelectedApps);
            settings.GameHelper.ProtectedApps = GetString(values, "GameHelper.ProtectedApps", settings.GameHelper.ProtectedApps);
            LoadListSettings(values, "GameHelper.CrosshairAppsList", settings.GameHelper.CrosshairAppsList);
            LoadListSettings(values, "GameHelper.AddAppsList", settings.GameHelper.AddAppsList);
            LoadListSettings(values, "GameHelper.ProtectedAppsList", settings.GameHelper.ProtectedAppsList);
            settings.AppGuard.ExplorerIntegrationEnabled = GetBool(values, "AppGuard.ExplorerIntegrationEnabled", settings.AppGuard.ExplorerIntegrationEnabled);
            LoadListSettings(values, "AppGuard.AppsList", settings.AppGuard.AppsList);
            settings.LinkGuard.Rules = GetString(values, "LinkGuard.Rules", settings.LinkGuard.Rules);
            LoadListSettings(values, "LinkGuard.AllAppsList", settings.LinkGuard.AllAppsList);
            LoadListSettings(values, "LinkGuard.LinkedAppsList", settings.LinkGuard.LinkedAppsList);
            settings.DisplayGuard.SelectedMode = GetString(values, "DisplayGuard.SelectedMode", settings.DisplayGuard.SelectedMode);
            settings.DisplayGuard.Profiles = GetString(values, "DisplayGuard.Profiles", settings.DisplayGuard.Profiles);
            LoadListSettings(values, "DisplayGuard.MonitorsList", settings.DisplayGuard.MonitorsList);
            settings.PowerGuard.DurationKind = GetString(values, "PowerGuard.DurationKind",
                settings.PowerGuard.DurationKind);
            settings.PowerGuard.CustomDurationMinutes = GetInt(values, "PowerGuard.CustomDurationMinutes",
                settings.PowerGuard.CustomDurationMinutes, 1, 43200);
            settings.PowerGuard.KeepDisplayOn = GetBool(values, "PowerGuard.KeepDisplayOn",
                settings.PowerGuard.KeepDisplayOn);
            settings.UacGuard.AuthorizationMode = UacGuardModule.NormalizeAuthorizationMode(
                GetString(values, "UacGuard.AuthorizationMode", settings.UacGuard.AuthorizationMode));
            settings.Appearance.CustomIconPath = GetString(values, "Appearance.CustomIconPath",
                settings.Appearance.CustomIconPath);
            settings.Layout.SidebarWidth = GetInt(values, "Layout.SidebarWidth", settings.Layout.SidebarWidth, 240, 620);
            settings.Layout.ContentWidth = GetInt(values, "Layout.ContentWidth", settings.Layout.ContentWidth, 360, 2400);
            settings.Layout.UiScalePercent = GetInt(values, "Layout.UiScalePercent", settings.Layout.UiScalePercent, 80, 150);
            settings.Layout.ModuleOrder = ModuleNavigationOrder.Normalize(
                GetString(values, "Layout.ModuleOrder", settings.Layout.ModuleOrder));

            return settings;
        }

        public void Save(AppSettings settings)
        {
            AppPaths.EnsureRoot();
            var lines = new List<string>();
            lines.Add("# Guard Center settings");
            lines.Add("Audio.Enabled=" + settings.Audio.Enabled);
            lines.Add("Audio.ZeroOnEnable=" + settings.Audio.ZeroOnEnable);
            lines.Add("Audio.SetMute=" + settings.Audio.SetMute);
            lines.Add("Audio.SetVolumeZero=" + settings.Audio.SetVolumeZero);
            lines.Add("Audio.ReactToPropertyChanges=" + settings.Audio.ReactToPropertyChanges);
            lines.Add("Audio.PollIntervalMs=" + settings.Audio.PollIntervalMs);
            lines.Add("Audio.RetryDelaysCsv=" + settings.Audio.RetryDelaysCsv);
            lines.Add("AudioMixer.DefaultVolumePercent=" + settings.AudioMixer.DefaultVolumePercent);
            lines.Add("AudioMixer.DefaultedAppKeys=" + settings.AudioMixer.DefaultedAppKeys);
            lines.Add("AudioMixer.ManualAppKeys=" + settings.AudioMixer.ManualAppKeys);
            AddListSettings(lines, "AudioMixer.AppsList", settings.AudioMixer.AppsList);
            AddListSettings(lines, "DeviceGuard.DetectableDevicesList", settings.DeviceGuard.DetectableDevicesList);
            AddListSettings(lines, "DeviceGuard.CoreHardwareList", settings.DeviceGuard.CoreHardwareList);
            lines.Add("Keyboard.DisableShiftSpaceWidthToggle=" + settings.Keyboard.DisableShiftSpaceWidthToggle);
            lines.Add("Keyboard.ShiftSpaceMicrosoftBackupCaptured=" + settings.Keyboard.ShiftSpaceMicrosoftBackupCaptured);
            lines.Add("Keyboard.ShiftSpaceMicrosoftOriginalExists=" + settings.Keyboard.ShiftSpaceMicrosoftOriginalExists);
            lines.Add("Keyboard.ShiftSpaceMicrosoftOriginalValue=" + settings.Keyboard.ShiftSpaceMicrosoftOriginalValue);
            lines.Add("Keyboard.ShiftSpaceAsusBackupCaptured=" + settings.Keyboard.ShiftSpaceAsusBackupCaptured);
            lines.Add("Keyboard.ShiftSpaceAsusOriginalFullWidth=" + settings.Keyboard.ShiftSpaceAsusOriginalFullWidth);
            lines.Add("Keyboard.ShiftSpaceImmBackupCaptured=" + settings.Keyboard.ShiftSpaceImmBackupCaptured);
            lines.Add("Keyboard.ShiftSpaceImmBackup00000011=" + settings.Keyboard.ShiftSpaceImmBackup00000011);
            lines.Add("Keyboard.ShiftSpaceImmBackup00000071=" + settings.Keyboard.ShiftSpaceImmBackup00000071);
            lines.Add("Keyboard.ShiftSpaceBackupCaptured=" + settings.Keyboard.ShiftSpaceBackupCaptured);
            lines.Add("Keyboard.ShiftSpaceBackup00000011=" + settings.Keyboard.ShiftSpaceBackup00000011);
            lines.Add("Keyboard.ShiftSpaceBackup00000071=" + settings.Keyboard.ShiftSpaceBackup00000071);
            lines.Add("Keyboard.DisableStickyKeysHotkey=" + settings.Keyboard.DisableStickyKeysHotkey);
            lines.Add("Keyboard.StickyKeysHotkeyBackupCaptured=" + settings.Keyboard.StickyKeysHotkeyBackupCaptured);
            lines.Add("Keyboard.StickyKeysOriginalHotkeyActive=" + settings.Keyboard.StickyKeysOriginalHotkeyActive);
            lines.Add("GameHelper.BlockWindowsKey=" + settings.GameHelper.BlockWindowsKey);
            lines.Add("GameHelper.KeepEnhancedPointerPrecisionOff="
                + settings.GameHelper.KeepEnhancedPointerPrecisionOff);
            lines.Add("GameHelper.CrosshairEnabled=" + settings.GameHelper.CrosshairEnabled);
            lines.Add("GameHelper.CrosshairSize=" + settings.GameHelper.CrosshairSize);
            lines.Add("GameHelper.CrosshairOpacityPercent=" + settings.GameHelper.CrosshairOpacityPercent);
            lines.Add("GameHelper.CrosshairColor=" + settings.GameHelper.CrosshairColor);
            lines.Add("GameHelper.CrosshairCustomColor=" + settings.GameHelper.CrosshairCustomColor);
            lines.Add("GameHelper.CrosshairStyle=" + settings.GameHelper.CrosshairStyle);
            lines.Add("GameHelper.CrosshairRestrictToSelectedApps="
                + settings.GameHelper.CrosshairRestrictToSelectedApps);
            lines.Add("GameHelper.CrosshairSelectedApps=" + settings.GameHelper.CrosshairSelectedApps);
            lines.Add("GameHelper.ProtectedApps=" + settings.GameHelper.ProtectedApps);
            AddListSettings(lines, "GameHelper.CrosshairAppsList", settings.GameHelper.CrosshairAppsList);
            AddListSettings(lines, "GameHelper.AddAppsList", settings.GameHelper.AddAppsList);
            AddListSettings(lines, "GameHelper.ProtectedAppsList", settings.GameHelper.ProtectedAppsList);
            lines.Add("AppGuard.ExplorerIntegrationEnabled=" + settings.AppGuard.ExplorerIntegrationEnabled);
            AddListSettings(lines, "AppGuard.AppsList", settings.AppGuard.AppsList);
            lines.Add("LinkGuard.Rules=" + settings.LinkGuard.Rules);
            AddListSettings(lines, "LinkGuard.AllAppsList", settings.LinkGuard.AllAppsList);
            AddListSettings(lines, "LinkGuard.LinkedAppsList", settings.LinkGuard.LinkedAppsList);
            lines.Add("DisplayGuard.SelectedMode=" + settings.DisplayGuard.SelectedMode);
            lines.Add("DisplayGuard.Profiles=" + settings.DisplayGuard.Profiles);
            AddListSettings(lines, "DisplayGuard.MonitorsList", settings.DisplayGuard.MonitorsList);
            lines.Add("PowerGuard.DurationKind=" + settings.PowerGuard.DurationKind);
            lines.Add("PowerGuard.CustomDurationMinutes=" + settings.PowerGuard.CustomDurationMinutes);
            lines.Add("PowerGuard.KeepDisplayOn=" + settings.PowerGuard.KeepDisplayOn);
            lines.Add("UacGuard.AuthorizationMode="
                + UacGuardModule.NormalizeAuthorizationMode(settings.UacGuard.AuthorizationMode));
            lines.Add("Appearance.CustomIconPath=" + settings.Appearance.CustomIconPath);
            lines.Add("Layout.SidebarWidth=" + settings.Layout.SidebarWidth);
            lines.Add("Layout.ContentWidth=" + settings.Layout.ContentWidth);
            lines.Add("Layout.UiScalePercent=" + settings.Layout.UiScalePercent);
            lines.Add("Layout.ModuleOrder=" + ModuleNavigationOrder.Normalize(settings.Layout.ModuleOrder));
            File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
        }

        private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string value;
            if (!values.TryGetValue(key, out value))
            {
                return fallback;
            }

            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
        {
            string value;
            if (!values.TryGetValue(key, out value))
            {
                return fallback;
            }

            int parsed;
            if (!int.TryParse(value, out parsed))
            {
                return fallback;
            }

            return Math.Max(min, Math.Min(max, parsed));
        }

        private static string GetString(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static void LoadListSettings(Dictionary<string, string> values, string prefix, ListPresentationSettings settings)
        {
            int legacyBatchSize = GetInt(values, prefix + ".PageSize", settings.BatchSize, 1, 1000);
            settings.BatchSize = GetInt(values, prefix + ".BatchSize", legacyBatchSize, 1, 1000);
            if (settings.BatchSize != 10 && settings.BatchSize != 20 && settings.BatchSize != 50 && settings.BatchSize != 100)
            {
                settings.BatchSize = 20;
            }
            settings.SortKey = GetString(values, prefix + ".SortKey", settings.SortKey);
            if (string.IsNullOrWhiteSpace(settings.SortKey))
            {
                settings.SortKey = "name";
            }
            settings.SortDescending = GetBool(values, prefix + ".SortDescending", settings.SortDescending);
        }

        private static void AddListSettings(List<string> lines, string prefix, ListPresentationSettings settings)
        {
            if (settings == null)
            {
                settings = new ListPresentationSettings();
            }

            lines.Add(prefix + ".BatchSize=" + settings.BatchSize);
            lines.Add(prefix + ".SortKey=" + settings.SortKey);
            lines.Add(prefix + ".SortDescending=" + settings.SortDescending);
        }
    }

    internal static class AppPaths
    {
        public static readonly string Root = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        public static readonly string SharedRoot = Path.Combine(Root, "Shared");
        public static readonly string SettingsPath = Path.Combine(SharedRoot, "settings.ini");
        public static readonly string LogPath = Path.Combine(Root, "Guard Center.log");
        public static readonly string InstalledExePath = Path.Combine(Root, "Guard Center.exe");
        public static readonly string PersistentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guard Center");
        public static readonly string AppearanceRoot = Path.Combine(PersistentRoot, "Appearance");
        public static readonly string DeviceGuardDataRoot = Path.Combine(PersistentRoot, "DeviceGuard");
        public static readonly string DeviceGuardBaselinePath = Path.Combine(DeviceGuardDataRoot,
            "device-guard-baseline.json");
        public static readonly string InputStackCompatibilityRoot = Path.Combine(DeviceGuardDataRoot,
            "InputStackCompatibility");

        public static void EnsureRoot()
        {
            if (!Directory.Exists(Root))
            {
                Directory.CreateDirectory(Root);
            }
            if (!Directory.Exists(SharedRoot))
            {
                Directory.CreateDirectory(SharedRoot);
            }
            if (!Directory.Exists(DeviceGuardDataRoot))
            {
                Directory.CreateDirectory(DeviceGuardDataRoot);
            }
        }
    }

    internal static class StartupManager
    {
        private const string LinkName = "Guard Center.lnk";
        private const string MinimizedArguments = "--minimized";

        public static bool IsEnabled()
        {
            string targetPath;
            string arguments;
            string workingDirectory;
            return TryReadShortcut(ShortcutPath, out targetPath, out arguments, out workingDirectory)
                && IsShortcutCurrent(targetPath, arguments, workingDirectory,
                    AppPaths.InstalledExePath, AppPaths.Root)
                && File.Exists(AppPaths.InstalledExePath);
        }

        public static void Enable(string iconPath = null)
        {
            Directory.CreateDirectory(StartupPath);
            CreateShortcut(ShortcutPath, AppPaths.InstalledExePath, MinimizedArguments, AppPaths.Root,
                iconPath);
        }

        public static bool RefreshIfConfigured(string iconPath = null)
        {
            if (!File.Exists(ShortcutPath))
            {
                return false;
            }

            string targetPath;
            string arguments;
            string workingDirectory;
            bool changed = !TryReadShortcut(ShortcutPath, out targetPath, out arguments,
                    out workingDirectory)
                || !IsShortcutCurrent(targetPath, arguments, workingDirectory,
                    AppPaths.InstalledExePath, AppPaths.Root);

            // A Windows startup shortcut necessarily resolves to a concrete location. Rewriting
            // every field whenever the portable app runs lets the shortcut follow a moved folder
            // after the user launches Guard Center once from its new location.
            Enable(iconPath);
            return changed;
        }

        public static void Disable()
        {
            DeleteIfExists(ShortcutPath);
        }

        public static string StartupPath
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }

        public static string ShortcutPath
        {
            get { return Path.Combine(StartupPath, LinkName); }
        }

        private static void CreateShortcut(string path, string target, string arguments,
            string workingDirectory, string iconPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("Windows Script Host is unavailable.");
            }

            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { path });

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                    new object[] { target });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                    new object[] { arguments });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { workingDirectory });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "Guard Center" });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                    new object[] { FormatIconLocation(iconPath) });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static bool TryReadShortcut(string path, out string targetPath, out string arguments,
            out string workingDirectory)
        {
            targetPath = string.Empty;
            arguments = string.Empty;
            workingDirectory = string.Empty;
            if (!File.Exists(path))
            {
                return false;
            }

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return false;
            }

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type shortcutType = shortcut.GetType();
                targetPath = Convert.ToString(shortcutType.InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, shortcut, null)) ?? string.Empty;
                arguments = Convert.ToString(shortcutType.InvokeMember("Arguments",
                    BindingFlags.GetProperty, null, shortcut, null)) ?? string.Empty;
                workingDirectory = Convert.ToString(shortcutType.InvokeMember("WorkingDirectory",
                    BindingFlags.GetProperty, null, shortcut, null)) ?? string.Empty;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        internal static bool IsShortcutCurrent(string targetPath, string arguments,
            string workingDirectory, string expectedTargetPath, string expectedWorkingDirectory)
        {
            return PathsEqual(targetPath, expectedTargetPath)
                && string.Equals((arguments ?? string.Empty).Trim(), MinimizedArguments,
                    StringComparison.OrdinalIgnoreCase)
                && PathsEqual(workingDirectory, expectedWorkingDirectory);
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                string normalizedLeft = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                        (left ?? string.Empty).Trim().Trim('"')))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRight = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                        (right ?? string.Empty).Trim().Trim('"')))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        private static string FormatIconLocation(string iconPath)
        {
            string path = string.IsNullOrWhiteSpace(iconPath) ? AppPaths.InstalledExePath : iconPath;
            return path + ",0";
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

}

