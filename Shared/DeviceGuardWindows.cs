using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UiControls = Wpf.Ui.Controls;

namespace GuardCenter
{
    public partial class MainWindow
    {
        private Window detectableDevicesWindow;
        private Window coreHardwareWindow;
        private Window inputStackWindow;
        private ProgressiveListPresenter<DeviceGuardDevice> detectableDevicesPresenter;
        private ProgressiveListPresenter<CoreHardwareItem> coreHardwarePresenter;
        private bool inputStackCompatibilityActive;
        private bool inputStackCompatibilityCompleted;
        private bool inputStackCompatibilityFailed;
        private bool inputStackCompatibilityRestoring;
        private DateTime inputStackCompatibilityCompletedAt;
        private string inputStackCompatibilityResult = string.Empty;

        private UIElement CreateDeviceGuardLandingActions()
        {
            var buttons = new List<Button>();
            Button refresh = CreateActionButton("重新整理", true, delegate { controller.RefreshDeviceGuard(); });
            refresh.IsEnabled = !controller.IsDeviceGuardScanning() && !controller.IsDeviceGuardRepairing();
            buttons.Add(refresh);
            if (controller.IsDeviceGuardRepairing())
                buttons.Add(CreateActionButton("取消後續步驟", false, delegate { controller.CancelDeviceGuardRepair(); }));
            buttons.Add(CreateActionButton("開啟記錄", false, delegate { controller.OpenLog(); }));
            return CreateResponsiveButtonGrid(buttons);
        }

        private void ShowDetectableDevicesWindow()
        {
            if (detectableDevicesWindow != null)
            {
                detectableDevicesWindow.Activate();
                return;
            }
            detectableDevicesWindow = CreateGameHelperDialog("目前可偵測裝置", Z(820), Z(650));
            detectableDevicesWindow.Closed += delegate
            {
                if (detectableDevicesPresenter != null) detectableDevicesPresenter.Dispose();
                detectableDevicesPresenter = null;
                detectableDevicesWindow = null;
            };
            RenderDetectableDevicesWindow(0);
            detectableDevicesWindow.Show();
        }

        private void ShowCoreHardwareWindow()
        {
            if (coreHardwareWindow != null)
            {
                coreHardwareWindow.Activate();
                return;
            }
            coreHardwareWindow = CreateGameHelperDialog("電腦核心硬體", Z(900), Z(700));
            coreHardwareWindow.Closed += delegate
            {
                if (coreHardwarePresenter != null) coreHardwarePresenter.Dispose();
                coreHardwarePresenter = null;
                coreHardwareWindow = null;
            };
            RenderCoreHardwareWindow(0);
            coreHardwareWindow.Show();
        }

        private void ShowInputStackWindow()
        {
            if (inputStackWindow != null)
            {
                inputStackWindow.Activate();
                return;
            }
            inputStackWindow = CreateGameHelperDialog("輸入裝置堆疊", Z(860), Z(690));
            inputStackWindow.Closed += delegate
            {
                ResetInputStackOperationState();
                inputStackWindow = null;
            };
            RenderInputStackWindow();
            inputStackWindow.Show();
            if (!controller.IsDeviceGuardScanning() && !controller.IsDeviceGuardRepairing())
                controller.RefreshDeviceGuard();
        }

        private void RefreshDeviceGuardWindows()
        {
            if (detectableDevicesWindow != null)
            {
                double offset = detectableDevicesPresenter == null ? 0 : detectableDevicesPresenter.VerticalOffset;
                RenderDetectableDevicesWindow(offset);
            }
            if (coreHardwareWindow != null)
            {
                double offset = coreHardwarePresenter == null ? 0 : coreHardwarePresenter.VerticalOffset;
                RenderCoreHardwareWindow(offset);
            }
            if (inputStackWindow != null) RenderInputStackWindow();
        }

        private void CloseDeviceGuardWindows()
        {
            if (detectableDevicesWindow != null) detectableDevicesWindow.Close();
            if (coreHardwareWindow != null) coreHardwareWindow.Close();
            if (inputStackWindow != null) inputStackWindow.Close();
        }

