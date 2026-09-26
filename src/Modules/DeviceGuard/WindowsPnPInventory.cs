using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace GuardCenter
{
    internal sealed class PnPDeviceNode
    {
        public string InstanceId = string.Empty;
        public string DisplayName = string.Empty;
        public string DeviceDescription = string.Empty;
        public string ClassName = string.Empty;
        public string ClassGuid = string.Empty;
        public string Service = string.Empty;
        public string Manufacturer = string.Empty;
        public string EnumeratorName = string.Empty;
        public string BusTypeGuid = string.Empty;
        public string ContainerId = string.Empty;
        public string ParentInstanceId = string.Empty;
        public string DriverInfPath = string.Empty;
        public string DriverVersion = string.Empty;
        public string DriverProvider = string.Empty;
        public string[] HardwareIds = Array.Empty<string>();
        public string[] CompatibleIds = Array.Empty<string>();
        public string[] LocationPaths = Array.Empty<string>();
        public uint Capabilities;
        public uint RemovalPolicy;
        public uint InstallState;
        public uint DevNodeStatus;
        public uint ProblemCode;
        public bool IsPresent;
        public bool IsStarted;

        public PnPDeviceNode Clone()
        {
            return new PnPDeviceNode
            {
                InstanceId = InstanceId,
                DisplayName = DisplayName,
                DeviceDescription = DeviceDescription,
                ClassName = ClassName,
                ClassGuid = ClassGuid,
                Service = Service,
                Manufacturer = Manufacturer,
                EnumeratorName = EnumeratorName,
                BusTypeGuid = BusTypeGuid,
                ContainerId = ContainerId,
                ParentInstanceId = ParentInstanceId,
                DriverInfPath = DriverInfPath,
                DriverVersion = DriverVersion,
                DriverProvider = DriverProvider,
                HardwareIds = HardwareIds == null ? Array.Empty<string>() : (string[])HardwareIds.Clone(),
                CompatibleIds = CompatibleIds == null ? Array.Empty<string>() : (string[])CompatibleIds.Clone(),
                LocationPaths = LocationPaths == null ? Array.Empty<string>() : (string[])LocationPaths.Clone(),
                Capabilities = Capabilities,
                RemovalPolicy = RemovalPolicy,
                InstallState = InstallState,
                DevNodeStatus = DevNodeStatus,
                ProblemCode = ProblemCode,
                IsPresent = IsPresent,
                IsStarted = IsStarted
            };
        }
    }

    internal sealed class WindowsPnPSnapshot
    {
        private readonly Dictionary<string, PnPDeviceNode> byId;
        public readonly List<PnPDeviceNode> Devices;
        public readonly DateTime CapturedUtc;

        public WindowsPnPSnapshot(IList<PnPDeviceNode> devices)
        {
            Devices = new List<PnPDeviceNode>();
            byId = new Dictionary<string, PnPDeviceNode>(StringComparer.OrdinalIgnoreCase);
            if (devices != null)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    PnPDeviceNode node = devices[i];
                    if (node == null || string.IsNullOrWhiteSpace(node.InstanceId)
                        || byId.ContainsKey(node.InstanceId))
                    {
                        continue;
                    }
                    byId[node.InstanceId] = node;
                    Devices.Add(node);
                }
            }
            CapturedUtc = DateTime.UtcNow;
        }

        public PnPDeviceNode Find(string instanceId)
        {
            PnPDeviceNode value;
            return !string.IsNullOrWhiteSpace(instanceId) && byId.TryGetValue(instanceId, out value)
                ? value
                : null;
        }

        public List<PnPDeviceNode> GetDescendants(string instanceId)
        {
            var result = new List<PnPDeviceNode>();
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return result;
            }
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { instanceId };
            pending.Enqueue(instanceId);
            while (pending.Count > 0)
            {
                string parent = pending.Dequeue();
                for (int i = 0; i < Devices.Count; i++)
                {
                    PnPDeviceNode candidate = Devices[i];
                    if (!string.Equals(candidate.ParentInstanceId, parent, StringComparison.OrdinalIgnoreCase)
                        || !visited.Add(candidate.InstanceId))
                    {
                        continue;
                    }
                    result.Add(candidate);
                    pending.Enqueue(candidate.InstanceId);
                }
            }
            return result;
        }
    }

    internal interface IWindowsPnPInventory
    {
        WindowsPnPSnapshot Capture();
    }

    internal sealed class WindowsPnPInventory : IWindowsPnPInventory
    {
        private const uint CrSuccess = 0;
        private const uint CmLocateNormal = 0;
        private const uint CmLocatePhantom = 1;
        private const uint DnStarted = 0x00000008;

        private const uint CmDrpDeviceDesc = 0x00000001;
        private const uint CmDrpHardwareId = 0x00000002;
        private const uint CmDrpCompatibleIds = 0x00000003;
        private const uint CmDrpService = 0x00000005;
        private const uint CmDrpClass = 0x00000008;
        private const uint CmDrpClassGuid = 0x00000009;
        private const uint CmDrpManufacturer = 0x0000000C;
        private const uint CmDrpFriendlyName = 0x0000000D;
        private const uint CmDrpCapabilities = 0x00000010;
        private const uint CmDrpBusTypeGuid = 0x00000014;
        private const uint CmDrpEnumeratorName = 0x00000017;
        private const uint CmDrpRemovalPolicy = 0x00000020;
        private const uint CmDrpInstallState = 0x00000023;
        private const uint CmDrpLocationPaths = 0x00000024;
        private const uint CmDrpBaseContainerId = 0x00000025;

        public WindowsPnPSnapshot Capture()
        {
            Dictionary<string, DriverMetadata> drivers = ReadDriverMetadata();
            List<string> ids = EnumerateInstalledDeviceIds();
            var nodes = new List<PnPDeviceNode>();
            for (int i = 0; i < ids.Count; i++)
            {
                uint devInst;
                if (NativeMethods.CM_Locate_DevNodeW(out devInst, ids[i], CmLocatePhantom) != CrSuccess)
                {
                    continue;
                }

                uint presentDevInst;
                bool present = NativeMethods.CM_Locate_DevNodeW(out presentDevInst, ids[i], CmLocateNormal) == CrSuccess;
                uint status = 0;
                uint problem = 0;
                if (present)
                {
                    NativeMethods.CM_Get_DevNode_Status(out status, out problem, presentDevInst, 0);
                }

                var node = new PnPDeviceNode
                {
                    InstanceId = ids[i],
                    DeviceDescription = ReadString(devInst, CmDrpDeviceDesc),
                    ClassName = ReadString(devInst, CmDrpClass),
                    ClassGuid = NormalizeGuidText(ReadString(devInst, CmDrpClassGuid)),
                    Service = ReadString(devInst, CmDrpService),
                    Manufacturer = ReadString(devInst, CmDrpManufacturer),
                    EnumeratorName = ReadString(devInst, CmDrpEnumeratorName),
                    BusTypeGuid = ReadGuid(devInst, CmDrpBusTypeGuid),
                    ContainerId = ReadGuid(devInst, CmDrpBaseContainerId),
                    HardwareIds = ReadStringList(devInst, CmDrpHardwareId),
                    CompatibleIds = ReadStringList(devInst, CmDrpCompatibleIds),
                    LocationPaths = ReadStringList(devInst, CmDrpLocationPaths),
                    Capabilities = ReadUInt32(devInst, CmDrpCapabilities),
                    RemovalPolicy = ReadUInt32(devInst, CmDrpRemovalPolicy),
                    InstallState = ReadUInt32(devInst, CmDrpInstallState),
                    DevNodeStatus = status,
                    ProblemCode = problem,
                    IsPresent = present,
                    IsStarted = present && (status & DnStarted) != 0
                };
                node.DisplayName = ReadString(devInst, CmDrpFriendlyName);
                if (string.IsNullOrWhiteSpace(node.DisplayName))
                {
                    node.DisplayName = node.DeviceDescription;
                }
                if (string.IsNullOrWhiteSpace(node.DisplayName))
                {
                    node.DisplayName = node.ClassName;
                }
                node.ParentInstanceId = present ? ReadParentInstanceId(presentDevInst) : string.Empty;

                DriverMetadata metadata;
                if (drivers.TryGetValue(node.InstanceId, out metadata))
                {
                    node.DriverInfPath = metadata.InfName;
                    node.DriverVersion = metadata.Version;
                    node.DriverProvider = metadata.Provider;
                }
                nodes.Add(node);
            }
            return new WindowsPnPSnapshot(nodes);
        }

        private static List<string> EnumerateInstalledDeviceIds()
        {
            uint length;
            uint sizeResult = NativeMethods.CM_Get_Device_ID_List_SizeW(out length, null, 0);
            if (sizeResult != CrSuccess || length == 0)
            {
                throw new InvalidOperationException("CM_Get_Device_ID_List_Size failed: " + sizeResult);
            }
            var buffer = new char[length];
            uint listResult = NativeMethods.CM_Get_Device_ID_ListW(null, buffer, length, 0);
            if (listResult != CrSuccess)
            {
                throw new InvalidOperationException("CM_Get_Device_ID_List failed: " + listResult);
            }
            string all = new string(buffer);
            var result = new List<string>();
            string[] values = all.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    result.Add(values[i]);
                }
            }
            return result;
        }

        private static string ReadParentInstanceId(uint devInst)
        {
            uint parent;
            return NativeMethods.CM_Get_Parent(out parent, devInst, 0) == CrSuccess
                ? ReadInstanceId(parent)
                : string.Empty;
        }

        private static string ReadInstanceId(uint devInst)
        {
            uint length;
            if (NativeMethods.CM_Get_Device_ID_Size(out length, devInst, 0) != CrSuccess)
            {
                return string.Empty;
            }
            var builder = new StringBuilder((int)length + 1);
            return NativeMethods.CM_Get_Device_IDW(devInst, builder, length + 1, 0) == CrSuccess
                ? builder.ToString()
                : string.Empty;
        }

        private static string ReadString(uint devInst, uint property)
        {
            uint type;
            byte[] data = ReadRegistryProperty(devInst, property, out type);
            if (data.Length == 0)
            {
                return string.Empty;
            }
            return Encoding.Unicode.GetString(data).TrimEnd('\0').Trim();
        }

        private static string[] ReadStringList(uint devInst, uint property)
        {
            uint type;
            byte[] data = ReadRegistryProperty(devInst, property, out type);
            if (data.Length == 0)
            {
                return Array.Empty<string>();
            }
            string text = Encoding.Unicode.GetString(data);
            string[] values = text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
            return result.ToArray();
        }

        private static uint ReadUInt32(uint devInst, uint property)
        {
            uint type;
            byte[] data = ReadRegistryProperty(devInst, property, out type);
            return data.Length >= 4 ? BitConverter.ToUInt32(data, 0) : 0;
        }

        private static string ReadGuid(uint devInst, uint property)
        {
            uint type;
            byte[] data = ReadRegistryProperty(devInst, property, out type);
            if (data.Length >= 16 && type == 3)
            {
                var guidBytes = new byte[16];
                Array.Copy(data, guidBytes, 16);
                return NormalizeGuidText(new Guid(guidBytes).ToString("B"));
            }
            return data.Length == 0
                ? string.Empty
                : NormalizeGuidText(Encoding.Unicode.GetString(data).TrimEnd('\0').Trim());
        }

        private static byte[] ReadRegistryProperty(uint devInst, uint property, out uint type)
        {
            type = 0;
            var buffer = new byte[65536];
            uint length = (uint)buffer.Length;
            uint result = NativeMethods.CM_Get_DevNode_Registry_PropertyW(devInst, property,
                out type, buffer, ref length, 0);
            if (result != CrSuccess || length == 0)
            {
                return Array.Empty<byte>();
            }
            var exact = new byte[length];
            Array.Copy(buffer, exact, length);
            return exact;
        }

        private static string NormalizeGuidText(string value)
        {
            Guid guid;
            return Guid.TryParse(value, out guid) ? guid.ToString("B").ToUpperInvariant() : string.Empty;
        }

        private static Dictionary<string, DriverMetadata> ReadDriverMetadata()
        {
            var result = new Dictionary<string, DriverMetadata>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, DriverVersion, InfName, DriverProviderName FROM Win32_PnPSignedDriver"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        string id = Convert.ToString(item["DeviceID"]) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }
                        result[id] = new DriverMetadata
                        {
                            Version = Convert.ToString(item["DriverVersion"]) ?? string.Empty,
                            InfName = Convert.ToString(item["InfName"]) ?? string.Empty,
                            Provider = Convert.ToString(item["DriverProviderName"]) ?? string.Empty
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Device Guard", "PnP driver metadata inventory failed: " + ex);
            }
            return result;
        }

        private sealed class DriverMetadata
        {
            public string Version = string.Empty;
            public string InfName = string.Empty;
            public string Provider = string.Empty;
        }

        private static class NativeMethods
        {
            [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
            public static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

            [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
            public static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer,
                uint bufferLength, uint flags);

            [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
            public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

            [DllImport("CfgMgr32.dll")]
            public static extern uint CM_Get_DevNode_Status(out uint status, out uint problem,
                uint devInst, uint flags);

            [DllImport("CfgMgr32.dll")]
            public static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

            [DllImport("CfgMgr32.dll")]
            public static extern uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

            [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
            public static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer,
                uint bufferLength, uint flags);

            [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
            public static extern uint CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property,
                out uint registryDataType, [Out] byte[] buffer, ref uint bufferLength, uint flags);
        }
    }
}
