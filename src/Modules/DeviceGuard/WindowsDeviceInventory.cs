using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GuardCenter
{
    internal interface IDeviceGuardInventory
    {
        List<DeviceGuardDevice> Scan();
        DeviceGuardDevice Resolve(DeviceRepairTarget target);
        bool TryReadDevNodeStatus(string instanceId, out uint status, out uint problemCode, out string error);
    }

    internal sealed class WindowsDeviceInventory : IDeviceGuardInventory
    {
        private const uint CmProblemDisabled = 22;
        private const uint CmProblemFailedInstall = 28;
        private readonly IWindowsPnPInventory pnpInventory;

        public WindowsDeviceInventory()
            : this(new WindowsPnPInventory())
        {
        }

        internal WindowsDeviceInventory(IWindowsPnPInventory pnpInventory)
        {
            this.pnpInventory = pnpInventory;
        }

        public List<DeviceGuardDevice> Scan()
        {
            return Scan(pnpInventory.Capture());
        }

        internal List<DeviceGuardDevice> Scan(WindowsPnPSnapshot snapshot)
        {
            var result = new List<DeviceGuardDevice>();
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                PnPDeviceNode item = snapshot.Devices[i];
                if (!item.IsPresent)
                {
                    continue;
                }
                DeviceGuardKind kind;
                string category;
                bool includeInRepairAll;
                if (!DeviceGuardPolicy.TryClassify(item.ClassName, item.Service, item.InstanceId,
                    out kind, out category, out includeInRepairAll))
                {
                    continue;
                }

                string unsupported = string.Empty;
                bool canRepair = true;
                if (item.ProblemCode == CmProblemDisabled)
                {
                    canRepair = false;
                    unsupported = "This device is disabled. Use Computer Core Hardware to enable it safely.";
                }
                else if (item.ProblemCode == CmProblemFailedInstall)
                {
                    canRepair = false;
                    unsupported = "The driver is not installed correctly. Use Computer Core Hardware for recovery.";
                }

                result.Add(new DeviceGuardDevice
                {
                    Kind = kind,
                    CategoryName = category,
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? category : item.DisplayName,
                    InstanceId = item.InstanceId,
                    RuntimeId = CreateRuntimeId(kind, item.InstanceId),
                    Service = item.Service,
                    Manufacturer = item.Manufacturer,
                    DriverVersion = item.DriverVersion,
                    HardwareIds = item.HardwareIds == null ? Array.Empty<string>() : (string[])item.HardwareIds.Clone(),
                    ProblemCode = item.ProblemCode,
                    IsPresent = item.IsPresent,
                    IsStarted = item.IsStarted,
                    CanRepair = canRepair,
                    IncludeInRepairAll = includeInRepairAll && canRepair,
                    UnsupportedReason = unsupported,
                    RepairState = canRepair ? DeviceRepairState.Ready : DeviceRepairState.Unsupported
                });
            }

            result.Sort(delegate(DeviceGuardDevice left, DeviceGuardDevice right)
            {
                int category = left.Kind.CompareTo(right.Kind);
                return category != 0
                    ? category
                    : string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        public DeviceGuardDevice Resolve(DeviceRepairTarget target)
        {
            if (target == null)
            {
                return null;
            }

            List<DeviceGuardDevice> devices = Scan();
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].Kind == target.Kind
                    && string.Equals(devices[i].InstanceId, target.ExpectedInstanceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return devices[i];
                }
            }

            DeviceGuardDevice match = null;
            for (int i = 0; i < devices.Count; i++)
            {
                DeviceGuardDevice candidate = devices[i];
                if (candidate.Kind != target.Kind || !HardwareIdsOverlap(candidate.HardwareIds, target.ExpectedHardwareIds))
                {
                    continue;
                }

                if (match != null)
                {
                    return null;
                }
                match = candidate;
            }

            return match;
        }

        public bool TryReadDevNodeStatus(string instanceId, out uint status, out uint problemCode, out string error)
        {
            status = 0;
            problemCode = 0;
            error = string.Empty;
            uint devInst;
            uint locate = NativeMethods.CM_Locate_DevNodeW(out devInst, instanceId, 0);
            if (locate != 0)
            {
                error = "CM_Locate_DevNode failed: " + locate;
                return false;
            }

            uint read = NativeMethods.CM_Get_DevNode_Status(out status, out problemCode, devInst, 0);
            if (read != 0)
            {
                error = "CM_Get_DevNode_Status failed: " + read;
                return false;
            }
            return true;
        }

        internal static string CreateRuntimeId(DeviceGuardKind kind, string instanceId)
        {
            return kind + ":" + (instanceId ?? string.Empty).ToUpperInvariant();
        }

        private static bool HardwareIdsOverlap(string[] left, string[] right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                for (int j = 0; j < right.Length; j++)
                {
                    if (string.Equals(left[i], right[j], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static class NativeMethods
        {
            [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
            public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

            [DllImport("CfgMgr32.dll")]
            public static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber,
                uint devInst, uint flags);
        }
    }

    internal static class DeviceGuardPolicy
    {
        public static bool TryClassify(string pnpClass, string service, string instanceId,
            out DeviceGuardKind kind, out string category, out bool includeInRepairAll)
        {
            if (string.Equals(pnpClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)
                && string.Equals(service, "BTHUSB", StringComparison.OrdinalIgnoreCase))
            {
                kind = DeviceGuardKind.BluetoothAdapter;
                category = "Bluetooth adapter";
                includeInRepairAll = true;
                return true;
            }
            if ((string.Equals(pnpClass, "Camera", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pnpClass, "Image", StringComparison.OrdinalIgnoreCase))
                && string.Equals(service, "usbvideo", StringComparison.OrdinalIgnoreCase))
            {
                kind = DeviceGuardKind.Camera;
                category = "Camera";
                includeInRepairAll = true;
                return true;
            }
            if (string.Equals(pnpClass, "MEDIA", StringComparison.OrdinalIgnoreCase)
                && string.Equals(service, "usbaudio2", StringComparison.OrdinalIgnoreCase)
                && (instanceId ?? string.Empty).StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                kind = DeviceGuardKind.UsbAudio;
                category = "USB audio";
                includeInRepairAll = true;
                return true;
            }

            kind = DeviceGuardKind.BluetoothAdapter;
            category = string.Empty;
            includeInRepairAll = false;
            return false;
        }
    }
}
