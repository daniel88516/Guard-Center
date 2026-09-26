using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace GuardCenter
{
    internal interface IInputStackInventory
    {
        InputStackSnapshot Capture(WindowsPnPSnapshot pnpSnapshot);
    }

    internal sealed class WindowsInputStackInventory : IInputStackInventory
    {
        private const string KeyboardClassRegistry =
            @"SYSTEM\CurrentControlSet\Control\Class\{4D36E96B-E325-11CE-BFC1-08002BE10318}";
        private const string MouseClassRegistry =
            @"SYSTEM\CurrentControlSet\Control\Class\{4D36E96F-E325-11CE-BFC1-08002BE10318}";
        private const string KernelPnpDeviceManagementLog =
            "Microsoft-Windows-Kernel-PnP/Device Management";
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        private readonly IWindowsObjectNamespace objectNamespace;

        public WindowsInputStackInventory()
            : this(new WindowsObjectNamespace())
        {
        }

        internal WindowsInputStackInventory(IWindowsObjectNamespace objectNamespace)
        {
            this.objectNamespace = objectNamespace;
        }

        public InputStackSnapshot Capture(WindowsPnPSnapshot pnpSnapshot)
        {
            var result = new InputStackSnapshot
            {
                CapturedAt = DateTime.Now,
                BootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64),
                KeyboardUpperFilters = ReadUpperFilters(KeyboardClassRegistry),
                MouseUpperFilters = ReadUpperFilters(MouseClassRegistry)
            };

            ReadDriver(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "drivers", "keyboard.sys"), out result.KeyboardDriverExists,
                out result.KeyboardDriverVersion, out result.KeyboardDriverProduct,
                out result.KeyboardDriverSignatureValid);
            ReadDriver(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "drivers", "mouse.sys"), out result.MouseDriverExists,
                out result.MouseDriverVersion, out result.MouseDriverProduct,
                out result.MouseDriverSignatureValid);

            result.KeyboardInterceptionInstalled = Contains(result.KeyboardUpperFilters, "keyboard")
                && IsInterception(result.KeyboardDriverProduct);
            result.MouseInterceptionInstalled = Contains(result.MouseUpperFilters, "mouse")
                && IsInterception(result.MouseDriverProduct);

            var errors = new List<string>();
            try
            {
                string[] objects = objectNamespace.EnumerateDeviceObjects();
                result.KeyboardClassDevices = FilterClassObjects(objects, "KeyboardClass");
                result.PointerClassDevices = FilterClassObjects(objects, "PointerClass");
                result.HighestKeyboardClassIndex = HighestIndex(result.KeyboardClassDevices, "KeyboardClass");
                result.HighestPointerClassIndex = HighestIndex(result.PointerClassDevices, "PointerClass");
            }
            catch (Exception ex)
            {
                errors.Add("Object Manager inventory failed: " + ex.Message);
            }

            try
            {
                CountSurpriseRemovals(result, pnpSnapshot);
            }
            catch (Exception ex)
            {
                errors.Add("Kernel-PnP history failed: " + ex.Message);
            }

            try
            {
                HuaJuanCompatibilitySnapshot compatibility =
                    new HuaJuanCompatibilityEngine().Inspect(result.BootTime,
                        result.KeyboardUpperFilters, result.MouseUpperFilters);
                result.HuaJuanInstalled = compatibility.Installed;
                result.HuaJuanPayloadAvailable = compatibility.PayloadAvailable;
                result.HuaJuanBackupAvailable = compatibility.BackupAvailable;
                result.HuaJuanKeyboardIsolated = compatibility.KeyboardIsolated;
                result.HuaJuanMouseInterceptionRetained =
                    compatibility.MouseInterceptionRetained;
                result.HuaJuanCompatibleDllCount = compatibility.CompatibleDllCount;
                result.HuaJuanInterceptionDllCount = compatibility.InterceptionDllCount;
                result.HuaJuanRepairAppliedAt = compatibility.AppliedAt;
                result.HuaJuanCompatibilityState = compatibility.State;
                result.HuaJuanCompatibilitySummary = compatibility.Summary;
            }
            catch (Exception ex)
            {
                errors.Add("HuaJuan compatibility inventory failed: " + ex.Message);
                result.HuaJuanCompatibilityState = HuaJuanCompatibilityState.Unknown;
                result.HuaJuanCompatibilitySummary = "無法確認鍵盤 Interception 隔離狀態。";
            }

            result.DiagnosticError = string.Join(Environment.NewLine, errors.ToArray());
            InputStackHealthAnalyzer.Analyze(result);
            return result;
        }

        private static void CountSurpriseRemovals(InputStackSnapshot result,
            WindowsPnPSnapshot pnpSnapshot)
        {
            string utc = result.BootTime.ToUniversalTime().ToString("O");
            string query = "*[System[(EventID=1010) and TimeCreated[@SystemTime >= '" + utc + "']]]";
            var knownInputIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (pnpSnapshot != null)
            {
                for (int i = 0; i < pnpSnapshot.Devices.Count; i++)
                {
                    PnPDeviceNode node = pnpSnapshot.Devices[i];
                    if (string.Equals(node.ClassName, "Keyboard", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(node.ClassName, "Mouse", StringComparison.OrdinalIgnoreCase))
                    {
                        knownInputIds.Add(node.InstanceId);
                    }
                }
            }

            using (var reader = new EventLogReader(new EventLogQuery(
                KernelPnpDeviceManagementLog, PathType.LogName, query)))
            {
                for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        string instanceId = ReadEventData(record.ToXml(), "DeviceInstanceId");
                        if (instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
                            result.HidSurpriseRemovalCount++;
                        if (knownInputIds.Contains(instanceId))
                            result.InputSurpriseRemovalCount++;
                    }
                }
            }
        }

        private static string ReadEventData(string xml, string name)
        {
            XDocument document = XDocument.Parse(xml);
            foreach (XElement element in document.Descendants())
            {
                XAttribute attribute = element.Attribute("Name");
                if (element.Name.LocalName == "Data" && attribute != null
                    && string.Equals(attribute.Value, name, StringComparison.Ordinal))
                {
                    return element.Value ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static string[] ReadUpperFilters(string path)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path, false))
            {
                object value = key == null ? null : key.GetValue("UpperFilters");
                if (value is string[] multiple) return multiple;
                if (value is string single && !string.IsNullOrWhiteSpace(single))
                    return new[] { single };
                return Array.Empty<string>();
            }
        }

        private static string[] FilterClassObjects(string[] objects, string prefix)
        {
            var result = new List<string>();
            if (objects != null)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    int ignored;
                    if (TryParseIndex(objects[i], prefix, out ignored)) result.Add(objects[i]);
                }
            }
            result.Sort(delegate(string left, string right)
            {
                int leftIndex;
                int rightIndex;
                TryParseIndex(left, prefix, out leftIndex);
                TryParseIndex(right, prefix, out rightIndex);
                return leftIndex.CompareTo(rightIndex);
            });
            return result.ToArray();
        }

        private static int HighestIndex(string[] values, string prefix)
        {
            int highest = -1;
            for (int i = 0; i < values.Length; i++)
            {
                int value;
                if (TryParseIndex(values[i], prefix, out value) && value > highest) highest = value;
            }
            return highest;
        }

        internal static bool TryParseIndex(string value, string prefix, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return int.TryParse(value.Substring(prefix.Length), out index) && index >= 0;
        }

        private static bool Contains(string[] values, string wanted)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsInterception(string product)
        {
            return product.IndexOf("Interception", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ReadDriver(string path, out bool exists, out string version,
            out string product, out bool signatureValid)
        {
            exists = File.Exists(path);
            version = string.Empty;
            product = string.Empty;
            signatureValid = false;
            if (!exists) return;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            version = info.FileVersion ?? string.Empty;
            product = info.ProductName ?? string.Empty;
            signatureValid = VerifyEmbeddedSignature(path);
        }

        private static bool VerifyEmbeddedSignature(string path)
        {
            IntPtr fileInfoPointer = IntPtr.Zero;
            WinTrustFileInfo fileInfo = default;
            try
            {
                fileInfo = new WinTrustFileInfo(path);
                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                var trustData = new WinTrustData(fileInfoPointer);
                Guid action = WinTrustActionGenericVerifyV2;
                return NativeMethods.WinVerifyTrust(new IntPtr(-1), ref action, ref trustData) == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPointer);
                if (fileInfo.FilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo.FilePath);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;

            public WinTrustFileInfo(string path)
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
                FilePath = Marshal.StringToCoTaskMemUni(path);
                FileHandle = IntPtr.Zero;
                KnownSubject = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public string UrlReference;
            public uint ProviderFlags;
            public uint UiContext;

            public WinTrustData(IntPtr fileInfo)
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>();
                PolicyCallbackData = IntPtr.Zero;
                SipClientData = IntPtr.Zero;
                UiChoice = 2;
                RevocationChecks = 0;
                UnionChoice = 1;
                FileInfo = fileInfo;
                StateAction = 0;
                StateData = IntPtr.Zero;
                UrlReference = null;
                ProviderFlags = 0x00000020;
                UiContext = 0;
            }
        }

        private static class NativeMethods
        {
            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true,
                CharSet = CharSet.Unicode)]
            public static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId,
                ref WinTrustData trustData);
        }
    }

    internal static class InputStackHealthAnalyzer
    {
        public static void Analyze(InputStackSnapshot snapshot)
        {
            if (snapshot == null) return;
            snapshot.Health = string.IsNullOrWhiteSpace(snapshot.DiagnosticError)
                ? InputStackHealth.Healthy : InputStackHealth.Unknown;
            if (!string.IsNullOrWhiteSpace(snapshot.HuaJuanCompatibilitySummary))
                snapshot.Summary = snapshot.HuaJuanCompatibilitySummary;
            else
                snapshot.Summary = snapshot.KeyboardInterceptionInstalled
                    ? "鍵盤仍經過 Interception；可隔離鍵盤，並將 HuaJuan 滑鼠改為輸出專用硬體身分模式。"
                    : "鍵盤已直接使用 Windows kbdclass。";
        }
    }
}
