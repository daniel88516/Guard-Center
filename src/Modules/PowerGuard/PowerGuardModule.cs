using Microsoft.Win32;
using System;
using System.Threading;

namespace GuardCenter
{
    internal sealed class PowerGuardModule : IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private readonly object stateLock = new object();
        private readonly IPowerRequestLeaseFactory leaseFactory;
        private readonly TimeProvider timeProvider;
        private readonly bool subscribedToPowerEvents;
        private readonly ITimer timer;
        private IPowerRequestLease activeLease;
        private PowerGuardDurationKind durationKind = PowerGuardDurationKind.TwoHours;
        private TimeSpan customDuration = TimeSpan.FromHours(2);
        private bool keepDisplayOnPreference;
        private DateTimeOffset? endAtUtc;
        private bool keepDisplayOn;
        private bool disposed;
        private string statusText = "Power Guard 已關閉，Windows 電源設定正在管理系統。";
        private string lastError = string.Empty;

        public event EventHandler StateChanged;

        public PowerGuardModule()
            : this(null, new WindowsPowerRequestLeaseFactory(), TimeProvider.System, true, true)
        {
        }

        public PowerGuardModule(PowerGuardSettings settings)
            : this(settings, new WindowsPowerRequestLeaseFactory(), TimeProvider.System, true, true)
        {
        }

        internal PowerGuardModule(IPowerRequestLeaseFactory leaseFactory, TimeProvider timeProvider,
            bool createTimer, bool subscribeToPowerEvents)
            : this(null, leaseFactory, timeProvider, createTimer, subscribeToPowerEvents)
        {
        }

