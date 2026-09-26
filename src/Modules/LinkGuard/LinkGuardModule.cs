using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GuardCenter
{
    internal enum LinkGuardMode
    {
        OneWay,
        Bidirectional
    }

    internal sealed class LinkGuardApp
    {
        public string Name = string.Empty;
        public string TargetPath = string.Empty;
        public string Arguments = string.Empty;
        public string WorkingDirectory = string.Empty;
        public string Publisher = string.Empty;
        public string Source = string.Empty;
        public string IconPath = string.Empty;

        public LinkGuardApp Clone()
        {
            return (LinkGuardApp)MemberwiseClone();
        }
    }

    internal sealed class LinkGuardRule
    {
        public string Id = string.Empty;
        public LinkGuardApp TriggerApp = new LinkGuardApp();
        public LinkGuardApp LinkedApp = new LinkGuardApp();
        public LinkGuardMode Mode = LinkGuardMode.OneWay;
        public bool Enabled = true;
        public bool UseGsudo;
        public bool KeepLinkedAppRunning = true;
        public bool MaintainLinkedAppRunning = true;
        public int LaunchDelaySeconds;
        public string RuntimeStatus = "Waiting for the trigger app.";

        public LinkGuardRule Clone()
        {
            return new LinkGuardRule
            {
                Id = Id,
                TriggerApp = TriggerApp.Clone(),
                LinkedApp = LinkedApp.Clone(),
                Mode = Mode,
                Enabled = Enabled,
                UseGsudo = UseGsudo,
                KeepLinkedAppRunning = KeepLinkedAppRunning,
                MaintainLinkedAppRunning = MaintainLinkedAppRunning,
                LaunchDelaySeconds = LaunchDelaySeconds,
                RuntimeStatus = RuntimeStatus
            };
        }
    }

    internal sealed class LinkGuardActionResult
    {
        public bool Success;
        public string Message = string.Empty;
    }

    internal sealed class LinkGuardModule : IDisposable
    {
        private const int PollIntervalMilliseconds = 1000;
        private const int RetryDelaySeconds = 5;
        private const int MaximumLaunchAttempts = 3;
        private const int CloseRetryMilliseconds = 1000;
        private const int WmClose = 0x0010;
        private const uint GwOwner = 4;
        private static readonly string GsudoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gsudo", "Current", "gsudo.exe");

        private readonly LinkGuardSettings settings;
        private readonly object syncRoot = new object();
        private readonly List<LinkGuardRule> rules;
        private readonly Dictionary<string, RuleRuntime> runtimes =
            new Dictionary<string, RuleRuntime>(StringComparer.OrdinalIgnoreCase);
        private Timer monitorTimer;
        private List<AppCatalogItem> installedApps;
        private int monitorRunning;
        private bool disposed;
        private string statusText = "Link Guard is ready.";

        public LinkGuardModule(LinkGuardSettings settings)
        {
            this.settings = settings ?? new LinkGuardSettings();
            rules = DecodeRules(this.settings.Rules);
            UpdateOverallStatusLocked();
        }

        public event EventHandler StatusChanged;
        public event EventHandler RulesChanged;

        public string StatusText
        {
            get
            {
                lock (syncRoot)
                {
                    return statusText;
                }
            }
        }

        public void Start()
        {
            lock (syncRoot)
            {
                if (disposed || monitorTimer != null)
                {
                    return;
                }
                monitorTimer = new Timer(MonitorTimer_Tick, null, 250, PollIntervalMilliseconds);
            }
        }

        public List<LinkGuardRule> GetRules()
        {
            lock (syncRoot)
            {
                var result = new List<LinkGuardRule>(rules.Count);
                for (int i = 0; i < rules.Count; i++)
                {
                    LinkGuardRule copy = rules[i].Clone();
                    RuleRuntime runtime;
                    if (runtimes.TryGetValue(copy.Id, out runtime))
                    {
                        copy.RuntimeStatus = runtime.StatusText;
                    }
                    result.Add(copy);
                }
                return result;
            }
        }

        public List<AppCatalogItem> GetInstalledApps()
        {
            lock (syncRoot)
            {
                if (installedApps == null)
                {
                    installedApps = new AppCatalogService().GetInstalledApps();
                }
                return new List<AppCatalogItem>(installedApps);
            }
        }

        public void RefreshInstalledApps()
        {
            lock (syncRoot)
            {
                installedApps = null;
            }
        }

        public LinkGuardActionResult AddRule(AppCatalogItem trigger, AppCatalogItem linked)
        {
            if (trigger == null || linked == null)
            {
                return Result(false, "Select both apps before creating a link.");
            }

            LinkGuardApp triggerApp = FromCatalogItem(trigger);
            LinkGuardApp linkedApp = FromCatalogItem(linked);
            string id = CreateRuleId(triggerApp.TargetPath, linkedApp.TargetPath);
            if (string.IsNullOrWhiteSpace(id))
            {
                return Result(false, "Both apps must have a valid executable path.");
            }
            if (PathsEqual(triggerApp.TargetPath, linkedApp.TargetPath))
            {
                return Result(false, "An app cannot be linked to itself.");
            }

            lock (syncRoot)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    LinkGuardRule current = rules[i];
                    if (string.Equals(current.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result(false, "This link already exists.");
                    }
                    if (PathsEqual(current.TriggerApp.TargetPath, linkedApp.TargetPath)
                        && PathsEqual(current.LinkedApp.TargetPath, triggerApp.TargetPath))
                    {
                        return Result(false,
                            "The reverse link already exists. Change that rule to Bidirectional instead.");
                    }
                }

                rules.Add(new LinkGuardRule
                {
                    Id = id,
                    TriggerApp = triggerApp,
                    LinkedApp = linkedApp,
                    Mode = LinkGuardMode.OneWay,
                    Enabled = true,
                    KeepLinkedAppRunning = true,
                    MaintainLinkedAppRunning = true
                });
                PersistRulesLocked();
                UpdateOverallStatusLocked();
            }
            RaiseRulesChanged();
            RaiseStatusChanged();
            return Result(true, triggerApp.Name + " → " + linkedApp.Name + " created.");
        }

        public LinkGuardActionResult RemoveRule(string id)
        {
            bool removed = false;
            lock (syncRoot)
            {
                for (int i = rules.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(rules[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        rules.RemoveAt(i);
                        removed = true;
                    }
                }
                runtimes.Remove(id ?? string.Empty);
                if (removed)
                {
                    PersistRulesLocked();
                    UpdateOverallStatusLocked();
                }
            }
            if (removed)
            {
                RaiseRulesChanged();
                RaiseStatusChanged();
            }
            return Result(removed, removed ? "Link removed." : "Link was not found.");
        }

        public LinkGuardActionResult UpdateRule(string id, LinkGuardMode mode, bool enabled,
            bool useGsudo, bool keepLinkedAppRunning, bool maintainLinkedAppRunning,
            int launchDelaySeconds)
        {
            launchDelaySeconds = Math.Max(0, Math.Min(60, launchDelaySeconds));
            lock (syncRoot)
            {
                LinkGuardRule rule = FindRuleLocked(id);
                if (rule == null)
                {
                    return Result(false, "Link was not found.");
                }
                LinkGuardMode previousMode = rule.Mode;
                bool wasEnabled = rule.Enabled;
                rule.Mode = mode;
                rule.Enabled = enabled;
                rule.UseGsudo = useGsudo;
                rule.KeepLinkedAppRunning = keepLinkedAppRunning;
                rule.MaintainLinkedAppRunning = maintainLinkedAppRunning;
                rule.LaunchDelaySeconds = launchDelaySeconds;
                RuleRuntime runtime;
                if (runtimes.TryGetValue(rule.Id, out runtime))
                {
                    runtime.ResetLaunch();
                    runtime.ResetClose();
                    runtime.PairActive = false;
                    runtime.Stopping = false;
                    if (!enabled || !wasEnabled || previousMode != mode
                        || mode != LinkGuardMode.OneWay)
                    {
                        runtime.LinkedAppWasObservedDuringTriggerSession = false;
                    }
                    if (!enabled || mode != LinkGuardMode.OneWay)
                    {
                        runtime.ClearLinkedAppOwnership();
                    }
                }
                PersistRulesLocked();
                UpdateOverallStatusLocked();
            }
            RaiseRulesChanged();
            RaiseStatusChanged();
            return Result(true, "Link settings updated.");
        }

        public LinkGuardActionResult StartGroup(string id)
        {
            LinkGuardRule rule;
            lock (syncRoot)
            {
                rule = FindRuleLocked(id);
                if (rule == null)
                {
                    return Result(false, "Link was not found.");
                }
                rule = rule.Clone();
                RuleRuntime runtime = GetRuntimeLocked(rule.Id);
                runtime.Stopping = false;
                runtime.ResetLaunch();
                runtime.ResetClose();
            }

            RunningAppSnapshot running = GetRunningApplications();
            bool first = running.Paths.Contains(NormalizePath(rule.TriggerApp.TargetPath));
            string firstMessage = first ? rule.TriggerApp.Name + " is already running." : string.Empty;
            if (!first)
            {
                first = LaunchApp(rule.TriggerApp, rule.UseGsudo, out firstMessage);
            }
            bool secondWasRunning = running.Paths.Contains(NormalizePath(rule.LinkedApp.TargetPath));
            bool second = secondWasRunning;
            string secondMessage = second ? rule.LinkedApp.Name + " is already running." : string.Empty;
            if (!second)
            {
                second = LaunchApp(rule.LinkedApp, rule.UseGsudo, out secondMessage);
            }
            lock (syncRoot)
            {
                RuleRuntime runtime = GetRuntimeLocked(rule.Id);
                runtime.ClearLinkedAppOwnership();
                if (rule.Mode == LinkGuardMode.OneWay && !secondWasRunning && second)
                {
                    runtime.LinkedAppStartedByRule = true;
                }
            }
            AppLog.Write("Link Guard", firstMessage);
            AppLog.Write("Link Guard", secondMessage);
            return Result(first && second, first && second
                ? "Both apps were started."
                : "One or more apps could not be started. Check the Guard Center log.");
        }

        public LinkGuardActionResult StopGroup(string id)
        {
            LinkGuardRule rule;
            lock (syncRoot)
            {
                rule = FindRuleLocked(id);
                if (rule == null)
                {
                    return Result(false, "Link was not found.");
                }
                rule = rule.Clone();
                RuleRuntime runtime = GetRuntimeLocked(rule.Id);
                runtime.Stopping = true;
                runtime.PairActive = false;
                runtime.StatusText = "Stopping both apps.";
            }
            int requested = RequestClose(rule.TriggerApp.TargetPath)
                + RequestClose(rule.LinkedApp.TargetPath);
            RaiseRulesChanged();
            return Result(true, requested == 0
                ? "Neither app had a closable window. The rule remains paused until both apps exit."
                : "A normal close request was sent to the linked apps.");
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                if (monitorTimer != null)
                {
                    monitorTimer.Dispose();
                    monitorTimer = null;
                }
            }
        }

        private void MonitorTimer_Tick(object state)
        {
            if (disposed || Interlocked.Exchange(ref monitorRunning, 1) != 0)
            {
                return;
            }
            bool changed = false;
            try
            {
                RunningAppSnapshot running = GetRunningApplications();
                lock (syncRoot)
                {
                    for (int i = 0; i < rules.Count; i++)
                    {
                        changed |= ReconcileRuleLocked(rules[i], running);
                    }
                    UpdateOverallStatusLocked();
                }
            }
            catch (Exception ex)
            {
                lock (syncRoot)
                {
                    statusText = "Link Guard monitoring failed: " + ex.Message;
                }
                AppLog.Write("Link Guard", ex.ToString());
                changed = true;
            }
            finally
            {
                Interlocked.Exchange(ref monitorRunning, 0);
            }
            if (changed)
            {
                RaiseRulesChanged();
                RaiseStatusChanged();
            }
        }

        private bool ReconcileRuleLocked(LinkGuardRule rule, RunningAppSnapshot running)
        {
            RuleRuntime runtime = GetRuntimeLocked(rule.Id);
            string previousStatus = runtime.StatusText;
            string triggerPath = NormalizePath(rule.TriggerApp.TargetPath);
            string linkedPath = NormalizePath(rule.LinkedApp.TargetPath);
            bool triggerProcessRunning = running.Paths.Contains(triggerPath);
            bool triggerHasWindow = running.WindowedPaths.Contains(triggerPath);
            if (triggerHasWindow)
            {
                runtime.SourceWindowWasObserved = true;
            }
            bool triggerRunning = IsAppConsideredRunning(triggerProcessRunning, triggerHasWindow,
                runtime.SourceWindowWasObserved);
            bool linkedRunning = running.Paths.Contains(linkedPath);

            if (!rule.Enabled)
            {
                runtime.StatusText = "Paused.";
                runtime.ResetLaunch();
                runtime.PairActive = false;
                runtime.ClearLinkedAppOwnership();
                runtime.ResetClose();
                runtime.LinkedAppWasObservedDuringTriggerSession = false;
                runtime.SourceWasRunning = triggerRunning;
                if (!triggerProcessRunning) runtime.SourceWindowWasObserved = false;
                return !string.Equals(previousStatus, runtime.StatusText, StringComparison.Ordinal);
            }

            if (runtime.Stopping)
            {
                if (!triggerRunning && !linkedRunning)
                {
                    runtime.Stopping = false;
                    runtime.StatusText = "Stopped. Waiting for either app.";
                }
                else
                {
                    runtime.StatusText = "Stopping both apps; automatic launch is paused.";
                }
                runtime.ClearLinkedAppOwnership();
                runtime.ResetClose();
                runtime.LinkedAppWasObservedDuringTriggerSession = false;
                runtime.SourceWasRunning = triggerRunning;
                if (!triggerProcessRunning) runtime.SourceWindowWasObserved = false;
                return !string.Equals(previousStatus, runtime.StatusText, StringComparison.Ordinal);
            }

            if (rule.Mode == LinkGuardMode.OneWay)
            {
                if (triggerRunning)
                {
                    runtime.ResetClose();
                    if (linkedRunning)
                    {
                        runtime.ResetLaunch();
                        runtime.LinkedAppWasObservedDuringTriggerSession = true;
                        if (runtime.LinkedAppStartedByRule)
                        {
                            runtime.LinkedAppWasObservedRunning = true;
                        }
                        runtime.StatusText = "Linked — both apps are running.";
                    }
                    else
                    {
                        if (runtime.LinkedAppStartedByRule && runtime.LinkedAppWasObservedRunning)
                        {
                            runtime.ClearLinkedAppOwnership();
                        }
                        if (rule.MaintainLinkedAppRunning
                            || !runtime.LinkedAppWasObservedDuringTriggerSession)
                        {
                            EnsureAppRunningLocked(rule, rule.LinkedApp, linkedPath, running.Paths, runtime);
                        }
                        else
                        {
                            runtime.ResetLaunch();
                            runtime.StatusText = "Linked app was closed; keep-running is off.";
                        }
                    }
                }
                else
                {
                    runtime.ResetLaunch();
                    runtime.LinkedAppWasObservedDuringTriggerSession = false;
                    if (runtime.ClosingLinkedApp)
                    {
                        if (!linkedRunning)
                        {
                            runtime.ResetClose();
                            runtime.ClearLinkedAppOwnership();
                            runtime.StatusText = "Linked app closed. Waiting for "
                                + rule.TriggerApp.Name + ".";
                        }
                        else
                        {
                            int requested = RequestCloseIfDue(rule.LinkedApp.TargetPath, runtime);
                            runtime.StatusText = requested > 0
                                ? "Trigger app closed; closing the linked app."
                                : requested == 0
                                    ? "Trigger app closed; waiting for a closable linked-app window."
                                    : "Trigger app closed; waiting for the linked app to exit.";
                        }
                    }
                    else if (ShouldCloseLinkedAppAfterTriggerExit(runtime.SourceWasRunning, linkedRunning,
                        !rule.KeepLinkedAppRunning))
                    {
                        runtime.ClosingLinkedApp = true;
                        int requested = RequestCloseIfDue(rule.LinkedApp.TargetPath, runtime);
                        runtime.StatusText = requested > 0
                            ? "Trigger app closed; closing the linked app."
                            : "Trigger app closed; waiting for a closable linked-app window.";
                    }
                    else
                    {
                        if (!linkedRunning)
                        {
                            runtime.ClearLinkedAppOwnership();
                        }
                        runtime.StatusText = linkedRunning
                            ? "Linked app is running independently."
                            : "Waiting for " + rule.TriggerApp.Name + ".";
                    }
                }
                runtime.SourceWasRunning = triggerRunning;
                if (!triggerProcessRunning) runtime.SourceWindowWasObserved = false;
            }
            else
            {
                runtime.ResetClose();
                runtime.ClearLinkedAppOwnership();
                if (triggerRunning && linkedRunning)
                {
                    runtime.PairActive = true;
                    runtime.ResetLaunch();
                    runtime.StatusText = "Linked — both apps are running.";
                }
                else if (!triggerRunning && !linkedRunning)
                {
                    runtime.PairActive = false;
                    runtime.ResetLaunch();
                    runtime.StatusText = "Waiting for either app.";
                }
                else if (runtime.PairActive && !rule.KeepLinkedAppRunning)
                {
                    runtime.Stopping = true;
                    runtime.PairActive = false;
                    RequestClose(triggerRunning ? rule.TriggerApp.TargetPath : rule.LinkedApp.TargetPath);
                    runtime.StatusText = "One app closed; closing the other app.";
                }
                else if (!triggerRunning)
                {
                    EnsureAppRunningLocked(rule, rule.TriggerApp, triggerPath, running.Paths, runtime);
                }
                else
                {
                    EnsureAppRunningLocked(rule, rule.LinkedApp, linkedPath, running.Paths, runtime);
                }
                runtime.SourceWasRunning = triggerRunning;
            }
            return !string.Equals(previousStatus, runtime.StatusText, StringComparison.Ordinal);
        }

        internal static bool ShouldCloseLinkedAppAfterTriggerExit(bool triggerWasRunning,
            bool linkedAppRunning, bool closeLinkedAppAfterTriggerExit)
        {
            return closeLinkedAppAfterTriggerExit && triggerWasRunning && linkedAppRunning;
        }

        internal static bool IsAppConsideredRunning(bool processRunning, bool hasTopLevelWindow,
            bool topLevelWindowWasObserved)
        {
            return processRunning && (!topLevelWindowWasObserved || hasTopLevelWindow);
        }

        internal static bool HasTopLevelWindow(string executablePath)
        {
            return GetRunningApplications().WindowedPaths.Contains(NormalizePath(executablePath));
        }

        private static int RequestCloseIfDue(string executablePath, RuleRuntime runtime)
        {
            DateTime now = DateTime.UtcNow;
            if (runtime.LastCloseAttemptUtc != DateTime.MinValue
                && (now - runtime.LastCloseAttemptUtc).TotalMilliseconds < CloseRetryMilliseconds)
            {
                return -1;
            }
            runtime.LastCloseAttemptUtc = now;
            runtime.CloseAttemptCount++;
            int requested = RequestClose(executablePath);
            AppLog.Write("Link Guard", requested > 0
                ? "Sent WM_CLOSE to " + requested + " linked-app window(s): " + executablePath
                : "No closable linked-app window found yet: " + executablePath);
            return requested;
        }

        private void EnsureAppRunningLocked(LinkGuardRule rule, LinkGuardApp app, string normalizedPath,
            HashSet<string> running, RuleRuntime runtime)
        {
            DateTime now = DateTime.UtcNow;
            if (!string.Equals(runtime.PendingPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                runtime.PendingPath = normalizedPath;
                runtime.PendingSinceUtc = now;
                runtime.LastAttemptUtc = DateTime.MinValue;
                runtime.AttemptCount = 0;
            }

            double pendingSeconds = (now - runtime.PendingSinceUtc).TotalSeconds;
            if (pendingSeconds < rule.LaunchDelaySeconds)
            {
                int remaining = Math.Max(1, rule.LaunchDelaySeconds - (int)pendingSeconds);
                runtime.StatusText = "Starting " + app.Name + " in " + remaining + "s.";
                return;
            }
            if (runtime.AttemptCount >= MaximumLaunchAttempts)
            {
                runtime.StatusText = "Could not start " + app.Name + " after "
                    + MaximumLaunchAttempts + " attempts.";
                return;
            }
            if (runtime.LastAttemptUtc != DateTime.MinValue
                && (now - runtime.LastAttemptUtc).TotalSeconds < RetryDelaySeconds)
            {
                runtime.StatusText = "Waiting for " + app.Name + " to start.";
                return;
            }

            runtime.LastAttemptUtc = now;
            runtime.AttemptCount++;
            bool started = LaunchApp(app, rule.UseGsudo, out string message);
            AppLog.Write("Link Guard", message);
            runtime.StatusText = started
                ? "Starting " + app.Name + "."
                : "Failed to start " + app.Name + "; retry " + runtime.AttemptCount
                    + " of " + MaximumLaunchAttempts + ".";
            if (started)
            {
                running.Add(normalizedPath);
                if (rule.Mode == LinkGuardMode.OneWay
                    && PathsEqual(app.TargetPath, rule.LinkedApp.TargetPath))
                {
                    runtime.LinkedAppStartedByRule = true;
                    runtime.LinkedAppWasObservedRunning = false;
                }
            }
        }

        private static bool LaunchApp(LinkGuardApp app, bool preferGsudo, out string message)
        {
            string path = NormalizePath(app == null ? string.Empty : app.TargetPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                message = "Executable not found: " + path;
                return false;
            }

            bool useGsudo = preferGsudo && IsGsudoCacheAvailable();
            try
            {
                ProcessStartInfo startInfo;
                if (useGsudo)
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = GsudoPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = ResolveWorkingDirectory(app, path)
                    };
                    startInfo.ArgumentList.Add("--direct");
                    startInfo.ArgumentList.Add(path);
                    AppendArguments(startInfo, app.Arguments);
                }
                else
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = app.Arguments ?? string.Empty,
                        WorkingDirectory = ResolveWorkingDirectory(app, path),
                        UseShellExecute = true
                    };
                }
                Process process = Process.Start(startInfo);
                if (process != null)
                {
                    process.Dispose();
                }
                message = "Started " + app.Name + (useGsudo
                    ? " through the active gsudo cache." : " with normal Windows launch behavior.");
                return true;
            }
            catch (Exception ex)
            {
                message = "Failed to start " + app.Name + ": " + ex.Message;
                return false;
            }
        }

        private static void AppendArguments(ProcessStartInfo startInfo, string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return;
            }
            // Preserve catalog arguments as one command-line tail. Most Start Menu entries use
            // a single switch or URI; complex quoting remains exactly as stored by Windows.
            startInfo.Arguments = "--direct " + QuoteArgument(startInfo.ArgumentList[1]) + " " + arguments;
            startInfo.ArgumentList.Clear();
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string ResolveWorkingDirectory(LinkGuardApp app, string executablePath)
        {
            string requested = app == null ? string.Empty : app.WorkingDirectory;
            if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
            {
                return requested;
            }
            return Path.GetDirectoryName(executablePath) ?? string.Empty;
        }

        private static bool IsGsudoCacheAvailable()
        {
            if (!File.Exists(GsudoPath))
            {
                return false;
            }
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = GsudoPath,
                    Arguments = "status CacheAvailable --no-output",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (process == null || !process.WaitForExit(2500))
                    {
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static int RequestClose(string executablePath)
        {
            string normalized = NormalizePath(executablePath);
            string processName = AppIdentityService.GetProcessNameFromPath(normalized);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return 0;
            }
            int requested = 0;
            var matchingProcessIds = new HashSet<uint>();
            var mainWindowsAlreadyRequested = new HashSet<IntPtr>();
            Process[] processes = Process.GetProcessesByName(processName);
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    string actualPath;
                    int error;
                    if (!GameInputLanguageNative.TryGetProcessPath((uint)process.Id, out actualPath, out error)
                        || !PathsEqual(actualPath, normalized))
                    {
                        continue;
                    }
                    matchingProcessIds.Add((uint)process.Id);
                    try
                    {
                        IntPtr mainWindow = process.MainWindowHandle;
                        if (process.CloseMainWindow())
                        {
                            requested++;
                            if (mainWindow != IntPtr.Zero)
                            {
                                mainWindowsAlreadyRequested.Add(mainWindow);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (matchingProcessIds.Count == 0)
            {
                return 0;
            }

            EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
            {
                uint processId;
                GetWindowThreadProcessId(hwnd, out processId);
                if (!matchingProcessIds.Contains(processId) || !IsWindowVisible(hwnd)
                    || GetWindow(hwnd, GwOwner) != IntPtr.Zero
                    || mainWindowsAlreadyRequested.Contains(hwnd))
                {
                    return true;
                }
                try
                {
                    if (PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
                    {
                        requested++;
                    }
                }
                catch
                {
                }
                return true;
            }, IntPtr.Zero);
            return requested;
        }

        private static RunningAppSnapshot GetRunningApplications()
        {
            var result = new RunningAppSnapshot();
            var pathsByProcessId = new Dictionary<uint, string>();
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    try
                    {
                        string path;
                        int error;
                        if (GameInputLanguageNative.TryGetProcessPath((uint)process.Id, out path, out error))
                        {
                            path = NormalizePath(path);
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                result.Paths.Add(path);
                                pathsByProcessId[(uint)process.Id] = path;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                {
                    return true;
                }
                uint processId;
                GetWindowThreadProcessId(hwnd, out processId);
                string path;
                if (pathsByProcessId.TryGetValue(processId, out path))
                {
                    result.WindowedPaths.Add(path);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private LinkGuardRule FindRuleLocked(string id)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                if (string.Equals(rules[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return rules[i];
                }
            }
            return null;
        }

        private RuleRuntime GetRuntimeLocked(string id)
        {
            RuleRuntime runtime;
            if (!runtimes.TryGetValue(id, out runtime))
            {
                runtime = new RuleRuntime();
                runtimes[id] = runtime;
            }
            return runtime;
        }

        private void PersistRulesLocked()
        {
            settings.Rules = EncodeRules(rules);
        }

        private void UpdateOverallStatusLocked()
        {
            int enabled = 0;
            int active = 0;
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].Enabled)
                {
                    enabled++;
                }
                RuleRuntime runtime;
                if (runtimes.TryGetValue(rules[i].Id, out runtime)
                    && runtime.StatusText.StartsWith("Linked", StringComparison.Ordinal))
                {
                    active++;
                }
            }
            statusText = rules.Count == 0
                ? "Link Guard is ready; no app links are configured."
                : active + " active link" + (active == 1 ? string.Empty : "s") + " · "
                    + enabled + " enabled of " + rules.Count + ".";
        }

        private static LinkGuardApp FromCatalogItem(AppCatalogItem item)
        {
            return new LinkGuardApp
            {
                Name = item.Name,
                TargetPath = NormalizePath(item.TargetPath),
                Arguments = item.Arguments,
                WorkingDirectory = item.WorkingDirectory,
                Publisher = item.Publisher,
                Source = item.Source,
                IconPath = item.IconPath
            };
        }

        private static string CreateRuleId(string triggerPath, string linkedPath)
        {
            string trigger = NormalizePath(triggerPath);
            string linked = NormalizePath(linkedPath);
            if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(linked))
            {
                return string.Empty;
            }
            string value = trigger.ToUpperInvariant() + "\n" + linked.ToUpperInvariant();
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            using (System.Security.Cryptography.SHA256 sha =
                System.Security.Cryptography.SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(bytes));
            }
        }

        private static string NormalizePath(string path)
        {
            return AppIdentityService.NormalizeExecutablePath(path);
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(NormalizePath(left), NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static List<LinkGuardRule> DecodeRules(string encoded)
        {
            var result = new List<LinkGuardRule>();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return result;
            }
            string[] records = encoded.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < records.Length; i++)
            {
                try
                {
                    string text = Encoding.UTF8.GetString(Convert.FromBase64String(records[i]));
                    string[] p = text.Split('\t');
                    if (p.Length < 19)
                    {
                        continue;
                    }
                    var rule = new LinkGuardRule
                    {
                        Mode = string.Equals(p[0], "two-way", StringComparison.OrdinalIgnoreCase)
                            ? LinkGuardMode.Bidirectional : LinkGuardMode.OneWay,
                        Enabled = ParseBool(p[1], true),
                        UseGsudo = ParseBool(p[2], false),
                        KeepLinkedAppRunning = ParseBool(p[3], true),
                        MaintainLinkedAppRunning = p.Length >= 20 ? ParseBool(p[19], true) : true,
                        LaunchDelaySeconds = ParseInt(p[4], 0, 0, 60),
                        TriggerApp = DecodeApp(p, 5),
                        LinkedApp = DecodeApp(p, 12)
                    };
                    rule.Id = CreateRuleId(rule.TriggerApp.TargetPath, rule.LinkedApp.TargetPath);
                    if (!string.IsNullOrWhiteSpace(rule.Id)
                        && !PathsEqual(rule.TriggerApp.TargetPath, rule.LinkedApp.TargetPath))
                    {
                        result.Add(rule);
                    }
                }
                catch
                {
                }
            }
            return result;
        }

        internal static string EncodeRules(IList<LinkGuardRule> rules)
        {
            var records = new List<string>();
            if (rules == null)
            {
                return string.Empty;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                LinkGuardRule rule = rules[i];
                var fields = new List<string>
                {
                    rule.Mode == LinkGuardMode.Bidirectional ? "two-way" : "one-way",
                    rule.Enabled ? "1" : "0",
                    rule.UseGsudo ? "1" : "0",
                    rule.KeepLinkedAppRunning ? "1" : "0",
                    Math.Max(0, Math.Min(60, rule.LaunchDelaySeconds)).ToString()
                };
                EncodeApp(fields, rule.TriggerApp);
                EncodeApp(fields, rule.LinkedApp);
                fields.Add(rule.MaintainLinkedAppRunning ? "1" : "0");
                records.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    string.Join("\t", fields.ToArray()))));
            }
            return string.Join(",", records.ToArray());
        }

        private static void EncodeApp(List<string> fields, LinkGuardApp app)
        {
            fields.Add(Clean(app.Name));
            fields.Add(Clean(NormalizePath(app.TargetPath)));
            fields.Add(Clean(app.Arguments));
            fields.Add(Clean(app.WorkingDirectory));
            fields.Add(Clean(app.Publisher));
            fields.Add(Clean(app.Source));
            fields.Add(Clean(app.IconPath));
        }

        private static LinkGuardApp DecodeApp(string[] p, int offset)
        {
            return new LinkGuardApp
            {
                Name = p[offset],
                TargetPath = NormalizePath(p[offset + 1]),
                Arguments = p[offset + 2],
                WorkingDirectory = p[offset + 3],
                Publisher = p[offset + 4],
                Source = p[offset + 5],
                IconPath = p[offset + 6]
            };
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ')
                .Replace('\n', ' ').Trim();
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static int ParseInt(string value, int fallback, int min, int max)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? Math.Max(min, Math.Min(max, parsed)) : fallback;
        }

        private static LinkGuardActionResult Result(bool success, string message)
        {
            return new LinkGuardActionResult { Success = success, Message = message };
        }

        private void RaiseStatusChanged()
        {
            EventHandler handler = StatusChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseRulesChanged()
        {
            EventHandler handler = RulesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private sealed class RunningAppSnapshot
        {
            public readonly HashSet<string> Paths =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> WindowedPaths =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class RuleRuntime
        {
            public bool SourceWasRunning;
            public bool SourceWindowWasObserved;
            public bool PairActive;
            public bool Stopping;
            public bool LinkedAppStartedByRule;
            public bool LinkedAppWasObservedRunning;
            public bool LinkedAppWasObservedDuringTriggerSession;
            public bool ClosingLinkedApp;
            public DateTime LastCloseAttemptUtc;
            public int CloseAttemptCount;
            public string PendingPath = string.Empty;
            public DateTime PendingSinceUtc;
            public DateTime LastAttemptUtc;
            public int AttemptCount;
            public string StatusText = "Waiting for the trigger app.";

            public void ResetLaunch()
            {
                PendingPath = string.Empty;
                PendingSinceUtc = DateTime.MinValue;
                LastAttemptUtc = DateTime.MinValue;
                AttemptCount = 0;
            }

            public void ClearLinkedAppOwnership()
            {
                LinkedAppStartedByRule = false;
                LinkedAppWasObservedRunning = false;
            }

            public void ResetClose()
            {
                ClosingLinkedApp = false;
                LastCloseAttemptUtc = DateTime.MinValue;
                CloseAttemptCount = 0;
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    }
}