        private void RenderInputStackWindow()
        {
            if (inputStackWindow == null) return;
            InputStackSnapshot snapshot = controller.GetInputStackSnapshot();
            bool operating = controller.IsDeviceGuardScanning() || inputStackCompatibilityActive;
            var root = CreateDeviceDialogRoot();
            root.Children.Add(CreateDeviceDialogHeader("輸入裝置堆疊",
                "鍵盤退出 Interception；HuaJuan 僅保留滑鼠輸出並依硬體身分固定目標。Windows 裝置編號不回收、不重排。"));

            var controls = new WrapPanel { Margin = ZThickness(0, 12, 0, 12) };
            bool applied = snapshot.HuaJuanCompatibilityState == HuaJuanCompatibilityState.Applied
                || snapshot.HuaJuanCompatibilityState
                    == HuaJuanCompatibilityState.AppliedPendingReboot;
            Button apply = CreateActionButton(
                inputStackCompatibilityActive && !inputStackCompatibilityRestoring
                    ? "隔離中…" : applied ? "鍵盤已隔離" : "隔離鍵盤 Interception", true,
                delegate { BeginHuaJuanCompatibilityOperation(false); });
            apply.IsEnabled = !operating && !controller.IsDeviceGuardRepairing()
                && snapshot.HuaJuanInstalled && snapshot.HuaJuanPayloadAvailable && !applied;
            controls.Children.Add(apply);
            Grid.SetRow(controls, 1);
            root.Children.Add(controls);

            var details = new StackPanel();
            details.Children.Add(CreateInputStackStatusCard(snapshot));
            AddInputStackDetailRows(details, BuildInputStackOverview(snapshot));
            var advanced = new StackPanel { Margin = ZThickness(0, 10, 0, 0) };
            AddInputStackDetailRows(advanced, BuildInputStackDetails(snapshot));
            var advancedActions = new WrapPanel { Margin = ZThickness(0, 6, 0, 0) };
            Button restore = CreateActionButton(
                inputStackCompatibilityActive && inputStackCompatibilityRestoring
                    ? "還原中…" : "還原原版", false,
                delegate { BeginHuaJuanCompatibilityOperation(true); });
            restore.IsEnabled = !operating && !controller.IsDeviceGuardRepairing()
                && snapshot.HuaJuanBackupAvailable;
            advancedActions.Children.Add(restore);
            advancedActions.Children.Add(CreateActionButton("開啟記錄", false,
                delegate { controller.OpenLog(); }));
            advanced.Children.Add(advancedActions);
            details.Children.Add(new Expander
            {
                Header = "進階診斷資訊",
                Foreground = TextBrush,
                FontSize = Z(12.5),
                Content = advanced,
                IsExpanded = false,
                Margin = ZThickness(0, 6, 0, 0)
            });

            var scroll = new ScrollViewer
            {
                Content = details,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);
            var captured = new TextBlock
            {
                Text = snapshot.CapturedAt == default
                    ? "尚未完成輸入堆疊掃描。"
                    : "擷取時間：" + snapshot.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                Foreground = MutedBrush,
                FontSize = Z(12)
            };
            root.Children.Add(CreateDeviceDialogFooter(captured));
            inputStackWindow.Content = root;
        }

        private async void BeginHuaJuanCompatibilityOperation(bool restore)
        {
            if (inputStackCompatibilityActive || controller.IsDeviceGuardScanning()
                || controller.IsDeviceGuardRepairing())
                return;

            inputStackCompatibilityActive = true;
            inputStackCompatibilityCompleted = false;
            inputStackCompatibilityFailed = false;
            inputStackCompatibilityRestoring = restore;
            inputStackCompatibilityCompletedAt = default;
            inputStackCompatibilityResult = string.Empty;
            RenderInputStackWindow();

            HuaJuanCompatibilityResult result;
            try
            {
                result = restore
                    ? await controller.RestoreHuaJuanCompatibilityAsync()
                    : await controller.ApplyHuaJuanCompatibilityAsync();
            }
            catch (Exception ex)
            {
                result = new HuaJuanCompatibilityResult
                {
                    Message = "輸入堆疊操作失敗：" + ex.Message
                };
            }

            inputStackCompatibilityActive = false;
            inputStackCompatibilityCompleted = true;
            inputStackCompatibilityFailed = !result.Succeeded;
            inputStackCompatibilityCompletedAt = DateTime.Now;
            inputStackCompatibilityResult = string.IsNullOrWhiteSpace(result.Message)
                ? result.Succeeded ? "操作完成。" : "操作失敗。"
                : result.Message;
            controller.RefreshDeviceGuard();
            RenderInputStackWindow();
        }

        private void ResetInputStackOperationState()
        {
            inputStackCompatibilityActive = false;
            inputStackCompatibilityCompleted = false;
            inputStackCompatibilityFailed = false;
            inputStackCompatibilityRestoring = false;
            inputStackCompatibilityCompletedAt = default;
            inputStackCompatibilityResult = string.Empty;
        }

