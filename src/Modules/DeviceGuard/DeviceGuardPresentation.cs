namespace GuardCenter
{
    internal enum DeviceHealthPresentationState
    {
        Healthy,
        Disabled,
        Stopped,
        Missing,
        DriverError,
        DeviceError,
        RebootRequired,
        Repairing,
        Unknown
    }

    internal static class DeviceGuardPresentation
    {
        private const uint CmProblemNeedRestart = 14;
        private const uint CmProblemDisabled = 22;
        private const uint CmProblemFailedInstall = 28;

        public static DeviceHealthPresentationState Classify(DeviceGuardDevice device)
        {
            if (device == null)
            {
                return DeviceHealthPresentationState.Unknown;
            }
            if (device.RepairState == DeviceRepairState.Repairing)
            {
                return DeviceHealthPresentationState.Repairing;
            }
            if (!device.IsPresent)
            {
                return DeviceHealthPresentationState.Missing;
            }
            if (device.ProblemCode == CmProblemDisabled)
            {
                return DeviceHealthPresentationState.Disabled;
            }
            if (device.ProblemCode == CmProblemFailedInstall)
            {
                return DeviceHealthPresentationState.DriverError;
            }
            if (device.ProblemCode == CmProblemNeedRestart)
            {
                return DeviceHealthPresentationState.RebootRequired;
            }
            if (device.ProblemCode != 0)
            {
                return DeviceHealthPresentationState.DeviceError;
            }
            if (!device.IsStarted)
            {
                return DeviceHealthPresentationState.Stopped;
            }
            return DeviceHealthPresentationState.Healthy;
        }

        public static DeviceHealthPresentationState Classify(CoreHardwareItem item)
        {
            if (item == null)
            {
                return DeviceHealthPresentationState.Unknown;
            }
            if (item.RepairState == DeviceRepairState.Repairing)
            {
                return DeviceHealthPresentationState.Repairing;
            }
            if (item.Health == CoreHardwareHealth.Missing || !item.IsPresent)
            {
                return DeviceHealthPresentationState.Missing;
            }
            if (item.Health == CoreHardwareHealth.Disabled || item.ProblemCode == CmProblemDisabled)
            {
                return DeviceHealthPresentationState.Disabled;
            }
            if (item.Health == CoreHardwareHealth.DriverMissing || item.ProblemCode == CmProblemFailedInstall)
            {
                return DeviceHealthPresentationState.DriverError;
            }
            if (item.Health == CoreHardwareHealth.RebootRequired || item.ProblemCode == CmProblemNeedRestart)
            {
                return DeviceHealthPresentationState.RebootRequired;
            }
            if (item.Health == CoreHardwareHealth.Degraded || item.ProblemCode != 0)
            {
                return DeviceHealthPresentationState.DeviceError;
            }
            if (!item.IsStarted)
            {
                return DeviceHealthPresentationState.Stopped;
            }
            if (item.Health == CoreHardwareHealth.Healthy)
            {
                return DeviceHealthPresentationState.Healthy;
            }
            return DeviceHealthPresentationState.Unknown;
        }

        public static bool HasOperation(DeviceRepairState state, string resultMessage)
        {
            return state == DeviceRepairState.Repairing
                || !string.IsNullOrWhiteSpace(resultMessage)
                || (state != DeviceRepairState.Ready && state != DeviceRepairState.Unsupported);
        }

        public static string StatusLabel(DeviceHealthPresentationState state)
        {
            if (state == DeviceHealthPresentationState.Healthy) return "正常運作";
            if (state == DeviceHealthPresentationState.Disabled) return "已停用";
            if (state == DeviceHealthPresentationState.Stopped) return "已停止";
            if (state == DeviceHealthPresentationState.Missing) return "未偵測";
            if (state == DeviceHealthPresentationState.DriverError) return "驅動異常";
            if (state == DeviceHealthPresentationState.DeviceError) return "裝置異常";
            if (state == DeviceHealthPresentationState.RebootRequired) return "需要重新啟動";
            if (state == DeviceHealthPresentationState.Repairing) return "修復中";
            return "狀態不明";
        }
    }
}
