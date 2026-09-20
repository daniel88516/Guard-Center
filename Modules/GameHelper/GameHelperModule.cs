using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GuardCenter
{
    internal sealed class GameHelperModule : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const uint WmQuit = 0x0012;
        private const uint PmNoRemove = 0x0000;
        private const int GwlExstyle = -20;
        private const int SwForceminimize = 11;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int CrosshairMinSize = 2;
        private const int CrosshairMaxSize = 48;
        private const int ForegroundRestoreStableMilliseconds = 300;
        private static readonly TimeSpan LanguageRestoreTimeout = TimeSpan.FromSeconds(25);
        internal static readonly TimeSpan ForegroundReconciliationDelay =
            TimeSpan.FromMilliseconds(75);

        private readonly GameHelperSettings settings;
        private readonly LowLevelKeyboardProc keyboardProc;
        private readonly GameInputLanguageNative.WinEventProc foregroundEventProc;
        private readonly GameHelperElevationClient elevationClient = new GameHelperElevationClient();
        private readonly PointerPrecisionGuard pointerPrecisionGuard;
        private readonly GameKeyboardProtectionState keyboardProtectionState =
            new GameKeyboardProtectionState();
        private readonly List<PendingLanguageRestore> pendingLanguageRestores = new List<PendingLanguageRestore>();
        private readonly GameInputLanguageOriginalCache languageOriginals =
            new GameInputLanguageOriginalCache();
        private readonly HashSet<string> pendingElevatedRequests = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> failedElevatedRequests = new HashSet<string>(StringComparer.Ordinal);
        private readonly object keyboardHookSync = new object();
        private List<GameHelperAppCandidate> installedAppCache;
        private IntPtr keyboardHook;
        private Thread keyboardHookThread;
        private uint keyboardHookThreadId;
        private readonly Dictionary<uint, IntPtr> foregroundEventHooks =
            new Dictionary<uint, IntPtr>();
        private DispatcherTimer foregroundReconciliationTimer;
        private bool foregroundImmediateReconciliationQueued;
        private DispatcherTimer languageWatchdog;
        private ActiveLanguageSession activeLanguageSession;
        private PendingLanguageRestore pendingForegroundLanguageRestore;
        private IntPtr currentForegroundHwnd;
        private bool handlingForegroundChange;
        private IntPtr queuedForegroundHwnd;
        private bool disposed;
        private CrosshairOverlayWindow crosshairWindow;
        private int crosshairPreviewCount;
        private CrosshairAppSelectionIndex crosshairAppIndex;
        private IntPtr crosshairTargetHwnd;
        private bool foregroundExecutableMatched;
        private volatile ForegroundProtectionSnapshot foregroundProtection =
            ForegroundProtectionSnapshot.Empty;
        private bool keyboardProtectionConfigured;
        private bool languageProtectionConfigured;
        private string lastStatus = "Game Helper ready.";

        public event EventHandler StatusChanged;

        public GameHelperModule(GameHelperSettings settings)
        {
            this.settings = settings;
            keyboardProc = KeyboardHookCallback;
            foregroundEventProc = ForegroundEventCallback;
            crosshairAppIndex = new CrosshairAppSelectionIndex(
                CrosshairAppSelectionCodec.Decode(settings.CrosshairSelectedApps));
            pointerPrecisionGuard = new PointerPrecisionGuard(
                new WindowsPointerPrecisionService(), SetStatus);
        }

        public string StatusText
        {
            get { return lastStatus; }
        }

        internal bool IsKeyboardProtectionHookActive
        {
            get
            {
                lock (keyboardHookSync)
                {
                    return keyboardHook != IntPtr.Zero;
                }
            }
        }

        internal bool IsKeyboardProtectionHookThreadAlive
        {
            get
            {
                lock (keyboardHookSync)
                {
                    return keyboardHookThread != null && keyboardHookThread.IsAlive
                        && keyboardHookThreadId != 0;
                }
            }
        }

        internal bool IsInputLanguageWatchdogActive
        {
            get { return languageWatchdog != null && languageWatchdog.IsEnabled; }
        }

        internal bool IsForegroundMonitoringActive
        {
            get { return foregroundEventHooks.Count > 0; }
        }

        internal bool IsForegroundEventHookActive
        {
            get { return foregroundEventHooks.ContainsKey(
                GameInputLanguageNative.EventSystemForeground); }
        }

        internal int ForegroundEventHookCount
        {
            get { return foregroundEventHooks.Count; }
        }

        internal bool IsForegroundReconciliationPending
        {
            get
            {
                return foregroundImmediateReconciliationQueued
                    || (foregroundReconciliationTimer != null
                        && foregroundReconciliationTimer.IsEnabled);
            }
        }

        internal bool IsCrosshairOverlayCreated
        {
            get { return crosshairWindow != null; }
        }

        internal bool IsCrosshairOverlayVisible
        {
            get { return crosshairWindow != null && crosshairWindow.IsVisible; }
        }

        internal int PendingInputLanguageRestoreCount
        {
            get
            {
                return pendingLanguageRestores.Count
                    + (pendingForegroundLanguageRestore == null ? 0 : 1);
            }
        }

        internal IntPtr ForegroundProtectionHwnd
        {
            get { return foregroundProtection.Hwnd; }
        }

        internal IntPtr CrosshairTargetHwnd
        {
            get { return crosshairTargetHwnd; }
        }

        internal Point CrosshairOverlayCenterInDevicePixels
        {
            get
            {
                return crosshairWindow == null
                    ? new Point(double.NaN, double.NaN)
                    : crosshairWindow.GetCenterInDevicePixels();
            }
        }

        internal bool IsKeyboardProtectionStateClean
        {
            get { return keyboardProtectionState.IsClean; }
        }

        public void ApplySavedState()
        {
            EnsureHookState();
            EnsureCrosshairState();
            pointerPrecisionGuard.SetEnabled(settings.KeepEnhancedPointerPrecisionOff);
            HandleForegroundChanged(GameInputLanguageNative.GetCurrentForegroundWindow());
        }

        public bool IsPointerPrecisionGuardEnabled()
        {
            return settings.KeepEnhancedPointerPrecisionOff;
        }

        internal bool IsPointerPrecisionGuardMonitoring
        {
            get { return pointerPrecisionGuard.IsMonitoring; }
        }

        public void SetPointerPrecisionGuardEnabled(bool enabled)
        {
            settings.KeepEnhancedPointerPrecisionOff = enabled;
            pointerPrecisionGuard.SetEnabled(enabled);
            if (!enabled)
            {
                SetStatus("Pointer acceleration guard disabled.");
            }
        }

        public bool IsCrosshairEnabled()
        {
            return settings.CrosshairEnabled;
        }

        public CrosshairOptions GetCrosshairOptions()
        {
            return CreateCrosshairOptions();
        }

        public void SetCrosshairEnabled(bool enabled)
        {
            settings.CrosshairEnabled = enabled;
            EnsureHookState();
            RefreshForegroundState();
            EnsureCrosshairState();
            SetStatus(GetCrosshairStatusText());
        }

        public bool IsCrosshairRestrictedToSelectedApps()
        {
            return settings.CrosshairRestrictToSelectedApps;
        }

        public List<CrosshairSelectedApp> GetCrosshairSelectedApps()
        {
            return CrosshairAppSelectionCodec.Decode(settings.CrosshairSelectedApps);
        }

        public void SetCrosshairRestrictToSelectedApps(bool restrict)
        {
            settings.CrosshairRestrictToSelectedApps = restrict;
            RebuildCrosshairAppIndex();
            EnsureHookState();
            RefreshForegroundState();
            EnsureCrosshairState();
            SetStatus(GetCrosshairStatusText());
        }

        public void AddCrosshairSelectedApp(GameHelperAppCandidate candidate)
        {
            if (candidate == null)
            {
                return;
            }

            string targetPath = CrosshairAppSelectionCodec.NormalizeExecutablePath(candidate.TargetPath);
            string id = CrosshairAppSelectionCodec.CreateExecutableId(targetPath);
            if (string.IsNullOrWhiteSpace(id))
            {
                SetStatus("The selected app does not have a valid executable path.");
                return;
            }

            List<CrosshairSelectedApp> apps = GetCrosshairSelectedApps();
            for (int i = 0; i < apps.Count; i++)
            {
                if (string.Equals(apps[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(candidate.Name + " is already selected for Screen crosshair.");
                    return;
                }
            }

            apps.Add(new CrosshairSelectedApp
            {
                Id = id,
                Name = candidate.Name,
                TargetPath = targetPath,
                Publisher = candidate.Publisher,
                Source = candidate.Source,
                IconPath = candidate.IconPath
            });
            settings.CrosshairSelectedApps = CrosshairAppSelectionCodec.Encode(apps);
            RebuildCrosshairAppIndex();
            RefreshForegroundState();
            EnsureCrosshairState();
            SetStatus(candidate.Name + " selected for Screen crosshair.");
        }

        public void RemoveCrosshairSelectedApp(string id)
        {
            List<CrosshairSelectedApp> apps = GetCrosshairSelectedApps();
            int removed = apps.RemoveAll(delegate(CrosshairSelectedApp app)
            {
                return string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase);
            });
            if (removed == 0)
            {
                return;
            }

            settings.CrosshairSelectedApps = CrosshairAppSelectionCodec.Encode(apps);
            RebuildCrosshairAppIndex();
            RefreshForegroundState();
            EnsureCrosshairState();
            SetStatus(GetCrosshairStatusText());
        }

        public void ApplyCrosshairOptions(CrosshairOptions options)
        {
            if (options == null)
            {
                return;
            }

            settings.CrosshairSize = Clamp(options.Size, CrosshairMinSize, CrosshairMaxSize);
            settings.CrosshairOpacityPercent = Clamp(options.OpacityPercent, 10, 100);
            settings.CrosshairColor = NormalizeColorHex(options.ColorHex);
            settings.CrosshairCustomColor = NormalizeColorHex(options.CustomColorHex, "#FFFFFF");
            settings.CrosshairStyle = NormalizeCrosshairStyle(options.Style);

            CrosshairOverlayWindow window = crosshairWindow;
            if (window != null)
            {
                window.ApplyOptions(CreateCrosshairOptions(), crosshairTargetHwnd);
            }

            SetStatus("Screen crosshair updated.");
        }

        public IDisposable PreviewCrosshairOverlay()
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                return (IDisposable)Application.Current.Dispatcher.Invoke(
                    new Func<IDisposable>(PreviewCrosshairOverlay));
            }

            crosshairPreviewCount++;
            EnsureCrosshairState();
            return new ActionDisposable(delegate
            {
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(ReleaseCrosshairOverlayPreview));
                    return;
                }

                ReleaseCrosshairOverlayPreview();
            });
        }

        public List<GameHelperProtectedApp> GetProtectedApps()
        {
            return DecodeProtectedApps(settings.ProtectedApps);
        }

        public List<GameHelperAppCandidate> GetInstalledAppCandidates()
        {
            if (installedAppCache == null)
            {
                installedAppCache = EnumerateInstalledApps();
            }

            return new List<GameHelperAppCandidate>(installedAppCache);
        }

        public void AddProtectedApp(GameHelperAppCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.TargetPath))
            {
                return;
            }

            List<GameHelperProtectedApp> apps = DecodeProtectedApps(settings.ProtectedApps);
            string id = CreateAppId(candidate.TargetPath);
            for (int i = 0; i < apps.Count; i++)
            {
                if (string.Equals(apps[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(candidate.Name + " is already protected.");
                    return;
                }
            }

            apps.Add(new GameHelperProtectedApp
            {
                Id = id,
                Name = candidate.Name,
                TargetPath = candidate.TargetPath,
                Publisher = candidate.Publisher,
                ProcessName = GetProcessNameFromPath(candidate.TargetPath),
                BlockWindowsKey = true,
                RightControlDShowsDesktop = true,
                LockMicrosoftEnglish = true,
                BlockInputLanguageSwitch = true,
                RestorePreviousInputLanguage = true
            });

            settings.ProtectedApps = EncodeProtectedApps(apps);
            EnsureHookState();
            SetStatus("Game protection added for " + candidate.Name + ".");
            RefreshForegroundState();
        }

        public void RemoveProtectedApp(string id)
        {
            List<GameHelperProtectedApp> apps = DecodeProtectedApps(settings.ProtectedApps);
            int removed = apps.RemoveAll(delegate(GameHelperProtectedApp app)
            {
                return string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase);
            });

            if (removed > 0)
            {
                settings.ProtectedApps = EncodeProtectedApps(apps);
                EnsureHookState();
                SetStatus("Game protection removed for " + removed + " app" + (removed == 1 ? "." : "s."));
                RefreshForegroundState();
            }
        }

        public void UpdateProtectedAppOptions(string id, bool blockWindowsKey,
            bool rightControlDShowsDesktop, bool lockMicrosoftEnglish,
            bool blockInputLanguageSwitch, bool restorePreviousInputLanguage)
        {
            List<GameHelperProtectedApp> apps = DecodeProtectedApps(settings.ProtectedApps);
            for (int i = 0; i < apps.Count; i++)
            {
                GameHelperProtectedApp app = apps[i];
                if (!string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                app.BlockWindowsKey = blockWindowsKey;
                app.RightControlDShowsDesktop = rightControlDShowsDesktop;
                app.LockMicrosoftEnglish = lockMicrosoftEnglish;
                app.BlockInputLanguageSwitch = blockInputLanguageSwitch;
                app.RestorePreviousInputLanguage = restorePreviousInputLanguage;
                settings.ProtectedApps = EncodeProtectedApps(apps);
                elevationClient.ResetCancellation();
                failedElevatedRequests.Clear();
                EnsureHookState();
                RefreshForegroundState();
                SetStatus("Game protection settings updated for " + app.Name + ".");
                return;
            }
        }

        public void RefreshInstalledAppCache()
        {
            installedAppCache = null;
            SetStatus("Installed app list refreshed.");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopInputLanguageMonitoring(true);
            DisableWinKeyBlocker();
            CloseCrosshairOverlay();
            pointerPrecisionGuard.Dispose();
            elevationClient.Dispose();
        }

        private void EnsureHookState()
        {
            List<GameHelperProtectedApp> apps = DecodeProtectedApps(settings.ProtectedApps);
            keyboardProtectionConfigured = false;
            languageProtectionConfigured = false;
            for (int i = 0; i < apps.Count; i++)
            {
                keyboardProtectionConfigured |= apps[i].BlockWindowsKey
                    || apps[i].RightControlDShowsDesktop;
                languageProtectionConfigured |= apps[i].LockMicrosoftEnglish;
            }

            if (keyboardProtectionConfigured) EnableWinKeyBlocker(apps.Count);
            else DisableWinKeyBlocker();

            if (!languageProtectionConfigured) StopLanguageProtection(false);
            bool gameProtectionNeedsForeground =
                keyboardProtectionConfigured || languageProtectionConfigured;
            bool crosshairNeedsForeground = settings.CrosshairEnabled
                && settings.CrosshairRestrictToSelectedApps;
            bool shouldMonitorForeground =
                gameProtectionNeedsForeground || crosshairNeedsForeground;
            if (shouldMonitorForeground)
            {
                EnableForegroundMonitoring(languageProtectionConfigured);
                return;
            }

            DisableForegroundMonitoring();
            foregroundProtection = ForegroundProtectionSnapshot.Empty;
            foregroundExecutableMatched = false;
            SetStatus(apps.Count == 0
                ? "Add protected apps to configure game protection."
                : "All game protection options are disabled.");
        }

        private void EnableForegroundMonitoring(bool enableLanguageWatchdog)
        {
            EnsureForegroundEventHook(GameInputLanguageNative.EventSystemForeground,
                "EVENT_SYSTEM_FOREGROUND");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectFocus,
                "EVENT_OBJECT_FOCUS");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectCreate,
                "EVENT_OBJECT_CREATE");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectDestroy,
                "EVENT_OBJECT_DESTROY");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectShow,
                "EVENT_OBJECT_SHOW");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectHide,
                "EVENT_OBJECT_HIDE");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectLocationChange,
                "EVENT_OBJECT_LOCATIONCHANGE");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectCloaked,
                "EVENT_OBJECT_CLOAKED");
            EnsureForegroundEventHook(GameInputLanguageNative.EventObjectUncloaked,
                "EVENT_OBJECT_UNCLOAKED");
            EnsureForegroundEventHook(GameInputLanguageNative.EventSystemMoveSizeEnd,
                "EVENT_SYSTEM_MOVESIZEEND");
            EnsureForegroundEventHook(GameInputLanguageNative.EventSystemMinimizeStart,
                "EVENT_SYSTEM_MINIMIZESTART");
            EnsureForegroundEventHook(GameInputLanguageNative.EventSystemMinimizeEnd,
                "EVENT_SYSTEM_MINIMIZEEND");
            EnsureForegroundEventHook(GameInputLanguageNative.EventSystemDesktopSwitch,
                "EVENT_SYSTEM_DESKTOPSWITCH");

            if (enableLanguageWatchdog && languageWatchdog == null)
            {
                languageWatchdog = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                languageWatchdog.Tick += LanguageWatchdog_Tick;
                languageWatchdog.Start();
            }
        }

        private void EnsureForegroundEventHook(uint eventType, string eventName)
        {
            if (foregroundEventHooks.ContainsKey(eventType)) return;
            IntPtr hook = GameInputLanguageNative.InstallEventHook(eventType, foregroundEventProc);
            if (hook == IntPtr.Zero)
            {
                AppLog.Write("Game Helper", "SetWinEventHook(" + eventName + ") failed: "
                    + Marshal.GetLastWin32Error() + ".");
                if (eventType == GameInputLanguageNative.EventSystemForeground)
                    foregroundExecutableMatched = false;
                return;
            }
            foregroundEventHooks[eventType] = hook;
        }

        private void StopInputLanguageMonitoring(bool waitForElevatedRestore)
        {
            StopLanguageProtection(waitForElevatedRestore);
            DisableForegroundMonitoring();
            foregroundProtection = ForegroundProtectionSnapshot.Empty;
        }

        private void StopLanguageProtection(bool waitForElevatedRestore)
        {
            EndActiveLanguageSession(waitForElevatedRestore);
            for (int i = pendingLanguageRestores.Count - 1; i >= 0; i--)
            {
                PendingLanguageRestore restore = pendingLanguageRestores[i];
                _ = ApplyLayoutRequest(restore.ThreadId, restore.FallbackHwnd, restore.Layout,
                    restore.ExpectedPath, restore.NextCandidateIndex, waitForElevatedRestore,
                    out restore.NextCandidateIndex);
            }
            pendingLanguageRestores.Clear();
            pendingForegroundLanguageRestore = null;
            languageOriginals.Clear();

            if (languageWatchdog != null)
            {
                languageWatchdog.Stop();
                languageWatchdog.Tick -= LanguageWatchdog_Tick;
                languageWatchdog = null;
            }

        }

        private void DisableForegroundMonitoring()
        {
            foreach (KeyValuePair<uint, IntPtr> hook in foregroundEventHooks)
            {
                GameInputLanguageNative.RemoveForegroundHook(hook.Value);
            }
            foregroundEventHooks.Clear();
            StopForegroundReconciliation();
            currentForegroundHwnd = IntPtr.Zero;
            crosshairTargetHwnd = IntPtr.Zero;
            foregroundExecutableMatched = false;
        }

        private void RefreshForegroundState()
        {
            if (disposed)
            {
                return;
            }

            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(RefreshForegroundState));
                return;
            }

            HandleForegroundChanged(GameInputLanguageNative.GetCurrentForegroundWindow());
        }

        private void ForegroundEventCallback(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint eventThread, uint eventTime)
        {
            if (disposed || !ShouldReconcileForegroundEvent(eventType, hwnd,
                    idObject, idChild, eventThread))
            {
                return;
            }

            QueueForegroundReconciliation(
                eventType == GameInputLanguageNative.EventSystemForeground);
        }

        private bool ShouldReconcileForegroundEvent(uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint eventThread)
        {
            if (eventType == GameInputLanguageNative.EventSystemForeground
                || eventType == GameInputLanguageNative.EventSystemDesktopSwitch)
            {
                return true;
            }

            bool objectEvent = eventType >= GameInputLanguageNative.EventObjectCreate;
            if (objectEvent && eventType != GameInputLanguageNative.EventObjectFocus
                && (idObject != GameInputLanguageNative.ObjIdWindow
                    || idChild != GameInputLanguageNative.ChildIdSelf))
            {
                return false;
            }
            if (hwnd == IntPtr.Zero) return false;
            if (IsCrosshairOverlayWindow(hwnd)) return false;
            if (hwnd == currentForegroundHwnd || hwnd == crosshairTargetHwnd
                || (foregroundProtection != null && hwnd == foregroundProtection.Hwnd))
            {
                return true;
            }

            IntPtr foreground = GameInputLanguageNative.GetCurrentForegroundWindow();
            uint foregroundThreadId;
            uint foregroundProcessId;
            if (!GameInputLanguageNative.TryGetWindowProcess(foreground,
                    out foregroundThreadId, out foregroundProcessId))
            {
                return false;
            }
            if (eventThread != 0 && eventThread == foregroundThreadId) return true;

            uint eventThreadId;
            uint eventProcessId;
            if (!GameInputLanguageNative.TryGetWindowProcess(hwnd,
                    out eventThreadId, out eventProcessId))
            {
                return false;
            }
            if (eventProcessId == (uint)Environment.ProcessId) return false;
            return eventProcessId == foregroundProcessId;
        }

        private bool IsCrosshairOverlayWindow(IntPtr hwnd)
        {
            CrosshairOverlayWindow overlay = crosshairWindow;
            return overlay != null && hwnd != IntPtr.Zero
                && new WindowInteropHelper(overlay).Handle == hwnd;
        }

        private void QueueForegroundReconciliation(bool immediate)
        {
            if (disposed) return;
            Dispatcher dispatcher = Application.Current == null
                ? Dispatcher.CurrentDispatcher : Application.Current.Dispatcher;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send,
                    new Action(delegate { QueueForegroundReconciliation(immediate); }));
                return;
            }

            if (immediate && !foregroundImmediateReconciliationQueued)
            {
                foregroundImmediateReconciliationQueued = true;
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate
                {
                    foregroundImmediateReconciliationQueued = false;
                    RefreshForegroundState();
                }));
            }

            if (foregroundReconciliationTimer == null)
            {
                foregroundReconciliationTimer = new DispatcherTimer(
                    DispatcherPriority.Background, dispatcher)
                {
                    Interval = ForegroundReconciliationDelay
                };
                foregroundReconciliationTimer.Tick += ForegroundReconciliationTimer_Tick;
            }
            foregroundReconciliationTimer.Stop();
            foregroundReconciliationTimer.Start();
        }

        private void ForegroundReconciliationTimer_Tick(object sender, EventArgs e)
        {
            if (foregroundReconciliationTimer != null)
                foregroundReconciliationTimer.Stop();
            if (!disposed) RefreshForegroundState();
        }

        private void StopForegroundReconciliation()
        {
            foregroundImmediateReconciliationQueued = false;
            if (foregroundReconciliationTimer != null)
            {
                foregroundReconciliationTimer.Stop();
                foregroundReconciliationTimer.Tick -= ForegroundReconciliationTimer_Tick;
                foregroundReconciliationTimer = null;
            }
        }

        private void HandleForegroundChanged(IntPtr hwnd)
        {
            if (disposed)
            {
                return;
            }

            if (handlingForegroundChange)
            {
                queuedForegroundHwnd = hwnd;
                return;
            }

            handlingForegroundChange = true;
            try
            {
                IntPtr nextHwnd = hwnd;
                do
                {
                    queuedForegroundHwnd = IntPtr.Zero;
                    ProcessForegroundChange(nextHwnd);
                    nextHwnd = queuedForegroundHwnd;
                }
                while (nextHwnd != IntPtr.Zero && !disposed);
            }
            finally
            {
                handlingForegroundChange = false;
            }
        }

        private void ProcessForegroundChange(IntPtr hwnd)
        {
            uint resolvedThreadId;
            uint resolvedProcessId;
            if (!GameInputLanguageNative.TryGetWindowProcess(hwnd, out resolvedThreadId,
                out resolvedProcessId))
            {
                return;
            }

            IntPtr returnLayout = IntPtr.Zero;
            if (activeLanguageSession != null)
            {
                returnLayout = activeLanguageSession.ReturnLayout;
            }
            else
            {
                uint previousThreadId;
                uint previousProcessId;
                if (currentForegroundHwnd != hwnd
                    && GameInputLanguageNative.TryGetWindowProcess(currentForegroundHwnd,
                        out previousThreadId, out previousProcessId))
                {
                    returnLayout = GameInputLanguageNative.GetThreadLayout(previousThreadId);
                }
            }

            currentForegroundHwnd = hwnd;
            uint threadId;
            uint processId;
            string foregroundPath;
            GameHelperProtectedApp foregroundApp = FindProtectedAppForWindow(hwnd,
                out threadId, out processId, out foregroundPath);
            UpdateCrosshairForegroundMatch(foregroundPath, hwnd);
            foregroundProtection = foregroundApp == null
                ? ForegroundProtectionSnapshot.Empty
                : new ForegroundProtectionSnapshot(foregroundApp.Id, foregroundApp.Name, hwnd,
                    foregroundApp.BlockWindowsKey, foregroundApp.RightControlDShowsDesktop);
            GameHelperProtectedApp matchedApp = foregroundApp != null
                && foregroundApp.LockMicrosoftEnglish ? foregroundApp : null;

            if (activeLanguageSession != null)
            {
                if (matchedApp != null
                    && string.Equals(activeLanguageSession.AppId, matchedApp.Id,
                        StringComparison.OrdinalIgnoreCase)
                    && activeLanguageSession.ThreadId == threadId)
                {
                    activeLanguageSession.FallbackHwnd = hwnd;
                    activeLanguageSession.BlockInputLanguageSwitch = matchedApp.BlockInputLanguageSwitch;
                    activeLanguageSession.RestorePreviousInputLanguage = matchedApp.RestorePreviousInputLanguage;
                    EnsureActiveLanguageLayout(true);
                    return;
                }

                if (threadId == 0)
                {
                    return;
                }

                EndActiveLanguageSession(false, matchedApp == null);
            }

            if (pendingForegroundLanguageRestore != null)
            {
                if (matchedApp != null)
                {
                    pendingForegroundLanguageRestore = null;
                }
                else if (threadId != 0)
                {
                    UpdateForegroundLanguageRestore(threadId, hwnd, foregroundPath);
                }
            }

            if (matchedApp == null || threadId == 0)
            {
                return;
            }

            IntPtr originalLayout;
            bool rememberedOriginal = TryGetRememberedOriginalLayout(threadId,
                matchedApp.TargetPath, out originalLayout);
            if (!rememberedOriginal)
            {
                originalLayout = returnLayout != IntPtr.Zero
                    ? returnLayout
                    : GameInputLanguageNative.GetThreadLayout(threadId);
            }
            if (originalLayout == IntPtr.Zero)
            {
                AppLog.Write("Game Helper", "Could not capture the game thread input language for "
                    + matchedApp.Name + ".");
                SetStatus("Could not capture the input language for " + matchedApp.Name + ".");
                return;
            }

            activeLanguageSession = new ActiveLanguageSession
            {
                AppId = matchedApp.Id,
                AppName = matchedApp.Name,
                ExpectedPath = matchedApp.TargetPath,
                ThreadId = threadId,
                FallbackHwnd = hwnd,
                OriginalLayout = originalLayout,
                ReturnLayout = returnLayout,
                RestorePreviousInputLanguage = matchedApp.RestorePreviousInputLanguage,
                BlockInputLanguageSwitch = matchedApp.BlockInputLanguageSwitch,
                NextCandidateIndex = 0
            };
            AppLog.Write("Game Helper", "Input-language session started for " + matchedApp.Name
                + ": thread=" + threadId
                + " original=" + FormatLayout(originalLayout)
                + " return=" + FormatLayout(returnLayout) + ".");
            EnsureActiveLanguageLayout(true);
            SetStatus("Microsoft ENG locked for " + matchedApp.Name + ".");
        }

        private GameHelperProtectedApp FindProtectedAppForWindow(IntPtr hwnd, out uint threadId,
            out uint processId, out string foregroundPath)
        {
            threadId = 0;
            processId = 0;
            foregroundPath = string.Empty;
            if (!GameInputLanguageNative.TryGetWindowProcess(hwnd, out threadId, out processId))
            {
                return null;
            }

            string foregroundProcessName = string.Empty;
            int pathError;
            GameInputLanguageNative.TryGetProcessPath(processId, out foregroundPath, out pathError);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    foregroundProcessName = process.ProcessName;
                }
            }
            catch
            {
            }

            string foregroundId = CreateAppId(foregroundPath);
            List<GameHelperProtectedApp> apps = DecodeProtectedApps(settings.ProtectedApps);
            if (!string.IsNullOrWhiteSpace(foregroundId))
            {
                for (int i = 0; i < apps.Count; i++)
                {
                    if (string.Equals(apps[i].Id, foregroundId, StringComparison.OrdinalIgnoreCase))
                    {
                        return apps[i];
                    }
                }
                return null;
            }

            for (int i = 0; i < apps.Count; i++)
            {
                GameHelperProtectedApp app = apps[i];
                if (!string.IsNullOrWhiteSpace(foregroundProcessName)
                    && !string.IsNullOrWhiteSpace(app.ProcessName)
                    && string.Equals(app.ProcessName, foregroundProcessName,
                        StringComparison.OrdinalIgnoreCase)) return app;
            }
            return null;
        }

        private bool TryGetRememberedOriginalLayout(uint threadId, string expectedPath,
            out IntPtr originalLayout)
        {
            originalLayout = IntPtr.Zero;
            for (int i = pendingLanguageRestores.Count - 1; i >= 0; i--)
            {
                PendingLanguageRestore restore = pendingLanguageRestores[i];
                if (restore.ThreadId != threadId
                    || !string.Equals(restore.ExpectedPath, expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                originalLayout = restore.Layout;
                pendingLanguageRestores.RemoveAt(i);
                return originalLayout != IntPtr.Zero;
            }

            return languageOriginals.TryGet(threadId, expectedPath, out originalLayout);
        }

        private void EnsureActiveLanguageLayout(bool force)
        {
            ActiveLanguageSession session = activeLanguageSession;
            if (session == null || (!force && !session.BlockInputLanguageSwitch))
            {
                return;
            }

            IntPtr current = GameInputLanguageNative.GetThreadLayout(session.ThreadId);
            if (GameInputLanguageNative.LayoutsEqual(current,
                GameInputLanguageNative.MicrosoftEnglishUsLayout))
            {
                session.NextCandidateIndex = 0;
                return;
            }

            int nextIndex;
            _ = ApplyLayoutRequest(session.ThreadId, session.FallbackHwnd,
                GameInputLanguageNative.MicrosoftEnglishUsLayout, session.ExpectedPath,
                session.NextCandidateIndex, false, out nextIndex);
            session.NextCandidateIndex = nextIndex;
        }

        private void EndActiveLanguageSession(bool waitForElevatedRestore)
        {
            EndActiveLanguageSession(waitForElevatedRestore, false);
        }

        private void EndActiveLanguageSession(bool waitForElevatedRestore,
            bool followNonGameForeground)
        {
            ActiveLanguageSession session = activeLanguageSession;
            activeLanguageSession = null;
            if (session == null || !session.RestorePreviousInputLanguage)
            {
                return;
            }

            languageOriginals.Remember(session.ThreadId, session.ExpectedPath,
                session.OriginalLayout);
            AppLog.Write("Game Helper", "Input-language session ended for " + session.AppName
                + ": thread=" + session.ThreadId
                + " restore-game=" + FormatLayout(session.OriginalLayout)
                + " restore-foreground=" + FormatLayout(session.ReturnLayout) + ".");

            QueueLanguageRestore(new PendingLanguageRestore
            {
                ThreadId = session.ThreadId,
                FallbackHwnd = session.FallbackHwnd,
                Layout = session.OriginalLayout,
                ExpectedPath = session.ExpectedPath,
                AppName = session.AppName,
                DeadlineUtc = DateTime.UtcNow.Add(LanguageRestoreTimeout),
                NextCandidateIndex = 0
            }, waitForElevatedRestore);

            if (followNonGameForeground && !waitForElevatedRestore
                && session.ReturnLayout != IntPtr.Zero)
            {
                pendingForegroundLanguageRestore = new PendingLanguageRestore
                {
                    Layout = session.ReturnLayout,
                    AppName = session.AppName,
                    SourceThreadId = session.ThreadId,
                    SourceExpectedPath = session.ExpectedPath,
                    SourceLayout = session.OriginalLayout,
                    DeadlineUtc = DateTime.UtcNow.Add(LanguageRestoreTimeout),
                    NextCandidateIndex = 0
                };
            }
        }

        private void UpdateForegroundLanguageRestore(uint threadId, IntPtr hwnd,
            string expectedPath)
        {
            PendingLanguageRestore restore = pendingForegroundLanguageRestore;
            if (restore == null || threadId == 0 || hwnd == IntPtr.Zero)
            {
                return;
            }

            bool targetChanged = restore.ThreadId != threadId
                || !string.Equals(restore.ExpectedPath, expectedPath,
                    StringComparison.OrdinalIgnoreCase);
            if (targetChanged)
            {
                restore.ThreadId = threadId;
                restore.ExpectedPath = expectedPath;
                restore.NextCandidateIndex = 0;
                restore.TargetSinceUtc = DateTime.UtcNow;
            }
            restore.FallbackHwnd = hwnd;

            if (GameInputLanguageNative.LayoutsEqual(
                GameInputLanguageNative.GetThreadLayout(threadId), restore.Layout))
            {
                return;
            }

            int nextIndex;
            int error = ApplyLayoutRequest(threadId, hwnd, restore.Layout, restore.ExpectedPath,
                restore.NextCandidateIndex, false, out nextIndex);
            restore.NextCandidateIndex = nextIndex;
            if (error == 1400)
            {
                restore.ThreadId = 0;
                restore.FallbackHwnd = IntPtr.Zero;
                restore.ExpectedPath = string.Empty;
                restore.NextCandidateIndex = 0;
            }
        }

        private void QueueLanguageRestore(PendingLanguageRestore restore,
            bool waitForElevatedRestore)
        {
            IntPtr current = GameInputLanguageNative.GetThreadLayout(restore.ThreadId);
            if (GameInputLanguageNative.LayoutsEqual(current, restore.Layout))
            {
                return;
            }

            int nextIndex;
            int error = ApplyLayoutRequest(restore.ThreadId, restore.FallbackHwnd, restore.Layout,
                restore.ExpectedPath, restore.NextCandidateIndex, waitForElevatedRestore, out nextIndex);
            restore.NextCandidateIndex = nextIndex;
            if (!waitForElevatedRestore && error != 1400)
            {
                pendingLanguageRestores.Add(restore);
            }
        }

        private void LanguageWatchdog_Tick(object sender, EventArgs e)
        {
            if (disposed)
            {
                return;
            }

            if (currentForegroundHwnd != IntPtr.Zero)
            {
                EnsureActiveLanguageLayout(false);
            }

            ProcessPendingForegroundLanguageRestore(currentForegroundHwnd);

            for (int i = pendingLanguageRestores.Count - 1; i >= 0; i--)
            {
                PendingLanguageRestore restore = pendingLanguageRestores[i];
                IntPtr current = GameInputLanguageNative.GetThreadLayout(restore.ThreadId);
                if (GameInputLanguageNative.LayoutsEqual(current, restore.Layout))
                {
                    if (restore.TargetSinceUtc == default(DateTime))
                    {
                        restore.TargetSinceUtc = DateTime.UtcNow;
                        continue;
                    }
                    if (DateTime.UtcNow - restore.TargetSinceUtc
                        < TimeSpan.FromMilliseconds(ForegroundRestoreStableMilliseconds))
                    {
                        continue;
                    }

                    PendingLanguageRestore foregroundRestore = pendingForegroundLanguageRestore;
                    if (foregroundRestore == null
                        || foregroundRestore.SourceThreadId != restore.ThreadId
                        || !string.Equals(foregroundRestore.SourceExpectedPath,
                            restore.ExpectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        languageOriginals.Forget(restore.ThreadId, restore.ExpectedPath,
                            restore.Layout);
                    }
                    pendingLanguageRestores.RemoveAt(i);
                    continue;
                }

                restore.TargetSinceUtc = default(DateTime);
                if (DateTime.UtcNow >= restore.DeadlineUtc)
                {
                    AppLog.Write("Game Helper", "Timed out restoring the original input language for "
                        + restore.AppName + ".");
                    pendingLanguageRestores.RemoveAt(i);
                    continue;
                }

                int nextIndex;
                int error = ApplyLayoutRequest(restore.ThreadId, restore.FallbackHwnd, restore.Layout,
                    restore.ExpectedPath, restore.NextCandidateIndex, false, out nextIndex);
                restore.NextCandidateIndex = nextIndex;
                if (error == 1400)
                {
                    pendingLanguageRestores.RemoveAt(i);
                }
            }
        }

        private void ProcessPendingForegroundLanguageRestore(IntPtr foreground)
        {
            PendingLanguageRestore restore = pendingForegroundLanguageRestore;
            if (restore == null)
            {
                return;
            }

            if (DateTime.UtcNow >= restore.DeadlineUtc)
            {
                AppLog.Write("Game Helper", "Timed out restoring the original input language after leaving "
                    + restore.AppName + ".");
                pendingForegroundLanguageRestore = null;
                return;
            }

            uint foregroundThreadId;
            uint foregroundProcessId;
            if (!GameInputLanguageNative.TryGetWindowProcess(foreground,
                    out foregroundThreadId, out foregroundProcessId)
                || foregroundThreadId != restore.ThreadId || restore.ThreadId == 0)
            {
                return;
            }

            IntPtr current = GameInputLanguageNative.GetThreadLayout(restore.ThreadId);
            if (GameInputLanguageNative.LayoutsEqual(current, restore.Layout))
            {
                if (DateTime.UtcNow - restore.TargetSinceUtc
                    >= TimeSpan.FromMilliseconds(ForegroundRestoreStableMilliseconds))
                {
                    languageOriginals.Forget(restore.SourceThreadId,
                        restore.SourceExpectedPath, restore.SourceLayout);
                    pendingForegroundLanguageRestore = null;
                }
                return;
            }

            UpdateForegroundLanguageRestore(restore.ThreadId, restore.FallbackHwnd,
                restore.ExpectedPath);
        }

        private int ApplyLayoutRequest(uint threadId, IntPtr fallbackHwnd, IntPtr layout,
            string expectedPath, int preferredCandidateIndex, bool waitForElevated,
            out int nextCandidateIndex)
        {
            int usedIndex;
            int error;
            bool posted = GameInputLanguageNative.TryPostLayout(threadId, fallbackHwnd, layout,
                preferredCandidateIndex, out usedIndex, out error);
            nextCandidateIndex = usedIndex < 0 ? 0 : usedIndex + 1;
            if (posted)
            {
                return 0;
            }

            if (error == 1400)
            {
                return error;
            }
            if (error != GameInputLanguageNative.ErrorAccessDenied)
            {
                AppLog.Write("Game Helper", "WM_INPUTLANGCHANGEREQUEST failed for thread "
                    + threadId + " with Win32 error " + error + ".");
                return error;
            }

            if (string.IsNullOrWhiteSpace(expectedPath))
            {
                AppLog.Write("Game Helper", "Could not request elevated input-language access for thread "
                    + threadId + " because its executable path was unavailable.");
                return error;
            }

            var request = new GameHelperElevationRequest
            {
                ThreadId = threadId,
                FallbackHwnd = fallbackHwnd.ToInt64(),
                Layout = layout.ToInt64(),
                ExpectedPath = expectedPath,
                PreferredCandidateIndex = preferredCandidateIndex
            };

            if (waitForElevated)
            {
                if (!elevationClient.IsConnected)
                {
                    AppLog.Write("Game Helper",
                        "Could not restore an elevated game because no approved helper was connected.");
                    return error;
                }

                try
                {
                    GameHelperElevationResponse response = elevationClient.PostAsync(request, false,
                        CancellationToken.None).GetAwaiter().GetResult();
                    if (!response.Success)
                    {
                        AppLog.Write("Game Helper", "Elevated restore failed: " + response.Message);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("Game Helper", "Elevated restore failed: " + ex.Message);
                }
                return error;
            }

            QueueElevatedLayoutRequest(request);
            return error;
        }

        private void QueueElevatedLayoutRequest(GameHelperElevationRequest request)
        {
            string key = request.ThreadId + ":" + request.FallbackHwnd + ":" + request.Layout
                + ":" + request.PreferredCandidateIndex;
            if (elevationClient.ElevationCancelled || failedElevatedRequests.Contains(key)
                || !pendingElevatedRequests.Add(key))
            {
                return;
            }

            Task.Run(async delegate
            {
                GameHelperElevationResponse response;
                try
                {
                    response = await elevationClient.PostAsync(request, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = GameHelperElevationResponse.Fail(ex.HResult, ex.Message);
                }

                if (Application.Current == null)
                {
                    return;
                }

                _ = Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    pendingElevatedRequests.Remove(key);
                    if (disposed || response.Success)
                    {
                        return;
                    }

                    failedElevatedRequests.Add(key);

                    if (response.Error == 1223)
                    {
                        SetStatus("Administrator approval was canceled; elevated game input lock is inactive.");
                    }
                    else
                    {
                        AppLog.Write("Game Helper", "Elevated input-language request failed: "
                            + response.Error + " " + response.Message);
                        SetStatus("Elevated game input-language request failed.");
                    }
                }));
            });
        }

        private void EnsureCrosshairState()
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke((Action)EnsureCrosshairState);
                return;
            }

            bool shouldExist = CrosshairVisibilityPolicy.ShouldExist(
                settings.CrosshairEnabled, crosshairPreviewCount);
            if (!shouldExist)
            {
                CloseCrosshairOverlay();
                return;
            }

            CrosshairOverlayWindow window = EnsureCrosshairOverlay();
            bool shouldShow = CrosshairVisibilityPolicy.ShouldShow(settings.CrosshairEnabled,
                settings.CrosshairRestrictToSelectedApps, foregroundExecutableMatched,
                crosshairPreviewCount);
            if (shouldShow)
            {
                window.RecoverFromShowDesktop();
                window.ApplyOptions(CreateCrosshairOptions(), crosshairTargetHwnd);
                if (!window.IsVisible)
                {
                    window.Show();
                    window.ActivateTopmost();
                }
                return;
            }

            if (window.IsVisible)
            {
                window.Hide();
            }
        }

        private CrosshairOverlayWindow EnsureCrosshairOverlay()
        {
            if (crosshairWindow != null)
            {
                return crosshairWindow;
            }

            var window = new CrosshairOverlayWindow(CreateCrosshairOptions());
            crosshairWindow = window;
            window.Closed += delegate
            {
                if (ReferenceEquals(crosshairWindow, window))
                {
                    crosshairWindow = null;
                }
            };
            return window;
        }

        private void CloseCrosshairOverlay()
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke((Action)CloseCrosshairOverlay);
                return;
            }

            CrosshairOverlayWindow window = crosshairWindow;
            crosshairWindow = null;
            if (window != null)
            {
                window.Close();
            }
        }

        private CrosshairOptions CreateCrosshairOptions()
        {
            return new CrosshairOptions
            {
                Size = Clamp(settings.CrosshairSize, CrosshairMinSize, CrosshairMaxSize),
                OpacityPercent = Clamp(settings.CrosshairOpacityPercent, 10, 100),
                ColorHex = NormalizeColorHex(settings.CrosshairColor),
                CustomColorHex = NormalizeColorHex(settings.CrosshairCustomColor, "#FFFFFF"),
                Style = NormalizeCrosshairStyle(settings.CrosshairStyle)
            };
        }

        private void ReleaseCrosshairOverlayPreview()
        {
            if (crosshairPreviewCount > 0)
            {
                crosshairPreviewCount--;
            }

            EnsureCrosshairState();
        }

        private void RebuildCrosshairAppIndex()
        {
            crosshairAppIndex = new CrosshairAppSelectionIndex(GetCrosshairSelectedApps());
        }

        private void UpdateCrosshairForegroundMatch(string foregroundPath, IntPtr foregroundHwnd)
        {
            bool matched = IsForegroundMonitoringActive
                && crosshairAppIndex != null
                && crosshairAppIndex.ContainsExecutable(foregroundPath);
            foregroundExecutableMatched = matched;
            crosshairTargetHwnd = matched
                ? CrosshairTargetWindow.Resolve(foregroundHwnd)
                : IntPtr.Zero;
            if (settings.CrosshairEnabled || crosshairWindow != null)
            {
                EnsureCrosshairState();
            }
        }

        private string GetCrosshairStatusText()
        {
            if (!settings.CrosshairEnabled)
            {
                return "Screen crosshair disabled.";
            }
            if (!settings.CrosshairRestrictToSelectedApps)
            {
                return "Screen crosshair enabled globally.";
            }

            int selectedCount = GetCrosshairSelectedApps().Count;
            return selectedCount == 0
                ? "Select at least one app to show Screen crosshair."
                : "Screen crosshair limited to " + selectedCount + " selected app"
                    + (selectedCount == 1 ? "." : "s.");
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static string FormatLayout(IntPtr layout)
        {
            return "0x" + unchecked((uint)layout.ToInt64()).ToString("X8");
        }

        private static string NormalizeCrosshairStyle(string style)
        {
            string[] styles = CrosshairStyleNames();
            for (int i = 0; i < styles.Length; i++)
            {
                if (string.Equals(style, styles[i], StringComparison.OrdinalIgnoreCase))
                {
                    return styles[i];
                }
            }

            return "Classic";
        }

        private static string NormalizeColorHex(string colorHex)
        {
            return NormalizeColorHex(colorHex, "#48FF78");
        }

        private static string NormalizeColorHex(string colorHex, string fallback)
        {
            if (string.IsNullOrWhiteSpace(colorHex))
            {
                return fallback;
            }

            string value = colorHex.Trim();
            if (value.Length == 7 && value[0] == '#')
            {
                for (int i = 1; i < value.Length; i++)
                {
                    char c = value[i];
                    bool isHex = (c >= '0' && c <= '9')
                        || (c >= 'a' && c <= 'f')
                        || (c >= 'A' && c <= 'F');
                    if (!isHex)
                    {
                        return fallback;
                    }
                }

                return value.ToUpperInvariant();
            }

            return fallback;
        }

        private static string[] CrosshairStyleNames()
        {
            return new string[]
            {
                "Classic",
                "Cross",
                "Dot",
                "Circle",
                "Tiny Dot",
                "Ring Dot",
                "Plus Dot",
                "Gap Dot",
                "T-Shape",
                "Inverted T",
                "Chevron",
                "Diamond",
                "Square",
                "Box Dot",
                "X Cross",
                "X Dot",
                "Brackets",
                "Corners",
                "Vertical Post",
                "Horizontal Bars"
            };
        }

        private void EnableWinKeyBlocker(int protectedCount)
        {
            lock (keyboardHookSync)
            {
                if (keyboardHook != IntPtr.Zero && keyboardHookThread != null
                    && keyboardHookThread.IsAlive)
                {
                    SetStatus("Game keyboard protection active for " + protectedCount + " app"
                        + (protectedCount == 1 ? "." : "s."));
                    return;
                }
            }

            int installError = 0;
            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(new ThreadStart(delegate
            {
                NativeMessage message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, PmNoRemove);
                uint threadId = GetCurrentThreadId();
                IntPtr moduleHandle = IntPtr.Zero;
                using (Process process = Process.GetCurrentProcess())
                using (ProcessModule module = process.MainModule)
                {
                    if (module != null)
                    {
                        moduleHandle = GetModuleHandle(module.ModuleName);
                    }
                }

                IntPtr hook = SetWindowsHookEx(WhKeyboardLl, keyboardProc, moduleHandle, 0);
                if (hook == IntPtr.Zero)
                {
                    installError = Marshal.GetLastWin32Error();
                }
                lock (keyboardHookSync)
                {
                    keyboardHook = hook;
                    keyboardHookThreadId = threadId;
                }
                ready.Set();

                if (hook != IntPtr.Zero)
                {
                    while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                    {
                    }
                    if (!UnhookWindowsHookEx(hook))
                    {
                        AppLog.Write("Game Helper", "Keyboard protection hook removal failed: "
                            + Marshal.GetLastWin32Error());
                    }
                }

                lock (keyboardHookSync)
                {
                    if (keyboardHook == hook)
                    {
                        keyboardHook = IntPtr.Zero;
                    }
                    if (keyboardHookThreadId == threadId)
                    {
                        keyboardHookThreadId = 0;
                    }
                    if (ReferenceEquals(keyboardHookThread, Thread.CurrentThread))
                    {
                        keyboardHookThread = null;
                    }
                }
            }))
            {
                IsBackground = true,
                Name = "Game Helper keyboard hook"
            };
            lock (keyboardHookSync)
            {
                keyboardHookThread = thread;
            }
            thread.Start();

            bool installed = ready.Wait(TimeSpan.FromSeconds(5));
            if (installed)
            {
                ready.Dispose();
            }
            if (!installed || !IsKeyboardProtectionHookActive)
            {
                AppLog.Write("Game Helper", "Keyboard protection hook install failed: "
                    + (installed ? installError.ToString() : "timeout"));
                SetStatus("Game keyboard protection hook install failed.");
            }
            else
            {
                AppLog.Write("Game Helper", "Keyboard protection hook installed for "
                    + protectedCount + " configured app" + (protectedCount == 1 ? "." : "s."));
                SetStatus("Game keyboard protection active for " + protectedCount + " app"
                    + (protectedCount == 1 ? "." : "s."));
            }
        }

        private void DisableWinKeyBlocker()
        {
            Thread thread;
            uint threadId;
            IntPtr hook;
            lock (keyboardHookSync)
            {
                thread = keyboardHookThread;
                threadId = keyboardHookThreadId;
                hook = keyboardHook;
            }

            if (thread != null && thread.IsAlive && threadId != 0)
            {
                PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                if (!thread.Join(TimeSpan.FromSeconds(2)) && hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hook);
                }
            }
            else if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
            }

            lock (keyboardHookSync)
            {
                keyboardHook = IntPtr.Zero;
                keyboardHookThread = null;
                keyboardHookThreadId = 0;
            }
            keyboardProtectionState.Reset();
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                bool keyDown = message == WmKeyDown || message == WmSysKeyDown;
                bool keyUp = message == WmKeyUp || message == WmSysKeyUp;

                if (keyDown || keyUp)
                {
                    var data = (KeyboardHookData)Marshal.PtrToStructure(lParam, typeof(KeyboardHookData));
                    ForegroundProtectionSnapshot snapshot = foregroundProtection;
                    IntPtr foregroundHwnd = GetForegroundWindow();
                    bool snapshotIsCurrent = snapshot != null && snapshot.Hwnd == foregroundHwnd;
                    GameKeyboardDecision decision = keyboardProtectionState.Process(data.vkCode,
                        data.flags, keyDown, keyUp,
                        snapshotIsCurrent && snapshot.BlockWindowsKey,
                        snapshotIsCurrent && snapshot.RightControlDShowsDesktop);
                    if (decision.ShowDesktop && snapshotIsCurrent)
                    {
                        ShowDesktopAsync(snapshot.Hwnd, snapshot.AppName);
                        QueueStatus("Right Ctrl + D showed desktop for " + snapshot.AppName + ".");
                    }
                    if (decision.Suppress)
                    {
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private void ShowDesktopAsync(IntPtr sourceHwnd, string appName)
        {
            var thread = new Thread(new ThreadStart(delegate
            {
                bool shellAccepted = TryShellMinimizeAll();
                Thread.Sleep(50);
                int forcedCount = MinimizeVisibleTopLevelWindows();
                Thread.Sleep(100);
                bool sourceMinimized = sourceHwnd == IntPtr.Zero || IsIconic(sourceHwnd)
                    || GetForegroundWindow() != sourceHwnd;
                AppLog.Write("Game Helper", "Show desktop for " + appName
                    + ": shell=" + shellAccepted + " forced=" + forcedCount
                    + " sourceMinimized=" + sourceMinimized + ".");
            }));
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static bool TryShellMinimizeAll()
        {
            object shell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return false;
                }

                shell = Activator.CreateInstance(shellType);
                shellType.InvokeMember("MinimizeAll", BindingFlags.InvokeMethod, null, shell, null);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (shell != null)
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }

        private static int MinimizeVisibleTopLevelWindows()
        {
            int minimized = 0;
            IntPtr shellWindow = GetShellWindow();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                bool visible = hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
                bool shellDesktop = hwnd == shellWindow || IsShellDesktopWindow(hwnd);
                int extendedStyle = hwnd == IntPtr.Zero ? 0 : GetWindowLong(hwnd, GwlExstyle);
                if (!ShouldForceMinimizeForShowDesktop(visible, shellDesktop, extendedStyle))
                {
                    return true;
                }

                if (ShowWindowAsync(hwnd, SwForceminimize)) minimized++;
                return true;
            }, IntPtr.Zero);
            return minimized;
        }

        internal static bool ShouldForceMinimizeForShowDesktop(bool visible,
            bool shellDesktop, int extendedStyle)
        {
            // Shell MinimizeAll deliberately leaves tool windows alone. Match that behavior in
            // the fallback so the no-activate crosshair overlay is not minimized and later
            // restored with Windows' minimized-window coordinates.
            return visible && !shellDesktop && (extendedStyle & WsExToolWindow) == 0;
        }

        private static bool IsShellDesktopWindow(IntPtr hwnd)
        {
            var className = new StringBuilder(64);
            if (GetClassName(hwnd, className, className.Capacity) == 0) return false;
            string value = className.ToString();
            return string.Equals(value, "Shell_TrayWnd", StringComparison.Ordinal)
                || string.Equals(value, "Shell_SecondaryTrayWnd", StringComparison.Ordinal)
                || string.Equals(value, "Progman", StringComparison.Ordinal)
                || string.Equals(value, "WorkerW", StringComparison.Ordinal);
        }

        private static List<GameHelperAppCandidate> EnumerateInstalledApps()
        {
            List<AppCatalogItem> catalog = new AppCatalogService().GetInstalledApps();
            var result = new List<GameHelperAppCandidate>();
            for (int i = 0; i < catalog.Count; i++)
            {
                AppCatalogItem app = catalog[i];
                result.Add(new GameHelperAppCandidate
                {
                    Id = app.Id,
                    Name = app.Name,
                    TargetPath = app.TargetPath,
                    Publisher = app.Publisher,
                    Source = app.Source,
                    IconPath = string.IsNullOrWhiteSpace(app.IconPath) ? app.TargetPath : app.IconPath
                });
            }

            result.Sort(delegate(GameHelperAppCandidate left, GameHelperAppCandidate right)
            {
                return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        internal static List<GameHelperProtectedApp> DecodeProtectedApps(string encodedText)
        {
            var result = new List<GameHelperProtectedApp>();
            if (string.IsNullOrWhiteSpace(encodedText))
            {
                return result;
            }

            string[] records = encodedText.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < records.Length; i++)
            {
                try
                {
                    string record = Encoding.UTF8.GetString(Convert.FromBase64String(records[i]));
                    string[] parts = record.Split('\t');
                    string name = parts.Length > 0 ? parts[0] : string.Empty;
                    string path = parts.Length > 1 ? parts[1] : string.Empty;
                    string publisher = parts.Length > 2 ? parts[2] : string.Empty;
                    bool lockMicrosoftEnglish = parts.Length > 3 && ParseStoredBool(parts[3], false);
                    bool blockInputLanguageSwitch = parts.Length <= 4 || ParseStoredBool(parts[4], true);
                    bool restorePreviousInputLanguage = parts.Length <= 5 || ParseStoredBool(parts[5], true);
                    bool blockWindowsKey = parts.Length <= 6 || ParseStoredBool(parts[6], true);
                    bool rightControlDShowsDesktop = parts.Length <= 7 || ParseStoredBool(parts[7], true);
                    string normalizedPath = NormalizeExecutablePath(path);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        continue;
                    }

                    result.Add(new GameHelperProtectedApp
                    {
                        Id = CreateAppId(normalizedPath),
                        Name = name,
                        TargetPath = normalizedPath,
                        Publisher = publisher,
                        ProcessName = GetProcessNameFromPath(normalizedPath),
                        BlockWindowsKey = blockWindowsKey,
                        RightControlDShowsDesktop = rightControlDShowsDesktop,
                        LockMicrosoftEnglish = lockMicrosoftEnglish,
                        BlockInputLanguageSwitch = blockInputLanguageSwitch,
                        RestorePreviousInputLanguage = restorePreviousInputLanguage
                    });
                }
                catch
                {
                    // Ignore malformed records so one bad entry does not break the module.
                }
            }

            return result;
        }

        internal static string EncodeProtectedApps(List<GameHelperProtectedApp> apps)
        {
            var records = new List<string>();
            for (int i = 0; i < apps.Count; i++)
            {
                GameHelperProtectedApp app = apps[i];
                string record = CleanField(app.Name) + "\t"
                    + CleanField(app.TargetPath) + "\t"
                    + CleanField(app.Publisher) + "\t"
                    + (app.LockMicrosoftEnglish ? "1" : "0") + "\t"
                    + (app.BlockInputLanguageSwitch ? "1" : "0") + "\t"
                    + (app.RestorePreviousInputLanguage ? "1" : "0") + "\t"
                    + (app.BlockWindowsKey ? "1" : "0") + "\t"
                    + (app.RightControlDShowsDesktop ? "1" : "0");
                records.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(record)));
            }

            return string.Join(",", records.ToArray());
        }

        private static bool ParseStoredBool(string value, bool fallback)
        {
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return fallback;
        }

        private static string ExtractExecutablePath(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string expanded = Environment.ExpandEnvironmentVariables(text.Trim());
            if (expanded.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = expanded.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    return expanded.Substring(1, endQuote - 1);
                }
            }

            int exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                return expanded.Substring(0, exeIndex + 4).Trim().Trim('"');
            }

            return expanded.Trim().Trim('"');
        }

        private static string NormalizeExecutablePath(string path)
        {
            string executablePath = ExtractExecutablePath(path);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return string.Empty;
            }

            try
            {
                executablePath = Environment.ExpandEnvironmentVariables(executablePath);
                if (!Path.IsPathRooted(executablePath))
                {
                    return string.Empty;
                }

                string extension = Path.GetExtension(executablePath);
                if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return Path.GetFullPath(executablePath);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CreateAppId(string executablePath)
        {
            string normalizedPath = NormalizeExecutablePath(executablePath);
            return string.IsNullOrWhiteSpace(normalizedPath)
                ? string.Empty
                : normalizedPath.ToUpperInvariant();
        }

        private static string GetProcessNameFromPath(string path)
        {
            try
            {
                return Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CleanField(string text)
        {
            return (text ?? string.Empty)
                .Replace('\t', ' ')
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private void SetStatus(string status)
        {
            lastStatus = status;
            EventHandler handler = StatusChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void QueueStatus(string status)
        {
            try
            {
                Dispatcher dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                if (dispatcher == null)
                {
                    ThreadPool.QueueUserWorkItem(delegate { SetStatus(status); });
                    return;
                }
                dispatcher.BeginInvoke(new Action(delegate { SetStatus(status); }),
                    DispatcherPriority.Background);
            }
            catch
            {
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, uint message,
            IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage message, IntPtr window,
            uint minimumMessage, uint maximumMessage);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window,
            uint minimumMessage, uint maximumMessage, uint removeMessage);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maximumCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public NativePoint point;
            public uint privateValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHookData
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private sealed class CrosshairOverlayWindow : Window
        {
            private readonly CrosshairVisual visual;
            private CrosshairOptions options;
            private IntPtr targetHwnd;

            public CrosshairOverlayWindow(CrosshairOptions options)
            {
                this.options = options;
                visual = new CrosshairVisual();

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                Focusable = false;
                IsHitTestVisible = false;
                Content = visual;

                ApplyOptions(options, IntPtr.Zero);
                SourceInitialized += delegate
                {
                    ApplyOverlayWindowStyles();
                    SetOverlayBounds();
                };
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            }

            public void ApplyOptions(CrosshairOptions newOptions, IntPtr newTargetHwnd)
            {
                options = newOptions;
                targetHwnd = newTargetHwnd;
                visual.ApplyOptions(newOptions);
                SetOverlayBounds();
            }

            public void RecoverFromShowDesktop()
            {
                // MinimizeAll should ignore WS_EX_TOOLWINDOW, but older Guard Center builds and
                // some shell implementations may already have minimized this persistent overlay.
                // Normalize it before applying target bounds so WPF does not retain the special
                // minimized-window position when the protected app returns to the foreground.
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
            }

            public void ActivateTopmost()
            {
                Topmost = false;
                Topmost = true;
            }

            public Point GetCenterInDevicePixels()
            {
                PresentationSource source = PresentationSource.FromVisual(this);
                if (source == null || source.CompositionTarget == null)
                {
                    return new Point(double.NaN, double.NaN);
                }

                Matrix toDevice = source.CompositionTarget.TransformToDevice;
                return toDevice.Transform(new Point(Left + (Width / 2.0), Top + (Height / 2.0)));
            }

            protected override void OnClosed(EventArgs e)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
                base.OnClosed(e);
            }

            private void DisplaySettingsChanged(object sender, EventArgs e)
            {
                Dispatcher.BeginInvoke(new Action(SetOverlayBounds));
            }

            private void SetOverlayBounds()
            {
                double side = Math.Max(48, (options.Size * 3.0) + 30);
                Width = side;
                Height = side;

                CrosshairTargetWindow.NativeRect bounds;
                PresentationSource source = PresentationSource.FromVisual(this);
                if (targetHwnd != IntPtr.Zero
                    && CrosshairTargetWindow.TryGetClientBounds(targetHwnd, out bounds)
                    && source != null
                    && source.CompositionTarget != null)
                {
                    Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                    Point center = fromDevice.Transform(new Point(bounds.CenterX, bounds.CenterY));
                    if (IsFinite(center.X) && IsFinite(center.Y))
                    {
                        Left = Math.Round(center.X - (side / 2.0));
                        Top = Math.Round(center.Y - (side / 2.0));
                        return;
                    }
                }

                Left = Math.Round((SystemParameters.PrimaryScreenWidth - side) / 2.0);
                Top = Math.Round((SystemParameters.PrimaryScreenHeight - side) / 2.0);
            }

            private static bool IsFinite(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }

            private void ApplyOverlayWindowStyles()
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, GwlExstyle);
                style |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
                SetWindowLong(hwnd, GwlExstyle, style);
            }
        }

        private sealed class CrosshairVisual : FrameworkElement
        {
            private CrosshairOptions options = new CrosshairOptions
            {
                Size = 20,
                OpacityPercent = 100,
                ColorHex = "#48FF78",
                Style = "Classic"
            };

            public CrosshairVisual()
            {
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
            }

            public void ApplyOptions(CrosshairOptions newOptions)
            {
                options = newOptions;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);

                double width = ActualWidth;
                double height = ActualHeight;
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                double centerX = Math.Round(width / 2.0) + 0.5;
                double centerY = Math.Round(height / 2.0) + 0.5;
                double length = Math.Max(CrosshairMinSize, options.Size);
                double gap = Math.Max(1.2, Math.Min(14, length * 0.35));
                double opacity = Math.Max(0.1, Math.Min(1.0, options.OpacityPercent / 100.0));
                Color color = ParseColor(options.ColorHex, Color.FromRgb(72, 255, 120));
                double lineThickness = Math.Max(1.2, Math.Min(2.4, 1.0 + (length * 0.08)));
                double outlineThickness = lineThickness + Math.Max(1.6, lineThickness * 1.15);

                var outlinePen = CreatePen(Color.FromArgb((byte)(230 * opacity), 0, 0, 0), outlineThickness);
                var crosshairPen = CreatePen(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B), lineThickness);
                var outlineBrush = new SolidColorBrush(Color.FromArgb((byte)(220 * opacity), 0, 0, 0));
                var crosshairBrush = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));

                string style = options.Style ?? "Classic";
                if (string.Equals(style, "Dot", StringComparison.OrdinalIgnoreCase))
                {
                    double radius = Math.Max(1.8, length * 0.18);
                    drawingContext.DrawEllipse(outlineBrush, null, new Point(centerX, centerY), radius + 2, radius + 2);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), radius, radius);
                    return;
                }

                if (string.Equals(style, "Tiny Dot", StringComparison.OrdinalIgnoreCase))
                {
                    drawingContext.DrawEllipse(outlineBrush, null, new Point(centerX, centerY), 2.4, 2.4);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), 1.1, 1.1);
                    return;
                }

                if (string.Equals(style, "Circle", StringComparison.OrdinalIgnoreCase))
                {
                    double radius = Math.Max(3.5, length * 0.75);
                    drawingContext.DrawEllipse(null, outlinePen, new Point(centerX, centerY), radius, radius);
                    drawingContext.DrawEllipse(null, crosshairPen, new Point(centerX, centerY), radius, radius);
                    return;
                }

                if (string.Equals(style, "Ring Dot", StringComparison.OrdinalIgnoreCase))
                {
                    double radius = Math.Max(3.5, length * 0.65);
                    drawingContext.DrawEllipse(null, outlinePen, new Point(centerX, centerY), radius, radius);
                    drawingContext.DrawEllipse(null, crosshairPen, new Point(centerX, centerY), radius, radius);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), 2.2, 2.2);
                    return;
                }

                if (string.Equals(style, "Cross", StringComparison.OrdinalIgnoreCase))
                {
                    DrawSolidCross(drawingContext, outlinePen, centerX, centerY, length);
                    DrawSolidCross(drawingContext, crosshairPen, centerX, centerY, length);
                    return;
                }

                if (string.Equals(style, "Plus Dot", StringComparison.OrdinalIgnoreCase))
                {
                    DrawSolidCross(drawingContext, outlinePen, centerX, centerY, length);
                    DrawSolidCross(drawingContext, crosshairPen, centerX, centerY, length);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), 2.4, 2.4);
                    return;
                }

                if (string.Equals(style, "Gap Dot", StringComparison.OrdinalIgnoreCase))
                {
                    DrawClassicCrosshair(drawingContext, outlinePen, centerX, centerY, gap, length);
                    DrawClassicCrosshair(drawingContext, crosshairPen, centerX, centerY, gap, length);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), 2.4, 2.4);
                    return;
                }

                if (string.Equals(style, "T-Shape", StringComparison.OrdinalIgnoreCase))
                {
                    DrawTShape(drawingContext, outlinePen, centerX, centerY, gap, length, false);
                    DrawTShape(drawingContext, crosshairPen, centerX, centerY, gap, length, false);
                    return;
                }

                if (string.Equals(style, "Inverted T", StringComparison.OrdinalIgnoreCase))
                {
                    DrawTShape(drawingContext, outlinePen, centerX, centerY, gap, length, true);
                    DrawTShape(drawingContext, crosshairPen, centerX, centerY, gap, length, true);
                    return;
                }

                if (string.Equals(style, "Chevron", StringComparison.OrdinalIgnoreCase))
                {
                    DrawChevron(drawingContext, outlinePen, centerX, centerY, length);
                    DrawChevron(drawingContext, crosshairPen, centerX, centerY, length);
                    return;
                }

                if (string.Equals(style, "Diamond", StringComparison.OrdinalIgnoreCase))
                {
                    DrawDiamond(drawingContext, outlinePen, centerX, centerY, length * 0.75);
                    DrawDiamond(drawingContext, crosshairPen, centerX, centerY, length * 0.75);
                    return;
                }

                if (string.Equals(style, "Square", StringComparison.OrdinalIgnoreCase))
                {
                    DrawSquare(drawingContext, outlinePen, centerX, centerY, length * 0.7);
                    DrawSquare(drawingContext, crosshairPen, centerX, centerY, length * 0.7);
                    return;
                }

                if (string.Equals(style, "Box Dot", StringComparison.OrdinalIgnoreCase))
                {
                    DrawSquare(drawingContext, outlinePen, centerX, centerY, length * 0.65);
                    DrawSquare(drawingContext, crosshairPen, centerX, centerY, length * 0.65);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), 2.2, 2.2);
                    return;
                }

                if (string.Equals(style, "X Cross", StringComparison.OrdinalIgnoreCase))
                {
                    DrawX(drawingContext, outlinePen, centerX, centerY, length);
                    DrawX(drawingContext, crosshairPen, centerX, centerY, length);
                    return;
                }

                if (string.Equals(style, "X Dot", StringComparison.OrdinalIgnoreCase))
                {
                    DrawX(drawingContext, outlinePen, centerX, centerY, length);
                    DrawX(drawingContext, crosshairPen, centerX, centerY, length);
                    drawingContext.DrawEllipse(crosshairBrush, null, new Point(centerX, centerY), 2.3, 2.3);
                    return;
                }

                if (string.Equals(style, "Brackets", StringComparison.OrdinalIgnoreCase))
                {
                    DrawBrackets(drawingContext, outlinePen, centerX, centerY, length);
                    DrawBrackets(drawingContext, crosshairPen, centerX, centerY, length);
                    return;
                }

                if (string.Equals(style, "Corners", StringComparison.OrdinalIgnoreCase))
                {
                    DrawCorners(drawingContext, outlinePen, centerX, centerY, length);
                    DrawCorners(drawingContext, crosshairPen, centerX, centerY, length);
                    return;
                }

                if (string.Equals(style, "Vertical Post", StringComparison.OrdinalIgnoreCase))
                {
                    DrawVerticalPost(drawingContext, outlinePen, centerX, centerY, gap, length);
                    DrawVerticalPost(drawingContext, crosshairPen, centerX, centerY, gap, length);
                    return;
                }

                if (string.Equals(style, "Horizontal Bars", StringComparison.OrdinalIgnoreCase))
                {
                    DrawHorizontalBars(drawingContext, outlinePen, centerX, centerY, gap, length);
                    DrawHorizontalBars(drawingContext, crosshairPen, centerX, centerY, gap, length);
                    return;
                }

                DrawClassicCrosshair(drawingContext, outlinePen, centerX, centerY, gap, length);
                DrawClassicCrosshair(drawingContext, crosshairPen, centerX, centerY, gap, length);
            }

            private static Pen CreatePen(Color color, double thickness)
            {
                var pen = new Pen(new SolidColorBrush(color), thickness);
                pen.StartLineCap = PenLineCap.Square;
                pen.EndLineCap = PenLineCap.Square;
                pen.Freeze();
                return pen;
            }

            private static Color ParseColor(string colorHex, Color fallback)
            {
                try
                {
                    object value = ColorConverter.ConvertFromString(colorHex);
                    return value is Color ? (Color)value : fallback;
                }
                catch
                {
                    return fallback;
                }
            }

            private static void DrawClassicCrosshair(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double gap, double length)
            {
                drawingContext.DrawLine(pen,
                    new Point(centerX - gap - length, centerY),
                    new Point(centerX - gap, centerY));
                drawingContext.DrawLine(pen,
                    new Point(centerX + gap, centerY),
                    new Point(centerX + gap + length, centerY));
                drawingContext.DrawLine(pen,
                    new Point(centerX, centerY - gap - length),
                    new Point(centerX, centerY - gap));
                drawingContext.DrawLine(pen,
                    new Point(centerX, centerY + gap),
                    new Point(centerX, centerY + gap + length));
            }

            private static void DrawSolidCross(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                drawingContext.DrawLine(pen,
                    new Point(centerX - length, centerY),
                    new Point(centerX + length, centerY));
                drawingContext.DrawLine(pen,
                    new Point(centerX, centerY - length),
                    new Point(centerX, centerY + length));
            }

            private static void DrawTShape(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double gap, double length, bool inverted)
            {
                drawingContext.DrawLine(pen,
                    new Point(centerX - gap - length, centerY),
                    new Point(centerX - gap, centerY));
                drawingContext.DrawLine(pen,
                    new Point(centerX + gap, centerY),
                    new Point(centerX + gap + length, centerY));

                if (inverted)
                {
                    drawingContext.DrawLine(pen,
                        new Point(centerX, centerY - gap - length),
                        new Point(centerX, centerY - gap));
                }
                else
                {
                    drawingContext.DrawLine(pen,
                        new Point(centerX, centerY + gap),
                        new Point(centerX, centerY + gap + length));
                }
            }

            private static void DrawChevron(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                double width = length * 0.55;
                double height = length * 0.6;
                drawingContext.DrawLine(pen,
                    new Point(centerX - width, centerY + height * 0.5),
                    new Point(centerX, centerY - height * 0.5));
                drawingContext.DrawLine(pen,
                    new Point(centerX, centerY - height * 0.5),
                    new Point(centerX + width, centerY + height * 0.5));
            }

            private static void DrawDiamond(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double radius)
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(new Point(centerX, centerY - radius), false, true);
                    context.LineTo(new Point(centerX + radius, centerY), true, false);
                    context.LineTo(new Point(centerX, centerY + radius), true, false);
                    context.LineTo(new Point(centerX - radius, centerY), true, false);
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, pen, geometry);
            }

            private static void DrawSquare(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double radius)
            {
                drawingContext.DrawRectangle(null, pen,
                    new Rect(centerX - radius, centerY - radius, radius * 2, radius * 2));
            }

            private static void DrawX(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                double diagonal = length * 0.7;
                drawingContext.DrawLine(pen,
                    new Point(centerX - diagonal, centerY - diagonal),
                    new Point(centerX + diagonal, centerY + diagonal));
                drawingContext.DrawLine(pen,
                    new Point(centerX + diagonal, centerY - diagonal),
                    new Point(centerX - diagonal, centerY + diagonal));
            }

            private static void DrawBrackets(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                double distance = length * 0.9;
                double half = length * 0.55;
                double hook = length * 0.35;
                drawingContext.DrawLine(pen, new Point(centerX - distance, centerY - half), new Point(centerX - distance, centerY + half));
                drawingContext.DrawLine(pen, new Point(centerX - distance, centerY - half), new Point(centerX - distance + hook, centerY - half));
                drawingContext.DrawLine(pen, new Point(centerX - distance, centerY + half), new Point(centerX - distance + hook, centerY + half));
                drawingContext.DrawLine(pen, new Point(centerX + distance, centerY - half), new Point(centerX + distance, centerY + half));
                drawingContext.DrawLine(pen, new Point(centerX + distance, centerY - half), new Point(centerX + distance - hook, centerY - half));
                drawingContext.DrawLine(pen, new Point(centerX + distance, centerY + half), new Point(centerX + distance - hook, centerY + half));
            }

            private static void DrawCorners(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                double inner = length * 0.45;
                double outer = length * 0.95;
                DrawCorner(drawingContext, pen, centerX - inner, centerY - inner, -outer + inner, -outer + inner);
                DrawCorner(drawingContext, pen, centerX + inner, centerY - inner, outer - inner, -outer + inner);
                DrawCorner(drawingContext, pen, centerX - inner, centerY + inner, -outer + inner, outer - inner);
                DrawCorner(drawingContext, pen, centerX + inner, centerY + inner, outer - inner, outer - inner);
            }

            private static void DrawCorner(DrawingContext drawingContext, Pen pen,
                double x, double y, double dx, double dy)
            {
                drawingContext.DrawLine(pen, new Point(x, y), new Point(x + dx, y));
                drawingContext.DrawLine(pen, new Point(x, y), new Point(x, y + dy));
            }

            private static void DrawVerticalPost(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double gap, double length)
            {
                drawingContext.DrawLine(pen,
                    new Point(centerX, centerY - gap - length),
                    new Point(centerX, centerY - gap));
                drawingContext.DrawLine(pen,
                    new Point(centerX, centerY + gap),
                    new Point(centerX, centerY + gap + length * 1.3));
            }

            private static void DrawHorizontalBars(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double gap, double length)
            {
                drawingContext.DrawLine(pen,
                    new Point(centerX - gap - length, centerY),
                    new Point(centerX - gap, centerY));
                drawingContext.DrawLine(pen,
                    new Point(centerX + gap, centerY),
                    new Point(centerX + gap + length, centerY));
            }
        }

        private sealed class ForegroundProtectionSnapshot
        {
            public static readonly ForegroundProtectionSnapshot Empty =
                new ForegroundProtectionSnapshot(string.Empty, string.Empty, IntPtr.Zero, false, false);

            public ForegroundProtectionSnapshot(string appId, string appName, IntPtr hwnd,
                bool blockWindowsKey, bool rightControlDShowsDesktop)
            {
                AppId = appId;
                AppName = appName;
                Hwnd = hwnd;
                BlockWindowsKey = blockWindowsKey;
                RightControlDShowsDesktop = rightControlDShowsDesktop;
            }

            public string AppId { get; }
            public string AppName { get; }
            public IntPtr Hwnd { get; }
            public bool BlockWindowsKey { get; }
            public bool RightControlDShowsDesktop { get; }
        }

        private sealed class ActiveLanguageSession
        {
            public string AppId;
            public string AppName;
            public string ExpectedPath;
            public uint ThreadId;
            public IntPtr FallbackHwnd;
            public IntPtr OriginalLayout;
            public IntPtr ReturnLayout;
            public bool RestorePreviousInputLanguage;
            public bool BlockInputLanguageSwitch;
            public int NextCandidateIndex;
        }

        private sealed class PendingLanguageRestore
        {
            public string AppName;
            public string ExpectedPath;
            public uint ThreadId;
            public IntPtr FallbackHwnd;
            public IntPtr Layout;
            public uint SourceThreadId;
            public string SourceExpectedPath;
            public IntPtr SourceLayout;
            public DateTime DeadlineUtc;
            public DateTime TargetSinceUtc;
            public int NextCandidateIndex;
        }

        private sealed class ActionDisposable : IDisposable
        {
            private Action action;

            public ActionDisposable(Action action)
            {
                this.action = action;
            }

            public void Dispose()
            {
                Action current = action;
                action = null;
                if (current != null)
                {
                    current();
                }
            }
        }
    }

    internal sealed class GameHelperAppCandidate
    {
        public string Id;
        public string Name;
        public string TargetPath;
        public string Publisher;
        public string Source;
        public string IconPath;
    }

    internal sealed class GameHelperProtectedApp
    {
        public string Id;
        public string Name;
        public string TargetPath;
        public string Publisher;
        public string ProcessName;
        public bool BlockWindowsKey = true;
        public bool RightControlDShowsDesktop = true;
        public bool LockMicrosoftEnglish;
        public bool BlockInputLanguageSwitch = true;
        public bool RestorePreviousInputLanguage = true;
    }

    internal sealed class CrosshairOptions
    {
        public int Size;
        public int OpacityPercent;
        public string ColorHex;
        public string CustomColorHex;
        public string Style;
    }
}
