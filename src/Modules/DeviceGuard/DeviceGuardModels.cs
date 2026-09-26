using System;
using System.Collections.Generic;

namespace GuardCenter
{
    internal enum DeviceGuardKind
    {
        BluetoothAdapter,
        Camera,
        UsbAudio
    }

    internal enum DeviceRepairState
    {
        Ready,
        Repairing,
        Succeeded,
        Partial,
        Skipped,
        Unsupported,
        Failed,
        RebootRequired,
        NeedsDriver,
        Ambiguous,
        RiskDeclined,
        Canceled
    }

    internal enum DeviceGuardTargetType
    {
        ConnectedDevice,
        CoreHardware
    }

    internal enum CoreHardwareCapability
    {
        Bluetooth,
        Graphics,
        Network,
        Audio,
        Camera,
        Usb
    }

    internal enum CoreHardwareHealth
    {
        Healthy,
        Degraded,
        Disabled,
        DriverMissing,
        Missing,
        RebootRequired,
        Ambiguous,
        Unknown
    }

    internal enum RepairRiskLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    internal enum DeviceRepairMode
    {
        Manual,
        RepairAll
    }

    internal enum CoreHardwareRecoveryKind
    {
        UsbDescriptorFailure
    }

    internal sealed class DeviceGuardDevice
    {
        public string RuntimeId = string.Empty;
        public DeviceGuardKind Kind;
        public string DisplayName = string.Empty;
        public string CategoryName = string.Empty;
        public string InstanceId = string.Empty;
        public string Service = string.Empty;
        public string Manufacturer = string.Empty;
        public string DriverVersion = string.Empty;
        public string[] HardwareIds = Array.Empty<string>();
        public uint ProblemCode;
        public bool IsPresent;
        public bool IsStarted;
        public bool CanRepair;
        public bool IncludeInRepairAll;
        public string UnsupportedReason = string.Empty;
        public DeviceRepairState RepairState = DeviceRepairState.Ready;
        public string ResultMessage = string.Empty;

        public DeviceGuardDevice Clone()
        {
            return new DeviceGuardDevice
            {
                RuntimeId = RuntimeId,
                Kind = Kind,
                DisplayName = DisplayName,
                CategoryName = CategoryName,
                InstanceId = InstanceId,
                Service = Service,
                Manufacturer = Manufacturer,
                DriverVersion = DriverVersion,
                HardwareIds = HardwareIds == null ? Array.Empty<string>() : (string[])HardwareIds.Clone(),
                ProblemCode = ProblemCode,
                IsPresent = IsPresent,
                IsStarted = IsStarted,
                CanRepair = CanRepair,
                IncludeInRepairAll = IncludeInRepairAll,
                UnsupportedReason = UnsupportedReason,
                RepairState = RepairState,
                ResultMessage = ResultMessage
            };
        }
    }

    internal sealed class DeviceRepairRequest
    {
        public string Token = string.Empty;
        public string OperationId = string.Empty;
        public List<DeviceRepairTarget> Targets = new List<DeviceRepairTarget>();
    }

    internal sealed class DeviceRepairControl
    {
        public string Type = string.Empty;
        public string Token = string.Empty;
        public string OperationId = string.Empty;
    }

    internal sealed class DeviceRepairTarget
    {
        public string RuntimeId = string.Empty;
        public DeviceGuardTargetType TargetType = DeviceGuardTargetType.ConnectedDevice;
        public DeviceGuardKind Kind;
        public string ExpectedInstanceId = string.Empty;
        public string[] ExpectedHardwareIds = Array.Empty<string>();
        public string CoreHardwareId = string.Empty;
        public CoreHardwareCapability CoreCapability;
        public DeviceRepairMode Mode = DeviceRepairMode.Manual;
        public RepairRiskLevel MaximumAuthorizedRisk = RepairRiskLevel.Medium;
    }

