using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Automation;
using Forms = System.Windows.Forms;

namespace GuardCenter
{
    internal sealed class VsrGuardSnapshot
    {
        public bool ChromeInstalled;
        public string ChromePath = string.Empty;
        public string ChromeVersion = string.Empty;
        public bool ChromeHighPerformance;
        public bool ChromeHardwareAcceleration;
        public bool ChromeHardwareAccelerationKnown;
        public bool ChromeRunning;
        public bool NvidiaGpuPresent;
        public bool NvidiaRtxSupported;
        public bool NvidiaGpuHealthy;
        public string NvidiaGpuName = string.Empty;
        public string NvidiaDriverVersion = string.Empty;
        public string NvidiaAdapterRegistryPath = string.Empty;
        public bool VsrStateKnown;
        public bool VsrEnabled;
        public bool OnBatteryPower;
        public string Error = string.Empty;

        public bool IsReady
        {
            get
            {
                return ChromeInstalled
                    && NvidiaRtxSupported
                    && NvidiaGpuHealthy
                    && ChromeHighPerformance
                    && ChromeHardwareAccelerationKnown
                    && ChromeHardwareAcceleration
                    && VsrStateKnown
                    && VsrEnabled;
            }
        }

        public string StatusText
        {
            get
            {
                if (IsReady)
                {
                    return OnBatteryPower
                        ? "VSR is configured, but browsers normally suspend RTX upscaling on battery power."
                        : "Chrome and NVIDIA RTX Video Super Resolution are ready.";
                }
                if (!ChromeInstalled)
                {
                    return "Google Chrome was not found.";
                }
                if (!NvidiaRtxSupported)
                {
                    return NvidiaGpuPresent
                        ? "The detected NVIDIA GPU does not report RTX VSR support."
                        : "A supported NVIDIA RTX GPU was not found.";
                }
                if (!NvidiaGpuHealthy)
                {
                    return "The NVIDIA GPU or its driver is not reporting a healthy state.";
                }
                return "VSR Guard found settings that still need attention.";
            }
        }
    }

    internal sealed class VsrGuardActionResult
    {
        public bool Success;
        public bool RestartChrome;
        public string Message = string.Empty;
    }

    internal sealed class VsrGuardModule
    {
        internal const string ElevatedEnableArgument = "--vsr-guard-enable";
        internal const string ElevatedDisableArgument = "--vsr-guard-disable";
        internal const string GpuPreferencesRegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        internal const string NvidiaDisplayClassPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        internal const string NvidiaVideoFlagsValue = "_User_Global_VAL_RTXVideoFlags";
        internal const string NvidiaVideoFlagsExplicitValue = "_User_Global_XEN_RTXVideoFlags";
        private const uint VsrEnabledBit = 0x00000001u;
        private const uint NvidiaUserOverrideBit = 0x80000000u;
        private static readonly string GsudoPath = @"C:\Program Files\gsudo\Current\gsudo.exe";

        private VsrGuardSnapshot lastSnapshot;

        public VsrGuardSnapshot LastSnapshot
        {
            get { return lastSnapshot; }
        }

