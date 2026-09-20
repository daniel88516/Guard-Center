using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GuardCenter
{
    public partial class MainWindow
    {
        private Window activeLinkGuardDialog;
        private Action linkGuardDialogRefresh;
        private ProgressiveListState<AppCatalogItem> linkGuardAllAppsListState;
        private ProgressiveListState<LinkGuardRuleGroup> linkGuardLinkedAppsListState;
        private ProgressiveListPresenter<AppCatalogItem> linkGuardAllAppsPresenter;
        private ProgressiveListPresenter<LinkGuardRuleGroup> linkGuardLinkedAppsPresenter;
        private bool rebuildingLinkGuardLinkedList;
        private readonly HashSet<string> linkGuardExpandedGroupIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class LinkGuardRuleGroup
        {
            public string Id { get; set; }
            public LinkGuardApp TriggerApp { get; set; }
            public List<LinkGuardRule> Rules { get; set; }
        }

        internal void RefreshLinkGuardUi(bool rulesChanged)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { RefreshLinkGuardUi(rulesChanged); }));
                return;
            }
            RefreshStatus();
            if (selectedModuleIndex == 8)
            {
                RenderLinkGuardPage();
            }
        }

        private void RenderLinkGuardPage()
        {
            ConfigureHeader("Link Guard",
                "Keep related applications running through independent one-way or bidirectional rules.", false);
            SettingsStack.Children.Clear();
            audioDependentCards.Clear();

            List<LinkGuardRule> rules = controller.GetLinkGuardRules();
            int enabled = 0;
            int active = 0;
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].Enabled) enabled++;
                if ((rules[i].RuntimeStatus ?? string.Empty).StartsWith("Linked", StringComparison.Ordinal))
                {
                    active++;
                }
            }

            AddSection("App links");
            AddCard("Manage linked apps",
                rules.Count == 0
                    ? "No links are configured. Choose a trigger app and then the app that should follow it."
                    : rules.Count + " rule" + (rules.Count == 1 ? string.Empty : "s") + " · "
                        + enabled + " enabled · " + active + " active.",
                CreateActionButton("Manage apps", true, delegate { ShowLinkGuardManageDialog(); }), false);

            AddSection("How it works");
            AddCard("One-way  A → B",
                "When A is running, B is kept running. B never starts A unless the rule is changed to bidirectional.",
                null, false);
            AddCard("Bidirectional  A ↔ B",
                "Starting either app starts the other. Each rule independently controls gsudo, exit behavior and launch delay.",
                null, false);
        }

        private void ShowLinkGuardManageDialog()
        {
            if (activeLinkGuardDialog != null)
            {
                activeLinkGuardDialog.Activate();
                return;
            }

            EnsureLinkGuardListStates();
            ClearLinkGuardSearchStates();
            Window dialog = CreateGameHelperDialog("Link Guard · Manage apps", Z(880), Z(700));
            activeLinkGuardDialog = dialog;
            bool showingAllApps = true;
            bool loadingApps = true;
            bool closed = false;
            string loadError = string.Empty;
            List<AppCatalogItem> installedApps = null;
            List<LinkGuardRule> linkedRules = controller.GetLinkGuardRules();

            var root = new Grid { Margin = ZThickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = "Manage apps",
                FontSize = Z(24),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 16)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var tabs = new WrapPanel { Margin = ZThickness(0, 0, 0, 12) };
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            var controls = new Grid { Margin = ZThickness(0, 0, 0, 10) };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var search = new TextBox
            {
                MinHeight = Z(36),
                Padding = ZThickness(10, 6, 10, 6),
                FontSize = Z(13),
                Background = CardBrush,
                Foreground = TextBrush,
                BorderBrush = CardBorderBrush,
                ToolTip = "Search name, publisher, source or executable path"
            };
            controls.Children.Add(search);
            Button refresh = CreateActionButton("Refresh", false, null);
            refresh.Margin = ZThickness(10, 0, 0, 0);
            Grid.SetColumn(refresh, 1);
            controls.Children.Add(refresh);
            Grid.SetRow(controls, 2);
            root.Children.Add(controls);

            var countText = new TextBlock
            {
                Foreground = MutedBrush,
                FontSize = Z(12),
                Margin = ZThickness(2, 0, 0, 10)
            };
            Grid.SetRow(countText, 3);
            root.Children.Add(countText);

            var listHost = new Grid();
            Grid.SetRow(listHost, 4);
            root.Children.Add(listHost);

            Action disposePresenters = delegate
            {
                if (linkGuardAllAppsPresenter != null)
                {
                    linkGuardAllAppsPresenter.Dispose();
                    linkGuardAllAppsPresenter = null;
                }
                if (linkGuardLinkedAppsPresenter != null)
                {
                    linkGuardLinkedAppsPresenter.Dispose();
                    linkGuardLinkedAppsPresenter = null;
                }
            };

            Action renderTabs = null;
            Action renderList = null;
            Action<bool> selectTab = null;

            renderTabs = delegate
            {
                tabs.Children.Clear();
                Button all = CreateActionButton("All apps", showingAllApps, delegate { selectTab(true); });
                all.Width = Z(132);
                all.Margin = ZThickness(0, 0, 8, 0);
                tabs.Children.Add(all);
                Button linked = CreateActionButton("Linked apps", !showingAllApps,
                    delegate { selectTab(false); });
                linked.Width = Z(142);
                tabs.Children.Add(linked);
            };

            renderList = delegate
            {
                var expandedBeforeRebuild = new HashSet<string>(
                    linkGuardExpandedGroupIds, StringComparer.OrdinalIgnoreCase);
                rebuildingLinkGuardLinkedList = true;
                try
                {
                    disposePresenters();
                    listHost.Children.Clear();
                }
                finally
                {
                    rebuildingLinkGuardLinkedList = false;
                    linkGuardExpandedGroupIds.UnionWith(expandedBeforeRebuild);
                }
                if (showingAllApps)
                {
                    if (loadingApps)
                    {
                        AddLinkGuardMessage(listHost, "Loading installed apps",
                            "Reading Start Menu and installed application entries.");
                        countText.Text = "Loading…";
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(loadError))
                    {
                        AddLinkGuardMessage(listHost, "Installed apps unavailable", loadError);
                        countText.Text = "Load failed";
                        return;
                    }
                    linkGuardAllAppsPresenter = new ProgressiveListPresenter<AppCatalogItem>(
                        linkGuardAllAppsListState,
                        delegate(AppCatalogItem app)
                        {
                            return CreateGameHelperPickerCardElement(ToGameHelperCandidate(app),
                                "Link", true, delegate
                                {
                                    ShowLinkGuardTargetPicker(app, installedApps, delegate
                                    {
                                        linkedRules = controller.GetLinkGuardRules();
                                        showingAllApps = false;
                                        ClearLinkGuardSearchStates();
                                        search.Text = string.Empty;
                                        renderTabs();
                                        renderList();
                                        RenderLinkGuardPage();
                                    });
                                });
                        }, delegate(int visible, int total)
                        {
                            countText.Text = visible + " of " + total + " apps";
                        });
                    linkGuardAllAppsPresenter.Reset(installedApps, LinkGuardAppMatches,
                        CompareLinkGuardApps, true);
                    listHost.Children.Add(linkGuardAllAppsPresenter.Element);
                }
                else
                {
                    linkedRules = controller.GetLinkGuardRules();
                    List<LinkGuardRuleGroup> groups = BuildLinkGuardRuleGroups(linkedRules);
                    if (groups.Count == 0)
                    {
                        AddLinkGuardMessage(listHost, "No linked apps",
                            "Open All apps, choose a trigger app, and select the app it should launch.");
                        countText.Text = "0 linked apps";
                        return;
                    }
                    linkGuardLinkedAppsPresenter = new ProgressiveListPresenter<LinkGuardRuleGroup>(
                        linkGuardLinkedAppsListState, CreateLinkGuardRuleGroupCard,
                        delegate(int visible, int total)
                        {
                            countText.Text = visible + " of " + total + " linked apps · "
                                + linkedRules.Count + " links";
                        });
                    linkGuardLinkedAppsPresenter.Reset(groups, LinkGuardRuleGroupMatches,
                        CompareLinkGuardRuleGroups, true);
                    listHost.Children.Add(linkGuardLinkedAppsPresenter.Element);
                }
            };

            selectTab = delegate(bool allApps)
            {
                if (showingAllApps == allApps) return;
                showingAllApps = allApps;
                ClearLinkGuardSearchStates();
                search.Text = string.Empty;
                renderTabs();
                renderList();
            };

            search.TextChanged += delegate
            {
                if (showingAllApps)
                {
                    linkGuardAllAppsListState.SearchText = search.Text ?? string.Empty;
                }
                else
                {
                    linkGuardLinkedAppsListState.SearchText = search.Text ?? string.Empty;
                }
                renderList();
            };
            refresh.Click += async delegate
            {
                if (!showingAllApps)
                {
                    linkedRules = controller.GetLinkGuardRules();
                    renderList();
                    return;
                }
                loadingApps = true;
                loadError = string.Empty;
                renderList();
                try
                {
                    installedApps = await Task.Run(delegate
                    {
                        controller.RefreshLinkGuardInstalledApps();
                        return controller.GetLinkGuardInstalledApps();
                    });
                }
                catch (Exception ex)
                {
                    loadError = ex.Message;
                }
                loadingApps = false;
                if (showingAllApps)
                {
                    renderList();
                }
            };

            linkGuardDialogRefresh = delegate
            {
                if (closed || showingAllApps) return;
                linkedRules = controller.GetLinkGuardRules();
                renderList();
            };
            dialog.Content = root;
            dialog.Closed += delegate
            {
                closed = true;
                disposePresenters();
                ClearLinkGuardSearchStates();
                linkGuardDialogRefresh = null;
                activeLinkGuardDialog = null;
            };
            renderTabs();
            renderList();

            dialog.Loaded += async delegate
            {
                try
                {
                    installedApps = await Task.Run(new Func<List<AppCatalogItem>>(
                        controller.GetLinkGuardInstalledApps));
                }
                catch (Exception ex)
                {
                    loadError = ex.Message;
                }
                loadingApps = false;
                if (closed) return;
                if (showingAllApps)
                {
                    renderList();
                }
            };
            dialog.ShowDialog();
        }

        private void ShowLinkGuardTargetPicker(AppCatalogItem trigger, List<AppCatalogItem> installedApps,
            Action completed)
        {
            Window dialog = CreateGameHelperDialog("Choose linked apps for " + trigger.Name, Z(820), Z(700));
            var state = new ProgressiveListState<AppCatalogItem>(new ListPresentationSettings(),
                delegate(AppCatalogItem item) { return item.Id; });
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentLinkedRules = new List<LinkGuardRule>();
            List<LinkGuardRule> existingRules = controller.GetLinkGuardRules();
            string triggerPath = AppIdentityService.NormalizeExecutablePath(trigger.TargetPath);
            for (int i = 0; i < existingRules.Count; i++)
            {
                if (string.Equals(AppIdentityService.NormalizeExecutablePath(
                        existingRules[i].TriggerApp.TargetPath), triggerPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    existingPaths.Add(AppIdentityService.NormalizeExecutablePath(
                        existingRules[i].LinkedApp.TargetPath));
                    currentLinkedRules.Add(existingRules[i]);
                }
            }
            var choices = new List<AppCatalogItem>();
            for (int i = 0; i < installedApps.Count; i++)
            {
                string candidatePath = AppIdentityService.NormalizeExecutablePath(installedApps[i].TargetPath);
                if (!string.Equals(candidatePath, triggerPath, StringComparison.OrdinalIgnoreCase))
                {
                    choices.Add(installedApps[i]);
                }
            }

            var root = new Grid { Margin = ZThickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = "Link apps to " + trigger.Name,
                FontSize = Z(22),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = ZThickness(0, 0, 0, 14)
            });
            var search = new TextBox
            {
                MinHeight = Z(36),
                Padding = ZThickness(10, 6, 10, 6),
                Margin = ZThickness(0, 0, 0, 12),
                FontSize = Z(13),
                Background = CardBrush,
                Foreground = TextBrush,
                BorderBrush = CardBorderBrush,
                ToolTip = "Search linked app"
            };
            Grid.SetRow(search, 1);
            root.Children.Add(search);

            Action render = null;
            var footer = new Grid { Margin = ZThickness(0, 14, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var selectedText = new TextBlock
            {
                Text = "0 new apps selected",
                FontSize = Z(12),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(selectedText);
            Button viewLinked = CreateActionButton(
                "View linked apps (" + currentLinkedRules.Count + ")", false,
                delegate
                {
                    ShowCurrentLinkGuardLinks(trigger, currentLinkedRules, selectedPaths,
                        choices, existingPaths);
                    render();
                });
            viewLinked.Margin = ZThickness(8, 0, 8, 0);
            Grid.SetColumn(viewLinked, 1);
            footer.Children.Add(viewLinked);
            var footerButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = CreateActionButton("Cancel", false, delegate { dialog.Close(); });
            cancel.Margin = ZThickness(0, 0, 8, 0);
            footerButtons.Children.Add(cancel);
            Button confirm = CreateActionButton("Add links", true, null);
            confirm.Margin = new Thickness(0);
            confirm.IsEnabled = false;
            footerButtons.Children.Add(confirm);
            Grid.SetColumn(footerButtons, 2);
            footer.Children.Add(footerButtons);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            ProgressiveListPresenter<AppCatalogItem> presenter = null;
            render = delegate
            {
                if (presenter != null) presenter.Dispose();
                var availableChoices = new List<AppCatalogItem>();
                for (int i = 0; i < choices.Count; i++)
                {
                    string candidatePath = AppIdentityService.NormalizeExecutablePath(choices[i].TargetPath);
                    if (!existingPaths.Contains(candidatePath))
                    {
                        availableChoices.Add(choices[i]);
                    }
                }
                presenter = new ProgressiveListPresenter<AppCatalogItem>(state,
                    delegate(AppCatalogItem linked)
                    {
                        string linkedPath = AppIdentityService.NormalizeExecutablePath(linked.TargetPath);
                        bool selected = selectedPaths.Contains(linkedPath);
                        return CreateGameHelperPickerCardElement(ToGameHelperCandidate(linked),
                            selected ? "Selected ✓" : "Add", selected, delegate
                            {
                                if (!selectedPaths.Add(linkedPath))
                                {
                                    selectedPaths.Remove(linkedPath);
                                }
                                render();
                            });
                    }, null);
                presenter.Reset(availableChoices, LinkGuardAppMatches, CompareLinkGuardApps, true);
                Grid.SetRow(presenter.Element, 2);
                UIElement previousList = null;
                for (int i = 0; i < root.Children.Count; i++)
                {
                    UIElement child = root.Children[i];
                    if (Grid.GetRow(child) == 2)
                    {
                        previousList = child;
                        break;
                    }
                }
                if (previousList != null) root.Children.Remove(previousList);
                root.Children.Add(presenter.Element);
                selectedText.Text = selectedPaths.Count + " new app"
                    + (selectedPaths.Count == 1 ? string.Empty : "s") + " selected · "
                    + currentLinkedRules.Count + " existing link"
                    + (currentLinkedRules.Count == 1 ? string.Empty : "s");
                viewLinked.Content = "View linked apps ("
                    + (currentLinkedRules.Count + selectedPaths.Count) + ")";
                confirm.IsEnabled = selectedPaths.Count > 0;
            };
            search.TextChanged += delegate
            {
                state.SearchText = search.Text ?? string.Empty;
                render();
            };
            confirm.Click += delegate
            {
                var failures = new List<string>();
                int added = 0;
                for (int i = 0; i < choices.Count; i++)
                {
                    string path = AppIdentityService.NormalizeExecutablePath(choices[i].TargetPath);
                    if (!selectedPaths.Contains(path)) continue;
                    LinkGuardActionResult result = controller.AddLinkGuardRule(trigger, choices[i]);
                    if (result.Success)
                    {
                        added++;
                    }
                    else
                    {
                        failures.Add(choices[i].Name + ": " + result.Message);
                    }
                }
                if (failures.Count > 0)
                {
                    MessageBox.Show(added + " links added." + Environment.NewLine + Environment.NewLine
                        + string.Join(Environment.NewLine, failures), "Link Guard",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                if (added == 0) return;
                dialog.Close();
                if (completed != null) completed();
            };
            dialog.Content = root;
            dialog.Closed += delegate
            {
                state.SearchText = string.Empty;
                selectedPaths.Clear();
                if (presenter != null) presenter.Dispose();
            };
            render();
            dialog.ShowDialog();
        }

        private void ShowCurrentLinkGuardLinks(AppCatalogItem trigger, List<LinkGuardRule> linkedRules,
            HashSet<string> selectedPaths, List<AppCatalogItem> candidates,
            HashSet<string> existingPaths)
        {
            Window dialog = CreateGameHelperDialog(trigger.Name + " · Linked apps", Z(720), Z(620));
            var root = new Grid { Margin = ZThickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = "Selected and linked apps",
                FontSize = Z(22),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            });
            var count = new TextBlock
            {
                Text = string.Empty,
                FontSize = Z(12),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 6, 0, 14)
            };
            Grid.SetRow(count, 1);
            root.Children.Add(count);

            var list = new StackPanel();
            var scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            Button close = CreateActionButton("Close", false, delegate { dialog.Close(); });
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = ZThickness(0, 14, 0, 0);
            Grid.SetRow(close, 3);
            root.Children.Add(close);
            Action render = null;
            render = delegate
            {
                list.Children.Clear();
                var selectedApps = new List<AppCatalogItem>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    string path = AppIdentityService.NormalizeExecutablePath(candidates[i].TargetPath);
                    if (selectedPaths.Contains(path)) selectedApps.Add(candidates[i]);
                }
                count.Text = trigger.Name + " has " + linkedRules.Count + " existing link"
                    + (linkedRules.Count == 1 ? string.Empty : "s") + " and "
                    + selectedApps.Count + " new selection"
                    + (selectedApps.Count == 1 ? string.Empty : "s") + ".";

                list.Children.Add(CreateLinkGuardListSectionTitle(
                    "Selected now (" + selectedApps.Count + ")", false));
                if (selectedApps.Count == 0)
                {
                    AddCardToPanel(list, "No new selections",
                        "Apps selected in the previous window will appear here before you add them.",
                        null, false);
                }
                else
                {
                    for (int i = 0; i < selectedApps.Count; i++)
                    {
                        AppCatalogItem selectedApp = selectedApps[i];
                        string selectedPath = AppIdentityService.NormalizeExecutablePath(selectedApp.TargetPath);
                        list.Children.Add(CreateLinkGuardSelectionSummaryCard(selectedApp.Name,
                            selectedApp.TargetPath, selectedApp.IconPath, "Remove selection", delegate
                            {
                                selectedPaths.Remove(selectedPath);
                                render();
                            }));
                    }
                }

                list.Children.Add(CreateLinkGuardListSectionTitle(
                    "Previously linked (" + linkedRules.Count + ")", true));
                if (linkedRules.Count == 0)
                {
                    AddCardToPanel(list, "No previous links",
                        "No apps are currently linked to " + trigger.Name + ".", null, false);
                }
                else
                {
                    for (int i = 0; i < linkedRules.Count; i++)
                    {
                        LinkGuardRule linkedRule = linkedRules[i];
                        string linkedPath = AppIdentityService.NormalizeExecutablePath(
                            linkedRule.LinkedApp.TargetPath);
                        list.Children.Add(CreateLinkGuardSelectionSummaryCard(
                            linkedRule.LinkedApp.Name, linkedRule.LinkedApp.TargetPath,
                            linkedRule.LinkedApp.IconPath, "Remove link", delegate
                            {
                                if (MessageBox.Show("Remove the link to " + linkedRule.LinkedApp.Name + "?",
                                    "Link Guard", MessageBoxButton.YesNo, MessageBoxImage.Question)
                                    != MessageBoxResult.Yes) return;
                                LinkGuardActionResult result = controller.RemoveLinkGuardRule(linkedRule.Id);
                                ShowLinkGuardResult(result);
                                if (!result.Success) return;
                                linkedRules.RemoveAll(delegate(LinkGuardRule item)
                                {
                                    return string.Equals(item.Id, linkedRule.Id,
                                        StringComparison.OrdinalIgnoreCase);
                                });
                                existingPaths.Remove(linkedPath);
                                render();
                            }));
                    }
                }
            };
            dialog.Content = root;
            render();
            dialog.ShowDialog();
        }

        private UIElement CreateLinkGuardListSectionTitle(string text, bool spaced)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = Z(13),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = spaced ? ZThickness(0, 18, 0, 10) : ZThickness(0, 0, 0, 10)
            };
        }

        private UIElement CreateLinkGuardSelectionSummaryCard(string name, string targetPath,
            string iconPath, string actionLabel, RoutedEventHandler action)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string resolvedIconPath = string.IsNullOrWhiteSpace(iconPath) ? targetPath : iconPath;
            UIElement icon = CreateFileIcon(resolvedIconPath);
            icon.SetValue(FrameworkElement.WidthProperty, Z(42));
            icon.SetValue(FrameworkElement.HeightProperty, Z(42));
            icon.SetValue(FrameworkElement.MarginProperty, ZThickness(0, 0, 14, 0));
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = Z(14),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            });
            text.Children.Add(new TextBlock
            {
                Text = targetPath,
                FontSize = Z(11),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Button actionButton = CreateActionButton("×", false, action);
            actionButton.MinWidth = Z(38);
            actionButton.Width = Z(38);
            actionButton.Height = Z(38);
            actionButton.Padding = new Thickness(0);
            actionButton.FontSize = Z(18);
            actionButton.VerticalAlignment = VerticalAlignment.Top;
            actionButton.ToolTip = actionLabel;
            System.Windows.Automation.AutomationProperties.SetName(actionButton, actionLabel);
            actionButton.Margin = ZThickness(14, 0, 0, 0);
            Grid.SetColumn(actionButton, 2);
            grid.Children.Add(actionButton);
            return new Border
            {
                Margin = ZThickness(0, 0, 0, 8),
                Padding = ZThickness(14, 12, 14, 12),
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(8)),
                Child = grid
            };
        }

        private UIElement CreateLinkGuardRuleGroupCard(LinkGuardRuleGroup group)
        {
            int enabled = 0;
            int active = 0;
            for (int i = 0; i < group.Rules.Count; i++)
            {
                if (group.Rules[i].Enabled) enabled++;
                if ((group.Rules[i].RuntimeStatus ?? string.Empty).StartsWith("Linked",
                    StringComparison.Ordinal)) active++;
            }
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string triggerIconPath = string.IsNullOrWhiteSpace(group.TriggerApp.IconPath)
                ? group.TriggerApp.TargetPath : group.TriggerApp.IconPath;
            UIElement triggerIcon = CreateFileIcon(triggerIconPath);
            triggerIcon.SetValue(FrameworkElement.WidthProperty, Z(42));
            triggerIcon.SetValue(FrameworkElement.HeightProperty, Z(42));
            triggerIcon.SetValue(FrameworkElement.MarginProperty, ZThickness(0, 0, 14, 0));
            triggerIcon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.Children.Add(triggerIcon);
            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headerText.Children.Add(new TextBlock
            {
                Text = group.TriggerApp.Name,
                FontSize = Z(15),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            });
            headerText.Children.Add(new TextBlock
            {
                Text = group.Rules.Count + " linked app"
                    + (group.Rules.Count == 1 ? string.Empty : "s") + " · "
                    + enabled + " enabled · " + active + " active",
                FontSize = Z(12),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 0)
            });
            Grid.SetColumn(headerText, 1);
            header.Children.Add(headerText);

            var expander = new Expander
            {
                Header = header,
                Foreground = TextBrush,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = linkGuardExpandedGroupIds.Contains(group.Id)
            };
            expander.Expanded += delegate { linkGuardExpandedGroupIds.Add(group.Id); };
            expander.Collapsed += delegate
            {
                if (expander.IsLoaded && !rebuildingLinkGuardLinkedList)
                {
                    linkGuardExpandedGroupIds.Remove(group.Id);
                }
            };
            var content = new StackPanel { Margin = ZThickness(4, 12, 4, 4) };
            for (int i = 0; i < group.Rules.Count; i++)
            {
                content.Children.Add(CreateLinkGuardRuleDetail(group.Rules[i]));
            }
            expander.Content = content;
            return new Border
            {
                Margin = ZThickness(0, 0, 0, 10),
                Padding = ZThickness(14, 12, 14, 12),
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(8)),
                Child = expander
            };
        }

        private UIElement CreateLinkGuardRuleDetail(LinkGuardRule rule)
        {
            string arrow = rule.Mode == LinkGuardMode.Bidirectional ? " ↔ " : " → ";
            var content = new StackPanel { Margin = ZThickness(4, 4, 4, 4) };
            var identity = new Grid { Margin = ZThickness(0, 0, 0, 14) };
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string linkedIconPath = string.IsNullOrWhiteSpace(rule.LinkedApp.IconPath)
                ? rule.LinkedApp.TargetPath : rule.LinkedApp.IconPath;
            UIElement linkedIcon = CreateFileIcon(linkedIconPath);
            linkedIcon.SetValue(FrameworkElement.WidthProperty, Z(36));
            linkedIcon.SetValue(FrameworkElement.HeightProperty, Z(36));
            linkedIcon.SetValue(FrameworkElement.MarginProperty, ZThickness(0, 0, 12, 0));
            linkedIcon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            identity.Children.Add(linkedIcon);
            var identityText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var linkedTitle = new TextBlock
            {
                Text = arrow.Trim() + " " + rule.LinkedApp.Name,
                FontSize = Z(14),
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            };
            identityText.Children.Add(linkedTitle);
            identityText.Children.Add(new TextBlock
            {
                Text = rule.RuntimeStatus,
                FontSize = Z(12),
                Foreground = MutedBrush,
                Margin = ZThickness(0, 4, 0, 0)
            });
            Grid.SetColumn(identityText, 1);
            identity.Children.Add(identityText);
            Button remove = CreateActionButton("×", false, delegate
            {
                if (MessageBox.Show("Remove the link to " + rule.LinkedApp.Name + "?", "Link Guard",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                LinkGuardActionResult result = controller.RemoveLinkGuardRule(rule.Id);
                ShowLinkGuardResult(result);
                if (result.Success && linkGuardDialogRefresh != null)
                {
                    linkGuardDialogRefresh();
                }
            });
            remove.MinWidth = Z(38);
            remove.Width = Z(38);
            remove.Height = Z(38);
            remove.Padding = new Thickness(0);
            remove.Margin = new Thickness(0);
            remove.FontSize = Z(18);
            remove.ToolTip = "Remove link";
            Grid.SetColumn(remove, 2);
            identity.Children.Add(remove);
            content.Children.Add(identity);

            var mode = CreateSmallComboBox(Z(180));
            AddComboItem(mode, "One-way  A → B", "one-way",
                rule.Mode == LinkGuardMode.OneWay ? "one-way" : "two-way");
            AddComboItem(mode, "Bidirectional  A ↔ B", "two-way",
                rule.Mode == LinkGuardMode.OneWay ? "one-way" : "two-way");
            TextBlock keepLabelText = null;
            Wpf.Ui.Controls.ToggleSwitch keepToggle = null;
            bool syncingKeepToggle = false;
            mode.SelectionChanged += delegate
            {
                ComboBoxItem selected = mode.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                LinkGuardMode selectedMode = string.Equals(Convert.ToString(selected.Tag), "two-way",
                    StringComparison.OrdinalIgnoreCase)
                    ? LinkGuardMode.Bidirectional : LinkGuardMode.OneWay;
                linkedTitle.Text = (selectedMode == LinkGuardMode.Bidirectional ? "↔ " : "→ ")
                    + rule.LinkedApp.Name;
                if (keepLabelText != null)
                {
                    keepLabelText.Text = selectedMode == LinkGuardMode.Bidirectional
                        ? "Restart the missing app if one closes"
                        : "Close B after A closes";
                }
                if (keepToggle != null)
                {
                    syncingKeepToggle = true;
                    keepToggle.IsChecked = selectedMode == LinkGuardMode.OneWay
                        ? !rule.KeepLinkedAppRunning : rule.KeepLinkedAppRunning;
                    syncingKeepToggle = false;
                }
                UpdateLinkGuardRule(rule, selectedMode,
                    rule.Enabled, rule.UseGsudo, rule.KeepLinkedAppRunning,
                    rule.MaintainLinkedAppRunning, rule.LaunchDelaySeconds);
            };
            content.Children.Add(CreateLinkGuardSettingRow("Link direction", mode));

            content.Children.Add(CreateLinkGuardSettingRow("Rule enabled",
                CreateToggle(rule.Enabled, delegate(bool value)
                {
                    UpdateLinkGuardRule(rule, rule.Mode, value, rule.UseGsudo,
                        rule.KeepLinkedAppRunning, rule.MaintainLinkedAppRunning,
                        rule.LaunchDelaySeconds);
                })));
            content.Children.Add(CreateLinkGuardSettingRow("Use gsudo when available",
                CreateToggle(rule.UseGsudo, delegate(bool value)
                {
                    UpdateLinkGuardRule(rule, rule.Mode, rule.Enabled, value,
                        rule.KeepLinkedAppRunning, rule.MaintainLinkedAppRunning,
                        rule.LaunchDelaySeconds);
                })));
            string keepLabel = rule.Mode == LinkGuardMode.Bidirectional
                ? "Restart the missing app if one closes"
                : "Close B after A closes";
            keepToggle = (Wpf.Ui.Controls.ToggleSwitch)CreateToggle(
                rule.Mode == LinkGuardMode.OneWay
                    ? !rule.KeepLinkedAppRunning : rule.KeepLinkedAppRunning,
                delegate(bool value)
                {
                    if (syncingKeepToggle) return;
                    UpdateLinkGuardRule(rule, rule.Mode, rule.Enabled, rule.UseGsudo,
                        rule.Mode == LinkGuardMode.OneWay ? !value : value,
                        rule.MaintainLinkedAppRunning, rule.LaunchDelaySeconds);
                });
            UIElement keepRow = CreateLinkGuardSettingRow(keepLabel, keepToggle);
            Grid keepGrid = keepRow as Grid;
            if (keepGrid != null && keepGrid.Children.Count > 0)
            {
                keepLabelText = keepGrid.Children[0] as TextBlock;
            }
            content.Children.Add(keepRow);

            Wpf.Ui.Controls.ToggleSwitch maintainToggle =
                (Wpf.Ui.Controls.ToggleSwitch)CreateToggle(rule.MaintainLinkedAppRunning,
                delegate(bool value)
                {
                    UpdateLinkGuardRule(rule, rule.Mode, rule.Enabled, rule.UseGsudo,
                        rule.KeepLinkedAppRunning, value, rule.LaunchDelaySeconds);
                });
            maintainToggle.ToolTip =
                "When off, closing B while A remains open will not restart B until A starts again.";
            UIElement maintainRow = CreateLinkGuardSettingRow("Keep B running while A is open",
                maintainToggle);
            maintainRow.Visibility = rule.Mode == LinkGuardMode.OneWay
                ? Visibility.Visible : Visibility.Collapsed;
            content.Children.Add(maintainRow);
            mode.SelectionChanged += delegate
            {
                ComboBoxItem selected = mode.SelectedItem as ComboBoxItem;
                maintainRow.Visibility = selected != null
                    && string.Equals(Convert.ToString(selected.Tag), "one-way",
                        StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            };

            var delay = CreateSmallComboBox(Z(132));
            int[] delays = { 0, 1, 3, 5, 10, 30, 60 };
            for (int i = 0; i < delays.Length; i++)
            {
                AddComboItem(delay, delays[i] == 0 ? "No delay" : delays[i] + " seconds",
                    delays[i].ToString(), rule.LaunchDelaySeconds.ToString());
            }
            delay.SelectionChanged += delegate
            {
                ComboBoxItem selected = delay.SelectedItem as ComboBoxItem;
                int seconds;
                if (selected == null || !int.TryParse(Convert.ToString(selected.Tag), out seconds)) return;
                UpdateLinkGuardRule(rule, rule.Mode, rule.Enabled, rule.UseGsudo,
                    rule.KeepLinkedAppRunning, rule.MaintainLinkedAppRunning, seconds);
            };
            content.Children.Add(CreateLinkGuardSettingRow("Launch delay", delay));

            return new Border
            {
                Margin = ZThickness(0, 0, 0, 12),
                Padding = ZThickness(14, 14, 14, 6),
                Background = rule.Enabled ? CardBrush : CardDisabledBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Z(8)),
                Child = content
            };
        }

        private UIElement CreateLinkGuardSettingRow(string label, UIElement control)
        {
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            var row = new Grid { Margin = ZThickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = Z(12),
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private void UpdateLinkGuardRule(LinkGuardRule rule, LinkGuardMode mode, bool enabled,
            bool useGsudo, bool keepRunning, bool maintainRunning, int delay)
        {
            LinkGuardActionResult result = controller.UpdateLinkGuardRule(rule.Id, mode, enabled,
                useGsudo, keepRunning, maintainRunning, delay);
            if (!result.Success)
            {
                ShowLinkGuardResult(result);
                return;
            }
            rule.Mode = mode;
            rule.Enabled = enabled;
            rule.UseGsudo = useGsudo;
            rule.KeepLinkedAppRunning = keepRunning;
            rule.MaintainLinkedAppRunning = maintainRunning;
            rule.LaunchDelaySeconds = delay;
        }

        private void ShowLinkGuardResult(LinkGuardActionResult result)
        {
            if (result == null) return;
            StatusTextBlock.Text = result.Message;
            if (!result.Success)
            {
                MessageBox.Show(result.Message, "Link Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            RefreshLinkGuardUi(true);
        }

        private void EnsureLinkGuardListStates()
        {
            if (linkGuardAllAppsListState == null)
            {
                linkGuardAllAppsListState = new ProgressiveListState<AppCatalogItem>(
                    settings.LinkGuard.AllAppsList, delegate(AppCatalogItem item) { return item.Id; });
            }
            if (linkGuardLinkedAppsListState == null)
            {
                linkGuardLinkedAppsListState = new ProgressiveListState<LinkGuardRuleGroup>(
                    settings.LinkGuard.LinkedAppsList, delegate(LinkGuardRuleGroup item) { return item.Id; });
            }
        }

        private void ClearLinkGuardSearchStates()
        {
            if (linkGuardAllAppsListState != null)
            {
                linkGuardAllAppsListState.SearchText = string.Empty;
            }
            if (linkGuardLinkedAppsListState != null)
            {
                linkGuardLinkedAppsListState.SearchText = string.Empty;
            }
        }

        private void AddLinkGuardMessage(Panel target, string title, string description)
        {
            var host = new StackPanel();
            AddCardToPanel(host, title, description, null, false);
            target.Children.Add(host);
        }

        private static GameHelperAppCandidate ToGameHelperCandidate(AppCatalogItem app)
        {
            return new GameHelperAppCandidate
            {
                Id = app.Id,
                Name = app.Name,
                TargetPath = app.TargetPath,
                Publisher = app.Publisher,
                Source = app.Source,
                IconPath = app.IconPath
            };
        }

        private static bool LinkGuardAppMatches(AppCatalogItem app, string query)
        {
            return AppIdentityService.MatchesAppSearch(app.Name, app.Publisher,
                app.Source, app.TargetPath, query);
        }

        private static List<LinkGuardRuleGroup> BuildLinkGuardRuleGroups(List<LinkGuardRule> rules)
        {
            var groups = new List<LinkGuardRuleGroup>();
            var byPath = new Dictionary<string, LinkGuardRuleGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rules.Count; i++)
            {
                LinkGuardRule rule = rules[i];
                string id = AppIdentityService.NormalizeExecutablePath(rule.TriggerApp.TargetPath);
                LinkGuardRuleGroup group;
                if (!byPath.TryGetValue(id, out group))
                {
                    group = new LinkGuardRuleGroup
                    {
                        Id = id,
                        TriggerApp = rule.TriggerApp,
                        Rules = new List<LinkGuardRule>()
                    };
                    byPath[id] = group;
                    groups.Add(group);
                }
                group.Rules.Add(rule);
            }
            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].Rules.Sort(CompareLinkGuardRules);
            }
            return groups;
        }

        private static bool LinkGuardRuleGroupMatches(LinkGuardRuleGroup group, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (AppIdentityService.MatchesAppSearch(group.TriggerApp.Name,
                group.TriggerApp.Publisher, group.TriggerApp.Source,
                group.TriggerApp.TargetPath, query)) return true;
            for (int i = 0; i < group.Rules.Count; i++)
            {
                LinkGuardApp linked = group.Rules[i].LinkedApp;
                if (AppIdentityService.MatchesAppSearch(linked.Name, linked.Publisher,
                    linked.Source, linked.TargetPath, query)) return true;
            }
            return false;
        }

        private static int CompareLinkGuardApps(AppCatalogItem left, AppCatalogItem right)
        {
            return string.Compare(left == null ? string.Empty : left.Name,
                right == null ? string.Empty : right.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        private static int CompareLinkGuardRules(LinkGuardRule left, LinkGuardRule right)
        {
            int value = string.Compare(left == null ? string.Empty : left.TriggerApp.Name,
                right == null ? string.Empty : right.TriggerApp.Name,
                StringComparison.CurrentCultureIgnoreCase);
            if (value != 0) return value;
            return string.Compare(left == null ? string.Empty : left.LinkedApp.Name,
                right == null ? string.Empty : right.LinkedApp.Name,
                StringComparison.CurrentCultureIgnoreCase);
        }

        private static int CompareLinkGuardRuleGroups(LinkGuardRuleGroup left,
            LinkGuardRuleGroup right)
        {
            return string.Compare(left == null ? string.Empty : left.TriggerApp.Name,
                right == null ? string.Empty : right.TriggerApp.Name,
                StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