    internal sealed class DeviceRepairMessage
    {
        public string Type = string.Empty;
        public string RuntimeId = string.Empty;
        public DeviceRepairState State;
        public string Message = string.Empty;
        public int ExitCode;
    }

    internal sealed class DeviceRepairSummary
    {
        public int Succeeded;
        public int Skipped;
        public int Failed;
        public int RebootRequired;
        public int Canceled;
        public int Partial;
        public int NeedsDriver;

        public string ToStatusText()
        {
            return Succeeded + " succeeded, " + Skipped + " skipped, " + Failed + " failed"
                + (Partial > 0 ? ", " + Partial + " partial" : string.Empty)
                + (NeedsDriver > 0 ? ", " + NeedsDriver + " need driver" : string.Empty)
                + (RebootRequired > 0 ? ", " + RebootRequired + " restart required" : string.Empty)
                + (Canceled > 0 ? ", " + Canceled + " canceled" : string.Empty) + ".";
        }
    }

    internal sealed class CoreHardwareItem
    {
        public string Id = string.Empty;
        public CoreHardwareCapability Capability;
        public string DisplayName = string.Empty;
        public string AnchorInstanceId = string.Empty;
        public string[] NodeInstanceIds = Array.Empty<string>();
        public string[] HardwareIds = Array.Empty<string>();
        public string[] LocationPaths = Array.Empty<string>();
        public string ParentInstanceId = string.Empty;
        public string DriverInfPath = string.Empty;
        public string DriverVersion = string.Empty;
        public string Manufacturer = string.Empty;
        public CoreHardwareHealth Health;
        public uint ProblemCode;
        public bool IsPresent;
        public bool IsStarted;
        public bool FromBaseline;
        public bool IdentityAmbiguous;
        public CoreHardwareRecoveryCandidate RecoveryCandidate;
        public bool UsbRestartBlocked;
        public string UsbRestartBlockReason = string.Empty;
        public DeviceRepairState RepairState = DeviceRepairState.Ready;
        public string ResultMessage = string.Empty;

        public CoreHardwareItem Clone()
        {
            return new CoreHardwareItem
            {
                Id = Id,
                Capability = Capability,
                DisplayName = DisplayName,
                AnchorInstanceId = AnchorInstanceId,
                NodeInstanceIds = NodeInstanceIds == null ? Array.Empty<string>() : (string[])NodeInstanceIds.Clone(),
                HardwareIds = HardwareIds == null ? Array.Empty<string>() : (string[])HardwareIds.Clone(),
                LocationPaths = LocationPaths == null ? Array.Empty<string>() : (string[])LocationPaths.Clone(),
                ParentInstanceId = ParentInstanceId,
                DriverInfPath = DriverInfPath,
                DriverVersion = DriverVersion,
                Manufacturer = Manufacturer,
                Health = Health,
                ProblemCode = ProblemCode,
                IsPresent = IsPresent,
                IsStarted = IsStarted,
                FromBaseline = FromBaseline,
                IdentityAmbiguous = IdentityAmbiguous,
                RecoveryCandidate = RecoveryCandidate == null ? null : RecoveryCandidate.Clone(),
                UsbRestartBlocked = UsbRestartBlocked,
                UsbRestartBlockReason = UsbRestartBlockReason,
                RepairState = RepairState,
                ResultMessage = ResultMessage
            };
        }
    }

    internal sealed class CoreHardwareRecoveryCandidate
    {
        public CoreHardwareRecoveryKind Kind;
        public string InstanceId = string.Empty;
        public string DisplayName = string.Empty;
        public string LocationPath = string.Empty;
        public string MatchEvidence = string.Empty;
        public uint ProblemCode;

        public CoreHardwareRecoveryCandidate Clone()
        {
            return new CoreHardwareRecoveryCandidate
            {
                Kind = Kind,
                InstanceId = InstanceId,
                DisplayName = DisplayName,
                LocationPath = LocationPath,
                MatchEvidence = MatchEvidence,
                ProblemCode = ProblemCode
            };
        }
    }
}