        public VsrGuardSnapshot Refresh()
        {
            var snapshot = new VsrGuardSnapshot();
            var errors = new List<string>();

            try
            {
                snapshot.ChromePath = FindChromePath();
                snapshot.ChromeInstalled = !string.IsNullOrWhiteSpace(snapshot.ChromePath);
                if (snapshot.ChromeInstalled)
                {
                    snapshot.ChromeVersion = FileVersionInfo.GetVersionInfo(snapshot.ChromePath).ProductVersion
                        ?? string.Empty;
                    snapshot.ChromeHighPerformance = IsChromeHighPerformance(snapshot.ChromePath);
                    snapshot.ChromeRunning = IsChromeRunning();

                    bool acceleration;
                    snapshot.ChromeHardwareAccelerationKnown = TryReadChromeHardwareAcceleration(out acceleration);
                    snapshot.ChromeHardwareAcceleration = acceleration;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Chrome: " + ex.Message);
            }

            try
            {
                ReadNvidiaState(snapshot);
            }
            catch (Exception ex)
            {
                errors.Add("NVIDIA: " + ex.Message);
            }

            try
            {
                snapshot.OnBatteryPower = Forms.SystemInformation.PowerStatus.PowerLineStatus
                    == Forms.PowerLineStatus.Offline;
            }
            catch (Exception ex)
            {
                errors.Add("Power: " + ex.Message);
            }

            snapshot.Error = string.Join(" | ", errors.ToArray());
            lastSnapshot = snapshot;
            return snapshot;
        }

        public VsrGuardActionResult EnableChromeHighPerformance()
        {
            VsrGuardSnapshot snapshot = Refresh();
            if (!snapshot.ChromeInstalled)
            {
                return Failure("Google Chrome is not installed.");
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(GpuPreferencesRegistryPath, true))
                {
                    string existing = Convert.ToString(key.GetValue(snapshot.ChromePath, string.Empty));
                    key.SetValue(snapshot.ChromePath, MergeGpuPreference(existing, 2), RegistryValueKind.String);
                }

                snapshot = Refresh();
                if (!snapshot.ChromeHighPerformance)
                {
                    return Failure("Windows did not retain Chrome's High performance GPU preference.");
                }
                return Success("Chrome now uses the High performance GPU. Restart Chrome to apply it.", true);
            }
            catch (Exception ex)
            {
                return Failure("Could not set Chrome to High performance: " + ex.Message);
            }
        }

        public VsrGuardActionResult EnableChromeGraphicsAcceleration()
        {
            VsrGuardSnapshot snapshot = Refresh();
            if (!snapshot.ChromeInstalled)
            {
                return Failure("Google Chrome is not installed.");
            }
            if (snapshot.ChromeHardwareAccelerationKnown && snapshot.ChromeHardwareAcceleration)
            {
                return Success("Chrome graphics acceleration is already enabled.", false);
            }
            if (snapshot.ChromeRunning)
            {
                return Failure("Close every Chrome window first. Chrome can overwrite Local State while it is running.");
            }

            try
            {
                string path = GetChromeLocalStatePath();
                if (!File.Exists(path))
                {
                    return Failure("Chrome Local State was not found. Launch Chrome once, close it, and try again.");
                }

                string json = ReadSharedText(path);
                JsonObject root = JsonNode.Parse(json) as JsonObject;
                if (root == null)
                {
                    return Failure("Chrome Local State is not a JSON object.");
                }

                JsonObject mode = root["hardware_acceleration_mode"] as JsonObject;
                if (mode == null)
                {
                    mode = new JsonObject();
                    root["hardware_acceleration_mode"] = mode;
                }
                mode["enabled"] = true;

                WriteJsonAtomically(path, root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false
                }));