        private UIElement CreateInputStackStatusCard(InputStackSnapshot snapshot)
        {
            DeviceHealthPresentationState state =
                snapshot.HuaJuanCompatibilityState == HuaJuanCompatibilityState.Applied
                    || snapshot.HuaJuanCompatibilityState
                        == HuaJuanCompatibilityState.AppliedPendingReboot
                    ? DeviceHealthPresentationState.Healthy
                    : snapshot.HuaJuanCompatibilityState == HuaJuanCompatibilityState.Partial
                        ? DeviceHealthPresentationState.DeviceError
                        : snapshot.HuaJuanCompatibilityState == HuaJuanCompatibilityState.Original
                            ? DeviceHealthPresentationState.RebootRequired
                            : DeviceHealthPresentationState.Unknown;
            var panel = new StackPanel();
            if (inputStackCompatibilityActive)
            {
                panel.Children.Add(CreateInputStackOperationBadge(
                    inputStackCompatibilityRestoring ? "還原中" : "隔離鍵盤中",
                    UiControls.SymbolRegular.ArrowSync20,
                    BrushFromRgb(0x71, 0xA7, 0xFF),
                    BrushFromArgb(0x28, 0x3E, 0x7D, 0xE0)));
                panel.Children.Add(new TextBlock
                {
                    Text = inputStackCompatibilityRestoring
                        ? "目前操作：正在還原原版 HuaJuan DLL 與 Keyboard UpperFilters。"
                        : "目前操作：正在備份原始狀態、部署輸出專用硬體身分 shim，並隔離鍵盤 Interception。",
                    Foreground = MutedBrush,
                    FontSize = Z(12.5),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = ZThickness(0, 4, 0, 10)
                });
            }
            else if (inputStackCompatibilityCompleted)
            {
                Brush foreground = inputStackCompatibilityFailed
                    ? BrushFromRgb(0xFF, 0x72, 0x72)
                    : BrushFromRgb(0x58, 0xD6, 0x8D);
                Brush background = inputStackCompatibilityFailed
                    ? BrushFromArgb(0x28, 0xD9, 0x42, 0x42)
                    : BrushFromArgb(0x25, 0x35, 0xC4, 0x78);
                panel.Children.Add(CreateInputStackOperationBadge(
                    inputStackCompatibilityFailed ? "操作失敗" : "操作完成",
                    inputStackCompatibilityFailed
                        ? UiControls.SymbolRegular.ErrorCircle20
                        : UiControls.SymbolRegular.CheckmarkCircle20,
                    foreground, background));
                panel.Children.Add(new TextBlock
                {
                    Text = "最近操作：" + inputStackCompatibilityResult + " "
                        + inputStackCompatibilityCompletedAt.ToString("HH:mm:ss"),
                    Foreground = inputStackCompatibilityFailed ? foreground : MutedBrush,
                    FontSize = Z(12.5),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = ZThickness(0, 4, 0, 10)
                });
            }
            panel.Children.Add(CreateDeviceStatusBadge(state));
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(snapshot.Summary)
                    ? "等待輸入堆疊掃描結果。" : snapshot.Summary,
                Foreground = TextBrush,
                FontSize = Z(13.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, 4, 0, 0)
            });
            return new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = ZThickness(16, 14, 16, 14),
                Margin = ZThickness(0, 0, 0, 16),
                Child = panel
            };
        }

        private UIElement CreateInputStackOperationBadge(string text,
            UiControls.SymbolRegular symbol, Brush foreground, Brush background)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new UiControls.SymbolIcon
            {
                Symbol = symbol,
                FontSize = Z(15),
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(0, 0, 5, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = Z(11.5),
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            });
            return new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(Z(10)),
                Padding = ZThickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = row
            };
        }

        private void AddInputStackDetailRows(StackPanel panel,
            List<KeyValuePair<string, string>> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(rows[i].Value)) continue;
                var row = new Grid { Margin = ZThickness(0, 0, 0, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(Z(185)) });
                row.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock
                {
                    Text = rows[i].Key,
                    Foreground = MutedBrush,
                    FontSize = Z(12.5),
                    Margin = ZThickness(0, 2, 16, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                var value = new TextBlock
                {
                    Text = rows[i].Value,
                    Foreground = TextBrush,
                    FontSize = Z(12.5),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                panel.Children.Add(row);
            }
        }

        private static List<KeyValuePair<string, string>> BuildInputStackOverview(
            InputStackSnapshot snapshot)
        {
            string compatibilityState =
                snapshot.HuaJuanCompatibilityState == HuaJuanCompatibilityState.NotInstalled
                    ? "未安裝 HuaJuan"
                    : snapshot.HuaJuanCompatibilityState
                        == HuaJuanCompatibilityState.AppliedPendingReboot
                        ? "已套用，等待重開機"
                        : snapshot.HuaJuanCompatibilityState == HuaJuanCompatibilityState.Applied
                            ? "已套用"
                            : snapshot.HuaJuanCompatibilityState
                                == HuaJuanCompatibilityState.Partial
                                ? "狀態不完整"
                                : snapshot.HuaJuanCompatibilityState
                                    == HuaJuanCompatibilityState.Original
                                    ? "原版 Interception"
                                    : "無法確認";
            return new List<KeyValuePair<string, string>>
            {
                Detail("鍵盤 Interception", snapshot.KeyboardInterceptionInstalled
                    ? "仍在作用中" : "已隔離，使用 Windows kbdclass"),
                Detail("鍵盤隔離", compatibilityState),
                Detail("狀態說明", snapshot.HuaJuanCompatibilitySummary),
                Detail("滑鼠 Interception", snapshot.HuaJuanMouseInterceptionRetained
                    ? "保留（輸出專用）" : "未偵測到"),
                Detail("硬體身分 shim", snapshot.HuaJuanInterceptionDllCount <= 0
                    ? string.Empty : snapshot.HuaJuanCompatibleDllCount + " / "
                        + snapshot.HuaJuanInterceptionDllCount),
                Detail("可還原備份", snapshot.HuaJuanBackupAvailable ? "有" : "無")
            };
        }

        private static List<KeyValuePair<string, string>> BuildInputStackDetails(
            InputStackSnapshot snapshot)
        {
            return new List<KeyValuePair<string, string>>
            {
                Detail("Keyboard Filter", snapshot.KeyboardInterceptionInstalled
                    ? "Interception keyboard 已載入" : "未載入"),
                Detail("Mouse Filter", snapshot.MouseInterceptionInstalled
                    ? "Interception mouse 已載入" : "未載入"),
                Detail("Keyboard UpperFilters", JoinDetails(snapshot.KeyboardUpperFilters)),
                Detail("Mouse UpperFilters", JoinDetails(snapshot.MouseUpperFilters)),
                Detail("Keyboard class objects", JoinDetails(snapshot.KeyboardClassDevices)),
                Detail("Pointer class objects", JoinDetails(snapshot.PointerClassDevices)),
                Detail("最高 KeyboardClass 編號", snapshot.HighestKeyboardClassIndex.ToString()),
                Detail("最高 PointerClass 編號", snapshot.HighestPointerClassIndex.ToString()),
                Detail("編號處理", "僅供觀察；不回收、不重排，也不阻擋 Windows 裝置操作"),
                Detail("本次開機 HID surprise removal", snapshot.HidSurpriseRemovalCount.ToString()),
                Detail("其中目前可辨識輸入裝置", snapshot.InputSurpriseRemovalCount.ToString()),
                Detail("keyboard.sys", DriverDescription(snapshot.KeyboardDriverExists,
                    snapshot.KeyboardDriverProduct, snapshot.KeyboardDriverVersion,
                    snapshot.KeyboardDriverSignatureValid)),
                Detail("mouse.sys", DriverDescription(snapshot.MouseDriverExists,
                    snapshot.MouseDriverProduct, snapshot.MouseDriverVersion,
                    snapshot.MouseDriverSignatureValid)),
                Detail("本次 Windows 開機時間", snapshot.BootTime == default
                    ? string.Empty : snapshot.BootTime.ToString("yyyy-MM-dd HH:mm:ss")),
                Detail("診斷限制", snapshot.DiagnosticError)
            };
        }

        private static string DriverDescription(bool exists, string product, string version,
            bool signatureValid)
        {
            if (!exists) return "檔案不存在";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(product)) parts.Add(product);
            if (!string.IsNullOrWhiteSpace(version)) parts.Add(version);
            parts.Add(signatureValid ? "簽章有效" : "簽章未通過驗證");
            return string.Join(" · ", parts.ToArray());
        }

        private void RenderDetectableDevicesWindow(double restoreOffset)
        {
            if (detectableDevicesWindow == null) return;
            if (detectableDevicesPresenter != null) detectableDevicesPresenter.Dispose();

            var root = CreateDeviceDialogRoot();
            root.Children.Add(CreateDeviceDialogHeader("目前可偵測裝置",
                "Windows 目前仍存在的個別 PnP 裝置。這裡的修復會重新啟動指定節點；例如 USB Audio 終端裝置會出現在此處。"));

            var controls = new WrapPanel { Margin = ZThickness(0, 12, 0, 12) };
            Button repairAll = CreateActionButton("全部修復", true, delegate { controller.RepairAllDevices(); });
            repairAll.IsEnabled = !controller.IsDeviceGuardRepairing() && !controller.IsDeviceGuardScanning()
                && controller.GetDeviceGuardDevices().Exists(delegate(DeviceGuardDevice d)
                { return d.CanRepair && d.IncludeInRepairAll; });
            controls.Children.Add(repairAll);
            AddCommonDeviceControls(controls, detectableDevicesListState,
                delegate { RenderDetectableDevicesWindow(0); });
            Grid.SetRow(controls, 1);
            root.Children.Add(controls);

            List<DeviceGuardDevice> devices = controller.GetDeviceGuardDevices();
            var listHost = new Grid();
            var count = new TextBlock { Foreground = MutedBrush, FontSize = Z(12), VerticalAlignment = VerticalAlignment.Center };
            detectableDevicesPresenter = new ProgressiveListPresenter<DeviceGuardDevice>(detectableDevicesListState,
                CreateDetectableDeviceCard, delegate(int visible, int total)
                {
                    count.Text = controller.IsDeviceGuardScanning() ? "正在掃描硬體…"
                        : total == 0 ? controller.GetDeviceGuardStatus()
                        : "已顯示 " + visible + " / " + total + " 個裝置 — " + controller.GetDeviceGuardStatus();
                });
            detectableDevicesPresenter.Reset(devices, null, delegate(DeviceGuardDevice a, DeviceGuardDevice b)
            { return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase); }, true);
            listHost.Children.Add(detectableDevicesPresenter.Element);
            Grid.SetRow(listHost, 2);
            root.Children.Add(listHost);
            root.Children.Add(CreateDeviceDialogFooter(count));
            detectableDevicesWindow.Content = root;
            detectableDevicesPresenter.RestoreVerticalOffset(restoreOffset);
        }

        private void RenderCoreHardwareWindow(double restoreOffset)
        {
            if (coreHardwareWindow == null) return;
            if (coreHardwarePresenter != null) coreHardwarePresenter.Dispose();

            var root = CreateDeviceDialogRoot();
            root.Children.Add(CreateDeviceDialogHeader("電腦核心硬體",
                "以 Bluetooth、Graphics、Network、Audio、Camera、USB 硬體能力為單位。Repair All 不會重新啟動健康項目，也不會對高風險硬體執行 restart。"));

            var controls = new WrapPanel { Margin = ZThickness(0, 12, 0, 12) };
            Button repairAll = CreateActionButton("Core Repair All", true,
                delegate { controller.RepairAllCoreHardware(); });
            repairAll.IsEnabled = !controller.IsDeviceGuardRepairing() && !controller.IsDeviceGuardScanning()
                && controller.GetCoreHardwareItems().Count > 0;
            controls.Children.Add(repairAll);
            AddCommonDeviceControls(controls, coreHardwareListState,
                delegate { RenderCoreHardwareWindow(0); });
            Grid.SetRow(controls, 1);
            root.Children.Add(controls);

            List<CoreHardwareItem> items = controller.GetCoreHardwareItems();
            var listHost = new Grid();
            var count = new TextBlock { Foreground = MutedBrush, FontSize = Z(12), VerticalAlignment = VerticalAlignment.Center };
            coreHardwarePresenter = new ProgressiveListPresenter<CoreHardwareItem>(coreHardwareListState,
                CreateCoreHardwareCard, delegate(int visible, int total)
                {
                    count.Text = controller.IsDeviceGuardScanning() ? "正在分析共享硬體快照…"
                        : total == 0 ? controller.GetDeviceGuardStatus()
                        : "已顯示 " + visible + " / " + total + " 個核心硬體 — " + controller.GetDeviceGuardStatus();
                });
            coreHardwarePresenter.Reset(items, null, delegate(CoreHardwareItem a, CoreHardwareItem b)
            {
                int value = a.Capability.CompareTo(b.Capability);
                return value != 0 ? value : string.Compare(a.DisplayName, b.DisplayName,
                    StringComparison.CurrentCultureIgnoreCase);
            }, true);
            listHost.Children.Add(coreHardwarePresenter.Element);
            Grid.SetRow(listHost, 2);
            root.Children.Add(listHost);
            root.Children.Add(CreateDeviceDialogFooter(count));
            coreHardwareWindow.Content = root;
            coreHardwarePresenter.RestoreVerticalOffset(restoreOffset);
        }

        private Grid CreateDeviceDialogRoot()
        {
            var root = new Grid { Margin = ZThickness(24, 20, 24, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            return root;
        }

        private UIElement CreateDeviceDialogHeader(string title, string description)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title, FontSize = Z(24), FontWeight = FontWeights.SemiBold, Foreground = TextBrush
            });
            panel.Children.Add(new TextBlock
            {
                Text = description, FontSize = Z(13), Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap, Margin = ZThickness(0, 6, 0, 0)
            });
            Grid.SetRow(panel, 0);
            return panel;
        }

        private void AddCommonDeviceControls<T>(WrapPanel controls, ProgressiveListState<T> state,
            Action rerender)
        {
            Button refresh = CreateActionButton("重新整理", false, delegate { controller.RefreshDeviceGuard(); });
            refresh.IsEnabled = !controller.IsDeviceGuardScanning() && !controller.IsDeviceGuardRepairing();
            controls.Children.Add(refresh);
            if (controller.IsDeviceGuardRepairing())
                controls.Children.Add(CreateActionButton("取消後續步驟", false,
                    delegate { controller.CancelDeviceGuardRepair(); }));
            controls.Children.Add(CreateBatchSizeCombo(state, rerender));
            controls.Children.Add(CreateActionButton("開啟記錄", false, delegate { controller.OpenLog(); }));
        }

        private UIElement CreateDeviceDialogFooter(TextBlock count)
        {
            var footer = new Grid { Margin = ZThickness(0, 12, 0, 0) };
            footer.Children.Add(count);
            Grid.SetRow(footer, 3);
            return footer;
        }

        private UIElement CreateDetectableDeviceCard(DeviceGuardDevice device)
        {
            Button repair = CreateActionButton(device.RepairState == DeviceRepairState.Repairing
                ? "修復中…"
                : DeviceGuardPresentation.Classify(device) == DeviceHealthPresentationState.Healthy
                    ? "重新初始化" : "一鍵修復",
                true, delegate { controller.RepairDevice(device.RuntimeId); });
            repair.IsEnabled = device.CanRepair && !controller.IsDeviceGuardRepairing()
                && !controller.IsDeviceGuardScanning();
            Button information = CreateDeviceInformationButton(delegate { ShowDetectableDeviceDetails(device); });
            return CreateDeviceStatusCard(DeviceTypeSymbol(device.Kind), device.DisplayName,
                DeviceGuardPresentation.Classify(device), BuildDeviceSummary(device),
                device.RepairState, device.ResultMessage, repair, information);
        }

        private UIElement CreateCoreHardwareCard(CoreHardwareItem item)
        {
            Button repair = CreateActionButton(item.RepairState == DeviceRepairState.Repairing
                ? "修復中…"
                : DeviceGuardPresentation.Classify(item) == DeviceHealthPresentationState.Healthy
                    ? "重新初始化" : "一鍵修復",
                true, delegate { BeginCoreHardwareRepair(item); });
            repair.IsEnabled = !controller.IsDeviceGuardRepairing() && !controller.IsDeviceGuardScanning()
                && !item.IdentityAmbiguous;
            Button information = CreateDeviceInformationButton(delegate { ShowCoreHardwareDetails(item); });
            return CreateDeviceStatusCard(CoreCapabilitySymbol(item.Capability),
                CoreCapabilityLabel(item.Capability) + " — " + item.DisplayName,
                DeviceGuardPresentation.Classify(item), BuildCoreHardwareSummary(item),
                item.RepairState, item.ResultMessage, repair, information);
        }

        private UIElement CreateDeviceStatusCard(UiControls.SymbolRegular typeSymbol, string title,
            DeviceHealthPresentationState health, string summary, DeviceRepairState repairState,
            string resultMessage, Button repairButton, Button informationButton)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                MinHeight = Z(92),
                Padding = ZThickness(18, 14, 18, 14),
                Margin = ZThickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = Z(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var typeIconHost = new Border
            {
                Width = Z(42), Height = Z(42), CornerRadius = new CornerRadius(Z(9)),
                Background = BrushFromArgb(0x28, 0x4C, 0x8D, 0xFF),
                Margin = ZThickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new UiControls.SymbolIcon
                {
                    Symbol = typeSymbol, FontSize = Z(23), Foreground = AccentBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(typeIconHost, 0);
            grid.Children.Add(typeIconHost);

            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = title, FontSize = Z(15), FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush, TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, 0, 0, 6)
            });
            if (!string.IsNullOrWhiteSpace(summary))
            {
                content.Children.Add(new TextBlock
                {
                    Text = summary, FontSize = Z(12.5), Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap, Margin = ZThickness(0, 3, 0, 0)
                });
            }
            if (DeviceGuardPresentation.HasOperation(repairState, resultMessage))
            {
                string prefix = repairState == DeviceRepairState.Repairing ? "目前操作：" : "最近操作：";
                string operation = string.IsNullOrWhiteSpace(resultMessage)
                    ? RepairResultLabel(repairState) : resultMessage;
                content.Children.Add(new TextBlock
                {
                    Text = prefix + operation, FontSize = Z(12.5),
                    Foreground = repairState == DeviceRepairState.Failed ? BrushFromRgb(0xFF, 0x7A, 0x7A) : MutedBrush,
                    TextWrapping = TextWrapping.Wrap, Margin = ZThickness(0, 7, 0, 0)
                });
            }
            UIElement statusBadge = CreateDeviceStatusBadge(health);
            statusBadge.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
            statusBadge.SetValue(MarginProperty, ZThickness(0, 7, 0, 0));
            content.Children.Add(statusBadge);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = ZThickness(20, 0, 0, 0)
            };
            informationButton.Margin = ZThickness(0, 0, 8, 0);
            repairButton.Margin = new Thickness(0);
            actions.Children.Add(informationButton);
            actions.Children.Add(repairButton);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
            AttachAdaptiveTwoColumnLayout(card, grid, content, actions, Z(650));

            card.Child = grid;
            return card;
        }

        private UIElement CreateDeviceStatusBadge(DeviceHealthPresentationState health)
        {
            Brush foreground;
            Brush background;
            UiControls.SymbolRegular symbol;
            if (health == DeviceHealthPresentationState.Healthy)
            {
                foreground = BrushFromRgb(0x58, 0xD6, 0x8D);
                background = BrushFromArgb(0x25, 0x35, 0xC4, 0x78);
                symbol = UiControls.SymbolRegular.CheckmarkCircle20;
            }
            else if (health == DeviceHealthPresentationState.Disabled
                || health == DeviceHealthPresentationState.Stopped
                || health == DeviceHealthPresentationState.RebootRequired)
            {
                foreground = BrushFromRgb(0xFF, 0xC8, 0x57);
                background = BrushFromArgb(0x28, 0xD4, 0x94, 0x22);
                symbol = health == DeviceHealthPresentationState.Stopped
                    ? UiControls.SymbolRegular.PauseCircle20 : UiControls.SymbolRegular.Warning20;
            }
            else if (health == DeviceHealthPresentationState.Missing
                || health == DeviceHealthPresentationState.DriverError
                || health == DeviceHealthPresentationState.DeviceError)
            {
                foreground = BrushFromRgb(0xFF, 0x72, 0x72);
                background = BrushFromArgb(0x28, 0xD9, 0x42, 0x42);
                symbol = health == DeviceHealthPresentationState.Missing
                    ? UiControls.SymbolRegular.PlugDisconnected20 : UiControls.SymbolRegular.ErrorCircle20;
            }
            else if (health == DeviceHealthPresentationState.Repairing)
            {
                foreground = BrushFromRgb(0x71, 0xA7, 0xFF);
                background = BrushFromArgb(0x28, 0x3E, 0x7D, 0xE0);
                symbol = UiControls.SymbolRegular.ArrowSync20;
            }
            else
            {
                foreground = MutedBrush;
                background = BrushFromArgb(0x20, 0xA7, 0xB4, 0xC8);
                symbol = UiControls.SymbolRegular.QuestionCircle20;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new UiControls.SymbolIcon
            {
                Symbol = symbol, FontSize = Z(15), Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center, Margin = ZThickness(0, 0, 5, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = DeviceGuardPresentation.StatusLabel(health), FontSize = Z(11.5),
                FontWeight = FontWeights.SemiBold, Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            });
            return new Border
            {
                Background = background, CornerRadius = new CornerRadius(Z(10)),
                Padding = ZThickness(8, 3, 8, 3), Child = row, Margin = ZThickness(0, 0, 0, 4)
            };
        }

        private Button CreateDeviceInformationButton(RoutedEventHandler onClick)
        {
            var button = new Button
            {
                Content = new UiControls.SymbolIcon
                {
                    Symbol = UiControls.SymbolRegular.Info24,
                    FontSize = Z(19), Foreground = TextBrush
                },
                Style = (Style)FindResource("ActionButtonStyle"),
                Width = Z(40), MinWidth = Z(40), MaxWidth = Z(40),
                Height = Z(38), MinHeight = Z(38), MaxHeight = Z(38), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "查看詳細資訊"
            };
            button.Click += onClick;
            return button;
        }

        private void ShowDetectableDeviceDetails(DeviceGuardDevice device)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Detail("類型", device.CategoryName),
                Detail("目前健康狀況", DeviceGuardPresentation.StatusLabel(DeviceGuardPresentation.Classify(device))),
                Detail("目前存在", YesNo(device.IsPresent)),
                Detail("已啟動", YesNo(device.IsStarted)),
                Detail("Problem Code", device.ProblemCode.ToString()),
                Detail("製造商", device.Manufacturer),
                Detail("Driver Version", device.DriverVersion),
                Detail("Service", device.Service),
                Detail("Instance ID", device.InstanceId),
                Detail("Hardware IDs", JoinDetails(device.HardwareIds)),
                Detail("Repair All", device.IncludeInRepairAll ? "允許" : "不包含"),
                Detail("修復限制", device.UnsupportedReason)
            };
            if (DeviceGuardPresentation.HasOperation(device.RepairState, device.ResultMessage))
            {
                rows.Add(Detail("最近操作狀態", RepairResultLabel(device.RepairState)));
                rows.Add(Detail("最近操作結果", device.ResultMessage));
            }
            ShowDeviceDetailDialog(device.DisplayName, rows, detectableDevicesWindow);
        }

        private void ShowCoreHardwareDetails(CoreHardwareItem item)
        {
            CoreHardwareRecoveryCandidate recovery = item.RecoveryCandidate;
            var rows = new List<KeyValuePair<string, string>>
            {
                Detail("硬體能力", CoreCapabilityLabel(item.Capability)),
                Detail("目前健康狀況", DeviceGuardPresentation.StatusLabel(DeviceGuardPresentation.Classify(item))),
                Detail("目前存在", YesNo(item.IsPresent)),
                Detail("已啟動", YesNo(item.IsStarted)),
                Detail("Problem Code", item.ProblemCode.ToString()),
                Detail("製造商", item.Manufacturer),
                Detail("Driver Version", item.DriverVersion),
                Detail("Driver INF", item.DriverInfPath),
                Detail("Anchor Instance ID", item.AnchorInstanceId),
                Detail("相關裝置節點", JoinDetails(item.NodeInstanceIds)),
                Detail("Hardware IDs", JoinDetails(item.HardwareIds)),
                Detail("Location Paths", JoinDetails(item.LocationPaths)),
                Detail("Parent Instance ID", item.ParentInstanceId),
                Detail("Recovery Kind", recovery == null ? string.Empty : recovery.Kind.ToString()),
                Detail("Recovery Instance ID", recovery == null ? string.Empty : recovery.InstanceId),
                Detail("Recovery Problem Code", recovery == null ? string.Empty : recovery.ProblemCode.ToString()),
                Detail("Recovery Location", recovery == null ? string.Empty : recovery.LocationPath),
                Detail("Recovery Evidence", recovery == null ? string.Empty : recovery.MatchEvidence),
                Detail("持久健康基線", item.FromBaseline ? "已由健康基線識別" : "否"),
                Detail("識別唯一性", item.IdentityAmbiguous ? "符合多個候選，禁止自動操作" : "已唯一辨識"),
                Detail("USB restart 政策", item.UsbRestartBlocked ? item.UsbRestartBlockReason : string.Empty)
            };
            if (DeviceGuardPresentation.HasOperation(item.RepairState, item.ResultMessage))
            {
                rows.Add(Detail("最近操作狀態", RepairResultLabel(item.RepairState)));
                rows.Add(Detail("最近操作結果", item.ResultMessage));
            }
            ShowDeviceDetailDialog(CoreCapabilityLabel(item.Capability) + " — " + item.DisplayName,
                rows, coreHardwareWindow);
        }

        private void ShowDeviceDetailDialog(string title, List<KeyValuePair<string, string>> rows, Window owner)
        {
            Window dialog = CreateGameHelperDialog("裝置詳細資訊", Z(760), Z(620));
            dialog.Owner = owner ?? this;
            var root = new Grid { Margin = ZThickness(24, 20, 24, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = title, FontSize = Z(21), FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush, TextWrapping = TextWrapping.Wrap,
                Margin = ZThickness(0, 0, 0, 14)
            });

            var details = new StackPanel();
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(rows[i].Value))
                {
                    continue;
                }
                var row = new Grid { Margin = ZThickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Z(150)) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var label = new TextBlock
                {
                    Text = rows[i].Key, Foreground = MutedBrush, FontSize = Z(12.5),
                    Margin = ZThickness(0, 2, 16, 0), TextWrapping = TextWrapping.Wrap
                };
                row.Children.Add(label);
                var value = new TextBlock
                {
                    Text = rows[i].Value, Foreground = TextBrush, FontSize = Z(12.5),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                details.Children.Add(row);
            }
            var scroll = new ScrollViewer
            {
                Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            Button close = CreateActionButton("關閉", false, delegate { dialog.Close(); });
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = ZThickness(0, 14, 0, 0);
            Grid.SetRow(close, 2);
            root.Children.Add(close);
            dialog.Content = root;
            dialog.ShowDialog();
        }

        private static string BuildDeviceSummary(DeviceGuardDevice device)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(device.Manufacturer)) parts.Add(device.Manufacturer);
            if (!string.IsNullOrWhiteSpace(device.DriverVersion)) parts.Add("Driver " + device.DriverVersion);
            return string.Join(" · ", parts.ToArray());
        }

        private static string BuildCoreHardwareSummary(CoreHardwareItem item)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Manufacturer)) parts.Add(item.Manufacturer);
            if (!string.IsNullOrWhiteSpace(item.DriverVersion)) parts.Add("Driver " + item.DriverVersion);
            return string.Join(" · ", parts.ToArray());
        }

        private static UiControls.SymbolRegular DeviceTypeSymbol(DeviceGuardKind kind)
        {
            if (kind == DeviceGuardKind.BluetoothAdapter) return UiControls.SymbolRegular.Bluetooth24;
            if (kind == DeviceGuardKind.Camera) return UiControls.SymbolRegular.Camera24;
            if (kind == DeviceGuardKind.UsbAudio) return UiControls.SymbolRegular.Speaker224;
            return UiControls.SymbolRegular.QuestionCircle24;
        }

        private static UiControls.SymbolRegular CoreCapabilitySymbol(CoreHardwareCapability capability)
        {
            if (capability == CoreHardwareCapability.Bluetooth) return UiControls.SymbolRegular.Bluetooth24;
            if (capability == CoreHardwareCapability.Graphics) return UiControls.SymbolRegular.Desktop24;
            if (capability == CoreHardwareCapability.Network) return UiControls.SymbolRegular.NetworkAdapter16;
            if (capability == CoreHardwareCapability.Audio) return UiControls.SymbolRegular.Speaker224;
            if (capability == CoreHardwareCapability.Camera) return UiControls.SymbolRegular.Camera24;
            if (capability == CoreHardwareCapability.Usb) return UiControls.SymbolRegular.UsbPlug24;
            return UiControls.SymbolRegular.QuestionCircle24;
        }

        private static string RepairResultLabel(DeviceRepairState state)
        {
            if (state == DeviceRepairState.Repairing) return "修復步驟執行中";
            if (state == DeviceRepairState.Succeeded) return "修復完成且驗證成功";
            if (state == DeviceRepairState.Partial) return "部分完成";
            if (state == DeviceRepairState.Skipped) return "已略過";
            if (state == DeviceRepairState.Unsupported) return "不支援此操作";
            if (state == DeviceRepairState.Failed) return "修復失敗";
            if (state == DeviceRepairState.RebootRequired) return "操作完成，需要重新啟動電腦";
            if (state == DeviceRepairState.NeedsDriver) return "需要驅動程式";
            if (state == DeviceRepairState.Ambiguous) return "無法唯一辨識裝置";
            if (state == DeviceRepairState.RiskDeclined) return "未授權所需風險層級";
            if (state == DeviceRepairState.Canceled) return "已取消尚未開始的步驟";
            return "尚無操作";
        }

        private static KeyValuePair<string, string> Detail(string label, string value)
        {
            return new KeyValuePair<string, string>(label, value ?? string.Empty);
        }

        private static string JoinDetails(string[] values)
        {
            return values == null || values.Length == 0 ? string.Empty : string.Join(Environment.NewLine, values);
        }

        private static string YesNo(bool value)
        {
            return value ? "是" : "否";
        }

        private static SolidColorBrush BrushFromArgb(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private void BeginCoreHardwareRepair(CoreHardwareItem item)
        {
            RepairRiskLevel authorized = CoreHardwareResolver.RestartRisk(item.Capability);
            if (item.Capability == CoreHardwareCapability.Usb && item.UsbRestartBlocked)
            {
                controller.RepairCoreHardware(item.Id, RepairRiskLevel.Low);
                return;
            }

            if (item.Health != CoreHardwareHealth.Healthy)
                authorized = CoreHardwareResolver.RemoveRisk(item.Capability);

            if (authorized >= RepairRiskLevel.High)
            {
                string warning = item.Capability == CoreHardwareCapability.Graphics
                    ? "此操作可能短暫黑畫面；只有低風險步驟失敗後，才會依授權風險處理精確裝置實例。"
                    : item.Capability == CoreHardwareCapability.Network
                        ? "此操作可能中斷網路連線；只有低風險步驟失敗後，才會處理精確裝置實例。"
                        : item.Capability == CoreHardwareCapability.Usb
                            ? "此操作可能切斷 USB 裝置。已確認存在關鍵輸入或儲存子節點時，程式仍會強制阻止 restart。"
                            : "若 scan、enable、restart 與 service 修復皆失敗，可能移除精確裝置實例再重新列舉。";
                MessageBoxResult result = System.Windows.MessageBox.Show(coreHardwareWindow,
                    warning + Environment.NewLine + Environment.NewLine + "是否繼續一鍵修復？",
                    "確認高風險修復", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
            controller.RepairCoreHardware(item.Id, authorized);
        }

        private static string BuildCoreHardwareDescription(CoreHardwareItem item)
        {
            var parts = new List<string>
            {
                "狀態 " + CoreHealthLabel(item.Health),
                "Problem Code " + item.ProblemCode
            };
            if (!string.IsNullOrWhiteSpace(item.Manufacturer)) parts.Add(item.Manufacturer);
            if (!string.IsNullOrWhiteSpace(item.DriverVersion)) parts.Add("Driver " + item.DriverVersion);
            if (!string.IsNullOrWhiteSpace(item.DriverInfPath)) parts.Add(item.DriverInfPath);
            string detail = string.Join(" | ", parts.ToArray()) + ".";
            if (item.FromBaseline) detail += Environment.NewLine + "已由持久健康基線識別。";
            if (item.IdentityAmbiguous) detail += Environment.NewLine + "符合多個候選，已禁止自動操作。";
            if (item.UsbRestartBlocked) detail += Environment.NewLine + item.UsbRestartBlockReason;
            if (!string.IsNullOrWhiteSpace(item.ResultMessage)) detail += Environment.NewLine + item.ResultMessage;
            return detail;
        }

        private static string CoreCapabilityLabel(CoreHardwareCapability capability)
        {
            return capability == CoreHardwareCapability.Usb ? "USB Host Controller" : capability.ToString();
        }

        private static string CoreHealthLabel(CoreHardwareHealth health)
        {
            if (health == CoreHardwareHealth.Healthy) return "正常";
            if (health == CoreHardwareHealth.Degraded) return "異常";
            if (health == CoreHardwareHealth.Disabled) return "已停用";
            if (health == CoreHardwareHealth.DriverMissing) return "驅動未載入";
            if (health == CoreHardwareHealth.Missing) return "缺失";
            if (health == CoreHardwareHealth.RebootRequired) return "需要重新啟動";
            if (health == CoreHardwareHealth.Ambiguous) return "無法唯一辨識";
            return "無法確認";
        }
    }
}
