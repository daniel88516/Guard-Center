using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class DisplayGuardModule : IDisposable
    {
        private readonly DisplayGuardSettings settings;
        private readonly Action persistSettings;
        private readonly IDisplayGuardProvider provider;
        private readonly object stateLock = new object();
        private readonly List<DisplayGuardDevice> devices = new List<DisplayGuardDevice>();
        private readonly Dictionary<string, DisplayGuardDeviceQueue> queues =
            new Dictionary<string, DisplayGuardDeviceQueue>(StringComparer.OrdinalIgnoreCase);
        private DisplayGuardProfileStore profiles;
        private int refreshVersion;
        private int activeTopologyOperations;
        private bool disposed;
        private string lastStatus = "Display Guard starting.";

        public event EventHandler StatusChanged;
        public event EventHandler DisplaysChanged;

        public DisplayGuardModule(DisplayGuardSettings settings, Action persistSettings)
            : this(settings, persistSettings, new CompositeDisplayGuardProvider())
        {
        }

        public DisplayGuardModule(DisplayGuardSettings settings, Action persistSettings, IDisplayGuardProvider provider)
        {
            this.settings = settings;
            this.persistSettings = persistSettings;
            this.provider = provider;

            bool hadProfileError;
            profiles = DisplayGuardProfileCodec.Decode(settings.Profiles, out hadProfileError);
            settings.SelectedMode = DisplayGuardModes.NormalizeKey(settings.SelectedMode);
            if (hadProfileError)
            {
                settings.Profiles = DisplayGuardProfileCodec.Encode(profiles);
                SetStatus("Display Guard settings were damaged and were reset.");
            }

            try
            {
                SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
                SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            }
            catch (Exception ex)
            {
                SetStatus("Display Guard could not subscribe to Windows display events: " + ex.Message);
                AppLog.Write("Display Guard", "Display event subscription failed: " + ex);
            }
        }

        public string StatusText
        {
            get
            {
                lock (stateLock)
                {
                    return lastStatus;
                }
            }
        }

        public string SelectedMode
        {
            get { return DisplayGuardModes.NormalizeKey(settings.SelectedMode); }
        }

        public DisplayGuardModeDefinition[] Modes
        {
            get { return DisplayGuardModes.All; }
        }

        public void Start()
        {
            RefreshAsync(false);
        }

        public List<DisplayGuardMonitorInfo> GetMonitors()
        {
            lock (stateLock)
            {
                var result = new List<DisplayGuardMonitorInfo>();
                for (int i = 0; i < devices.Count; i++)
                {
                    result.Add(devices[i].ToInfo());
                }

                return result;
            }
        }

        public void SetSelectedMode(string modeKey)
        {
            string normalized = DisplayGuardModes.NormalizeKey(modeKey);
            if (string.Equals(settings.SelectedMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settings.SelectedMode = normalized;
            if (string.Equals(normalized, DisplayGuardModes.Live, StringComparison.OrdinalIgnoreCase))
            {
                int saved;
                int skipped;
                lock (stateLock)
                {
                    DisplayGuardModeService.CaptureCurrent(profiles, DisplayGuardModes.Live,
                        CreateSnapshotNoLock(), out saved, out skipped);
                }
            }
            SaveProfileSettings();
            SetStatus("Display mode selected: " + DisplayGuardModes.Get(settings.SelectedMode).Name + ".");
        }

        public void RefreshAsync(bool applySelectedModeAfterRefresh)
        {
            if (disposed)
            {
                return;
            }

            int version = Interlocked.Increment(ref refreshVersion);
            SetStatus("Refreshing displays.");
            QueueTopologyOperation(delegate { RefreshCore(version, applySelectedModeAfterRefresh, false); });
        }

        public bool TryAutomaticTopologyRefreshAsync()
        {
            if (disposed || HasBusyDisplayNoLockSafe()
                || Interlocked.CompareExchange(ref activeTopologyOperations, 1, 0) != 0)
            {
                return false;
            }

            int version = Interlocked.Increment(ref refreshVersion);
            Task.Run(delegate
            {
                try
                {
                    RefreshCore(version, false, true);
                }
                finally
                {
                    Interlocked.Decrement(ref activeTopologyOperations);
                }
            });
            return true;
        }

        public void ApplySelectedModeAsync()
        {
            RefreshAndApplySelectedModeAsync(false);
        }

        private void RefreshAndApplySelectedModeAsync(bool automatic)
        {
            if (disposed)
            {
                return;
            }

            int version = Interlocked.Increment(ref refreshVersion);
            SetStatus((automatic ? "Restoring " : "Applying ") + DisplayGuardModes.Get(SelectedMode).Name + " mode.");
            QueueTopologyOperation(delegate
            {
                RefreshCore(version, false, false);
                ApplySelectedModeFromSnapshotAsync(automatic);
            });
        }

        public void SaveCurrentToSelectedMode()
        {
            SaveCurrentToMode(SelectedMode, true);
        }

        public void SaveCurrentToCustomMode()
        {
            if (disposed)
            {
                return;
            }

            settings.SelectedMode = DisplayGuardModes.Custom;
            SaveProfileSettings();
            SetStatus("Saving Custom mode.");
            int version = Interlocked.Increment(ref refreshVersion);
            QueueTopologyOperation(delegate
            {
                RefreshCore(version, false, false);
                SaveCurrentToMode(DisplayGuardModes.Custom, false);
            });
        }

        private void SaveCurrentToMode(string modeKey, bool selectedModeLabel)
        {
            List<DisplayGuardMonitorInfo> snapshot = GetMonitors();
            int saved;
            int skipped;
            bool changed;
            lock (stateLock)
            {
                changed = DisplayGuardModeService.CaptureCurrent(profiles, modeKey, snapshot, out saved, out skipped);
            }
            if (changed)
            {
                SaveProfileSettings();
            }
            else if (string.Equals(modeKey, DisplayGuardModes.Custom, StringComparison.OrdinalIgnoreCase))
            {
                SaveProfileSettings();
            }

            SetStatus("Saved " + saved + " display" + Plural(saved) + " to "
                + DisplayGuardModes.Get(selectedModeLabel ? SelectedMode : modeKey).Name + "."
                + (skipped > 0 ? " Skipped " + skipped + " unmatched display" + Plural(skipped) + "." : string.Empty));
        }

        public void QueueBrightness(string runtimeId, int percent)
        {
            QueueValue(runtimeId, DisplayGuardFeature.Brightness, percent);
        }

        public void QueueContrast(string runtimeId, int percent)
        {
            QueueValue(runtimeId, DisplayGuardFeature.Contrast, percent);
        }

        public void QueueAllBrightness(int percent)
        {
            QueueAll(DisplayGuardFeature.Brightness, percent);
        }

        public void QueueAllContrast(int percent)
        {
            QueueAll(DisplayGuardFeature.Contrast, percent);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
                SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Display Guard event unsubscribe failed: " + ex);
                AppLog.Write("Display Guard", "Display event unsubscribe failed: " + ex);
            }

            List<DisplayGuardDeviceQueue> queueSnapshot;
            List<DisplayGuardDevice> deviceSnapshot;
            lock (stateLock)
            {
                queueSnapshot = new List<DisplayGuardDeviceQueue>(queues.Values);
                queues.Clear();
                deviceSnapshot = new List<DisplayGuardDevice>(devices);
                devices.Clear();
            }

            DisposeQueuesAndDevices(queueSnapshot, deviceSnapshot);
        }

        private void RefreshCore(int version, bool applySelectedModeAfterRefresh, bool silent)
        {
            List<DisplayGuardDevice> newDevices;
            try
            {
                newDevices = provider.EnumerateMonitors(!silent);
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    SetStatus("Display refresh failed: " + ex.Message);
                }
                else
                {
                    Trace.TraceWarning("Display polling refresh failed: " + ex);
                }
                return;
            }

            List<DisplayGuardDeviceQueue> oldQueues;
            List<DisplayGuardDevice> oldDevices;
            bool shouldPersist = false;
            bool accepted = false;
            int monitorCount;
            int controllableCount;
            lock (stateLock)
            {
                if (disposed || version != refreshVersion)
                {
                    oldQueues = new List<DisplayGuardDeviceQueue>();
                    oldDevices = newDevices;
                    monitorCount = 0;
                    controllableCount = 0;
                }
                else
                {
                    oldQueues = new List<DisplayGuardDeviceQueue>(queues.Values);
                    oldDevices = new List<DisplayGuardDevice>(devices);
                    queues.Clear();
                    devices.Clear();
                    devices.AddRange(newDevices);
                    accepted = true;
                    List<DisplayGuardMonitorInfo> infos = CreateSnapshotNoLock();
                    shouldPersist = DisplayGuardModeService.EnsureInitialProfiles(profiles, infos);
                    monitorCount = infos.Count;
                    controllableCount = CountControllable(infos);
                }
            }

            DisposeQueuesAndDevices(oldQueues, oldDevices);

            if (!accepted)
            {
                return;
            }

            if (shouldPersist)
            {
                SaveProfileSettings();
            }

            RaiseDisplaysChanged();
            if (!silent)
            {
                AppLog.Write("Display Guard", "Refresh accepted monitors=" + monitorCount
                    + " controllable=" + controllableCount + " version=" + version);
                SetStatus(monitorCount == 0
                    ? "No displays detected."
                    : "Detected " + monitorCount + " display" + Plural(monitorCount) + ", "
                        + controllableCount + " controllable.");
            }

            if (applySelectedModeAfterRefresh && controllableCount > 0)
            {
                ApplySelectedModeFromSnapshotAsync(true);
            }
        }

        private void QueueTopologyOperation(Action operation)
        {
            Interlocked.Increment(ref activeTopologyOperations);
            Task.Run(delegate
            {
                try
                {
                    operation();
                }
                finally
                {
                    Interlocked.Decrement(ref activeTopologyOperations);
                }
            });
        }

        private void ApplySelectedModeFromSnapshotAsync(bool automatic)
        {
            if (disposed)
            {
                return;
            }

            string modeKey = SelectedMode;
            DisplayGuardModeDefinition mode = DisplayGuardModes.Get(modeKey);
            DisplayGuardApplyPlan plan = DisplayGuardModeService.BuildApplyPlan(profiles, modeKey, GetMonitors());
            if (plan.Items.Count == 0)
            {
                SetStatus("No matched displays to apply " + mode.Name + SummarizeSkips(plan) + ".");
                return;
            }

            CancelAllPending();
            SetStatus((automatic ? "Restoring " : "Applying ") + mode.Name + " mode.");
            QueueTopologyOperation(delegate
            {
                int ok = 0;
                int failed = 0;
                int partial = 0;
                int clamped = 0;
                for (int i = 0; i < plan.Items.Count; i++)
                {
                    DisplayGuardApplyPlanItem item = plan.Items[i];
                    DisplayGuardDeviceQueue queue = GetOrCreateQueue(item.RuntimeId);
                    if (queue == null)
                    {
                        failed++;
                        continue;
                    }

                    DisplayGuardOperationResult result = queue.ApplyNow(
                        item.HasBrightness ? (int?)item.BrightnessPercent : null,
                        item.HasContrast ? (int?)item.ContrastPercent : null);
                    if (item.BrightnessClamped || item.ContrastClamped)
                    {
                        clamped++;
                    }

                    if (result.FailedCount == 0 && result.AttemptedCount > 0)
                    {
                        ok++;
                    }
                    else if (result.SucceededCount > 0)
                    {
                        partial++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                string status = mode.Name + " mode: " + ok + " ok";
                if (partial > 0)
                {
                    status += ", " + partial + " partial";
                }
                if (failed > 0)
                {
                    status += ", " + failed + " failed";
                }
                if (clamped > 0)
                {
                    status += ", " + clamped + " clamped";
                }

                status += SummarizeSkips(plan) + ".";
                SetStatus(status);
            });
        }

        private void QueueValue(string runtimeId, DisplayGuardFeature feature, int percent)
        {
            Interlocked.Increment(ref refreshVersion);
            percent = ClampPercent(percent);
            SaveLiveAdjustment(runtimeId, feature, percent);
            DisplayGuardDeviceQueue queue = GetOrCreateQueue(runtimeId);
            if (queue == null)
            {
                SetStatus("Display is no longer connected.");
                return;
            }

            queue.Queue(feature, percent);
            SetStatus((feature == DisplayGuardFeature.Brightness ? "Brightness" : "Contrast")
                + " queued: " + percent + "%.");
        }

        private void QueueAll(DisplayGuardFeature feature, int percent)
        {
            Interlocked.Increment(ref refreshVersion);
            percent = ClampPercent(percent);
            List<DisplayGuardMonitorInfo> snapshot = GetMonitors();
            int queued = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                DisplayGuardMonitorInfo monitor = snapshot[i];
                if ((feature == DisplayGuardFeature.Brightness && !monitor.SupportsBrightness)
                    || (feature == DisplayGuardFeature.Contrast && !monitor.SupportsContrast))
                {
                    continue;
                }

                DisplayGuardDeviceQueue queue = GetOrCreateQueue(monitor.RuntimeId);
                if (queue != null)
                {
                    SaveLiveAdjustment(monitor.RuntimeId, feature, percent);
                    queue.Queue(feature, percent);
                    queued++;
                }
            }

            SetStatus((feature == DisplayGuardFeature.Brightness ? "Brightness" : "Contrast")
                + " queued for " + queued + " display" + Plural(queued) + ": " + percent + "%.");
        }

        private void SaveLiveAdjustment(string runtimeId, DisplayGuardFeature feature, int percent)
        {
            if (!string.Equals(SelectedMode, DisplayGuardModes.Live, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool changed;
            lock (stateLock)
            {
                changed = DisplayGuardModeService.CaptureAdjustment(profiles, DisplayGuardModes.Live,
                    CreateSnapshotNoLock(), runtimeId, feature, percent);
            }

            if (changed)
            {
                SaveProfileSettings();
            }
        }

        private bool HasBusyDisplayNoLockSafe()
        {
            lock (stateLock)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].IsBusy)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private DisplayGuardOperationResult ApplyValues(DisplayGuardDevice device, int? brightnessPercent,
            int? contrastPercent)
        {
            var result = new DisplayGuardOperationResult();
            if (device == null)
            {
                result.FailedCount++;
                result.LastError = "Display is no longer connected.";
                return result;
            }

            if (brightnessPercent.HasValue)
            {
                result.AttemptedCount++;
                string error;
                if (device.SetBrightnessPercent(brightnessPercent.Value, out error))
                {
                    result.SucceededCount++;
                    AppLog.Write("Display Guard", device.DisplayName + " brightness verified="
                        + device.ToInfo().BrightnessPercent + "% requested=" + brightnessPercent.Value + "%.");
                }
                else
                {
                    result.FailedCount++;
                    result.LastError = error;
                    AppLog.Write("Display Guard", device.DisplayName + " brightness failed: " + error);
                }
            }

            if (contrastPercent.HasValue)
            {
                result.AttemptedCount++;
                string error;
                if (device.SetContrastPercent(contrastPercent.Value, out error))
                {
                    result.SucceededCount++;
                    AppLog.Write("Display Guard", device.DisplayName + " contrast verified="
                        + device.ToInfo().ContrastPercent + "% requested=" + contrastPercent.Value + "%.");
                }
                else
                {
                    result.FailedCount++;
                    result.LastError = error;
                    AppLog.Write("Display Guard", device.DisplayName + " contrast failed: " + error);
                }
            }

            if (result.FailedCount > 0 && !string.IsNullOrWhiteSpace(result.LastError))
            {
                SetStatus(device.DisplayName + ": " + result.LastError);
            }
            else if (result.SucceededCount > 0)
            {
                SetStatus(device.DisplayName + " updated.");
            }

            RaiseDisplaysChanged();
            return result;
        }

        private DisplayGuardDeviceQueue GetOrCreateQueue(string runtimeId)
        {
            lock (stateLock)
            {
                DisplayGuardDevice device = FindDeviceNoLock(runtimeId);
                if (device == null)
                {
                    return null;
                }

                DisplayGuardDeviceQueue queue;
                if (!queues.TryGetValue(runtimeId, out queue))
                {
                    queue = new DisplayGuardDeviceQueue(device, ApplyValues, SetDeviceBusy);
                    queues[runtimeId] = queue;
                }

                return queue;
            }
        }

        private DisplayGuardDevice FindDeviceNoLock(string runtimeId)
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

        private void SetDeviceBusy(DisplayGuardDevice device, bool busy)
        {
            lock (stateLock)
            {
                device.IsBusy = busy;
            }

            RaiseStatusChanged();
            RaiseDisplaysChanged();
        }

        private void CancelAllPending()
        {
            lock (stateLock)
            {
                foreach (DisplayGuardDeviceQueue queue in queues.Values)
                {
                    queue.CancelPending();
                }
            }
        }

        private void SaveProfileSettings()
        {
            settings.SelectedMode = DisplayGuardModes.NormalizeKey(settings.SelectedMode);
            settings.Profiles = DisplayGuardProfileCodec.Encode(profiles);
            try
            {
                if (persistSettings != null)
                {
                    persistSettings();
                }
            }
            catch (Exception ex)
            {
                SetStatus("Display Guard settings save failed: " + ex.Message);
            }
        }

        private List<DisplayGuardMonitorInfo> CreateSnapshotNoLock()
        {
            var result = new List<DisplayGuardMonitorInfo>();
            for (int i = 0; i < devices.Count; i++)
            {
                result.Add(devices[i].ToInfo());
            }

            return result;
        }

        private static int CountControllable(List<DisplayGuardMonitorInfo> monitors)
        {
            int count = 0;
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].IsControllable)
                {
                    count++;
                }
            }

            return count;
        }

        private static string SummarizeSkips(DisplayGuardApplyPlan plan)
        {
            var parts = new List<string>();
            if (plan.OfflineProfileCount > 0)
            {
                parts.Add(plan.OfflineProfileCount + " offline");
            }
            if (plan.NewMonitorCount > 0)
            {
                parts.Add(plan.NewMonitorCount + " new");
            }
            if (plan.UnmatchedMonitorCount > 0)
            {
                parts.Add(plan.UnmatchedMonitorCount + " unmatched");
            }
            if (plan.UnsupportedFeatureCount > 0)
            {
                parts.Add(plan.UnsupportedFeatureCount + " unsupported feature");
            }

            if (parts.Count == 0)
            {
                return string.Empty;
            }

            return " (skipped " + string.Join(", ", parts.ToArray()) + ")";
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static string Plural(int count)
        {
            return count == 1 ? string.Empty : "s";
        }

        private void SetStatus(string status)
        {
            lock (stateLock)
            {
                lastStatus = status;
            }

            RaiseStatusChanged();
        }

        private void RaiseStatusChanged()
        {
            EventHandler handler = StatusChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseDisplaysChanged()
        {
            EventHandler handler = DisplaysChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            RefreshAsync(true);
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                RefreshAsync(true);
            }
        }

        private static void DisposeQueuesAndDevices(List<DisplayGuardDeviceQueue> queueSnapshot,
            List<DisplayGuardDevice> deviceSnapshot)
        {
            bool allIdle = true;
            for (int i = 0; i < queueSnapshot.Count; i++)
            {
                queueSnapshot[i].CancelPending();
                if (!queueSnapshot[i].WaitForIdle(10000))
                {
                    AppLog.Write("Display Guard", "A hardware operation did not become idle before disposal timeout.");
                    allIdle = false;
                }
                queueSnapshot[i].Dispose();
            }

            if (allIdle)
            {
                DisposeDeviceList(deviceSnapshot);
                return;
            }

            Task.Run(delegate
            {
                for (int i = 0; i < queueSnapshot.Count; i++)
                {
                    queueSnapshot[i].WaitForIdle(Timeout.Infinite);
                }
                DisposeDeviceList(deviceSnapshot);
                AppLog.Write("Display Guard", "Deferred physical monitor handle disposal completed.");
            });
        }

        private static void DisposeDeviceList(List<DisplayGuardDevice> deviceSnapshot)
        {
            for (int i = 0; i < deviceSnapshot.Count; i++)
            {
                deviceSnapshot[i].Dispose();
            }
        }
    }

    internal sealed class DisplayGuardOperationResult
    {
        public int AttemptedCount;
        public int SucceededCount;
        public int FailedCount;
        public string LastError = string.Empty;
    }

    internal sealed class DisplayGuardDeviceQueue : IDisposable
    {
        private readonly DisplayGuardDevice device;
        private readonly Func<DisplayGuardDevice, int?, int?, DisplayGuardOperationResult> applyValues;
        private readonly Action<DisplayGuardDevice, bool> setBusy;
        private readonly object queueLock = new object();
        private readonly object activityCallbackLock = new object();
        private readonly SemaphoreSlim hardwareLock = new SemaphoreSlim(1, 1);
        private readonly ManualResetEventSlim idleSignal = new ManualResetEventSlim(true);
        private int activeActivities;
        private bool workerRunning;
        private bool publishedBusy;
        private bool disposed;
        private int? pendingBrightness;
        private int? pendingContrast;

        public DisplayGuardDeviceQueue(DisplayGuardDevice device,
            Func<DisplayGuardDevice, int?, int?, DisplayGuardOperationResult> applyValues,
            Action<DisplayGuardDevice, bool> setBusy = null)
        {
            this.device = device;
            this.applyValues = applyValues;
            this.setBusy = setBusy;
        }

        public void Queue(DisplayGuardFeature feature, int percent)
        {
            bool startWorker = false;
            lock (queueLock)
            {
                if (disposed)
                {
                    return;
                }

                if (feature == DisplayGuardFeature.Brightness)
                {
                    pendingBrightness = percent;
                }
                else
                {
                    pendingContrast = percent;
                }

                if (!workerRunning)
                {
                    workerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                BeginActivity();
                Task.Run(ProcessQueue);
            }
        }

        public DisplayGuardOperationResult ApplyNow(int? brightnessPercent, int? contrastPercent)
        {
            BeginActivity();
            try
            {
                hardwareLock.Wait();
                try
                {
                    return applyValues(device, brightnessPercent, contrastPercent);
                }
                finally
                {
                    hardwareLock.Release();
                }
            }
            finally
            {
                EndActivity();
            }
        }

        public void CancelPending()
        {
            lock (queueLock)
            {
                pendingBrightness = null;
                pendingContrast = null;
            }
        }

        public bool WaitForIdle(int milliseconds)
        {
            return idleSignal.Wait(milliseconds);
        }

        public void Dispose()
        {
            lock (queueLock)
            {
                disposed = true;
                pendingBrightness = null;
                pendingContrast = null;
            }
        }

        private void ProcessQueue()
        {
            try
            {
                while (true)
                {
                    int? brightness;
                    int? contrast;
                    lock (queueLock)
                    {
                        if (disposed)
                        {
                            workerRunning = false;
                            return;
                        }

                        brightness = pendingBrightness;
                        contrast = pendingContrast;
                        pendingBrightness = null;
                        pendingContrast = null;
                        if (!brightness.HasValue && !contrast.HasValue)
                        {
                            workerRunning = false;
                            return;
                        }
                    }

                    hardwareLock.Wait();
                    try
                    {
                        bool canApply;
                        lock (queueLock)
                        {
                            canApply = !disposed;
                        }
                        if (canApply)
                        {
                            applyValues(device, brightness, contrast);
                        }
                    }
                    finally
                    {
                        hardwareLock.Release();
                    }
                }
            }
            finally
            {
                EndActivity();
            }
        }

        private void BeginActivity()
        {
            lock (queueLock)
            {
                activeActivities++;
                if (activeActivities == 1)
                {
                    idleSignal.Reset();
                }
            }
            PublishBusyState();
        }

        private void EndActivity()
        {
            bool possiblyIdle;
            lock (queueLock)
            {
                if (activeActivities > 0)
                {
                    activeActivities--;
                }
                possiblyIdle = activeActivities == 0;
            }
            PublishBusyState();

            if (possiblyIdle)
            {
                lock (queueLock)
                {
                    if (activeActivities == 0)
                    {
                        idleSignal.Set();
                    }
                }
            }
        }

        private void PublishBusyState()
        {
            if (setBusy == null)
            {
                return;
            }

            lock (activityCallbackLock)
            {
                bool busy;
                lock (queueLock)
                {
                    busy = activeActivities > 0;
                }
                if (busy == publishedBusy)
                {
                    return;
                }

                publishedBusy = busy;
                setBusy(device, busy);
            }
        }
    }
}