        internal PowerGuardModule(PowerGuardSettings settings, IPowerRequestLeaseFactory leaseFactory,
            TimeProvider timeProvider, bool createTimer, bool subscribeToPowerEvents)
        {
            this.leaseFactory = leaseFactory ?? new WindowsPowerRequestLeaseFactory();
            this.timeProvider = timeProvider ?? TimeProvider.System;
            if (settings != null)
            {
                durationKind = ParseDurationKind(settings.DurationKind);
                customDuration = PowerGuardDurations.ClampCustom(
                    TimeSpan.FromMinutes(settings.CustomDurationMinutes));
                keepDisplayOnPreference = settings.KeepDisplayOn;
            }

            if (createTimer)
            {
                timer = this.timeProvider.CreateTimer(Timer_Tick, null,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            if (subscribeToPowerEvents)
            {
                try
                {
                    SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
                    subscribedToPowerEvents = true;
                }
                catch (Exception ex)
                {
                    statusText = "Power Guard 無法監聽 Windows 電源恢復事件。";
                    lastError = statusText;
                    AppLog.Write("Power Guard", "Power event subscription failed: " + ex);
                }
            }
        }

        public PowerGuardState GetState()
        {
            lock (stateLock)
            {
                return CreateStateNoLock(timeProvider.GetUtcNow());
            }
        }

        public bool TrySetEnabled(bool enabled, out string error)
        {
            IPowerRequestLease oldLease = null;
            bool changed = false;
            lock (stateLock)
            {
                if (disposed)
                {
                    error = "Power Guard 已停止。";
                    return false;
                }

                bool isEnabled = activeLease != null;
                if (isEnabled == enabled)
                {
                    error = string.Empty;
                    return true;
                }

                if (enabled)
                {
                    IPowerRequestLease newLease;
                    bool displayOn = keepDisplayOnPreference;
                    if (!leaseFactory.TryCreate(displayOn, out newLease, out error))
                    {
                        SetErrorNoLock(error);
                        AppLog.Write("Power Guard", "Enable failed: " + error);
                        changed = true;
                    }
                    else
                    {
                        activeLease = newLease;
                        keepDisplayOn = displayOn;
                        endAtUtc = durationKind == PowerGuardDurationKind.UntilManual
                            ? (DateTimeOffset?)null
                            : timeProvider.GetUtcNow() + GetSelectedDurationNoLock();
                        statusText = displayOn
                            ? "保持清醒與螢幕恆亮已開啟。"
                            : (durationKind == PowerGuardDurationKind.UntilManual
                                ? "保持清醒已開啟，未設定結束時間。"
                                : "保持清醒已開啟，倒數已開始。");
                        lastError = string.Empty;
                        UpdateTimerNoLock();
                        error = string.Empty;
                        changed = true;
                    }
                }
                else
                {
                    oldLease = activeLease;
                    activeLease = null;
                    keepDisplayOn = false;
                    endAtUtc = null;
                    statusText = "已解除保持清醒，Windows 電源設定正在管理系統。";
                    lastError = string.Empty;
                    UpdateTimerNoLock();
                    error = string.Empty;
                    changed = true;
                }
            }

            if (oldLease != null)
            {
                oldLease.Dispose();
            }
            if (changed)
            {
                RaiseStateChanged();
                if (string.IsNullOrEmpty(error))
                {
                    AppLog.Write("Power Guard", enabled
                        ? "Keep-awake enabled. duration=" + durationKind
                            + " display=" + keepDisplayOn + "."
                        : "Keep-awake disabled; requests cleared.");
                }
            }
            return string.IsNullOrEmpty(error);
        }

        public bool TrySetKeepDisplayOn(bool enabled, out string error)
        {
            IPowerRequestLease oldLease = null;
            bool changed = false;
            lock (stateLock)
            {
                if (disposed)
                {
                    error = "Power Guard 已停止。";
                    return false;
                }
                if (activeLease == null)
                {
                    if (!enabled)
                    {
                        keepDisplayOn = false;
                        error = string.Empty;
                        return true;
                    }
                    error = "請先開啟保持清醒。";
                    SetErrorNoLock(error);
                    changed = true;
                }
                else if (keepDisplayOn == enabled)
                {
                    error = string.Empty;
                    return true;
                }
                else
                {
                    IPowerRequestLease newLease;
                    if (!leaseFactory.TryCreate(enabled, out newLease, out error))
                    {
                        SetErrorNoLock(error);
                        AppLog.Write("Power Guard", "Display request change failed: " + error);
                        changed = true;
                    }
                    else
                    {
                        oldLease = activeLease;
                        activeLease = newLease;
                        keepDisplayOn = enabled;
                        keepDisplayOnPreference = enabled;
                        statusText = enabled
                            ? "保持清醒與螢幕恆亮已開啟。"
                            : "系統保持清醒；螢幕仍依 Windows 設定自動熄滅。";
                        lastError = string.Empty;
                        error = string.Empty;
                        changed = true;
                    }
                }
            }

            if (oldLease != null)
            {
                oldLease.Dispose();
            }
            if (changed)
            {
                RaiseStateChanged();
                if (string.IsNullOrEmpty(error))
                {
                    AppLog.Write("Power Guard", "Display request changed. enabled=" + enabled + ".");
                }
            }
            return string.IsNullOrEmpty(error);
        }

        public void SelectDuration(PowerGuardDurationKind kind, TimeSpan customValue)
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }

                durationKind = kind;
                if (kind == PowerGuardDurationKind.Custom)
                {
                    customDuration = PowerGuardDurations.ClampCustom(customValue);
                }

                if (activeLease == null)
                {
                    endAtUtc = null;
                    statusText = "已更新預設保持時間；開啟保持清醒後才會開始。";
                }
                else if (durationKind == PowerGuardDurationKind.UntilManual)
                {
                    endAtUtc = null;
                    statusText = "已切換為直到手動關閉。";
                }
                else
                {
                    endAtUtc = timeProvider.GetUtcNow() + GetSelectedDurationNoLock();
                    statusText = "已從目前時間重新開始倒數。";
                }
                lastError = string.Empty;
                UpdateTimerNoLock();
            }
            RaiseStateChanged();
            AppLog.Write("Power Guard", "Duration selected. kind=" + kind
                + " customMinutes=" + customValue.TotalMinutes + ".");
        }

        public bool TryActivate(PowerGuardDurationKind kind, TimeSpan customValue, out string error)
        {
            SelectDuration(kind, customValue);
            return TrySetEnabled(true, out error);
        }

        public void AdjustRemaining(TimeSpan remaining)
        {
            bool turnOff = false;
            lock (stateLock)
            {
                if (disposed || activeLease == null || durationKind == PowerGuardDurationKind.UntilManual)
                {
                    return;
                }

                TimeSpan next = PowerGuardDurations.ClampRemaining(remaining, GetSelectedDurationNoLock());
                if (next <= TimeSpan.Zero)
                {
                    turnOff = true;
                }
                else
                {
                    endAtUtc = timeProvider.GetUtcNow() + next;
                    statusText = "已更新剩餘保持時間。";
                    lastError = string.Empty;
                    UpdateTimerNoLock();
                }
            }

            if (turnOff)
            {
                string ignored;
                TrySetEnabled(false, out ignored);
            }
            else
            {
                RaiseStateChanged();
                AppLog.Write("Power Guard", "Remaining time adjusted. minutes=" + remaining.TotalMinutes + ".");
            }
        }

        internal void UpdateFromClock()
        {
            IPowerRequestLease expiredLease = null;
            bool notify = false;
            lock (stateLock)
            {
                if (disposed || activeLease == null || !endAtUtc.HasValue)
                {
                    return;
                }

                if (timeProvider.GetUtcNow() >= endAtUtc.Value)
                {
                    expiredLease = activeLease;
                    activeLease = null;
                    keepDisplayOn = false;
                    endAtUtc = null;
                    statusText = "保持清醒已結束，已交回 Windows 電源設定管理。";
                    lastError = string.Empty;
                    UpdateTimerNoLock();
                }
                notify = true;
            }

            if (expiredLease != null)
            {
                expiredLease.Dispose();
                AppLog.Write("Power Guard", "Timed keep-awake completed; requests cleared.");
            }
            if (notify)
            {
                RaiseStateChanged();
            }
        }

        internal void HandleResume()
        {
            IPowerRequestLease oldLease = null;
            bool notify = false;
            lock (stateLock)
            {
                if (disposed || activeLease == null)
                {
                    return;
                }

                DateTimeOffset now = timeProvider.GetUtcNow();
                if (endAtUtc.HasValue && now >= endAtUtc.Value)
                {
                    oldLease = activeLease;
                    activeLease = null;
                    keepDisplayOn = false;
                    endAtUtc = null;
                    statusText = "保持清醒已在系統恢復前到期，已交回 Windows 電源設定管理。";
                    lastError = string.Empty;
                    UpdateTimerNoLock();
                    notify = true;
                }
                else
                {
                    IPowerRequestLease newLease;
                    string error;
                    if (leaseFactory.TryCreate(keepDisplayOn, out newLease, out error))
                    {
                        oldLease = activeLease;
                        activeLease = newLease;
                        statusText = "系統已恢復，保持清醒要求已重新建立。";
                        lastError = string.Empty;
                    }
                    else
                    {
                        oldLease = activeLease;
                        activeLease = null;
                        keepDisplayOn = false;
                        endAtUtc = null;
                        SetErrorNoLock("系統恢復後無法重新建立保持清醒要求：" + error);
                        UpdateTimerNoLock();
                        AppLog.Write("Power Guard", "Resume reapply failed: " + error);
                    }
                    notify = true;
                }
            }

            if (oldLease != null)
            {
                oldLease.Dispose();
            }
            if (notify)
            {
                RaiseStateChanged();
                PowerGuardState state = GetState();
                if (state.IsEnabled)
                {
                    AppLog.Write("Power Guard", "Resume detected; power request recreated.");
                }
                else if (string.IsNullOrEmpty(state.LastError))
                {
                    AppLog.Write("Power Guard", "Resume detected after deadline; requests cleared.");
                }
            }
        }

        public void Dispose()
        {
            IPowerRequestLease lease;
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                lease = activeLease;
                activeLease = null;
                keepDisplayOn = false;
                endAtUtc = null;
            }

            if (subscribedToPowerEvents)
            {
                try
                {
                    SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
                }
                catch (Exception ex)
                {
                    AppLog.Write("Power Guard", "Power event unsubscribe failed: " + ex);
                }
            }

            if (timer != null)
            {
                timer.Dispose();
            }

            if (lease != null)
            {
                lease.Dispose();
            }
        }

        private PowerGuardState CreateStateNoLock(DateTimeOffset now)
        {
            TimeSpan? remaining = null;
            if (activeLease != null && endAtUtc.HasValue)
            {
                TimeSpan value = endAtUtc.Value - now;
                remaining = value > TimeSpan.Zero ? value : TimeSpan.Zero;
            }

            return new PowerGuardState(activeLease != null, keepDisplayOn, durationKind,
                GetSelectedDurationNoLock(), customDuration, endAtUtc, remaining, statusText, lastError);
        }

        private TimeSpan GetSelectedDurationNoLock()
        {
            return PowerGuardDurations.Get(durationKind, customDuration);
        }

        private static PowerGuardDurationKind ParseDurationKind(string value)
        {
            PowerGuardDurationKind parsed;
            if (Enum.TryParse(value, true, out parsed)
                && Enum.IsDefined(typeof(PowerGuardDurationKind), parsed))
            {
                return parsed;
            }
            return PowerGuardDurationKind.TwoHours;
        }

        private void UpdateTimerNoLock()
        {
            if (timer == null)
            {
                return;
            }

            if (activeLease != null && endAtUtc.HasValue)
            {
                timer.Change(TickInterval, TickInterval);
            }
            else
            {
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        private void SetErrorNoLock(string error)
        {
            lastError = error ?? "未知錯誤。";
            statusText = lastError;
        }

        private void Timer_Tick(object state)
        {
            UpdateFromClock();
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                HandleResume();
            }
        }

        private void RaiseStateChanged()
        {
            EventHandler handler = StateChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
