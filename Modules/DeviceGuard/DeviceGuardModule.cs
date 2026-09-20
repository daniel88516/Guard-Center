using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class DeviceGuardModule : IDisposable
    {
        private readonly IDeviceGuardInventory inventory;
        private readonly IWindowsPnPInventory pnpInventory;
        private readonly WindowsDeviceInventory connectedInventory;
        private readonly CoreHardwareResolver coreResolver;
        private readonly CoreHardwareBaselineStore baselineStore;
        private readonly CoreHardwareBaselineDocument baseline;
        private readonly IInputStackInventory inputStackInventory;
        private readonly object stateLock = new object();
        private readonly List<DeviceGuardDevice> devices = new List<DeviceGuardDevice>();
        private readonly List<CoreHardwareItem> coreHardware = new List<CoreHardwareItem>();
        private InputStackSnapshot inputStack = new InputStackSnapshot();
        private readonly SemaphoreSlim operationGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource activeOperation;
        private int scanVersion;
        private bool disposed;
        private bool scanning;
        private bool repairing;
        private string lastStatus = "Device Guard ready.";

        public DeviceGuardModule()
        {
            pnpInventory = new WindowsPnPInventory();
            connectedInventory = new WindowsDeviceInventory(pnpInventory);
            inventory = connectedInventory;
            coreResolver = new CoreHardwareResolver();
            baselineStore = new CoreHardwareBaselineStore(AppPaths.DeviceGuardBaselinePath);
            baseline = baselineStore.Load();
            inputStackInventory = new WindowsInputStackInventory();
        }

        public DeviceGuardModule(IDeviceGuardInventory inventory)
        {
            this.inventory = inventory;
            baseline = new CoreHardwareBaselineDocument();
        }

        internal DeviceGuardModule(IWindowsPnPInventory pnpInventory, string baselinePath)
        {
            this.pnpInventory = pnpInventory;
            connectedInventory = new WindowsDeviceInventory(pnpInventory);
            inventory = connectedInventory;
            coreResolver = new CoreHardwareResolver();
            baselineStore = new CoreHardwareBaselineStore(baselinePath);
            baseline = baselineStore.Load();
            inputStackInventory = new WindowsInputStackInventory();
        }

        public event EventHandler StatusChanged;
        public event EventHandler DevicesChanged;
        public event EventHandler CoreHardwareChanged;
        public event EventHandler InputStackChanged;

        public string StatusText
        {
            get { lock (stateLock) { return lastStatus; } }
        }

        public bool IsScanning
        {
            get { lock (stateLock) { return scanning; } }
        }

        public bool IsRepairing
        {
            get { lock (stateLock) { return repairing; } }
        }

        public void Start()
        {
            RefreshAsync();
        }

        public List<DeviceGuardDevice> GetDevices()
        {
            lock (stateLock)
            {
                var result = new List<DeviceGuardDevice>();
                for (int i = 0; i < devices.Count; i++)
                {
                    result.Add(devices[i].Clone());
                }
                return result;
            }
        }

        public List<CoreHardwareItem> GetCoreHardware()
        {
            lock (stateLock)
            {
                var result = new List<CoreHardwareItem>();
                for (int i = 0; i < coreHardware.Count; i++) result.Add(coreHardware[i].Clone());
                return result;
            }
        }

        public InputStackSnapshot GetInputStack()
        {
            lock (stateLock)
            {
                return inputStack.Clone();
            }
        }

        public void RefreshAsync()
        {
            RefreshAsync(null);
        }

        private void RefreshAsync(string statusAfterRefresh)
        {
            if (disposed)
            {
                return;
            }
            lock (stateLock)
            {
                if (repairing)
                {
                    lastStatus = "Refresh is unavailable while a repair operation is running.";
                    RaiseStatusChanged();
                    return;
                }
            }
            int version = Interlocked.Increment(ref scanVersion);
            lock (stateLock)
            {
                scanning = true;
                if (string.IsNullOrWhiteSpace(statusAfterRefresh))
                {
                    lastStatus = "Scanning supported devices.";
                }
            }
            RaiseStatusChanged();
            Task.Run(delegate
            {
                try
                {
                    List<DeviceGuardDevice> loaded;
                    List<CoreHardwareItem> loadedCore;
                    InputStackSnapshot loadedInput;
                    if (pnpInventory != null && connectedInventory != null && coreResolver != null)
                    {
                        WindowsPnPSnapshot snapshot = pnpInventory.Capture();
                        loaded = connectedInventory.Scan(snapshot);
                        loadedCore = coreResolver.Resolve(snapshot, baseline);
                        loadedInput = inputStackInventory == null
                            ? new InputStackSnapshot()
                            : inputStackInventory.Capture(snapshot);
                        if (baselineStore != null)
                        {
                            baselineStore.MergeHealthy(baseline, loadedCore);
                        }
                    }
                    else
                    {
                        loaded = inventory.Scan();
                        loadedCore = new List<CoreHardwareItem>();
                        loadedInput = new InputStackSnapshot
                        {
                            CapturedAt = DateTime.Now,
                            Summary = "此 inventory 不提供 Windows 輸入裝置堆疊資訊。"
                        };
                    }
                    lock (stateLock)
                    {
                        if (disposed || version != scanVersion)
                        {
                            return;
                        }
                        PreserveResultsNoLock(loaded);
                        devices.Clear();
                        devices.AddRange(loaded);
                        PreserveCoreResultsNoLock(loadedCore);
                        coreHardware.Clear();
                        coreHardware.AddRange(loadedCore);
                        inputStack = loadedInput;
                        scanning = false;
                        lastStatus = string.IsNullOrWhiteSpace(statusAfterRefresh)
                            ? "Device Guard found " + loaded.Count + " detectable device"
                                + (loaded.Count == 1 ? "" : "s") + " and " + loadedCore.Count
                                + " core hardware item" + (loadedCore.Count == 1 ? "." : "s.")
                            : statusAfterRefresh;
                    }
                    RaiseDevicesChanged();
                    RaiseCoreHardwareChanged();
                    RaiseInputStackChanged();
                    RaiseStatusChanged();
                }
                catch (Exception ex)
                {
                    AppLog.Write("Device Guard", "Scan failed: " + ex);
                    lock (stateLock)
                    {
                        if (version == scanVersion)
                        {
                            scanning = false;
                            lastStatus = "Device scan failed: " + ex.Message;
                        }
                    }
                    RaiseDevicesChanged();
                    RaiseCoreHardwareChanged();
                    RaiseInputStackChanged();
                    RaiseStatusChanged();
                }
            });
        }

        public Task RepairAsync(string runtimeId)
        {
            DeviceGuardDevice device = FindClone(runtimeId);
            if (device == null)
            {
                SetStatus("The selected device is no longer present.");
                return Task.CompletedTask;
            }
            return RunRepairRequestsAsync(new List<DeviceRepairTarget>
            {
                new DeviceRepairTarget
                {
                    RuntimeId = device.RuntimeId,
                    TargetType = DeviceGuardTargetType.ConnectedDevice,
                    Kind = device.Kind,
                    ExpectedInstanceId = device.InstanceId,
                    ExpectedHardwareIds = device.HardwareIds,
                    Mode = DeviceRepairMode.Manual,
                    MaximumAuthorizedRisk = RepairRiskLevel.Medium
                }
            }, "Device repair starting. The selected device may briefly disconnect.");
        }

        public Task RepairAllAsync()
        {
            List<DeviceGuardDevice> targets = GetDevices().FindAll(delegate(DeviceGuardDevice device)
            {
                return device.CanRepair && device.IncludeInRepairAll;
            });
            if (targets.Count == 0)
            {
                SetStatus("There are no safe repair targets to run.");
                return Task.CompletedTask;
            }
            var requests = new List<DeviceRepairTarget>();
            for (int i = 0; i < targets.Count; i++)
            {
                requests.Add(new DeviceRepairTarget
                {
                    RuntimeId = targets[i].RuntimeId,
                    TargetType = DeviceGuardTargetType.ConnectedDevice,
                    Kind = targets[i].Kind,
                    ExpectedInstanceId = targets[i].InstanceId,
                    ExpectedHardwareIds = targets[i].HardwareIds,
                    Mode = DeviceRepairMode.RepairAll,
                    MaximumAuthorizedRisk = RepairRiskLevel.Medium
                });
            }
            return RunRepairRequestsAsync(requests,
                "Device repair starting. Bluetooth, camera, or USB audio may briefly disconnect.");
        }

        public Task RepairCoreAsync(string id, RepairRiskLevel maximumAuthorizedRisk)
        {
            CoreHardwareItem item = FindCoreClone(id);
            if (item == null)
            {
                SetStatus("The selected core hardware item is no longer available.");
                return Task.CompletedTask;
            }
            if (item.IdentityAmbiguous)
            {
                SetStatus("Repair cannot start because the hardware identity is ambiguous.");
                return Task.CompletedTask;
            }
            if (item.Capability == CoreHardwareCapability.Usb && item.UsbRestartBlocked)
            {
                maximumAuthorizedRisk = RepairRiskLevel.Low;
            }
            return RunRepairRequestsAsync(new List<DeviceRepairTarget>
            {
                CreateCoreRequest(item, DeviceRepairMode.Manual, maximumAuthorizedRisk)
            }, "Core hardware repair starting. Only authorized repair steps will run.");
        }

        public Task RepairAllCoreAsync()
        {
            List<CoreHardwareItem> items = GetCoreHardware();
            if (items.Count == 0)
            {
                SetStatus("There are no core hardware capabilities to verify.");
                return Task.CompletedTask;
            }
            var requests = new List<DeviceRepairTarget>();
            for (int i = 0; i < items.Count; i++)
            {
                // Repair All may restart only medium-risk unhealthy capabilities. Graphics,
                // network and USB are restricted to scan and verification.
                RepairRiskLevel authorized = CoreHardwareRepairPolicy.MaximumRiskForRepairAll(items[i].Capability);
                requests.Add(CreateCoreRequest(items[i], DeviceRepairMode.RepairAll, authorized));
            }
            return RunRepairRequestsAsync(requests,
                "Core Repair All starting. Healthy items and high-risk hardware will only be scanned and verified.");
        }

        public void CancelRepair()
        {
            lock (stateLock)
            {
                if (activeOperation != null && !activeOperation.IsCancellationRequested)
                {
                    activeOperation.Cancel();
                    lastStatus = "Cancellation requested. The active Windows operation will finish or time out first.";
                }
            }
            RaiseStatusChanged();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            CancelRepair();
            lifetime.Cancel();
            lifetime.Dispose();
        }

        private async Task RunRepairRequestsAsync(List<DeviceRepairTarget> requests, string startingStatus)
        {
            if (!await operationGate.WaitAsync(0).ConfigureAwait(false))
            {
                SetStatus("A Device Guard operation is already running.");
                return;
            }
            try
            {
                lock (stateLock)
                {
                    repairing = true;
                    activeOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    for (int i = 0; i < requests.Count; i++)
                    {
                        UpdateStateNoLock(requests[i].RuntimeId, DeviceRepairState.Repairing,
                            "Waiting for administrator approval.");
                    }
                    lastStatus = startingStatus;
                }
                RaiseDevicesChanged();
                RaiseCoreHardwareChanged();
                RaiseStatusChanged();

                var summary = new DeviceRepairSummary();
                await DeviceGuardElevation.RunAsync(requests, delegate(DeviceRepairMessage message)
                {
                    lock (stateLock)
                    {
                        UpdateStateNoLock(message.RuntimeId, message.State, message.Message);
                        if (string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase))
                        {
                            AddToSummary(summary, message.State);
                        }
                        lastStatus = message.Message;
                    }
                    RaiseDevicesChanged();
                    RaiseCoreHardwareChanged();
                    RaiseStatusChanged();
                }, activeOperation.Token).ConfigureAwait(false);

                string completionStatus;
                lock (stateLock)
                {
                    repairing = false;
                    completionStatus = "Device repair finished: " + summary.ToStatusText();
                    lastStatus = completionStatus;
                }
                RaiseDevicesChanged();
                RaiseCoreHardwareChanged();
                RaiseStatusChanged();
                RefreshAsync(completionStatus);
            }
            catch (OperationCanceledException)
            {
                lock (stateLock)
                {
                    repairing = false;
                    lastStatus = "Device repair canceled.";
                }
                RaiseStatusChanged();
                RaiseDevicesChanged();
                RaiseCoreHardwareChanged();
            }
            catch (Exception ex)
            {
                AppLog.Write("Device Guard", "Repair coordinator failed: " + ex);
                lock (stateLock)
                {
                    for (int i = 0; i < requests.Count; i++)
                    {
                        MarkUnfinishedFailedNoLock(requests[i].RuntimeId);
                    }
                    repairing = false;
                    lastStatus = "Device repair failed: " + ex.Message;
                }
                RaiseDevicesChanged();
                RaiseCoreHardwareChanged();
                RaiseStatusChanged();
            }
            finally
            {
                lock (stateLock)
                {
                    if (activeOperation != null)
                    {
                        activeOperation.Dispose();
                        activeOperation = null;
                    }
                }
                operationGate.Release();
            }
        }

        private static DeviceRepairTarget CreateCoreRequest(CoreHardwareItem item,
            DeviceRepairMode mode, RepairRiskLevel authorizedRisk)
        {
            return new DeviceRepairTarget
            {
                RuntimeId = item.Id,
                TargetType = DeviceGuardTargetType.CoreHardware,
                CoreHardwareId = item.Id,
                CoreCapability = item.Capability,
                ExpectedInstanceId = item.AnchorInstanceId,
                ExpectedHardwareIds = item.HardwareIds,
                Mode = mode,
                MaximumAuthorizedRisk = authorizedRisk
            };
        }

        private void PreserveResultsNoLock(List<DeviceGuardDevice> loaded)
        {
            for (int i = 0; i < loaded.Count; i++)
            {
                DeviceGuardDevice old = FindNoLock(loaded[i].RuntimeId);
                if (old != null && old.RepairState != DeviceRepairState.Ready
                    && old.RepairState != DeviceRepairState.Repairing)
                {
                    loaded[i].RepairState = old.RepairState;
                    loaded[i].ResultMessage = old.ResultMessage;
                }
            }
        }

        private void PreserveCoreResultsNoLock(List<CoreHardwareItem> loaded)
        {
            for (int i = 0; i < loaded.Count; i++)
            {
                CoreHardwareItem old = FindCoreNoLock(loaded[i].Id);
                if (old != null && old.RepairState != DeviceRepairState.Ready
                    && old.RepairState != DeviceRepairState.Repairing)
                {
                    loaded[i].RepairState = old.RepairState;
                    loaded[i].ResultMessage = old.ResultMessage;
                }
            }
        }

        private DeviceGuardDevice FindClone(string runtimeId)
        {
            lock (stateLock)
            {
                DeviceGuardDevice device = FindNoLock(runtimeId);
                return device == null ? null : device.Clone();
            }
        }

        private DeviceGuardDevice FindNoLock(string runtimeId)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
                {
                    return devices[i];
                }
            }
            return null;
        }

        private CoreHardwareItem FindCoreClone(string id)
        {
            lock (stateLock)
            {
                CoreHardwareItem item = FindCoreNoLock(id);
                return item == null ? null : item.Clone();
            }
        }

        private CoreHardwareItem FindCoreNoLock(string id)
        {
            for (int i = 0; i < coreHardware.Count; i++)
            {
                if (string.Equals(coreHardware[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return coreHardware[i];
                }
            }
            return null;
        }

        private void UpdateStateNoLock(string runtimeId, DeviceRepairState state, string message)
        {
            DeviceGuardDevice device = FindNoLock(runtimeId);
            if (device != null)
            {
                device.RepairState = state;
                device.ResultMessage = message ?? string.Empty;
            }
            CoreHardwareItem core = FindCoreNoLock(runtimeId);
            if (core != null)
            {
                core.RepairState = state;
                core.ResultMessage = message ?? string.Empty;
            }
        }

        private void MarkUnfinishedFailedNoLock(string runtimeId)
        {
            DeviceGuardDevice device = FindNoLock(runtimeId);
            if (device != null && device.RepairState == DeviceRepairState.Repairing)
            {
                device.RepairState = DeviceRepairState.Failed;
                device.ResultMessage = "Repair coordinator failed before a verified result was returned.";
            }
            CoreHardwareItem core = FindCoreNoLock(runtimeId);
            if (core != null && core.RepairState == DeviceRepairState.Repairing)
            {
                core.RepairState = DeviceRepairState.Failed;
                core.ResultMessage = "Repair coordinator failed before a verified result was returned.";
            }
        }

        private static void AddToSummary(DeviceRepairSummary summary, DeviceRepairState state)
        {
            if (state == DeviceRepairState.Succeeded) summary.Succeeded++;
            else if (state == DeviceRepairState.Skipped || state == DeviceRepairState.Unsupported) summary.Skipped++;
            else if (state == DeviceRepairState.RebootRequired) summary.RebootRequired++;
            else if (state == DeviceRepairState.Canceled) summary.Canceled++;
            else if (state == DeviceRepairState.Partial) summary.Partial++;
            else if (state == DeviceRepairState.NeedsDriver) summary.NeedsDriver++;
            else if (state == DeviceRepairState.Ambiguous || state == DeviceRepairState.RiskDeclined) summary.Skipped++;
            else if (state == DeviceRepairState.Failed) summary.Failed++;
        }

        private void SetStatus(string value)
        {
            lock (stateLock) { lastStatus = value; }
            RaiseStatusChanged();
        }

        private void RaiseStatusChanged()
        {
            EventHandler handler = StatusChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseDevicesChanged()
        {
            EventHandler handler = DevicesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseCoreHardwareChanged()
        {
            EventHandler handler = CoreHardwareChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseInputStackChanged()
        {
            EventHandler handler = InputStackChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
