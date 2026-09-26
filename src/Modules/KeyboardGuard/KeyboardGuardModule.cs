using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GuardCenter
{
    internal readonly struct InputMethodHotKeyState
    {
        public InputMethodHotKeyState(bool exists, uint modifiers, uint virtualKey, IntPtr layout)
        {
            Exists = exists;
            Modifiers = modifiers;
            VirtualKey = virtualKey;
            Layout = layout;
        }

        public bool Exists { get; }
        public uint Modifiers { get; }
        public uint VirtualKey { get; }
        public IntPtr Layout { get; }

        public static InputMethodHotKeyState Disabled
        {
            get { return new InputMethodHotKeyState(false, 0, 0, IntPtr.Zero); }
        }

        public string Encode()
        {
            return (Exists ? "1" : "0") + "|" + Modifiers.ToString("X8", CultureInfo.InvariantCulture)
                + "|" + VirtualKey.ToString("X8", CultureInfo.InvariantCulture)
                + "|" + unchecked((ulong)Layout.ToInt64()).ToString("X16", CultureInfo.InvariantCulture);
        }

        public static bool TryDecode(string text, out InputMethodHotKeyState state)
        {
            state = Disabled;
            string[] parts = (text ?? string.Empty).Split('|');
            if (parts.Length != 4 || (parts[0] != "0" && parts[0] != "1"))
            {
                return false;
            }

            uint modifiers;
            uint virtualKey;
            ulong layout;
            if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modifiers)
                || !uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out virtualKey)
                || !ulong.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out layout))
            {
                return false;
            }

            state = new InputMethodHotKeyState(parts[0] == "1", modifiers, virtualKey,
                new IntPtr(unchecked((long)layout)));
            return true;
        }

        public static bool TryDecodeLegacy(string text, out InputMethodHotKeyState state)
        {
            state = Disabled;
            string[] parts = (text ?? string.Empty).Split('|');
            if (parts.Length != 4 || (parts[0] != "0" && parts[0] != "1"))
            {
                return false;
            }

            uint virtualKey;
            uint modifiers;
            uint layout;
            if (!TryParseLegacyLittleEndianUInt32(parts[1], out virtualKey)
                || !TryParseLegacyLittleEndianUInt32(parts[2], out modifiers)
                || !TryParseLegacyLittleEndianUInt32(parts[3], out layout))
            {
                return false;
            }

            state = new InputMethodHotKeyState(parts[0] == "1", modifiers, virtualKey,
                new IntPtr(unchecked((int)layout)));
            return true;
        }

        private static bool TryParseLegacyLittleEndianUInt32(string text, out uint value)
        {
            value = 0;
            if (text == null || text.Length != 8) return false;
            uint encoded;
            if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out encoded))
            {
                return false;
            }

            value = ((encoded & 0x000000FFU) << 24)
                | ((encoded & 0x0000FF00U) << 8)
                | ((encoded & 0x00FF0000U) >> 8)
                | ((encoded & 0xFF000000U) >> 24);
            return true;
        }

        public bool Equals(InputMethodHotKeyState other)
        {
            return Exists == other.Exists
                && (!Exists || (Modifiers == other.Modifiers
                    && VirtualKey == other.VirtualKey
                    && Layout == other.Layout));
        }
    }

    internal interface IInputMethodHotKeyApi
    {
        InputMethodHotKeyState Read(uint hotKeyId);
        bool TryWrite(uint hotKeyId, InputMethodHotKeyState state, out string error);
    }

    internal sealed class WindowsInputMethodHotKeyApi : IInputMethodHotKeyApi
    {
        public InputMethodHotKeyState Read(uint hotKeyId)
        {
            uint modifiers;
            uint virtualKey;
            IntPtr layout;
            bool exists = ImmGetHotKey(hotKeyId, out modifiers, out virtualKey, out layout);
            return new InputMethodHotKeyState(exists, modifiers, virtualKey, layout);
        }

        public bool TryWrite(uint hotKeyId, InputMethodHotKeyState state, out string error)
        {
            bool success = state.Exists
                ? ImmSetHotKey(hotKeyId, state.Modifiers, state.VirtualKey, state.Layout)
                : ImmSetHotKey(hotKeyId, 0, 0, IntPtr.Zero);
            error = success
                ? string.Empty
                : "寫入輸入法快捷鍵 0x" + hotKeyId.ToString("X", CultureInfo.InvariantCulture)
                    + " 失敗。";
            return success;
        }

        [DllImport("imm32.dll")]
        private static extern bool ImmGetHotKey(uint hotKeyId, out uint modifiers,
            out uint virtualKey, out IntPtr layout);

        [DllImport("imm32.dll")]
        private static extern bool ImmSetHotKey(uint hotKeyId, uint modifiers,
            uint virtualKey, IntPtr layout);
    }

    internal readonly struct RegistryTextValueState
    {
        public RegistryTextValueState(bool exists, string value)
        {
            Exists = exists;
            Value = value ?? string.Empty;
        }

        public bool Exists { get; }
        public string Value { get; }

        public bool Equals(RegistryTextValueState other)
        {
            return Exists == other.Exists
                && (!Exists || string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal interface IMicrosoftImeCharacterWidthApi
    {
        bool IsAvailable { get; }
        bool TryRead(out RegistryTextValueState state, out string error);
        bool TryWrite(RegistryTextValueState state, out string error);
    }

    internal sealed class WindowsMicrosoftImeCharacterWidthApi : IMicrosoftImeCharacterWidthApi
    {
        private const string RegistryPath = @"Software\Microsoft\IME\15.0\IMETC";
        private const string ValueName = "Enable Switch Character Width Hotkey";

        public bool IsAvailable
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                {
                    return key != null && key.GetValue(ValueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) is string;
                }
            }
        }

        public bool TryRead(out RegistryTextValueState state, out string error)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                {
                    if (key == null)
                    {
                        state = new RegistryTextValueState(false, string.Empty);
                        error = string.Empty;
                        return true;
                    }

                    object value = key.GetValue(ValueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                    {
                        state = new RegistryTextValueState(false, string.Empty);
                        error = string.Empty;
                        return true;
                    }
                    if (!(value is string))
                    {
                        state = new RegistryTextValueState(false, string.Empty);
                        error = "Microsoft 注音字元寬度設定不是預期的 REG_SZ，未變更。";
                        return false;
                    }

                    state = new RegistryTextValueState(true, (string)value);
                    error = string.Empty;
                    return true;
                }
            }
            catch (Exception ex)
            {
                state = new RegistryTextValueState(false, string.Empty);
                error = "讀取 Microsoft 注音字元寬度設定失敗：" + ex.Message;
                return false;
            }
        }

        public bool TryWrite(RegistryTextValueState state, out string error)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true))
                {
                    if (key == null)
                    {
                        error = "無法開啟 Microsoft 注音設定。";
                        return false;
                    }
                    if (state.Exists)
                    {
                        key.SetValue(ValueName, state.Value, RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }

                RegistryTextValueState actual;
                if (!TryRead(out actual, out error)) return false;
                if (!actual.Equals(state))
                {
                    error = "Microsoft 注音未套用字元寬度快捷鍵設定。";
                    return false;
                }
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "寫入 Microsoft 注音字元寬度設定失敗：" + ex.Message;
                return false;
            }
        }
    }

    internal interface IAsusImeCharacterWidthApi : IDisposable
    {
        bool IsAvailable { get; }
        event EventHandler Changed;
        bool TryRead(out bool fullWidth, out string error);
        bool TryWrite(bool fullWidth, out string error);
    }

    internal sealed class WindowsAsusImeCharacterWidthApi : IAsusImeCharacterWidthApi
    {
        private const string Section = "Setting";
        private const string ValueName = "IsInFullWidthMode";
        private readonly string configPath;
        private FileSystemWatcher watcher;

        public WindowsAsusImeCharacterWidthApi()
        {
            configPath = FindConfigPath();
            if (string.IsNullOrWhiteSpace(configPath)) return;
            watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath), Path.GetFileName(configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += Watcher_Changed;
            watcher.Created += Watcher_Changed;
            watcher.Renamed += Watcher_Changed;
        }

        public bool IsAvailable { get { return !string.IsNullOrWhiteSpace(configPath); } }
        public event EventHandler Changed;

        public bool TryRead(out bool fullWidth, out string error)
        {
            fullWidth = false;
            if (!IsAvailable)
            {
                error = string.Empty;
                return false;
            }

            var value = new StringBuilder(32);
            const string missing = "__GUARD_CENTER_MISSING__";
            uint length = GetPrivateProfileString(Section, ValueName, missing, value,
                (uint)value.Capacity, configPath);
            string text = value.ToString();
            if (length == 0 || string.Equals(text, missing, StringComparison.Ordinal))
            {
                error = "華碩注音設定缺少 IsInFullWidthMode。";
                return false;
            }
            if (text == "0")
            {
                error = string.Empty;
                return true;
            }
            if (text == "1")
            {
                fullWidth = true;
                error = string.Empty;
                return true;
            }

            error = "華碩注音 IsInFullWidthMode 值無效：" + text;
            return false;
        }

        public bool TryWrite(bool fullWidth, out string error)
        {
            if (!IsAvailable)
            {
                error = "找不到華碩注音使用者設定檔。";
                return false;
            }
            if (!WritePrivateProfileString(Section, ValueName, fullWidth ? "1" : "0", configPath))
            {
                error = "寫入華碩注音字元寬度設定失敗。Win32 error "
                    + Marshal.GetLastWin32Error() + ".";
                return false;
            }

            bool actual;
            if (!TryRead(out actual, out error)) return false;
            if (actual != fullWidth)
            {
                error = "華碩注音未套用字元寬度設定。";
                return false;
            }
            error = string.Empty;
            return true;
        }

        public void Dispose()
        {
            if (watcher == null) return;
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= Watcher_Changed;
            watcher.Created -= Watcher_Changed;
            watcher.Renamed -= Watcher_Changed;
            watcher.Dispose();
            watcher = null;
        }

        private void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static string FindConfigPath()
        {
            string[] programRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            for (int rootIndex = 0; rootIndex < programRoots.Length; rootIndex++)
            {
                string root = Path.Combine(programRoots[rootIndex] ?? string.Empty,
                    "ASUS", "AsusIME");
                if (!Directory.Exists(root)) continue;
                try
                {
                    string[] localeDirectories = Directory.GetDirectories(root);
                    for (int i = 0; i < localeDirectories.Length; i++)
                    {
                        string candidate = Path.Combine(localeDirectories[i], "data",
                            Environment.UserName, "AsusIMEConfig.ini");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
            return string.Empty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key,
            string defaultValue, StringBuilder value, uint size, string filePath);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string section, string key,
            string value, string filePath);
    }

    internal interface IStickyKeysSettingsApi
    {
        bool TryRead(out uint flags, out string error);
        bool TryWrite(uint flags, out string error);
    }

    internal sealed class WindowsStickyKeysSettingsApi : IStickyKeysSettingsApi
    {
        private const uint SpiGetStickyKeys = 0x003A;
        private const uint SpiSetStickyKeys = 0x003B;
        private const uint SpifUpdateIniFile = 0x0001;

        public bool TryRead(out uint flags, out string error)
        {
            var value = new StickyKeys
            {
                Size = (uint)Marshal.SizeOf(typeof(StickyKeys))
            };
            if (!SystemParametersInfo(SpiGetStickyKeys, value.Size, ref value, 0))
            {
                flags = 0;
                error = "讀取相黏鍵設定失敗。Win32 error " + Marshal.GetLastWin32Error() + ".";
                return false;
            }

            flags = value.Flags;
            error = string.Empty;
            return true;
        }

        public bool TryWrite(uint flags, out string error)
        {
            var value = new StickyKeys
            {
                Size = (uint)Marshal.SizeOf(typeof(StickyKeys)),
                Flags = flags
            };
            // SPI_SETSTICKYKEYS applies the value immediately, while
            // SPIF_UPDATEINIFILE persists it for the user. Avoid SPIF_SENDCHANGE
            // here because its synchronous broadcast can stall the toggle UI;
            // external changes are already observed by StickyKeysSettingsMonitor.
            if (!SystemParametersInfo(SpiSetStickyKeys, value.Size, ref value,
                SpifUpdateIniFile))
            {
                error = "寫入相黏鍵設定失敗。Win32 error " + Marshal.GetLastWin32Error() + ".";
                return false;
            }

            error = string.Empty;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StickyKeys
        {
            public uint Size;
            public uint Flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint parameter,
            ref StickyKeys value, uint updateFlags);
    }

    internal sealed class KeyboardGuardModule : IDisposable
    {
        internal const uint SimplifiedChineseShapeToggleHotKey = 0x11;
        internal const uint TraditionalChineseShapeToggleHotKey = 0x71;
        internal const uint StickyKeysHotkeyActive = 0x00000004;
        private const int StickyKeysPreferenceDebounceMs = 350;

        private readonly KeyboardGuardSettings settings;
        private readonly IStickyKeysSettingsApi stickyKeysApi;
        private readonly IInputMethodHotKeyApi inputMethodHotKeyApi;
        private readonly IMicrosoftImeCharacterWidthApi microsoftImeWidthApi;
        private readonly IAsusImeCharacterWidthApi asusImeWidthApi;
        private readonly object characterWidthSync = new object();
        private readonly object stickyKeysSync = new object();
        private Timer characterWidthTimer;
        private Timer stickyKeysPreferenceTimer;
        private bool stickyKeysWriteInProgress;
        private bool hasLastObservedStickyKeysFlags;
        private uint lastObservedStickyKeysFlags;
        private bool disposed;
        private string lastStatus = "Keyboard Guard ready.";

        public event EventHandler StatusChanged;

        public KeyboardGuardModule(KeyboardGuardSettings settings)
            : this(settings, new WindowsStickyKeysSettingsApi(), new WindowsInputMethodHotKeyApi(),
                new WindowsMicrosoftImeCharacterWidthApi(), new WindowsAsusImeCharacterWidthApi())
        {
        }

        internal KeyboardGuardModule(KeyboardGuardSettings settings, IStickyKeysSettingsApi stickyKeysApi)
            : this(settings, stickyKeysApi, new WindowsInputMethodHotKeyApi(),
                new WindowsMicrosoftImeCharacterWidthApi(), new WindowsAsusImeCharacterWidthApi())
        {
        }

        internal KeyboardGuardModule(KeyboardGuardSettings settings, IStickyKeysSettingsApi stickyKeysApi,
            IInputMethodHotKeyApi inputMethodHotKeyApi)
            : this(settings, stickyKeysApi, inputMethodHotKeyApi,
                new WindowsMicrosoftImeCharacterWidthApi(), new WindowsAsusImeCharacterWidthApi())
        {
        }

        internal KeyboardGuardModule(KeyboardGuardSettings settings, IStickyKeysSettingsApi stickyKeysApi,
            IInputMethodHotKeyApi inputMethodHotKeyApi,
            IMicrosoftImeCharacterWidthApi microsoftImeWidthApi,
            IAsusImeCharacterWidthApi asusImeWidthApi)
        {
            this.settings = settings ?? new KeyboardGuardSettings();
            this.stickyKeysApi = stickyKeysApi ?? new WindowsStickyKeysSettingsApi();
            this.inputMethodHotKeyApi = inputMethodHotKeyApi ?? new WindowsInputMethodHotKeyApi();
            this.microsoftImeWidthApi = microsoftImeWidthApi ?? new WindowsMicrosoftImeCharacterWidthApi();
            this.asusImeWidthApi = asusImeWidthApi ?? new WindowsAsusImeCharacterWidthApi();
            this.asusImeWidthApi.Changed += AsusImeWidthApi_Changed;
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        }

        public string StatusText
        {
            get { return lastStatus; }
        }

        public bool IsShiftSpaceWidthToggleDisabled()
        {
            return settings.DisableShiftSpaceWidthToggle;
        }

        public bool IsStickyKeysHotkeyDisabled()
        {
            return settings.DisableStickyKeysHotkey;
        }

        public bool ApplySavedState()
        {
            bool settingsChanged = false;
            if (settings.DisableShiftSpaceWidthToggle)
            {
                bool microsoftBackupWasMissing = !settings.ShiftSpaceMicrosoftBackupCaptured;
                bool asusBackupWasMissing = !settings.ShiftSpaceAsusBackupCaptured;
                bool legacyBackupWasPresent = settings.ShiftSpaceImmBackupCaptured
                    || settings.ShiftSpaceBackupCaptured;
                if (DisableShiftSpaceWidthToggle())
                {
                    settingsChanged = (microsoftBackupWasMissing && settings.ShiftSpaceMicrosoftBackupCaptured)
                        || (asusBackupWasMissing && settings.ShiftSpaceAsusBackupCaptured)
                        || (legacyBackupWasPresent && !settings.ShiftSpaceImmBackupCaptured
                            && !settings.ShiftSpaceBackupCaptured);
                }
            }

            if (settings.DisableStickyKeysHotkey)
            {
                bool backupWasMissing = !settings.StickyKeysHotkeyBackupCaptured;
                if (DisableStickyKeysHotkey())
                {
                    settingsChanged |= backupWasMissing && settings.StickyKeysHotkeyBackupCaptured;
                }
            }
            return settingsChanged;
        }

        public bool DisableShiftSpaceWidthToggle()
        {
            lock (characterWidthSync)
            {
                bool previousConfigured = settings.DisableShiftSpaceWidthToggle;
                bool previousMicrosoftCaptured = settings.ShiftSpaceMicrosoftBackupCaptured;
                bool previousMicrosoftExists = settings.ShiftSpaceMicrosoftOriginalExists;
                string previousMicrosoftValue = settings.ShiftSpaceMicrosoftOriginalValue;
                bool previousAsusCaptured = settings.ShiftSpaceAsusBackupCaptured;
                bool previousAsusFullWidth = settings.ShiftSpaceAsusOriginalFullWidth;

                bool hasSupportedIme = microsoftImeWidthApi.IsAvailable || asusImeWidthApi.IsAvailable;
                string error = string.Empty;
                if (!hasSupportedIme
                    || !CaptureCharacterWidthBackups(out error)
                    || !ApplyCharacterWidthProtection(out error))
                {
                    RestoreCharacterWidthBackendsBestEffort();
                    settings.DisableShiftSpaceWidthToggle = previousConfigured;
                    settings.ShiftSpaceMicrosoftBackupCaptured = previousMicrosoftCaptured;
                    settings.ShiftSpaceMicrosoftOriginalExists = previousMicrosoftExists;
                    settings.ShiftSpaceMicrosoftOriginalValue = previousMicrosoftValue;
                    settings.ShiftSpaceAsusBackupCaptured = previousAsusCaptured;
                    settings.ShiftSpaceAsusOriginalFullWidth = previousAsusFullWidth;
                    SetStatus(hasSupportedIme ? error : "找不到支援的 Microsoft 注音或華碩注音設定。");
                    return false;
                }

                settings.DisableShiftSpaceWidthToggle = true;
                RestoreLegacyImmHotKeysBestEffort();
                StartCharacterWidthMonitor();
                SetStatus("已鎖定注音輸入法的字元寬度狀態；Shift + Space 按鍵仍會傳給目前程式。");
                return true;
            }
        }

        public bool RestoreShiftSpaceWidthToggle()
        {
            lock (characterWidthSync)
            {
                StopCharacterWidthMonitor();
                string error;
                if (!RestoreCharacterWidthBackends(out error))
                {
                    StartCharacterWidthMonitor();
                    SetStatus(error);
                    return false;
                }

                RestoreLegacyImmHotKeysBestEffort();
                settings.DisableShiftSpaceWidthToggle = false;
                settings.ShiftSpaceMicrosoftBackupCaptured = false;
                settings.ShiftSpaceMicrosoftOriginalExists = false;
                settings.ShiftSpaceMicrosoftOriginalValue = string.Empty;
                settings.ShiftSpaceAsusBackupCaptured = false;
                settings.ShiftSpaceAsusOriginalFullWidth = false;
                SetStatus("已停止鎖定字元寬度，並恢復支援輸入法的原始快捷鍵設定。");
                return true;
            }
        }

        public bool DisableStickyKeysHotkey()
        {
            lock (stickyKeysSync)
            {
                uint current;
                string error;
                if (!stickyKeysApi.TryRead(out current, out error))
                {
                    SetStatus(error);
                    return false;
                }

                bool previousBackupCaptured = settings.StickyKeysHotkeyBackupCaptured;
                bool previousOriginalHotkey = settings.StickyKeysOriginalHotkeyActive;
                bool previousConfigured = settings.DisableStickyKeysHotkey;
                if (!settings.StickyKeysHotkeyBackupCaptured)
                {
                    settings.StickyKeysOriginalHotkeyActive = HasStickyKeysHotkey(current);
                    settings.StickyKeysHotkeyBackupCaptured = true;
                }

                if (!WriteAndVerifyStickyKeys(false, out error))
                {
                    settings.StickyKeysHotkeyBackupCaptured = previousBackupCaptured;
                    settings.StickyKeysOriginalHotkeyActive = previousOriginalHotkey;
                    settings.DisableStickyKeysHotkey = previousConfigured;
                    SetStatus(error);
                    return false;
                }

                settings.DisableStickyKeysHotkey = true;
                SetStatus("已停用連按五次 Shift 啟動相黏鍵；一般 Shift 不受影響。");
                return true;
            }
        }

        public bool RestoreStickyKeysHotkey()
        {
            lock (stickyKeysSync)
            {
                if (!settings.StickyKeysHotkeyBackupCaptured)
                {
                    SetStatus("缺少啟用前的相黏鍵快捷鍵狀態，未變更 Windows 設定。");
                    return false;
                }

                string error;
                if (!WriteAndVerifyStickyKeys(settings.StickyKeysOriginalHotkeyActive, out error))
                {
                    SetStatus(error);
                    return false;
                }

                settings.DisableStickyKeysHotkey = false;
                settings.StickyKeysHotkeyBackupCaptured = false;
                SetStatus(settings.StickyKeysOriginalHotkeyActive
                    ? "已恢復連按五次 Shift 的原始快捷鍵狀態。"
                    : "相黏鍵快捷鍵原本即為停用，已保留原始狀態。");
                return true;
            }
        }

        public void Dispose()
        {
            lock (characterWidthSync)
            {
                StopCharacterWidthMonitor();
            }
            lock (stickyKeysSync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
            }
            asusImeWidthApi.Changed -= AsusImeWidthApi_Changed;
            asusImeWidthApi.Dispose();
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            if (stickyKeysPreferenceTimer != null)
            {
                stickyKeysPreferenceTimer.Dispose();
                stickyKeysPreferenceTimer = null;
            }
        }

        public void OpenTypingSettings()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:typing",
                UseShellExecute = true
            });
        }

        public void OpenLanguageSettings()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:regionlanguage",
                UseShellExecute = true
            });
        }

        private bool CaptureCharacterWidthBackups(out string error)
        {
            if (microsoftImeWidthApi.IsAvailable && !settings.ShiftSpaceMicrosoftBackupCaptured)
            {
                RegistryTextValueState original;
                if (!microsoftImeWidthApi.TryRead(out original, out error)) return false;
                settings.ShiftSpaceMicrosoftOriginalExists = original.Exists;
                settings.ShiftSpaceMicrosoftOriginalValue = original.Value;
                settings.ShiftSpaceMicrosoftBackupCaptured = true;
            }

            if (asusImeWidthApi.IsAvailable && !settings.ShiftSpaceAsusBackupCaptured)
            {
                bool original;
                if (!asusImeWidthApi.TryRead(out original, out error)) return false;
                settings.ShiftSpaceAsusOriginalFullWidth = original;
                settings.ShiftSpaceAsusBackupCaptured = true;
            }

            error = string.Empty;
            return true;
        }

        private bool ApplyCharacterWidthProtection(out string error)
        {
            if (settings.ShiftSpaceMicrosoftBackupCaptured && microsoftImeWidthApi.IsAvailable)
            {
                if (!microsoftImeWidthApi.TryWrite(
                    new RegistryTextValueState(true, "0x00000000"), out error)) return false;
            }

            if (settings.ShiftSpaceAsusBackupCaptured && asusImeWidthApi.IsAvailable)
            {
                if (!asusImeWidthApi.TryWrite(settings.ShiftSpaceAsusOriginalFullWidth, out error))
                    return false;
            }

            error = string.Empty;
            return true;
        }

        private bool RestoreCharacterWidthBackends(out string error)
        {
            if (settings.ShiftSpaceMicrosoftBackupCaptured && microsoftImeWidthApi.IsAvailable)
            {
                var original = new RegistryTextValueState(
                    settings.ShiftSpaceMicrosoftOriginalExists,
                    settings.ShiftSpaceMicrosoftOriginalValue);
                if (!microsoftImeWidthApi.TryWrite(original, out error)) return false;
            }

            if (settings.ShiftSpaceAsusBackupCaptured && asusImeWidthApi.IsAvailable)
            {
                if (!asusImeWidthApi.TryWrite(settings.ShiftSpaceAsusOriginalFullWidth, out error))
                    return false;
            }

            error = string.Empty;
            return true;
        }

        private void RestoreCharacterWidthBackendsBestEffort()
        {
            string error;
            if (!RestoreCharacterWidthBackends(out error) && !string.IsNullOrWhiteSpace(error))
            {
                AppLog.Write("Keyboard Guard", error);
            }
        }

        private void StartCharacterWidthMonitor()
        {
            if (characterWidthTimer != null) return;
            characterWidthTimer = new Timer(CharacterWidthTimer_Tick, null, 100, 100);
        }

        private void StopCharacterWidthMonitor()
        {
            Timer timer = characterWidthTimer;
            characterWidthTimer = null;
            if (timer != null) timer.Dispose();
        }

        private void CharacterWidthTimer_Tick(object state)
        {
            EnsureCharacterWidthProtection();
        }

        private void AsusImeWidthApi_Changed(object sender, EventArgs e)
        {
            EnsureCharacterWidthProtection();
        }

        internal void EnsureCharacterWidthProtection()
        {
            lock (characterWidthSync)
            {
                if (disposed || !settings.DisableShiftSpaceWidthToggle) return;

                string error;
                if (settings.ShiftSpaceMicrosoftBackupCaptured && microsoftImeWidthApi.IsAvailable)
                {
                    RegistryTextValueState current;
                    var expected = new RegistryTextValueState(true, "0x00000000");
                    if (!microsoftImeWidthApi.TryRead(out current, out error))
                    {
                        AppLog.Write("Keyboard Guard", error);
                    }
                    else if (!current.Equals(expected)
                        && !microsoftImeWidthApi.TryWrite(expected, out error))
                    {
                        AppLog.Write("Keyboard Guard", error);
                    }
                }

                if (settings.ShiftSpaceAsusBackupCaptured && asusImeWidthApi.IsAvailable)
                {
                    bool current;
                    if (!asusImeWidthApi.TryRead(out current, out error))
                    {
                        AppLog.Write("Keyboard Guard", error);
                    }
                    else if (current != settings.ShiftSpaceAsusOriginalFullWidth
                        && !asusImeWidthApi.TryWrite(settings.ShiftSpaceAsusOriginalFullWidth,
                            out error))
                    {
                        AppLog.Write("Keyboard Guard", error);
                    }
                }
            }
        }

        private void RestoreLegacyImmHotKeysBestEffort()
        {
            if (!settings.ShiftSpaceImmBackupCaptured && !settings.ShiftSpaceBackupCaptured) return;
            InputMethodHotKeyState original11;
            InputMethodHotKeyState original71;
            if (!TryReadSavedShiftSpaceBackup(out original11, out original71))
            {
                AppLog.Write("Keyboard Guard", "舊版 IMM 快捷鍵備份無效，無法自動遷移。");
                return;
            }

            string error11;
            string error71;
            bool restored11 = WriteAndVerifyInputMethodHotKey(
                SimplifiedChineseShapeToggleHotKey, original11, out error11);
            bool restored71 = WriteAndVerifyInputMethodHotKey(
                TraditionalChineseShapeToggleHotKey, original71, out error71);
            if (!restored11 || !restored71)
            {
                if (!restored11) AppLog.Write("Keyboard Guard", error11);
                if (!restored71) AppLog.Write("Keyboard Guard", error71);
                return;
            }

            settings.ShiftSpaceImmBackupCaptured = false;
            settings.ShiftSpaceImmBackup00000011 = string.Empty;
            settings.ShiftSpaceImmBackup00000071 = string.Empty;
            settings.ShiftSpaceBackupCaptured = false;
            settings.ShiftSpaceBackup00000011 = string.Empty;
            settings.ShiftSpaceBackup00000071 = string.Empty;
        }

        private bool TryReadSavedShiftSpaceBackup(out InputMethodHotKeyState original11,
            out InputMethodHotKeyState original71)
        {
            original11 = InputMethodHotKeyState.Disabled;
            original71 = InputMethodHotKeyState.Disabled;
            if (settings.ShiftSpaceImmBackupCaptured)
            {
                return InputMethodHotKeyState.TryDecode(settings.ShiftSpaceImmBackup00000011, out original11)
                    && InputMethodHotKeyState.TryDecode(settings.ShiftSpaceImmBackup00000071, out original71);
            }

            return settings.ShiftSpaceBackupCaptured
                && InputMethodHotKeyState.TryDecodeLegacy(settings.ShiftSpaceBackup00000011, out original11)
                && InputMethodHotKeyState.TryDecodeLegacy(settings.ShiftSpaceBackup00000071, out original71);
        }

        private bool WriteAndVerifyInputMethodHotKey(uint hotKeyId, InputMethodHotKeyState expected,
            out string error)
        {
            if (!inputMethodHotKeyApi.TryWrite(hotKeyId, expected, out error))
            {
                return false;
            }

            InputMethodHotKeyState actual = inputMethodHotKeyApi.Read(hotKeyId);
            if (!actual.Equals(expected))
            {
                error = "Windows 未套用輸入法快捷鍵 0x"
                    + hotKeyId.ToString("X", CultureInfo.InvariantCulture) + " 的變更。";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private void RestoreInputMethodHotKeyBestEffort(uint hotKeyId, InputMethodHotKeyState state)
        {
            string ignored;
            if (!WriteAndVerifyInputMethodHotKey(hotKeyId, state, out ignored))
            {
                AppLog.Write("Keyboard Guard", ignored);
            }
        }

        private void SetStatus(string status)
        {
            lastStatus = status;
            EventHandler handler = StatusChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        internal static uint SetStickyKeysHotkey(uint flags, bool enabled)
        {
            return enabled ? flags | StickyKeysHotkeyActive : flags & ~StickyKeysHotkeyActive;
        }

        internal void CheckStickyKeysAfterPreferenceChange()
        {
            lock (stickyKeysSync)
            {
                if (disposed || stickyKeysWriteInProgress || !settings.DisableStickyKeysHotkey)
                {
                    return;
                }

                uint current;
                string error;
                if (!stickyKeysApi.TryRead(out current, out error))
                {
                    SetStatus(error);
                    return;
                }

                if (hasLastObservedStickyKeysFlags && current == lastObservedStickyKeysFlags)
                {
                    return;
                }
                hasLastObservedStickyKeysFlags = true;
                lastObservedStickyKeysFlags = current;

                if (!HasStickyKeysHotkey(current))
                {
                    return;
                }

                if (WriteAndVerifyStickyKeys(false, out error))
                {
                    SetStatus("偵測到 Windows 重新啟用相黏鍵快捷鍵，已再次停用。");
                }
                else
                {
                    SetStatus(error);
                }
            }
        }

        private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            lock (stickyKeysSync)
            {
                if (disposed || !settings.DisableStickyKeysHotkey)
                {
                    return;
                }
                if (stickyKeysPreferenceTimer == null)
                {
                    stickyKeysPreferenceTimer = new Timer(delegate
                    {
                        CheckStickyKeysAfterPreferenceChange();
                    }, null, Timeout.Infinite, Timeout.Infinite);
                }
                stickyKeysPreferenceTimer.Change(StickyKeysPreferenceDebounceMs, Timeout.Infinite);
            }
        }

        private bool WriteAndVerifyStickyKeys(bool expectedHotkeyActive, out string error)
        {
            uint current;
            if (!stickyKeysApi.TryRead(out current, out error))
            {
                return false;
            }
            if (HasStickyKeysHotkey(current) == expectedHotkeyActive)
            {
                hasLastObservedStickyKeysFlags = true;
                lastObservedStickyKeysFlags = current;
                return true;
            }

            uint desired = SetStickyKeysHotkey(current, expectedHotkeyActive);
            stickyKeysWriteInProgress = true;
            try
            {
                if (!stickyKeysApi.TryWrite(desired, out error))
                {
                    return false;
                }

                uint verified;
                if (!stickyKeysApi.TryRead(out verified, out error))
                {
                    return false;
                }
                if (HasStickyKeysHotkey(verified) != expectedHotkeyActive)
                {
                    error = "Windows 未套用預期的相黏鍵快捷鍵狀態。";
                    return false;
                }

                hasLastObservedStickyKeysFlags = true;
                lastObservedStickyKeysFlags = verified;
                error = string.Empty;
                return true;
            }
            finally
            {
                stickyKeysWriteInProgress = false;
            }
        }

        private static bool HasStickyKeysHotkey(uint flags)
        {
            return (flags & StickyKeysHotkeyActive) != 0;
        }

    }
}