                snapshot = Refresh();
                if (!snapshot.ChromeHardwareAccelerationKnown || !snapshot.ChromeHardwareAcceleration)
                {
                    return Failure("Chrome graphics acceleration could not be verified after writing Local State.");
                }
                return Success("Chrome graphics acceleration is enabled. Restart Chrome to apply it.", true);
            }
            catch (Exception ex)
            {
                return Failure("Could not enable Chrome graphics acceleration: " + ex.Message);
            }
        }

        public async Task<VsrGuardActionResult> EnableVsrAsync()
        {
            return await SetVsrEnabledAsync(true);
        }

        public async Task<VsrGuardActionResult> DisableVsrAsync()
        {
            return await SetVsrEnabledAsync(false);
        }

        public VsrGuardActionResult OpenNvidiaControlPanel()
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                };
                start.ArgumentList.Add(
                    @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel");
                Process.Start(start);
                return Success("Opened NVIDIA Control Panel.", false);
            }
            catch (Exception firstError)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "control.exe",
                        Arguments = "/name NVIDIA.Display",
                        UseShellExecute = true
                    });
                    return Success("Opened NVIDIA Control Panel.", false);
                }
                catch (Exception fallbackError)
                {
                    return Failure("Could not open NVIDIA Control Panel: "
                        + firstError.Message + " | " + fallbackError.Message);
                }
            }
        }

        public VsrGuardActionResult OpenWindowsGraphicsSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:display-advancedgraphics",
                    UseShellExecute = true
                });
                return Success("Opened Windows Graphics settings.", false);
            }
            catch (Exception ex)
            {
                return Failure("Could not open Windows Graphics settings: " + ex.Message);
            }
        }

        public async Task<VsrGuardActionResult> OpenChromeSystemSettingsAsync()
        {
            VsrGuardSnapshot snapshot = Refresh();
            if (!snapshot.ChromeInstalled || string.IsNullOrWhiteSpace(snapshot.ChromePath))
            {
                return Failure("Google Chrome is not installed.");
            }

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return Failure("Windows Desktop Shell is unavailable.");
                }

                object shell = Activator.CreateInstance(shellType);
                try
                {
                    shellType.InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell,
                        new object[]
                        {
                            snapshot.ChromePath,
                            string.Empty,
                            Path.GetDirectoryName(snapshot.ChromePath) ?? string.Empty,
                            "open",
                            1
                        });
                }
                finally
                {
                    if (shell != null && Marshal.IsComObject(shell))
                    {
                        Marshal.FinalReleaseComObject(shell);
                    }
                }

                IntPtr chromeWindow = IntPtr.Zero;
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    await Task.Delay(100);
                    IntPtr foreground = GetForegroundWindow();
                    if (IsChromeWindow(foreground))
                    {
                        chromeWindow = foreground;
                        break;
                    }
                }

                if (chromeWindow == IntPtr.Zero)
                {
                    chromeWindow = Process.GetProcessesByName("chrome")
                        .Select(process => process.MainWindowHandle)
                        .FirstOrDefault(handle => handle != IntPtr.Zero);
                }
                if (chromeWindow == IntPtr.Zero || !SetForegroundWindow(chromeWindow))
                {
                    return Failure("Chrome opened, but its browser window could not be focused.");
                }

                await Task.Delay(150);
                string addressError;
                if (!TrySetChromeAddressBar(chromeWindow, "chrome://settings/system", out addressError))
                {
                    return Failure(addressError);
                }
                await Task.Delay(100);
                Forms.SendKeys.SendWait("{ENTER}");
                return Success("Opened Chrome system settings.", false);
            }
            catch (Exception ex)
            {
                return Failure("Could not open Chrome system settings: " + ex.Message);
            }
        }

        private static bool TrySetChromeAddressBar(IntPtr chromeWindow, string address, out string error)
        {
            error = string.Empty;
            try
            {
                AutomationElement chrome = AutomationElement.FromHandle(chromeWindow);
                AutomationElementCollection edits = chrome.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                AutomationElement addressBar = null;
                double bestTop = double.MaxValue;
                for (int index = 0; index < edits.Count; index++)
                {
                    AutomationElement candidate = edits[index];
                    object pattern;
                    if (!candidate.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)
                        || ((ValuePattern)pattern).Current.IsReadOnly)
                    {
                        continue;
                    }

                    System.Windows.Rect bounds = candidate.Current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Top >= bestTop)
                    {
                        continue;
                    }
                    addressBar = candidate;
                    bestTop = bounds.Top;
                }

                if (addressBar == null)
                {
                    error = "Chrome opened, but its address bar could not be found.";
                    return false;
                }

                object valuePattern;
                if (!addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern))
                {
                    error = "Chrome opened, but its address bar cannot be edited.";
                    return false;
                }
                addressBar.SetFocus();
                ((ValuePattern)valuePattern).SetValue(address);
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not navigate Chrome to its system settings: " + ex.Message;
                return false;
            }
        }

        private static bool IsChromeWindow(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return false;
            }

            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == 0)
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public VsrGuardActionResult OpenWindowsHdrSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:display",
                    UseShellExecute = true
                });
                return Success("Opened Windows display settings for HDR.", false);
            }
            catch (Exception ex)
            {
                return Failure("Could not open Windows HDR settings: " + ex.Message);
            }
        }

        private async Task<VsrGuardActionResult> SetVsrEnabledAsync(bool enabled)
        {
            VsrGuardSnapshot snapshot = Refresh();
            if (!snapshot.NvidiaRtxSupported)
            {
                return Failure("A supported NVIDIA RTX GPU was not found.");
            }
            if (!snapshot.NvidiaGpuHealthy)
            {
                return Failure("The NVIDIA GPU or driver is not healthy. Repair or update the driver first.");
            }
            if (snapshot.VsrStateKnown && snapshot.VsrEnabled == enabled)
            {
                return Success("NVIDIA RTX Video Super Resolution is already "
                    + (enabled ? "enabled." : "disabled."), false);
            }

            try
            {
                int exitCode;
                if (IsProcessElevated())
                {
                    string error;
                    exitCode = TrySetDriverSetting(enabled, out error) ? 0 : 3;
                    if (exitCode != 0)
                    {
                        return Failure(error);
                    }
                }
                else
                {
                    exitCode = await RunElevatedSetAsync(enabled);
                }

                if (exitCode != 0)
                {
                    return Failure(exitCode == 1223
                        ? "Administrator authorization was canceled."
                        : "The NVIDIA VSR setting helper returned exit code " + exitCode + ".");
                }

                snapshot = Refresh();
                if (!snapshot.VsrStateKnown || snapshot.VsrEnabled != enabled)
                {
                    return Failure("The NVIDIA driver setting was written but could not be verified.");
                }

                return Success("NVIDIA RTX Video Super Resolution is "
                    + (enabled ? "enabled." : "disabled.")
                    + " Restart Chrome; a Windows restart may be required by some driver versions.", true);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return Failure("Administrator authorization was canceled.");
            }
            catch (Exception ex)
            {
                return Failure("Could not " + (enabled ? "enable" : "disable")
                    + " NVIDIA VSR: " + ex.Message);
            }
        }

        public async Task<VsrGuardActionResult> SetupAllAsync()
        {
            VsrGuardSnapshot snapshot = Refresh();
            if (!snapshot.ChromeInstalled)
            {
                return Failure("Google Chrome is required before VSR Guard can finish setup.");
            }
            if (!snapshot.NvidiaRtxSupported || !snapshot.NvidiaGpuHealthy)
            {
                return Failure("A healthy NVIDIA RTX GPU and driver are required.");
            }

            bool restartChrome = false;
            var completed = new List<string>();

            if (!snapshot.ChromeHighPerformance)
            {
                VsrGuardActionResult gpuResult = EnableChromeHighPerformance();
                if (!gpuResult.Success)
                {
                    return gpuResult;
                }
                completed.Add("Chrome High performance");
                restartChrome |= gpuResult.RestartChrome;
            }

            snapshot = Refresh();
            if (!snapshot.ChromeHardwareAccelerationKnown || !snapshot.ChromeHardwareAcceleration)
            {
                VsrGuardActionResult accelerationResult = EnableChromeGraphicsAcceleration();
                if (!accelerationResult.Success)
                {
                    accelerationResult.Message = completed.Count == 0
                        ? accelerationResult.Message
                        : string.Join(", ", completed.ToArray()) + " completed. " + accelerationResult.Message;
                    accelerationResult.RestartChrome |= restartChrome;
                    return accelerationResult;
                }
                completed.Add("Chrome graphics acceleration");
                restartChrome |= accelerationResult.RestartChrome;
            }

            snapshot = Refresh();
            if (!snapshot.VsrStateKnown || !snapshot.VsrEnabled)
            {
                VsrGuardActionResult vsrResult = await EnableVsrAsync();
                if (!vsrResult.Success)
                {
                    vsrResult.Message = completed.Count == 0
                        ? vsrResult.Message
                        : string.Join(", ", completed.ToArray()) + " completed. " + vsrResult.Message;
                    vsrResult.RestartChrome |= restartChrome;
                    return vsrResult;
                }
                completed.Add("NVIDIA VSR");
                restartChrome |= vsrResult.RestartChrome;
            }

            string message = completed.Count == 0
                ? "All VSR requirements are already configured."
                : "Configured: " + string.Join(", ", completed.ToArray()) + ".";
            if (restartChrome)
            {
                message += " Fully close and reopen Chrome to apply the changes.";
            }
            return Success(message, restartChrome);
        }

        internal static bool TryHandleCommandLine(string[] args)
        {
            bool enable = HasArgument(args, ElevatedEnableArgument);
            bool disable = HasArgument(args, ElevatedDisableArgument);
            if (!enable && !disable)
            {
                return false;
            }

            if (!IsProcessElevated())
            {
                Environment.ExitCode = 5;
                return true;
            }

            string error;
            bool success = TrySetDriverSetting(enable, out error);
            if (!success)
            {
                AppLog.Write("VsrGuard", "Elevated VSR "
                    + (enable ? "enable" : "disable") + " failed: " + error);
            }
            Environment.ExitCode = success ? 0 : 3;
            return true;
        }

        internal static string MergeGpuPreference(string existing, int preference)
        {
            var parts = new List<string>();
            string[] values = (existing ?? string.Empty).Split(';');
            for (int i = 0; i < values.Length; i++)
            {
                string part = values[i].Trim();
                if (part.Length == 0 || part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                parts.Add(part);
            }

            var builder = new StringBuilder();
            builder.Append("GpuPreference=").Append(preference).Append(';');
            for (int i = 0; i < parts.Count; i++)
            {
                builder.Append(parts[i]).Append(';');
            }
            return builder.ToString();
        }

        internal static bool TryReadHardwareAcceleration(string json, out bool enabled)
        {
            enabled = false;
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                JsonElement mode;
                JsonElement value;
                if (root.TryGetProperty("hardware_acceleration_mode", out mode)
                    && mode.ValueKind == JsonValueKind.Object
                    && mode.TryGetProperty("enabled", out value)
                    && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
                {
                    enabled = value.GetBoolean();
                    return true;
                }
                if (root.TryGetProperty("hardware_acceleration_mode_previous", out value)
                    && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
                {
                    enabled = value.GetBoolean();
                    return true;
                }
            }
            return false;
        }

        internal static uint EnableVsrValue(uint current)
        {
            return current | VsrEnabledBit;
        }

        internal static uint EnableVsrExplicitValue(uint current)
        {
            return current | NvidiaUserOverrideBit | VsrEnabledBit;
        }

        internal static uint DisableVsrValue(uint current)
        {
            return current & ~VsrEnabledBit;
        }

        internal static uint DisableVsrExplicitValue(uint current)
        {
            return (current | NvidiaUserOverrideBit) & ~VsrEnabledBit;
        }

        private static string FindChromePath()
        {
            var candidates = new List<string>();
            AddAppPathCandidate(candidates, Registry.CurrentUser);
            AddAppPathCandidate(candidates, Registry.LocalMachine);
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"));

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = Environment.ExpandEnvironmentVariables(candidates[i] ?? string.Empty).Trim('"');
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            return string.Empty;
        }

        private static void AddAppPathCandidate(List<string> candidates, RegistryKey root)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                {
                    if (key != null)
                    {
                        candidates.Add(Convert.ToString(key.GetValue(null, string.Empty)));
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsChromeHighPerformance(string chromePath)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(GpuPreferencesRegistryPath))
            {
                string value = key == null ? string.Empty : Convert.ToString(key.GetValue(chromePath, string.Empty));
                string[] parts = value.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (string.Equals(parts[i].Trim(), "GpuPreference=2", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private static bool TryReadChromeHardwareAcceleration(out bool enabled)
        {
            enabled = false;
            object policy = ReadChromePolicy("HardwareAccelerationModeEnabled");
            if (policy != null)
            {
                enabled = Convert.ToInt32(policy) != 0;
                return true;
            }

            string path = GetChromeLocalStatePath();
            if (!File.Exists(path))
            {
                return false;
            }
            return TryReadHardwareAcceleration(ReadSharedText(path), out enabled);
        }

        private static object ReadChromePolicy(string name)
        {
            const string path = @"Software\Policies\Google\Chrome";
            using (RegistryKey user = Registry.CurrentUser.OpenSubKey(path))
            {
                object value = user == null ? null : user.GetValue(name);
                if (value != null)
                {
                    return value;
                }
            }
            using (RegistryKey machine = Registry.LocalMachine.OpenSubKey(path))
            {
                return machine == null ? null : machine.GetValue(name);
            }
        }

        private static string GetChromeLocalStatePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "Local State");
        }

        private static string ReadSharedText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteJsonAtomically(string path, string json)
        {
            string directory = Path.GetDirectoryName(path);
            string temporary = Path.Combine(directory, Path.GetFileName(path) + ".guardcenter-" + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = Path.Combine(directory, Path.GetFileName(path) + ".guardcenter-backup");
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Replace(temporary, path, backup, true);
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
            catch
            {
                if (File.Exists(backup) && !File.Exists(path))
                {
                    File.Move(backup, path);
                }
                throw;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static bool IsChromeRunning()
        {
            Process[] processes = Process.GetProcessesByName("chrome");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    processes[i].Dispose();
                }
            }
        }

        private static void ReadNvidiaState(VsrGuardSnapshot snapshot)
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion, Status, ConfigManagerErrorCode FROM Win32_VideoController"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject gpu in results)
                {
                    string name = Convert.ToString(gpu["Name"]);
                    string pnpDeviceId = Convert.ToString(gpu["PNPDeviceID"]);
                    bool nvidia = pnpDeviceId.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nvidia)
                    {
                        continue;
                    }

                    snapshot.NvidiaGpuPresent = true;
                    bool rtx = name.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!rtx || snapshot.NvidiaRtxSupported)
                    {
                        continue;
                    }

                    snapshot.NvidiaRtxSupported = true;
                    snapshot.NvidiaGpuName = name;
                    snapshot.NvidiaDriverVersion = Convert.ToString(gpu["DriverVersion"]);
                    uint errorCode = gpu["ConfigManagerErrorCode"] == null
                        ? uint.MaxValue : Convert.ToUInt32(gpu["ConfigManagerErrorCode"]);
                    snapshot.NvidiaGpuHealthy = errorCode == 0
                        && string.Equals(Convert.ToString(gpu["Status"]), "OK", StringComparison.OrdinalIgnoreCase);
                    snapshot.NvidiaAdapterRegistryPath = ResolveAdapterRegistryPath(pnpDeviceId);
                }
            }

            if (!snapshot.NvidiaRtxSupported || string.IsNullOrWhiteSpace(snapshot.NvidiaAdapterRegistryPath))
            {
                return;
            }

            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(snapshot.NvidiaAdapterRegistryPath))
            {
                if (key == null)
                {
                    return;
                }
                uint flags = ReadDword(key, NvidiaVideoFlagsValue, 0);
                snapshot.VsrEnabled = (flags & VsrEnabledBit) != 0;
                snapshot.VsrStateKnown = true;
            }
        }

        private static string ResolveAdapterRegistryPath(string pnpDeviceId)
        {
            if (string.IsNullOrWhiteSpace(pnpDeviceId))
            {
                return string.Empty;
            }
            using (RegistryKey device = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + pnpDeviceId))
            {
                string driver = device == null ? string.Empty : Convert.ToString(device.GetValue("Driver", string.Empty));
                if (string.IsNullOrWhiteSpace(driver)
                    || driver.IndexOf("{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) < 0
                    || driver.IndexOf("..", StringComparison.Ordinal) >= 0)
                {
                    return string.Empty;
                }
                return @"SYSTEM\CurrentControlSet\Control\Class\" + driver;
            }
        }

        private static bool TrySetDriverSetting(bool enabled, out string error)
        {
            error = string.Empty;
            try
            {
                string adapterPath = FindActiveRtxAdapterRegistryPath();
                if (string.IsNullOrWhiteSpace(adapterPath))
                {
                    error = "Could not resolve an active NVIDIA RTX display adapter registry key.";
                    return false;
                }

                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(adapterPath, true))
                {
                    if (key == null)
                    {
                        error = "The NVIDIA display adapter registry key could not be opened for writing.";
                        return false;
                    }

                    uint flags = ReadDword(key, NvidiaVideoFlagsValue, 0);
                    uint explicitFlags = ReadDword(key, NvidiaVideoFlagsExplicitValue, 0);
                    flags = enabled ? EnableVsrValue(flags) : DisableVsrValue(flags);
                    explicitFlags = enabled
                        ? EnableVsrExplicitValue(explicitFlags)
                        : DisableVsrExplicitValue(explicitFlags);
                    key.SetValue(NvidiaVideoFlagsValue, unchecked((int)flags), RegistryValueKind.DWord);
                    key.SetValue(NvidiaVideoFlagsExplicitValue, unchecked((int)explicitFlags), RegistryValueKind.DWord);
                    key.Flush();

                    uint verified = ReadDword(key, NvidiaVideoFlagsValue, 0);
                    if (((verified & VsrEnabledBit) != 0) != enabled)
                    {
                        error = "The NVIDIA VSR flag failed read-back verification.";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string FindActiveRtxAdapterRegistryPath()
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject gpu in results)
                {
                    string name = Convert.ToString(gpu["Name"]);
                    string pnp = Convert.ToString(gpu["PNPDeviceID"]);
                    uint error = gpu["ConfigManagerErrorCode"] == null
                        ? uint.MaxValue : Convert.ToUInt32(gpu["ConfigManagerErrorCode"]);
                    if (error == 0
                        && name.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0
                        && pnp.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ResolveAdapterRegistryPath(pnp);
                    }
                }
            }
            return string.Empty;
        }

        private static uint ReadDword(RegistryKey key, string name, uint fallback)
        {
            object value = key.GetValue(name);
            if (value == null)
            {
                return fallback;
            }
            return unchecked((uint)Convert.ToInt32(value));
        }

        private static async Task<int> RunElevatedSetAsync(bool enabled)
        {
            string executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return 2;
            }

            if (File.Exists(GsudoPath) && await IsGsudoCacheAvailableAsync())
            {
                var start = new ProcessStartInfo
                {
                    FileName = GsudoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("--direct");
                start.ArgumentList.Add(executable);
                start.ArgumentList.Add(enabled ? ElevatedEnableArgument : ElevatedDisableArgument);
                using (Process process = Process.Start(start))
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode;
                }
            }

            var runAs = new ProcessStartInfo
            {
                FileName = executable,
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
            runAs.ArgumentList.Add(enabled ? ElevatedEnableArgument : ElevatedDisableArgument);
            using (Process process = Process.Start(runAs))
            {
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
        }

        private static async Task<bool> IsGsudoCacheAvailableAsync()
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = GsudoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("status");
                start.ArgumentList.Add("--json");
                using (Process process = Process.Start(start))
                {
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    string output = await outputTask;
                    await errorTask;
                    if (process.ExitCode != 0)
                    {
                        return false;
                    }
                    using (JsonDocument document = JsonDocument.Parse(output))
                    {
                        JsonElement available;
                        return document.RootElement.TryGetProperty("CacheAvailable", out available)
                            && available.ValueKind == JsonValueKind.True;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProcessElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null)
            {
                return false;
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static VsrGuardActionResult Success(string message, bool restartChrome)
        {
            return new VsrGuardActionResult
            {
                Success = true,
                RestartChrome = restartChrome,
                Message = message
            };
        }

        private static VsrGuardActionResult Failure(string message)
        {
            return new VsrGuardActionResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
