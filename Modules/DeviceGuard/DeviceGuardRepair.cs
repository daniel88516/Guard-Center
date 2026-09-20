using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class DeviceGuardRepairEngine
    {
        private const uint DnStarted = 0x00000008;
        private readonly IDeviceGuardInventory inventory;
        private readonly CoreHardwareRepairEngine coreEngine;

        public DeviceGuardRepairEngine(IDeviceGuardInventory inventory)
            : this(inventory, new CoreHardwareRepairEngine())
        {
        }

        internal DeviceGuardRepairEngine(IDeviceGuardInventory inventory,
            CoreHardwareRepairEngine coreEngine)
        {
            this.inventory = inventory;
            this.coreEngine = coreEngine;
        }

        public async Task RunAsync(IList<DeviceRepairTarget> targets,
            Action<DeviceRepairMessage> publish, CancellationToken cancellationToken)
        {
            if (targets == null)
            {
                return;
            }

            bool coreRepairAllScanCompleted = false;
            for (int i = 0; i < targets.Count; i++)
            {
                DeviceRepairTarget target = targets[i];
                if (cancellationToken.IsCancellationRequested)
                {
                    Publish(publish, target.RuntimeId, DeviceRepairState.Canceled,
                        "Repair canceled before this device was started.", 0, "result");
                    continue;
                }

                Publish(publish, target.RuntimeId, DeviceRepairState.Repairing,
                    target.TargetType == DeviceGuardTargetType.CoreHardware
                        ? "Preparing a risk-controlled core hardware repair."
                        : "Restarting device.", 0, "started");
                DeviceRepairMessage result;
                try
                {
                    result = target.TargetType == DeviceGuardTargetType.CoreHardware
                        ? await coreEngine.RepairOneAsync(target, publish, cancellationToken,
                            target.Mode == DeviceRepairMode.RepairAll && coreRepairAllScanCompleted)
                            .ConfigureAwait(false)
                        : await RepairOneAsync(target, cancellationToken).ConfigureAwait(false);
                    if (target.TargetType == DeviceGuardTargetType.CoreHardware
                        && target.Mode == DeviceRepairMode.RepairAll
                        && result.State != DeviceRepairState.Failed
                        && result.State != DeviceRepairState.RiskDeclined
                        && result.State != DeviceRepairState.Ambiguous
                        && result.State != DeviceRepairState.Canceled)
                        coreRepairAllScanCompleted = true;
                }
                catch (OperationCanceledException)
                {
                    result = Result(target, DeviceRepairState.Canceled,
                        "Repair canceled before the next Windows operation was started.", 0);
                }
                if (publish != null)
                {
                    publish(result);
                }
            }
        }

        private async Task<DeviceRepairMessage> RepairOneAsync(DeviceRepairTarget target,
            CancellationToken cancellationToken)
        {
            DeviceGuardDevice before;
            try
            {
                before = inventory.Resolve(target);
            }
            catch (Exception ex)
            {
                return Result(target, DeviceRepairState.Failed,
                    "Device inventory failed before repair: " + ex.Message, -1);
            }

            if (before == null || !before.IsPresent)
            {
                return Result(target, DeviceRepairState.Skipped,
                    "The device is no longer present.", 0);
            }
            if (!before.CanRepair)
            {
                return Result(target, DeviceRepairState.Skipped,
                    string.IsNullOrWhiteSpace(before.UnsupportedReason)
                        ? "This device is not eligible for safe repair."
                        : before.UnsupportedReason,
                    0);
            }

            AppLog.Write("Device Guard", "Restart begin kind=" + target.Kind
                + " instance=" + before.InstanceId);
            DeviceCommandResult command = await PnpUtilCommandRunner.RunAsync(
                new[] { "/restart-device", before.InstanceId }, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            AppLog.Write("Device Guard", "pnputil /restart-device exit=" + command.ExitCode
                + " timedOut=" + command.TimedOut + " instance=" + before.InstanceId);
            if (!string.IsNullOrWhiteSpace(command.Output))
            {
                AppLog.Write("Device Guard", "stdout:" + Environment.NewLine + command.Output.Trim());
            }
            if (!string.IsNullOrWhiteSpace(command.Error))
            {
                AppLog.Write("Device Guard", "stderr:" + Environment.NewLine + command.Error.Trim());
            }

            if (command.TimedOut)
            {
                return Result(target, DeviceRepairState.Failed,
                    "Device restart timed out. The device state was not confirmed.", command.ExitCode);
            }
            if (command.ExitCode == 3010)
            {
                return Result(target, DeviceRepairState.RebootRequired,
                    "Windows accepted the repair, but a computer restart is required.", command.ExitCode);
            }
            if (command.ExitCode != 0)
            {
                return Result(target, DeviceRepairState.Failed,
                    "Windows rejected the device restart (exit code " + command.ExitCode + ").", command.ExitCode);
            }

            string verificationError = string.Empty;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeviceGuardDevice current = null;
                try
                {
                    current = inventory.Resolve(target);
                }
                catch (Exception ex)
                {
                    verificationError = ex.Message;
                }

                if (current != null && current.IsPresent)
                {
                    uint status;
                    uint problem;
                    string error;
                    if (inventory.TryReadDevNodeStatus(current.InstanceId, out status, out problem, out error))
                    {
                        if ((status & DnStarted) != 0 && problem == 0)
                        {
                            AppLog.Write("Device Guard", "Restart verified instance=" + current.InstanceId);
                            return Result(target, DeviceRepairState.Succeeded,
                                "Device restarted and Windows reports it is working.", command.ExitCode);
                        }
                        verificationError = "Windows problem code " + problem + ".";
                    }
                    else
                    {
                        verificationError = error;
                    }
                }
                else
                {
                    verificationError = "The device has not returned after restart.";
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            return Result(target, DeviceRepairState.Failed,
                "The restart command completed, but the device could not be verified. " + verificationError,
                command.ExitCode);
        }

        private static DeviceRepairMessage Result(DeviceRepairTarget target, DeviceRepairState state,
            string message, int exitCode)
        {
            return new DeviceRepairMessage
            {
                Type = "result",
                RuntimeId = target.RuntimeId,
                State = state,
                Message = message,
                ExitCode = exitCode
            };
        }

        private static void Publish(Action<DeviceRepairMessage> publish, string runtimeId,
            DeviceRepairState state, string message, int exitCode, string type)
        {
            if (publish != null)
            {
                publish(new DeviceRepairMessage
                {
                    Type = type,
                    RuntimeId = runtimeId,
                    State = state,
                    Message = message,
                    ExitCode = exitCode
                });
            }
        }

    }

    internal static class DeviceGuardElevation
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        public static async Task RunAsync(IList<DeviceRepairTarget> targets,
            Action<DeviceRepairMessage> publish, CancellationToken cancellationToken)
        {
            if (IsAdministrator())
            {
                await new DeviceGuardRepairEngine(new WindowsDeviceInventory())
                    .RunAsync(targets, publish, cancellationToken).ConfigureAwait(false);
                return;
            }

            string pipeName = "GuardCenter.DeviceGuard." + Guid.NewGuid().ToString("N");
            string token = Guid.NewGuid().ToString("N");
            string operationId = Guid.NewGuid().ToString("N");
            var request = new DeviceRepairRequest
            {
                Token = token,
                OperationId = operationId,
                Targets = new List<DeviceRepairTarget>(targets)
            };
            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = AppPaths.InstalledExePath;
            }

            using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = "--device-guard-helper --pipe " + Quote(pipeName) + " --token " + Quote(token),
                        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppPaths.Root,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        publish(new DeviceRepairMessage
                        {
                            Type = "result",
                            RuntimeId = targets[i].RuntimeId,
                            State = DeviceRepairState.Canceled,
                            Message = "Administrator approval was canceled."
                        });
                    }
                    return;
                }

                using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectionTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        PublishIncomplete(targets, publish, DeviceRepairState.Canceled,
                            "Repair canceled before the administrator helper connected.");
                        return;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        for (int i = 0; i < targets.Count; i++)
                        {
                            publish(new DeviceRepairMessage
                            {
                                Type = "result",
                                RuntimeId = targets[i].RuntimeId,
                                State = DeviceRepairState.Failed,
                                Message = "The administrator helper did not connect within 20 seconds."
                            });
                        }
                        return;
                    }
                }

                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
                    using (var cancelSenderCompletion = new CancellationTokenSource())
                    using (var helperTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(
                        Math.Min(60, Math.Max(10, targets.Count * 4)))))
                    {
                        Task cancelSender = SendCancellationAsync(writer, token, operationId,
                            cancellationToken, cancelSenderCompletion.Token);
                        while (true)
                        {
                            string line;
                            try
                            {
                                line = await reader.ReadLineAsync(helperTimeout.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            if (line == null) break;
                            DeviceRepairMessage message = JsonSerializer.Deserialize<DeviceRepairMessage>(line, JsonOptions);
                            if (message == null) continue;
                            if (string.Equals(message.Type, "complete", StringComparison.OrdinalIgnoreCase)) break;
                            publish(message);
                            if (string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase))
                                completed.Add(message.RuntimeId);
                        }
                        cancelSenderCompletion.Cancel();
                        await cancelSender.ConfigureAwait(false);
                    }

                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (!completed.Contains(targets[i].RuntimeId))
                        {
                            publish(new DeviceRepairMessage
                            {
                                Type = "result",
                                RuntimeId = targets[i].RuntimeId,
                                State = cancellationToken.IsCancellationRequested
                                    ? DeviceRepairState.Canceled : DeviceRepairState.Failed,
                                Message = cancellationToken.IsCancellationRequested
                                    ? "Repair canceled before this target returned a result."
                                    : "The elevated repair helper ended before returning a verified result."
                            });
                        }
                    }
                }
            }
        }

        public static bool TryHandleCommandLine(string[] args)
        {
            if (!HasArg(args, "--device-guard-helper"))
            {
                return false;
            }

            string pipeName = GetArgValue(args, "--pipe");
            string token = GetArgValue(args, "--token");
            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token) || !IsAdministrator())
            {
                AppLog.Write("Device Guard", "Rejected invalid elevated helper invocation.");
                return true;
            }

            RunHelperAsync(pipeName, token).GetAwaiter().GetResult();
            return true;
        }

        private static async Task RunHelperAsync(string pipeName, string token)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                }

                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    string requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    DeviceRepairRequest request = string.IsNullOrWhiteSpace(requestLine)
                        ? null
                        : JsonSerializer.Deserialize<DeviceRepairRequest>(requestLine, JsonOptions);
                    if (request == null || !string.Equals(request.Token, token, StringComparison.Ordinal)
                        || request.Targets == null)
                    {
                        AppLog.Write("Device Guard", "Elevated helper rejected an invalid request.");
                        return;
                    }

                    using (var repairCancellation = new CancellationTokenSource())
                    {
                        Task controlReader = ReadControlsAsync(reader, request, repairCancellation);
                        var engine = new DeviceGuardRepairEngine(new WindowsDeviceInventory());
                        await engine.RunAsync(request.Targets, delegate(DeviceRepairMessage message)
                        {
                            writer.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
                        }, repairCancellation.Token).ConfigureAwait(false);
                    }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new DeviceRepairMessage
                    {
                        Type = "complete"
                    }, JsonOptions)).ConfigureAwait(false);
                }
            }
        }

        private static async Task SendCancellationAsync(StreamWriter writer, string token,
            string operationId, CancellationToken cancellationToken, CancellationToken completedToken)
        {
            if (!cancellationToken.CanBeCanceled) return;
            try
            {
                Task canceled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                Task completed = Task.Delay(Timeout.InfiniteTimeSpan, completedToken);
                Task winner = await Task.WhenAny(canceled, completed).ConfigureAwait(false);
                if (winner != canceled || !cancellationToken.IsCancellationRequested) return;
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new DeviceRepairControl
                    {
                        Type = "cancel", Token = token, OperationId = operationId
                    }, JsonOptions)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Device Guard", "Could not send cancellation to helper: " + ex.Message);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task ReadControlsAsync(StreamReader reader, DeviceRepairRequest request,
            CancellationTokenSource repairCancellation)
        {
            try
            {
                while (true)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) return;
                    DeviceRepairControl control = JsonSerializer.Deserialize<DeviceRepairControl>(line, JsonOptions);
                    if (control != null && string.Equals(control.Type, "cancel", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(control.Token, request.Token, StringComparison.Ordinal)
                        && string.Equals(control.OperationId, request.OperationId, StringComparison.Ordinal))
                    {
                        repairCancellation.Cancel();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Device Guard", "Elevated helper control channel ended: " + ex.Message);
            }
        }

        private static void PublishIncomplete(IList<DeviceRepairTarget> targets,
            Action<DeviceRepairMessage> publish, DeviceRepairState state, string message)
        {
            for (int i = 0; i < targets.Count; i++) publish(new DeviceRepairMessage
            {
                Type = "result", RuntimeId = targets[i].RuntimeId, State = state, Message = message
            });
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static bool HasArg(string[] args, string value)
        {
            return Array.FindIndex(args, delegate(string arg)
            {
                return string.Equals(arg, value, StringComparison.OrdinalIgnoreCase);
            }) >= 0;
        }

        private static string GetArgValue(string[] args, string key)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return string.Empty;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
