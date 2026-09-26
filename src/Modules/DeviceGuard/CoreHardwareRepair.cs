using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class DeviceCommandResult
    {
        public int ExitCode = -1;
        public string Output = string.Empty;
        public string Error = string.Empty;
        public bool TimedOut;

        public bool Accepted { get { return ExitCode == 0 || ExitCode == 3010; } }
    }

    internal static class PnpUtilCommandRunner
    {
        public static async Task<DeviceCommandResult> RunAsync(IEnumerable<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new DeviceCommandResult();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
                process.Start();

                // Cooperative cancellation deliberately does not abort an already-started Windows
                // device operation. Only this step's timeout may terminate the child process.
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                using (var timeoutSource = new CancellationTokenSource(timeout))
                {
                    try
                    {
                        await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                        result.ExitCode = process.ExitCode;
                    }
                    catch (OperationCanceledException)
                    {
                        result.TimedOut = true;
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
                    }
                }
                result.Output = await outputTask.ConfigureAwait(false);
                result.Error = await errorTask.ConfigureAwait(false);
            }
            AppLog.Write("Device Guard", "pnputil " + string.Join(" ", arguments)
                + " exit=" + result.ExitCode + " timedOut=" + result.TimedOut);
            if (!string.IsNullOrWhiteSpace(result.Output))
                AppLog.Write("Device Guard", "stdout:" + Environment.NewLine + result.Output.Trim());
            if (!string.IsNullOrWhiteSpace(result.Error))
                AppLog.Write("Device Guard", "stderr:" + Environment.NewLine + result.Error.Trim());
            return result;
        }
    }

    internal sealed class CoreHardwareRepairEngine
    {
        private readonly IWindowsPnPInventory inventory;
        private readonly CoreHardwareResolver resolver;
        private readonly CoreHardwareBaselineStore baselineStore;

        public CoreHardwareRepairEngine()
            : this(new WindowsPnPInventory(), new CoreHardwareResolver(),
                new CoreHardwareBaselineStore(AppPaths.DeviceGuardBaselinePath))
        {
        }

        internal CoreHardwareRepairEngine(IWindowsPnPInventory inventory,
            CoreHardwareResolver resolver, CoreHardwareBaselineStore baselineStore)
        {
            this.inventory = inventory;
            this.resolver = resolver;
            this.baselineStore = baselineStore;
        }

        public async Task<DeviceRepairMessage> RepairOneAsync(DeviceRepairTarget target,
            Action<DeviceRepairMessage> publish, CancellationToken cancellationToken,
            bool sharedRepairAllScanCompleted = false)
        {
            CoreHardwareBaselineDocument baseline = baselineStore.Load();
            CoreHardwareItem item = Resolve(target, baseline);
            if (item != null && item.IdentityAmbiguous)
                return Result(target, DeviceRepairState.Ambiguous,
                    "Multiple hardware candidates match the saved identity. No device operation was performed.");

            CoreHardwareHealth initialHealth = item == null ? CoreHardwareHealth.Unknown : item.Health;
            var scan = new DeviceCommandResult { ExitCode = 0 };
            if (!sharedRepairAllScanCompleted)
            {
                PublishStep(publish, target, "Scanning for hardware changes.");
                scan = await RunPnpAsync(new[] { "/scan-devices" },
                    TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                if (scan.TimedOut)
                    return Result(target, DeviceRepairState.Failed,
                        "Hardware scan timed out; the resulting hardware state could not be confirmed.");
                if (!scan.Accepted)
                    return Result(target, DeviceRepairState.Failed,
                        "Windows rejected the hardware scan (exit code " + scan.ExitCode + ").");
            }

            cancellationToken.ThrowIfCancellationRequested();
            item = Resolve(target, baseline);
            if (item != null && item.IdentityAmbiguous)
                return Result(target, DeviceRepairState.Ambiguous,
                    "Multiple hardware candidates match the saved identity. No device operation was performed.");
            if (IsHealthy(item))
            {
                baselineStore.MergeHealthy(baseline, new[] { item });
                if (target.Mode == DeviceRepairMode.RepairAll || initialHealth != CoreHardwareHealth.Healthy)
                    return Result(target, scan.ExitCode == 3010 ? DeviceRepairState.RebootRequired : DeviceRepairState.Succeeded,
                        initialHealth == CoreHardwareHealth.Healthy
                            ? "Healthy hardware was verified against the Repair All scan; it was not restarted."
                            : "The hardware returned after scanning and Windows reports it is working.");
                // A manual one-click repair of a healthy item may continue to restart per policy.
            }

            if (item != null && item.Health == CoreHardwareHealth.RebootRequired)
                return Result(target, DeviceRepairState.RebootRequired,
                    "Windows problem code 14 requires a computer restart.");

            if (item != null && item.Health == CoreHardwareHealth.Disabled)
            {
                PublishStep(publish, target, "Enabling the disabled device.");
                DeviceCommandResult enable = await RunPnpAsync(
                    new[] { "/enable-device", item.AnchorInstanceId }, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                if (enable.TimedOut)
                    return Result(target, DeviceRepairState.Failed, "Device enable timed out and was not verified.");
                if (!enable.Accepted)
                    return Result(target, DeviceRepairState.Failed,
                        "Windows rejected device enable (exit code " + enable.ExitCode + ").");
                cancellationToken.ThrowIfCancellationRequested();
                item = Resolve(target, baseline);
                if (IsHealthy(item))
                {
                    baselineStore.MergeHealthy(baseline, new[] { item });
                    return Result(target, enable.ExitCode == 3010 ? DeviceRepairState.RebootRequired : DeviceRepairState.Succeeded,
                        "The device was enabled and Windows reports it is working.");
                }
            }

            bool mayRestart = CoreHardwareRepairPolicy.MayRestart(target, initialHealth, item);

            if (mayRestart && item != null && item.IsPresent && !string.IsNullOrWhiteSpace(item.AnchorInstanceId))
            {
                PublishStep(publish, target, "Restarting the exact core device instance.");
                DeviceCommandResult restart = await RunPnpAsync(
                    new[] { "/restart-device", item.AnchorInstanceId }, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                if (restart.TimedOut)
                    return Result(target, DeviceRepairState.Failed, "Device restart timed out and was not verified.");
                if (restart.ExitCode == 3010)
                    return Result(target, DeviceRepairState.RebootRequired,
                        "Windows accepted the device restart, but a computer restart is required.");
                cancellationToken.ThrowIfCancellationRequested();
                item = Resolve(target, baseline);
                if (restart.Accepted && IsHealthy(item))
                {
                    baselineStore.MergeHealthy(baseline, new[] { item });
                    return Result(target, DeviceRepairState.Succeeded,
                        "The core device restarted and Windows reports it is working.");
                }
            }

            if (CoreHardwareRepairPolicy.MayRestartRecoveryTarget(target, item))
            {
                string recoveryInstanceId = item.RecoveryCandidate.InstanceId;
                PublishStep(publish, target,
                    "Restarting the exact failed USB node on the saved "
                    + target.CoreCapability + " port.");
                DeviceCommandResult recoveryRestart = await RunPnpAsync(
                    new[] { "/restart-device", recoveryInstanceId }, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                if (recoveryRestart.TimedOut)
                    return Result(target, DeviceRepairState.Failed,
                        "The failed USB node restart timed out and was not verified.");
                if (recoveryRestart.ExitCode == 3010)
                    return Result(target, DeviceRepairState.RebootRequired,
                        "Windows accepted the failed USB node restart, but a computer restart is required.");
                if (!recoveryRestart.Accepted)
                    AppLog.Write("Device Guard", "Failed USB recovery restart was rejected for "
                        + recoveryInstanceId + " exit=" + recoveryRestart.ExitCode);
                cancellationToken.ThrowIfCancellationRequested();
                PublishStep(publish, target, "Scanning after the targeted USB recovery restart.");
                DeviceCommandResult recoveryScan = await RunPnpAsync(new[] { "/scan-devices" },
                    TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                if (recoveryScan.TimedOut)
                    return Result(target, DeviceRepairState.Failed,
                        "The targeted USB restart completed, but its verification scan timed out.");
                cancellationToken.ThrowIfCancellationRequested();
                item = Resolve(target, baseline);
                if (item != null && item.IdentityAmbiguous)
                    return Result(target, DeviceRepairState.Ambiguous,
                        "Multiple hardware candidates appeared after USB recovery. No further operation was performed.");
                if (recoveryRestart.Accepted && IsHealthy(item))
                {
                    baselineStore.MergeHealthy(baseline, new[] { item });
                    return Result(target, DeviceRepairState.Succeeded,
                        "The failed USB node restarted and the " + target.CoreCapability
                        + " hardware returned healthy.");
                }
            }

            if ((target.CoreCapability == CoreHardwareCapability.Bluetooth
                    || target.CoreCapability == CoreHardwareCapability.Audio)
                && item != null && item.IsPresent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PublishStep(publish, target, "Restarting the related Windows service.");
                string service = target.CoreCapability == CoreHardwareCapability.Bluetooth ? "bthserv" : "Audiosrv";
                WindowsServiceRepairResult serviceResult = await WindowsServiceRepair.RestartAsync(service,
                    TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (!serviceResult.Succeeded)
                    AppLog.Write("Device Guard", "Service repair did not complete: " + serviceResult.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishStep(publish, target, "Scanning again and verifying device health.");
            DeviceCommandResult rescan = await RunPnpAsync(new[] { "/scan-devices" },
                TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (rescan.TimedOut)
                return Result(target, DeviceRepairState.Failed, "Final hardware scan timed out and was not verified.");
            cancellationToken.ThrowIfCancellationRequested();
            item = Resolve(target, baseline);
            if (IsHealthy(item))
            {
                baselineStore.MergeHealthy(baseline, new[] { item });
                return Result(target, DeviceRepairState.Succeeded,
                    "Repair steps completed and Windows reports the hardware is working.");
            }

            RepairRiskLevel removeRisk = CoreHardwareResolver.RemoveRisk(target.CoreCapability);
            bool removeRecovery = CoreHardwareRepairPolicy.MayRemoveRecoveryTarget(target, item);
            string removeInstanceId = removeRecovery
                ? item.RecoveryCandidate.InstanceId : item == null ? string.Empty : item.AnchorInstanceId;
            bool mayRemove = target.Mode == DeviceRepairMode.Manual
                && target.CoreCapability != CoreHardwareCapability.Usb
                && removeRisk <= target.MaximumAuthorizedRisk
                && item != null && item.FromBaseline && !item.IdentityAmbiguous
                && HasLocalDriver(item.DriverInfPath)
                && !string.IsNullOrWhiteSpace(removeInstanceId);
            if (mayRemove)
            {
                PublishStep(publish, target, removeRecovery
                    ? "Removing the exact failed USB node, then re-enumerating its port."
                    : "Removing the exact device instance, then re-enumerating it.");
                DeviceCommandResult remove = await RunPnpAsync(
                    new[] { "/remove-device", removeInstanceId }, TimeSpan.FromSeconds(60), cancellationToken)
                    .ConfigureAwait(false);
                if (remove.TimedOut)
                    return Result(target, DeviceRepairState.Failed, "Device removal timed out and was not verified.");
                if (!remove.Accepted)
                    return Result(target, DeviceRepairState.Failed,
                        "Windows rejected exact device removal (exit code " + remove.ExitCode + ").");
                cancellationToken.ThrowIfCancellationRequested();
                DeviceCommandResult finalScan = await RunPnpAsync(new[] { "/scan-devices" },
                    TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                if (finalScan.TimedOut)
                    return Result(target, DeviceRepairState.Failed,
                        "Device removal completed, but re-enumeration timed out and was not verified.");
                cancellationToken.ThrowIfCancellationRequested();
                item = Resolve(target, baseline);
                if (IsHealthy(item))
                {
                    baselineStore.MergeHealthy(baseline, new[] { item });
                    return Result(target, remove.ExitCode == 3010 || finalScan.ExitCode == 3010
                        ? DeviceRepairState.RebootRequired : DeviceRepairState.Succeeded,
                        removeRecovery
                            ? "The failed USB node was re-enumerated and the " + target.CoreCapability
                                + " hardware returned healthy."
                            : "The device was re-enumerated and Windows reports it is working.");
                }
            }

            if (item != null && item.Health == CoreHardwareHealth.DriverMissing || !HasLocalDriver(
                    item == null ? string.Empty : item.DriverInfPath))
                return Result(target, DeviceRepairState.NeedsDriver,
                    "No verified local driver is available. Use Windows Update or the computer manufacturer's driver package.");
            if (!mayRestart || (target.CoreCapability == CoreHardwareCapability.Usb
                    && item != null && item.UsbRestartBlocked))
                return Result(target, DeviceRepairState.Partial,
                    item != null && item.UsbRestartBlocked
                        ? "Scan and verification completed. USB restart is blocked because critical input or storage depends on this controller."
                        : "Scan and verification completed. Additional steps exceed the authorized risk level.");
            return Result(target, DeviceRepairState.Failed,
                "Repair steps completed, but Windows still does not report this hardware as healthy.");
        }

        private CoreHardwareItem Resolve(DeviceRepairTarget target, CoreHardwareBaselineDocument baseline)
        {
            return resolver.ResolveOne(inventory.Capture(), baseline, target.CoreHardwareId);
        }

        private static bool IsHealthy(CoreHardwareItem item)
        {
            return item != null && item.IsPresent && item.IsStarted && item.ProblemCode == 0
                && item.Health == CoreHardwareHealth.Healthy && !item.IdentityAmbiguous;
        }

        private static bool HasLocalDriver(string infName)
        {
            if (string.IsNullOrWhiteSpace(infName)) return false;
            string name = Path.GetFileName(infName);
            return !string.IsNullOrWhiteSpace(name)
                && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", name));
        }

        private static async Task<DeviceCommandResult> RunPnpAsync(string[] arguments, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return await PnpUtilCommandRunner.RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        }

        private static void PublishStep(Action<DeviceRepairMessage> publish, DeviceRepairTarget target, string message)
        {
            if (publish != null) publish(new DeviceRepairMessage
            {
                Type = "progress", RuntimeId = target.RuntimeId,
                State = DeviceRepairState.Repairing, Message = message
            });
        }

        private static DeviceRepairMessage Result(DeviceRepairTarget target, DeviceRepairState state, string message)
        {
            return new DeviceRepairMessage
            {
                Type = "result", RuntimeId = target.RuntimeId, State = state, Message = message
            };
        }
    }

    internal sealed class WindowsServiceRepairResult
    {
        public bool Succeeded;
        public string Message = string.Empty;
    }

    internal static class WindowsServiceRepair
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint ScStatusProcessInfo = 0;
        private const uint ServiceControlStop = 1;
        private const uint ServiceStopped = 1;
        private const uint ServiceRunning = 4;

        public static Task<WindowsServiceRepairResult> RestartAsync(string serviceName, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Once SCM work starts it is allowed to finish or time out; cancellation affects only later steps.
            return Task.Run(delegate { return RestartBlocking(serviceName, timeout); });
        }

        private static WindowsServiceRepairResult RestartBlocking(string serviceName, TimeSpan timeout)
        {
            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return Failed("OpenSCManager failed", Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenService(manager, serviceName,
                    ServiceQueryStatus | ServiceStart | ServiceStop);
                if (service == IntPtr.Zero) return Failed("OpenService failed", Marshal.GetLastWin32Error());
                try
                {
                    SERVICE_STATUS_PROCESS status;
                    if (!TryQuery(service, out status)) return Failed("QueryServiceStatusEx failed", Marshal.GetLastWin32Error());
                    DateTime deadline = DateTime.UtcNow + timeout;
                    if (status.dwCurrentState == ServiceRunning)
                    {
                        SERVICE_STATUS ignored;
                        if (!ControlService(service, ServiceControlStop, out ignored))
                            return Failed("ControlService(STOP) failed", Marshal.GetLastWin32Error());
                        if (!WaitForState(service, ServiceStopped, deadline))
                            return new WindowsServiceRepairResult { Message = "Service stop timed out." };
                    }
                    if (!StartService(service, 0, null))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 1056) return Failed("StartService failed", error);
                    }
                    if (!WaitForState(service, ServiceRunning, deadline))
                        return new WindowsServiceRepairResult { Message = "Service start timed out." };
                    return new WindowsServiceRepairResult { Succeeded = true, Message = "Service restarted." };
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static bool WaitForState(IntPtr service, uint wanted, DateTime deadline)
        {
            while (DateTime.UtcNow < deadline)
            {
                SERVICE_STATUS_PROCESS status;
                if (!TryQuery(service, out status)) return false;
                if (status.dwCurrentState == wanted) return true;
                Thread.Sleep(200);
            }
            return false;
        }

        private static bool TryQuery(IntPtr service, out SERVICE_STATUS_PROCESS status)
        {
            uint needed;
            return QueryServiceStatusEx(service, ScStatusProcessInfo, out status,
                (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out needed);
        }

        private static WindowsServiceRepairResult Failed(string operation, int error)
        {
            return new WindowsServiceRepairResult
            {
                Message = operation + ": " + new Win32Exception(error).Message + " (" + error + ")."
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode;
            public uint dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode;
            public uint dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel,
            out SERVICE_STATUS_PROCESS buffer, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(IntPtr service, uint argc, string[] argv);
    }
}
