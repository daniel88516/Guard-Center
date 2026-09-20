using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;
using UiControls = Wpf.Ui.Controls;

namespace GuardCenter
{
    internal sealed class GuardCenterController : IDisposable
    {
        private readonly SettingsStore settingsStore;
        private readonly AppSettings settings;
        private readonly AudioZeroModule audioModule;
        private readonly AudioMixerModule audioMixerModule;
        private readonly DeviceGuardModule deviceGuardModule;
        private readonly KeyboardGuardModule keyboardModule;
        private readonly GameHelperModule gameHelperModule;
        private readonly AppGuardModule appGuardModule;
        private readonly LinkGuardModule linkGuardModule;
        private readonly DisplayGuardModule displayModule;
        private readonly VsrGuardModule vsrGuardModule;
        private readonly PowerGuardModule powerGuardModule;
        private readonly UacGuardModule uacGuardModule;
        private readonly AppActionIpcServer appActionIpcServer;
        private DrawingIcon applicationIcon;
        private readonly Forms.NotifyIcon trayIcon;
        private Forms.ToolStripMenuItem powerGuardEnabledMenuItem;
        private Forms.ToolStripMenuItem powerGuardDisplayMenuItem;
        private Forms.ToolStripMenuItem powerGuardThirtyMinutesMenuItem;
        private Forms.ToolStripMenuItem powerGuardOneHourMenuItem;
        private Forms.ToolStripMenuItem powerGuardTwoHoursMenuItem;
        private Forms.ToolStripMenuItem powerGuardUntilManualMenuItem;
        private Forms.ToolStripMenuItem powerGuardRemainingMenuItem;
        private Forms.ToolStripMenuItem screenCrosshairMenuItem;
        private MainWindow mainWindow;
        private Timer uacGuardAutoTimer;
        private int uacGuardAutoTickRunning;
        private string lastUacGuardAutoError = string.Empty;
        private bool exiting;
        private bool disposed;

        public GuardCenterController(bool startMinimized)
            : this(startMinimized, null)
        {
        }

