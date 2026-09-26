using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace GuardCenter
{
    internal interface IDisplayGuardProvider
    {
        List<DisplayGuardDevice> EnumerateMonitors(bool writeDiagnosticLog = true);
    }

    internal interface IDisplayGuardControlEndpoint : IDisposable
    {
        string Kind { get; }
        bool TryReadBrightness(out int minimum, out int current, out int maximum, out string error);
        bool TrySetBrightness(int value, out string error);
        bool TryReadContrast(out int minimum, out int current, out int maximum, out string error);
        bool TrySetContrast(int value, out string error);
    }

    internal sealed class DisplayGuardDevice : IDisposable
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
        public int BrightnessMinimum;
        public int BrightnessMaximum;
        public int BrightnessCurrent;
        public int ContrastMinimum;
        public int ContrastMaximum;
        public int ContrastCurrent;
        public string LastError = string.Empty;
        public bool IsBusy;
        public IDisplayGuardControlEndpoint BrightnessEndpoint;
        public IDisplayGuardControlEndpoint ContrastEndpoint;

        public DisplayGuardMonitorInfo ToInfo()
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
                BrightnessPercent = SupportsBrightness
                    ? NativeToPercent(BrightnessMinimum, BrightnessMaximum, BrightnessCurrent)
                    : 0,
                ContrastPercent = SupportsContrast
                    ? NativeToPercent(ContrastMinimum, ContrastMaximum, ContrastCurrent)
                    : 0,
                BrightnessMinimum = BrightnessMinimum,
                BrightnessMaximum = BrightnessMaximum,
                ContrastMinimum = ContrastMinimum,
                ContrastMaximum = ContrastMaximum,
                LastError = LastError,
                IsBusy = IsBusy
            };
        }

        public bool SetBrightnessPercent(int percent, out string error)
        {
            error = string.Empty;
            if (!SupportsBrightness || BrightnessEndpoint == null)
            {
                error = "Brightness is not supported.";
                LastError = error;
                return false;
            }

            int native = PercentToNative(BrightnessMinimum, BrightnessMaximum, percent);
            if (!BrightnessEndpoint.TrySetBrightness(native, out error))
            {
                LastError = error;
                return false;
            }

            int minimum;
            int current;
            int maximum;
            string readError;
            if (!BrightnessEndpoint.TryReadBrightness(out minimum, out current, out maximum, out readError))
            {
                error = "Brightness was written, but readback failed: " + readError;
                LastError = error;
                return false;
            }

            BrightnessMinimum = minimum;
            BrightnessCurrent = current;
            BrightnessMaximum = maximum;
            if (!ValuesMatch(native, current, minimum, maximum))
            {
                error = "Brightness readback did not match the requested value (requested "
                    + native + ", read " + current + ").";
                LastError = error;
                return false;
            }

            LastError = string.Empty;
            return true;
        }

        public bool SetContrastPercent(int percent, out string error)
        {
            error = string.Empty;
            if (!SupportsContrast || ContrastEndpoint == null)
            {
                error = "Contrast is not supported.";
                LastError = error;
                return false;
            }

            int native = PercentToNative(ContrastMinimum, ContrastMaximum, percent);
            if (!ContrastEndpoint.TrySetContrast(native, out error))
            {
                LastError = error;
                return false;
            }

            int minimum;
            int current;
            int maximum;
            string readError;
            if (!ContrastEndpoint.TryReadContrast(out minimum, out current, out maximum, out readError))
            {
                error = "Contrast was written, but readback failed: " + readError;
                LastError = error;
                return false;
            }

            ContrastMinimum = minimum;
            ContrastCurrent = current;
            ContrastMaximum = maximum;
            if (!ValuesMatch(native, current, minimum, maximum))
            {
                error = "Contrast readback did not match the requested value (requested "
                    + native + ", read " + current + ").";
                LastError = error;
                return false;
            }

            LastError = string.Empty;
            return true;
        }

        public void TakeBrightnessFrom(DisplayGuardDevice source, bool preferSource)
        {
            if (source == null || !source.SupportsBrightness)
            {
                return;
            }

            if (!preferSource && SupportsBrightness)
            {
                return;
            }

            DisposeEndpointIfUnused(BrightnessEndpoint, ContrastEndpoint);
            BrightnessEndpoint = source.BrightnessEndpoint;
            source.BrightnessEndpoint = null;
            SupportsBrightness = true;
            BrightnessMinimum = source.BrightnessMinimum;
            BrightnessCurrent = source.BrightnessCurrent;
            BrightnessMaximum = source.BrightnessMaximum;
            UpdateControlKind();
        }

        public void TakeContrastFrom(DisplayGuardDevice source)
        {
            if (source == null || !source.SupportsContrast || SupportsContrast)
            {
                return;
            }

            ContrastEndpoint = source.ContrastEndpoint;
            source.ContrastEndpoint = null;
            SupportsContrast = true;
            ContrastMinimum = source.ContrastMinimum;
            ContrastCurrent = source.ContrastCurrent;
            ContrastMaximum = source.ContrastMaximum;
            UpdateControlKind();
        }

        public void Dispose()
        {
            DisposeEndpointIfUnused(BrightnessEndpoint, ContrastEndpoint);
            DisposeEndpointIfUnused(ContrastEndpoint, null);
            BrightnessEndpoint = null;
            ContrastEndpoint = null;
        }

        private void UpdateControlKind()
        {
            string brightness = BrightnessEndpoint == null ? string.Empty : BrightnessEndpoint.Kind;
            string contrast = ContrastEndpoint == null ? string.Empty : ContrastEndpoint.Kind;
            if (!string.IsNullOrWhiteSpace(brightness) && !string.IsNullOrWhiteSpace(contrast)
                && !string.Equals(brightness, contrast, StringComparison.OrdinalIgnoreCase))
            {
                ControlKind = brightness + " + " + contrast;
            }
            else if (!string.IsNullOrWhiteSpace(brightness))
            {
                ControlKind = brightness;
            }
            else if (!string.IsNullOrWhiteSpace(contrast))
            {
                ControlKind = contrast;
            }
        }

        private static void DisposeEndpointIfUnused(IDisplayGuardControlEndpoint endpoint,
            IDisplayGuardControlEndpoint stillUsedEndpoint)
        {
            if (endpoint != null && !ReferenceEquals(endpoint, stillUsedEndpoint))
            {
                endpoint.Dispose();
            }
        }

        private static int NativeToPercent(int minimum, int maximum, int value)
        {
            if (maximum <= minimum)
            {
                return 0;
            }

            double percent = ((double)(value - minimum) * 100.0) / (double)(maximum - minimum);
            return Math.Max(0, Math.Min(100, (int)Math.Round(percent)));
        }

        private static int PercentToNative(int minimum, int maximum, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            if (maximum <= minimum)
            {
                return minimum;
            }

            return minimum + (int)Math.Round(((double)(maximum - minimum) * percent) / 100.0);
        }

        private static bool ValuesMatch(int requested, int actual, int minimum, int maximum)
        {
            int tolerance = Math.Max(1, Math.Abs(maximum - minimum) / 100);
            return Math.Abs(requested - actual) <= tolerance;
        }
    }

    internal sealed class CompositeDisplayGuardProvider : IDisplayGuardProvider
    {
        private readonly IDisplayGuardProvider[] providers;

        public CompositeDisplayGuardProvider()
            : this(new IDisplayGuardProvider[] { new NativeDisplayGuardProvider(), new WmiDisplayGuardProvider() })
        {
        }

        public CompositeDisplayGuardProvider(IDisplayGuardProvider[] providers)
        {
            this.providers = providers ?? new IDisplayGuardProvider[0];
        }

        public List<DisplayGuardDevice> EnumerateMonitors(bool writeDiagnosticLog = true)
        {
            var result = new List<DisplayGuardDevice>();
            var byStableId = new Dictionary<string, DisplayGuardDevice>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < providers.Length; i++)
            {
                List<DisplayGuardDevice> devices;
                try
                {
                    devices = providers[i].EnumerateMonitors(writeDiagnosticLog);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Display Guard provider failed: " + ex);
                    if (writeDiagnosticLog)
                    {
                        AppLog.Write("Display Guard", "Provider failed: " + ex);
                    }
                    continue;
                }

                for (int j = 0; j < devices.Count; j++)
                {
                    DisplayGuardDevice device = devices[j];
                    if (device.StableIdReliable && !string.IsNullOrWhiteSpace(device.StableId))
                    {
                        DisplayGuardDevice existing;
                        if (byStableId.TryGetValue(device.StableId, out existing))
                        {
                            bool preferBrightness = string.Equals(device.ControlKind, "WMI", StringComparison.OrdinalIgnoreCase);
                            existing.TakeBrightnessFrom(device, preferBrightness);
                            existing.TakeContrastFrom(device);
                            device.Dispose();
                            continue;
                        }

                        byStableId[device.StableId] = device;
                    }

                    result.Add(device);
                }
            }

            return result;
        }
    }

    internal sealed class NativeDisplayGuardProvider : IDisplayGuardProvider
    {
        public List<DisplayGuardDevice> EnumerateMonitors(bool writeDiagnosticLog = true)
        {
            Dictionary<string, DisplayConfigTargetInfo> targets = DisplayConfigReader.GetActiveTargetsBySourceName();
            List<MonitorEnumRecord> monitors = EnumerateMonitorHandles();
            var result = new List<DisplayGuardDevice>();

            for (int i = 0; i < monitors.Count; i++)
            {
                MonitorEnumRecord monitor = monitors[i];
                DisplayConfigTargetInfo target;
                targets.TryGetValue(monitor.DeviceName, out target);

                int count;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.Handle, out count) || count <= 0)
                {
                    if (writeDiagnosticLog)
                    {
                        AppLog.Write("Display Guard", monitor.DeviceName + ": no physical monitor handle, error="
                            + Marshal.GetLastWin32Error());
                    }
                    result.Add(CreateScreenOnlyDevice(monitor, target));
                    continue;
                }

                NativeMethods.PHYSICAL_MONITOR[] physical;
                string physicalError;
                if (!TryGetPhysicalMonitors(monitor.Handle, count, writeDiagnosticLog, out physical, out physicalError))
                {
                    if (writeDiagnosticLog)
                    {
                        AppLog.Write("Display Guard", monitor.DeviceName + ": physical monitor enumeration failed: "
                            + physicalError);
                    }
                    result.Add(CreateScreenOnlyDevice(monitor, target));
                    continue;
                }

                for (int index = 0; index < physical.Length; index++)
                {
                    NativeMonitorEndpoint endpoint = new NativeMonitorEndpoint(physical[index].hPhysicalMonitor);
                    DisplayGuardDevice device = CreateNativeDevice(monitor, target, physical[index].szPhysicalMonitorDescription,
                        index, physical.Length, endpoint, writeDiagnosticLog);
                    if (!device.SupportsBrightness && !device.SupportsContrast)
                    {
                        endpoint.Dispose();
                        device.BrightnessEndpoint = null;
                        device.ContrastEndpoint = null;
                    }

                    result.Add(device);
                }
            }

            return result;
        }

        private static bool TryGetPhysicalMonitors(IntPtr logicalMonitor, int count, bool writeDiagnosticLog,
            out NativeMethods.PHYSICAL_MONITOR[] monitors, out string error)
        {
            monitors = new NativeMethods.PHYSICAL_MONITOR[0];
            error = string.Empty;
            int structSize = Marshal.SizeOf(typeof(NativeMethods.PHYSICAL_MONITOR));
            IntPtr buffer = Marshal.AllocHGlobal(checked(structSize * count));
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    for (int offset = 0; offset < structSize * count; offset++)
                    {
                        Marshal.WriteByte(buffer, offset, 0);
                    }

                    if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, buffer))
                    {
                        error = "Monitor Configuration API failed: " + Marshal.GetLastWin32Error();
                        continue;
                    }

                    var result = new NativeMethods.PHYSICAL_MONITOR[count];
                    bool valid = true;
                    for (int index = 0; index < count; index++)
                    {
                        IntPtr item = IntPtr.Add(buffer, index * structSize);
                        result[index] = Marshal.PtrToStructure<NativeMethods.PHYSICAL_MONITOR>(item);
                        if (result[index].hPhysicalMonitor == IntPtr.Zero)
                        {
                            valid = false;
                            for (int disposeIndex = 0; disposeIndex < index; disposeIndex++)
                            {
                                NativeMethods.DestroyPhysicalMonitor(result[disposeIndex].hPhysicalMonitor);
                            }
                            error = "Windows returned an invalid physical monitor handle.";
                            break;
                        }
                    }

                    if (valid)
                    {
                        monitors = result;
                        return true;
                    }

                    if (writeDiagnosticLog)
                    {
                        AppLog.Write("Display Guard", "Windows returned a null physical monitor handle; retrying enumeration.");
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static DisplayGuardDevice CreateNativeDevice(MonitorEnumRecord monitor, DisplayConfigTargetInfo target,
            string physicalDescription, int physicalIndex, int physicalCount, NativeMonitorEndpoint endpoint,
            bool writeDiagnosticLog)
        {
            string stableId = target == null ? string.Empty : target.StableId;
            bool reliable = physicalCount == 1 && !string.IsNullOrWhiteSpace(stableId);
            string name = GetBestName(target, physicalDescription, monitor.DeviceName);
            var device = new DisplayGuardDevice
            {
                RuntimeId = "Native:" + monitor.DeviceName + ":" + physicalIndex,
                StableId = stableId,
                StableIdReliable = reliable,
                DisplayName = name,
                SourceName = monitor.DeviceName,
                IsPrimary = monitor.IsPrimary,
                ControlKind = "DDC/CI"
            };

            int minimum;
            int current;
            int maximum;
            string error;
            if (endpoint.TryReadBrightness(out minimum, out current, out maximum, out error))
            {
                device.SupportsBrightness = true;
                device.BrightnessMinimum = minimum;
                device.BrightnessCurrent = current;
                device.BrightnessMaximum = maximum;
                device.BrightnessEndpoint = endpoint;
            }
            else
            {
                device.LastError = error;
                if (writeDiagnosticLog)
                {
                    AppLog.Write("Display Guard", name + " brightness unsupported: " + error);
                }
            }

            if (endpoint.TryReadContrast(out minimum, out current, out maximum, out error))
            {
                device.SupportsContrast = true;
                device.ContrastMinimum = minimum;
                device.ContrastCurrent = current;
                device.ContrastMaximum = maximum;
                device.ContrastEndpoint = endpoint;
            }
            else if (string.IsNullOrWhiteSpace(device.LastError))
            {
                device.LastError = error;
                if (writeDiagnosticLog)
                {
                    AppLog.Write("Display Guard", name + " contrast unsupported: " + error);
                }
            }

            return device;
        }

        private static DisplayGuardDevice CreateScreenOnlyDevice(MonitorEnumRecord monitor, DisplayConfigTargetInfo target)
        {
            string stableId = target == null ? string.Empty : target.StableId;
            return new DisplayGuardDevice
            {
                RuntimeId = "Screen:" + monitor.DeviceName,
                StableId = stableId,
                StableIdReliable = !string.IsNullOrWhiteSpace(stableId),
                DisplayName = GetBestName(target, string.Empty, monitor.DeviceName),
                SourceName = monitor.DeviceName,
                IsPrimary = monitor.IsPrimary,
                ControlKind = "Unavailable",
                LastError = "No physical monitor handle was available."
            };
        }

        private static string GetBestName(DisplayConfigTargetInfo target, string physicalDescription, string fallback)
        {
            if (target != null && !string.IsNullOrWhiteSpace(target.FriendlyName))
            {
                return target.FriendlyName;
            }

            if (!string.IsNullOrWhiteSpace(physicalDescription)
                && !string.Equals(physicalDescription, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
            {
                return physicalDescription;
            }

            if (target != null && !string.IsNullOrWhiteSpace(target.DevicePath))
            {
                return target.DevicePath;
            }

            return fallback;
        }

        private static List<MonitorEnumRecord> EnumerateMonitorHandles()
        {
            var result = new List<MonitorEnumRecord>();
            NativeMethods.MonitorEnumProc proc = delegate(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData)
            {
                var info = new NativeMethods.MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    result.Add(new MonitorEnumRecord
                    {
                        Handle = hMonitor,
                        DeviceName = info.szDevice,
                        IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0
                    });
                }

                return true;
            };

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            return result;
        }
    }

    internal sealed class WmiDisplayGuardProvider : IDisplayGuardProvider
    {
        public List<DisplayGuardDevice> EnumerateMonitors(bool writeDiagnosticLog = true)
        {
            var identities = ReadIdentities(writeDiagnosticLog);
            var activeStableIds = DisplayConfigReader.GetActiveStableIds();
            var result = new List<DisplayGuardDevice>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        string instanceName = Convert.ToString(item["InstanceName"]);
                        string stableId = DisplayGuardIdentity.NormalizeStableId(instanceName);
                        if (string.IsNullOrWhiteSpace(stableId))
                        {
                            continue;
                        }
                        if (activeStableIds.Count > 0 && !activeStableIds.Contains(stableId))
                        {
                            if (writeDiagnosticLog)
                            {
                                AppLog.Write("Display Guard", "Skipping inactive WMI brightness target " + stableId + ".");
                            }
                            continue;
                        }

                        byte current = Convert.ToByte(item["CurrentBrightness"]);
                        int minimum = 0;
                        int maximum = 100;
                        Array levels = item["Level"] as Array;
                        if (levels != null && levels.Length > 0)
                        {
                            minimum = 100;
                            maximum = 0;
                            foreach (object level in levels)
                            {
                                int value = Convert.ToInt32(level);
                                minimum = Math.Min(minimum, value);
                                maximum = Math.Max(maximum, value);
                            }
                        }

                        WmiMonitorIdentity identity;
                        identities.TryGetValue(stableId, out identity);
                        string name = identity == null || string.IsNullOrWhiteSpace(identity.Name)
                            ? stableId
                            : identity.Name;

                        var endpoint = new WmiBrightnessEndpoint(instanceName);
                        result.Add(new DisplayGuardDevice
                        {
                            RuntimeId = "WMI:" + stableId,
                            StableId = stableId,
                            StableIdReliable = true,
                            DisplayName = name,
                            SourceName = string.Empty,
                            IsPrimary = false,
                            ControlKind = "WMI",
                            SupportsBrightness = true,
                            BrightnessMinimum = minimum,
                            BrightnessCurrent = current,
                            BrightnessMaximum = maximum,
                            BrightnessEndpoint = endpoint
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("WMI display brightness enumeration failed: " + ex);
                if (writeDiagnosticLog)
                {
                    AppLog.Write("Display Guard", "WMI brightness enumeration failed: " + ex);
                }
                return result;
            }

            return result;
        }

        private static Dictionary<string, WmiMonitorIdentity> ReadIdentities(bool writeDiagnosticLog)
        {
            var result = new Dictionary<string, WmiMonitorIdentity>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName, UserFriendlyName, ManufacturerName, ProductCodeID, SerialNumberID FROM WmiMonitorID"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        string stableId = DisplayGuardIdentity.NormalizeStableId(Convert.ToString(item["InstanceName"]));
                        if (string.IsNullOrWhiteSpace(stableId))
                        {
                            continue;
                        }

                        string friendly = DecodeWmiString(item["UserFriendlyName"] as Array);
                        string manufacturer = DecodeWmiString(item["ManufacturerName"] as Array);
                        string product = DecodeWmiString(item["ProductCodeID"] as Array);
                        string serial = DecodeWmiString(item["SerialNumberID"] as Array);
                        string name = !string.IsNullOrWhiteSpace(friendly)
                            ? friendly
                            : (manufacturer + " " + product).Trim();

                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = stableId;
                        }

                        if (!string.IsNullOrWhiteSpace(serial) && serial != "0")
                        {
                            name += " (" + serial + ")";
                        }

                        result[stableId] = new WmiMonitorIdentity { Name = name };
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("WMI monitor identity enumeration failed: " + ex);
                if (writeDiagnosticLog)
                {
                    AppLog.Write("Display Guard", "WMI identity enumeration failed: " + ex);
                }
            }

            return result;
        }

        private static string DecodeWmiString(Array values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            var chars = new List<char>();
            foreach (object value in values)
            {
                int code = Convert.ToInt32(value);
                if (code == 0)
                {
                    break;
                }

                chars.Add((char)code);
            }

            return new string(chars.ToArray()).Trim();
        }
    }

    internal sealed class NativeMonitorEndpoint : IDisplayGuardControlEndpoint
    {
        private IntPtr handle;

        public NativeMonitorEndpoint(IntPtr handle)
        {
            this.handle = handle;
        }

        internal IntPtr Handle
        {
            get { return handle; }
        }

        public string Kind
        {
            get { return "DDC/CI"; }
        }

        public bool TryReadBrightness(out int minimum, out int current, out int maximum, out string error)
        {
            return TryReadContinuous(delegate(IntPtr h, out int min, out int cur, out int max)
            {
                return NativeMethods.GetMonitorBrightness(h, out min, out cur, out max);
            }, out minimum, out current, out maximum, out error);
        }

        public bool TrySetBrightness(int value, out string error)
        {
            if (TrySetContinuous(delegate(IntPtr h, int v) { return NativeMethods.SetMonitorBrightness(h, v); },
                value, out error))
            {
                return true;
            }

            string highLevelError = error;
            if (handle != IntPtr.Zero && NativeMethods.SetVCPFeature(handle, 0x10, (uint)value))
            {
                AppLog.Write("Display Guard", "High-level brightness write failed; VCP 0x10 fallback succeeded. "
                    + highLevelError);
                error = string.Empty;
                return true;
            }
            error = highLevelError + " VCP 0x10 fallback failed: " + Marshal.GetLastWin32Error();
            return false;
        }

        public bool TryReadContrast(out int minimum, out int current, out int maximum, out string error)
        {
            return TryReadContinuous(delegate(IntPtr h, out int min, out int cur, out int max)
            {
                return NativeMethods.GetMonitorContrast(h, out min, out cur, out max);
            }, out minimum, out current, out maximum, out error);
        }

        public bool TrySetContrast(int value, out string error)
        {
            if (TrySetContinuous(delegate(IntPtr h, int v) { return NativeMethods.SetMonitorContrast(h, v); },
                value, out error))
            {
                return true;
            }

            string highLevelError = error;
            if (handle != IntPtr.Zero && NativeMethods.SetVCPFeature(handle, 0x12, (uint)value))
            {
                AppLog.Write("Display Guard", "High-level contrast write failed; VCP 0x12 fallback succeeded. "
                    + highLevelError);
                error = string.Empty;
                return true;
            }
            error = highLevelError + " VCP 0x12 fallback failed: " + Marshal.GetLastWin32Error();
            return false;
        }

        public void Dispose()
        {
            IntPtr current = handle;
            handle = IntPtr.Zero;
            if (current != IntPtr.Zero)
            {
                NativeMethods.DestroyPhysicalMonitor(current);
            }
        }

        private bool TryReadContinuous(ReadContinuous read, out int minimum, out int current, out int maximum,
            out string error)
        {
            minimum = 0;
            current = 0;
            maximum = 0;
            error = string.Empty;
            if (handle == IntPtr.Zero)
            {
                error = "Physical monitor handle is closed.";
                return false;
            }

            if (!read(handle, out minimum, out current, out maximum))
            {
                error = "Monitor Configuration API failed: " + Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }

        private bool TrySetContinuous(SetContinuous set, int value, out string error)
        {
            error = string.Empty;
            if (handle == IntPtr.Zero)
            {
                error = "Physical monitor handle is closed.";
                return false;
            }

            if (!set(handle, value))
            {
                error = "Monitor Configuration API failed: " + Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }

        private delegate bool ReadContinuous(IntPtr handle, out int minimum, out int current, out int maximum);
        private delegate bool SetContinuous(IntPtr handle, int value);
    }

    internal sealed class WmiBrightnessEndpoint : IDisplayGuardControlEndpoint
    {
        private readonly string instanceName;

        public WmiBrightnessEndpoint(string instanceName)
        {
            this.instanceName = instanceName;
        }

        public string Kind
        {
            get { return "WMI"; }
        }

        public bool TryReadBrightness(out int minimum, out int current, out int maximum, out string error)
        {
            minimum = 0;
            current = 0;
            maximum = 100;
            error = string.Empty;
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        if (!string.Equals(Convert.ToString(item["InstanceName"]), instanceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        current = Convert.ToInt32(item["CurrentBrightness"]);
                        Array levels = item["Level"] as Array;
                        if (levels != null && levels.Length > 0)
                        {
                            minimum = 100;
                            maximum = 0;
                            foreach (object level in levels)
                            {
                                int value = Convert.ToInt32(level);
                                minimum = Math.Min(minimum, value);
                                maximum = Math.Max(maximum, value);
                            }
                        }

                        return true;
                    }
                }

                error = "WMI brightness object was not found.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TrySetBrightness(int value, out string error)
        {
            error = string.Empty;
            value = Math.Max(0, Math.Min(100, value));
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        if (!string.Equals(Convert.ToString(item["InstanceName"]), instanceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        object returnValue = item.InvokeMethod("WmiSetBrightness", new object[] { 1U, (byte)value });
                        uint code = returnValue == null ? 0 : Convert.ToUInt32(returnValue);
                        if (code == 0)
                        {
                            return true;
                        }
                        error = "WmiSetBrightness failed with code " + code + ".";
                        return false;
                    }
                }

                error = "WMI brightness method was not found.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryReadContrast(out int minimum, out int current, out int maximum, out string error)
        {
            minimum = 0;
            current = 0;
            maximum = 0;
            error = "WMI brightness provider does not support contrast.";
            return false;
        }

        public bool TrySetContrast(int value, out string error)
        {
            error = "WMI brightness provider does not support contrast.";
            return false;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class DisplayConfigTargetInfo
    {
        public string SourceName = string.Empty;
        public string FriendlyName = string.Empty;
        public string DevicePath = string.Empty;
        public string StableId = string.Empty;
    }

    internal sealed class MonitorEnumRecord
    {
        public IntPtr Handle;
        public string DeviceName = string.Empty;
        public bool IsPrimary;
    }

    internal sealed class WmiMonitorIdentity
    {
        public string Name = string.Empty;
    }

    internal static class DisplayConfigReader
    {
        public static Dictionary<string, DisplayConfigTargetInfo> GetActiveTargetsBySourceName()
        {
            var result = new Dictionary<string, DisplayConfigTargetInfo>(StringComparer.OrdinalIgnoreCase);

            uint pathCount;
            uint modeCount;
            int status = NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                out pathCount, out modeCount);
            if (status != NativeMethods.ERROR_SUCCESS || pathCount == 0)
            {
                AddScreenFallbacks(result);
                return result;
            }

            var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
            status = NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths,
                ref modeCount, modes, IntPtr.Zero);
            if (status != NativeMethods.ERROR_SUCCESS)
            {
                AddScreenFallbacks(result);
                return result;
            }

            for (int i = 0; i < pathCount; i++)
            {
                string sourceName = GetSourceName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    continue;
                }

                DisplayConfigTargetInfo target = GetTargetName(paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
                if (target == null)
                {
                    continue;
                }

                target.SourceName = sourceName;
                result[sourceName] = target;
            }

            AddScreenFallbacks(result);
            return result;
        }

        public static HashSet<string> GetActiveStableIds()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayConfigTargetInfo target in GetActiveTargetsBySourceName().Values)
            {
                if (target != null && !string.IsNullOrWhiteSpace(target.StableId))
                {
                    result.Add(target.StableId);
                }
            }
            return result;
        }

        private static string GetSourceName(NativeMethods.LUID adapterId, uint sourceId)
        {
            var source = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            source.header.adapterId = adapterId;
            source.header.id = sourceId;

            int status = NativeMethods.DisplayConfigGetDeviceInfo(ref source);
            return status == NativeMethods.ERROR_SUCCESS ? source.viewGdiDeviceName : string.Empty;
        }

        private static DisplayConfigTargetInfo GetTargetName(NativeMethods.LUID adapterId, uint targetId)
        {
            var target = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME));
            target.header.adapterId = adapterId;
            target.header.id = targetId;

            int status = NativeMethods.DisplayConfigGetDeviceInfo(ref target);
            if (status != NativeMethods.ERROR_SUCCESS)
            {
                return null;
            }

            string stableId = DisplayGuardIdentity.NormalizeStableId(target.monitorDevicePath);
            return new DisplayConfigTargetInfo
            {
                FriendlyName = target.monitorFriendlyDeviceName ?? string.Empty,
                DevicePath = target.monitorDevicePath ?? string.Empty,
                StableId = stableId
            };
        }

        private static void AddScreenFallbacks(Dictionary<string, DisplayConfigTargetInfo> result)
        {
            Forms.Screen[] screens = Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                if (!result.ContainsKey(screens[i].DeviceName))
                {
                    result[screens[i].DeviceName] = new DisplayConfigTargetInfo
                    {
                        SourceName = screens[i].DeviceName,
                        FriendlyName = screens[i].DeviceName
                    };
                }
            }
        }
    }

    internal static class NativeMethods
    {
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        public const int ERROR_SUCCESS = 0;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint MONITORINFOF_PRIMARY = 0x00000001;

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
            MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out int numberOfPhysicalMonitors);

        [DllImport("Dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR", SetLastError = true,
            CharSet = CharSet.Unicode)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, int physicalMonitorArraySize,
            IntPtr physicalMonitorArray);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorBrightness(IntPtr handle, out int minimum, out int current, out int maximum);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool SetMonitorBrightness(IntPtr handle, int value);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorContrast(IntPtr handle, out int minimum, out int current, out int maximum);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool SetMonitorContrast(IntPtr handle, int value);

        [DllImport("Dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr handle, byte vcpCode, uint newValue);

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public int scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public int scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize;
            public RECT DesktopImageRegion;
            public RECT DesktopImageClip;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)]
            public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(0)]
            public DISPLAYCONFIG_SOURCE_MODE sourceMode;
            [FieldOffset(0)]
            public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS
        {
            public uint value;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS flags;
            public int outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }
    }
}