        public GuardCenterController(bool startMinimized, AppActionRequest pendingAppAction)
        {
            AppPaths.EnsureRoot();
            settingsStore = new SettingsStore(AppPaths.SettingsPath);
            settings = settingsStore.Load();
            ApplicationIconService.CleanupManagedOrphans();
            settings.Appearance.CustomIconPath = ApplicationIconService.NormalizeCustomPath(
                settings.Appearance.CustomIconPath);
            if (string.IsNullOrWhiteSpace(settings.Appearance.CustomIconPath))
            {
                string managedCustomIcon = Path.Combine(AppPaths.AppearanceRoot,
                    ApplicationIconService.CustomPngFileName);
                settings.Appearance.CustomIconPath = ApplicationIconService.NormalizeCustomPath(managedCustomIcon);
            }

            try
            {
                if (StartupManager.RefreshIfConfigured(GetApplicationShellIconPath()))
                {
                    AppLog.Write("Startup", "Updated the Windows startup shortcut for the current portable location: "
                        + AppPaths.InstalledExePath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Startup", "Failed to refresh the Windows startup shortcut: " + ex);
            }

            audioModule = new AudioZeroModule(settings.Audio);
            audioMixerModule = new AudioMixerModule(settings.AudioMixer);
            deviceGuardModule = new DeviceGuardModule();
            keyboardModule = new KeyboardGuardModule(settings.Keyboard);
            gameHelperModule = new GameHelperModule(settings.GameHelper);
            appGuardModule = new AppGuardModule();
            linkGuardModule = new LinkGuardModule(settings.LinkGuard);
            displayModule = new DisplayGuardModule(settings.DisplayGuard, SaveSettings);
            vsrGuardModule = new VsrGuardModule();
            powerGuardModule = new PowerGuardModule(settings.PowerGuard);
            uacGuardModule = new UacGuardModule();
            if (keyboardModule.ApplySavedState())
            {
                SaveSettings();
            }
            gameHelperModule.ApplySavedState();

            audioModule.StatusChanged += delegate { UpdateTrayText(); };
            audioMixerModule.StatusChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            };
            keyboardModule.StatusChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            };
            gameHelperModule.StatusChanged += delegate
            {
                UpdateScreenCrosshairTrayMenu();
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            };
            appGuardModule.StatusChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            };
            linkGuardModule.StatusChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshLinkGuardUi(false);
                }
            };
            linkGuardModule.RulesChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshLinkGuardUi(true);
                }
            };
            deviceGuardModule.StatusChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            };
            displayModule.StatusChanged += delegate
            {
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            };
            powerGuardModule.StateChanged += PowerGuardModule_StateChanged;

            applicationIcon = ApplicationIconService.LoadDrawingIcon(settings.Appearance.CustomIconPath);
            trayIcon = new Forms.NotifyIcon
            {
                Icon = applicationIcon,
                Text = "Guard Center",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu()
            };
            trayIcon.DoubleClick += delegate { ShowMainWindow(); };
            UpdatePowerGuardTrayMenu();
            UpdateScreenCrosshairTrayMenu();

            if (settings.Audio.Enabled)
            {
                audioModule.Start();
            }

            if (settings.AppGuard.ExplorerIntegrationEnabled)
            {
                try
                {
                    ExplorerIntegrationService.Enable(GetApplicationShellIconPath());
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "Explorer integration failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            appGuardModule.Start();
            linkGuardModule.Start();
            deviceGuardModule.Start();
            displayModule.Start();
            appActionIpcServer = new AppActionIpcServer(HandleAppActionRequest);
            appActionIpcServer.Start();
            UpdateTrayText();

            uacGuardAutoTimer = new Timer(delegate { BeginUacGuardAutomaticAuthorization(); },
                null, 250, 5000);

            if (!startMinimized)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }

            if (pendingAppAction != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    HandleAppActionRequest(pendingAppAction);
                }));
            }
        }

        public void SaveSettings()
        {
            settingsStore.Save(settings);
        }

        private async void BeginUacGuardAutomaticAuthorization()
        {
            if (disposed || settings.UacGuard.AutomaticAuthorizationPaused
                || Interlocked.Exchange(ref uacGuardAutoTickRunning, 1) != 0)
            {
                return;
            }

            try
            {
                string mode = UacGuardModule.NormalizeAuthorizationMode(settings.UacGuard.AuthorizationMode);
                UacGuardActionResult result = string.Equals(mode,
                        UacGuardModule.GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                    ? await uacGuardModule.StartLifecycleGsudoSessionAsync()
                    : await uacGuardModule.StartGsudoSessionAsync();

                bool waitingForCodex = !result.Success && string.Equals(mode,
                        UacGuardModule.CodexProcessMode, StringComparison.OrdinalIgnoreCase)
                    && result.Message.StartsWith("尚未偵測到 Codex", StringComparison.Ordinal);
                if (result.Success)
                {
                    lastUacGuardAutoError = string.Empty;
                }
                else if (!waitingForCodex && !string.Equals(lastUacGuardAutoError,
                    result.Message, StringComparison.Ordinal))
                {
                    lastUacGuardAutoError = result.Message;
                    AppLog.Write("UAC Guard", result.Message);
                    trayIcon.ShowBalloonTip(6000, "UAC Guard 自動授權失敗", result.Message,
                        Forms.ToolTipIcon.Warning);
                }

                if (mainWindow != null)
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(mainWindow.RefreshStatus));
                }
            }
            finally
            {
                Interlocked.Exchange(ref uacGuardAutoTickRunning, 0);
            }
        }

        public string GetCustomApplicationIconPath()
        {
            return settings.Appearance.CustomIconPath;
        }

        public string GetApplicationShellIconPath()
        {
            return ApplicationIconService.GetShellIconPath(settings.Appearance.CustomIconPath);
        }

        public AppActionResult ImportApplicationIcon(string sourcePath)
        {
            try
            {
                string importedPath = ApplicationIconService.Import(sourcePath);
                settings.Appearance.CustomIconPath = importedPath;
                SaveSettings();
                ApplyApplicationIcon();
                return AppActionResult.Ok("Application icon updated.");
            }
            catch (Exception ex)
            {
                return AppActionResult.Fail(ex.Message);
            }
        }

        public AppActionResult ResetApplicationIcon()
        {
            try
            {
                ApplicationIconService.Reset();
                settings.Appearance.CustomIconPath = string.Empty;
                SaveSettings();
                ApplyApplicationIcon();
                return AppActionResult.Ok("Application icon restored to default.");
            }
            catch (Exception ex)
            {
                return AppActionResult.Fail(ex.Message);
            }
        }

        private void ApplyApplicationIcon()
        {
            DrawingIcon replacement = ApplicationIconService.LoadDrawingIcon(
                settings.Appearance.CustomIconPath);
            trayIcon.Icon = replacement;
            DrawingIcon previous = applicationIcon;
            applicationIcon = replacement;
            if (previous != null)
            {
                previous.Dispose();
            }

            StartupManager.RefreshIfConfigured(GetApplicationShellIconPath());
            if (settings.AppGuard.ExplorerIntegrationEnabled)
            {
                ExplorerIntegrationService.Enable(GetApplicationShellIconPath());
            }
            if (mainWindow != null)
            {
                mainWindow.ApplyApplicationIcon();
            }
        }

        public void ApplyAudioSettings()
        {
            audioModule.ApplySettings(settings.Audio);
            SaveSettings();
            UpdateTrayText();
        }

        public void ZeroAudioNow()
        {
            audioModule.ZeroNow("gui-manual");
        }

        public List<AudioAppVolume> GetAudioAppVolumes()
        {
            return audioMixerModule.GetAppVolumes();
        }

        public void PersistAudioMixerSettingsIfDirty()
        {
            if (audioMixerModule.ConsumeSettingsDirty())
            {
                SaveSettings();
            }
        }

        public void SetAudioAppVolume(string key, int volumePercent)
        {
            audioMixerModule.SetAppVolume(key, volumePercent);
            if (audioMixerModule.ConsumeSettingsDirty())
            {
                SaveSettings();
            }
        }

        public void SetAudioAppMute(string key, bool muted)
        {
            audioMixerModule.SetAppMute(key, muted);
        }

        public List<DeviceGuardDevice> GetDeviceGuardDevices()
        {
            return deviceGuardModule.GetDevices();
        }

        public List<CoreHardwareItem> GetCoreHardwareItems()
        {
            return deviceGuardModule.GetCoreHardware();
        }

        public InputStackSnapshot GetInputStackSnapshot()
        {
            return deviceGuardModule.GetInputStack();
        }

        public bool IsDeviceGuardScanning()
        {
            return deviceGuardModule.IsScanning;
        }

        public bool IsDeviceGuardRepairing()
        {
            return deviceGuardModule.IsRepairing;
        }

        public string GetDeviceGuardStatus()
        {
            return deviceGuardModule.StatusText;
        }

        public void RefreshDeviceGuard()
        {
            deviceGuardModule.RefreshAsync();
        }

        public void RepairDevice(string runtimeId)
        {
            deviceGuardModule.RepairAsync(runtimeId);
        }

        public void RepairAllDevices()
        {
            deviceGuardModule.RepairAllAsync();
        }

        public void RepairCoreHardware(string id, RepairRiskLevel authorizedRisk)
        {
            deviceGuardModule.RepairCoreAsync(id, authorizedRisk);
        }

        public void RepairAllCoreHardware()
        {
            deviceGuardModule.RepairAllCoreAsync();
        }

        public void CancelDeviceGuardRepair()
        {
            deviceGuardModule.CancelRepair();
        }

        public Task<HuaJuanCompatibilityResult> ApplyHuaJuanCompatibilityAsync()
        {
            return HuaJuanCompatibilityElevation.RunAsync(
                HuaJuanCompatibilityOperation.Apply);
        }

        public Task<HuaJuanCompatibilityResult> RestoreHuaJuanCompatibilityAsync()
        {
            return HuaJuanCompatibilityElevation.RunAsync(
                HuaJuanCompatibilityOperation.Restore);
        }

        public void SetShiftSpaceWidthToggleDisabled(bool disabled)
        {
            if (disabled)
            {
                keyboardModule.DisableShiftSpaceWidthToggle();
            }
            else
            {
                keyboardModule.RestoreShiftSpaceWidthToggle();
            }

            SaveSettings();
        }

        public async Task<bool> SetStickyKeysHotkeyDisabledAsync(bool disabled)
        {
            bool succeeded = await Task.Run(delegate
            {
                return disabled
                    ? keyboardModule.DisableStickyKeysHotkey()
                    : keyboardModule.RestoreStickyKeysHotkey();
            });
            if (succeeded)
            {
                SaveSettings();
            }
            return succeeded;
        }

        public void OpenTypingSettings()
        {
            keyboardModule.OpenTypingSettings();
        }

        public void OpenLanguageSettings()
        {
            keyboardModule.OpenLanguageSettings();
        }

        public List<GameHelperAppCandidate> GetGameHelperInstalledApps()
        {
            return gameHelperModule.GetInstalledAppCandidates();
        }

        public List<GameHelperProtectedApp> GetGameHelperProtectedApps()
        {
            return gameHelperModule.GetProtectedApps();
        }

        public bool IsGameHelperCrosshairEnabled()
        {
            return gameHelperModule.IsCrosshairEnabled();
        }

        public bool IsGameHelperPointerPrecisionGuardEnabled()
        {
            return gameHelperModule.IsPointerPrecisionGuardEnabled();
        }

        public CrosshairOptions GetGameHelperCrosshairOptions()
        {
            return gameHelperModule.GetCrosshairOptions();
        }

        public bool IsGameHelperCrosshairRestrictedToSelectedApps()
        {
            return gameHelperModule.IsCrosshairRestrictedToSelectedApps();
        }

        public List<CrosshairSelectedApp> GetGameHelperCrosshairSelectedApps()
        {
            return gameHelperModule.GetCrosshairSelectedApps();
        }

        public void SetGameHelperCrosshairEnabled(bool enabled)
        {
            gameHelperModule.SetCrosshairEnabled(enabled);
            SaveSettings();
        }

        public void SetGameHelperPointerPrecisionGuardEnabled(bool enabled)
        {
            gameHelperModule.SetPointerPrecisionGuardEnabled(enabled);
            SaveSettings();
        }

        public void SetGameHelperCrosshairRestrictToSelectedApps(bool restrict)
        {
            gameHelperModule.SetCrosshairRestrictToSelectedApps(restrict);
            SaveSettings();
        }

        public void AddGameHelperCrosshairSelectedApp(GameHelperAppCandidate candidate)
        {
            gameHelperModule.AddCrosshairSelectedApp(candidate);
            SaveSettings();
        }

        public void RemoveGameHelperCrosshairSelectedApp(string id)
        {
            gameHelperModule.RemoveCrosshairSelectedApp(id);
            SaveSettings();
        }

        public void ApplyGameHelperCrosshairOptions(CrosshairOptions options)
        {
            gameHelperModule.ApplyCrosshairOptions(options);
            SaveSettings();
        }

        public IDisposable PreviewGameHelperCrosshairOverlay()
        {
            return gameHelperModule.PreviewCrosshairOverlay();
        }

        public void AddGameHelperProtectedApp(GameHelperAppCandidate candidate)
        {
            gameHelperModule.AddProtectedApp(candidate);
            SaveSettings();
        }

        public void RemoveGameHelperProtectedApp(string id)
        {
            gameHelperModule.RemoveProtectedApp(id);
            SaveSettings();
        }

        public void UpdateGameHelperProtectedAppOptions(string id, bool blockWindowsKey,
            bool rightControlDShowsDesktop, bool lockMicrosoftEnglish,
            bool blockInputLanguageSwitch, bool restorePreviousInputLanguage)
        {
            gameHelperModule.UpdateProtectedAppOptions(id, blockWindowsKey,
                rightControlDShowsDesktop, lockMicrosoftEnglish,
                blockInputLanguageSwitch, restorePreviousInputLanguage);
            SaveSettings();
        }

        public void RefreshGameHelperInstalledApps()
        {
            gameHelperModule.RefreshInstalledAppCache();
        }

        public List<AppCatalogItem> GetLinkGuardInstalledApps()
        {
            return linkGuardModule.GetInstalledApps();
        }

        public List<LinkGuardRule> GetLinkGuardRules()
        {
            return linkGuardModule.GetRules();
        }

        public LinkGuardActionResult AddLinkGuardRule(AppCatalogItem trigger, AppCatalogItem linked)
        {
            LinkGuardActionResult result = linkGuardModule.AddRule(trigger, linked);
            if (result.Success) SaveSettings();
            return result;
        }

        public LinkGuardActionResult RemoveLinkGuardRule(string id)
        {
            LinkGuardActionResult result = linkGuardModule.RemoveRule(id);
            if (result.Success) SaveSettings();
            return result;
        }

        public LinkGuardActionResult UpdateLinkGuardRule(string id, LinkGuardMode mode,
            bool enabled, bool useGsudo, bool keepLinkedAppRunning,
            bool maintainLinkedAppRunning, int launchDelaySeconds)
        {
            LinkGuardActionResult result = linkGuardModule.UpdateRule(id, mode, enabled,
                useGsudo, keepLinkedAppRunning, maintainLinkedAppRunning, launchDelaySeconds);
            if (result.Success) SaveSettings();
            return result;
        }

        public LinkGuardActionResult StartLinkGuardGroup(string id)
        {
            return linkGuardModule.StartGroup(id);
        }

        public LinkGuardActionResult StopLinkGuardGroup(string id)
        {
            return linkGuardModule.StopGroup(id);
        }

        public void RefreshLinkGuardInstalledApps()
        {
            linkGuardModule.RefreshInstalledApps();
        }

        public List<AppCatalogItem> GetAppGuardApps()
        {
            return appGuardModule.GetApps();
        }

        public bool IsAppGuardLoading()
        {
            return appGuardModule.IsLoading;
        }

        public string GetAppGuardLastError()
        {
            return appGuardModule.LastError;
        }

        public void RefreshAppGuardApps()
        {
            appGuardModule.RefreshAsync();
        }

        public AppCatalogItem FindOrImportAppGuardTarget(string targetPath)
        {
            return appGuardModule.FindOrImport(targetPath);
        }

        public void LoadAppGuardDetailAsync(AppCatalogItem app, Action<AppCatalogDetail> callback)
        {
            appGuardModule.LoadDetailAsync(app, callback);
        }

        public void RefreshAppGuardDetailAsync(AppCatalogItem app, Action<AppCatalogDetail> callback)
        {
            appGuardModule.LoadDetailAsync(app, callback, true);
        }

        public bool TryGetAppGuardDetail(string appId, out AppCatalogDetail detail)
        {
            return appGuardModule.TryGetDetail(appId, out detail);
        }

        public void PrefetchAppGuardDetails(IList<AppCatalogItem> apps)
        {
            appGuardModule.PrefetchDetails(apps);
        }

        public void InvalidateAppGuardDetail(string appId)
        {
            appGuardModule.InvalidateDetail(appId);
        }

        public void CancelAppGuardDetailLoad()
        {
            appGuardModule.CancelDetailLoad();
        }

        public AppActionResult ExecuteAppGuardAction(AppCatalogItem app, AppActionType action)
        {
            if (action == AppActionType.CopyPath)
            {
                return appGuardModule.CopyPathToClipboard(app);
            }

            return appGuardModule.Execute(app, action);
        }

        public bool IsExplorerIntegrationEnabled()
        {
            return settings.AppGuard.ExplorerIntegrationEnabled;
        }

        public void SetExplorerIntegrationEnabled(bool enabled)
        {
            if (enabled)
            {
                ExplorerIntegrationService.Enable(GetApplicationShellIconPath());
            }
            else
            {
                ExplorerIntegrationService.Disable();
            }

            settings.AppGuard.ExplorerIntegrationEnabled = enabled;
            SaveSettings();
        }

        public DisplayGuardModeDefinition[] GetDisplayGuardModes()
        {
            return displayModule.Modes;
        }

        public string GetSelectedDisplayGuardMode()
        {
            return displayModule.SelectedMode;
        }

        public void SetSelectedDisplayGuardMode(string modeKey)
        {
            displayModule.SetSelectedMode(modeKey);
        }

        public List<DisplayGuardMonitorInfo> GetDisplayGuardMonitors()
        {
            return displayModule.GetMonitors();
        }

        public void RefreshDisplayGuard()
        {
            displayModule.RefreshAsync(false);
        }

        public bool TryAutomaticRefreshDisplayGuard()
        {
            return displayModule.TryAutomaticTopologyRefreshAsync();
        }

        public void ApplyDisplayGuardMode()
        {
            displayModule.ApplySelectedModeAsync();
        }

        public void SaveDisplayGuardMode()
        {
            displayModule.SaveCurrentToCustomMode();
        }

        public void SetDisplayGuardBrightness(string runtimeId, int percent)
        {
            displayModule.QueueBrightness(runtimeId, percent);
        }

        public void SetDisplayGuardContrast(string runtimeId, int percent)
        {
            displayModule.QueueContrast(runtimeId, percent);
        }

        public void SetAllDisplayGuardBrightness(int percent)
        {
            displayModule.QueueAllBrightness(percent);
        }

        public void SetAllDisplayGuardContrast(int percent)
        {
            displayModule.QueueAllContrast(percent);
        }

        public PowerGuardState GetPowerGuardState()
        {
            return powerGuardModule.GetState();
        }

        public bool SetPowerGuardEnabled(bool enabled, out string error)
        {
            return powerGuardModule.TrySetEnabled(enabled, out error);
        }

        public bool SetPowerGuardDisplayOn(bool enabled, out string error)
        {
            bool succeeded = powerGuardModule.TrySetKeepDisplayOn(enabled, out error);
            if (succeeded)
            {
                settings.PowerGuard.KeepDisplayOn = enabled;
                SaveSettings();
            }
            return succeeded;
        }

        public void SelectPowerGuardDuration(PowerGuardDurationKind kind, TimeSpan customDuration)
        {
            powerGuardModule.SelectDuration(kind, customDuration);
            SavePowerGuardDurationSettings();
        }

        private void SavePowerGuardDurationSettings()
        {
            PowerGuardState state = powerGuardModule.GetState();
            settings.PowerGuard.DurationKind = state.DurationKind.ToString();
            settings.PowerGuard.CustomDurationMinutes = (int)Math.Round(
                PowerGuardDurations.ClampCustom(state.CustomDuration).TotalMinutes);
            SaveSettings();
        }

        public void AdjustPowerGuardRemaining(TimeSpan remaining)
        {
            powerGuardModule.AdjustRemaining(remaining);
        }

        public void OpenPowerSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:powersleep",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLog.Write("Power Guard", "Opening Windows power settings failed: " + ex);
                System.Windows.MessageBox.Show("無法開啟 Windows 電源與睡眠設定：" + ex.Message,
                    "Power Guard", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void OpenLog()
        {
            try
            {
                if (!File.Exists(AppPaths.LogPath))
                {
                    File.WriteAllText(AppPaths.LogPath, string.Empty, Encoding.UTF8);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + AppPaths.LogPath + "\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Open log failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ClearLog()
        {
            try
            {
                File.WriteAllText(AppPaths.LogPath, string.Empty, Encoding.UTF8);
                if (mainWindow != null)
                {
                    mainWindow.RefreshStatus();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Clear log failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ShowMainWindow()
        {
            if (disposed)
            {
                return;
            }

            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                return;
            }

            if (mainWindow == null)
            {
                mainWindow = new MainWindow(this, settings, audioModule, audioMixerModule, deviceGuardModule,
                    keyboardModule, gameHelperModule, appGuardModule, linkGuardModule, displayModule,
                    vsrGuardModule, powerGuardModule);
                mainWindow.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
                {
                    if (!exiting)
                    {
                        e.Cancel = true;
                        mainWindow.Hide();
                    }
                };
                mainWindow.Closed += delegate { mainWindow = null; };
            }

            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            mainWindow.RestoreToForeground();
            mainWindow.Activate();
        }

        public void HandleAppActionRequest(AppActionRequest request)
        {
            if (disposed || request == null)
            {
                return;
            }

            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    HandleAppActionRequest(request);
                }));
                return;
            }

            ShowMainWindow();
            if (mainWindow != null)
            {
                mainWindow.HandleAppActionRequest(request);
            }
        }

        public void ExitApp()
        {
            if (disposed)
            {
                return;
            }

            exiting = true;
            SaveSettings();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            exiting = true;

            if (uacGuardAutoTimer != null)
            {
                uacGuardAutoTimer.Dispose();
                uacGuardAutoTimer = null;
            }

            // The elevated lifecycle host owns revocation. It observes this process ID and
            // clears the gsudo cache after the process exits, so shutdown must not block the
            // WPF dispatcher while waiting for an async child process.

            audioModule.Dispose();
            keyboardModule.Dispose();
            gameHelperModule.Dispose();
            appGuardModule.Dispose();
            linkGuardModule.Dispose();
            deviceGuardModule.Dispose();
            displayModule.Dispose();
            powerGuardModule.StateChanged -= PowerGuardModule_StateChanged;
            powerGuardModule.Dispose();
            if (appActionIpcServer != null)
            {
                appActionIpcServer.Dispose();
            }
            trayIcon.Visible = false;
            trayIcon.Dispose();
            applicationIcon.Dispose();

            if (mainWindow != null)
            {
                mainWindow.Close();
                mainWindow = null;
            }
        }

        private Forms.ContextMenuStrip BuildTrayMenu()
        {
            var menu = new Forms.ContextMenuStrip
            {
                ShowCheckMargin = true,
                ShowImageMargin = false
            };
            menu.Items.Add(new StableTrayMenuItem("Open Guard Center", delegate { ShowMainWindow(); }));
            var powerMenu = new StableTrayMenuItem("Power Guard");
            powerMenu.DropDownItems.Add(new StableTrayMenuItem("開啟 Power Guard 頁面", delegate
            {
                ShowMainWindow();
                if (mainWindow != null)
                {
                    mainWindow.ShowPowerGuard();
                }
            }));
            powerMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

            powerGuardEnabledMenuItem = new StableTrayMenuItem("保持清醒");
            powerGuardEnabledMenuItem.Click += delegate
            {
                PowerGuardState state = powerGuardModule.GetState();
                string error;
                bool succeeded = powerGuardModule.TrySetEnabled(!state.IsEnabled, out error);
                ShowPowerGuardTrayErrorIfNeeded(succeeded, error);
            };
            powerMenu.DropDownItems.Add(powerGuardEnabledMenuItem);

            powerGuardDisplayMenuItem = new StableTrayMenuItem("螢幕恆亮");
            powerGuardDisplayMenuItem.Click += delegate
            {
                PowerGuardState state = powerGuardModule.GetState();
                string error;
                bool succeeded = powerGuardModule.TrySetKeepDisplayOn(!state.KeepDisplayOn, out error);
                ShowPowerGuardTrayErrorIfNeeded(succeeded, error);
            };
            powerMenu.DropDownItems.Add(powerGuardDisplayMenuItem);
            powerMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

            powerGuardThirtyMinutesMenuItem = AddPowerGuardDurationMenuItem(powerMenu, "保持 30 分鐘",
                PowerGuardDurationKind.ThirtyMinutes);
            powerGuardOneHourMenuItem = AddPowerGuardDurationMenuItem(powerMenu, "保持 1 小時",
                PowerGuardDurationKind.OneHour);
            powerGuardTwoHoursMenuItem = AddPowerGuardDurationMenuItem(powerMenu, "保持 2 小時",
                PowerGuardDurationKind.TwoHours);
            powerGuardUntilManualMenuItem = AddPowerGuardDurationMenuItem(powerMenu, "直到手動關閉",
                PowerGuardDurationKind.UntilManual);

            powerMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            powerGuardRemainingMenuItem = new StableTrayMenuItem("目前：已關閉") { Enabled = false };
            powerMenu.DropDownItems.Add(powerGuardRemainingMenuItem);
            var powerDropDown = (Forms.ToolStripDropDownMenu)powerMenu.DropDown;
            powerDropDown.ShowCheckMargin = true;
            powerDropDown.ShowImageMargin = false;
            powerMenu.DropDownOpening += delegate
            {
                UpdatePowerGuardTrayMenu();
                StabilizeTrayMenuItemHeights(menu, powerDropDown);
            };
            powerDropDown.Closing += KeepTrayMenuOpenOnItemClick;
            menu.Items.Add(powerMenu);
            screenCrosshairMenuItem = new StableTrayMenuItem();
            screenCrosshairMenuItem.Click += delegate
            {
                SetGameHelperCrosshairEnabled(!gameHelperModule.IsCrosshairEnabled());
                UpdateScreenCrosshairTrayMenu();
            };
            menu.Items.Add(screenCrosshairMenuItem);
            menu.Items.Add(new StableTrayMenuItem("Zero playback devices now", delegate { ZeroAudioNow(); }));
            menu.Items.Add(new StableTrayMenuItem("Open Device Guard", delegate
            {
                ShowMainWindow();
                if (mainWindow != null)
                {
                    mainWindow.ShowDeviceGuard();
                }
            }));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new StableTrayMenuItem("Exit", delegate { ExitApp(); }));
            menu.Opening += delegate
            {
                UpdatePowerGuardTrayMenu();
                UpdateScreenCrosshairTrayMenu();
                StabilizeTrayMenuItemHeights(menu, powerDropDown);
            };
            menu.Closing += KeepTrayMenuOpenOnItemClick;
            UpdatePowerGuardTrayMenu();
            UpdateScreenCrosshairTrayMenu();
            StabilizeTrayMenuItemHeights(menu, powerDropDown);
            return menu;
        }

        internal static int StabilizeTrayMenuItemHeights(params Forms.ToolStripDropDownMenu[] menus)
        {
            int stableHeight = 0;
            for (int menuIndex = 0; menuIndex < menus.Length; menuIndex++)
            {
                Forms.ToolStripDropDownMenu menu = menus[menuIndex];
                for (int itemIndex = 0; itemIndex < menu.Items.Count; itemIndex++)
                {
                    if (menu.Items[itemIndex] is StableTrayMenuItem menuItem)
                    {
                        menuItem.StableHeight = 0;
                    }
                }
                menu.PerformLayout();
            }

            for (int menuIndex = 0; menuIndex < menus.Length; menuIndex++)
            {
                Forms.ToolStripDropDownMenu menu = menus[menuIndex];
                for (int itemIndex = 0; itemIndex < menu.Items.Count; itemIndex++)
                {
                    if (menu.Items[itemIndex] is StableTrayMenuItem menuItem)
                    {
                        stableHeight = Math.Max(stableHeight,
                            menuItem.GetPreferredSize(System.Drawing.Size.Empty).Height);
                    }
                }
            }

            stableHeight += stableHeight % 2;
            if (stableHeight == 0)
            {
                return 0;
            }

            for (int menuIndex = 0; menuIndex < menus.Length; menuIndex++)
            {
                Forms.ToolStripDropDownMenu menu = menus[menuIndex];
                for (int itemIndex = 0; itemIndex < menu.Items.Count; itemIndex++)
                {
                    if (menu.Items[itemIndex] is StableTrayMenuItem menuItem)
                    {
                        menuItem.StableHeight = stableHeight;
                    }
                }
                menu.PerformLayout();
            }

            return stableHeight;
        }

        internal static void KeepTrayMenuOpenOnItemClick(object sender,
            Forms.ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == Forms.ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        }

        private Forms.ToolStripMenuItem AddPowerGuardDurationMenuItem(Forms.ToolStripMenuItem parent,
            string text, PowerGuardDurationKind kind)
        {
            var item = new StableTrayMenuItem(text);
            item.Click += delegate
            {
                string error;
                bool succeeded = powerGuardModule.TryActivate(kind, TimeSpan.FromHours(2), out error);
                if (succeeded)
                {
                    SavePowerGuardDurationSettings();
                }
                ShowPowerGuardTrayErrorIfNeeded(succeeded, error);
            };
            parent.DropDownItems.Add(item);
            return item;
        }

        private void PowerGuardModule_StateChanged(object sender, EventArgs e)
        {
            if (disposed)
            {
                return;
            }
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                {
                    PowerGuardModule_StateChanged(sender, e);
                }));
                return;
            }

            UpdatePowerGuardTrayMenu();
            UpdateTrayText();
            if (mainWindow != null)
            {
                mainWindow.RefreshPowerGuardState();
            }
        }

        private void UpdatePowerGuardTrayMenu()
        {
            if (powerGuardEnabledMenuItem == null)
            {
                return;
            }

            PowerGuardState state = powerGuardModule.GetState();
            powerGuardEnabledMenuItem.Checked = state.IsEnabled;
            powerGuardDisplayMenuItem.Checked = state.KeepDisplayOn;
            powerGuardDisplayMenuItem.Enabled = state.IsEnabled;
            powerGuardThirtyMinutesMenuItem.Checked = state.DurationKind == PowerGuardDurationKind.ThirtyMinutes;
            powerGuardOneHourMenuItem.Checked = state.DurationKind == PowerGuardDurationKind.OneHour;
            powerGuardTwoHoursMenuItem.Checked = state.DurationKind == PowerGuardDurationKind.TwoHours;
            powerGuardUntilManualMenuItem.Checked = state.DurationKind == PowerGuardDurationKind.UntilManual;

            if (!state.IsEnabled)
            {
                powerGuardRemainingMenuItem.Text = "目前：已關閉";
            }
            else if (!state.HasTimer)
            {
                powerGuardRemainingMenuItem.Text = "目前：未設定結束時間";
            }
            else
            {
                powerGuardRemainingMenuItem.Text = "剩餘：" + FormatPowerGuardRemaining(state.Remaining.Value);
            }
        }

        private void UpdateScreenCrosshairTrayMenu()
        {
            if (screenCrosshairMenuItem == null)
            {
                return;
            }

            bool enabled = gameHelperModule.IsCrosshairEnabled();
            screenCrosshairMenuItem.Text = FormatScreenCrosshairTrayText(enabled);
            screenCrosshairMenuItem.Checked = enabled;
        }

        internal static string FormatScreenCrosshairTrayText(bool enabled)
        {
            return "Screen crosshair: " + (enabled ? "On" : "Off");
        }

        private void ShowPowerGuardTrayErrorIfNeeded(bool succeeded, string error)
        {
            if (succeeded || string.IsNullOrWhiteSpace(error))
            {
                return;
            }

            trayIcon.BalloonTipTitle = "Power Guard";
            trayIcon.BalloonTipText = error;
            trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Error;
            trayIcon.ShowBalloonTip(5000);
        }

        internal static string FormatPowerGuardRemaining(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return "0 秒";
            }

            long totalSeconds = Math.Max(1L, (long)Math.Ceiling(remaining.TotalSeconds));
            long days = totalSeconds / 86400;
            long hours = (totalSeconds % 86400) / 3600;
            long minutes = (totalSeconds % 3600) / 60;
            long seconds = totalSeconds % 60;
            if (days > 0)
            {
                return days + " 天 " + hours + " 小時 " + minutes + " 分 " + seconds + " 秒";
            }
            if (hours > 0)
            {
                return hours + " 小時 " + minutes + " 分 " + seconds + " 秒";
            }
            if (minutes > 0)
            {
                return minutes + " 分 " + seconds + " 秒";
            }
            return seconds + " 秒";
        }

        internal static string FormatPowerGuardDuration(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return "0 分鐘";
            }

            long totalMinutes = Math.Max(1L, (long)Math.Ceiling(duration.TotalMinutes));
            long days = totalMinutes / 1440;
            long hours = (totalMinutes % 1440) / 60;
            long minutes = totalMinutes % 60;
            var parts = new List<string>();
            if (days > 0)
            {
                parts.Add(days + " 天");
            }
            if (hours > 0)
            {
                parts.Add(hours + " 小時");
            }
            if (minutes > 0)
            {
                parts.Add(minutes + " 分鐘");
            }
            return parts.Count == 0 ? "0 分鐘" : string.Join(" ", parts);
        }

        internal sealed class StableTrayMenuItem : Forms.ToolStripMenuItem
        {
            private int stableHeight;

            public StableTrayMenuItem()
            {
            }

            public StableTrayMenuItem(string text)
                : base(text)
            {
            }

            public StableTrayMenuItem(string text, EventHandler onClick)
                : base(text, null, onClick)
            {
            }

            internal int StableHeight
            {
                get { return stableHeight; }
                set { stableHeight = Math.Max(0, value); }
            }

            public override System.Drawing.Size GetPreferredSize(System.Drawing.Size constrainingSize)
            {
                System.Drawing.Size preferredSize = base.GetPreferredSize(constrainingSize);
                preferredSize.Height = Math.Max(preferredSize.Height, stableHeight);
                return preferredSize;
            }
        }

        private void UpdateTrayText()
        {
            PowerGuardState powerState = powerGuardModule.GetState();
            string text = powerState.IsEnabled
                ? "Guard Center - Power Guard ON"
                : "Guard Center - Audio Guard " + (settings.Audio.Enabled ? "ON" : "OFF");
            if (text.Length > 63)
            {
                text = text.Substring(0, 63);
            }

            trayIcon.Text = text;
        }
    }

    public partial class MainWindow
    {
        private static readonly Brush TransparentBrush = Brushes.Transparent;
        private static readonly Brush WindowBrush = BrushFromRgb(0x0F, 0x12, 0x19);
        private static readonly Brush CardBrush = BrushFromRgb(0x18, 0x1E, 0x2A);
        private static readonly Brush CardDisabledBrush = BrushFromRgb(0x15, 0x1B, 0x25);
        private static readonly Brush CardBorderBrush = BrushFromRgb(0x2A, 0x34, 0x46);
        private static readonly Brush AccentBrush = BrushFromRgb(0x2D, 0x7D, 0xFF);
        private static readonly Brush TextBrush = BrushFromRgb(0xF4, 0xF7, 0xFB);
        private static readonly Brush MutedBrush = BrushFromRgb(0xA7, 0xB4, 0xC8);
        private static readonly Brush SubtleBrush = BrushFromRgb(0x7D, 0x8A, 0xA0);
        private static readonly Brush NavIconBrush = BrushFromRgb(0x71, 0xA7, 0xFF);
        private static readonly Brush NavIconTileBrush = BrushFromRgb(0x17, 0x24, 0x3A);
        private static readonly Brush NavIconTileBorderBrush = BrushFromRgb(0x29, 0x42, 0x63);

        private readonly GuardCenterController controller;
        private readonly AppSettings settings;
        private readonly AudioZeroModule audioModule;
        private readonly AudioMixerModule audioMixerModule;
        private readonly DeviceGuardModule deviceGuardModule;
        private readonly KeyboardGuardModule keyboardModule;
        private readonly GameHelperModule gameHelperModule;
        private readonly AppGuardModule appGuardModule;
        private readonly LinkGuardModule linkGuardModule;
        private readonly DisplayGuardModule displayModule;
        private readonly VsrGuardModule vsrGuardModule;
        private readonly PowerGuardModule powerGuardModule;
        private readonly UacGuardModule uacGuardModule;
        private readonly List<DependentCard> audioDependentCards = new List<DependentCard>();
        private Window activeCrosshairSettingsDialog;
        private Window activeUacGuardHelpDialog;
        private TextBox uacGuardHelpLogBox;
        private UiControls.ToggleSwitch crosshairEnabledToggle;
        private UiControls.ToggleSwitch crosshairRestrictionToggle;
        private bool crosshairUiSyncing;
        private ComboBox displayModeComboBox;
        private UiControls.ToggleSwitch powerGuardEnabledToggle;
        private UiControls.ToggleSwitch powerGuardDisplayToggle;
        private ComboBox powerGuardDurationComboBox;
        private Slider powerGuardTimelineSlider;
        private TextBlock powerGuardTimelineMaximumText;
        private TextBlock powerGuardRemainingText;
        private TextBlock powerGuardEndText;
        private TextBlock powerGuardErrorText;
        private PowerGuardDurationKind powerGuardRenderedDurationKind;
        private bool powerGuardDragging;
        private bool powerGuardUiSyncing;
        private string crosshairAppSearchText = string.Empty;
        private string crosshairSelectedAppSearchText = string.Empty;
        private string gameHelperSearchText = string.Empty;
        private string gameHelperProtectedSearchText = string.Empty;
        private string appGuardSearchText = string.Empty;
        private string appGuardExpandedAppId = string.Empty;
        private AppCatalogDetail appGuardExpandedDetail;
        private AppGuardCardView appGuardExpandedCard;
        private FrameworkElement appGuardScrollTailSpacer;
        private StackPanel appGuardListHost;
        private List<AppCatalogItem> appGuardLoadedApps;
        private EventHandler appGuardScrollAnimationHandler;
        private readonly Dictionary<string, ImageSource> appGuardIconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<ImageSource>> appGuardIconTasks =
            new Dictionary<string, Task<ImageSource>>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim appGuardIconLoadGate = new SemaphoreSlim(1, 1);
        private readonly ProgressiveListState<AudioAppVolume> audioMixerListState;
        private readonly ProgressiveListState<AppCatalogItem> appGuardListState;
        private readonly ProgressiveListState<GameHelperAppCandidate> crosshairAppsListState;
        private readonly ProgressiveListState<CrosshairSelectedApp> crosshairSelectedAppsListState;
        private readonly ProgressiveListState<GameHelperAppCandidate> gameHelperAddAppsListState;
        private readonly ProgressiveListState<GameHelperProtectedApp> gameHelperProtectedAppsListState;
        private readonly ProgressiveListState<DisplayGuardMonitorInfo> displayMonitorListState;
        private ProgressiveListState<DeviceGuardDevice> detectableDevicesListState;
        private ProgressiveListState<CoreHardwareItem> coreHardwareListState;
        private InlineProgressiveListPresenter<AudioAppVolume> audioMixerPresenter;
        private InlineProgressiveListPresenter<AppCatalogItem> appGuardPresenter;
        private ProgressiveListPresenter<DisplayGuardMonitorInfo> displayMonitorPresenter;
        private const string ModuleDragDataFormat = "GuardCenter.ModuleNavigationId";
        private readonly Dictionary<string, Button> moduleNavigationButtons =
            new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> moduleNavigationHandles =
            new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer moduleAutoScrollTimer;
        private DispatcherTimer focusedRefreshTimer;
        private Border moduleInsertionIndicator;
        private Button moduleDraggedButton;
        private string moduleDraggedId = string.Empty;
        private Point moduleDragStartPoint;
        private bool moduleDragPending;
        private bool moduleOrderEditMode;
        private string audioRefreshFingerprint = string.Empty;
        private string latestAudioRefreshFingerprint = string.Empty;
        private string displayRefreshFingerprint = string.Empty;
        private List<AudioAppVolume> latestAudioRefreshSnapshot;
        private int audioRefreshInFlight;
        private bool audioRefreshClosed;
        private int appGuardRenderVersion;
        private int moduleDropIndex = -1;
        private int moduleAutoScrollDirection;
        private double displayListRestoreOffset;
        private bool syncing;
        private bool suppressDisplayModeSelectionChanged;
        private int selectedModuleIndex;
        private UacGuardStatus uacGuardStatus;
        private int uacGuardRefreshVersion;
        private bool uacGuardBusy;
        private string uacGuardBusyOperation = string.Empty;
        private VsrGuardSnapshot vsrGuardSnapshot;
        private bool vsrGuardBusy;
        private string vsrGuardLastMessage = string.Empty;
        private bool vsrGuardLastActionSucceeded;
        private readonly List<string> uacGuardLog = new List<string>();
        private HwndSource hwndSource;

        internal MainWindow(GuardCenterController controller, AppSettings settings,
            AudioZeroModule audioModule, AudioMixerModule audioMixerModule,
            DeviceGuardModule deviceGuardModule,
            KeyboardGuardModule keyboardModule,
            GameHelperModule gameHelperModule,
            AppGuardModule appGuardModule,
            LinkGuardModule linkGuardModule,
            DisplayGuardModule displayModule,
            VsrGuardModule vsrGuardModule,
            PowerGuardModule powerGuardModule)
        {
            this.controller = controller;
            this.settings = settings;
            this.audioModule = audioModule;
            this.audioMixerModule = audioMixerModule;
            this.deviceGuardModule = deviceGuardModule;
            this.keyboardModule = keyboardModule;
            this.gameHelperModule = gameHelperModule;
            this.appGuardModule = appGuardModule;
            this.linkGuardModule = linkGuardModule;
            this.displayModule = displayModule;
            this.vsrGuardModule = vsrGuardModule;
            this.powerGuardModule = powerGuardModule;
            uacGuardModule = new UacGuardModule();
            audioMixerListState = new ProgressiveListState<AudioAppVolume>(settings.AudioMixer.AppsList,
                delegate(AudioAppVolume item) { return item.Key; });
            appGuardListState = new ProgressiveListState<AppCatalogItem>(settings.AppGuard.AppsList,
                delegate(AppCatalogItem item) { return item.Id; });
            crosshairAppsListState = new ProgressiveListState<GameHelperAppCandidate>(
                settings.GameHelper.CrosshairAppsList,
                delegate(GameHelperAppCandidate item) { return item.Id; });
            crosshairSelectedAppsListState = new ProgressiveListState<CrosshairSelectedApp>(
                settings.GameHelper.CrosshairAppsList,
                delegate(CrosshairSelectedApp item) { return item.Id; });
            gameHelperAddAppsListState = new ProgressiveListState<GameHelperAppCandidate>(settings.GameHelper.AddAppsList,
                delegate(GameHelperAppCandidate item) { return item.Id; });
            gameHelperProtectedAppsListState = new ProgressiveListState<GameHelperProtectedApp>(settings.GameHelper.ProtectedAppsList,
                delegate(GameHelperProtectedApp item) { return item.Id; });
            displayMonitorListState = new ProgressiveListState<DisplayGuardMonitorInfo>(settings.DisplayGuard.MonitorsList,
                delegate(DisplayGuardMonitorInfo item) { return item.RuntimeId; });
            detectableDevicesListState = new ProgressiveListState<DeviceGuardDevice>(
                settings.DeviceGuard.DetectableDevicesList, delegate(DeviceGuardDevice item) { return item.RuntimeId; });
            coreHardwareListState = new ProgressiveListState<CoreHardwareItem>(
                settings.DeviceGuard.CoreHardwareList, delegate(CoreHardwareItem item) { return item.Id; });

            InitializeComponent();
            InitializeModuleNavigationOrdering();
            InitializeFocusedRefreshPolling();
            SetWindowIcon();
            ApplySavedSidebarWidth();
            ContentScrollViewer.SizeChanged += delegate { UpdateContentWidth(); };
            ApplyUiScale();
            UpdateContentWidth();

            Loaded += delegate
            {
                QueueEnsureWindowOnScreen();
                UpdateFocusedRefreshPolling();
            };
            Activated += delegate { UpdateFocusedRefreshPolling(); };
            Deactivated += delegate { UpdateFocusedRefreshPolling(); };
            IsVisibleChanged += delegate { UpdateFocusedRefreshPolling(); };
            StateChanged += delegate
            {
                QueueEnsureWindowOnScreen();
                UpdateFocusedRefreshPolling();
            };

            audioModule.StatusChanged += Module_StatusChanged;
            audioMixerModule.StatusChanged += Module_StatusChanged;
            deviceGuardModule.StatusChanged += Module_StatusChanged;
            deviceGuardModule.DevicesChanged += DeviceGuardModule_DevicesChanged;
            deviceGuardModule.CoreHardwareChanged += DeviceGuardModule_DevicesChanged;
            deviceGuardModule.InputStackChanged += DeviceGuardModule_DevicesChanged;
            keyboardModule.StatusChanged += Module_StatusChanged;
            gameHelperModule.StatusChanged += Module_StatusChanged;
            appGuardModule.StatusChanged += Module_StatusChanged;
            appGuardModule.AppsChanged += AppGuardModule_AppsChanged;
            displayModule.StatusChanged += Module_StatusChanged;
            displayModule.DisplaysChanged += DisplayModule_DisplaysChanged;

            SelectModule(0);
            RefreshStatus();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (hwndSource != null)
            {
                hwndSource.AddHook(WindowProc);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            audioRefreshClosed = true;
            CancelAppGuardScrollAnimation();
            CleanupModuleDrag();
            if (moduleAutoScrollTimer != null)
            {
                moduleAutoScrollTimer.Stop();
            }
            if (focusedRefreshTimer != null)
            {
                focusedRefreshTimer.Stop();
            }

            if (hwndSource != null)
            {
                hwndSource.RemoveHook(WindowProc);
                hwndSource = null;
            }

            audioModule.StatusChanged -= Module_StatusChanged;
            audioMixerModule.StatusChanged -= Module_StatusChanged;
            deviceGuardModule.StatusChanged -= Module_StatusChanged;
            deviceGuardModule.DevicesChanged -= DeviceGuardModule_DevicesChanged;
            deviceGuardModule.CoreHardwareChanged -= DeviceGuardModule_DevicesChanged;
            deviceGuardModule.InputStackChanged -= DeviceGuardModule_DevicesChanged;
            CloseDeviceGuardWindows();
            keyboardModule.StatusChanged -= Module_StatusChanged;
            gameHelperModule.StatusChanged -= Module_StatusChanged;
            appGuardModule.StatusChanged -= Module_StatusChanged;
            appGuardModule.AppsChanged -= AppGuardModule_AppsChanged;
            displayModule.StatusChanged -= Module_StatusChanged;
            displayModule.DisplaysChanged -= DisplayModule_DisplaysChanged;
            DisposeMainListPresenters();
            base.OnClosed(e);
        }

        internal void RestoreToForeground()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            ShowActivated = true;
            QueueEnsureWindowOnScreen();
            Activate();
        }

        internal void ShowDeviceGuard()
        {
            SelectModule(1);
        }

        internal void ShowPowerGuard()
        {
            SelectModule(6);
        }

        public void RefreshStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshStatus));
                return;
            }

            StatusTextBlock.Text = GetSelectedModuleStatusText();

            syncing = true;
            HeaderEnabledSwitch.IsChecked = settings.Audio.Enabled;
            syncing = false;
        }

        private void AudioNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(0);
        }

        private void DeviceGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(1);
        }

        private void KeyboardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(2);
        }

        private void GameHelperNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(3);
        }

        private void AppGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(4);
        }

        private void LinkGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(8);
        }

        private void DisplayGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(5);
        }

        private void VsrGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(9);
        }

        private void PowerGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(6);
        }

        private void UacGuardNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(7);
        }

        private void HeaderHelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedModuleIndex == 7)
            {
                ShowUacGuardHelp();
            }
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            SelectModule(-1);
        }

        private void InitializeFocusedRefreshPolling()
        {
            focusedRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(FocusedRefreshPolicy.IntervalMilliseconds)
            };
            focusedRefreshTimer.Tick += FocusedRefreshTimer_Tick;
        }

        private void UpdateFocusedRefreshPolling()
        {
            if (focusedRefreshTimer == null)
            {
                return;
            }

            bool shouldPoll = FocusedRefreshPolicy.ShouldPoll(IsVisible, IsMainWindowForeground(),
                WindowState == WindowState.Minimized);
            if (shouldPoll)
            {
                if (!focusedRefreshTimer.IsEnabled)
                {
                    if (FocusedRefreshPolicy.ShouldRefreshDisplayOnFocus(IsVisible,
                        IsMainWindowForeground(), WindowState == WindowState.Minimized,
                        selectedModuleIndex == 5))
                    {
                        controller.TryAutomaticRefreshDisplayGuard();
                    }
                    focusedRefreshTimer.Start();
                }
            }
            else
            {
                focusedRefreshTimer.Stop();
            }
        }

        private void FocusedRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (!FocusedRefreshPolicy.ShouldPoll(IsVisible, IsMainWindowForeground(),
                WindowState == WindowState.Minimized))
            {
                UpdateFocusedRefreshPolling();
                return;
            }

            PollAudioGuardSnapshot();
        }

        private bool IsMainWindowForeground()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private async void PollAudioGuardSnapshot()
        {
            if (Interlocked.Exchange(ref audioRefreshInFlight, 1) != 0)
            {
                return;
            }

            try
            {
                List<AudioAppVolume> apps = await Task.Run(new Func<List<AudioAppVolume>>(
                    controller.GetAudioAppVolumes));
                if (audioRefreshClosed)
                {
                    return;
                }

                controller.PersistAudioMixerSettingsIfDirty();
                ApplyAudioGuardSnapshot(apps);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Audio polling refresh failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref audioRefreshInFlight, 0);
            }
        }

        private void ApplyAudioGuardSnapshot(List<AudioAppVolume> apps)
        {
            apps = apps ?? new List<AudioAppVolume>();
            string fingerprint = FocusedRefreshPolicy.GetAudioFingerprint(apps);
            latestAudioRefreshSnapshot = apps;
            latestAudioRefreshFingerprint = fingerprint;

            if (selectedModuleIndex != 0 || SettingsStack.IsMouseCaptureWithin)
            {
                return;
            }

            if (string.Equals(audioRefreshFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            audioRefreshFingerprint = fingerprint;
            if (audioMixerPresenter != null && apps.Count > 0)
            {
                audioMixerPresenter.Reset(apps, null, null);
            }
            else
            {
                RenderAudioPage();
            }
        }

        private void InitializeModuleNavigationOrdering()
        {
            RegisterModuleNavigationButton(ModuleNavigationOrder.Audio, AudioNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.Device, DeviceGuardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.Keyboard, KeyboardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.GameHelper, GameHelperNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.AppGuard, AppGuardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.LinkGuard, LinkGuardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.DisplayGuard, DisplayGuardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.VsrGuard, VsrGuardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.PowerGuard, PowerGuardNavButton);
            RegisterModuleNavigationButton(ModuleNavigationOrder.UacGuard, UacGuardNavButton);

            ApplySavedModuleNavigationOrder();
            SidebarModulesScrollViewer.AllowDrop = true;
            SidebarModulesScrollViewer.Background = Brushes.Transparent;
            SidebarModulesScrollViewer.PreviewDragOver += SidebarModulesScrollViewer_PreviewDragOver;
            SidebarModulesScrollViewer.PreviewDrop += SidebarModulesScrollViewer_PreviewDrop;
            SidebarModulesScrollViewer.PreviewDragLeave += SidebarModulesScrollViewer_PreviewDragLeave;

            moduleAutoScrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            moduleAutoScrollTimer.Tick += delegate
            {
                if (moduleDraggedButton == null || moduleAutoScrollDirection == 0)
                {
                    StopModuleAutoScroll();
                    return;
                }

                double next = SidebarModulesScrollViewer.VerticalOffset + moduleAutoScrollDirection * 10;
                next = Math.Max(0, Math.Min(SidebarModulesScrollViewer.ScrollableHeight, next));
                SidebarModulesScrollViewer.ScrollToVerticalOffset(next);
                SidebarModulesScrollViewer.UpdateLayout();
                UpdateModuleDropPosition(Mouse.GetPosition(SidebarModulesPanel));
            };

            SetModuleOrderEditMode(false);
        }

        private void RegisterModuleNavigationButton(string id, Button button)
        {
            moduleNavigationButtons[id] = button;
            button.Tag = id;

            UIElement content = button.Content as UIElement;
            if (content == null)
            {
                return;
            }

            button.Content = null;
            var wrapper = new Grid();
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(content, 0);
            wrapper.Children.Add(content);

            var handle = new Border
            {
                Width = 30,
                Visibility = Visibility.Collapsed,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to reorder modules",
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = "⋮⋮",
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 14,
                    Foreground = SubtleBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            moduleNavigationHandles[id] = handle;
            Grid.SetColumn(handle, 1);
            wrapper.Children.Add(handle);
            button.Content = wrapper;

            handle.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (!moduleOrderEditMode)
                {
                    return;
                }

                moduleDragPending = true;
                moduleDragStartPoint = e.GetPosition(this);
                moduleDraggedButton = button;
                moduleDraggedId = id;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!moduleOrderEditMode || !moduleDragPending || moduleDraggedButton != button
                    || e.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                Point current = e.GetPosition(this);
                if (Math.Abs(current.X - moduleDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(current.Y - moduleDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                moduleDragPending = false;
                handle.ReleaseMouseCapture();
                moduleDraggedButton.Opacity = 0.55;
                try
                {
                    var data = new DataObject();
                    data.SetData(ModuleDragDataFormat, moduleDraggedId);
                    DragDrop.DoDragDrop(handle, data, DragDropEffects.Move);
                }
                finally
                {
                    CleanupModuleDrag();
                }
                e.Handled = true;
            };
            handle.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                handle.ReleaseMouseCapture();
                moduleDragPending = false;
                if (moduleDraggedButton == button)
                {
                    moduleDraggedButton = null;
                    moduleDraggedId = string.Empty;
                }
                e.Handled = true;
            };
        }

        private void ModuleOrderEditButton_Click(object sender, RoutedEventArgs e)
        {
            SetModuleOrderEditMode(!moduleOrderEditMode);
        }

        private void SetModuleOrderEditMode(bool enabled)
        {
            moduleOrderEditMode = enabled;
            if (!enabled)
            {
                CleanupModuleDrag();
            }

            SidebarModulesScrollViewer.AllowDrop = enabled;
            foreach (Border handle in moduleNavigationHandles.Values)
            {
                handle.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            }

            ModuleOrderEditGlyph.Text = enabled ? "\u2713" : "\u2195";
            ModuleOrderEditButton.ToolTip = enabled
                ? "Finish adjusting module order"
                : "Adjust module order";
            ModuleOrderEditButton.Background = enabled ? CardBrush : TransparentBrush;
            ModuleOrderEditButton.BorderBrush = enabled ? AccentBrush : TransparentBrush;
        }

        private void ApplySavedModuleNavigationOrder()
        {
            List<string> order = ModuleNavigationOrder.Parse(settings.Layout.ModuleOrder);
            settings.Layout.ModuleOrder = string.Join(",", order.ToArray());
            for (int i = 0; i < order.Count; i++)
            {
                Button button;
                if (moduleNavigationButtons.TryGetValue(order[i], out button))
                {
                    SidebarModulesPanel.Children.Remove(button);
                    SidebarModulesPanel.Children.Add(button);
                }
            }
            UpdateModuleNavigationMargins();
        }

        private void SidebarModulesScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!IsModuleNavigationDrag(e.Data))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Move;
            UpdateModuleDropPosition(e.GetPosition(SidebarModulesPanel));
            UpdateModuleAutoScroll(e.GetPosition(SidebarModulesScrollViewer));
            e.Handled = true;
        }

        private void SidebarModulesScrollViewer_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!IsModuleNavigationDrag(e.Data) || moduleDraggedButton == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            UpdateModuleDropPosition(e.GetPosition(SidebarModulesPanel));
            List<Button> ordered = GetOrderedModuleButtons(moduleDraggedButton);
            int insertAt = Clamp(moduleDropIndex, 0, ordered.Count);
            ordered.Insert(insertAt, moduleDraggedButton);
            RemoveModuleInsertionIndicator();

            for (int i = 0; i < ordered.Count; i++)
            {
                SidebarModulesPanel.Children.Remove(ordered[i]);
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                SidebarModulesPanel.Children.Add(ordered[i]);
            }
            UpdateModuleNavigationMargins();

            var ids = new List<string>();
            for (int i = 0; i < ordered.Count; i++)
            {
                ids.Add(Convert.ToString(ordered[i].Tag));
            }
            string nextOrder = ModuleNavigationOrder.Normalize(string.Join(",", ids.ToArray()));
            if (!string.Equals(settings.Layout.ModuleOrder, nextOrder, StringComparison.OrdinalIgnoreCase))
            {
                settings.Layout.ModuleOrder = nextOrder;
                controller.SaveSettings();
                StatusTextBlock.Text = "Module order saved.";
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            CleanupModuleDrag();
        }

        private void SidebarModulesScrollViewer_PreviewDragLeave(object sender, DragEventArgs e)
        {
            Point position = e.GetPosition(SidebarModulesScrollViewer);
            if (position.X >= 0 && position.X <= SidebarModulesScrollViewer.ActualWidth
                && position.Y >= 0 && position.Y <= SidebarModulesScrollViewer.ActualHeight)
            {
                return;
            }

            StopModuleAutoScroll();
            RemoveModuleInsertionIndicator();
        }

        private bool IsModuleNavigationDrag(IDataObject data)
        {
            if (!moduleOrderEditMode || data == null || !data.GetDataPresent(ModuleDragDataFormat)
                || moduleDraggedButton == null)
            {
                return false;
            }

            string id = Convert.ToString(data.GetData(ModuleDragDataFormat));
            return !string.IsNullOrWhiteSpace(id)
                && string.Equals(id, moduleDraggedId, StringComparison.OrdinalIgnoreCase)
                && moduleNavigationButtons.ContainsKey(id);
        }

        private void UpdateModuleDropPosition(Point position)
        {
            if (moduleDraggedButton == null)
            {
                return;
            }

            List<Button> candidates = GetOrderedModuleButtons(moduleDraggedButton);
            var centers = new List<double>();
            for (int i = 0; i < candidates.Count; i++)
            {
                Point top = candidates[i].TranslatePoint(new Point(0, 0), SidebarModulesPanel);
                centers.Add(top.Y + candidates[i].ActualHeight / 2);
            }
            int target = ModuleNavigationOrder.GetInsertionIndex(position.Y, centers);

            if (moduleDropIndex == target && moduleInsertionIndicator != null
                && moduleInsertionIndicator.Parent == SidebarModulesPanel)
            {
                return;
            }

            moduleDropIndex = target;
            ShowModuleInsertionIndicator(candidates, target);
        }

        private void ShowModuleInsertionIndicator(List<Button> candidates, int target)
        {
            RemoveModuleInsertionIndicator();
            if (moduleInsertionIndicator == null)
            {
                moduleInsertionIndicator = new Border
                {
                    Height = 3,
                    Margin = new Thickness(4, 0, 4, 5),
                    Background = AccentBrush,
                    CornerRadius = new CornerRadius(2),
                    IsHitTestVisible = false
                };
            }

            int childIndex = SidebarModulesPanel.Children.Count;
            if (target >= 0 && target < candidates.Count)
            {
                childIndex = SidebarModulesPanel.Children.IndexOf(candidates[target]);
            }
            SidebarModulesPanel.Children.Insert(Math.Max(0, childIndex), moduleInsertionIndicator);
        }

        private void RemoveModuleInsertionIndicator()
        {
            if (moduleInsertionIndicator != null && moduleInsertionIndicator.Parent == SidebarModulesPanel)
            {
                SidebarModulesPanel.Children.Remove(moduleInsertionIndicator);
            }
        }

        private List<Button> GetOrderedModuleButtons(Button excluded)
        {
            var result = new List<Button>();
            for (int i = 0; i < SidebarModulesPanel.Children.Count; i++)
            {
                Button button = SidebarModulesPanel.Children[i] as Button;
                if (button != null && button != excluded && moduleNavigationButtons.ContainsValue(button))
                {
                    result.Add(button);
                }
            }
            return result;
        }

        private void UpdateModuleNavigationMargins()
        {
            List<Button> buttons = GetOrderedModuleButtons(null);
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].Margin = new Thickness(0, i == 0 ? 8 : 0, 0, i == buttons.Count - 1 ? 0 : 8);
            }
        }

        private void UpdateModuleAutoScroll(Point position)
        {
            double threshold = 48;
            int direction = ModuleNavigationOrder.GetAutoScrollDirection(position.Y,
                SidebarModulesScrollViewer.ActualHeight, SidebarModulesScrollViewer.VerticalOffset,
                SidebarModulesScrollViewer.ScrollableHeight, threshold);

            moduleAutoScrollDirection = direction;
            if (direction == 0)
            {
                StopModuleAutoScroll();
            }
            else if (moduleAutoScrollTimer != null && !moduleAutoScrollTimer.IsEnabled)
            {
                moduleAutoScrollTimer.Start();
            }
        }

        private void StopModuleAutoScroll()
        {
            moduleAutoScrollDirection = 0;
            if (moduleAutoScrollTimer != null)
            {
                moduleAutoScrollTimer.Stop();
            }
        }

        private void CleanupModuleDrag()
        {
            StopModuleAutoScroll();
            RemoveModuleInsertionIndicator();
            if (moduleDraggedButton != null)
            {
                moduleDraggedButton.Opacity = 1.0;
            }
            moduleDraggedButton = null;
            moduleDraggedId = string.Empty;
            moduleDragPending = false;
            moduleDropIndex = -1;
        }

        private void HeaderEnabledSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (syncing || selectedModuleIndex != 0)
            {
                return;
            }

            SetAudioEnabled(HeaderEnabledSwitch.IsChecked == true);
        }

        private void RootGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FindSliderAncestor(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            int next = Clamp(settings.Layout.UiScalePercent + (e.Delta > 0 ? 5 : -5), 85, 135);
            if (next != settings.Layout.UiScalePercent)
            {
                settings.Layout.UiScalePercent = next;
                ApplyUiScale();
                RenderSelectedModule();
                controller.SaveSettings();
                StatusTextBlock.Text = "UI scale " + next + "%";
            }

            e.Handled = true;
        }

        private static Slider FindSliderAncestor(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null)
            {
                var slider = current as Slider;
                if (slider != null)
                {
                    return slider;
                }

                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return null;
        }

        private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            int next = Clamp((int)Math.Round(SidebarColumn.ActualWidth), 240, 620);
            if (settings.Layout.SidebarWidth != next)
            {
                settings.Layout.SidebarWidth = next;
                controller.SaveSettings();
                StatusTextBlock.Text = "Sidebar width " + next;
            }

            SidebarColumn.Width = new GridLength(next);
            UpdateContentWidth();
        }

        private void SelectModule(int index)
        {
            CancelAppGuardScrollAnimation();
            int previousIndex = selectedModuleIndex;
            if (previousIndex != index)
            {
                ResetAllSearchState();
            }
            bool refreshDisplayOnNavigation = FocusedRefreshPolicy.ShouldRefreshDisplayOnNavigation(
                previousIndex == 5, index == 5);
            if (index != 0 && audioMixerPresenter != null)
            {
                audioMixerPresenter.Dispose();
                audioMixerPresenter = null;
            }
            if (index != 4 && appGuardPresenter != null)
            {
                appGuardPresenter.Dispose();
                appGuardPresenter = null;
            }
            if (index != 5 && displayMonitorPresenter != null)
            {
                displayMonitorPresenter.Dispose();
                displayMonitorPresenter = null;
            }
            selectedModuleIndex = index;
            UpdateNavigationState();

            if (index == -1)
            {
                RenderSettingsPage();
            }
            else if (index == 0)
            {
                RenderAudioPage();
                PollAudioGuardSnapshot();
            }
            else if (index == 1)
            {
                RenderDeviceGuardPage();
            }
            else if (index == 2)
            {
                RenderKeyboardPage();
            }
            else if (index == 3)
            {
                RenderGameHelperPage();
            }
            else if (index == 4)
            {
                RenderAppGuardPage();
            }
            else if (index == 5)
            {
                RenderDisplayGuardPage();
                if (refreshDisplayOnNavigation)
                {
                    controller.TryAutomaticRefreshDisplayGuard();
                }
            }
            else if (index == 6)
            {
                RenderPowerGuardPage();
            }
            else if (index == 7)
            {
                RenderUacGuardPage();
            }
            else if (index == 8)
            {
                RenderLinkGuardPage();
            }
            else if (index == 9)
            {
                RenderVsrGuardPage();
            }

            ContentScrollViewer.ScrollToTop();
            RefreshStatus();
        }

        private void ResetAllSearchState()
        {
            ResetCrosshairSearchState();
            ResetGameHelperSearchState();
            appGuardSearchText = string.Empty;
            appGuardListState.ClearSearch();
        }

        private void ResetCrosshairSearchState()
        {
            crosshairAppSearchText = string.Empty;
            crosshairSelectedAppSearchText = string.Empty;
            crosshairAppsListState.ClearSearch();
            crosshairSelectedAppsListState.ClearSearch();
        }

        private void ResetGameHelperSearchState()
        {
            gameHelperSearchText = string.Empty;
            gameHelperProtectedSearchText = string.Empty;
            gameHelperAddAppsListState.ClearSearch();
            gameHelperProtectedAppsListState.ClearSearch();
        }

        private void UpdateNavigationState()
        {
            SetSettingsSelected(selectedModuleIndex == -1);
            SetNavSelected(AudioNavButton, AudioNavIcon, AudioNavIconTile, selectedModuleIndex == 0);
            SetNavSelected(DeviceGuardNavButton, DeviceGuardNavIcon, DeviceGuardNavIconTile,
                selectedModuleIndex == 1);
            SetNavSelected(KeyboardNavButton, KeyboardNavIcon, KeyboardNavIconTile, selectedModuleIndex == 2);
            SetNavSelected(GameHelperNavButton, GameHelperNavIcon, GameHelperNavIconTile,
                selectedModuleIndex == 3);
            SetNavSelected(AppGuardNavButton, AppGuardNavIcon, AppGuardNavIconTile, selectedModuleIndex == 4);
            SetNavSelected(LinkGuardNavButton, LinkGuardNavIcon, LinkGuardNavIconTile,
                selectedModuleIndex == 8);
            SetNavSelected(DisplayGuardNavButton, DisplayGuardNavIcon, DisplayGuardNavIconTile,
                selectedModuleIndex == 5);
            SetNavSelected(VsrGuardNavButton, VsrGuardNavIcon, VsrGuardNavIconTile,
                selectedModuleIndex == 9);
            SetNavSelected(PowerGuardNavButton, PowerGuardNavIcon, PowerGuardNavIconTile,
                selectedModuleIndex == 6);
            SetNavSelected(UacGuardNavButton, UacGuardNavIcon, UacGuardNavIconTile,
                selectedModuleIndex == 7);
        }

        private string GetSelectedModuleStatusText()
        {
            if (selectedModuleIndex == -1)
            {
                return "Guard Center settings.";
            }
            if (selectedModuleIndex == 1)
            {
                return deviceGuardModule.StatusText;
            }
            if (selectedModuleIndex == 2)
            {
                return keyboardModule.StatusText;
            }
            if (selectedModuleIndex == 3)
            {
                return gameHelperModule.StatusText;
            }
            if (selectedModuleIndex == 4)
            {
                return appGuardModule.StatusText;
            }
            if (selectedModuleIndex == 5)
            {
                return displayModule.StatusText;
            }
            if (selectedModuleIndex == 6)
            {
                return powerGuardModule.GetState().StatusText;
            }
            if (selectedModuleIndex == 7)
            {
                return uacGuardStatus == null ? "UAC Guard status is loading." : uacGuardStatus.StatusText;
            }
            if (selectedModuleIndex == 8)
            {
                return linkGuardModule.StatusText;
            }
            if (selectedModuleIndex == 9)
            {
                return vsrGuardSnapshot == null
                    ? "VSR Guard status has not been checked yet."
                    : vsrGuardSnapshot.StatusText;
            }

            return audioMixerModule.StatusText + " | " + audioModule.StatusText;
        }

        private static void SetNavSelected(Button button, UiControls.SymbolIcon icon, Border iconTile,
            bool selected)
        {
            button.Background = selected ? BrushFromRgb(0x1D, 0x27, 0x3A) : TransparentBrush;
            button.BorderBrush = selected ? AccentBrush : TransparentBrush;
            button.BorderThickness = selected ? new Thickness(1.5) : new Thickness(1);
            icon.Filled = selected;
            icon.Foreground = selected ? TextBrush : NavIconBrush;
            iconTile.Background = selected ? AccentBrush : NavIconTileBrush;
            iconTile.BorderBrush = selected ? AccentBrush : NavIconTileBorderBrush;
        }

        private void SetSettingsSelected(bool selected)
        {
            SettingsNavButton.Background = selected ? BrushFromRgb(0x1D, 0x27, 0x3A) : TransparentBrush;
            SettingsNavButton.BorderBrush = selected ? AccentBrush : TransparentBrush;
            SettingsNavButton.Foreground = selected ? TextBrush : MutedBrush;
        }

        private void RenderAudioPage()
        {
            if (audioMixerPresenter != null)
            {
                audioMixerPresenter.Dispose();
                audioMixerPresenter = null;
            }
            ConfigureHeader("Audio Guard",
                "集中管理應用程式音量，並保留音訊切換時的安全保護。", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            AddSection("App Mixer", CreateAudioMixerActions());
            try
            {
                List<AudioAppVolume> apps = latestAudioRefreshSnapshot;
                if (apps == null)
                {
                    AddCard("Loading app audio sessions",
                        "Refreshing the current Core Audio sessions in the background.",
                        null, false);
                }
                else if (apps.Count == 0)
                {
                    audioRefreshFingerprint = latestAudioRefreshFingerprint;
                    AddCard("No app audio sessions",
                        "播放一次音訊後，應用程式會出現在這裡。",
                        null, false);
                }
                else
                {
                    audioRefreshFingerprint = latestAudioRefreshFingerprint;
                    audioMixerPresenter = new InlineProgressiveListPresenter<AudioAppVolume>(audioMixerListState,
                        CreateAudioAppCard, null, ContentScrollViewer);
                    audioMixerPresenter.Reset(apps, null, null);
                    SettingsStack.Children.Add(audioMixerPresenter.Element);
                }
            }
            catch (Exception ex)
            {
                AddCard("Audio mixer unavailable",
                    ex.Message,
                    null, false);
            }

            AddSection("Audio Zero Guard", CreateToggle(settings.Audio.Enabled, delegate(bool value)
            {
                SetAudioEnabled(value);
            }));

            StackPanel audioZeroStack = AddIndentedGroup();

            AddSubSection(audioZeroStack, "Protection");
            AddCardToPanel(audioZeroStack, "Set mute",
                "切換事件發生後，將作用中的播放裝置設為靜音。",
                CreateToggle(settings.Audio.SetMute, delegate(bool value)
                {
                    settings.Audio.SetMute = value;
                    controller.ApplyAudioSettings();
                }), true);

            AddCardToPanel(audioZeroStack, "Set volume to 0",
                "強制將音訊端點主音量設為 0。",
                CreateToggle(settings.Audio.SetVolumeZero, delegate(bool value)
                {
                    settings.Audio.SetVolumeZero = value;
                    controller.ApplyAudioSettings();
                }), true);

            AddSubSection(audioZeroStack, "Automation");
            UIElement zeroOnEnableToggle = CreateToggle(settings.Audio.ZeroOnEnable,
                delegate(bool value)
                {
                    settings.Audio.ZeroOnEnable = value;
                    controller.ApplyAudioSettings();
                });
            System.Windows.Automation.AutomationProperties.SetName(
                zeroOnEnableToggle, "Zero audio when enabled");
            AddCardToPanel(audioZeroStack, "Zero audio when enabled",
                "Audio Guard 啟動時執行一次目前設定的靜音與音量歸零保護。",
                zeroOnEnableToggle, true);

            AddCardToPanel(audioZeroStack, "React to property changes",
                "將驅動或音訊端點中繼資料變更視為安全事件。",
                CreateToggle(settings.Audio.ReactToPropertyChanges, delegate(bool value)
                {
                    settings.Audio.ReactToPropertyChanges = value;
                    controller.ApplyAudioSettings();
                }), true);

            AddSubSection(audioZeroStack, "Timing");
            AddCardToPanel(audioZeroStack, "Poll interval",
                "音訊拓樸檢查間隔，單位為毫秒。",
                CreatePollIntervalEditor(), true);

            AddCardToPanel(audioZeroStack, "Retry delays",
                "偵測到切換後，依序延遲幾毫秒再補做一次保護。",
                CreateRetryDelayEditor(), true);

            AddSubSection(audioZeroStack, "Actions");
            AddCardToPanel(audioZeroStack, "Utilities",
                "立即執行保護，或檢查執行記錄。",
                CreateAudioActions(), true);

            ApplyAudioDependentState();
        }

        private void RenderSettingsPage()
        {
            ConfigureHeader("Guard Center",
                "管理 Guard Center 本身的啟動與全域行為。", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            AddSection("Startup");
            AddCard("Launch at Windows startup",
                "登入 Windows 後自動啟動，並縮到系統匣。",
                CreateToggle(StartupManager.IsEnabled(), delegate(bool value)
                {
                    if (value)
                    {
                        StartupManager.Enable(controller.GetApplicationShellIconPath());
                    }
                    else
                    {
                        StartupManager.Disable();
                    }
                    controller.SaveSettings();
                }), false);

            AddSection("Appearance");
            AddCard("Application icon", string.Empty, CreateApplicationIconEditor(), false);
        }

        private UIElement CreateApplicationIconEditor()
        {
            string customPath = controller.GetCustomApplicationIconPath();
            bool hasCustomIcon = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Border
            {
                Width = Z(58),
                Height = Z(58),
                Margin = ZThickness(0, 0, 14, 0),
                Padding = new Thickness(3),
                Background = BrushFromRgb(0x12, 0x18, 0x24),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new Image
                {
                    Source = ApplicationIconService.LoadImageSource(customPath),
                    Stretch = Stretch.Uniform
                }
            });

            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = hasCustomIcon ? "Custom icon active" : "Using default icon",
                FontSize = Z(12),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 0, 0, 8)
            });

            var buttons = new WrapPanel();
            Button choose = CreateActionButton("Choose image...", true, delegate
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Choose Guard Center application icon",
                    Filter = "Supported images|*.png;*.jpg;*.jpeg;*.bmp;*.ico|PNG image|*.png|JPEG image|*.jpg;*.jpeg|Bitmap image|*.bmp|Windows icon|*.ico",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) != true)
                {
                    return;
                }

                AppActionResult result = controller.ImportApplicationIcon(dialog.FileName);
                if (!result.Success)
                {
                    System.Windows.MessageBox.Show(result.Message, "Application icon",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                StatusTextBlock.Text = result.Message;
                RenderSettingsPage();
            });
            choose.Margin = ZThickness(0, 0, 8, 0);
            buttons.Children.Add(choose);

            Button reset = CreateActionButton("Reset to default", false, delegate
            {
                AppActionResult result = controller.ResetApplicationIcon();
                if (!result.Success)
                {
                    System.Windows.MessageBox.Show(result.Message, "Application icon",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                StatusTextBlock.Text = result.Message;
                RenderSettingsPage();
            });
            reset.IsEnabled = hasCustomIcon;
            reset.ToolTip = hasCustomIcon ? "Delete the custom icon and restore the embedded default."
                : "No custom icon is currently stored.";
            buttons.Children.Add(reset);
            content.Children.Add(buttons);
            panel.Children.Add(content);
            return panel;
        }

        private void RenderDeviceGuardPage()
        {
            ConfigureHeader("Device Guard",
                "分開處理目前存在的個別 PnP 裝置，以及電腦原有的核心硬體能力。", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            AddSection("修復中心");
            AddCard("目前可偵測裝置",
                "列出 Windows 目前仍可偵測的 Bluetooth Adapter、USB Camera、USB Audio 等個別 PnP 裝置；可重新啟動指定裝置節點。",
                CreateActionButton("開啟", true, delegate { ShowDetectableDevicesWindow(); }), false);
            AddCard("電腦核心硬體",
                "依 Bluetooth、Graphics、Network、Audio、Camera、USB 等硬體能力判斷正常、異常、停用或缺失，並採風險分級修復。",
                CreateActionButton("開啟", true, delegate { ShowCoreHardwareWindow(); }), false);
            InputStackSnapshot inputStack = controller.GetInputStackSnapshot();
            AddCard("輸入裝置堆疊",
                string.IsNullOrWhiteSpace(inputStack.Summary)
                    ? "隔離鍵盤 Interception；HuaJuan 滑鼠輸出依硬體身分固定，Windows 裝置編號只觀察、不介入。"
                    : inputStack.Summary,
                CreateActionButton("開啟", true, delegate { ShowInputStackWindow(); }), false);

            if (controller.IsDeviceGuardScanning())
            {
                AddCard("Scanning devices", "Reading Windows Plug and Play state.",
                    new UiControls.ProgressRing
                    {
                        Width = Z(22),
                        Height = Z(22),
                        IsIndeterminate = true
                    }, false);
            }

            AddSection("狀態");
            AddCard("共享 PnP snapshot",
                controller.IsDeviceGuardScanning()
                    ? "正在建立一次完整裝置快照；兩個視窗將共用結果。"
                    : "重新整理時只掃描一次完整裝置樹。滑到底載入下一批時只建立 UI item，不會重新掃描硬體。",
                CreateDeviceGuardLandingActions(), false);
        }

        private string BuildDeviceGuardDescription(DeviceGuardDevice device)
        {
            var parts = new List<string> { device.CategoryName };
            if (!string.IsNullOrWhiteSpace(device.Manufacturer)) parts.Add(device.Manufacturer);
            if (!string.IsNullOrWhiteSpace(device.DriverVersion)) parts.Add("Driver " + device.DriverVersion);
            parts.Add(DeviceRepairStateText(device.RepairState));
            string result = string.Join(" | ", parts.ToArray()) + ".";
            string detail = !string.IsNullOrWhiteSpace(device.ResultMessage)
                ? device.ResultMessage
                : device.UnsupportedReason;
            return string.IsNullOrWhiteSpace(detail) ? result : result + Environment.NewLine + detail;
        }

        private static string DeviceRepairStateText(DeviceRepairState state)
        {
            if (state == DeviceRepairState.Repairing) return "Repairing";
            if (state == DeviceRepairState.Succeeded) return "Verified";
            if (state == DeviceRepairState.Partial) return "Partially repaired";
            if (state == DeviceRepairState.Skipped) return "Skipped";
            if (state == DeviceRepairState.Unsupported) return "Not supported";
            if (state == DeviceRepairState.Failed) return "Failed";
            if (state == DeviceRepairState.RebootRequired) return "Restart required";
            if (state == DeviceRepairState.NeedsDriver) return "Driver required";
            if (state == DeviceRepairState.Ambiguous) return "Ambiguous identity";
            if (state == DeviceRepairState.RiskDeclined) return "Risk not authorized";
            if (state == DeviceRepairState.Canceled) return "Canceled";
            return "Ready";
        }

        private void RenderKeyboardPage()
        {
            ConfigureHeader("Keyboard Guard",
                "把常用鍵盤行為集中管理，避開 Windows 分散難找的設定入口。", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            AddSection("Input Method");
            AddCard("Disable Shift + Space width toggle",
                "鎖定 Microsoft 注音或華碩注音目前的全形/半形狀態；Shift 與 Space 訊號仍會完整傳給目前程式。",
                CreateToggle(keyboardModule.IsShiftSpaceWidthToggleDisabled(), delegate(bool value)
                {
                    controller.SetShiftSpaceWidthToggleDisabled(value);
                }), false);

            AddSection("Accessibility shortcuts");
            AddCard("停用連按五次 Shift 啟動相黏鍵",
                "只停用 Windows 相黏鍵的五次 Shift 快捷鍵，不影響一般 Shift、組合鍵或遊戲輸入。關閉後只恢復啟用前的快捷鍵位元。",
                CreateStickyKeysToggle(), false);

            AddSection("Actions");
            AddCard("Windows keyboard settings",
                "快速打開 Windows 內建的打字與語言設定頁。",
                CreateKeyboardActions(), false);
        }

        private void RenderGameHelperPage()
        {
            ConfigureHeader("Game Helper",
                "改善遊戲體驗", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();
            crosshairEnabledToggle = null;
            crosshairRestrictionToggle = null;

            AddSection("Screen crosshair");
            AddCard("Show screen crosshair",
                "在螢幕正中央顯示 FPS 準心。" + Environment.NewLine
                    + "透明置頂、滑鼠穿透，不會影響遊戲操作。",
                CreateCrosshairPrimaryActions(), false);

            bool restricted = controller.IsGameHelperCrosshairRestrictedToSelectedApps();
            int selectedAppCount = controller.GetGameHelperCrosshairSelectedApps().Count;
            string restrictionDescription = selectedAppCount + " app"
                + (selectedAppCount == 1 ? string.Empty : "s") + " selected.";
            if (selectedAppCount == 0)
            {
                restrictionDescription =
                    "No apps selected. Choose at least one app before using this restriction.";
            }

            StackPanel restrictionGroup = AddIndentedGroup();
            AddCardToPanel(restrictionGroup, "Only show in selected apps",
                restrictionDescription, CreateCrosshairRestrictionActions(restricted,
                    selectedAppCount), false);

            AddSection("Mouse acceleration");
            AddCard("Keep Enhance pointer precision off",
                "全域監控 Windows「增強指標的準確性」。" + Environment.NewLine
                    + "開啟時會立即關閉，之後每 30 秒檢查一次；不綁定任何 App。",
                CreateToggle(controller.IsGameHelperPointerPrecisionGuardEnabled(),
                    delegate(bool value)
                    {
                        controller.SetGameHelperPointerPrecisionGuardEnabled(value);
                        RefreshStatus();
                    }), false);

            AddSection("Game Protection Settings");
            List<GameHelperProtectedApp> protectedApps = controller.GetGameHelperProtectedApps();
            AddCard(string.Empty,
                protectedApps.Count == 0
                    ? "尚未選擇應用程式。" + Environment.NewLine
                        + "加入後可分別設定 Windows 鍵保護、遊戲輸入法鎖定，並使用 Right Ctrl + D 顯示桌面。"
                    : "目前已設定 " + protectedApps.Count + " 個應用程式。" + Environment.NewLine
                        + "每個 App 可獨立設定 Windows 鍵、Right Ctrl + D 與 Microsoft ENG 保護。",
                CreateGameHelperActions(), false);
        }

        private void RenderAppGuardPage()
        {
            CancelAppGuardScrollAnimation();
            if (appGuardPresenter != null)
            {
                appGuardPresenter.Dispose();
                appGuardPresenter = null;
            }
            ConfigureHeader("App Guard",
                "Manage installed apps, shortcuts, processes, paths and uninstall entries from one place.", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();
            appGuardExpandedCard = null;
            appGuardScrollTailSpacer = null;
            appGuardListHost = null;
            appGuardLoadedApps = null;

            SettingsStack.Children.Add(CreateAppGuardLoadingIndicator());
            int renderVersion = unchecked(++appGuardRenderVersion);
            BuildAppGuardPageAsync(renderVersion);
        }

        private async void BuildAppGuardPageAsync(int renderVersion)
        {
            await Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.Background);
            if (renderVersion != appGuardRenderVersion || selectedModuleIndex != 4
                || audioRefreshClosed)
            {
                return;
            }

            SettingsStack.Children.Clear();
            AddSection("Explorer integration");
            AddCard("Shortcut right-click entry",
                "Global App Guard setting. Registers one Guard Center entry for .lnk shortcuts and .exe files; Explorer opens the selected app here.",
                CreateAppGuardExplorerIntegrationToggle(), false);

            await Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.Background);
            if (renderVersion != appGuardRenderVersion || selectedModuleIndex != 4
                || audioRefreshClosed)
            {
                return;
            }

            AddSection("Application catalog");
            SettingsStack.Children.Add(CreateAppGuardCatalogActions());

            var listHost = new StackPanel();
            listHost.Children.Add(CreateAppGuardLoadingIndicator());
            SettingsStack.Children.Add(listHost);
            appGuardListHost = listHost;
            LoadAppGuardListAsync(listHost, renderVersion);
        }

        private async void LoadAppGuardListAsync(StackPanel listHost, int renderVersion)
        {
            List<AppCatalogItem> apps;
            try
            {
                apps = await Task.Run(new Func<List<AppCatalogItem>>(controller.GetAppGuardApps));
                await Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                if (renderVersion == appGuardRenderVersion && selectedModuleIndex == 4)
                {
                    listHost.Children.Clear();
                    AddCardToPanel(listHost, "App list unavailable", ex.Message,
                        CreateAppGuardRefreshButton(), false);
                }
                return;
            }

            if (renderVersion != appGuardRenderVersion || selectedModuleIndex != 4
                || audioRefreshClosed)
            {
                return;
            }

            appGuardLoadedApps = apps;
            RenderAppGuardList(listHost, apps);
        }

        private void RenderAppGuardList(StackPanel listHost, List<AppCatalogItem> apps)
        {
            CancelAppGuardScrollAnimation();
            if (appGuardPresenter != null)
            {
                appGuardPresenter.Dispose();
                appGuardPresenter = null;
            }
            listHost.Children.Clear();
            bool loading = controller.IsAppGuardLoading();
            string error = controller.GetAppGuardLastError();
            if (loading)
            {
                listHost.Children.Add(CreateAppGuardLoadingIndicator());
            }
            else if (!string.IsNullOrWhiteSpace(error) && apps.Count == 0)
            {
                AddCardToPanel(listHost, "App scan failed", error,
                    CreateAppGuardRefreshButton(), false);
                return;
            }

            appGuardListState.SearchText = appGuardSearchText;
            appGuardListState.Reset(apps, AppIdentityService.Matches, CreateAppGuardComparison());
            if (appGuardListState.TotalItems == 0)
            {
                if (!loading)
                {
                    AddCardToPanel(listHost,
                        string.IsNullOrWhiteSpace(appGuardSearchText) ? "No apps found" : "No matching apps",
                        string.IsNullOrWhiteSpace(appGuardSearchText)
                            ? "No executable application targets were found."
                            : "Try another app name, publisher, source or path.",
                        null, false);
                }
                return;
            }

            appGuardPresenter = new InlineProgressiveListPresenter<AppCatalogItem>(appGuardListState,
                CreateAppGuardCard, null, ContentScrollViewer, OnAppGuardItemsMaterialized);
            appGuardPresenter.Reset(apps, AppIdentityService.Matches, CreateAppGuardComparison());
            if (!string.IsNullOrWhiteSpace(appGuardExpandedAppId))
            {
                appGuardPresenter.EnsureVisible(
                    delegate(AppCatalogItem item)
                    {
                        return string.Equals(item.Id, appGuardExpandedAppId, StringComparison.OrdinalIgnoreCase);
                    },
                    delegate(UIElement element)
                    {
                        FrameworkElement frameworkElement = element as FrameworkElement;
                        return frameworkElement != null && string.Equals(Convert.ToString(frameworkElement.Tag),
                            appGuardExpandedAppId, StringComparison.OrdinalIgnoreCase);
                    });
            }
            listHost.Children.Add(appGuardPresenter.Element);
            appGuardScrollTailSpacer = new Border { Height = 0, IsHitTestVisible = false };
            listHost.Children.Add(appGuardScrollTailSpacer);
        }

        private UIElement CreateAppGuardLoadingIndicator()
        {
            return new UiControls.ProgressRing
            {
                Width = Z(28),
                Height = Z(28),
                Margin = ZThickness(0, 18, 0, 18),
                IsIndeterminate = true,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private UIElement CreateAppGuardCatalogActions()
        {
            var panel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            var search = new TextBox
            {
                Text = appGuardSearchText,
                Width = Z(230),
                FontSize = Z(13),
                Foreground = TextBrush,
                Background = BrushFromRgb(0x0E, 0x12, 0x1B),
                BorderBrush = CardBorderBrush,
                Padding = ZThickness(10, 7, 10, 7),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            search.TextChanged += delegate
            {
                ApplyAppGuardSearchLive(search.Text);
            };
            search.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    ApplyAppGuardSearchLive(search.Text);
                    e.Handled = true;
                }
            };
            AddAppGuardToolbarItem(panel, search);
            AddAppGuardToolbarItem(panel,
                CreateActionButton("Search", true, delegate { ApplyAppGuardSearchLive(search.Text); }));
            AddAppGuardToolbarItem(panel,
                CreateActionButton("Clear", false, delegate { search.Text = string.Empty; }));
            AddAppGuardToolbarItem(panel, CreateAppGuardSortCombo());
            AddAppGuardToolbarItem(panel, CreateAppGuardDirectionCombo());
            AddAppGuardToolbarItem(panel, CreateAppGuardBatchSizeCombo());
            AddAppGuardToolbarItem(panel, CreateActionButton("Refresh", false, delegate
            {
                appGuardExpandedAppId = string.Empty;
                appGuardExpandedDetail = null;
                controller.RefreshAppGuardApps();
                RenderAppGuardPage();
                RefreshStatus();
            }));
            return panel;
        }

        private void AddAppGuardToolbarItem(Panel panel, FrameworkElement item)
        {
            if (panel == null || item == null)
            {
                return;
            }

            item.Margin = ZThickness(0, 0, 8, 8);
            panel.Children.Add(item);
        }

        private UIElement CreateAppGuardExplorerIntegrationToggle()
        {
            return CreateToggle(controller.IsExplorerIntegrationEnabled(), delegate(bool value)
            {
                try
                {
                    controller.SetExplorerIntegrationEnabled(value);
                    StatusTextBlock.Text = value
                        ? "Explorer Guard Center entry enabled."
                        : "Explorer Guard Center entry removed.";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "Explorer integration",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }

                RenderAppGuardPage();
            });
        }

        private void ApplyAppGuardSearchLive(string text)
        {
            string next = text ?? string.Empty;
            if (string.Equals(appGuardSearchText, next, StringComparison.Ordinal))
            {
                return;
            }

            appGuardSearchText = next;
            appGuardExpandedAppId = string.Empty;
            appGuardExpandedDetail = null;
            if (selectedModuleIndex == 4 && appGuardListHost != null
                && appGuardLoadedApps != null)
            {
                RenderAppGuardList(appGuardListHost, appGuardLoadedApps);
            }
        }

        private Button CreateAppGuardRefreshButton()
        {
            return CreateActionButton("Refresh", true, delegate
            {
                controller.RefreshAppGuardApps();
                RenderAppGuardPage();
            });
        }

        private ComboBox CreateAppGuardSortCombo()
        {
            var combo = CreateSmallComboBox(Z(130));
            AddComboItem(combo, "Original", "original", appGuardListState.SortKey);
            AddComboItem(combo, "Name", "name", appGuardListState.SortKey);
            AddComboItem(combo, "Publisher", "publisher", appGuardListState.SortKey);
            AddComboItem(combo, "Source", "source", appGuardListState.SortKey);
            combo.SelectionChanged += delegate
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                appGuardListState.SortKey = Convert.ToString(item.Tag);
                appGuardExpandedAppId = string.Empty;
                appGuardExpandedDetail = null;
                controller.SaveSettings();
                RenderAppGuardPage();
            };
            return combo;
        }

        private ComboBox CreateAppGuardDirectionCombo()
        {
            var selected = appGuardListState.SortDescending ? "true" : "false";
            var combo = CreateSmallComboBox(Z(118));
            AddComboItem(combo, "Ascending", "false", selected);
            AddComboItem(combo, "Descending", "true", selected);
            combo.SelectionChanged += delegate
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                appGuardListState.SortDescending = string.Equals(Convert.ToString(item.Tag), "true", StringComparison.OrdinalIgnoreCase);
                appGuardExpandedAppId = string.Empty;
                appGuardExpandedDetail = null;
                controller.SaveSettings();
                RenderAppGuardPage();
            };
            return combo;
        }

        private ComboBox CreateAppGuardBatchSizeCombo()
        {
            var combo = CreateSmallComboBox(Z(124));
            int[] sizes = appGuardListState.AllowedBatchSizes;
            for (int i = 0; i < sizes.Length; i++)
            {
                AddComboItem(combo, sizes[i] + " per batch", sizes[i].ToString(),
                    appGuardListState.BatchSize.ToString());
            }
            combo.SelectionChanged += delegate
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                int value;
                if (int.TryParse(Convert.ToString(item.Tag), out value))
                {
                    appGuardListState.BatchSize = value;
                    appGuardExpandedAppId = string.Empty;
                    appGuardExpandedDetail = null;
                    controller.SaveSettings();
                    RenderAppGuardPage();
                }
            };
            return combo;
        }

        private void OnAppGuardItemsMaterialized(IList<AppCatalogItem> apps)
        {
            controller.PrefetchAppGuardDetails(apps);
        }

        private UIElement CreateAppGuardCard(AppCatalogItem app)
        {
            bool expanded = string.Equals(appGuardExpandedAppId, app.Id, StringComparison.OrdinalIgnoreCase);
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = expanded ? AccentBrush : CardBorderBrush,
                BorderThickness = new Thickness(expanded ? 1.5 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = ZThickness(18, 16, 18, 16),
                Margin = ZThickness(0, 0, 0, 12)
            };
            card.Tag = app.Id;

            var stack = new StackPanel();
            card.Child = stack;

            var row = new Grid();
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement icon = CreateAppCatalogIcon(app);
            icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetValue(MarginProperty, ZThickness(0, 0, 16, 0));
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var text = new StackPanel();
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = app.Name,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (AppActionService.IsCurrentApplication(app))
            {
                nameRow.Children.Add(new Border
                {
                    Margin = ZThickness(8, 0, 0, 0),
                    Padding = ZThickness(6, 2, 6, 2),
                    BorderBrush = AccentBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Child = new TextBlock
                    {
                        Text = "Current app",
                        FontSize = Z(9),
                        Foreground = AccentBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }
            text.Children.Add(nameRow);
            text.Children.Add(new TextBlock
            {
                Text = BuildAppGuardDescription(app),
                Margin = ZThickness(0, 4, 0, 0),
                FontSize = Z(12),
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            AppGuardCardView view = null;
            Button toggle = CreateActionButton(expanded ? "Manage  ▲" : "Manage  ▼", true, delegate
            {
                ToggleAppGuardCard(view);
            });
            toggle.Width = Z(116);
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);
            AttachAdaptiveTwoColumnLayout(card, row, text, toggle, Z(620));
            stack.Children.Add(row);

            view = new AppGuardCardView
            {
                App = app,
                Card = card,
                Content = stack,
                Toggle = toggle
            };

            if (expanded)
            {
                appGuardExpandedCard = view;
                ExpandAppGuardCard(view);
            }

            return card;
        }

        private void ToggleAppGuardCard(AppGuardCardView view)
        {
            if (view == null || view.App == null || view.Card == null)
            {
                return;
            }

            bool expanded = string.Equals(appGuardExpandedAppId, view.App.Id,
                StringComparison.OrdinalIgnoreCase);

            CancelAppGuardScrollAnimation();
            controller.CancelAppGuardDetailLoad();
            if (expanded)
            {
                appGuardExpandedAppId = string.Empty;
                appGuardExpandedDetail = null;
                CollapseAppGuardCard(view);
            }
            else
            {
                if (appGuardExpandedCard != null && !ReferenceEquals(appGuardExpandedCard, view))
                {
                    CollapseAppGuardCard(appGuardExpandedCard);
                }

                appGuardExpandedAppId = view.App.Id;
                appGuardExpandedDetail = null;
                ExpandAppGuardCard(view);
                SmoothScrollAppGuardCardToCenter(view);
            }
        }

        private void ExpandAppGuardCard(AppGuardCardView view)
        {
            if (view == null || view.Card == null || view.Content == null)
            {
                return;
            }

            view.Card.BorderBrush = AccentBrush;
            view.Card.BorderThickness = new Thickness(1.5);
            if (view.Toggle != null)
            {
                view.Toggle.Content = "Manage  ▲";
            }

            if (view.DetailHost == null)
            {
                view.DetailHost = CreateAppGuardDetailPanel(view);
                view.Content.Children.Add(view.DetailHost);
            }
            appGuardExpandedCard = view;
        }

        private void CollapseAppGuardCard(AppGuardCardView view)
        {
            if (view == null || view.Card == null || view.Content == null)
            {
                return;
            }

            view.Card.BorderBrush = CardBorderBrush;
            view.Card.BorderThickness = new Thickness(1);
            if (view.Toggle != null)
            {
                view.Toggle.Content = "Manage  ▼";
            }

            if (view.DetailHost != null)
            {
                view.Content.Children.Remove(view.DetailHost);
                view.DetailHost = null;
            }
            ResetAppGuardDetailView(view);
            if (ReferenceEquals(appGuardExpandedCard, view))
            {
                appGuardExpandedCard = null;
            }
            if (appGuardScrollTailSpacer != null)
            {
                appGuardScrollTailSpacer.Height = 0;
            }
        }

        private Border CreateAppGuardDetailPanel(AppGuardCardView view)
        {
            var detailHost = new Border
            {
                Margin = ZThickness(0, 16, 0, 0),
                Padding = ZThickness(16, 14, 16, 14),
                Background = BrushFromRgb(0x12, 0x18, 0x24),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Child = new StackPanel()
            };
            view.DetailHost = detailHost;
            RenderAppGuardDetail(view);
            return detailHost;
        }

        private void RenderAppGuardDetail(AppGuardCardView view)
        {
            if (view == null || view.App == null || view.DetailHost == null)
            {
                return;
            }

            StackPanel stack = view.DetailHost.Child as StackPanel;
            if (stack == null)
            {
                return;
            }

            AppCatalogItem app = view.App;
            if (!view.DetailUiInitialized)
            {
                InitializeAppGuardDetailUi(view, stack);
            }

            AppCatalogDetail loaded = null;
            if (appGuardExpandedDetail != null && appGuardExpandedDetail.App != null
                && string.Equals(appGuardExpandedDetail.App.Id, app.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                loaded = appGuardExpandedDetail;
            }
            else
            {
                controller.TryGetAppGuardDetail(app.Id, out loaded);
            }

            if (loaded != null)
            {
                appGuardExpandedDetail = loaded;
                ApplyAppGuardDetail(view, loaded);
                if (!view.DetailRefreshStarted)
                {
                    view.DetailRefreshStarted = true;
                    StartAppGuardDetailLoad(view, true);
                }
                return;
            }

            ApplyAppGuardDetailLoadingState(view);
            StartAppGuardDetailLoad(view, false);
        }

        private void InitializeAppGuardDetailUi(AppGuardCardView view, StackPanel stack)
        {
            stack.Children.Clear();
            view.DetailErrorPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = ZThickness(0, 0, 0, 12)
            };
            view.DetailErrorText = new TextBlock
            {
                FontSize = Z(12),
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, 0, 0, 8)
            };
            view.DetailErrorPanel.Children.Add(view.DetailErrorText);
            view.DetailErrorPanel.Children.Add(CreateActionButton("Retry", true, delegate
            {
                controller.CancelAppGuardDetailLoad();
                controller.InvalidateAppGuardDetail(view.App.Id);
                appGuardExpandedDetail = null;
                view.DetailLoadPending = false;
                view.DetailRefreshStarted = false;
                ApplyAppGuardDetailLoadingState(view);
                StartAppGuardDetailLoad(view, false);
            }));
            stack.Children.Add(view.DetailErrorPanel);
            stack.Children.Add(CreateAppGuardInfoGrid(view.App, view));

            UIElement actions = CreateAppGuardActionButtons(view.App, view);
            actions.SetValue(MarginProperty, ZThickness(0, 14, 0, 0));
            stack.Children.Add(actions);
            view.DetailUiInitialized = true;
        }

        private void StartAppGuardDetailLoad(AppGuardCardView view, bool forceRefresh)
        {
            if (view == null || view.App == null || view.DetailLoadPending)
            {
                return;
            }

            view.DetailLoadPending = true;
            Action<AppCatalogDetail> callback = delegate(AppCatalogDetail detail)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    view.DetailLoadPending = false;
                    if (!ReferenceEquals(appGuardExpandedCard, view)
                        || view.DetailHost == null
                        || !string.Equals(appGuardExpandedAppId, view.App.Id,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    appGuardExpandedDetail = detail;
                    ApplyAppGuardDetail(view, detail);
                }));
            };

            if (forceRefresh)
            {
                controller.RefreshAppGuardDetailAsync(view.App, callback);
            }
            else
            {
                controller.LoadAppGuardDetailAsync(view.App, callback);
            }
        }

        private void ApplyAppGuardDetailLoadingState(AppGuardCardView view)
        {
            if (view == null || view.App == null || !view.DetailUiInitialized)
            {
                return;
            }

            if (view.DetailErrorPanel != null)
            {
                view.DetailErrorPanel.Visibility = Visibility.Collapsed;
            }
            view.StatusValue.Text = "Checking...";
            view.PathValue.Text = view.App.TargetPath ?? string.Empty;
            view.VersionValue.Text = "...";
            view.PublisherValue.Text = string.IsNullOrWhiteSpace(view.App.Publisher)
                ? "..."
                : view.App.Publisher;
            view.InstallLocationValue.Text = GetInitialAppGuardInstallLocation(view.App);
            view.SourceValue.Text = view.App.Source ?? string.Empty;
            view.ModifiedValue.Text = "...";
            view.SizeValue.Text = "...";
            if (view.TerminateButton != null)
            {
                view.TerminateButton.IsEnabled = false;
                view.TerminateButton.ToolTip = "Checking for a matching running process.";
            }
        }

        private void ApplyAppGuardDetail(AppGuardCardView view, AppCatalogDetail detail)
        {
            if (view == null || view.App == null || detail == null || !view.DetailUiInitialized)
            {
                return;
            }

            bool failed = !string.IsNullOrWhiteSpace(detail.Error);
            if (view.DetailErrorPanel != null)
            {
                view.DetailErrorPanel.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            }
            if (view.DetailErrorText != null)
            {
                view.DetailErrorText.Text = detail.Error ?? string.Empty;
            }

            view.StatusValue.Text = failed
                ? "Unavailable"
                : (detail.IsRunning
                    ? "Running (" + detail.ProcessIds.Count + " process" + Plural(detail.ProcessIds.Count) + ")"
                    : "Not running");
            view.PathValue.Text = view.App.TargetPath ?? string.Empty;
            view.VersionValue.Text = ValueOrUnavailable(detail.Version);
            view.PublisherValue.Text = ValueOrUnavailable(detail.CompanyName);
            view.InstallLocationValue.Text = ValueOrUnavailable(detail.InstallLocation);
            view.SourceValue.Text = view.App.Source ?? string.Empty;
            view.ModifiedValue.Text = ValueOrUnavailable(detail.LastWriteText);
            view.SizeValue.Text = ValueOrUnavailable(detail.FileSizeText);

            if (view.TerminateButton != null)
            {
                bool currentApp = AppActionService.IsCurrentApplication(view.App);
                view.TerminateButton.IsEnabled = !failed && detail.IsRunning && !currentApp;
                view.TerminateButton.ToolTip = currentApp
                    ? "Guard Center cannot terminate its current process from App Guard."
                    : (failed
                        ? "Running status is unavailable."
                        : (detail.IsRunning ? "Terminate matching process path." : "No matching running process."));
            }
        }

        private static string GetInitialAppGuardInstallLocation(AppCatalogItem app)
        {
            if (app == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                return app.InstallLocation;
            }
            return string.IsNullOrWhiteSpace(app.TargetPath)
                ? "..."
                : (Path.GetDirectoryName(app.TargetPath) ?? "...");
        }

        private static string ValueOrUnavailable(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
        }

        private static void ResetAppGuardDetailView(AppGuardCardView view)
        {
            view.DetailLoadPending = false;
            view.DetailRefreshStarted = false;
            view.DetailUiInitialized = false;
            view.DetailErrorPanel = null;
            view.DetailErrorText = null;
            view.StatusValue = null;
            view.PathValue = null;
            view.VersionValue = null;
            view.PublisherValue = null;
            view.InstallLocationValue = null;
            view.SourceValue = null;
            view.ModifiedValue = null;
            view.SizeValue = null;
            view.TerminateButton = null;
        }

        private double GetAppGuardCardViewportTop(AppGuardCardView view)
        {
            if (view == null || view.Card == null || ContentScrollViewer == null
                || !view.Card.IsLoaded)
            {
                return double.NaN;
            }

            try
            {
                return view.Card.TranslatePoint(new Point(0, 0), ContentScrollViewer).Y;
            }
            catch
            {
                return double.NaN;
            }
        }

        private void SmoothScrollAppGuardCardToCenter(AppGuardCardView view)
        {
            if (view == null || view.Card == null || ContentScrollViewer == null)
            {
                return;
            }

            EventHandler handler = null;
            bool tailPrepared = false;
            bool started = false;
            long startedAt = 0;
            double startOffset = 0;
            double targetOffset = 0;
            handler = delegate
            {
                if (audioRefreshClosed || selectedModuleIndex != 4 || ContentScrollViewer == null
                    || !ReferenceEquals(appGuardExpandedCard, view) || !view.Card.IsLoaded)
                {
                    StopAppGuardScrollAnimation(handler);
                    return;
                }

                if (!started)
                {
                    double cardTop = GetAppGuardCardViewportTop(view);
                    if (!IsFinite(cardTop))
                    {
                        StopAppGuardScrollAnimation(handler);
                        return;
                    }

                    startOffset = ContentScrollViewer.VerticalOffset;
                    double cardHeight = view.Card.ActualHeight;
                    double viewportHeight = ContentScrollViewer.ViewportHeight;
                    if (!tailPrepared)
                    {
                        tailPrepared = true;
                        double requiredTail = CalculateAppGuardRequiredScrollTail(startOffset, cardTop,
                            cardHeight, viewportHeight, ContentScrollViewer.ScrollableHeight);
                        if (requiredTail > 0.5 && appGuardScrollTailSpacer != null)
                        {
                            appGuardScrollTailSpacer.Height += requiredTail;
                            return;
                        }
                    }

                    targetOffset = CalculateAppGuardCenteredScrollOffset(startOffset, cardTop,
                        cardHeight, viewportHeight, ContentScrollViewer.ScrollableHeight);
                    if (Math.Abs(targetOffset - startOffset) < 0.5)
                    {
                        ContentScrollViewer.ScrollToVerticalOffset(targetOffset);
                        StopAppGuardScrollAnimation(handler);
                        return;
                    }

                    startedAt = Stopwatch.GetTimestamp();
                    started = true;
                }

                double elapsedMilliseconds = (Stopwatch.GetTimestamp() - startedAt) * 1000d
                    / Stopwatch.Frequency;
                double progress = elapsedMilliseconds / 280d;
                ContentScrollViewer.ScrollToVerticalOffset(
                    CalculateAppGuardScrollAnimationOffset(startOffset, targetOffset, progress));
                if (progress >= 1)
                {
                    ContentScrollViewer.ScrollToVerticalOffset(targetOffset);
                    StopAppGuardScrollAnimation(handler);
                }
            };

            appGuardScrollAnimationHandler = handler;
            CompositionTarget.Rendering += handler;
        }

        private void CancelAppGuardScrollAnimation()
        {
            StopAppGuardScrollAnimation(appGuardScrollAnimationHandler);
        }

        private void StopAppGuardScrollAnimation(EventHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(appGuardScrollAnimationHandler, handler))
            {
                appGuardScrollAnimationHandler = null;
            }
        }

        internal static double CalculateAppGuardCenteredScrollOffset(double currentOffset,
            double cardViewportTop, double cardHeight, double viewportHeight, double scrollableHeight)
        {
            double targetOffset = CalculateAppGuardUnclampedCenteredScrollOffset(currentOffset,
                cardViewportTop, cardHeight, viewportHeight);
            targetOffset = Math.Max(0, targetOffset);
            if (IsFinite(scrollableHeight))
            {
                targetOffset = Math.Min(targetOffset, Math.Max(0, scrollableHeight));
            }
            return targetOffset;
        }

        internal static double CalculateAppGuardRequiredScrollTail(double currentOffset,
            double cardViewportTop, double cardHeight, double viewportHeight, double scrollableHeight)
        {
            double targetOffset = CalculateAppGuardUnclampedCenteredScrollOffset(currentOffset,
                cardViewportTop, cardHeight, viewportHeight);
            double maximumOffset = IsFinite(scrollableHeight) ? Math.Max(0, scrollableHeight) : 0;
            return Math.Max(0, targetOffset - maximumOffset);
        }

        private static double CalculateAppGuardUnclampedCenteredScrollOffset(double currentOffset,
            double cardViewportTop, double cardHeight, double viewportHeight)
        {
            double targetOffset = IsFinite(currentOffset) ? currentOffset : 0;
            targetOffset += IsFinite(cardViewportTop) ? cardViewportTop : 0;
            if (IsFinite(cardHeight) && cardHeight > 0 && IsFinite(viewportHeight)
                && viewportHeight > 0 && cardHeight < viewportHeight)
            {
                targetOffset += (cardHeight - viewportHeight) / 2;
            }
            return targetOffset;
        }

        internal static double CalculateAppGuardScrollAnimationOffset(double startOffset,
            double targetOffset, double progress)
        {
            double normalized = IsFinite(progress) ? Math.Max(0, Math.Min(1, progress)) : 1;
            double inverse = 1 - normalized;
            double eased = 1 - (inverse * inverse * inverse);
            return startOffset + ((targetOffset - startOffset) * eased);
        }

        private UIElement CreateAppGuardInfoGrid(AppCatalogItem app, AppGuardCardView view)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            view.StatusValue = AddAppGuardInfoRow(grid, "Status", "Checking...");
            view.PathValue = AddAppGuardInfoRow(grid, "Path", app.TargetPath);
            view.VersionValue = AddAppGuardInfoRow(grid, "Version", "...");
            view.PublisherValue = AddAppGuardInfoRow(grid, "Publisher",
                string.IsNullOrWhiteSpace(app.Publisher) ? "..." : app.Publisher);
            view.InstallLocationValue = AddAppGuardInfoRow(grid, "Install location",
                GetInitialAppGuardInstallLocation(app));
            view.SourceValue = AddAppGuardInfoRow(grid, "Source", app.Source);
            view.ModifiedValue = AddAppGuardInfoRow(grid, "Modified", "...");
            view.SizeValue = AddAppGuardInfoRow(grid, "Size", "...");
            return grid;
        }

        private TextBlock AddAppGuardInfoRow(Grid grid, string label, string value)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelText = new TextBlock
            {
                Text = label,
                Width = Z(118),
                FontSize = Z(12),
                Foreground = SubtleBrush,
                Margin = ZThickness(0, row == 0 ? 0 : 6, 14, 0)
            };
            Grid.SetRow(labelText, row);
            Grid.SetColumn(labelText, 0);
            grid.Children.Add(labelText);

            var valueText = new TextBlock
            {
                Text = value ?? string.Empty,
                FontSize = Z(12),
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, row == 0 ? 0 : 6, 0, 0)
            };
            Grid.SetRow(valueText, row);
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
            return valueText;
        }

        private UIElement CreateAppGuardActionButtons(AppCatalogItem app, AppGuardCardView view)
        {
            bool currentApp = AppActionService.IsCurrentApplication(app);
            var buttons = new List<Button>();
            buttons.Add(CreateActionButton("Open", true, delegate { RunAppGuardAction(app, AppActionType.Run); }));
            buttons.Add(CreateActionButton("Run as admin", false, delegate { RunAppGuardAction(app, AppActionType.RunAsAdministrator); }));
            Button restart = CreateActionButton("Restart", false, delegate { RunAppGuardAction(app, AppActionType.Restart); });
            restart.IsEnabled = !currentApp;
            restart.ToolTip = currentApp ? "Guard Center cannot restart its current process from App Guard." : "Restart this app.";
            buttons.Add(restart);

            Button terminate = CreateActionButton("Terminate", false, delegate { RunAppGuardAction(app, AppActionType.Terminate); });
            terminate.IsEnabled = false;
            terminate.ToolTip = currentApp
                ? "Guard Center cannot terminate its current process from App Guard."
                : "Checking for a matching running process.";
            view.TerminateButton = terminate;
            buttons.Add(terminate);

            buttons.Add(CreateActionButton("Location", false, delegate { RunAppGuardAction(app, AppActionType.OpenFileLocation); }));
            buttons.Add(CreateActionButton("Copy path", false, delegate { RunAppGuardAction(app, AppActionType.CopyPath); }));

            Button modify = CreateActionButton("Modify/repair", false, delegate { RunAppGuardAction(app, AppActionType.ModifyOrRepair); });
            modify.IsEnabled = !string.IsNullOrWhiteSpace(app.ModifyCommand);
            modify.ToolTip = modify.IsEnabled ? app.ModifyCommand : "No modify or repair command was found.";
            buttons.Add(modify);

            Button uninstall = CreateActionButton("Uninstall", false, delegate { RunAppGuardAction(app, AppActionType.Uninstall); });
            uninstall.IsEnabled = !string.IsNullOrWhiteSpace(app.UninstallCommand);
            uninstall.ToolTip = uninstall.IsEnabled ? app.UninstallCommand : "No uninstall command was found.";
            buttons.Add(uninstall);
            return CreateResponsiveButtonGrid(buttons);
        }

        private void RunAppGuardAction(AppCatalogItem app, AppActionType action)
        {
            if (action == AppActionType.Terminate)
            {
                MessageBoxResult confirmTerminate = System.Windows.MessageBox.Show(
                    "Terminate running process(es) that exactly match this executable path?" + Environment.NewLine + Environment.NewLine + app.TargetPath,
                    "Terminate app", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmTerminate != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            else if (action == AppActionType.Uninstall)
            {
                MessageBoxResult confirmUninstall = System.Windows.MessageBox.Show(
                    "Run the uninstall command for this app?" + Environment.NewLine + Environment.NewLine + app.UninstallCommand,
                    "Uninstall app", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmUninstall != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            AppActionResult result = controller.ExecuteAppGuardAction(app, action);
            StatusTextBlock.Text = result.Message;
            if (!result.Success && !result.Cancelled)
            {
                System.Windows.MessageBox.Show(result.Message, "App Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            appGuardExpandedDetail = null;
            RenderAppGuardPage();
        }

        internal void HandleAppActionRequest(AppActionRequest request)
        {
            if (request == null)
            {
                return;
            }

            AppCatalogItem app = controller.FindOrImportAppGuardTarget(request.TargetPath);
            if (app == null)
            {
                SelectModule(4);
                System.Windows.MessageBox.Show("Could not resolve this shortcut or executable:" + Environment.NewLine + request.TargetPath,
                    "App Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            appGuardSearchText = string.Empty;
            appGuardExpandedAppId = app.Id;
            appGuardExpandedDetail = null;
            PrepareAppGuardTarget(app.Id);
            SelectModule(4);
        }

        private void PrepareAppGuardTarget(string appId)
        {
            appGuardListState.ClearSearch();
        }

        private Comparison<AppCatalogItem> CreateAppGuardComparison()
        {
            string key = appGuardListState.SortKey;
            if (string.Equals(key, "publisher", StringComparison.OrdinalIgnoreCase))
            {
                return delegate(AppCatalogItem left, AppCatalogItem right)
                {
                    int value = string.Compare(left.Publisher, right.Publisher, StringComparison.CurrentCultureIgnoreCase);
                    return value != 0 ? value : AppIdentityService.CompareName(left, right);
                };
            }
            if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase))
            {
                return delegate(AppCatalogItem left, AppCatalogItem right)
                {
                    int value = string.Compare(left.Source, right.Source, StringComparison.CurrentCultureIgnoreCase);
                    return value != 0 ? value : AppIdentityService.CompareName(left, right);
                };
            }

            return AppIdentityService.CompareName;
        }

        private string BuildAppGuardDescription(AppCatalogItem app)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(app.Publisher))
            {
                parts.Add(app.Publisher);
            }
            if (!string.IsNullOrWhiteSpace(app.Source))
            {
                parts.Add(app.Source);
            }
            parts.Add(app.TargetPath);
            return string.Join("  |  ", parts.ToArray());
        }

        private void RenderDisplayGuardPage()
        {
            if (displayMonitorPresenter != null)
            {
                displayMonitorPresenter.Dispose();
                displayMonitorPresenter = null;
            }
            ConfigureHeader("Display Guard",
                "Hardware display controls and per-monitor mode profiles. Live saves each adjustment automatically.", false);

            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            List<DisplayGuardMonitorInfo> monitors = controller.GetDisplayGuardMonitors();
            displayRefreshFingerprint = FocusedRefreshPolicy.GetDisplayFingerprint(monitors);

            AddSection("Display modes", CreateDisplayModeActions());
            AddCard("Selected mode",
                "Profiles are saved per display. Unmatched displays keep manual control but are skipped by mode apply.",
                CreateStaticValue(DisplayGuardModes.Get(controller.GetSelectedDisplayGuardMode()).Name), false);

            AddSection("Sync supported displays");
            int brightnessCount = CountDisplayFeature(monitors, DisplayGuardFeature.Brightness);
            int contrastCount = CountDisplayFeature(monitors, DisplayGuardFeature.Contrast);
            if (brightnessCount == 0 && contrastCount == 0)
            {
                AddCard("No controllable displays",
                    monitors.Count == 0
                        ? "No active display has been detected yet."
                        : "Connected displays did not expose brightness or contrast controls.",
                    CreateDisplayRefreshButton(), false);
            }
            else
            {
                if (brightnessCount > 0)
                {
                    AddCard("Brightness",
                        brightnessCount + " display" + Plural(brightnessCount) + " support hardware brightness.",
                        CreateDisplaySyncSlider(monitors, DisplayGuardFeature.Brightness), false);
                }

                if (contrastCount > 0)
                {
                    AddCard("Contrast",
                        contrastCount + " display" + Plural(contrastCount) + " support hardware contrast.",
                        CreateDisplaySyncSlider(monitors, DisplayGuardFeature.Contrast), false);
                }
            }

            AddSection("Displays", CreateBatchSizeCombo(displayMonitorListState, RenderDisplayGuardPage));
            if (monitors.Count == 0)
            {
                AddCard("No displays detected",
                    "Use refresh after connecting or waking displays.",
                    CreateDisplayRefreshButton(), false);
                return;
            }

            displayMonitorPresenter = new ProgressiveListPresenter<DisplayGuardMonitorInfo>(displayMonitorListState,
                CreateDisplayMonitorCard, null);
            displayMonitorPresenter.Element.Height = GetProgressiveListHeight(0.42, 220, 430);
            displayMonitorPresenter.Reset(monitors, null, null, true);
            SettingsStack.Children.Add(displayMonitorPresenter.Element);
            displayMonitorPresenter.RestoreVerticalOffset(displayListRestoreOffset);
            displayListRestoreOffset = 0;
        }

        private void RenderVsrGuardShortcutPage()
        {
            ConfigureHeader("VSR Guard",
                "Open NVIDIA's native video and RTX enhancement settings.", false);
            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            vsrGuardSnapshot = vsrGuardModule.Refresh();
            Button openNvidia = CreateActionButton("Open NVIDIA Control Panel", true,
                async delegate
                {
                    await RunVsrGuardActionAsync(delegate
                    {
                        return Task.FromResult(vsrGuardModule.OpenNvidiaControlPanel());
                    });
                });
            openNvidia.IsEnabled = !vsrGuardBusy;

            AddSection("NVIDIA Control Panel");
            AddCard("NVIDIA video settings",
                "Open the native NVIDIA Control Panel to manage Super Resolution, quality, HDR and deinterlacing.",
                openNvidia, false);
            RefreshStatus();
        }

        private void RenderVsrGuardPage()
        {
            ConfigureHeader("VSR Guard",
                "Prepare Chrome and control NVIDIA RTX video enhancement from one page.", false);
            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            vsrGuardSnapshot = vsrGuardModule.Refresh();

            var topActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Button setupAll = CreateActionButton(vsrGuardBusy ? "Working..." : "Set up all", true,
                async delegate
                {
                    await RunVsrGuardActionAsync(delegate { return vsrGuardModule.SetupAllAsync(); });
                });
            setupAll.IsEnabled = !vsrGuardBusy
                && vsrGuardSnapshot.ChromeInstalled
                && vsrGuardSnapshot.NvidiaRtxSupported
                && vsrGuardSnapshot.NvidiaGpuHealthy;
            setupAll.Margin = ZThickness(0, 0, 8, 0);
            topActions.Children.Add(setupAll);

            Button refresh = CreateActionButton("Check again", false, delegate
            {
                vsrGuardLastMessage = string.Empty;
                RenderVsrGuardPage();
                RefreshStatus();
            });
            refresh.IsEnabled = !vsrGuardBusy;
            refresh.Margin = new Thickness(0);
            topActions.Children.Add(refresh);
            AddSection("Readiness", topActions);

            Brush good = BrushFromRgb(0x35, 0xC5, 0x8A);
            Brush warning = BrushFromRgb(0xF2, 0xB8, 0x4B);
            Brush bad = BrushFromRgb(0xFF, 0x76, 0x76);

            string gpuDescription;
            Brush gpuBrush;
            string gpuBadge;
            if (!vsrGuardSnapshot.NvidiaRtxSupported)
            {
                gpuDescription = vsrGuardSnapshot.NvidiaGpuPresent
                    ? "An NVIDIA GPU was detected, but its name does not identify a supported RTX model."
                    : "No active NVIDIA RTX display adapter was found.";
                gpuBrush = bad;
                gpuBadge = "UNSUPPORTED";
            }
            else
            {
                gpuDescription = vsrGuardSnapshot.NvidiaGpuName + "  |  Driver "
                    + vsrGuardSnapshot.NvidiaDriverVersion;
                gpuBrush = vsrGuardSnapshot.NvidiaGpuHealthy ? good : bad;
                gpuBadge = vsrGuardSnapshot.NvidiaGpuHealthy ? "SUPPORTED" : "DRIVER ISSUE";
            }
            AddCard("NVIDIA RTX GPU", gpuDescription,
                CreateVsrGuardActionPanel(gpuBadge, gpuBrush, null, false, null), false);

            AddCard("Google Chrome",
                vsrGuardSnapshot.ChromeInstalled
                    ? "Version " + vsrGuardSnapshot.ChromeVersion + "  |  " + vsrGuardSnapshot.ChromePath
                    : "Chrome was not found in its registered, system, or per-user install locations.",
                CreateVsrGuardActionPanel(vsrGuardSnapshot.ChromeInstalled ? "FOUND" : "NOT FOUND",
                    vsrGuardSnapshot.ChromeInstalled ? good : bad, null, false, null), false);

            AddCard("Power source",
                vsrGuardSnapshot.OnBatteryPower
                    ? "Browsers normally fall back to their low-power upscaler while the notebook is on battery."
                    : "AC power is available. RTX browser upscaling is allowed to run.",
                CreateVsrGuardActionPanel(vsrGuardSnapshot.OnBatteryPower ? "BATTERY" : "AC POWER",
                    vsrGuardSnapshot.OnBatteryPower ? warning : good, null, false, null), false);

            AddSection("Required settings");

            AddCard("1. Chrome High performance GPU",
                vsrGuardSnapshot.ChromeHighPerformance
                    ? "Windows Graphics preference routes Chrome to the high-performance GPU."
                    : "Required on Optimus notebooks so Chrome can access the NVIDIA RTX GPU.",
                CreateVsrGuardActionPanel(vsrGuardSnapshot.ChromeHighPerformance ? "READY" : "NEEDS SETUP",
                    vsrGuardSnapshot.ChromeHighPerformance ? good : warning,
                    "Open Graphics settings", false,
                    async delegate
                    {
                        await RunVsrGuardActionAsync(delegate
                        {
                            return Task.FromResult(vsrGuardModule.OpenWindowsGraphicsSettings());
                        });
                    }), false);

            string accelerationBadge = !vsrGuardSnapshot.ChromeHardwareAccelerationKnown
                ? "UNKNOWN"
                : (vsrGuardSnapshot.ChromeHardwareAcceleration ? "READY" : "OFF");
            Brush accelerationBrush = vsrGuardSnapshot.ChromeHardwareAccelerationKnown
                && vsrGuardSnapshot.ChromeHardwareAcceleration ? good : warning;
            AddCard("2. Chrome graphics acceleration",
                vsrGuardSnapshot.ChromeHardwareAccelerationKnown
                    ? (vsrGuardSnapshot.ChromeHardwareAcceleration
                        ? "Chrome is configured to use graphics acceleration when available."
                        : "Chrome graphics acceleration is disabled.")
                    : "Chrome Local State has not recorded this setting yet.",
                CreateVsrGuardActionPanel(accelerationBadge, accelerationBrush,
                    "Open Chrome settings", false,
                    async delegate
                    {
                        await RunVsrGuardActionAsync(delegate
                        {
                            return vsrGuardModule.OpenChromeSystemSettingsAsync();
                        });
                    }), false);

            string vsrBadge = !vsrGuardSnapshot.VsrStateKnown
                ? "UNKNOWN"
                : (vsrGuardSnapshot.VsrEnabled ? "ENABLED" : "OFF");
            Brush vsrBrush = vsrGuardSnapshot.VsrStateKnown && vsrGuardSnapshot.VsrEnabled ? good : warning;
            AddCard("3. NVIDIA RTX Video Super Resolution",
                vsrGuardSnapshot.VsrStateKnown
                    ? (vsrGuardSnapshot.VsrEnabled
                        ? "The NVIDIA display driver VSR flag is enabled."
                        : "VSR is currently disabled in the NVIDIA display driver configuration.")
                    : "The active NVIDIA adapter setting could not be read.",
                CreateVsrGuardActionPanel(vsrBadge, vsrBrush,
                    "Open NVIDIA Control Panel", false,
                    async delegate
                    {
                        await RunVsrGuardActionAsync(delegate
                        {
                            return Task.FromResult(vsrGuardModule.OpenNvidiaControlPanel());
                        });
                    }), false);

            if (!string.IsNullOrWhiteSpace(vsrGuardLastMessage))
            {
                AddSection("Last result");
                AddCard(vsrGuardLastActionSucceeded ? "Setup completed" : "Setup needs attention",
                    vsrGuardLastMessage,
                    CreateVsrGuardActionPanel(vsrGuardLastActionSucceeded ? "DONE" : "FAILED",
                        vsrGuardLastActionSucceeded ? good : bad, null, false, null), false);
            }

            RefreshStatus();
        }

        private UIElement CreateNvidiaVideoImageSettingsPanel(Brush good, Brush warning)
        {
            var outer = new Border
            {
                Background = BrushFromRgb(0x18, 0x20, 0x2D),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(10)),
                Padding = ZThickness(22, 20, 22, 20),
                Margin = ZThickness(0, 0, 0, 14)
            };

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.36, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.64, GridUnitType.Star) });

            var deinterlace = new Border
            {
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(8)),
                Padding = ZThickness(18, 16, 18, 16)
            };
            Grid.SetColumn(deinterlace, 0);
            var deinterlaceStack = new StackPanel();
            deinterlaceStack.Children.Add(new TextBlock
            {
                Text = "Deinterlacing",
                FontSize = Z(17),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 14)
            });
            var inverseTelecine = new CheckBox
            {
                Content = "Use inverse telecine",
                IsChecked = true,
                IsEnabled = !vsrGuardBusy && vsrGuardSnapshot.NvidiaGpuPresent,
                FontSize = Z(15),
                Foreground = TextBrush,
                ToolTip = "Click to open this setting in NVIDIA Control Panel."
            };
            inverseTelecine.Click += async delegate
            {
                await RunVsrGuardActionAsync(delegate
                {
                    VsrGuardActionResult result = vsrGuardModule.OpenNvidiaControlPanel();
                    if (result.Success)
                    {
                        result.Message = "Opened NVIDIA Control Panel to adjust inverse telecine.";
                    }
                    return Task.FromResult(result);
                });
            };
            deinterlaceStack.Children.Add(inverseTelecine);
            deinterlace.Child = deinterlaceStack;
            columns.Children.Add(deinterlace);

            var rtx = new Border
            {
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(8)),
                Padding = ZThickness(18, 16, 18, 16)
            };
            Grid.SetColumn(rtx, 2);
            var rtxStack = new StackPanel();
            rtxStack.Children.Add(new TextBlock
            {
                Text = "RTX video enhancement",
                FontSize = Z(17),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 14)
            });

            var superResolution = new CheckBox
            {
                Content = "Super Resolution",
                IsChecked = vsrGuardSnapshot.VsrStateKnown && vsrGuardSnapshot.VsrEnabled,
                IsEnabled = !vsrGuardBusy && vsrGuardSnapshot.VsrStateKnown
                    && vsrGuardSnapshot.NvidiaRtxSupported && vsrGuardSnapshot.NvidiaGpuHealthy,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 8)
            };
            superResolution.Click += async delegate
            {
                bool enable = superResolution.IsChecked == true;
                await RunVsrGuardActionAsync(delegate
                {
                    return enable ? vsrGuardModule.EnableVsrAsync() : vsrGuardModule.DisableVsrAsync();
                });
            };
            rtxStack.Children.Add(superResolution);

            var statusRow = new DockPanel { Margin = ZThickness(0, 0, 0, 14) };
            statusRow.Children.Add(new TextBlock
            {
                Text = "Status: ",
                FontSize = Z(14),
                Foreground = MutedBrush
            });
            statusRow.Children.Add(new TextBlock
            {
                Text = !vsrGuardSnapshot.VsrStateKnown
                    ? "Unknown"
                    : (vsrGuardSnapshot.VsrEnabled ? "Enabled · currently inactive" : "Disabled"),
                FontSize = Z(14),
                FontWeight = FontWeights.SemiBold,
                Foreground = vsrGuardSnapshot.VsrEnabled ? good : warning
            });
            rtxStack.Children.Add(statusRow);

            rtxStack.Children.Add(new TextBlock
            {
                Text = "Quality",
                FontSize = Z(14),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 0, 0, 6)
            });
            var quality = new ComboBox
            {
                MinWidth = Z(190),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = Z(14),
                Padding = ZThickness(10, 7, 10, 7),
                IsEnabled = !vsrGuardBusy && vsrGuardSnapshot.NvidiaGpuPresent,
                ToolTip = "Choose a value to open NVIDIA Control Panel and apply the quality there."
            };
            quality.Items.Add("1");
            quality.Items.Add("2");
            quality.Items.Add("3");
            quality.Items.Add("4");
            quality.SelectedIndex = 2;
            quality.SelectionChanged += async delegate
            {
                await RunVsrGuardActionAsync(delegate
                {
                    VsrGuardActionResult result = vsrGuardModule.OpenNvidiaControlPanel();
                    if (result.Success)
                    {
                        result.Message = "Opened NVIDIA Control Panel to set RTX Video quality to "
                            + (quality.SelectedIndex + 1) + ".";
                    }
                    return Task.FromResult(result);
                });
            };
            rtxStack.Children.Add(quality);

            var hdr = new CheckBox
            {
                Content = "High Dynamic Range",
                IsChecked = false,
                IsEnabled = !vsrGuardBusy,
                FontSize = Z(15),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 16, 0, 4),
                ToolTip = "Click to open Windows display settings and enable HDR first."
            };
            hdr.Click += async delegate
            {
                await RunVsrGuardActionAsync(delegate
                {
                    return Task.FromResult(vsrGuardModule.OpenWindowsHdrSettings());
                });
            };
            rtxStack.Children.Add(hdr);
            rtxStack.Children.Add(new TextBlock
            {
                Text = "Enable HDR in Windows display settings",
                FontSize = Z(13),
                Foreground = MutedBrush
            });

            rtx.Child = rtxStack;
            columns.Children.Add(rtx);
            outer.Child = columns;
            return outer;
        }

        private UIElement CreateVsrGuardActionPanel(string badgeText, Brush badgeBrush,
            string actionText, bool primary, RoutedEventHandler action)
        {
            var panel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border badge = CreateUacGuardBadge(badgeText, badgeBrush);
            if (actionText != null)
            {
                Button button = CreateActionButton(vsrGuardBusy ? "Working..." : actionText, primary, action);
                button.IsEnabled = !vsrGuardBusy;
                button.Margin = ZThickness(0, 0, 8, 0);
                panel.Children.Add(button);
            }
            badge.Margin = new Thickness(0);
            panel.Children.Add(badge);
            return panel;
        }

        private async Task RunVsrGuardActionAsync(Func<Task<VsrGuardActionResult>> action)
        {
            if (vsrGuardBusy || action == null)
            {
                return;
            }

            vsrGuardBusy = true;
            vsrGuardLastMessage = string.Empty;
            RenderVsrGuardPage();
            try
            {
                VsrGuardActionResult result = await action();
                vsrGuardLastActionSucceeded = result != null && result.Success;
                vsrGuardLastMessage = result == null ? "The VSR action returned no result." : result.Message;
            }
            catch (Exception ex)
            {
                vsrGuardLastActionSucceeded = false;
                vsrGuardLastMessage = ex.Message;
                AppLog.Write("VsrGuard", "VSR Guard action failed: " + ex);
            }
            finally
            {
                vsrGuardBusy = false;
                RenderVsrGuardPage();
            }
        }

        private void RenderPowerGuardPage()
        {
            PowerGuardState state = powerGuardModule.GetState();
            powerGuardRenderedDurationKind = state.DurationKind;
            powerGuardDragging = false;
            powerGuardEnabledToggle = null;
            powerGuardDisplayToggle = null;
            powerGuardDurationComboBox = null;
            powerGuardTimelineSlider = null;
            powerGuardTimelineMaximumText = null;
            powerGuardRemainingText = null;
            powerGuardEndText = null;
            powerGuardErrorText = null;

            ConfigureHeader("Power Guard",
                "暫時防止 Windows 因閒置自動睡眠，不修改原始電源計畫。", false);
            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            AddSection("暫時保持清醒");
            powerGuardEnabledToggle = CreatePowerGuardMainToggle(state);
            AddCard("保持清醒", "防止系統因閒置自動進入睡眠。",
                powerGuardEnabledToggle, false);

            StackPanel displayGroup = AddIndentedGroup();
            powerGuardDisplayToggle = CreatePowerGuardDisplayToggle(state);
            AddCardToPanel(displayGroup, "螢幕恆亮",
                "關閉時，螢幕仍依 Windows 原始設定自動熄滅。",
                powerGuardDisplayToggle, false);

            AddSection("保持時間");
            powerGuardDurationComboBox = CreatePowerGuardDurationCombo(state);
            AddCard("預設保持時間",
                state.IsEnabled
                    ? "變更有限時間會從目前時間重新開始完整倒數。"
                    : "保持清醒關閉時，只更新預設時間與時間軸範圍。",
                powerGuardDurationComboBox, false);

            if (state.DurationKind == PowerGuardDurationKind.Custom)
            {
                AddCard("自訂時間", "可設定 1 分鐘至 30 天。",
                    CreatePowerGuardCustomDurationEditor(state.SelectedDuration), false);
            }

            AddSection("剩餘時間");
            if (state.DurationKind == PowerGuardDurationKind.UntilManual)
            {
                AddCard("未設定結束時間", "保持清醒會持續到手動關閉。",
                    CreatePowerGuardManualTimeStatus(), false);
            }
            else
            {
                AddCard("倒數時間軸",
                    state.IsEnabled ? "拖曳後放開即可套用新的剩餘時間。" : "開啟保持清醒後才會開始倒數。",
                    CreatePowerGuardTimeline(state), false);
            }

            AddSection("操作");
            var actions = CreateButtonRow();
            actions.Children.Add(CreateActionButton("開啟 Windows 電源與睡眠設定", false,
                delegate { controller.OpenPowerSettings(); }));
            AddCard("Windows 電源設定",
                "檢視 Windows 目前的顯示器與睡眠設定。",
                actions, false);

            var info = new TextBlock
            {
                Text = "Power Request 可能受 Windows 原則、Modern Standby、電池與鎖定畫面行為限制。",
                FontSize = Z(12),
                Foreground = SubtleBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(2, 6, 2, 0),
                ToolTip = "Power Guard 使用 Windows Power Request API；不修改電源計畫。"
            };
            SettingsStack.Children.Add(info);

            powerGuardErrorText = new TextBlock
            {
                FontSize = Z(13),
                Foreground = BrushFromRgb(0xFF, 0x76, 0x76),
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(2, 8, 2, 0),
                Visibility = Visibility.Collapsed
            };
            SettingsStack.Children.Add(powerGuardErrorText);
            RefreshPowerGuardState();
        }

        private void RenderUacGuardPage()
        {
            RenderUacGuardPageCore();
            BeginUacGuardRefresh();
        }

        private void RenderUacGuardPageCore()
        {
            ConfigureHeader("UAC Guard",
                "可選 Codex 行程週期，或由 Guard Center 開啟到完全退出的類永久授權。", false);
            HeaderHelpButton.Visibility = Visibility.Visible;
            SettingsStack.Children.Clear();
            audioDependentCards.Clear();
            bool lifecycleSelected = string.Equals(settings.UacGuard.AuthorizationMode,
                UacGuardModule.GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase);

            var statusActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Button install = CreateActionButton(IsUacGuardOperationBusy("install") ? "處理中…"
                    : (uacGuardStatus != null && uacGuardStatus.IsReady
                        ? "一鍵重新安裝／修復" : "一鍵安裝／修復"),
                true, async delegate { await RunUacGuardElevatedActionAsync("install"); });
            install.Margin = ZThickness(0, 0, 8, 0);
            install.IsEnabled = !uacGuardBusy;
            statusActions.Children.Add(install);
            Button refresh = CreateActionButton("重新檢查", false,
                delegate { BeginUacGuardRefresh(); });
            refresh.IsEnabled = !uacGuardBusy && uacGuardStatus != null;
            statusActions.Children.Add(refresh);
            AddSection("狀態總覽", statusActions);
            if (uacGuardStatus == null)
            {
                AddCard("正在檢查 UAC Guard",
                    "正在讀取 OpenAI.Codex 套件、工作排程、ACL 與選用捷徑。",
                    CreateUacGuardBadge("CHECKING", SubtleBrush), false);
            }
            else
            {
                AddCard(uacGuardStatus.IsReady ? "免 UAC 管理員入口已就緒" : "UAC Guard 尚未完成設定",
                    uacGuardStatus.IsReady
                        ? (uacGuardStatus.GsudoSessionActive
                            ? "自動授權已生效；ChatGPT/Codex GUI 本身仍維持一般權限。"
                            : "自動授權元件已就緒，正在等待所選生命週期的目標程序。")
                        : string.Join("  ", uacGuardStatus.Issues.ToArray()),
                    CreateUacGuardBadge(uacGuardStatus.IsReady ? "READY" : "ACTION NEEDED",
                        uacGuardStatus.IsReady ? BrushFromRgb(0x35, 0xC5, 0x8A)
                            : BrushFromRgb(0xF0, 0xB3, 0x4A)), false);

                AddCard("OpenAI.Codex 套件",
                    uacGuardStatus.CodexInstalled
                        ? "Version " + uacGuardStatus.CodexVersion + " · 每次啟動會重新解析目前版本路徑並驗證 OpenAI 簽章。"
                        : "找不到目前使用者安裝的 OpenAI.Codex 套件。",
                    CreateUacGuardBadge(uacGuardStatus.CodexInstalled ? "FOUND" : "MISSING",
                        uacGuardStatus.CodexInstalled ? BrushFromRgb(0x35, 0xC5, 0x8A) : BrushFromRgb(0xE8, 0x5D, 0x68)), false);
                AddCard("gsudo 提權引擎",
                    uacGuardStatus.GsudoInstalled
                        ? "已安裝 gsudo " + uacGuardStatus.GsudoVersion
                            + (string.Equals(settings.UacGuard.AuthorizationMode,
                                UacGuardModule.GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                                ? "；目前選擇 Guard Center 生命週期模式。"
                                : "；目前只把管理員快取限定給 Codex PID。")
                        : "尚未安裝官方 gsudo；按上方「一鍵安裝／修復」會自動安裝。",
                    CreateUacGuardBadge(uacGuardStatus.GsudoInstalled ? "FOUND" : "MISSING",
                        uacGuardStatus.GsudoInstalled ? BrushFromRgb(0x35, 0xC5, 0x8A)
                            : BrushFromRgb(0xE8, 0x5D, 0x68)), false);
                AddCard("自動授權排程",
                    uacGuardStatus.LifecycleTaskConfigurationValid
                        ? "固定執行 Program Files 內受 ACL 保護的授權監護程序。"
                        : (uacGuardStatus.LifecycleTaskInstalled
                            ? "排程存在，但內容不符合預期；請執行修復。"
                            : "尚未安裝；若要使用 Guard Center 生命週期模式，請先執行修復。"),
                    CreateUacGuardBadge(uacGuardStatus.LifecycleTaskConfigurationValid ? "READY" : "NOT READY",
                        uacGuardStatus.LifecycleTaskConfigurationValid ? BrushFromRgb(0x35, 0xC5, 0x8A)
                            : BrushFromRgb(0xF0, 0xB3, 0x4A)), false);
                AddCard("受保護監護程式與 ACL",
                    uacGuardStatus.RunnerAclProtected
                        ? "授權 Host 位於 Program Files；一般 Users 只有讀取與執行權限。"
                        : (uacGuardStatus.RunnerInstalled ? "監護程式存在，但 ACL 檢查未通過。" : "受保護監護程式尚未安裝。"),
                    CreateUacGuardBadge(uacGuardStatus.RunnerAclProtected ? "PROTECTED" : "NOT READY",
                        uacGuardStatus.RunnerAclProtected ? BrushFromRgb(0x35, 0xC5, 0x8A) : BrushFromRgb(0xE8, 0x5D, 0x68)), false);
                AddCard("目前管理員工作階段",
                    uacGuardStatus.GsudoSessionActive
                        ? uacGuardStatus.GsudoSessionMessage
                            + " 需要管理員權限的命令可透過 gsudo 免再次 UAC 執行。"
                        : (settings.UacGuard.AutomaticAuthorizationPaused
                            ? "自動授權已暫停；按「立即重新偵測／授權」恢復，或重新啟動 Guard Center。"
                            : (lifecycleSelected
                                ? "自動監測器正在建立 Guard Center 生命週期授權。"
                                : (uacGuardStatus.CodexIsRunning
                            ? "Codex 正在執行；自動監測器正在建立限定 PID 的 gsudo 管理員工作階段。"
                            : "等待 Codex 啟動；偵測到 app-server PID 後會自動授權。"))),
                    CreateUacGuardBadge(uacGuardStatus.GsudoSessionActive ? "ADMIN SESSION" : "INACTIVE",
                        uacGuardStatus.GsudoSessionActive ? BrushFromRgb(0x35, 0xC5, 0x8A) : SubtleBrush), false);
            }

            AddSection("授權模式");
            var modePanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            var modeSelector = new ComboBox
            {
                MinWidth = Z(310),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = ZThickness(10, 7, 10, 7),
                IsEnabled = !uacGuardBusy
            };
            modeSelector.Items.Add("Codex 行程週期（建議）");
            modeSelector.Items.Add("Guard Center 生命週期（高風險）");
            modeSelector.SelectedIndex = lifecycleSelected ? 1 : 0;
            modeSelector.SelectionChanged += async delegate
            {
                string requestedMode = modeSelector.SelectedIndex == 1
                    ? UacGuardModule.GuardCenterLifecycleMode : UacGuardModule.CodexProcessMode;
                await ChangeUacGuardAuthorizationModeAsync(requestedMode);
            };
            modePanel.Children.Add(modeSelector);
            AddCard(lifecycleSelected ? "Guard Center 生命週期" : "Codex 行程週期",
                lifecycleSelected
                    ? "Guard Center 開啟時自動授權，完全退出時撤銷；Codex 可任意重啟。期間同一使用者的其他程式也可能使用 gsudo。"
                    : "偵測到 Codex 後自動授權目前 PID 與其子程序；Codex 結束即撤銷，重新啟動後自動綁定新 PID。",
                modePanel, false);

            AddSection("管理操作");
            var gsudoButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            bool gsudoSessionActive = uacGuardStatus != null && uacGuardStatus.GsudoSessionActive;
            Border gsudoSessionBadge = CreateUacGuardBadge(
                gsudoSessionActive ? "ACTIVE" : "INACTIVE",
                gsudoSessionActive ? BrushFromRgb(0x35, 0xC5, 0x8A) : SubtleBrush);
            Button startGsudo = CreateActionButton(
                IsUacGuardOperationBusy("gsudo-start") ? "授權中…" : "立即重新偵測／授權", true,
                async delegate { await RunUacGuardGsudoSessionAsync(true); });
            startGsudo.Margin = ZThickness(0, 0, 8, 8);
            startGsudo.IsEnabled = !uacGuardBusy && uacGuardStatus != null
                && uacGuardStatus.GsudoInstalled
                && (lifecycleSelected ? uacGuardStatus.LifecycleTaskConfigurationValid
                    : uacGuardStatus.CodexIsRunning)
                && !uacGuardStatus.GsudoSessionActive;
            gsudoButtons.Children.Add(startGsudo);
            Button stopGsudo = CreateActionButton(
                IsUacGuardOperationBusy("gsudo-stop") ? "終止中…" : "立即終止工作階段", false,
                async delegate { await RunUacGuardGsudoSessionAsync(false); });
            stopGsudo.Margin = ZThickness(0, 0, 0, 8);
            stopGsudo.IsEnabled = !uacGuardBusy && uacGuardStatus != null
                && uacGuardStatus.GsudoSessionActive;
            gsudoButtons.Children.Add(stopGsudo);
            AddCard("gsudo 管理員工作階段",
                lifecycleSelected
                    ? "啟動 Guard Center 時由預先授權排程自動建立；按下終止後，可用左側按鈕重新建立。"
                    : "Guard Center 會自動偵測 Codex 並透過預先授權排程綁定 PID，全程不需要另外按 UAC。",
                gsudoButtons, false, gsudoSessionBadge);

            var inspectButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Button taskScheduler = CreateActionButton("工作排程器", false, delegate
            {
                try
                {
                    uacGuardModule.OpenTaskScheduler();
                    AppendUacGuardLog("已開啟 Windows 工作排程器。");
                }
                catch (Exception ex)
                {
                    ShowUacGuardResult(new UacGuardActionResult { Success = false, Message = ex.Message, Timestamp = DateTimeOffset.Now });
                }
            });
            taskScheduler.Margin = ZThickness(0, 0, 8, 8);
            inspectButtons.Children.Add(taskScheduler);
            Button folder = CreateActionButton("受保護目錄", false, delegate
            {
                try
                {
                    uacGuardModule.OpenInstallDirectory();
                    AppendUacGuardLog("已開啟 UAC Guard 受保護目錄。");
                }
                catch (Exception ex)
                {
                    ShowUacGuardResult(new UacGuardActionResult { Success = false, Message = ex.Message, Timestamp = DateTimeOffset.Now });
                }
            });
            folder.Margin = ZThickness(0, 0, 0, 8);
            folder.IsEnabled = uacGuardStatus != null && uacGuardStatus.RunnerInstalled;
            inspectButtons.Children.Add(folder);
            AddCard("檢查實際設定",
                "直接檢視排程與 Program Files 內的授權監護程式，不需要依賴隱藏狀態。",
                inspectButtons, false);

            Button uninstall = CreateActionButton(
                IsUacGuardOperationBusy("uninstall") ? "處理中…" : "解除安裝", false,
                async delegate { await RunUacGuardElevatedActionAsync("uninstall"); });
            uninstall.Foreground = BrushFromRgb(0xFF, 0xB6, 0xBD);
            uninstall.BorderBrush = BrushFromRgb(0x71, 0x39, 0x43);
            uninstall.Background = BrushFromRgb(0x35, 0x21, 0x27);
            uninstall.IsEnabled = !uacGuardBusy && uacGuardStatus != null
                && (uacGuardStatus.LifecycleTaskInstalled || uacGuardStatus.RunnerInstalled);
            AddCard("解除安裝 UAC Guard",
                "只移除自動授權排程、監護程式與舊版 Codex Admin 遺留物；不刪除 Codex、對話或設定，也不修改 Windows UAC 原則。",
                uninstall, false);
        }

        private void ShowUacGuardHelp()
        {
            if (activeUacGuardHelpDialog != null)
            {
                System.Media.SystemSounds.Exclamation.Play();
                if (activeUacGuardHelpDialog.WindowState == WindowState.Minimized)
                {
                    activeUacGuardHelpDialog.WindowState = WindowState.Normal;
                }
                activeUacGuardHelpDialog.Activate();
                return;
            }

            Window dialog = CreateGameHelperDialog("UAC Guard · 運作方式與安全邊界", Z(840), Z(740));
            dialog.MinWidth = Math.Min(Z(650), dialog.MaxWidth);
            dialog.MinHeight = Math.Min(Z(520), dialog.MaxHeight);
            dialog.Width = Clamp(dialog.Width, dialog.MinWidth, dialog.MaxWidth);
            dialog.Height = Clamp(dialog.Height, dialog.MinHeight, dialog.MaxHeight);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Loaded += delegate { EnsureDialogWithinWorkArea(dialog); };
            activeUacGuardHelpDialog = dialog;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel { Margin = ZThickness(30, 26, 30, 18) };
            heading.Children.Add(new TextBlock
            {
                Text = "UAC Guard 說明",
                FontSize = Z(25),
                FontWeight = FontWeights.Bold,
                Foreground = TextBrush
            });
            heading.Children.Add(new TextBlock
            {
                Text = "兩種模式都會自動授權；差別只在權限綁定的程序與結束時機。",
                FontSize = Z(13),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(heading);

            var details = new StackPanel { Margin = ZThickness(30, 0, 30, 22) };
            details.Children.Add(CreateUacGuardExpander("這個功能在做什麼",
                "讓 Codex 執行已授權的管理員命令時，不必逐次等待人工按 UAC。",
                new string[]
                {
                    "• Guard Center 透過預先建立的 Highest Privileges 排程啟動受保護監護程式。",
                    "• 監護程式使用 gsudo 建立管理員快取，Codex 只在命令確實需要時透過 gsudo 提權。",
                    "• ChatGPT/Codex GUI 與一般命令仍維持普通權限，不會整個程式常駐 Administrator。",
                    "• 這不是自動點擊 UAC，也不會關閉 Windows UAC 或修改 EnableLUA。"
                }, true));
            details.Children.Add(CreateUacGuardExpander("兩種自動授權模式",
                "選單只決定生命週期綁定誰；兩種模式都不需要日常人工 UAC。",
                new string[]
                {
                    "• Codex 行程週期（建議）：偵測到 Codex 後自動綁定目前 app-server PID 與子程序；Codex 結束即撤銷。",
                    "• Codex 重新啟動時，只要 Guard Center 仍在執行，就會自動偵測並綁定新的 PID。",
                    "• Guard Center 在 Codex 模式下退出，不會中斷仍在執行的 Codex；但之後的新 Codex 需要 Guard Center 才能自動綁定。",
                    "• Guard Center 生命週期（高風險）：Guard Center 開啟即授權、完全退出即撤銷，Codex 可任意開關。",
                    "• Guard Center 模式會讓同一 Windows 使用者的其他程序也可能使用 gsudo，因此便利性較高、隔離範圍較大。"
                }, false));
            details.Children.Add(CreateUacGuardExpander("按鈕與狀態",
                "一般使用只需要選好模式；其餘按鈕用於立即控制或維修。",
                new string[]
                {
                    "• ADMIN SESSION：目前自動授權已生效；管理員命令可以透過 gsudo 執行。",
                    "• 立即終止工作階段：撤銷快取，並暫停本次 Guard Center 執行期間的自動授權。",
                    "• 立即重新偵測／授權：解除暫停並依目前模式重新建立工作階段。",
                    "• 安裝／修復自動授權元件：只在首次安裝、版本更新，或排程、受保護檔案、ACL 異常時使用。",
                    "• 關閉主視窗只會縮到系統匣；選擇系統匣的 Exit 才算完全退出 Guard Center。"
                }, false));
            details.Children.Add(CreateUacGuardExpander("安全邊界",
                "免逐次 UAC 是刻意的權限取捨，不等於消除 Windows 安全邊界。",
                new string[]
                {
                    "• 能使用快取的程序若遭惡意注入，攻擊者可能藉此執行靜默管理員命令。",
                    "• Codex 行程週期隔離範圍較小，因此是預設建議；Guard Center 生命週期明確標示為高風險。",
                    "• 監護程式安裝在 Program Files 並套用 ACL，一般使用者只能讀取與執行，不能改寫固定入口。",
                    "• Codex 不得自行建立、延長或擴大快取；專案 AGENTS.md 只允許它消費 UAC Guard 已建立的授權。",
                    "• 離開電腦或完成系統工作時，可立即終止工作階段；一般開發命令應維持普通權限。"
                }, false));
            details.Children.Add(CreateUacGuardExpander("狀態異常時怎麼做",
                "先看狀態卡，再決定是否需要修復。",
                new string[]
                {
                    "• 找不到 gsudo：先安裝官方 gsudo 套件，再重新檢查。",
                    "• 自動授權排程或 ACL 顯示 NOT READY：執行「安裝／修復自動授權元件」。",
                    "• Codex 模式顯示 INACTIVE：確認 Codex 已啟動並等待數秒，或按「立即重新偵測／授權」。",
                    "• Codex 必須具備 Full Access 才能呼叫本機 gsudo；sandbox 權限與 Windows Administrator 是不同層級。",
                    "• 若快取不存在，Codex 應回報並要求回到本頁，而不應在背景突然觸發 UAC。"
                }, false));

            details.Children.Add(new TextBlock
            {
                Text = "操作紀錄",
                FontSize = Z(18),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(2, 18, 0, 8)
            });
            uacGuardHelpLogBox = CreateUacGuardLogBox();
            details.Children.Add(uacGuardHelpLogBox);

            var scroll = new ScrollViewer
            {
                Content = details,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Action updateContentWidth = delegate
            {
                double availableWidth = scroll.ViewportWidth;
                if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
                {
                    availableWidth = scroll.ActualWidth - Math.Max(Z(18), SystemParameters.VerticalScrollBarWidth);
                }
                if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
                {
                    availableWidth = dialog.ActualWidth - Z(90);
                }
                if (availableWidth > 0)
                {
                    details.Width = Math.Max(Z(320), availableWidth - Z(60));
                }
            };
            scroll.SizeChanged += delegate { updateContentWidth(); };
            dialog.SizeChanged += delegate { updateContentWidth(); };
            dialog.Loaded += delegate
            {
                dialog.Dispatcher.BeginInvoke(new Action(updateContentWidth), DispatcherPriority.Render);
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var footer = new Grid { Margin = ZThickness(30, 14, 30, 24) };
            Button close = CreateActionButton("關閉", false, delegate { dialog.Close(); });
            close.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Children.Add(close);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            dialog.Content = root;
            dialog.Closed += delegate
            {
                if (ReferenceEquals(activeUacGuardHelpDialog, dialog))
                {
                    activeUacGuardHelpDialog = null;
                    uacGuardHelpLogBox = null;
                }
            };
            dialog.Show();
        }

        private TextBox CreateUacGuardLogBox()
        {
            return new TextBox
            {
                IsReadOnly = true,
                Text = GetUacGuardLogText(),
                MinHeight = Z(118),
                MaxHeight = Z(220),
                Padding = ZThickness(14, 12, 14, 12),
                Background = BrushFromRgb(0x12, 0x18, 0x24),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = MutedBrush,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = Z(11.5),
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private string GetUacGuardLogText()
        {
            return uacGuardLog.Count == 0
                ? "尚未執行管理操作。"
                : string.Join(Environment.NewLine, uacGuardLog.ToArray());
        }

        private void BeginUacGuardRefresh()
        {
            int version = ++uacGuardRefreshVersion;
            RefreshUacGuardStatusAsync(version);
        }

        private async void RefreshUacGuardStatusAsync(int version)
        {
            try
            {
                UacGuardStatus status = await uacGuardModule.GetStatusAsync();
                if (version != uacGuardRefreshVersion)
                {
                    return;
                }
                uacGuardStatus = status;
                StatusTextBlock.Text = status.StatusText;
                if (selectedModuleIndex == 7)
                {
                    RenderUacGuardPageCore();
                }
            }
            catch (Exception ex)
            {
                AppendUacGuardLog("ERROR  狀態檢查失敗：" + ex.Message);
                StatusTextBlock.Text = "UAC Guard status check failed.";
            }
        }

        private async Task RunUacGuardElevatedActionAsync(string action)
        {
            if (uacGuardBusy)
            {
                return;
            }
            if (string.Equals(action, "uninstall", StringComparison.OrdinalIgnoreCase))
            {
                MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                    "將移除 UAC Guard 建立的排程、受保護啟動器與桌面捷徑。\n\nCodex 本體、對話、設定與 Windows UAC 原則不會受到影響。",
                    "解除安裝 UAC Guard", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            uacGuardBusyOperation = string.Equals(action, "uninstall", StringComparison.OrdinalIgnoreCase)
                ? "uninstall" : "install";
            uacGuardBusy = true;
            bool automaticAuthorizationWasPaused = settings.UacGuard.AutomaticAuthorizationPaused;
            settings.UacGuard.AutomaticAuthorizationPaused = true;
            AppendUacGuardLog((action == "uninstall" ? "開始解除安裝"
                : "開始一鍵安裝必要套件與 UAC Guard 元件") + "；等待 Windows UAC 核准。");
            RenderUacGuardPageCore();
            try
            {
                UacGuardActionResult result = await uacGuardModule.RequestElevatedActionAsync(action);
                ShowUacGuardResult(result);
                if (result.Success && string.Equals(action, "uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    settings.UacGuard.AuthorizationMode = UacGuardModule.CodexProcessMode;
                    controller.SaveSettings();
                }
                else if (result.Success && string.Equals(settings.UacGuard.AuthorizationMode,
                    UacGuardModule.GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase))
                {
                    UacGuardActionResult lifecycle = await uacGuardModule.StartLifecycleGsudoSessionAsync();
                    ShowUacGuardResult(lifecycle, false);
                }
            }
            finally
            {
                settings.UacGuard.AutomaticAuthorizationPaused = automaticAuthorizationWasPaused;
                uacGuardBusy = false;
                uacGuardBusyOperation = string.Empty;
                uacGuardStatus = await uacGuardModule.GetStatusAsync();
                if (selectedModuleIndex == 7)
                {
                    RenderUacGuardPageCore();
                    StatusTextBlock.Text = uacGuardStatus.StatusText;
                }
            }
        }

        private async Task RunUacGuardGsudoSessionAsync(bool start)
        {
            if (uacGuardBusy)
            {
                return;
            }

            uacGuardBusyOperation = start ? "gsudo-start" : "gsudo-stop";
            uacGuardBusy = true;
            settings.UacGuard.AutomaticAuthorizationPaused = !start;
            AppendUacGuardLog(start
                ? (string.Equals(settings.UacGuard.AuthorizationMode,
                        UacGuardModule.GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                    ? "正在透過預先授權排程建立 Guard Center 生命週期授權。"
                    : "正在透過預先授權排程，自動綁定目前 Codex PID。")
                : "正在立即終止所有 gsudo 快取工作階段。");
            RenderUacGuardPageCore();
            try
            {
                UacGuardActionResult result = start
                    ? (string.Equals(settings.UacGuard.AuthorizationMode,
                            UacGuardModule.GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                        ? await uacGuardModule.StartLifecycleGsudoSessionAsync()
                        : await uacGuardModule.StartGsudoSessionAsync())
                    : await uacGuardModule.StopGsudoSessionAsync();
                ShowUacGuardResult(result, false);
            }
            finally
            {
                uacGuardBusy = false;
                uacGuardBusyOperation = string.Empty;
                uacGuardStatus = await uacGuardModule.GetStatusAsync();
                if (selectedModuleIndex == 7)
                {
                    RenderUacGuardPageCore();
                    StatusTextBlock.Text = uacGuardStatus.StatusText;
                }
            }
        }

        private async Task ChangeUacGuardAuthorizationModeAsync(string requestedMode)
        {
            requestedMode = UacGuardModule.NormalizeAuthorizationMode(requestedMode);
            string currentMode = UacGuardModule.NormalizeAuthorizationMode(
                settings.UacGuard.AuthorizationMode);
            if (uacGuardBusy || string.Equals(requestedMode, currentMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(requestedMode, UacGuardModule.GuardCenterLifecycleMode,
                StringComparison.OrdinalIgnoreCase))
            {
                MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                    "Guard Center 開啟期間，gsudo 授權會允許同一 Windows 使用者的其他程序請求管理員命令。\n\n"
                    + "Guard Center 完全退出或你按下「立即終止工作階段」時會撤銷；Codex 中途開關不受影響。\n\n"
                    + "確定切換到此高風險模式嗎？",
                    "啟用 Guard Center 生命週期授權", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes)
                {
                    RenderUacGuardPageCore();
                    return;
                }
            }

            uacGuardBusy = true;
            uacGuardBusyOperation = "mode-change";
            RenderUacGuardPageCore();
            try
            {
                await uacGuardModule.StopGsudoSessionAsync();
                settings.UacGuard.AutomaticAuthorizationPaused = false;
                settings.UacGuard.AuthorizationMode = requestedMode;
                controller.SaveSettings();

                if (string.Equals(requestedMode, UacGuardModule.GuardCenterLifecycleMode,
                    StringComparison.OrdinalIgnoreCase))
                {
                    UacGuardActionResult start = await uacGuardModule.StartLifecycleGsudoSessionAsync();
                    if (!start.Success)
                    {
                        settings.UacGuard.AuthorizationMode = currentMode;
                        controller.SaveSettings();
                    }
                    ShowUacGuardResult(start, false);
                }
                else
                {
                    ShowUacGuardResult(new UacGuardActionResult
                    {
                        Success = true,
                        Message = "已切換到 Codex 行程週期；偵測到 Codex 後會自動綁定目前 PID。",
                        Timestamp = DateTimeOffset.Now
                    }, false);
                }
            }
            finally
            {
                uacGuardBusy = false;
                uacGuardBusyOperation = string.Empty;
                uacGuardStatus = await uacGuardModule.GetStatusAsync();
                if (selectedModuleIndex == 7)
                {
                    RenderUacGuardPageCore();
                    StatusTextBlock.Text = uacGuardStatus.StatusText;
                }
            }
        }

        private bool IsUacGuardOperationBusy(string operation)
        {
            return uacGuardBusy && string.Equals(uacGuardBusyOperation, operation,
                StringComparison.Ordinal);
        }

        private Border CreateUacGuardBadge(string text, Brush brush)
        {
            return new Border
            {
                Background = BrushFromRgb(0x15, 0x22, 0x38),
                BorderBrush = brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(11)),
                Padding = ZThickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = brush,
                    FontSize = Z(10.5),
                    FontWeight = FontWeights.SemiBold
                }
            };
        }

        private Expander CreateUacGuardExpander(string title, string summary, IList<string> lines, bool expanded)
        {
            var header = new StackPanel { Margin = ZThickness(8, 5, 8, 5) };
            header.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = TextBrush,
                FontSize = Z(14),
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = summary,
                Foreground = MutedBrush,
                FontSize = Z(11.5),
                Margin = ZThickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            var content = new StackPanel { Margin = ZThickness(16, 4, 16, 15) };
            if (lines != null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = lines[i],
                        Foreground = MutedBrush,
                        FontSize = Z(12.5),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = ZThickness(0, 4, 0, 4)
                    });
                }
            }

            return new Expander
            {
                Header = header,
                Content = content,
                IsExpanded = expanded,
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 8),
                Padding = ZThickness(6, 4, 6, 4)
            };
        }

        private void AppendUacGuardLog(string message)
        {
            uacGuardLog.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
            while (uacGuardLog.Count > 80)
            {
                uacGuardLog.RemoveAt(0);
            }
            if (uacGuardHelpLogBox != null)
            {
                uacGuardHelpLogBox.Text = GetUacGuardLogText();
                uacGuardHelpLogBox.ScrollToEnd();
            }
            AppLog.Write("UAC Guard", message);
        }

        private void ShowUacGuardResult(UacGuardActionResult result, bool showSuccessDialog = true)
        {
            AppendUacGuardLog((result.Success ? "OK     " : "ERROR  ") + result.Message);
            StatusTextBlock.Text = result.Message;
            if (!result.Success || showSuccessDialog)
            {
                System.Windows.MessageBox.Show(result.Message,
                    result.Success ? "UAC Guard" : "UAC Guard 操作失敗",
                    MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
        }

        private UiControls.ToggleSwitch CreatePowerGuardMainToggle(PowerGuardState state)
        {
            var toggle = new UiControls.ToggleSwitch
            {
                IsChecked = state.IsEnabled,
                Width = Z(58),
                Height = Z(30),
                OnContent = string.Empty,
                OffContent = string.Empty
            };
            RoutedEventHandler changed = delegate
            {
                if (powerGuardUiSyncing)
                {
                    return;
                }

                string error;
                controller.SetPowerGuardEnabled(toggle.IsChecked == true, out error);
            };
            toggle.Checked += changed;
            toggle.Unchecked += changed;
            return toggle;
        }

        private UiControls.ToggleSwitch CreatePowerGuardDisplayToggle(PowerGuardState state)
        {
            var toggle = new UiControls.ToggleSwitch
            {
                IsChecked = state.KeepDisplayOn,
                IsEnabled = state.IsEnabled,
                Width = Z(58),
                Height = Z(30),
                OnContent = string.Empty,
                OffContent = string.Empty
            };
            RoutedEventHandler changed = delegate
            {
                if (powerGuardUiSyncing)
                {
                    return;
                }

                string error;
                controller.SetPowerGuardDisplayOn(toggle.IsChecked == true, out error);
            };
            toggle.Checked += changed;
            toggle.Unchecked += changed;
            return toggle;
        }

        private ComboBox CreatePowerGuardDurationCombo(PowerGuardState state)
        {
            var combo = new ComboBox
            {
                Width = Z(190),
                FontSize = Z(13),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AddPowerGuardDurationItem(combo, "直到手動關閉", PowerGuardDurationKind.UntilManual, state.DurationKind);
            AddPowerGuardDurationItem(combo, "30 分鐘", PowerGuardDurationKind.ThirtyMinutes, state.DurationKind);
            AddPowerGuardDurationItem(combo, "1 小時", PowerGuardDurationKind.OneHour, state.DurationKind);
            AddPowerGuardDurationItem(combo, "2 小時", PowerGuardDurationKind.TwoHours, state.DurationKind);
            AddPowerGuardDurationItem(combo, "自訂時間", PowerGuardDurationKind.Custom, state.DurationKind);

            combo.SelectionChanged += delegate
            {
                if (powerGuardUiSyncing)
                {
                    return;
                }

                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null || !(item.Tag is PowerGuardDurationKind))
                {
                    return;
                }

                PowerGuardDurationKind kind = (PowerGuardDurationKind)item.Tag;
                TimeSpan custom = kind == PowerGuardDurationKind.Custom
                    ? powerGuardModule.GetState().CustomDuration
                    : TimeSpan.Zero;
                controller.SelectPowerGuardDuration(kind, custom);
            };
            return combo;
        }

        private static void AddPowerGuardDurationItem(ComboBox combo, string label,
            PowerGuardDurationKind kind, PowerGuardDurationKind selected)
        {
            var item = new ComboBoxItem { Content = label, Tag = kind };
            combo.Items.Add(item);
            if (kind == selected)
            {
                combo.SelectedItem = item;
            }
        }

        private UIElement CreatePowerGuardCustomDurationEditor(TimeSpan duration)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            int totalMinutes = (int)Math.Round(duration.TotalMinutes);
            var days = new UiControls.TextBox
            {
                Width = Z(72),
                Text = (totalMinutes / 1440).ToString(),
                MaxLength = 2,
                ClearButtonEnabled = false,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = ZThickness(8, 5, 8, 5),
            };
            var hours = new UiControls.TextBox
            {
                Width = Z(72),
                Text = ((totalMinutes % 1440) / 60).ToString(),
                MaxLength = 2,
                ClearButtonEnabled = false,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = ZThickness(8, 5, 8, 5),
                Margin = ZThickness(12, 0, 0, 0)
            };
            var minutes = new UiControls.TextBox
            {
                Width = Z(72),
                Text = (totalMinutes % 60).ToString(),
                MaxLength = 2,
                ClearButtonEnabled = false,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = ZThickness(8, 5, 8, 5),
                Margin = ZThickness(12, 0, 0, 0)
            };
            ConfigurePowerGuardNumericTextBox(days);
            ConfigurePowerGuardNumericTextBox(hours);
            ConfigurePowerGuardNumericTextBox(minutes);
            panel.Children.Add(days);
            panel.Children.Add(new TextBlock
            {
                Text = "天",
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(6, 0, 0, 0)
            });
            panel.Children.Add(hours);
            panel.Children.Add(new TextBlock
            {
                Text = "小時",
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(6, 0, 0, 0)
            });
            panel.Children.Add(minutes);
            panel.Children.Add(new TextBlock
            {
                Text = "分鐘",
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(6, 0, 0, 0)
            });
            panel.Children.Add(CreateActionButton("套用", true, delegate
            {
                int dayValue = int.TryParse(days.Text, out int parsedDays) ? parsedDays : 0;
                int hourValue = int.TryParse(hours.Text, out int parsedHours) ? parsedHours : 0;
                int minuteValue = int.TryParse(minutes.Text, out int parsedMinutes) ? parsedMinutes : 0;
                dayValue = Math.Min(dayValue, 30);
                hourValue = Math.Min(hourValue, 23);
                minuteValue = Math.Min(minuteValue, 59);
                TimeSpan value = PowerGuardDurations.ClampCustom(
                    TimeSpan.FromMinutes((((dayValue * 24) + hourValue) * 60) + minuteValue));
                int normalizedMinutes = (int)value.TotalMinutes;
                days.Text = (normalizedMinutes / 1440).ToString();
                hours.Text = ((normalizedMinutes % 1440) / 60).ToString();
                minutes.Text = (normalizedMinutes % 60).ToString();
                controller.SelectPowerGuardDuration(PowerGuardDurationKind.Custom, value);
            }));
            return panel;
        }

        private static void ConfigurePowerGuardNumericTextBox(TextBox box)
        {
            box.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (!box.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    box.Focus();
                }
            };
            box.GotKeyboardFocus += delegate { box.SelectAll(); };
            box.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e)
            {
                for (int i = 0; i < e.Text.Length; i++)
                {
                    if (e.Text[i] < '0' || e.Text[i] > '9')
                    {
                        e.Handled = true;
                        return;
                    }
                }
            };
            DataObject.AddPastingHandler(box, delegate(object sender, DataObjectPastingEventArgs e)
            {
                if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
                {
                    e.CancelCommand();
                    return;
                }

                string pastedText = e.SourceDataObject.GetData(DataFormats.UnicodeText, true) as string ?? string.Empty;
                for (int i = 0; i < pastedText.Length; i++)
                {
                    if (pastedText[i] < '0' || pastedText[i] > '9')
                    {
                        e.CancelCommand();
                        return;
                    }
                }

                int resultingLength = box.Text.Length - box.SelectionLength + pastedText.Length;
                if (resultingLength > box.MaxLength)
                {
                    e.CancelCommand();
                }
            });
        }

        private UIElement CreatePowerGuardManualTimeStatus()
        {
            powerGuardRemainingText = new TextBlock
            {
                Text = "未設定結束時間",
                FontSize = Z(13),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            return powerGuardRemainingText;
        }

        private UIElement CreatePowerGuardTimeline(PowerGuardState state)
        {
            var stack = new StackPanel
            {
                Width = Z(340),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var range = new Grid();
            range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            range.Children.Add(new TextBlock
            {
                Text = "0",
                FontSize = Z(12),
                Foreground = SubtleBrush
            });
            powerGuardTimelineMaximumText = new TextBlock
            {
                Text = GuardCenterController.FormatPowerGuardDuration(state.SelectedDuration),
                FontSize = Z(12),
                Foreground = SubtleBrush,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(powerGuardTimelineMaximumText, 1);
            range.Children.Add(powerGuardTimelineMaximumText);
            stack.Children.Add(range);

            double maximumMinutes = Math.Max(1, state.SelectedDuration.TotalMinutes);
            double startingValue = state.IsEnabled && state.Remaining.HasValue
                ? state.Remaining.Value.TotalMinutes
                : maximumMinutes;
            powerGuardTimelineSlider = new Slider
            {
                Minimum = 0,
                Maximum = maximumMinutes,
                Value = Math.Max(0, Math.Min(maximumMinutes, startingValue)),
                TickFrequency = GetPowerGuardSliderStep(maximumMinutes),
                SmallChange = GetPowerGuardSliderStep(maximumMinutes),
                LargeChange = GetPowerGuardSliderStep(maximumMinutes) * 3,
                IsSnapToTickEnabled = true,
                IsEnabled = state.IsEnabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = ZThickness(0, 4, 0, 8)
            };
            powerGuardTimelineSlider.AddHandler(Thumb.DragStartedEvent,
                new DragStartedEventHandler(delegate
                {
                    if (powerGuardTimelineSlider.IsEnabled)
                    {
                        powerGuardDragging = true;
                    }
                }));
            powerGuardTimelineSlider.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler(delegate
                {
                    if (!powerGuardDragging)
                    {
                        return;
                    }
                    powerGuardDragging = false;
                    controller.AdjustPowerGuardRemaining(TimeSpan.FromMinutes(powerGuardTimelineSlider.Value));
                }));
            powerGuardTimelineSlider.ValueChanged += delegate
            {
                if (powerGuardUiSyncing)
                {
                    return;
                }

                TimeSpan preview = TimeSpan.FromMinutes(powerGuardTimelineSlider.Value);
                UpdatePowerGuardTimelinePreview(preview);
                if (!powerGuardDragging && powerGuardTimelineSlider.IsEnabled)
                {
                    controller.AdjustPowerGuardRemaining(preview);
                }
            };
            stack.Children.Add(powerGuardTimelineSlider);

            powerGuardRemainingText = new TextBlock
            {
                FontSize = Z(13),
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            powerGuardEndText = new TextBlock
            {
                FontSize = Z(12),
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, 3, 0, 0)
            };
            stack.Children.Add(powerGuardRemainingText);
            stack.Children.Add(powerGuardEndText);
            return stack;
        }

        public void RefreshPowerGuardState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshPowerGuardState));
                return;
            }

            PowerGuardState state = powerGuardModule.GetState();
            if (selectedModuleIndex != 6)
            {
                return;
            }
            if (powerGuardDurationComboBox == null || state.DurationKind != powerGuardRenderedDurationKind)
            {
                RenderPowerGuardPage();
                return;
            }

            powerGuardUiSyncing = true;
            try
            {
                if (powerGuardEnabledToggle != null)
                {
                    powerGuardEnabledToggle.IsChecked = state.IsEnabled;
                }
                if (powerGuardDisplayToggle != null)
                {
                    powerGuardDisplayToggle.IsChecked = state.KeepDisplayOn;
                    powerGuardDisplayToggle.IsEnabled = state.IsEnabled;
                }
                if (powerGuardTimelineSlider != null)
                {
                    double maximum = Math.Max(1, state.SelectedDuration.TotalMinutes);
                    if (powerGuardTimelineMaximumText != null)
                    {
                        powerGuardTimelineMaximumText.Text =
                            GuardCenterController.FormatPowerGuardDuration(state.SelectedDuration);
                    }
                    powerGuardTimelineSlider.Maximum = maximum;
                    powerGuardTimelineSlider.TickFrequency = GetPowerGuardSliderStep(maximum);
                    powerGuardTimelineSlider.SmallChange = GetPowerGuardSliderStep(maximum);
                    powerGuardTimelineSlider.LargeChange = GetPowerGuardSliderStep(maximum) * 3;
                    powerGuardTimelineSlider.IsEnabled = state.IsEnabled;

                    if (!powerGuardDragging || !state.IsEnabled)
                    {
                        if (!state.IsEnabled)
                        {
                            powerGuardDragging = false;
                        }
                        double value = state.IsEnabled && state.Remaining.HasValue
                            ? state.Remaining.Value.TotalMinutes
                            : maximum;
                        powerGuardTimelineSlider.Value = Math.Max(0, Math.Min(maximum, value));
                        UpdatePowerGuardTimelineText(state);
                    }
                }
                else if (powerGuardRemainingText != null)
                {
                    powerGuardRemainingText.Text = state.IsEnabled
                        ? "未設定結束時間"
                        : "開啟保持清醒後將持續到手動關閉";
                }

                if (powerGuardErrorText != null)
                {
                    powerGuardErrorText.Text = state.LastError;
                    powerGuardErrorText.Visibility = string.IsNullOrWhiteSpace(state.LastError)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            }
            finally
            {
                powerGuardUiSyncing = false;
            }

            StatusTextBlock.Text = state.StatusText;
        }

        private void UpdatePowerGuardTimelineText(PowerGuardState state)
        {
            if (powerGuardRemainingText == null || powerGuardEndText == null)
            {
                return;
            }

            if (!state.IsEnabled || !state.Remaining.HasValue || !state.EndAtUtc.HasValue)
            {
                powerGuardRemainingText.Text = "預設時間："
                    + GuardCenterController.FormatPowerGuardDuration(state.SelectedDuration);
                powerGuardEndText.Text = "開啟保持清醒後開始倒數";
                return;
            }

            powerGuardRemainingText.Text = "剩餘時間："
                + GuardCenterController.FormatPowerGuardRemaining(state.Remaining.Value);
            powerGuardEndText.Text = "預計結束：" + FormatPowerGuardEndTime(state.EndAtUtc.Value);
        }

        private void UpdatePowerGuardTimelinePreview(TimeSpan remaining)
        {
            if (powerGuardRemainingText == null || powerGuardEndText == null)
            {
                return;
            }

            if (remaining <= TimeSpan.Zero)
            {
                powerGuardRemainingText.Text = "剩餘時間：0 秒";
                powerGuardEndText.Text = "放開後解除保持清醒";
                return;
            }

            powerGuardRemainingText.Text = "剩餘時間："
                + GuardCenterController.FormatPowerGuardRemaining(remaining);
            powerGuardEndText.Text = "預計結束：" + FormatPowerGuardEndTime(DateTimeOffset.UtcNow + remaining);
        }

        private static double GetPowerGuardSliderStep(double maximumMinutes)
        {
            if (maximumMinutes <= 30)
            {
                return 1;
            }
            if (maximumMinutes <= 240)
            {
                return 5;
            }
            return 15;
        }

        private static string FormatPowerGuardEndTime(DateTimeOffset endAtUtc)
        {
            DateTimeOffset local = endAtUtc.ToLocalTime();
            DateTimeOffset now = DateTimeOffset.Now;
            return local.Date == now.Date
                ? local.ToString("HH:mm")
                : local.ToString("yyyy/MM/dd HH:mm");
        }

        private UIElement CreateDisplayModeActions()
        {
            var panel = CreateButtonRow();
            var combo = new ComboBox
            {
                Width = Z(150),
                FontSize = Z(13),
                Margin = ZThickness(0, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            displayModeComboBox = combo;

            DisplayGuardModeDefinition[] modes = controller.GetDisplayGuardModes();
            string selected = controller.GetSelectedDisplayGuardMode();
            for (int i = 0; i < modes.Length; i++)
            {
                var item = new ComboBoxItem
                {
                    Content = modes[i].Name,
                    Tag = modes[i].Key
                };
                combo.Items.Add(item);
                if (string.Equals(modes[i].Key, selected, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                }
            }

            combo.SelectionChanged += delegate
            {
                if (suppressDisplayModeSelectionChanged)
                {
                    return;
                }

                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                controller.SetSelectedDisplayGuardMode(Convert.ToString(item.Tag));
                controller.ApplyDisplayGuardMode();
                RenderDisplayGuardPage();
                RefreshStatus();
            };

            panel.Children.Add(combo);
            panel.Children.Add(CreateActionButton("Save custom", true, delegate
            {
                controller.SaveDisplayGuardMode();
                RenderDisplayGuardPage();
                RefreshStatus();
            }));
            panel.Children.Add(CreateActionButton("Refresh", false, delegate
            {
                controller.RefreshDisplayGuard();
                RefreshStatus();
            }));
            return panel;
        }

        private Button CreateDisplayRefreshButton()
        {
            return CreateActionButton("Refresh", false, delegate
            {
                controller.RefreshDisplayGuard();
                RefreshStatus();
            });
        }

        private UIElement CreateDisplayMonitorCard(DisplayGuardMonitorInfo monitor)
        {
            var target = new StackPanel();
            AddCardToPanel(target, monitor.DisplayName, BuildDisplayDescription(monitor),
                CreateDisplayMonitorEditor(monitor), false);
            UIElement card = target.Children[0];
            target.Children.Remove(card);
            return card;
        }

        private string BuildDisplayDescription(DisplayGuardMonitorInfo monitor)
        {
            var parts = new List<string>();
            if (monitor.IsPrimary)
            {
                parts.Add("Primary");
            }
            if (!string.IsNullOrWhiteSpace(monitor.SourceName))
            {
                parts.Add(monitor.SourceName);
            }
            if (!string.IsNullOrWhiteSpace(monitor.ControlKind))
            {
                parts.Add(monitor.ControlKind);
            }
            if (!monitor.StableIdReliable && monitor.IsControllable)
            {
                parts.Add("Unmatched");
            }
            else if (monitor.StableIdReliable)
            {
                parts.Add("Matched");
            }
            if (monitor.IsBusy)
            {
                parts.Add("Writing and verifying");
            }

            string description = parts.Count == 0 ? "Connected display." : string.Join(" | ", parts.ToArray()) + ".";
            if (!string.IsNullOrWhiteSpace(monitor.LastError))
            {
                description += Environment.NewLine + monitor.LastError;
            }

            return description;
        }

        private UIElement CreateDisplayMonitorEditor(DisplayGuardMonitorInfo monitor)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = Z(250),
                IsEnabled = !monitor.IsBusy
            };

            if (monitor.SupportsBrightness)
            {
                stack.Children.Add(CreateDisplayFeatureEditor("Brightness", monitor.BrightnessPercent,
                    delegate(int value) { controller.SetDisplayGuardBrightness(monitor.RuntimeId, value); }));
            }

            if (monitor.SupportsContrast)
            {
                UIElement contrast = CreateDisplayFeatureEditor("Contrast", monitor.ContrastPercent,
                    delegate(int value) { controller.SetDisplayGuardContrast(monitor.RuntimeId, value); });
                contrast.SetValue(MarginProperty, ZThickness(0, monitor.SupportsBrightness ? 8 : 0, 0, 0));
                stack.Children.Add(contrast);
            }

            if (!monitor.SupportsBrightness && !monitor.SupportsContrast)
            {
                stack.Children.Add(CreateStaticValue("Not controllable"));
            }

            return stack;
        }

        private UIElement CreateDisplaySyncSlider(List<DisplayGuardMonitorInfo> monitors, DisplayGuardFeature feature)
        {
            int average = GetAverageDisplayValue(monitors, feature);
            return CreateDisplayFeatureEditor("All", average, delegate(int value)
            {
                if (feature == DisplayGuardFeature.Brightness)
                {
                    controller.SetAllDisplayGuardBrightness(value);
                }
                else
                {
                    controller.SetAllDisplayGuardContrast(value);
                }
            });
        }

        private UIElement CreateDisplayFeatureEditor(string label, int percent, Action<int> onChanged)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var labelText = new TextBlock
            {
                Text = label,
                Width = Z(76),
                FontSize = Z(13),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            var percentText = new TextBlock
            {
                Text = Clamp(percent, 0, 100) + "%",
                Width = Z(42),
                FontSize = Z(13),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = Clamp(percent, 0, 100),
                Width = Z(130),
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(6, 0, 0, 0)
            };

            var decreaseButton = CreateDisplayStepButton("−", "Decrease " + label + " by 1%");
            decreaseButton.Margin = ZThickness(8, 0, 0, 0);
            var increaseButton = CreateDisplayStepButton("+", "Increase " + label + " by 1%");
            increaseButton.Margin = ZThickness(6, 0, 0, 0);

            bool ready = false;
            bool thumbDragging = false;
            bool mouseAdjusting = false;
            int lastCommitted = Clamp(percent, 0, 100);
            Action commit = delegate
            {
                int next = Clamp((int)Math.Round(slider.Value), 0, 100);
                percentText.Text = next + "%";
                if (next == lastCommitted)
                {
                    return;
                }

                lastCommitted = next;
                SwitchDisplayGuardToLiveMode();
                onChanged(next);
            };

            slider.PreviewMouseLeftButtonDown += delegate
            {
                if (slider.IsEnabled)
                {
                    mouseAdjusting = true;
                }
            };
            slider.PreviewMouseLeftButtonUp += delegate
            {
                if (!mouseAdjusting)
                {
                    return;
                }

                mouseAdjusting = false;
                if (!thumbDragging)
                {
                    commit();
                }
            };
            slider.AddHandler(Thumb.DragStartedEvent,
                new DragStartedEventHandler(delegate
                {
                    if (slider.IsEnabled)
                    {
                        thumbDragging = true;
                        mouseAdjusting = true;
                    }
                }));
            slider.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler(delegate
                {
                    if (!thumbDragging)
                    {
                        return;
                    }

                    thumbDragging = false;
                    mouseAdjusting = false;
                    commit();
                }));
            slider.ValueChanged += delegate
            {
                if (!ready)
                {
                    return;
                }

                int next = Clamp((int)Math.Round(slider.Value), 0, 100);
                percentText.Text = next + "%";
                decreaseButton.IsEnabled = next > 0;
                increaseButton.IsEnabled = next < 100;
                if (!thumbDragging && !mouseAdjusting)
                {
                    commit();
                }
            };
            decreaseButton.Click += delegate
            {
                slider.Value = Clamp((int)Math.Round(slider.Value) - 1, 0, 100);
            };
            increaseButton.Click += delegate
            {
                slider.Value = Clamp((int)Math.Round(slider.Value) + 1, 0, 100);
            };
            ready = true;
            decreaseButton.IsEnabled = slider.Value > slider.Minimum;
            increaseButton.IsEnabled = slider.Value < slider.Maximum;

            panel.Children.Add(labelText);
            panel.Children.Add(percentText);
            panel.Children.Add(decreaseButton);
            panel.Children.Add(slider);
            panel.Children.Add(increaseButton);
            return panel;
        }

        private Button CreateDisplayStepButton(string text, string toolTip)
        {
            return new Button
            {
                Content = text,
                Style = (Style)FindResource("ActionButtonStyle"),
                Width = Z(32),
                MinWidth = Z(32),
                Height = Z(32),
                Padding = ZThickness(0, 0, 0, 0),
                FontSize = Z(16),
                ToolTip = toolTip,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private void SwitchDisplayGuardToLiveMode()
        {
            controller.SetSelectedDisplayGuardMode(DisplayGuardModes.Live);
            if (displayModeComboBox == null)
            {
                return;
            }

            suppressDisplayModeSelectionChanged = true;
            try
            {
                for (int i = 0; i < displayModeComboBox.Items.Count; i++)
                {
                    ComboBoxItem item = displayModeComboBox.Items[i] as ComboBoxItem;
                    if (item != null
                        && string.Equals(Convert.ToString(item.Tag), DisplayGuardModes.Live,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        displayModeComboBox.SelectedItem = item;
                        return;
                    }
                }
            }
            finally
            {
                suppressDisplayModeSelectionChanged = false;
            }
        }

        private static int CountDisplayFeature(List<DisplayGuardMonitorInfo> monitors, DisplayGuardFeature feature)
        {
            int count = 0;
            for (int i = 0; i < monitors.Count; i++)
            {
                if ((feature == DisplayGuardFeature.Brightness && monitors[i].SupportsBrightness)
                    || (feature == DisplayGuardFeature.Contrast && monitors[i].SupportsContrast))
                {
                    count++;
                }
            }

            return count;
        }

        private static int GetAverageDisplayValue(List<DisplayGuardMonitorInfo> monitors, DisplayGuardFeature feature)
        {
            int total = 0;
            int count = 0;
            for (int i = 0; i < monitors.Count; i++)
            {
                if (feature == DisplayGuardFeature.Brightness && monitors[i].SupportsBrightness)
                {
                    total += monitors[i].BrightnessPercent;
                    count++;
                }
                else if (feature == DisplayGuardFeature.Contrast && monitors[i].SupportsContrast)
                {
                    total += monitors[i].ContrastPercent;
                    count++;
                }
            }

            return count == 0 ? 0 : Clamp((int)Math.Round((double)total / count), 0, 100);
        }

        private static string Plural(int count)
        {
            return count == 1 ? string.Empty : "s";
        }

        private void ConfigureHeader(string title, string subtitle, bool showSwitch)
        {
            PageTitleText.Text = title;
            PageSubtitleText.Text = subtitle;

            HeaderHelpButton.Visibility = Visibility.Collapsed;
            HeaderEnabledSwitch.Visibility = showSwitch ? Visibility.Visible : Visibility.Collapsed;
            syncing = true;
            HeaderEnabledSwitch.IsChecked = settings.Audio.Enabled;
            syncing = false;
        }

        private void AddSection(string title)
        {
            var heading = new TextBlock
            {
                Text = title,
                FontSize = Z(18),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(2, SettingsStack.Children.Count == 0 ? 0 : 22, 0, 8)
            };
            SettingsStack.Children.Add(heading);
        }

        private void AddSection(string title, UIElement rightContent)
        {
            var grid = new Grid
            {
                Margin = ZThickness(2, SettingsStack.Children.Count == 0 ? 0 : 22, 0, 8)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = title,
                FontSize = Z(18),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(heading, 0);
            grid.Children.Add(heading);

            if (rightContent != null)
            {
                rightContent.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
                rightContent.SetValue(MarginProperty, ZThickness(18, 0, 0, 0));
                Grid.SetColumn(rightContent, 1);
                grid.Children.Add(rightContent);
                AttachAdaptiveSectionLayout(grid, heading, rightContent);
            }

            SettingsStack.Children.Add(grid);
        }

        private void AttachAdaptiveSectionLayout(Grid grid, TextBlock heading, UIElement rightContent)
        {
            RoutedEventHandler loaded = null;
            loaded = delegate
            {
                grid.Loaded -= loaded;
                ApplyAdaptiveSectionLayout(grid, heading, rightContent);
            };

            grid.Loaded += loaded;
            grid.SizeChanged += delegate { ApplyAdaptiveSectionLayout(grid, heading, rightContent); };
        }

        private void ApplyAdaptiveSectionLayout(Grid grid, TextBlock heading, UIElement rightContent)
        {
            if (grid == null || heading == null || rightContent == null)
            {
                return;
            }

            double width = grid.ActualWidth;
            if (!IsFinite(width) || width <= 0)
            {
                return;
            }

            heading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            rightContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double headingWidth = heading.DesiredSize.Width;
            double rightWidth = rightContent.DesiredSize.Width;
            bool stacked = headingWidth + rightWidth + Z(32) > width;

            Grid.SetRow(heading, 0);
            Grid.SetColumn(heading, 0);
            Grid.SetColumnSpan(heading, stacked ? 2 : 1);

            Grid.SetRow(rightContent, stacked ? 1 : 0);
            Grid.SetColumn(rightContent, stacked ? 0 : 1);
            Grid.SetColumnSpan(rightContent, stacked ? 2 : 1);
            rightContent.SetValue(MarginProperty, stacked ? ZThickness(0, 10, 0, 0) : ZThickness(18, 0, 0, 0));
            rightContent.SetValue(HorizontalAlignmentProperty,
                stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right);
        }

        private StackPanel AddIndentedGroup()
        {
            var stack = new StackPanel
            {
                Margin = ZThickness(18, 0, 0, 0)
            };
            SettingsStack.Children.Add(stack);
            return stack;
        }

        private void AddSubSection(Panel target, string title)
        {
            var heading = new TextBlock
            {
                Text = title,
                FontSize = Z(16),
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedBrush,
                Margin = ZThickness(2, target.Children.Count == 0 ? 4 : 18, 0, 8)
            };
            target.Children.Add(heading);
        }

        private void AddCard(string title, string description, UIElement rightContent,
            bool dependsOnAudioEnabled, UIElement titleSuffix = null)
        {
            AddCardToPanel(SettingsStack, title, description, rightContent,
                dependsOnAudioEnabled, titleSuffix);
        }

        private void AddCardToPanel(Panel target, string title, string description,
            UIElement rightContent, bool dependsOnAudioEnabled, UIElement titleSuffix = null)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                MinHeight = Z(76),
                Padding = ZThickness(18, 14, 18, 14),
                Margin = ZThickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = Z(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!string.IsNullOrWhiteSpace(title))
            {
                var titleText = new TextBlock
                {
                    Text = title,
                    FontSize = Z(14.5),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TextBrush,
                    TextWrapping = TextWrapping.Wrap
                };
                if (titleSuffix == null)
                {
                    textStack.Children.Add(titleText);
                }
                else
                {
                    var titleRow = new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    titleRow.Children.Add(titleText);
                    titleSuffix.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
                    titleSuffix.SetValue(MarginProperty, ZThickness(10, 0, 0, 0));
                    titleRow.Children.Add(titleSuffix);
                    textStack.Children.Add(titleRow);
                }
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = description,
                    FontSize = string.IsNullOrWhiteSpace(title) ? Z(13.5) : Z(13),
                    Foreground = MutedBrush,
                    Margin = string.IsNullOrWhiteSpace(title) ? new Thickness(0) : ZThickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            Grid.SetColumn(textStack, 0);
            grid.Children.Add(textStack);

            if (rightContent != null)
            {
                rightContent.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
                rightContent.SetValue(MarginProperty, ZThickness(24, 0, 0, 0));
                Grid.SetColumn(rightContent, 1);
                grid.Children.Add(rightContent);
                AttachAdaptiveTwoColumnLayout(card, grid, textStack, rightContent, Z(560));
            }

            card.Child = grid;
            target.Children.Add(card);

            if (dependsOnAudioEnabled)
            {
                var dependent = new DependentCard { Card = card };
                if (rightContent != null)
                {
                    CollectInteractiveControls(rightContent, dependent.Controls);
                }
                audioDependentCards.Add(dependent);
            }
        }

        private UIElement CreateAudioAppCard(AudioAppVolume app)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                MinHeight = Z(86),
                Padding = ZThickness(18, 14, 18, 14),
                Margin = ZThickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = Z(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement icon = CreateAppIcon(app);
            icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetValue(MarginProperty, ZThickness(0, 0, 16, 0));
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            textStack.Children.Add(new TextBlock
            {
                Text = app.Name,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap
            });
            textStack.Children.Add(new TextBlock
            {
                Text = app.Description,
                FontSize = Z(13),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            UIElement editor = CreateAppVolumeEditor(app);
            editor.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            editor.SetValue(MarginProperty, ZThickness(24, 0, 0, 0));
            Grid.SetColumn(editor, 2);
            grid.Children.Add(editor);
            AttachAdaptiveTwoColumnLayout(card, grid, textStack, editor, Z(680));

            card.Child = grid;
            return card;
        }

        private void AddGameHelperAppCard(string title, string description, string iconPath, UIElement rightContent)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                MinHeight = Z(84),
                Padding = ZThickness(18, 14, 18, 14),
                Margin = ZThickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = Z(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement icon = CreateFileIcon(iconPath);
            icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetValue(MarginProperty, ZThickness(0, 0, 16, 0));
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap
            });
            textStack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Z(13),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            if (rightContent != null)
            {
                rightContent.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
                rightContent.SetValue(MarginProperty, ZThickness(24, 0, 0, 0));
                Grid.SetColumn(rightContent, 2);
                grid.Children.Add(rightContent);
                AttachAdaptiveTwoColumnLayout(card, grid, textStack, rightContent, Z(620));
            }

            card.Child = grid;
            SettingsStack.Children.Add(card);
        }

        private void AttachAdaptiveTwoColumnLayout(Border card, Grid grid, UIElement primary, UIElement secondary, double stackBelowWidth)
        {
            if (secondary == null)
            {
                return;
            }

            RoutedEventHandler loaded = null;
            loaded = delegate
            {
                card.Loaded -= loaded;
                ApplyAdaptiveTwoColumnLayout(card, grid, primary, secondary, stackBelowWidth);
            };

            card.Loaded += loaded;
            card.SizeChanged += delegate
            {
                ApplyAdaptiveTwoColumnLayout(card, grid, primary, secondary, stackBelowWidth);
            };
        }

        private void ApplyAdaptiveTwoColumnLayout(Border card, Grid grid, UIElement primary, UIElement secondary, double stackBelowWidth)
        {
            if (grid.ColumnDefinitions.Count == 0)
            {
                return;
            }

            bool hasLeadColumn = grid.ColumnDefinitions.Count > 2;
            int textColumn = hasLeadColumn ? 1 : 0;
            int lastColumn = grid.ColumnDefinitions.Count - 1;
            bool stacked = IsFinite(card.ActualWidth) && card.ActualWidth > 0 && card.ActualWidth < stackBelowWidth;

            Grid.SetRow(primary, 0);
            Grid.SetColumn(primary, textColumn);
            Grid.SetColumnSpan(primary, stacked ? Math.Max(1, grid.ColumnDefinitions.Count - textColumn) : 1);

            if (stacked)
            {
                Grid.SetRow(secondary, 1);
                Grid.SetColumn(secondary, textColumn);
                Grid.SetColumnSpan(secondary, Math.Max(1, grid.ColumnDefinitions.Count - textColumn));
                secondary.SetValue(MarginProperty, ZThickness(0, 12, 0, 0));
                secondary.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
            }
            else
            {
                Grid.SetRow(secondary, 0);
                Grid.SetColumn(secondary, lastColumn);
                Grid.SetColumnSpan(secondary, 1);
                secondary.SetValue(MarginProperty, ZThickness(24, 0, 0, 0));
                secondary.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            }
        }

        private UIElement CreateToggle(bool value, Action<bool> onChanged)
        {
            var toggle = new UiControls.ToggleSwitch
            {
                IsChecked = value,
                Width = Z(58),
                Height = Z(30),
                OnContent = string.Empty,
                OffContent = string.Empty
            };
            toggle.Checked += delegate { RunToggleChange(toggle, onChanged); };
            toggle.Unchecked += delegate { RunToggleChange(toggle, onChanged); };
            return toggle;
        }

        private UIElement CreateStickyKeysToggle()
        {
            var toggle = new UiControls.ToggleSwitch
            {
                IsChecked = keyboardModule.IsStickyKeysHotkeyDisabled(),
                Width = Z(58),
                Height = Z(30),
                OnContent = string.Empty,
                OffContent = string.Empty
            };
            bool applying = false;
            RoutedEventHandler apply = null;
            apply = async delegate
            {
                if (syncing || applying)
                {
                    return;
                }

                applying = true;
                toggle.IsEnabled = false;
                bool desired = toggle.IsChecked == true;
                StatusTextBlock.Text = "正在套用相黏鍵快捷鍵設定…";
                bool succeeded = await controller.SetStickyKeysHotkeyDisabledAsync(desired);
                applying = false;

                if (!succeeded)
                {
                    syncing = true;
                    toggle.IsChecked = keyboardModule.IsStickyKeysHotkeyDisabled();
                    syncing = false;
                }
                toggle.IsEnabled = true;
                RefreshStatus();
            };
            toggle.Checked += apply;
            toggle.Unchecked += apply;
            return toggle;
        }

        private void RunToggleChange(UiControls.ToggleSwitch toggle, Action<bool> onChanged)
        {
            if (syncing)
            {
                return;
            }

            onChanged(toggle.IsChecked == true);
            RefreshStatus();
        }

        private UIElement CreatePollIntervalEditor()
        {
            var box = new UiControls.NumberBox
            {
                Width = Z(150),
                Value = settings.Audio.PollIntervalMs,
                Minimum = 50,
                Maximum = 5000,
                SmallChange = 50,
                LargeChange = 250,
                MaxDecimalPlaces = 0,
                SpinButtonPlacementMode = UiControls.NumberBoxSpinButtonPlacementMode.Inline,
                ValidationMode = UiControls.NumberBoxValidationMode.InvalidInputOverwritten
            };

            box.LostFocus += delegate { CommitPollInterval(box); };
            box.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    CommitPollInterval(box);
                    e.Handled = true;
                }
            };
            return box;
        }

        private void CommitPollInterval(UiControls.NumberBox box)
        {
            double value = box.Value.HasValue ? box.Value.Value : settings.Audio.PollIntervalMs;
            int next = Clamp((int)Math.Round(value), 50, 5000);
            box.Value = next;

            if (next == settings.Audio.PollIntervalMs)
            {
                return;
            }

            settings.Audio.PollIntervalMs = next;
            controller.ApplyAudioSettings();
            RefreshStatus();
        }

        private UIElement CreateRetryDelayEditor()
        {
            var box = new TextBox
            {
                Width = Z(190),
                Text = settings.Audio.RetryDelaysCsv,
                FontSize = Z(13),
                Foreground = TextBrush,
                Background = BrushFromRgb(0x0E, 0x12, 0x1B),
                BorderBrush = CardBorderBrush,
                Padding = ZThickness(8, 5, 8, 5)
            };

            box.LostFocus += delegate { CommitRetryDelays(box); };
            box.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    CommitRetryDelays(box);
                    e.Handled = true;
                }
            };
            return box;
        }

        private void CommitRetryDelays(TextBox box)
        {
            string normalized = NormalizeRetryDelays(box.Text);
            box.Text = normalized;
            if (string.Equals(normalized, settings.Audio.RetryDelaysCsv, StringComparison.Ordinal))
            {
                return;
            }

            settings.Audio.RetryDelaysCsv = normalized;
            controller.ApplyAudioSettings();
            RefreshStatus();
        }

        private UIElement CreateAppVolumeEditor(AudioAppVolume app)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var percentText = new TextBlock
            {
                Text = app.VolumePercent + "%",
                Width = Z(40),
                FontSize = Z(13),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = app.VolumePercent,
                Width = Z(112),
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(8, 0, 8, 0)
            };

            bool suppressVolumeChange = false;
            bool ready = false;
            slider.ValueChanged += delegate
            {
                if (!ready || suppressVolumeChange)
                {
                    return;
                }

                int next = Clamp((int)Math.Round(slider.Value), 0, 100);
                percentText.Text = next + "%";
                controller.SetAudioAppVolume(app.Key, next);
            };
            ready = true;

            panel.Children.Add(percentText);
            panel.Children.Add(slider);
            panel.Children.Add(CreateAppVolumeResetButton(app, slider, percentText, delegate(bool value)
            {
                suppressVolumeChange = value;
            }));
            panel.Children.Add(CreateAppMuteButton(app));

            return panel;
        }

        private UIElement CreateAppIcon(AudioAppVolume app)
        {
            ImageSource source = null;
            if (!app.IsSystemSounds && !string.IsNullOrWhiteSpace(app.IconPath))
            {
                source = LoadIconSource(app.IconPath);
            }

            if (source == null)
            {
                source = CreateIconSource(app.IsSystemSounds
                    ? System.Drawing.SystemIcons.Information
                    : System.Drawing.SystemIcons.Application);
            }

            var image = new Image
            {
                Source = source,
                Width = Z(36),
                Height = Z(36),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        private UIElement CreateFileIcon(string iconPath)
        {
            ImageSource source = LoadIconSource(iconPath);
            if (source == null)
            {
                source = CreateIconSource(System.Drawing.SystemIcons.Application);
            }

            var image = new Image
            {
                Source = source,
                Width = Z(34),
                Height = Z(34),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        private UIElement CreateAppCatalogIcon(AppCatalogItem app)
        {
            string preferredIconPath = AppActionService.IsCurrentApplication(app)
                ? settings.Appearance.CustomIconPath
                : string.Empty;
            List<string> candidates = BuildAppCatalogIconCandidates(app, preferredIconPath);

            string cacheKey = candidates.Count == 0
                ? "app:" + (app == null ? string.Empty : app.Id)
                : string.Join("|", candidates.ToArray());
            ImageSource source;
            bool cached = appGuardIconCache.TryGetValue(cacheKey, out source);
            if (!cached || source == null)
            {
                source = GetDefaultAppCatalogIconSource();
            }

            var image = new Image
            {
                Source = source,
                Width = Z(34),
                Height = Z(34),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (!cached)
            {
                LoadAppCatalogIconAsync(image, cacheKey, candidates, source, appGuardRenderVersion);
            }
            return image;
        }

        internal static List<string> BuildAppCatalogIconCandidates(AppCatalogItem app,
            string preferredIconPath = "")
        {
            var candidates = new List<string>();
            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (app == null)
            {
                return candidates;
            }

            AddIconCandidate(candidates, uniquePaths, preferredIconPath);

            bool internetShortcut = string.Equals(Path.GetExtension(app.ShortcutPath ?? string.Empty),
                ".url", StringComparison.OrdinalIgnoreCase);
            if (internetShortcut)
            {
                // A .url file has a generic document shell icon; its explicit IconFile is authoritative.
                AddIconCandidate(candidates, uniquePaths, app.IconPath);
                AddIconCandidate(candidates, uniquePaths, app.TargetPath);
                AddIconCandidate(candidates, uniquePaths, app.ShortcutPath);
            }
            else
            {
                // For .lnk entries, Explorer's shortcut icon remains the closest representation.
                AddIconCandidate(candidates, uniquePaths, app.ShortcutPath);
                AddIconCandidate(candidates, uniquePaths, app.IconPath);
                AddIconCandidate(candidates, uniquePaths, app.TargetPath);
            }

            return candidates;
        }

        private async void LoadAppCatalogIconAsync(Image image, string cacheKey,
            List<string> candidates, ImageSource fallback, int renderVersion)
        {
            Task<ImageSource> task;
            if (!appGuardIconTasks.TryGetValue(cacheKey, out task))
            {
                task = Task.Run(async delegate
                {
                    await appGuardIconLoadGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        return LoadAppCatalogIconSource(candidates);
                    }
                    finally
                    {
                        appGuardIconLoadGate.Release();
                    }
                });
                appGuardIconTasks[cacheKey] = task;
            }

            ImageSource source = null;
            try
            {
                source = await task;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("App Guard icon load failed: " + ex.Message);
            }

            appGuardIconTasks.Remove(cacheKey);
            source = source ?? fallback;
            appGuardIconCache[cacheKey] = source;
            if (!audioRefreshClosed && selectedModuleIndex == 4
                && renderVersion == appGuardRenderVersion)
            {
                image.Source = source;
            }
        }

        private static ImageSource LoadAppCatalogIconSource(List<string> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                ImageSource source = LoadIconSource(candidates[i]);
                if (source != null)
                {
                    return source;
                }
            }
            return null;
        }

        private ImageSource GetDefaultAppCatalogIconSource()
        {
            const string key = "__default__";
            ImageSource source;
            if (!appGuardIconCache.TryGetValue(key, out source))
            {
                source = CreateIconSource(System.Drawing.SystemIcons.Application);
                appGuardIconCache[key] = source;
            }
            return source;
        }

        private static void AddIconCandidate(List<string> candidates, HashSet<string> uniquePaths,
            string path)
        {
            string normalized = NormalizeIconLoadPath(path);
            if (string.IsNullOrWhiteSpace(normalized) || !uniquePaths.Add(normalized))
            {
                return;
            }

            candidates.Add(normalized);
        }

        private static ImageSource LoadIconSource(string path)
        {
            try
            {
                path = NormalizeIconLoadPath(path);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
                {
                    ImageSource icoSource = LoadIcoIconSource(path);
                    if (icoSource != null)
                    {
                        return NormalizeIconSource(icoSource);
                    }
                }

                using (DrawingIcon icon = ExtractHighResolutionIcon(path))
                {
                    return NormalizeIconSource(CreateIconSource(icon));
                }
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeIconLoadPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            int comma = expanded.LastIndexOf(',');
            if (comma >= 0)
            {
                expanded = expanded.Substring(0, comma);
            }

            return expanded.Trim().Trim('"');
        }

        private static ImageSource LoadIcoIconSource(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var decoder = new IconBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                BitmapFrame bestFrame = null;
                int bestPixels = -1;
                for (int i = 0; i < decoder.Frames.Count; i++)
                {
                    BitmapFrame frame = decoder.Frames[i];
                    int pixels = frame.PixelWidth * frame.PixelHeight;
                    if (pixels > bestPixels)
                    {
                        bestFrame = frame;
                        bestPixels = pixels;
                    }
                }

                if (bestFrame != null && bestFrame.CanFreeze)
                {
                    bestFrame.Freeze();
                }

                return bestFrame;
            }
        }

        private static ImageSource NormalizeIconSource(ImageSource source)
        {
            BitmapSource bitmap = source as BitmapSource;
            if (bitmap == null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return source;
            }

            try
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                int minX = width;
                int minY = height;
                int maxX = -1;
                int maxY = -1;
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        byte alpha = pixels[row + x * 4 + 3];
                        if (alpha <= 12)
                        {
                            continue;
                        }

                        if (x < minX)
                        {
                            minX = x;
                        }
                        if (x > maxX)
                        {
                            maxX = x;
                        }
                        if (y < minY)
                        {
                            minY = y;
                        }
                        if (y > maxY)
                        {
                            maxY = y;
                        }
                    }
                }

                if (maxX < minX || maxY < minY)
                {
                    return bitmap;
                }

                int margin = 1;
                minX = Math.Max(0, minX - margin);
                minY = Math.Max(0, minY - margin);
                maxX = Math.Min(width - 1, maxX + margin);
                maxY = Math.Min(height - 1, maxY + margin);

                if (minX == 0 && minY == 0 && maxX == width - 1 && maxY == height - 1)
                {
                    return bitmap;
                }

                var cropped = new CroppedBitmap(converted,
                    new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
                if (cropped.CanFreeze)
                {
                    cropped.Freeze();
                }

                return cropped;
            }
            catch
            {
                return bitmap;
            }
        }

        private static DrawingIcon ExtractHighResolutionIcon(string path)
        {
            IntPtr imageList = SHGetFileInfo(path, 0, out ShFileInfo fileInfo,
                (uint)Marshal.SizeOf(typeof(ShFileInfo)),
                ShGetFileInfoFlags.SysIconIndex | ShGetFileInfoFlags.LargeIcon);

            if (imageList != IntPtr.Zero)
            {
                DrawingIcon icon = TryGetShellImageListIcon(fileInfo.iIcon, ShellImageListSize.Jumbo);
                if (icon != null)
                {
                    return icon;
                }

                icon = TryGetShellImageListIcon(fileInfo.iIcon, ShellImageListSize.ExtraLarge);
                if (icon != null)
                {
                    return icon;
                }
            }

            return System.Drawing.Icon.ExtractAssociatedIcon(path);
        }

        private static DrawingIcon TryGetShellImageListIcon(int iconIndex, ShellImageListSize size)
        {
            Guid iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            IntPtr imageList;
            int hr = SHGetImageList(size, ref iidImageList, out imageList);
            if (hr != HResult.SOk || imageList == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                IImageList list = (IImageList)Marshal.GetObjectForIUnknown(imageList);
                try
                {
                    IntPtr iconHandle;
                    hr = list.GetIcon(iconIndex, ImageListDrawItemFlags.Transparent, out iconHandle);
                    if (hr != HResult.SOk || iconHandle == IntPtr.Zero)
                    {
                        return null;
                    }

                    DrawingIcon clonedIcon = (DrawingIcon)DrawingIcon.FromHandle(iconHandle).Clone();
                    DestroyIcon(iconHandle);
                    return clonedIcon;
                }
                finally
                {
                    Marshal.ReleaseComObject(list);
                }
            }
            finally
            {
                Marshal.Release(imageList);
            }
        }

        private static ImageSource CreateIconSource(DrawingIcon icon)
        {
            if (icon == null)
            {
                return null;
            }

            ImageSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }

        private Button CreateAppVolumeResetButton(AudioAppVolume app, Slider slider, TextBlock percentText, Action<bool> setSuppress)
        {
            var glyph = new TextBlock
            {
                Text = "\uE72C",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = Z(14),
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var button = new Button
            {
                Content = glyph,
                Style = (Style)FindResource("ActionButtonStyle"),
                Width = Z(32),
                MinWidth = Z(32),
                Height = Z(32),
                Padding = ZThickness(0, 0, 0, 0),
                Margin = ZThickness(0, 0, 6, 0),
                ToolTip = "Reset to 50%"
            };

            button.Click += delegate
            {
                const int target = 50;
                setSuppress(true);
                try
                {
                    slider.Value = target;
                    percentText.Text = target + "%";
                }
                finally
                {
                    setSuppress(false);
                }

                controller.SetAudioAppVolume(app.Key, target);
                RefreshStatus();
            };

            return button;
        }

        private Button CreateAppMuteButton(AudioAppVolume app)
        {
            bool muted = app.Muted;
            var button = new Button
            {
                Style = (Style)FindResource("ActionButtonStyle"),
                FontSize = Z(13),
                MinWidth = Z(68),
                Padding = ZThickness(10, 7, 10, 7),
                Margin = ZThickness(2, 0, 0, 0)
            };

            ApplyAppMuteButtonState(button, muted);

            button.Click += delegate
            {
                muted = !muted;
                controller.SetAudioAppMute(app.Key, muted);
                ApplyAppMuteButtonState(button, muted);
                RefreshStatus();
            };

            return button;
        }

        private void ApplyAppMuteButtonState(Button button, bool muted)
        {
            button.Content = muted ? "Muted" : "Mute";
            button.Background = muted ? BrushFromRgb(0x7A, 0x2E, 0x3A) : BrushFromRgb(0x23, 0x2B, 0x3A);
            button.BorderBrush = muted ? BrushFromRgb(0xD8, 0x52, 0x65) : BrushFromRgb(0x2F, 0x3A, 0x4E);
            button.Foreground = TextBrush;
        }

        private UIElement CreateAudioMixerActions()
        {
            var panel = CreateButtonRow();
            panel.Children.Add(CreateBatchSizeCombo(audioMixerListState, RenderAudioPage));
            panel.Children.Add(CreateActionButton("Refresh", true, delegate
            {
                RenderAudioPage();
                PollAudioGuardSnapshot();
                RefreshStatus();
            }));
            return panel;
        }

        private UIElement CreateAudioActions()
        {
            var panel = CreateButtonRow();
            panel.Children.Add(CreateActionButton("Run now", true, delegate
            {
                controller.ZeroAudioNow();
                RefreshStatus();
            }));
            panel.Children.Add(CreateActionButton("Open log", false, delegate { controller.OpenLog(); }));
            panel.Children.Add(CreateActionButton("Clear log", false, delegate { controller.ClearLog(); }));
            return panel;
        }

        private UIElement CreateDeviceGuardActions()
        {
            var buttons = new List<Button>();
            Button repairAll = CreateActionButton("Repair all", true, delegate
            {
                controller.RepairAllDevices();
            });
            repairAll.IsEnabled = !controller.IsDeviceGuardRepairing()
                && controller.GetDeviceGuardDevices().Exists(delegate(DeviceGuardDevice device)
                {
                    return device.CanRepair && device.IncludeInRepairAll;
                });
            buttons.Add(repairAll);

            Button refresh = CreateActionButton("Refresh", false, delegate
            {
                controller.RefreshDeviceGuard();
            });
            refresh.IsEnabled = !controller.IsDeviceGuardScanning() && !controller.IsDeviceGuardRepairing();
            buttons.Add(refresh);
            buttons.Add(CreateActionButton("Open log", false, delegate { controller.OpenLog(); }));
            return CreateResponsiveButtonGrid(buttons);
        }

        private UIElement CreateKeyboardActions()
        {
            var panel = CreateButtonRow();
            panel.Children.Add(CreateActionButton("Typing", true, delegate
            {
                controller.OpenTypingSettings();
            }));
            panel.Children.Add(CreateActionButton("Language", false, delegate
            {
                controller.OpenLanguageSettings();
            }));
            return panel;
        }

        private UIElement CreateCrosshairPrimaryActions()
        {
            Button customize = CreateActionButton("Customize", false, delegate
            {
                ShowCrosshairSettings();
            });

            crosshairEnabledToggle = (UiControls.ToggleSwitch)CreateToggle(
                controller.IsGameHelperCrosshairEnabled(),
                delegate(bool value)
                {
                    if (crosshairUiSyncing)
                    {
                        return;
                    }

                    controller.SetGameHelperCrosshairEnabled(value);
                    RefreshCrosshairToggleState();
                });
            return CreateCrosshairCardActions(customize, crosshairEnabledToggle);
        }

        private UIElement CreateCrosshairRestrictionActions(bool restricted, int selectedAppCount)
        {
            Button selectApps = CreateActionButton("Select apps",
                selectedAppCount == 0, delegate
                {
                    ShowCrosshairSelectApps();
                    RenderGameHelperPage();
                    RefreshStatus();
                });

            bool crosshairEnabled = controller.IsGameHelperCrosshairEnabled();
            crosshairRestrictionToggle = (UiControls.ToggleSwitch)CreateToggle(
                crosshairEnabled && restricted, delegate(bool value)
                {
                    if (crosshairUiSyncing)
                    {
                        return;
                    }

                    controller.SetGameHelperCrosshairRestrictToSelectedApps(value);
                });
            crosshairRestrictionToggle.IsEnabled = crosshairEnabled;
            return CreateCrosshairCardActions(selectApps, crosshairRestrictionToggle);
        }

        private void RefreshCrosshairToggleState()
        {
            if (crosshairRestrictionToggle == null)
            {
                return;
            }

            bool enabled = controller.IsGameHelperCrosshairEnabled();
            bool restricted = controller.IsGameHelperCrosshairRestrictedToSelectedApps();
            crosshairUiSyncing = true;
            try
            {
                crosshairRestrictionToggle.IsChecked = enabled && restricted;
                crosshairRestrictionToggle.IsEnabled = enabled;
            }
            finally
            {
                crosshairUiSyncing = false;
            }
        }

        private UIElement CreateCrosshairCardActions(Button actionButton, UIElement toggle)
        {
            var actions = CreateButtonRow();
            actionButton.Width = Z(112);
            actionButton.Margin = new Thickness(0);
            actions.Children.Add(actionButton);
            toggle.SetValue(MarginProperty, ZThickness(16, 0, 0, 0));
            actions.Children.Add(toggle);
            return actions;
        }

        private UIElement CreateGameHelperActions()
        {
            var panel = CreateButtonRow();
            panel.Children.Add(CreateActionButton("Manage apps", true, delegate
            {
                ShowGameHelperManageApps();
            }));
            return panel;
        }

        private void ShowCrosshairSettings()
        {
            if (activeCrosshairSettingsDialog != null)
            {
                System.Media.SystemSounds.Exclamation.Play();
                activeCrosshairSettingsDialog.Activate();
                return;
            }

            CrosshairOptions options = controller.GetGameHelperCrosshairOptions();
            var dialog = CreateGameHelperDialog("Crosshair settings", Z(680), Z(640));
            dialog.MinWidth = Math.Min(Z(620), dialog.MaxWidth);
            dialog.MinHeight = Math.Min(Z(520), dialog.MaxHeight);
            dialog.Width = Clamp(dialog.Width, dialog.MinWidth, dialog.MaxWidth);
            dialog.Height = Clamp(dialog.Height, dialog.MinHeight, dialog.MaxHeight);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Loaded += delegate { EnsureDialogWithinWorkArea(dialog); };
            activeCrosshairSettingsDialog = dialog;
            IDisposable previewScope = controller.PreviewGameHelperCrosshairOverlay();

            try
            {
                double contentInset = Z(28);
                double scrollBarReserve = Math.Max(Z(20), SystemParameters.VerticalScrollBarWidth);
                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var title = new TextBlock
                {
                    Text = "Screen crosshair",
                    FontSize = Z(26),
                    FontWeight = FontWeights.Bold,
                    Foreground = TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, contentInset, 0, 0)
                };
                root.Children.Add(title);

                var subtitle = new TextBlock
                {
                    Text = "調整準心大小、透明度、顏色與樣式。",
                    FontSize = Z(13.5),
                    Foreground = MutedBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, Z(6), 0, Z(22))
                };
                Grid.SetRow(subtitle, 1);
                root.Children.Add(subtitle);

                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var scroll = new ScrollViewer
                {
                    Content = stack,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                Grid.SetRow(scroll, 2);
                root.Children.Add(scroll);

                string selectedStyle = options.Style;
                string[] styles = CrosshairStyleNames();
                string selectedColor = options.ColorHex;
                string customColor = string.IsNullOrWhiteSpace(options.CustomColorHex) ? "#FFFFFF" : options.CustomColorHex;
                string[] colors = new string[] { "#FF3B30", "#FF9500", "#FFD60A", "#34C759", "#00C7FF", "#5856D6", "#BF5AF2" };
                if (!ContainsColor(colors, selectedColor)
                    && !string.Equals(selectedColor, customColor, StringComparison.OrdinalIgnoreCase))
                {
                    customColor = selectedColor;
                }

                int startingSize = Math.Max(2, Math.Min(48, options.Size));
                int startingOpacity = Math.Max(10, Math.Min(100, options.OpacityPercent));
                var sizeSlider = CreateCrosshairSlider(2, 48, startingSize);
                var sizeValue = CreateCrosshairValueText(startingSize.ToString());
                stack.Children.Add(CreateCrosshairSettingRow("Size", "準心線條長度。", sizeSlider, sizeValue));

                var opacitySlider = CreateCrosshairSlider(10, 100, startingOpacity);
                var opacityValue = CreateCrosshairValueText(startingOpacity + "%");
                stack.Children.Add(CreateCrosshairSettingRow("Opacity", "準心透明度。", opacitySlider, opacityValue));

                var styleBox = CreateCrosshairStyleComboBox(styles, selectedStyle);
                selectedStyle = GetSelectedCrosshairStyle(styleBox);
                stack.Children.Add(CreateCrosshairSettingRow("Style", "準心樣式。", styleBox, null));

                var colorPanel = new WrapPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = ZThickness(-4, 0, 0, 0)
                };
                var colorButtons = new List<Button>();

                Action refreshColorButtons = delegate
                {
                    for (int i = 0; i < colorButtons.Count; i++)
                    {
                        string color = colorButtons[i].Tag as string;
                        bool selected = string.Equals(color, selectedColor, StringComparison.OrdinalIgnoreCase);
                        ApplyCrosshairColorButtonState(colorButtons[i], color, selected);
                    }
                };

                Action applyOptions = delegate
                {
                    int size = (int)Math.Round(sizeSlider.Value);
                    int opacity = (int)Math.Round(opacitySlider.Value);
                    sizeValue.Text = size.ToString();
                    opacityValue.Text = opacity + "%";

                    var updatedOptions = new CrosshairOptions
                    {
                        Size = size,
                        OpacityPercent = opacity,
                        ColorHex = selectedColor,
                        CustomColorHex = customColor,
                        Style = selectedStyle
                    };
                    controller.ApplyGameHelperCrosshairOptions(updatedOptions);
                    RefreshStatus();
                };

                styleBox.SelectionChanged += delegate
                {
                    string style = GetSelectedCrosshairStyle(styleBox);
                    if (string.Equals(selectedStyle, style, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    selectedStyle = style;
                    applyOptions();
                };

                for (int i = 0; i < colors.Length; i++)
                {
                    Button button = CreateCrosshairColorButton(colors[i]);
                    colorButtons.Add(button);
                    button.Click += delegate(object sender, RoutedEventArgs e)
                    {
                        var clicked = sender as Button;
                        if (clicked == null)
                        {
                            return;
                        }

                        selectedColor = clicked.Tag as string;
                        refreshColorButtons();
                        applyOptions();
                    };
                    colorPanel.Children.Add(button);
                }

                Button customButton = CreateCrosshairCustomColorButton(customColor);
                colorButtons.Add(customButton);
                customButton.Click += delegate
                {
                    selectedColor = customColor;
                    customButton.Tag = customColor;
                    refreshColorButtons();
                    applyOptions();
                };
                colorPanel.Children.Add(customButton);

                Button editCustomButton = CreateCrosshairEditColorButton();
                editCustomButton.Click += delegate
                {
                    using (var picker = new Forms.ColorDialog())
                    {
                        picker.AllowFullOpen = true;
                        picker.FullOpen = true;
                        picker.Color = DrawingColorFromHex(customColor);
                        if (picker.ShowDialog(new WindowHandleOwner(new WindowInteropHelper(dialog).Handle))
                            != Forms.DialogResult.OK)
                        {
                            return;
                        }

                        customColor = HexFromDrawingColor(picker.Color);
                        selectedColor = customColor;
                        customButton.Tag = customColor;
                        refreshColorButtons();
                        applyOptions();
                    }
                };
                colorPanel.Children.Add(editCustomButton);
                refreshColorButtons();
                stack.Children.Add(CreateCrosshairFullSettingRow("Color", "準心顏色。", colorPanel));

                Grid bottom = null;
                Action updateWrapWidth = delegate
                {
                    double width = root.ActualWidth;
                    if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                    {
                        width = scroll.ActualWidth;
                    }

                    if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                    {
                        width = scroll.ViewportWidth;
                    }

                    if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                    {
                        width = dialog.ActualWidth;
                    }

                    if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                    {
                        return;
                    }

                    double contentWidth = Math.Max(Z(240), width - contentInset - contentInset - scrollBarReserve);
                    title.Width = contentWidth;
                    subtitle.Width = contentWidth;
                    stack.Width = contentWidth;
                    if (bottom != null)
                    {
                        bottom.Width = contentWidth;
                    }
                    double preferredColorWidth = Math.Max(Z(120), contentWidth - Z(38));
                    colorPanel.Width = Math.Min(contentWidth, preferredColorWidth);
                };
                Action queueUpdateWrapWidth = delegate
                {
                    dialog.Dispatcher.BeginInvoke(new Action(updateWrapWidth), DispatcherPriority.Render);
                    dialog.Dispatcher.BeginInvoke(new Action(updateWrapWidth), DispatcherPriority.ApplicationIdle);
                };
                root.SizeChanged += delegate { queueUpdateWrapWidth(); };
                scroll.SizeChanged += delegate { queueUpdateWrapWidth(); };
                dialog.SizeChanged += delegate { queueUpdateWrapWidth(); };
                dialog.StateChanged += delegate { queueUpdateWrapWidth(); };
                dialog.Loaded += delegate { queueUpdateWrapWidth(); };

                sizeSlider.ValueChanged += delegate { applyOptions(); };
                opacitySlider.ValueChanged += delegate { applyOptions(); };

                bottom = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, Z(18), 0, contentInset)
                };
                Button closeButton = CreateActionButton("Close", false, delegate { dialog.Close(); });
                closeButton.HorizontalAlignment = HorizontalAlignment.Right;
                bottom.Children.Add(closeButton);
                Grid.SetRow(bottom, 3);
                root.Children.Add(bottom);

                dialog.Content = root;
                dialog.ShowDialog();
            }
            finally
            {
                if (ReferenceEquals(activeCrosshairSettingsDialog, dialog))
                {
                    activeCrosshairSettingsDialog = null;
                }

                previewScope.Dispose();
            }
        }

        private Slider CreateCrosshairSlider(double minimum, double maximum, double value)
        {
            return new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Width = Z(200),
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private ComboBox CreateCrosshairStyleComboBox(string[] styles, string selectedStyle)
        {
            var styleBox = new ComboBox
            {
                Width = Z(260),
                MinHeight = Z(48),
                MaxDropDownHeight = Z(360),
                Padding = ZThickness(0, 0, 0, 0),
                Background = BrushFromRgb(0x23, 0x2B, 0x3A),
                BorderBrush = CardBorderBrush,
                Foreground = TextBrush,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var choices = new List<CrosshairStyleChoice>();
            for (int i = 0; i < styles.Length; i++)
            {
                choices.Add(new CrosshairStyleChoice
                {
                    Name = styles[i],
                    Glyph = CrosshairStyleGlyph(styles[i])
                });
            }

            styleBox.ItemTemplate = CreateCrosshairStyleTemplate();
            styleBox.ItemsSource = choices;
            SelectCrosshairStyle(styleBox, selectedStyle);
            return styleBox;
        }

        private DataTemplate CreateCrosshairStyleTemplate()
        {
            var template = new DataTemplate(typeof(CrosshairStyleChoice));
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.SetValue(FrameworkElement.HeightProperty, Z(46));
            row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetBinding(TextBlock.TextProperty, new Binding("Glyph"));
            glyph.SetValue(FrameworkElement.WidthProperty, Z(54));
            glyph.SetValue(TextBlock.FontSizeProperty, Z(21));
            glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Symbol"));
            glyph.SetValue(TextBlock.ForegroundProperty, AccentBrush);
            glyph.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            glyph.SetValue(TextBlock.LineHeightProperty, Z(28));
            glyph.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(glyph);

            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            label.SetValue(TextBlock.FontSizeProperty, Z(15));
            label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            label.SetValue(TextBlock.ForegroundProperty, TextBrush);
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(FrameworkElement.MarginProperty, ZThickness(10, 0, 0, 0));
            row.AppendChild(label);

            template.VisualTree = row;
            return template;
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

        private ComboBoxItem CreateCrosshairStyleItem(string style)
        {
            var row = new Grid
            {
                Height = Z(44),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Z(46)) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = CrosshairStyleGlyph(style),
                Width = Z(38),
                Height = Z(34),
                FontSize = Z(21),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = Z(28)
            });

            var label = new TextBlock
            {
                Text = style,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = ZThickness(8, 0, 0, 0)
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            return new ComboBoxItem
            {
                Tag = style,
                Content = row,
                MinHeight = Z(48),
                Padding = ZThickness(8, 2, 8, 2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private Button CreateCrosshairStyleButton(string style)
        {
            var button = new Button
            {
                Tag = style,
                Width = Z(152),
                Height = Z(58),
                Margin = ZThickness(0, 0, 10, 10),
                BorderThickness = new Thickness(1),
                FontSize = Z(13)
            };
            ApplyCrosshairStyleButtonState(button, false);
            return button;
        }

        private void ApplyCrosshairStyleButtonState(Button button, bool selected)
        {
            string style = button.Tag == null ? "Classic" : button.Tag.ToString();
            button.Background = selected ? BrushFromRgb(0x21, 0x35, 0x55) : BrushFromRgb(0x1E, 0x2A, 0x3E);
            button.BorderBrush = selected ? AccentBrush : CardBorderBrush;
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            button.Content = CreateCrosshairStyleButtonContent(style, selected);
        }

        private UIElement CreateCrosshairStyleButtonContent(string style, bool selected)
        {
            var grid = new Grid
            {
                Margin = ZThickness(10, 6, 10, 6)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Z(42)) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new TextBlock
            {
                Text = CrosshairStyleGlyph(style),
                FontSize = Z(24),
                FontFamily = new FontFamily("Consolas"),
                Foreground = selected ? AccentBrush : MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });

            var label = new TextBlock
            {
                Text = style,
                FontSize = Z(13),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = ZThickness(8, 0, 0, 0)
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            return grid;
        }

        private static string CrosshairStyleGlyph(string style)
        {
            switch (style)
            {
                case "Cross": return "+";
                case "Dot": return "•";
                case "Circle": return "○";
                case "Tiny Dot": return "·";
                case "Ring Dot": return "⊙";
                case "Plus Dot": return "⊕";
                case "Gap Dot": return "⌖";
                case "T-Shape": return "⊥";
                case "Inverted T": return "T";
                case "Chevron": return "⌃";
                case "Diamond": return "◇";
                case "Square": return "□";
                case "Box Dot": return "▣";
                case "X Cross": return "×";
                case "X Dot": return "⊗";
                case "Brackets": return "[ ]";
                case "Corners": return "⌜⌟";
                case "Vertical Post": return "│";
                case "Horizontal Bars": return "━";
                default: return "⌖";
            }
        }

        private static void SelectCrosshairStyle(ComboBox styleBox, string style)
        {
            for (int i = 0; i < styleBox.Items.Count; i++)
            {
                var choice = styleBox.Items[i] as CrosshairStyleChoice;
                if (choice != null && string.Equals(choice.Name, style, StringComparison.OrdinalIgnoreCase))
                {
                    styleBox.SelectedItem = choice;
                    return;
                }

                var item = styleBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, style, StringComparison.OrdinalIgnoreCase))
                {
                    styleBox.SelectedItem = item;
                    return;
                }
            }

            if (styleBox.Items.Count > 0)
            {
                styleBox.SelectedIndex = 0;
            }
        }

        private static string GetSelectedCrosshairStyle(ComboBox styleBox)
        {
            var choice = styleBox.SelectedItem as CrosshairStyleChoice;
            if (choice != null)
            {
                return string.IsNullOrWhiteSpace(choice.Name) ? "Classic" : choice.Name;
            }

            var item = styleBox.SelectedItem as ComboBoxItem;
            return item == null || item.Tag == null ? "Classic" : item.Tag.ToString();
        }

        private static bool ContainsColor(string[] colors, string color)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                if (string.Equals(colors[i], color, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private TextBlock CreateCrosshairValueText(string value)
        {
            return new TextBlock
            {
                Text = value,
                Width = Z(54),
                FontSize = Z(13),
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Margin = ZThickness(12, 0, 0, 0)
            };
        }

        private UIElement CreateCrosshairSettingRow(string title, string description, UIElement editor, UIElement valueContent)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = ZThickness(18, 14, 18, 14),
                Margin = ZThickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = Z(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = Z(14.5),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            });
            textStack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Z(13),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(textStack, 0);
            grid.Children.Add(textStack);

            editor.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            editor.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);

            var editorRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(24, 0, 0, 0)
            };
            editorRow.Children.Add(editor);

            if (valueContent != null)
            {
                editorRow.Children.Add(valueContent);
            }
            Grid.SetColumn(editorRow, 1);
            grid.Children.Add(editorRow);
            AttachAdaptiveTwoColumnLayout(card, grid, textStack, editorRow, Z(560));

            card.Child = grid;
            return card;
        }

        private UIElement CreateCrosshairFullSettingRow(string title, string description, UIElement editor)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = ZThickness(18, 14, 18, 14),
                Margin = ZThickness(0, 0, 0, 10)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = Z(14.5),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            });
            stack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Z(13),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 12)
            });
            editor.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            stack.Children.Add(editor);

            card.Child = stack;
            return card;
        }

        private Button CreateCrosshairColorButton(string colorHex)
        {
            return new Button
            {
                Tag = colorHex,
                Width = Z(48),
                Height = Z(42),
                Margin = ZThickness(8, 0, 0, 8),
                Background = BrushFromHex(colorHex),
                BorderBrush = TextBrush,
                BorderThickness = new Thickness(1),
                FontSize = Z(19),
                FontWeight = FontWeights.Bold,
                Foreground = GetReadableBrush(colorHex),
                Content = string.Empty
            };
        }

        private Button CreateCrosshairCustomColorButton(string colorHex)
        {
            var button = new Button
            {
                Tag = colorHex,
                Width = Z(124),
                Height = Z(42),
                Margin = ZThickness(8, 0, 0, 8),
                Background = BrushFromRgb(0x23, 0x2B, 0x3A),
                BorderBrush = TextBrush,
                BorderThickness = new Thickness(1)
            };
            UpdateCrosshairCustomColorButton(button, colorHex);
            return button;
        }

        private Button CreateCrosshairEditColorButton()
        {
            return new Button
            {
                Content = "Edit",
                Width = Z(74),
                Height = Z(42),
                Margin = ZThickness(8, 0, 0, 8),
                Background = BrushFromRgb(0x23, 0x2B, 0x3A),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = TextBrush,
                FontSize = Z(13),
                FontWeight = FontWeights.SemiBold
            };
        }

        private void ApplyCrosshairColorButtonState(Button button, string colorHex, bool selected)
        {
            bool isCustom = button.Content is StackPanel;
            button.BorderBrush = selected ? AccentBrush : TextBrush;
            button.BorderThickness = selected ? new Thickness(4) : new Thickness(1);
            button.Opacity = selected ? 1.0 : 0.82;

            if (isCustom)
            {
                UpdateCrosshairCustomColorButton(button, colorHex, selected);
                return;
            }

            button.Content = selected ? "✓" : string.Empty;
            button.Foreground = GetReadableBrush(colorHex);
            button.Background = BrushFromHex(colorHex);
        }

        private void UpdateCrosshairCustomColorButton(Button button, string colorHex)
        {
            UpdateCrosshairCustomColorButton(button, colorHex, false);
        }

        private void UpdateCrosshairCustomColorButton(Button button, string colorHex, bool selected)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new Border
            {
                Width = Z(16),
                Height = Z(16),
                CornerRadius = new CornerRadius(3),
                Background = BrushFromHex(colorHex),
                BorderBrush = TextBrush,
                BorderThickness = new Thickness(1),
                Margin = ZThickness(0, 0, 7, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = selected ? "✓ Custom" : "Custom",
                Foreground = TextBrush,
                FontSize = Z(13),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            button.Content = row;
        }

        private static Brush GetReadableBrush(string colorHex)
        {
            try
            {
                object value = ColorConverter.ConvertFromString(colorHex);
                if (value is Color)
                {
                    Color color = (Color)value;
                    double luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
                    return luminance > 160 ? Brushes.Black : Brushes.White;
                }
            }
            catch
            {
            }

            return Brushes.White;
        }

        private static System.Drawing.Color DrawingColorFromHex(string colorHex)
        {
            try
            {
                object value = ColorConverter.ConvertFromString(colorHex);
                if (value is Color)
                {
                    Color color = (Color)value;
                    return System.Drawing.Color.FromArgb(color.R, color.G, color.B);
                }
            }
            catch
            {
            }

            return System.Drawing.Color.White;
        }

        private static string HexFromDrawingColor(System.Drawing.Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private void ShowCrosshairSelectApps()
        {
            ResetCrosshairSearchState();
            var dialog = CreateGameHelperDialog("Select Crosshair apps", Z(820), Z(660));
            var root = new Grid
            {
                Margin = ZThickness(24, 22, 24, 20)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel
            {
                Margin = ZThickness(0, 0, 0, 14)
            };
            heading.Children.Add(new TextBlock
            {
                Text = "Select Crosshair apps",
                FontSize = Z(24),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Crosshair is shown only while one of these executable paths is in the foreground.",
                FontSize = Z(12.5),
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, 5, 0, 0)
            });
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            var tabsHost = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = ZThickness(0, 0, 0, 12)
            };
            Grid.SetRow(tabsHost, 1);
            root.Children.Add(tabsHost);

            var controlsHost = new Grid
            {
                Margin = ZThickness(0, 0, 0, 14)
            };
            Grid.SetRow(controlsHost, 2);
            root.Children.Add(controlsHost);

            var listHost = new Grid();
            Grid.SetRow(listHost, 3);
            root.Children.Add(listHost);

            var countText = new TextBlock
            {
                FontSize = Z(12),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(0, 0, 12, 0)
            };
            var bottom = new Grid
            {
                Margin = ZThickness(0, 16, 0, 0)
            };
            bottom.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.Children.Add(countText);
            Button close = CreateActionButton("Close", false, delegate { dialog.Close(); });
            Grid.SetColumn(close, 1);
            bottom.Children.Add(close);
            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            bool showingAllApps = true;
            bool loadingInstalledApps = false;
            bool dialogClosed = false;
            string installedAppsError = string.Empty;
            List<GameHelperAppCandidate> installedApps = null;
            List<CrosshairSelectedApp> selectedApps =
                controller.GetGameHelperCrosshairSelectedApps();
            CrosshairAppSelectionIndex selectedIndex =
                new CrosshairAppSelectionIndex(selectedApps);
            ProgressiveListPresenter<GameHelperAppCandidate> allAppsPresenter = null;
            ProgressiveListPresenter<CrosshairSelectedApp> selectedAppsPresenter = null;

            Action reloadSelectedApps = delegate
            {
                selectedApps = controller.GetGameHelperCrosshairSelectedApps();
                selectedIndex = new CrosshairAppSelectionIndex(selectedApps);
            };
            Action disposePresenters = delegate
            {
                if (allAppsPresenter != null)
                {
                    allAppsPresenter.Dispose();
                    allAppsPresenter = null;
                }
                if (selectedAppsPresenter != null)
                {
                    selectedAppsPresenter.Dispose();
                    selectedAppsPresenter = null;
                }
            };

            Action renderList = null;
            Action renderControls = null;
            Action renderTabs = null;
            Action<bool> selectTab = null;
            Func<bool, Task> loadInstalledAppsAsync = null;

            renderList = delegate
            {
                disposePresenters();
                listHost.Children.Clear();

                if (!showingAllApps)
                {
                    selectedAppsPresenter = new ProgressiveListPresenter<CrosshairSelectedApp>(
                        crosshairSelectedAppsListState,
                        delegate(CrosshairSelectedApp app)
                        {
                            var candidate = new GameHelperAppCandidate
                            {
                                Id = app.Id,
                                Name = app.Name,
                                TargetPath = app.TargetPath,
                                Publisher = app.Publisher,
                                Source = app.Source,
                                IconPath = app.IconPath
                            };
                            return CreateGameHelperPickerCardElement(candidate, "Remove", false,
                                delegate
                                {
                                    controller.RemoveGameHelperCrosshairSelectedApp(app.Id);
                                    reloadSelectedApps();
                                    renderList();
                                    RenderGameHelperPage();
                                    RefreshStatus();
                                });
                        },
                        delegate(int visible, int total)
                        {
                            countText.Text = total == 0
                                ? "No selected apps."
                                : "Showing " + visible + " of " + total + " selected.";
                        });
                    crosshairSelectedAppsListState.SearchText = crosshairSelectedAppSearchText;
                    selectedAppsPresenter.Reset(selectedApps, CrosshairSelectedAppMatches,
                        CreateCrosshairSelectedAppComparison(), true);
                    listHost.Children.Add(selectedAppsPresenter.Element);
                    return;
                }

                if (installedApps == null)
                {
                    var state = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    if (loadingInstalledApps)
                    {
                        state.Children.Add(CreateAppGuardLoadingIndicator());
                        state.Children.Add(new TextBlock
                        {
                            Text = "Loading installed apps...",
                            FontSize = Z(13),
                            Foreground = MutedBrush,
                            HorizontalAlignment = HorizontalAlignment.Center
                        });
                        countText.Text = "Loading installed apps...";
                    }
                    else if (!string.IsNullOrWhiteSpace(installedAppsError))
                    {
                        state.Children.Add(new TextBlock
                        {
                            Text = installedAppsError,
                            FontSize = Z(13),
                            Foreground = MutedBrush,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                            MaxWidth = Z(520),
                            Margin = ZThickness(0, 0, 0, 12)
                        });
                        state.Children.Add(CreateActionButton("Try again", true, delegate
                        {
                            Task ignored = loadInstalledAppsAsync(false);
                        }));
                        countText.Text = "Installed apps could not be loaded.";
                    }
                    listHost.Children.Add(state);
                    return;
                }

                allAppsPresenter = new ProgressiveListPresenter<GameHelperAppCandidate>(
                    crosshairAppsListState,
                    delegate(GameHelperAppCandidate candidate)
                    {
                        bool selected = selectedIndex.ContainsExecutable(candidate.TargetPath);
                        return CreateGameHelperPickerCardElement(candidate,
                            selected ? "Remove" : "Select", !selected, delegate
                            {
                                if (selected)
                                {
                                    controller.RemoveGameHelperCrosshairSelectedApp(candidate.Id);
                                }
                                else
                                {
                                    controller.AddGameHelperCrosshairSelectedApp(candidate);
                                }
                                reloadSelectedApps();
                                renderList();
                                RenderGameHelperPage();
                                RefreshStatus();
                            });
                    },
                    delegate(int visible, int total)
                    {
                        countText.Text = total == 0
                            ? "No matching apps."
                            : "Showing " + visible + " of " + total + " • "
                                + selectedApps.Count + " selected.";
                    });
                crosshairAppsListState.SearchText = crosshairAppSearchText;
                allAppsPresenter.Reset(installedApps, GameHelperCandidateMatches,
                    CreateGameHelperCandidateComparison(crosshairAppsListState), true);
                listHost.Children.Add(allAppsPresenter.Element);
            };

            renderControls = delegate
            {
                controlsHost.Children.Clear();
                var controls = new WrapPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var searchBox = new TextBox
                {
                    Text = showingAllApps ? crosshairAppSearchText : crosshairSelectedAppSearchText,
                    Width = Z(230),
                    FontSize = Z(13),
                    Foreground = TextBrush,
                    Background = BrushFromRgb(0x0E, 0x12, 0x1B),
                    BorderBrush = CardBorderBrush,
                    Padding = ZThickness(10, 7, 10, 7),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = ZThickness(0, 0, 8, 8)
                };
                searchBox.TextChanged += delegate
                {
                    if (showingAllApps)
                    {
                        crosshairAppSearchText = searchBox.Text;
                    }
                    else
                    {
                        crosshairSelectedAppSearchText = searchBox.Text;
                    }
                    renderList();
                };
                searchBox.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape)
                    {
                        dialog.Close();
                        e.Handled = true;
                    }
                };
                controls.Children.Add(searchBox);

                Button clear = CreateActionButton("Clear", false, delegate
                {
                    searchBox.Text = string.Empty;
                });
                clear.Margin = ZThickness(0, 0, 8, 8);
                controls.Children.Add(clear);

                if (showingAllApps)
                {
                    Button refresh = CreateActionButton("Refresh", false, delegate
                    {
                        Task ignored = loadInstalledAppsAsync(true);
                    });
                    refresh.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(refresh);
                }

                ProgressiveListState<GameHelperAppCandidate> candidateState =
                    showingAllApps ? crosshairAppsListState : null;
                if (candidateState != null)
                {
                    ComboBox sort = CreateGameHelperSortCombo(candidateState, true, renderList);
                    sort.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(sort);
                    ComboBox direction = CreateGameHelperDirectionCombo(candidateState, renderList);
                    direction.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(direction);
                    ComboBox batch = CreateBatchSizeCombo(candidateState, renderList);
                    batch.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(batch);
                }
                else
                {
                    ComboBox sort = CreateGameHelperSortCombo(
                        crosshairSelectedAppsListState, true, renderList);
                    sort.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(sort);
                    ComboBox direction = CreateGameHelperDirectionCombo(
                        crosshairSelectedAppsListState, renderList);
                    direction.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(direction);
                    ComboBox batch = CreateBatchSizeCombo(
                        crosshairSelectedAppsListState, renderList);
                    batch.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(batch);
                }
                controlsHost.Children.Add(controls);
            };

            renderTabs = delegate
            {
                tabsHost.Children.Clear();
                Button allApps = CreateActionButton("All apps", showingAllApps, delegate
                {
                    selectTab(true);
                });
                allApps.Width = Z(132);
                allApps.Margin = ZThickness(0, 0, 8, 0);
                tabsHost.Children.Add(allApps);

                Button selected = CreateActionButton("Selected apps", !showingAllApps, delegate
                {
                    selectTab(false);
                });
                selected.Width = Z(148);
                tabsHost.Children.Add(selected);
            };

            selectTab = delegate(bool allApps)
            {
                if (showingAllApps == allApps)
                {
                    return;
                }
                ResetCrosshairSearchState();
                showingAllApps = allApps;
                renderTabs();
                renderControls();
                renderList();
                if (showingAllApps && installedApps == null && !loadingInstalledApps)
                {
                    Task ignored = loadInstalledAppsAsync(false);
                }
            };

            loadInstalledAppsAsync = async delegate(bool forceRefresh)
            {
                if (loadingInstalledApps || dialogClosed)
                {
                    return;
                }

                loadingInstalledApps = true;
                installedAppsError = string.Empty;
                installedApps = null;
                renderList();
                try
                {
                    List<GameHelperAppCandidate> loaded = await Task.Run(delegate
                    {
                        if (forceRefresh)
                        {
                            controller.RefreshGameHelperInstalledApps();
                        }
                        return controller.GetGameHelperInstalledApps();
                    });
                    if (!dialogClosed)
                    {
                        installedApps = loaded;
                    }
                }
                catch (Exception ex)
                {
                    if (!dialogClosed)
                    {
                        installedAppsError = ex.Message;
                    }
                }
                finally
                {
                    if (!dialogClosed)
                    {
                        loadingInstalledApps = false;
                        renderList();
                        RefreshStatus();
                    }
                }
            };

            dialog.Content = root;
            dialog.Closed += delegate
            {
                dialogClosed = true;
                disposePresenters();
            };
            renderTabs();
            renderControls();
            renderList();
            Task initialLoad = loadInstalledAppsAsync(false);
            dialog.ShowDialog();
        }

        private void ShowGameHelperManageApps()
        {
            ResetGameHelperSearchState();
            var dialog = CreateGameHelperDialog("Manage apps", Z(820), Z(660));
            var root = new Grid
            {
                Margin = ZThickness(24, 22, 24, 20)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Manage apps",
                FontSize = Z(24),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 14)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var tabsHost = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = ZThickness(0, 0, 0, 12)
            };
            Grid.SetRow(tabsHost, 1);
            root.Children.Add(tabsHost);

            var controlsHost = new Grid
            {
                Margin = ZThickness(0, 0, 0, 14)
            };
            Grid.SetRow(controlsHost, 2);
            root.Children.Add(controlsHost);

            var listHost = new Grid();
            Grid.SetRow(listHost, 3);
            root.Children.Add(listHost);

            var countText = new TextBlock
            {
                FontSize = Z(12),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(0, 0, 12, 0)
            };

            var bottom = new Grid
            {
                Margin = ZThickness(0, 16, 0, 0)
            };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(countText, 0);
            bottom.Children.Add(countText);
            var closeButton = CreateActionButton("Close", false, delegate { dialog.Close(); });
            Grid.SetColumn(closeButton, 1);
            bottom.Children.Add(closeButton);
            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            bool showingAllApps = false;
            bool loadingInstalledApps = false;
            bool dialogClosed = false;
            string installedAppsError = string.Empty;
            string expandedProtectedAppId = string.Empty;
            bool preserveListPosition = false;
            List<GameHelperAppCandidate> installedApps = null;
            List<GameHelperProtectedApp> protectedApps = controller.GetGameHelperProtectedApps();
            GameHelperAppProtectionIndex protectionIndex =
                new GameHelperAppProtectionIndex(protectedApps);
            ProgressiveListPresenter<GameHelperAppCandidate> allAppsPresenter = null;
            ProgressiveListPresenter<GameHelperProtectedApp> protectedAppsPresenter = null;

            Action reloadProtectedApps = delegate
            {
                protectedApps = controller.GetGameHelperProtectedApps();
                protectionIndex = new GameHelperAppProtectionIndex(protectedApps);
            };

            Action disposePresenters = delegate
            {
                if (allAppsPresenter != null)
                {
                    allAppsPresenter.Dispose();
                    allAppsPresenter = null;
                }
                if (protectedAppsPresenter != null)
                {
                    protectedAppsPresenter.Dispose();
                    protectedAppsPresenter = null;
                }
            };

            Action renderList = null;
            Action renderControls = null;
            Action renderTabs = null;
            Action<bool> selectTab = null;
            Func<bool, Task> loadInstalledAppsAsync = null;

            renderList = delegate
            {
                double previousOffset = 0;
                if (allAppsPresenter != null)
                {
                    previousOffset = allAppsPresenter.VerticalOffset;
                }
                else if (protectedAppsPresenter != null)
                {
                    previousOffset = protectedAppsPresenter.VerticalOffset;
                }

                disposePresenters();
                listHost.Children.Clear();

                if (showingAllApps)
                {
                    if (installedApps == null)
                    {
                        var state = new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        if (loadingInstalledApps)
                        {
                            state.Children.Add(CreateAppGuardLoadingIndicator());
                            state.Children.Add(new TextBlock
                            {
                                Text = "Loading installed apps...",
                                FontSize = Z(13),
                                Foreground = MutedBrush,
                                HorizontalAlignment = HorizontalAlignment.Center
                            });
                            countText.Text = "Loading installed apps...";
                        }
                        else if (!string.IsNullOrWhiteSpace(installedAppsError))
                        {
                            state.Children.Add(new TextBlock
                            {
                                Text = installedAppsError,
                                FontSize = Z(13),
                                Foreground = MutedBrush,
                                TextWrapping = TextWrapping.Wrap,
                                TextAlignment = TextAlignment.Center,
                                MaxWidth = Z(520),
                                Margin = ZThickness(0, 0, 0, 12)
                            });
                            state.Children.Add(CreateActionButton("Try again", true, delegate
                            {
                                Task ignored = loadInstalledAppsAsync(false);
                            }));
                            countText.Text = "Installed apps could not be loaded.";
                        }
                        listHost.Children.Add(state);
                        return;
                    }

                    allAppsPresenter = new ProgressiveListPresenter<GameHelperAppCandidate>(
                        gameHelperAddAppsListState,
                        delegate(GameHelperAppCandidate candidate)
                        {
                            GameHelperProtectedApp protectedApp;
                            if (!protectionIndex.TryGet(candidate.TargetPath, out protectedApp))
                            {
                                return CreateGameHelperPickerCardElement(candidate, "Add", true, delegate
                                {
                                    controller.AddGameHelperProtectedApp(candidate);
                                    reloadProtectedApps();
                                    preserveListPosition = true;
                                    RenderGameHelperPage();
                                    renderList();
                                    RefreshStatus();
                                });
                            }

                            string expansionKey = GetGameHelperProtectedAppExpansionKey(protectedApp);
                            bool expanded = !string.IsNullOrWhiteSpace(expandedProtectedAppId)
                                && string.Equals(expandedProtectedAppId, expansionKey,
                                    StringComparison.OrdinalIgnoreCase);
                            return CreateGameHelperProtectedCardElement(protectedApp, expanded, delegate
                            {
                                expandedProtectedAppId = expanded ? string.Empty : expansionKey;
                                preserveListPosition = true;
                                renderList();
                            }, delegate
                            {
                                controller.RemoveGameHelperProtectedApp(protectedApp.Id);
                                expandedProtectedAppId = string.Empty;
                                reloadProtectedApps();
                                preserveListPosition = true;
                                RenderGameHelperPage();
                                renderList();
                                RefreshStatus();
                            });
                        },
                        delegate(int visible, int total)
                        {
                            countText.Text = total == 0 ? "No matching apps." : "Showing " + visible + " of " + total;
                        });
                    gameHelperAddAppsListState.SearchText = gameHelperSearchText;
                    allAppsPresenter.Reset(installedApps, GameHelperCandidateMatches,
                        CreateGameHelperCandidateComparison(), !preserveListPosition);
                    listHost.Children.Add(allAppsPresenter.Element);
                    if (preserveListPosition)
                    {
                        allAppsPresenter.RestoreVerticalOffset(previousOffset);
                    }
                    preserveListPosition = false;
                    return;
                }

                protectedAppsPresenter = new ProgressiveListPresenter<GameHelperProtectedApp>(
                    gameHelperProtectedAppsListState,
                    delegate(GameHelperProtectedApp app)
                    {
                        string expansionKey = GetGameHelperProtectedAppExpansionKey(app);
                        bool expanded = !string.IsNullOrWhiteSpace(expandedProtectedAppId)
                            && string.Equals(expandedProtectedAppId, expansionKey,
                                StringComparison.OrdinalIgnoreCase);
                        return CreateGameHelperProtectedCardElement(app, expanded, delegate
                        {
                            expandedProtectedAppId = expanded ? string.Empty : expansionKey;
                            preserveListPosition = true;
                            renderList();
                        }, delegate
                        {
                            controller.RemoveGameHelperProtectedApp(app.Id);
                            expandedProtectedAppId = string.Empty;
                            reloadProtectedApps();
                            preserveListPosition = true;
                            RenderGameHelperPage();
                            renderList();
                            RefreshStatus();
                        });
                    },
                    delegate(int visible, int total)
                    {
                        countText.Text = total == 0 ? "No protected apps." : "Showing " + visible + " of " + total;
                    });
                gameHelperProtectedAppsListState.SearchText = gameHelperProtectedSearchText;
                protectedAppsPresenter.Reset(protectedApps, GameHelperProtectedAppMatches,
                    CreateGameHelperProtectedComparison(), !preserveListPosition);
                listHost.Children.Add(protectedAppsPresenter.Element);
                if (preserveListPosition)
                {
                    protectedAppsPresenter.RestoreVerticalOffset(previousOffset);
                }
                preserveListPosition = false;
            };

            renderControls = delegate
            {
                controlsHost.Children.Clear();
                var controls = new WrapPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var searchBox = new TextBox
                {
                    Text = showingAllApps ? gameHelperSearchText : gameHelperProtectedSearchText,
                    Width = Z(230),
                    FontSize = Z(13),
                    Foreground = TextBrush,
                    Background = BrushFromRgb(0x0E, 0x12, 0x1B),
                    BorderBrush = CardBorderBrush,
                    Padding = ZThickness(10, 7, 10, 7),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = ZThickness(0, 0, 8, 8)
                };
                searchBox.TextChanged += delegate
                {
                    if (showingAllApps)
                    {
                        gameHelperSearchText = searchBox.Text;
                    }
                    else
                    {
                        gameHelperProtectedSearchText = searchBox.Text;
                    }
                    renderList();
                };
                searchBox.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape)
                    {
                        dialog.Close();
                        e.Handled = true;
                    }
                };
                controls.Children.Add(searchBox);

                Button clearButton = CreateActionButton("Clear", false, delegate
                {
                    searchBox.Text = string.Empty;
                });
                clearButton.Margin = ZThickness(0, 0, 8, 8);
                controls.Children.Add(clearButton);

                if (showingAllApps)
                {
                    Button refreshButton = CreateActionButton("Refresh", false, delegate
                    {
                        Task ignored = loadInstalledAppsAsync(true);
                    });
                    refreshButton.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(refreshButton);
                    ComboBox sort = CreateGameHelperSortCombo(gameHelperAddAppsListState, true, renderList);
                    sort.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(sort);
                    ComboBox direction = CreateGameHelperDirectionCombo(gameHelperAddAppsListState, renderList);
                    direction.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(direction);
                    ComboBox batch = CreateBatchSizeCombo(gameHelperAddAppsListState, renderList);
                    batch.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(batch);
                }
                else
                {
                    ComboBox sort = CreateGameHelperSortCombo(gameHelperProtectedAppsListState, false, renderList);
                    sort.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(sort);
                    ComboBox direction = CreateGameHelperDirectionCombo(gameHelperProtectedAppsListState, renderList);
                    direction.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(direction);
                    ComboBox batch = CreateBatchSizeCombo(gameHelperProtectedAppsListState, renderList);
                    batch.Margin = ZThickness(0, 0, 8, 8);
                    controls.Children.Add(batch);
                }

                controlsHost.Children.Add(controls);
            };

            renderTabs = delegate
            {
                tabsHost.Children.Clear();
                Button allAppsTab = CreateActionButton("All apps", showingAllApps, delegate
                {
                    selectTab(true);
                });
                allAppsTab.Width = Z(132);
                allAppsTab.Margin = ZThickness(0, 0, 8, 0);
                tabsHost.Children.Add(allAppsTab);

                Button protectedAppsTab = CreateActionButton("Protected apps", !showingAllApps, delegate
                {
                    selectTab(false);
                });
                protectedAppsTab.Width = Z(148);
                tabsHost.Children.Add(protectedAppsTab);
            };

            selectTab = delegate(bool allApps)
            {
                if (showingAllApps == allApps)
                {
                    return;
                }
                ResetGameHelperSearchState();
                showingAllApps = allApps;
                expandedProtectedAppId = string.Empty;
                preserveListPosition = false;
                renderTabs();
                renderControls();
                renderList();
                if (showingAllApps && installedApps == null && !loadingInstalledApps)
                {
                    Task ignored = loadInstalledAppsAsync(false);
                }
            };

            loadInstalledAppsAsync = async delegate(bool forceRefresh)
            {
                if (loadingInstalledApps || dialogClosed)
                {
                    return;
                }

                loadingInstalledApps = true;
                installedAppsError = string.Empty;
                installedApps = null;
                renderList();
                try
                {
                    List<GameHelperAppCandidate> loaded = await Task.Run(delegate
                    {
                        if (forceRefresh)
                        {
                            controller.RefreshGameHelperInstalledApps();
                        }
                        return controller.GetGameHelperInstalledApps();
                    });
                    if (!dialogClosed)
                    {
                        installedApps = loaded;
                    }
                }
                catch (Exception ex)
                {
                    if (!dialogClosed)
                    {
                        installedAppsError = ex.Message;
                    }
                }
                finally
                {
                    if (!dialogClosed)
                    {
                        loadingInstalledApps = false;
                        renderList();
                        RefreshStatus();
                    }
                }
            };

            dialog.Content = root;
            dialog.Closed += delegate
            {
                dialogClosed = true;
                disposePresenters();
            };
            renderTabs();
            renderControls();
            renderList();
            dialog.ShowDialog();
        }


        private Window CreateGameHelperDialog(string title, double width, double height)
        {
            Rect workArea = GetCurrentMonitorWorkAreaInDips();
            double margin = GetDialogScreenMargin(workArea);
            double normalMaxWidth = Math.Max(1, workArea.Width - (margin * 2));
            double normalMaxHeight = Math.Max(1, workArea.Height - (margin * 2));
            double minWidth = Math.Min(Z(620), normalMaxWidth);
            double minHeight = Math.Min(Z(460), normalMaxHeight);

            var dialog = new Window
            {
                Title = title,
                Owner = this,
                Width = Clamp(width, minWidth, normalMaxWidth),
                Height = Clamp(height, minHeight, normalMaxHeight),
                MinWidth = minWidth,
                MinHeight = minHeight,
                MaxWidth = double.PositiveInfinity,
                MaxHeight = double.PositiveInfinity,
                ResizeMode = ResizeMode.CanResize,
                WindowStyle = WindowStyle.SingleBorderWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = WindowBrush,
                Foreground = TextBrush,
                FontFamily = FontFamily,
                ShowInTaskbar = true
            };

            var floatingState = new FloatingDialogState(dialog);
            dialog.SizeChanged += delegate
            {
                floatingState.CaptureNormalBounds();
                EnsureDialogWithinWorkArea(dialog);
            };
            dialog.LocationChanged += delegate
            {
                floatingState.CaptureNormalBounds();
            };
            dialog.StateChanged += delegate
            {
                floatingState.HandleStateChanged();
                if (dialog.WindowState == WindowState.Normal)
                {
                    EnsureDialogWithinWorkArea(dialog);
                }
            };
            dialog.SourceInitialized += delegate
            {
                floatingState.AttachWindowHook();
            };
            dialog.Loaded += delegate
            {
                floatingState.CaptureNormalBounds();
                dialog.Dispatcher.BeginInvoke(new Action(floatingState.CaptureNormalBounds),
                    DispatcherPriority.ApplicationIdle);
            };

            return dialog;
        }

        private void PlaceCrosshairSettingsDialog(Window dialog)
        {
            Window owner = dialog.Owner ?? this;
            Rect workArea = GetMonitorWorkAreaInDips(owner);
            double margin = GetDialogScreenMargin(workArea);
            FitDialogToWorkArea(dialog, workArea, margin);

            double dialogWidth = GetWindowLayoutWidth(dialog);
            double dialogHeight = GetWindowLayoutHeight(dialog);
            Rect bounds = workArea;
            if (owner != null && owner.IsVisible)
            {
                Rect ownerBounds = GetWindowBoundsInDips(owner);
                ownerBounds.Intersect(workArea);
                if (!ownerBounds.IsEmpty && ownerBounds.Width > 0 && ownerBounds.Height > 0)
                {
                    bounds = ownerBounds;
                }
            }

            double left = bounds.Left + ((bounds.Width - dialogWidth) / 2.0);
            double top = bounds.Top + ((bounds.Height - dialogHeight) / 2.0);

            left = Clamp(left, workArea.Left + margin, workArea.Right - dialogWidth - margin);
            top = Clamp(top, workArea.Top + margin, workArea.Bottom - dialogHeight - margin);

            dialog.Left = Math.Round(left);
            dialog.Top = Math.Round(top);
        }

        private void AddGameHelperPickerCard(Panel target, GameHelperAppCandidate candidate,
            string actionText, bool primaryAction, RoutedEventHandler onClick)
        {
            string iconPath = string.IsNullOrWhiteSpace(candidate.IconPath) ? candidate.TargetPath : candidate.IconPath;
            AddCardToPanel(target, candidate.Name,
                BuildGameHelperAppDescription(candidate.Publisher, candidate.Source, candidate.TargetPath),
                CreateActionButton(actionText, primaryAction, onClick), false);

            Border card = target.Children[target.Children.Count - 1] as Border;
            if (card == null)
            {
                return;
            }

            Grid grid = card.Child as Grid;
            if (grid == null || grid.Children.Count == 0)
            {
                return;
            }

            grid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
            UIElement icon = CreateFileIcon(iconPath);
            icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetValue(MarginProperty, ZThickness(0, 0, 16, 0));
            Grid.SetColumn(icon, 0);
            grid.Children.Insert(0, icon);

            for (int i = 1; i < grid.Children.Count; i++)
            {
                UIElement child = grid.Children[i];
                int column = Grid.GetColumn(child);
                Grid.SetColumn(child, column + 1);
            }
        }

        private UIElement CreateGameHelperPickerCardElement(GameHelperAppCandidate candidate,
            string actionText, bool primaryAction, RoutedEventHandler onClick)
        {
            var target = new StackPanel();
            AddGameHelperPickerCard(target, candidate, actionText, primaryAction, onClick);
            UIElement card = target.Children[0];
            target.Children.Remove(card);
            return card;
        }

        private UIElement CreateGameHelperProtectedCardElement(GameHelperProtectedApp app,
            bool expanded, Action onToggle, RoutedEventHandler onRemove)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = expanded ? AccentBrush : CardBorderBrush,
                BorderThickness = new Thickness(expanded ? 1.5 : 1),
                CornerRadius = new CornerRadius(7),
                Margin = ZThickness(0, 0, 0, 8),
                Tag = GetGameHelperProtectedAppExpansionKey(app)
            };

            var stack = new StackPanel();
            card.Child = stack;

            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var identityRow = new Grid();
            identityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            identityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement icon = CreateFileIcon(app.TargetPath);
            icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetValue(MarginProperty, ZThickness(0, 0, 14, 0));
            Grid.SetColumn(icon, 0);
            identityRow.Children.Add(icon);

            var name = new TextBlock
            {
                Text = app.Name,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1);
            identityRow.Children.Add(name);

            var identityButton = new Button
            {
                Content = identityRow,
                Style = (Style)FindResource("NavButtonStyle"),
                Background = TransparentBrush,
                BorderBrush = TransparentBrush,
                BorderThickness = new Thickness(0),
                Padding = ZThickness(18, 14, 12, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = expanded ? "Collapse app details" : "Expand app details"
            };
            identityButton.Click += delegate
            {
                if (onToggle != null)
                {
                    onToggle();
                }
            };
            Grid.SetColumn(identityButton, 0);
            headerRow.Children.Add(identityButton);

            Button protectedButton = CreateActionButton(
                expanded ? "Protected  ▲" : "Protected  ▼", false, delegate
            {
                if (onToggle != null)
                {
                    onToggle();
                }
            });
            protectedButton.Margin = ZThickness(12, 10, 0, 10);
            protectedButton.ToolTip = expanded ? "Collapse app details" : "Expand app details";
            Grid.SetColumn(protectedButton, 1);
            headerRow.Children.Add(protectedButton);

            Button removeButton = CreateActionButton("×", false, onRemove);
            removeButton.Width = Z(40);
            removeButton.MinWidth = Z(40);
            removeButton.FontSize = Z(18);
            removeButton.FontWeight = FontWeights.SemiBold;
            removeButton.Padding = new Thickness(0);
            removeButton.Margin = ZThickness(8, 10, 18, 10);
            removeButton.ToolTip = "Remove protection";
            Grid.SetColumn(removeButton, 2);
            headerRow.Children.Add(removeButton);

            stack.Children.Add(headerRow);

            if (expanded)
            {
                stack.Children.Add(CreateGameHelperProtectedDetails(app, onRemove));
            }

            return card;
        }

        private UIElement CreateGameHelperProtectedDetails(GameHelperProtectedApp app,
            RoutedEventHandler onRemove)
        {
            var detail = new Border
            {
                Background = BrushFromRgb(0x15, 0x1B, 0x27),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = ZThickness(18, 18, 18, 16)
            };
            var stack = new StackPanel();
            detail.Child = stack;

            Action save = delegate
            {
                controller.UpdateGameHelperProtectedAppOptions(app.Id, app.BlockWindowsKey,
                    app.RightControlDShowsDesktop, app.LockMicrosoftEnglish,
                    app.BlockInputLanguageSwitch, app.RestorePreviousInputLanguage);
            };

            var protectionGrid = new Grid();
            protectionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            protectionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            protectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            protectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Z(12)) });
            protectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            UIElement windowsProtection = CreateGameHelperProtectionSection("Windows key protection",
                CreateGameHelperOptionRow("封鎖 Windows 鍵", app.BlockWindowsKey, delegate(bool value)
                {
                    app.BlockWindowsKey = value;
                    save();
                }),
                CreateGameHelperOptionRow("Right Ctrl + D 顯示桌面", app.RightControlDShowsDesktop,
                    delegate(bool value)
                    {
                        app.RightControlDShowsDesktop = value;
                        save();
                    }));
            Grid.SetColumn(windowsProtection, 0);
            protectionGrid.Children.Add(windowsProtection);

            UIElement inputProtection = CreateGameHelperProtectionSection("Input method protection",
                CreateGameHelperOptionRow("遊戲期間鎖定 Microsoft ENG", app.LockMicrosoftEnglish,
                    delegate(bool value)
                    {
                        app.LockMicrosoftEnglish = value;
                        save();
                    }),
                CreateGameHelperOptionRow("阻止輸入法切換（不依賴快捷鍵）", app.BlockInputLanguageSwitch,
                    delegate(bool value)
                    {
                        app.BlockInputLanguageSwitch = value;
                        save();
                    }),
                CreateGameHelperOptionRow("離開遊戲後恢復原輸入法", app.RestorePreviousInputLanguage,
                    delegate(bool value)
                    {
                        app.RestorePreviousInputLanguage = value;
                        save();
                    }));
            Grid.SetColumn(inputProtection, 2);
            protectionGrid.Children.Add(inputProtection);
            AttachGameHelperProtectionLayout(detail, protectionGrid,
                windowsProtection, inputProtection, Z(620));
            stack.Children.Add(protectionGrid);

            stack.Children.Add(new Border
            {
                Height = Math.Max(1, Z(1)),
                Background = CardBorderBrush,
                Margin = ZThickness(0, 16, 0, 14)
            });
            stack.Children.Add(CreateGameHelperOptionsSectionTitle("App information"));
            stack.Children.Add(CreateGameHelperInfoRow("Publisher",
                string.IsNullOrWhiteSpace(app.Publisher) ? "Unknown" : app.Publisher));
            stack.Children.Add(CreateGameHelperInfoRow("Status", "Protected"));
            stack.Children.Add(CreateGameHelperInfoRow("Executable path",
                string.IsNullOrWhiteSpace(app.TargetPath) ? "Unavailable" : app.TargetPath));

            Button remove = CreateActionButton("Remove", false, onRemove);
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            remove.Margin = ZThickness(0, 14, 0, 0);
            stack.Children.Add(remove);
            return detail;
        }

        private void AttachGameHelperProtectionLayout(Border host, Grid grid,
            UIElement leftSection, UIElement rightSection, double stackBelowWidth)
        {
            RoutedEventHandler loaded = null;
            loaded = delegate
            {
                host.Loaded -= loaded;
                ApplyGameHelperProtectionLayout(host, grid, leftSection, rightSection, stackBelowWidth);
            };
            host.Loaded += loaded;
            host.SizeChanged += delegate
            {
                ApplyGameHelperProtectionLayout(host, grid, leftSection, rightSection, stackBelowWidth);
            };
        }

        private void ApplyGameHelperProtectionLayout(Border host, Grid grid,
            UIElement leftSection, UIElement rightSection, double stackBelowWidth)
        {
            bool stacked = IsFinite(host.ActualWidth) && host.ActualWidth > 0
                && host.ActualWidth < stackBelowWidth;

            Grid.SetRow(leftSection, 0);
            Grid.SetColumn(leftSection, 0);
            Grid.SetColumnSpan(leftSection, stacked ? 3 : 1);
            leftSection.SetValue(MarginProperty, new Thickness(0));
            leftSection.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            Grid.SetRow(rightSection, stacked ? 1 : 0);
            Grid.SetColumn(rightSection, stacked ? 0 : 2);
            Grid.SetColumnSpan(rightSection, stacked ? 3 : 1);
            rightSection.SetValue(MarginProperty,
                stacked ? ZThickness(0, 12, 0, 0) : new Thickness(0));
            rightSection.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        }

        private UIElement CreateGameHelperProtectionSection(string title, params UIElement[] rows)
        {
            var section = new Border
            {
                Background = BrushFromRgb(0x12, 0x18, 0x24),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = ZThickness(14, 13, 14, 9)
            };
            var stack = new StackPanel();
            section.Child = stack;
            stack.Children.Add(CreateGameHelperOptionsSectionTitle(title));
            if (rows != null)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i] != null)
                    {
                        stack.Children.Add(rows[i]);
                    }
                }
            }
            return section;
        }

        private UIElement CreateGameHelperInfoRow(string label, string value)
        {
            var row = new Grid
            {
                Margin = ZThickness(0, 0, 0, 7)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Z(126)) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = Z(12),
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedBrush,
                Margin = ZThickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(labelText, 0);
            row.Children.Add(labelText);

            var valueText = new TextBlock
            {
                Text = value,
                FontSize = Z(12),
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            return row;
        }

        private UIElement CreateGameHelperOptionsSectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = Z(12),
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedBrush,
                Margin = ZThickness(0, 0, 0, 8)
            };
        }

        private UIElement CreateGameHelperOptionRow(string label, bool value, Action<bool> onChanged)
        {
            var row = new Grid
            {
                Margin = ZThickness(0, 0, 0, 6)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = label,
                FontSize = Z(12),
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(0, 0, 12, 0)
            };
            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            UIElement toggle = CreateToggle(value, onChanged);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            return row;
        }

        private void AddDialogMessageCard(Panel target, string title, string description)
        {
            AddCardToPanel(target, title, description, null, false);
        }

        private UIElement CreateGameHelperRefreshActions()
        {
            var panel = CreateButtonRow();
            panel.Children.Add(CreateActionButton("Refresh", false, delegate
            {
                controller.RefreshGameHelperInstalledApps();
                RenderGameHelperPage();
                RefreshStatus();
            }));
            return panel;
        }

        private static bool GameHelperCandidateMatches(GameHelperAppCandidate candidate, string query)
        {
            return AppIdentityService.MatchesAppSearch(candidate.Name, candidate.Publisher,
                candidate.Source, candidate.TargetPath, query);
        }

        private static bool GameHelperProtectedAppMatches(GameHelperProtectedApp app, string query)
        {
            return AppIdentityService.MatchesAppSearch(app.Name, app.Publisher,
                string.Empty, app.TargetPath, query);
        }

        private static bool CrosshairSelectedAppMatches(CrosshairSelectedApp app, string query)
        {
            return AppIdentityService.MatchesAppSearch(app.Name, app.Publisher,
                app.Source, app.TargetPath, query);
        }

        private static string GetGameHelperProtectedAppExpansionKey(GameHelperProtectedApp app)
        {
            if (app == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(app.Id))
            {
                return app.Id;
            }
            if (!string.IsNullOrWhiteSpace(app.TargetPath))
            {
                return app.TargetPath;
            }
            return app.Name ?? string.Empty;
        }

        private Comparison<GameHelperAppCandidate> CreateGameHelperCandidateComparison()
        {
            return CreateGameHelperCandidateComparison(gameHelperAddAppsListState);
        }

        private Comparison<GameHelperAppCandidate> CreateGameHelperCandidateComparison(
            ProgressiveListState<GameHelperAppCandidate> state)
        {
            string key = state.SortKey;
            return delegate(GameHelperAppCandidate left, GameHelperAppCandidate right)
            {
                return CompareGameHelperAppValues(key,
                    left == null ? string.Empty : left.Name,
                    left == null ? string.Empty : left.Publisher,
                    left == null ? string.Empty : left.Source,
                    left == null ? string.Empty : left.TargetPath,
                    right == null ? string.Empty : right.Name,
                    right == null ? string.Empty : right.Publisher,
                    right == null ? string.Empty : right.Source,
                    right == null ? string.Empty : right.TargetPath);
            };
        }

        private Comparison<CrosshairSelectedApp> CreateCrosshairSelectedAppComparison()
        {
            string key = crosshairSelectedAppsListState.SortKey;
            return delegate(CrosshairSelectedApp left, CrosshairSelectedApp right)
            {
                return CompareGameHelperAppValues(key,
                    left == null ? string.Empty : left.Name,
                    left == null ? string.Empty : left.Publisher,
                    left == null ? string.Empty : left.Source,
                    left == null ? string.Empty : left.TargetPath,
                    right == null ? string.Empty : right.Name,
                    right == null ? string.Empty : right.Publisher,
                    right == null ? string.Empty : right.Source,
                    right == null ? string.Empty : right.TargetPath);
            };
        }

        private Comparison<GameHelperProtectedApp> CreateGameHelperProtectedComparison()
        {
            string key = gameHelperProtectedAppsListState.SortKey;
            return delegate(GameHelperProtectedApp left, GameHelperProtectedApp right)
            {
                return CompareGameHelperAppValues(key,
                    left == null ? string.Empty : left.Name,
                    left == null ? string.Empty : left.Publisher,
                    string.Empty,
                    left == null ? string.Empty : left.TargetPath,
                    right == null ? string.Empty : right.Name,
                    right == null ? string.Empty : right.Publisher,
                    string.Empty,
                    right == null ? string.Empty : right.TargetPath);
            };
        }

        private static int CompareGameHelperAppValues(string key,
            string leftName, string leftPublisher, string leftSource, string leftPath,
            string rightName, string rightPublisher, string rightSource, string rightPath)
        {
            int value;
            if (string.Equals(key, "publisher", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Compare(leftPublisher, rightPublisher, StringComparison.CurrentCultureIgnoreCase);
                return value != 0 ? value : CompareGameHelperAppIdentity(leftName, leftPublisher, leftPath, rightName, rightPublisher, rightPath);
            }
            if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Compare(leftSource, rightSource, StringComparison.CurrentCultureIgnoreCase);
                return value != 0 ? value : CompareGameHelperAppIdentity(leftName, leftPublisher, leftPath, rightName, rightPublisher, rightPath);
            }
            if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Compare(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
                return value != 0 ? value : CompareGameHelperAppIdentity(leftName, leftPublisher, leftPath, rightName, rightPublisher, rightPath);
            }

            return CompareGameHelperAppIdentity(leftName, leftPublisher, leftPath, rightName, rightPublisher, rightPath);
        }

        private static int CompareGameHelperAppIdentity(string leftName, string leftPublisher, string leftPath,
            string rightName, string rightPublisher, string rightPath)
        {
            int value = string.Compare(leftName, rightName, StringComparison.CurrentCultureIgnoreCase);
            if (value != 0)
            {
                return value;
            }

            value = string.Compare(leftPublisher, rightPublisher, StringComparison.CurrentCultureIgnoreCase);
            if (value != 0)
            {
                return value;
            }

            return string.Compare(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildGameHelperAppDescription(string publisher, string source, string targetPath)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(publisher))
            {
                parts.Add(publisher);
            }
            if (!string.IsNullOrWhiteSpace(source))
            {
                parts.Add(source);
            }
            if (parts.Count == 0)
            {
                parts.Add("Installed app");
            }

            string path = targetPath ?? string.Empty;
            if (path.Length > 88)
            {
                path = "..." + path.Substring(path.Length - 85);
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                parts.Add(path);
            }

            return string.Join("  |  ", parts.ToArray());
        }

        private static bool ContainsIgnoreCase(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.IndexOf(value, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private StackPanel CreateButtonRow()
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
        }

        private Button CreateActionButton(string text, bool primary, RoutedEventHandler onClick)
        {
            var button = new Button
            {
                Content = text,
                Style = (Style)FindResource(primary ? "PrimaryActionButtonStyle" : "ActionButtonStyle"),
                FontSize = Z(13),
                MinWidth = Z(96),
                MinHeight = Z(38),
                Padding = ZThickness(18, 8, 18, 8),
                Margin = ZThickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (onClick != null)
            {
                button.Click += onClick;
            }
            return button;
        }

        private UIElement CreateResponsiveButtonGrid(IList<Button> buttons)
        {
            var grid = new UniformGrid
            {
                Columns = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            if (buttons != null)
            {
                for (int i = 0; i < buttons.Count; i++)
                {
                    Button button = buttons[i];
                    if (button == null)
                    {
                        continue;
                    }

                    button.Margin = ZThickness(0, 0, 8, 8);
                    button.HorizontalAlignment = HorizontalAlignment.Stretch;
                    button.VerticalAlignment = VerticalAlignment.Stretch;
                    grid.Children.Add(button);
                }
            }

            RoutedEventHandler loaded = null;
            loaded = delegate
            {
                grid.Loaded -= loaded;
                ApplyResponsiveButtonGridColumns(grid);
            };
            grid.Loaded += loaded;
            grid.SizeChanged += delegate { ApplyResponsiveButtonGridColumns(grid); };

            SizeChangedEventHandler viewportChanged = delegate { ApplyResponsiveButtonGridColumns(grid); };
            if (ContentScrollViewer != null)
            {
                ContentScrollViewer.SizeChanged += viewportChanged;
                grid.Unloaded += delegate { ContentScrollViewer.SizeChanged -= viewportChanged; };
            }
            return grid;
        }

        private void ApplyResponsiveButtonGridColumns(UniformGrid grid)
        {
            if (grid == null || grid.Children.Count == 0)
            {
                return;
            }

            double width = grid.ActualWidth;
            if (!IsFinite(width) || width <= 0)
            {
                return;
            }

            if (ContentScrollViewer != null && ContentFrame != null)
            {
                double layoutScale = UiScaleTransform == null ? 1 : UiScaleTransform.ScaleX;
                if (!IsFinite(layoutScale) || layoutScale <= 0)
                {
                    layoutScale = 1;
                }

                double viewportWidth = ActualWidth / layoutScale;
                if (SidebarColumn != null)
                {
                    viewportWidth -= SidebarColumn.ActualWidth + Z(6);
                }
                if (!IsFinite(viewportWidth) || viewportWidth <= 0)
                {
                    viewportWidth = ContentScrollViewer.ViewportWidth;
                }
                double nestedPadding = Z(18 * 2 + 16 * 2);
                double availableWidth = viewportWidth
                    - ContentFrame.Padding.Left
                    - ContentFrame.Padding.Right
                    - nestedPadding;
                if (IsFinite(availableWidth) && availableWidth > 0)
                {
                    grid.MaxWidth = availableWidth;
                    width = Math.Min(width, availableWidth);
                }
            }

            double minCellWidth = Z(140);
            int maxFit = Math.Max(1, (int)Math.Floor(width / minCellWidth));
            maxFit = Math.Min(grid.Children.Count, maxFit);

            int bestColumns = 1;
            int bestScore = int.MinValue;
            for (int columns = 1; columns <= maxFit; columns++)
            {
                int remainder = grid.Children.Count % columns;
                int lastRowCount = remainder == 0 ? columns : remainder;
                int score = columns * 10;
                if (lastRowCount == 1 && grid.Children.Count > 1)
                {
                    score -= 100;
                }
                score -= Math.Abs(columns - lastRowCount);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestColumns = columns;
                }
            }

            grid.Columns = Math.Max(1, bestColumns);
        }

        private ComboBox CreateSmallComboBox(double width)
        {
            return new ComboBox
            {
                Width = width,
                FontSize = Z(13),
                Margin = ZThickness(8, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private ComboBox CreateGameHelperSortCombo<T>(ProgressiveListState<T> state, bool includeSource, Action refreshList)
        {
            var combo = CreateSmallComboBox(Z(includeSource ? 118 : 112));
            AddComboItem(combo, "Original", "original", state.SortKey);
            AddComboItem(combo, "Name", "name", state.SortKey);
            AddComboItem(combo, "Publisher", "publisher", state.SortKey);
            if (includeSource)
            {
                AddComboItem(combo, "Source", "source", state.SortKey);
            }
            AddComboItem(combo, "Path", "path", state.SortKey);
            combo.SelectionChanged += delegate
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                state.SortKey = Convert.ToString(item.Tag);
                controller.SaveSettings();
                if (refreshList != null)
                {
                    refreshList();
                }
            };
            return combo;
        }

        private ComboBox CreateGameHelperDirectionCombo<T>(ProgressiveListState<T> state, Action refreshList)
        {
            string selected = state.SortDescending ? "true" : "false";
            var combo = CreateSmallComboBox(Z(118));
            AddComboItem(combo, "Ascending", "false", selected);
            AddComboItem(combo, "Descending", "true", selected);
            combo.SelectionChanged += delegate
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                state.SortDescending = string.Equals(Convert.ToString(item.Tag), "true", StringComparison.OrdinalIgnoreCase);
                controller.SaveSettings();
                if (refreshList != null)
                {
                    refreshList();
                }
            };
            return combo;
        }

        private ComboBox CreateBatchSizeCombo<T>(ProgressiveListState<T> state, Action refreshList)
        {
            var combo = CreateSmallComboBox(Z(124));
            int[] sizes = state.AllowedBatchSizes;
            for (int i = 0; i < sizes.Length; i++)
            {
                AddComboItem(combo, sizes[i] + " per batch", sizes[i].ToString(),
                    state.BatchSize.ToString());
            }
            combo.SelectionChanged += delegate
            {
                ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }

                int value;
                if (int.TryParse(Convert.ToString(item.Tag), out value))
                {
                    state.BatchSize = value;
                    controller.SaveSettings();
                    if (refreshList != null)
                    {
                        refreshList();
                    }
                }
            };
            return combo;
        }

        private void AddComboItem(ComboBox combo, string label, string tag, string selectedTag)
        {
            var item = new ComboBoxItem
            {
                Content = label,
                Tag = tag
            };
            combo.Items.Add(item);
            if (string.Equals(tag, selectedTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
            }
        }

        private TextBlock CreateProgressiveCountText()
        {
            return new TextBlock
            {
                FontSize = Z(12),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "Loading"
            };
        }

        private double GetProgressiveListHeight(double viewportRatio, double minimum, double maximum)
        {
            double viewport = ContentScrollViewer == null ? ActualHeight : ContentScrollViewer.ViewportHeight;
            if (!IsFinite(viewport) || viewport <= 0)
            {
                viewport = ActualHeight;
            }
            return Z(Math.Max(minimum, Math.Min(maximum, viewport * viewportRatio)));
        }

        private void DisposeMainListPresenters()
        {
            if (audioMixerPresenter != null) audioMixerPresenter.Dispose();
            if (appGuardPresenter != null) appGuardPresenter.Dispose();
            if (displayMonitorPresenter != null) displayMonitorPresenter.Dispose();
            audioMixerPresenter = null;
            appGuardPresenter = null;
            displayMonitorPresenter = null;
        }

        private UIElement CreateReadOnlyValue(string value, double width)
        {
            return new TextBox
            {
                Text = value,
                Width = Z(width),
                IsReadOnly = true,
                FontSize = Z(13),
                Foreground = TextBrush,
                Background = BrushFromRgb(0x0E, 0x12, 0x1B),
                BorderBrush = CardBorderBrush,
                Padding = ZThickness(8, 5, 8, 5)
            };
        }

        private UIElement CreateStaticValue(string value)
        {
            return new Border
            {
                Background = BrushFromRgb(0x1E, 0x2A, 0x3E),
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = ZThickness(14, 6, 14, 6),
                Child = new TextBlock
                {
                    Text = value,
                    FontSize = Z(13),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TextBrush
                }
            };
        }

        private void SetAudioEnabled(bool enabled)
        {
            settings.Audio.Enabled = enabled;
            ApplyAudioDependentState();
            controller.ApplyAudioSettings();
            RefreshStatus();
        }

        private void ApplyAudioDependentState()
        {
            bool enabled = settings.Audio.Enabled;
            for (int i = 0; i < audioDependentCards.Count; i++)
            {
                DependentCard dependent = audioDependentCards[i];
                dependent.Card.Background = enabled ? CardBrush : CardDisabledBrush;
                dependent.Card.Opacity = enabled ? 1.0 : 0.72;

                for (int j = 0; j < dependent.Controls.Count; j++)
                {
                    dependent.Controls[j].IsEnabled = enabled;
                }
            }
        }

        private void ApplyUiScale()
        {
            UiScaleTransform.ScaleX = 1.0;
            UiScaleTransform.ScaleY = 1.0;

            PageTitleText.FontSize = Z(30);
            PageTitleText.LineHeight = Z(36);
            PageSubtitleText.FontSize = Z(14);
            HeaderEnabledSwitch.Width = Z(66);
            HeaderEnabledSwitch.Height = Z(34);
            HeaderEnabledSwitch.Margin = ZThickness(22, 5, 0, 0);
            UpdateContentWidth();
        }

        private void ApplySavedSidebarWidth()
        {
            int width = Clamp(settings.Layout.SidebarWidth, 240, 620);
            SidebarColumn.Width = new GridLength(width);
        }

        private void RenderSelectedModule()
        {
            if (selectedModuleIndex == -1)
            {
                RenderSettingsPage();
            }
            else if (selectedModuleIndex == 0)
            {
                RenderAudioPage();
            }
            else if (selectedModuleIndex == 1)
            {
                RenderDeviceGuardPage();
            }
            else if (selectedModuleIndex == 2)
            {
                RenderKeyboardPage();
            }
            else if (selectedModuleIndex == 3)
            {
                RenderGameHelperPage();
            }
            else if (selectedModuleIndex == 4)
            {
                RenderAppGuardPage();
            }
            else if (selectedModuleIndex == 5)
            {
                RenderDisplayGuardPage();
            }
            else if (selectedModuleIndex == 6)
            {
                RenderPowerGuardPage();
            }
            else if (selectedModuleIndex == 7)
            {
                RenderUacGuardPage();
            }
            else if (selectedModuleIndex == 9)
            {
                RenderVsrGuardPage();
            }
            else
            {
                RenderLinkGuardPage();
            }
        }

        private void UpdateContentWidth()
        {
            if (ContentStack == null)
            {
                return;
            }

            if (ContentFrame != null && ContentScrollViewer != null)
            {
                double viewportWidth = ContentScrollViewer.ActualWidth;
                if (IsFinite(viewportWidth) && viewportWidth > 0)
                {
                    double horizontal = Clamp(viewportWidth * 0.06, 22, 64);
                    double vertical = Clamp(viewportWidth * 0.045, 28, 44);
                    ContentFrame.Padding = new Thickness(horizontal, vertical, horizontal, vertical);
                }
            }

            ContentStack.ClearValue(WidthProperty);
            ContentStack.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentStack.InvalidateMeasure();
            ContentStack.InvalidateArrange();
        }

        private void QueueEnsureWindowOnScreen()
        {
            Dispatcher.BeginInvoke(new Action(EnsureWindowOnScreen), DispatcherPriority.ApplicationIdle);
        }

        private void EnsureWindowOnScreen()
        {
            if (WindowState != WindowState.Normal)
            {
                return;
            }

            Rect virtualBounds = GetCurrentMonitorWorkAreaInDips();
            double screenMargin = Z(12);
            double availableWidth = Math.Max(1, virtualBounds.Width - (screenMargin * 2));
            double availableHeight = Math.Max(1, virtualBounds.Height - (screenMargin * 2));
            double dynamicMinWidth = Math.Min(940, availableWidth);
            double dynamicMinHeight = Math.Min(640, availableHeight);
            MinWidth = dynamicMinWidth;
            MinHeight = dynamicMinHeight;

            double width = ActualWidth > 100 ? ActualWidth : Width;
            double height = ActualHeight > 100 ? ActualHeight : Height;
            if (!IsFinite(width) || width < MinWidth)
            {
                width = MinWidth;
            }
            if (!IsFinite(height) || height < MinHeight)
            {
                height = MinHeight;
            }

            double maxWidth = Math.Max(MinWidth, availableWidth);
            double maxHeight = Math.Max(MinHeight, availableHeight);
            if (width > maxWidth)
            {
                width = maxWidth;
                Width = width;
            }

            if (height > maxHeight)
            {
                height = maxHeight;
                Height = height;
            }

            bool invalidPosition = !IsFinite(Left) || !IsFinite(Top);
            Rect windowBounds = invalidPosition ? Rect.Empty : new Rect(Left, Top, width, height);
            bool offscreen = invalidPosition
                || windowBounds.Right < virtualBounds.Left + 80
                || windowBounds.Left > virtualBounds.Right - 80
                || windowBounds.Bottom < virtualBounds.Top + 80
                || windowBounds.Top > virtualBounds.Bottom - 80;

            if (!offscreen)
            {
                double clampedLeft = Math.Min(Math.Max(Left, virtualBounds.Left + 12), virtualBounds.Right - width - 12);
                double clampedTop = Math.Min(Math.Max(Top, virtualBounds.Top + 12), virtualBounds.Bottom - height - 12);
                if (IsFinite(clampedLeft) && IsFinite(clampedTop)
                    && (Math.Abs(clampedLeft - Left) > 0.5 || Math.Abs(clampedTop - Top) > 0.5))
                {
                    Left = clampedLeft;
                    Top = clampedTop;
                }

                return;
            }

            double safeWidth = Math.Min(width, Math.Max(MinWidth, virtualBounds.Width - (screenMargin * 2)));
            double safeHeight = Math.Min(height, Math.Max(MinHeight, virtualBounds.Height - (screenMargin * 2)));
            Width = safeWidth;
            Height = safeHeight;
            Left = virtualBounds.Left + Math.Max(20, (virtualBounds.Width - safeWidth) / 2);
            Top = virtualBounds.Top + Math.Max(20, (virtualBounds.Height - safeHeight) / 2);
        }

        private Rect GetCurrentMonitorWorkAreaInDips()
        {
            return GetMonitorWorkAreaInDips(this);
        }

        private Rect GetMonitorWorkAreaInDips(Window window)
        {
            Window target = window ?? this;
            IntPtr hwnd = new WindowInteropHelper(target).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            var monitorInfo = new MonitorInfo();
            monitorInfo.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return SystemParameters.WorkArea;
            }

            RectInt workArea = monitorInfo.rcWork;
            return DeviceRectToDips(workArea, target);
        }

        private static Rect GetWindowBoundsInDips(Window window)
        {
            if (window == null)
            {
                return Rect.Empty;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            RectInt rect;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect))
            {
                return DeviceRectToDips(rect, window);
            }

            double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            if (!IsFinite(window.Left) || !IsFinite(window.Top) || !IsFinite(width) || !IsFinite(height)
                || width <= 0 || height <= 0)
            {
                return Rect.Empty;
            }

            return new Rect(window.Left, window.Top, width, height);
        }

        private static Rect DeviceRectToDips(RectInt rect, Visual visual)
        {
            Matrix transform = Matrix.Identity;
            PresentationSource source = visual == null ? null : PresentationSource.FromVisual(visual);
            if (source != null && source.CompositionTarget != null)
            {
                transform = source.CompositionTarget.TransformFromDevice;
            }

            Point topLeft = transform.Transform(new Point(rect.Left, rect.Top));
            Point bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        private double GetDialogScreenMargin(Rect workArea)
        {
            double shortestSide = Math.Min(workArea.Width, workArea.Height);
            if (!IsFinite(shortestSide) || shortestSide <= 0)
            {
                return Z(24);
            }

            return Clamp(Math.Round(shortestSide * 0.035), Z(10), Z(28));
        }

        private void FitDialogToWorkArea(Window dialog, Rect workArea, double margin)
        {
            if (dialog.WindowState != WindowState.Normal)
            {
                return;
            }

            double maxWidth = Math.Max(1, workArea.Width - (margin * 2));
            double maxHeight = Math.Max(1, workArea.Height - (margin * 2));

            dialog.MinWidth = Math.Min(dialog.MinWidth, maxWidth);
            dialog.MinHeight = Math.Min(dialog.MinHeight, maxHeight);
            dialog.MaxWidth = double.PositiveInfinity;
            dialog.MaxHeight = double.PositiveInfinity;

            dialog.Width = Clamp(GetWindowLayoutWidth(dialog), dialog.MinWidth, maxWidth);
            dialog.Height = Clamp(GetWindowLayoutHeight(dialog), dialog.MinHeight, maxHeight);
        }

        private void EnsureDialogWithinWorkArea(Window dialog)
        {
            if (dialog == null || !dialog.IsVisible || dialog.WindowState != WindowState.Normal)
            {
                return;
            }

            Window owner = dialog.Owner ?? this;
            Rect workArea = GetMonitorWorkAreaInDips(owner);
            double margin = GetDialogScreenMargin(workArea);
            FitDialogToWorkArea(dialog, workArea, margin);

            double width = GetWindowLayoutWidth(dialog);
            double height = GetWindowLayoutHeight(dialog);
            if (!IsFinite(dialog.Left) || !IsFinite(dialog.Top))
            {
                return;
            }

            double left = Clamp(dialog.Left, workArea.Left + margin, workArea.Right - width - margin);
            double top = Clamp(dialog.Top, workArea.Top + margin, workArea.Bottom - height - margin);
            if (Math.Abs(left - dialog.Left) > 0.5)
            {
                dialog.Left = Math.Round(left);
            }
            if (Math.Abs(top - dialog.Top) > 0.5)
            {
                dialog.Top = Math.Round(top);
            }
        }

        private static double GetWindowLayoutWidth(Window window)
        {
            double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            if (!IsFinite(width) || width <= 0)
            {
                width = window.MinWidth;
            }

            return width;
        }

        private static double GetWindowLayoutHeight(Window window)
        {
            double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            if (!IsFinite(height) || height <= 0)
            {
                height = window.MinHeight;
            }

            return height;
        }

        private void Module_StatusChanged(object sender, EventArgs e)
        {
            RefreshStatus();
        }

        private void AppGuardModule_AppsChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { AppGuardModule_AppsChanged(sender, e); }));
                return;
            }

            if (selectedModuleIndex == 4)
            {
                RenderAppGuardPage();
            }

            RefreshStatus();
        }

        private void DeviceGuardModule_DevicesChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { DeviceGuardModule_DevicesChanged(sender, e); }));
                return;
            }

            if (selectedModuleIndex == 1)
            {
                RenderDeviceGuardPage();
            }
            RefreshDeviceGuardWindows();
            RefreshStatus();
        }

        private void DisplayModule_DisplaysChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { DisplayModule_DisplaysChanged(sender, e); }));
                return;
            }

            if (selectedModuleIndex == 5)
            {
                List<DisplayGuardMonitorInfo> monitors = controller.GetDisplayGuardMonitors();
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (monitors[i].IsBusy)
                    {
                        RefreshStatus();
                        return;
                    }
                }

                string fingerprint = FocusedRefreshPolicy.GetDisplayFingerprint(monitors);
                if (string.Equals(displayRefreshFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                displayRefreshFingerprint = fingerprint;
                displayListRestoreOffset = displayMonitorPresenter == null ? 0 : displayMonitorPresenter.VerticalOffset;
                RenderDisplayGuardPage();
            }

            RefreshStatus();
        }

        private void SetWindowIcon()
        {
            try
            {
                Icon = ApplicationIconService.LoadImageSource(settings.Appearance.CustomIconPath);
            }
            catch
            {
                // Icon is cosmetic; the tray icon remains available even if conversion fails.
            }
        }

        internal void ApplyApplicationIcon()
        {
            SetWindowIcon();
            appGuardIconCache.Clear();
            unchecked
            {
                appGuardRenderVersion++;
            }
            if (selectedModuleIndex == 4)
            {
                RenderAppGuardPage();
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmGetMinMaxInfo)
            {
                ApplyMaximizedBounds(hwnd, lParam);
                handled = true;
            }

            return IntPtr.Zero;
        }

        private static void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var monitorInfo = new MonitorInfo();
            monitorInfo.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return;
            }

            var minMaxInfo = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));

            RectInt workArea = monitorInfo.rcWork;
            RectInt monitorArea = monitorInfo.rcMonitor;
            int reservedBottomPixels = GetTaskbarRevealStripPixels(workArea, monitorArea);

            minMaxInfo.ptMaxPosition.x = workArea.Left - monitorArea.Left;
            minMaxInfo.ptMaxPosition.y = workArea.Top - monitorArea.Top;
            minMaxInfo.ptMaxSize.x = Math.Max(1, workArea.Right - workArea.Left);
            minMaxInfo.ptMaxSize.y = Math.Max(1, workArea.Bottom - workArea.Top - reservedBottomPixels);

            Marshal.StructureToPtr(minMaxInfo, lParam, true);
        }

        private static int GetTaskbarRevealStripPixels(RectInt workArea, RectInt monitorArea)
        {
            bool workAreaUsesFullHeight = workArea.Top == monitorArea.Top && workArea.Bottom == monitorArea.Bottom;
            bool workAreaUsesFullWidth = workArea.Left == monitorArea.Left && workArea.Right == monitorArea.Right;
            return workAreaUsesFullHeight && workAreaUsesFullWidth ? 3 : 0;
        }

        private static void CollectInteractiveControls(DependencyObject root, List<Control> controls)
        {
            Control control = root as Control;
            if (control is Button || control is TextBox || control is UiControls.ToggleSwitch || control is UiControls.NumberBox)
            {
                controls.Add(control);
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject dependencyObject = child as DependencyObject;
                if (dependencyObject != null)
                {
                    CollectInteractiveControls(dependencyObject, controls);
                }
            }
        }

        private static string NormalizeRetryDelays(string text)
        {
            var values = new List<string>();
            string[] pieces = (text ?? string.Empty).Split(',');
            for (int i = 0; i < pieces.Length; i++)
            {
                int value;
                if (int.TryParse(pieces[i].Trim(), out value) && value >= 0 && value <= 10000)
                {
                    values.Add(value.ToString());
                }
            }

            if (values.Count == 0)
            {
                values.Add("100");
                values.Add("400");
                values.Add("1000");
            }

            return string.Join(",", values.ToArray());
        }

        private double Z(double value)
        {
            return Math.Round(value * settings.Layout.UiScalePercent / 100.0, 1);
        }

        private Thickness ZThickness(double left, double top, double right, double bottom)
        {
            return new Thickness(Z(left), Z(top), Z(right), Z(bottom));
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min)
            {
                max = min;
            }

            return Math.Max(min, Math.Min(max, value));
        }

        private static double Clamp(double value, double min, double max)
        {
            if (max < min)
            {
                max = min;
            }

            if (!IsFinite(value))
            {
                return min;
            }

            return Math.Max(min, Math.Min(max, value));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private static SolidColorBrush BrushFromHex(string colorHex)
        {
            try
            {
                object value = ColorConverter.ConvertFromString(colorHex);
                if (value is Color)
                {
                    return new SolidColorBrush((Color)value);
                }
            }
            catch
            {
            }

            return BrushFromRgb(0x48, 0xFF, 0x78);
        }

        private const int WmGetMinMaxInfo = 0x0024;
        private const int MonitorDefaultToNearest = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RectInt lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes,
            out ShFileInfo fileInfo, uint fileInfoSize, ShGetFileInfoFlags flags);

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(ShellImageListSize imageList, ref Guid riid, out IntPtr ppv);

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig]
            int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);

            [PreserveSig]
            int ReplaceIcon(int i, IntPtr hicon, out int pi);

            [PreserveSig]
            int SetOverlayImage(int iImage, int iOverlay);

            [PreserveSig]
            int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);

            [PreserveSig]
            int AddMasked(IntPtr hbmImage, int crMask, out int pi);

            [PreserveSig]
            int Draw(ref ImageListDrawParams pimldp);

            [PreserveSig]
            int Remove(int i);

            [PreserveSig]
            int GetIcon(int i, ImageListDrawItemFlags flags, out IntPtr picon);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointInt
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public PointInt ptReserved;
            public PointInt ptMaxSize;
            public PointInt ptMaxPosition;
            public PointInt ptMinTrackSize;
            public PointInt ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectInt
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public RectInt rcMonitor;
            public RectInt rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ImageListDrawParams
        {
            public int cbSize;
            public IntPtr himl;
            public int i;
            public IntPtr hdcDst;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public int xBitmap;
            public int yBitmap;
            public int rgbBk;
            public int rgbFg;
            public int fStyle;
            public int dwRop;
            public int fState;
            public int frame;
            public int crEffect;
        }

        [Flags]
        private enum ShGetFileInfoFlags : uint
        {
            SysIconIndex = 0x000004000,
            LargeIcon = 0x000000000
        }

        private enum ShellImageListSize
        {
            Large = 0,
            Small = 1,
            ExtraLarge = 2,
            Jumbo = 4
        }

        [Flags]
        private enum ImageListDrawItemFlags
        {
            Transparent = 0x00000001
        }

        private sealed class DependentCard
        {
            public Border Card;
            public readonly List<Control> Controls = new List<Control>();
        }

        private sealed class FloatingDialogState
        {
            private const int WmSysCommand = 0x0112;
            private const int ScMaximize = 0xF030;

            private readonly Window dialog;
            private HwndSource source;
            private WindowState previousState;
            private Rect normalBounds;
            private bool hasNormalBounds;
            private bool suppressCapture;
            private bool maximizing;
            private bool restoringFromMaximized;

            public FloatingDialogState(Window dialog)
            {
                this.dialog = dialog;
                previousState = dialog.WindowState;
            }

            public void CaptureNormalBounds()
            {
                CaptureNormalBounds(false);
            }

            public void AttachWindowHook()
            {
                if (source != null)
                {
                    return;
                }

                source = HwndSource.FromHwnd(new WindowInteropHelper(dialog).Handle);
                if (source != null)
                {
                    source.AddHook(WindowProc);
                    dialog.Closed += delegate
                    {
                        source.RemoveHook(WindowProc);
                        source = null;
                    };
                }
            }

            private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (msg == WmSysCommand)
                {
                    int command = wParam.ToInt32() & 0xFFF0;
                    if (command == ScMaximize)
                    {
                        CaptureNormalBounds(true);
                        maximizing = true;
                    }
                }

                return IntPtr.Zero;
            }

            private void CaptureNormalBounds(bool force)
            {
                if (!force && (suppressCapture || maximizing || restoringFromMaximized
                    || dialog.WindowState != WindowState.Normal
                    || previousState == WindowState.Maximized))
                {
                    return;
                }

                Rect bounds = GetWindowBoundsInDips(dialog);
                if (bounds.IsEmpty
                    || !IsFinite(bounds.Left) || !IsFinite(bounds.Top)
                    || !IsFinite(bounds.Width) || !IsFinite(bounds.Height)
                    || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return;
                }

                normalBounds = bounds;
                hasNormalBounds = true;
            }

            public void HandleStateChanged()
            {
                WindowState currentState = dialog.WindowState;
                if (currentState == WindowState.Maximized && !hasNormalBounds)
                {
                    CaptureRestoreBounds();
                }

                if (currentState == WindowState.Maximized)
                {
                    maximizing = false;
                }

                if (currentState == WindowState.Normal && previousState == WindowState.Maximized)
                {
                    RestoreNormalBoundsAfterMaximize();
                }
                else if (currentState == WindowState.Normal)
                {
                    CaptureNormalBounds();
                }

                previousState = currentState;
            }

            private void CaptureRestoreBounds()
            {
                Rect bounds = dialog.RestoreBounds;
                if (bounds.IsEmpty
                    || !IsFinite(bounds.Left) || !IsFinite(bounds.Top)
                    || !IsFinite(bounds.Width) || !IsFinite(bounds.Height)
                    || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return;
                }

                normalBounds = bounds;
                hasNormalBounds = true;
            }

            private void RestoreNormalBoundsAfterMaximize()
            {
                if (!hasNormalBounds)
                {
                    return;
                }

                restoringFromMaximized = true;
                dialog.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (dialog.WindowState != WindowState.Normal)
                    {
                        restoringFromMaximized = false;
                        return;
                    }

                    suppressCapture = true;
                    try
                    {
                        dialog.Left = Math.Round(normalBounds.Left);
                        dialog.Top = Math.Round(normalBounds.Top);
                        dialog.Width = Math.Max(dialog.MinWidth, normalBounds.Width);
                        dialog.Height = Math.Max(dialog.MinHeight, normalBounds.Height);
                    }
                    finally
                    {
                        suppressCapture = false;
                        restoringFromMaximized = false;
                    }

                    CaptureNormalBounds();
                }), DispatcherPriority.ApplicationIdle);
            }
        }

        private sealed class AppGuardCardView
        {
            public AppCatalogItem App;
            public Border Card;
            public StackPanel Content;
            public Button Toggle;
            public Border DetailHost;
            public bool DetailLoadPending;
            public bool DetailRefreshStarted;
            public bool DetailUiInitialized;
            public StackPanel DetailErrorPanel;
            public TextBlock DetailErrorText;
            public TextBlock StatusValue;
            public TextBlock PathValue;
            public TextBlock VersionValue;
            public TextBlock PublisherValue;
            public TextBlock InstallLocationValue;
            public TextBlock SourceValue;
            public TextBlock ModifiedValue;
            public TextBlock SizeValue;
            public Button TerminateButton;
        }

        private sealed class CrosshairStyleChoice
        {
            public string Name { get; set; }
            public string Glyph { get; set; }
        }

        private sealed class CrosshairPreviewVisual : FrameworkElement
        {
            private CrosshairOptions options = new CrosshairOptions
            {
                Size = 14,
                OpacityPercent = 100,
                ColorHex = "#48FF78",
                Style = "Classic"
            };

            public CrosshairPreviewVisual()
            {
                SnapsToDevicePixels = true;
                IsHitTestVisible = false;
            }

            public void ApplyOptions(CrosshairOptions newOptions)
            {
                if (newOptions != null)
                {
                    options = newOptions;
                }

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
                double length = Math.Max(2, Math.Min(48, options.Size));
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
                var pen = new Pen(new SolidColorBrush(color), thickness)
                {
                    StartLineCap = PenLineCap.Square,
                    EndLineCap = PenLineCap.Square
                };
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
                drawingContext.DrawLine(pen, new Point(centerX - gap - length, centerY), new Point(centerX - gap, centerY));
                drawingContext.DrawLine(pen, new Point(centerX + gap, centerY), new Point(centerX + gap + length, centerY));
                drawingContext.DrawLine(pen, new Point(centerX, centerY - gap - length), new Point(centerX, centerY - gap));
                drawingContext.DrawLine(pen, new Point(centerX, centerY + gap), new Point(centerX, centerY + gap + length));
            }

            private static void DrawSolidCross(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                drawingContext.DrawLine(pen, new Point(centerX - length, centerY), new Point(centerX + length, centerY));
                drawingContext.DrawLine(pen, new Point(centerX, centerY - length), new Point(centerX, centerY + length));
            }

            private static void DrawTShape(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double gap, double length, bool inverted)
            {
                drawingContext.DrawLine(pen, new Point(centerX - gap - length, centerY), new Point(centerX - gap, centerY));
                drawingContext.DrawLine(pen, new Point(centerX + gap, centerY), new Point(centerX + gap + length, centerY));
                drawingContext.DrawLine(pen,
                    inverted ? new Point(centerX, centerY - gap - length) : new Point(centerX, centerY + gap),
                    inverted ? new Point(centerX, centerY - gap) : new Point(centerX, centerY + gap + length));
            }

            private static void DrawChevron(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                double width = length * 0.55;
                double height = length * 0.6;
                drawingContext.DrawLine(pen, new Point(centerX - width, centerY + height * 0.5), new Point(centerX, centerY - height * 0.5));
                drawingContext.DrawLine(pen, new Point(centerX, centerY - height * 0.5), new Point(centerX + width, centerY + height * 0.5));
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
                drawingContext.DrawRectangle(null, pen, new Rect(centerX - radius, centerY - radius, radius * 2, radius * 2));
            }

            private static void DrawX(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double length)
            {
                double diagonal = length * 0.7;
                drawingContext.DrawLine(pen, new Point(centerX - diagonal, centerY - diagonal), new Point(centerX + diagonal, centerY + diagonal));
                drawingContext.DrawLine(pen, new Point(centerX + diagonal, centerY - diagonal), new Point(centerX - diagonal, centerY + diagonal));
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
                drawingContext.DrawLine(pen, new Point(centerX, centerY - gap - length), new Point(centerX, centerY - gap));
                drawingContext.DrawLine(pen, new Point(centerX, centerY + gap), new Point(centerX, centerY + gap + length * 1.3));
            }

            private static void DrawHorizontalBars(DrawingContext drawingContext, Pen pen,
                double centerX, double centerY, double gap, double length)
            {
                drawingContext.DrawLine(pen, new Point(centerX - gap - length, centerY), new Point(centerX - gap, centerY));
                drawingContext.DrawLine(pen, new Point(centerX + gap, centerY), new Point(centerX + gap + length, centerY));
            }
        }

        private sealed class WindowHandleOwner : Forms.IWin32Window
        {
            private readonly IntPtr handle;

            public WindowHandleOwner(IntPtr handle)
            {
                this.handle = handle;
            }

            public IntPtr Handle
            {
                get { return handle; }
            }
        }
    }
}
