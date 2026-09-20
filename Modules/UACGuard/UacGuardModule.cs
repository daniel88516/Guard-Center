using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class UacGuardStatus
    {
        public bool CodexInstalled;
        public string CodexVersion = string.Empty;
        public string CodexExecutable = string.Empty;
        public bool RunnerInstalled;
        public bool RunnerAclProtected;
        public bool CodexIsRunning;
        public bool GsudoInstalled;
        public string GsudoVersion = string.Empty;
        public bool GsudoSessionActive;
        public int GsudoTargetProcessId;
        public string GsudoSessionMessage = string.Empty;
        public string GsudoSessionMode = string.Empty;
        public bool LifecycleTaskInstalled;
        public bool LifecycleTaskConfigurationValid;
        public readonly List<string> Issues = new List<string>();

        public bool IsReady
        {
            get
            {
                return CodexInstalled && GsudoInstalled && LifecycleTaskConfigurationValid
                    && RunnerInstalled && RunnerAclProtected;
            }
        }

        public string StatusText
        {
            get
            {
                if (IsReady)
                {
                    if (GsudoSessionActive)
                    {
                        if (string.Equals(GsudoSessionMode, UacGuardModule.GuardCenterLifecycleMode,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return "UAC Guard active for the Guard Center lifecycle.";
                        }
                        return "UAC Guard active; gsudo is bound to Codex PID "
                            + GsudoTargetProcessId + ".";
                    }
                    return CodexIsRunning
                        ? "UAC Guard is ready and preparing automatic authorization."
                        : "UAC Guard is ready and waiting for the selected lifecycle target.";
                }
                return "UAC Guard requires installation or repair.";
            }
        }
    }

    internal sealed class UacGuardActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
    }

    internal sealed class UacGuardModule
    {
        internal const string LegacyTaskName = "Guard Center - Codex Elevated Launcher";
        internal const string LifecycleTaskName = "Guard Center - gsudo Lifecycle Cache";
        internal const string ElevatedActionSwitch = "--uac-guard-action";
        internal const string LifecycleHostSwitch = "--uac-guard-lifecycle-cache";
        internal const string CodexProcessMode = "codex-process";
        internal const string GuardCenterLifecycleMode = "guard-center-lifecycle";
        private const int ChildProcessTimeoutMilliseconds = 30000;
        private const int ElevatedActionTimeoutMilliseconds = 60000;
        private const int ElevatedHostTimeoutMilliseconds = 420000;
        private const int WingetInstallTimeoutMilliseconds = 300000;
        private const string LifecycleHostFileName = "GuardCenter.UacGuardHost.exe";
        private const string GsudoWingetPackageId = "gerardog.gsudo";
        private const int GsudoCacheMinutes = 30;

        private static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Guard Center", "UAC Guard");
        private static readonly string ProtectedLifecycleHostPath = Path.Combine(InstallDirectory,
            LifecycleHostFileName);
        private static readonly string LegacyShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Codex (Administrator).lnk");
        private static readonly string LegacyStartMenuShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Codex (Administrator).lnk");
        private static readonly string LegacyPinnedShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer",
            "Quick Launch", "User Pinned", "TaskBar", "Codex (Administrator).lnk");
        private static readonly string StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guard Center", "UAC Guard");
        private static readonly string LegacyTaskbarLauncherPath = Path.Combine(StateDirectory,
            "GuardCenter.CodexAdminLauncher.exe");
        private static readonly string LegacyTaskbarIdentityPath = Path.Combine(StateDirectory, "taskbar-appid.txt");
        private static readonly string ResultPath = Path.Combine(StateDirectory, "last-action.json");
        private static readonly string ProgressPath = Path.Combine(StateDirectory, "progress.log");
        private static readonly string GsudoSessionPath = Path.Combine(StateDirectory, "gsudo-session.json");
        private static readonly string GsudoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gsudo", "Current", "gsudo.exe");

        internal static string NormalizeAuthorizationMode(string mode)
        {
            return string.Equals(mode, GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                ? GuardCenterLifecycleMode : CodexProcessMode;
        }

        public async Task<UacGuardStatus> GetStatusAsync()
        {
            var status = new UacGuardStatus();
            status.CodexIsRunning = Process.GetProcessesByName("ChatGPT").Length > 0;
            ApplyGsudoStatus(status);
            status.GsudoInstalled = File.Exists(GsudoPath);
            if (status.GsudoInstalled)
            {
                try
                {
                    status.GsudoVersion = FileVersionInfo.GetVersionInfo(GsudoPath).ProductVersion ?? string.Empty;
                }
                catch
                {
                }
            }
            else
            {
                status.Issues.Add("找不到官方 gsudo 提權引擎。");
            }

            CodexPackageInfo package = await FindCodexPackageAsync();
            if (package != null)
            {
                status.CodexInstalled = true;
                status.CodexVersion = package.Version;
                status.CodexExecutable = package.Executable;
            }
            else
            {
                status.Issues.Add("找不到 OpenAI.Codex AppX 套件。");
            }

            ScheduledTaskInfo lifecycleTask = await GetScheduledTaskInfoAsync(LifecycleTaskName);
            if (lifecycleTask != null)
            {
                status.LifecycleTaskInstalled = true;
                status.LifecycleTaskConfigurationValid = string.Equals(lifecycleTask.RunLevel, "Highest",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(lifecycleTask.Execute, ProtectedLifecycleHostPath,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals((lifecycleTask.Arguments ?? string.Empty).Trim(), LifecycleHostSwitch,
                        StringComparison.OrdinalIgnoreCase);
                if (!status.LifecycleTaskConfigurationValid)
                {
                    status.Issues.Add("自動授權排程內容不符合預期，請執行修復。");
                }
            }
            else
            {
                status.Issues.Add("尚未建立自動授權排程，請執行安裝或修復。");
            }

            status.RunnerInstalled = AreProtectedHostFilesInstalled();
            if (!status.RunnerInstalled)
            {
                status.Issues.Add("受保護的自動授權監護程式不存在。");
            }
            status.RunnerAclProtected = status.RunnerInstalled && await IsRunnerAclProtectedAsync();
            if (status.RunnerInstalled && !status.RunnerAclProtected)
            {
                status.Issues.Add("授權監護程式目錄可被一般使用者寫入，請執行修復。");
            }

            return status;
        }

        public async Task<UacGuardActionResult> RequestElevatedActionAsync(string action)
        {
            if (string.Equals(action, "install", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "repair", StringComparison.OrdinalIgnoreCase))
            {
                CodexPackageInfo package = await FindCodexPackageAsync();
                if (package == null)
                {
                    return Result(false, "找不到 OpenAI.Codex AppX 套件。");
                }
            }

            if (IsCurrentProcessElevated())
            {
                int directCode = await ExecuteElevatedActionAsync(action);
                return ReadLastActionResult(directCode);
            }

            string host = Path.Combine(AppContext.BaseDirectory, "GuardCenter.UacGuardHost.exe");
            if (!File.Exists(host))
            {
                return Result(false, "找不到 UAC Guard 系統管理員 Host，請重新建置或修復 Guard Center。");
            }

            try
            {
                try
                {
                    if (File.Exists(ResultPath))
                    {
                        File.Delete(ResultPath);
                    }
                    if (File.Exists(ProgressPath))
                    {
                        File.Delete(ProgressPath);
                    }
                }
                catch
                {
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = host,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add(ElevatedActionSwitch);
                startInfo.ArgumentList.Add(action);
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return Result(false, "無法啟動系統管理員處理程序。");
                    }
                    Task wait = process.WaitForExitAsync();
                    if (await Task.WhenAny(wait, Task.Delay(ElevatedHostTimeoutMilliseconds)) != wait)
                    {
                        return Result(false, "系統管理員安裝程序超過 7 分鐘沒有完成。介面已解除鎖定；請查看操作紀錄後再重試。");
                    }
                    await wait;
                    return ReadLastActionResult(process.ExitCode);
                }
            }
            catch (Win32Exception exception)
            {
                if (exception.NativeErrorCode == 1223)
                {
                    return Result(false, "已取消 Windows UAC 授權，系統沒有變更。");
                }
                return Result(false, exception.Message);
            }
            catch (Exception exception)
            {
                return Result(false, exception.Message);
            }
        }

        public async Task<UacGuardActionResult> StartGsudoSessionAsync()
        {
            try
            {
                int codexPid = await FindCodexAppServerProcessIdAsync();
                if (codexPid <= 0)
                {
                    return Result(false, "尚未偵測到 Codex app-server；UAC Guard 會在 Codex 啟動後自動授權。");
                }

                GsudoSessionState existing = ReadGsudoSessionState();
                if (existing != null && existing.Ready
                    && string.Equals(existing.Mode, CodexProcessMode, StringComparison.OrdinalIgnoreCase)
                    && existing.TargetProcessId == codexPid && IsProcessAlive(codexPid)
                    && HasGsudoProcess())
                {
                    return Result(true, existing.Message);
                }

                var session = new GsudoSessionState
                {
                    Mode = CodexProcessMode,
                    TargetProcessId = codexPid,
                    StartedAt = DateTimeOffset.Now,
                    CacheMinutes = -1,
                    Ready = false,
                    Message = "正在自動綁定 Codex PID " + codexPid + "。"
                };
                return await StartScheduledGsudoSessionAsync(session, "Codex 行程週期");
            }
            catch (Exception exception)
            {
                return Result(false, exception.Message);
            }
        }

        public async Task<UacGuardActionResult> StartLifecycleGsudoSessionAsync()
        {
            try
            {
                GsudoSessionState existing = ReadGsudoSessionState();
                if (existing != null && existing.Ready
                    && string.Equals(existing.Mode, GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                    && existing.OwnerProcessId == Environment.ProcessId
                    && IsProcessAlive(existing.OwnerProcessId) && HasGsudoProcess())
                {
                    return Result(true, existing.Message);
                }

                var session = new GsudoSessionState
                {
                    Mode = GuardCenterLifecycleMode,
                    OwnerProcessId = Environment.ProcessId,
                    StartedAt = DateTimeOffset.Now,
                    CacheMinutes = -1,
                    Ready = false,
                    Message = "正在建立 Guard Center 生命週期管理員授權。"
                };
                return await StartScheduledGsudoSessionAsync(session, "Guard Center 生命週期");
            }
            catch (Exception exception)
            {
                return Result(false, exception.Message);
            }
        }

        private static async Task<UacGuardActionResult> StartScheduledGsudoSessionAsync(
            GsudoSessionState session, string modeLabel)
        {
            if (!File.Exists(GsudoPath))
            {
                return Result(false, "找不到 gsudo。請先安裝官方套件。");
            }

            ScheduledTaskInfo task = await GetScheduledTaskInfoAsync(LifecycleTaskName);
            bool taskValid = task != null
                && string.Equals(task.RunLevel, "Highest", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.Execute, ProtectedLifecycleHostPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals((task.Arguments ?? string.Empty).Trim(), LifecycleHostSwitch,
                    StringComparison.OrdinalIgnoreCase);
            if (!taskValid)
            {
                return Result(false, "自動授權排程尚未安裝；請先執行「修復／重新安裝」。");
            }

            await RunProcessCaptureAsync(GsudoPath, "-k", 15000);
            await RunProcessCaptureAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/End /TN \"" + LifecycleTaskName + "\"", 15000);
            await Task.Delay(350);
            WriteGsudoSessionState(session);

            ProcessResult launch = await RunProcessCaptureAsync(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/Run /TN \"" + LifecycleTaskName + "\"", 15000);
            if (launch.ExitCode != 0)
            {
                DeleteGsudoSessionStateForSession(session);
                return Result(false, string.IsNullOrWhiteSpace(launch.Output)
                    ? "無法啟動自動授權排程。" : launch.Output.Trim());
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                GsudoSessionState current = ReadGsudoSessionState();
                if (SessionIdentityMatches(current, session) && current.Ready)
                {
                    return Result(true, current.Message);
                }
            }

            DeleteGsudoSessionStateForSession(session);
            return Result(false, modeLabel + "排程已啟動，但 10 秒內沒有回報 gsudo 授權就緒。");
        }

        public async Task<UacGuardActionResult> StopGsudoSessionAsync()
        {
            try
            {
                if (File.Exists(GsudoPath))
                {
                    ProcessResult stop = await RunProcessCaptureAsync(GsudoPath, "-k", 15000);
                    if (stop.ExitCode != 0)
                    {
                        return Result(false, string.IsNullOrWhiteSpace(stop.Output)
                            ? "無法終止 gsudo 快取。" : stop.Output.Trim());
                    }
                }
                if (File.Exists(GsudoSessionPath))
                {
                    File.Delete(GsudoSessionPath);
                }
                return Result(true, "gsudo 管理員工作階段已終止；下一次提權需要重新授權。");
            }
            catch (Exception exception)
            {
                return Result(false, exception.Message);
            }
        }

        public void OpenTaskScheduler()
        {
            Process.Start(new ProcessStartInfo { FileName = "taskschd.msc", UseShellExecute = true });
        }

        public void OpenInstallDirectory()
        {
            if (!Directory.Exists(InstallDirectory))
            {
                throw new DirectoryNotFoundException("UAC Guard 尚未建立受保護的授權監護程式目錄。");
            }
            Process.Start(new ProcessStartInfo { FileName = InstallDirectory, UseShellExecute = true });
        }

        public static bool TryHandleCommandLine(string[] args)
        {
            if (args != null && args.Length == 1
                && string.Equals(args[0], LifecycleHostSwitch, StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = RunScheduledCacheHostAsync().GetAwaiter().GetResult();
                return true;
            }

            if (args == null || args.Length != 2
                || !string.Equals(args[0], ElevatedActionSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Environment.ExitCode = ExecuteElevatedActionAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }

        private static async Task<int> RunScheduledCacheHostAsync()
        {
            if (!IsCurrentProcessElevated() || !File.Exists(GsudoPath))
            {
                return 5;
            }

            GsudoSessionState session = ReadGsudoSessionState();
            bool lifecycleMode = session != null && string.Equals(session.Mode,
                GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase);
            bool codexMode = session != null && string.Equals(session.Mode,
                CodexProcessMode, StringComparison.OrdinalIgnoreCase);
            bool targetValid = lifecycleMode
                ? session.OwnerProcessId > 0 && IsProcessAlive(session.OwnerProcessId)
                : codexMode && session.TargetProcessId > 0 && IsProcessAlive(session.TargetProcessId);
            if (!targetValid)
            {
                return 6;
            }

            try
            {
                string cacheArguments = lifecycleMode
                    ? "cache on -p 0 -d -1"
                    : "cache on -p " + session.TargetProcessId + " -d -1";
                ProcessResult cache = await RunProcessCaptureAsync(GsudoPath,
                    cacheArguments, ElevatedActionTimeoutMilliseconds);
                if (cache.ExitCode != 0)
                {
                    session.Message = string.IsNullOrWhiteSpace(cache.Output)
                        ? "無法建立自動 gsudo 快取。" : cache.Output.Trim();
                    WriteGsudoSessionState(session);
                    return 7;
                }

                session.Ready = true;
                session.Message = lifecycleMode
                    ? "已自動授權；Guard Center 完全退出時會立即撤銷。"
                    : "已自動授權並限定給 Codex PID " + session.TargetProcessId
                        + " 與其子程序；Codex 結束時會立即撤銷。";
                WriteGsudoSessionState(session);

                while (lifecycleMode ? IsProcessAlive(session.OwnerProcessId)
                    : IsProcessAlive(session.TargetProcessId))
                {
                    await Task.Delay(500);
                    GsudoSessionState current = ReadGsudoSessionState();
                    if (!SessionIdentityMatches(current, session))
                    {
                        break;
                    }
                }
                return 0;
            }
            finally
            {
                try
                {
                    await RunProcessCaptureAsync(GsudoPath, "-k", 15000);
                }
                catch
                {
                }
                DeleteGsudoSessionStateForSession(session);
            }
        }

        private static async Task<int> ExecuteElevatedActionAsync(string action)
        {
            Directory.CreateDirectory(StateDirectory);
            WriteProgress("Elevated host started: " + action);
            if (!IsCurrentProcessElevated())
            {
                WriteActionResult(Result(false, "UAC Guard 管理操作沒有取得系統管理員權限。"));
                return 5;
            }

            try
            {
                if (string.Equals(action, "install", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "repair", StringComparison.OrdinalIgnoreCase))
                {
                    await InstallOrRepairAsync();
                    WriteActionResult(Result(true,
                        "gsudo 與 UAC Guard 自動授權元件已完成安裝／修復。"));
                    return 0;
                }
                if (string.Equals(action, "uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    await UninstallAsync();
                    WriteActionResult(Result(true,
                        "已移除 UAC Guard 的排程、受保護監護程式與舊版 Codex Admin 遺留物；Codex 本體與設定未被刪除。"));
                    return 0;
                }
                throw new InvalidOperationException("不支援的 UAC Guard 管理操作：" + action);
            }
            catch (Exception exception)
            {
                WriteActionResult(Result(false, exception.ToString()));
                return 1;
            }
        }

        private static async Task InstallOrRepairAsync()
        {
            await EnsureGsudoInstalledAsync();

            WriteProgress("Resolving validated Codex package path.");
            CodexPackageInfo package = await FindCodexPackageAsync();
            if (package == null)
            {
                throw new InvalidOperationException("找不到 OpenAI.Codex AppX 套件。");
            }

            string codexCli = Path.Combine(Directory.GetParent(package.Executable).FullName, "resources", "codex.exe");
            string signatureScript = "foreach ($path in @(" + QuotePowerShell(package.Executable) + ","
                + QuotePowerShell(codexCli) + ")) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { exit 8 }; "
                + "$signature = Get-AuthenticodeSignature -LiteralPath $path; "
                + "if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or "
                + "$signature.SignerCertificate.Subject -notmatch 'OpenAI OpCo, LLC') { exit 9 } }";
            WriteProgress("Verifying OpenAI Authenticode signature.");
            ProcessResult signature = await RunPowerShellAsync(signatureScript);
            if (signature.ExitCode != 0)
            {
                throw new InvalidOperationException("Codex/ChatGPT 執行檔的 OpenAI 數位簽章驗證失敗。");
            }

            WriteProgress("Installing protected automatic authorization host.");
            await StopProtectedLifecycleHostAsync();
            await CleanupLegacyCodexAdminArtifactsAsync();
            Directory.CreateDirectory(InstallDirectory);
            CopyProtectedHostFiles();

            WriteProgress("Applying authorization host ACL.");
            ProcessResult acl = await RunProcessCaptureAsync(
                Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                "\"" + InstallDirectory + "\" /inheritance:r /grant:r "
                + "*S-1-5-18:(OI)(CI)(F) *S-1-5-32-544:(OI)(CI)(F) *S-1-5-32-545:(OI)(CI)(RX)");
            if (acl.ExitCode != 0)
            {
                throw new InvalidOperationException("無法保護授權監護程式 ACL：" + acl.Output);
            }

            WriteProgress("Registering Guard Center lifecycle cache task.");
            string lifecycleTaskScript = "$action = New-ScheduledTaskAction -Execute "
                + QuotePowerShell(ProtectedLifecycleHostPath) + " -Argument "
                + QuotePowerShell(LifecycleHostSwitch) + "; "
                + "$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) "
                + "-LogonType Interactive -RunLevel Highest; "
                + "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries "
                + "-MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero); "
                + "$task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings; "
                + "Register-ScheduledTask -TaskName " + QuotePowerShell(LifecycleTaskName)
                + " -InputObject $task -Force | Out-Null";
            ProcessResult lifecycleTask = await RunPowerShellAsync(lifecycleTaskScript);
            if (lifecycleTask.ExitCode != 0)
            {
                throw new InvalidOperationException("無法建立 UAC Guard 自動授權排程："
                    + lifecycleTask.Output);
            }

            WriteProgress("Install completed.");
        }

        private static async Task StopProtectedLifecycleHostAsync()
        {
            await RunProcessCaptureAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/End /TN \"" + LifecycleTaskName + "\"", 15000);
            if (File.Exists(GsudoPath))
            {
                await RunProcessCaptureAsync(GsudoPath, "-k", 15000);
            }

            if (await WaitForProtectedLifecycleHostExitAsync(3000))
            {
                return;
            }

            WriteProgress("Protected authorization host is still running; terminating the exact installed image.");
            foreach (Process process in GetProtectedLifecycleHostProcesses())
            {
                using (process)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }

            if (!await WaitForProtectedLifecycleHostExitAsync(5000))
            {
                throw new IOException("受保護的 UAC Guard 監護程式仍在執行，無法安全更新檔案。請重新啟動 Guard Center 後再試一次。");
            }
        }

        private static async Task<bool> WaitForProtectedLifecycleHostExitAsync(int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            do
            {
                List<Process> processes = GetProtectedLifecycleHostProcesses();
                bool found = processes.Count > 0;
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
                if (!found)
                {
                    return true;
                }
                await Task.Delay(150);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        private static List<Process> GetProtectedLifecycleHostProcesses()
        {
            var matches = new List<Process>();
            Process[] candidates = Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(LifecycleHostFileName));
            foreach (Process process in candidates)
            {
                try
                {
                    string path = process.MainModule?.FileName ?? string.Empty;
                    if (process.Id != Environment.ProcessId && string.Equals(
                        path, ProtectedLifecycleHostPath, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }
            return matches;
        }

        private static async Task EnsureGsudoInstalledAsync()
        {
            if (File.Exists(GsudoPath))
            {
                WriteProgress("Official gsudo installation already exists.");
                return;
            }

            string wingetPath = FindWingetPath();
            if (string.IsNullOrWhiteSpace(wingetPath))
            {
                throw new InvalidOperationException(
                    "找不到 Windows Package Manager (winget)。請先透過 Microsoft Store 安裝或更新「應用程式安裝程式」，再按一次「一鍵安裝／修復」。");
            }

            WriteProgress("Installing official gsudo package through winget.");
            string arguments = GetGsudoWingetInstallArguments();
            ProcessResult install = await RunProcessCaptureAsync(
                wingetPath, arguments, WingetInstallTimeoutMilliseconds);

            if (!File.Exists(GsudoPath))
            {
                string details = install.Output.Trim();
                if (string.IsNullOrWhiteSpace(details))
                {
                    details = "winget 結束代碼：" + install.ExitCode;
                }
                throw new InvalidOperationException(
                    "無法安裝官方 gsudo 套件。" + Environment.NewLine + details);
            }

            WriteProgress("Official gsudo package installed successfully.");
        }

        internal static string GetGsudoWingetInstallArguments()
        {
            return "install --id " + GsudoWingetPackageId
                + " --exact --source winget --force --silent --accept-package-agreements"
                + " --accept-source-agreements --disable-interactivity";
        }

        private static string FindWingetPath()
        {
            string userAlias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(userAlias))
            {
                return userAlias;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory, "winget.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
            return string.Empty;
        }

        private static async Task UninstallAsync()
        {
            await RunProcessCaptureAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/End /TN \"" + LifecycleTaskName + "\"", 15000);
            if (File.Exists(GsudoPath))
            {
                await RunProcessCaptureAsync(GsudoPath, "-k", 15000);
            }
            await RunPowerShellAsync("Unregister-ScheduledTask -TaskName " + QuotePowerShell(LifecycleTaskName)
                + " -Confirm:$false -ErrorAction SilentlyContinue");
            await CleanupLegacyCodexAdminArtifactsAsync();
            if (Directory.Exists(InstallDirectory))
            {
                string actual = Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Guard Center", "UAC Guard").TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("拒絕移除非預期路徑：" + actual);
                }
                Directory.Delete(actual, true);
            }
        }

        private static void CopyProtectedHostFiles()
        {
            string[] names =
            {
                "GuardCenter.UacGuardHost.exe",
                "GuardCenter.UacGuardHost.dll",
                "GuardCenter.UacGuardHost.deps.json",
                "GuardCenter.UacGuardHost.runtimeconfig.json",
                "GuardCenter.UacGuardHost.core.dll"
            };
            foreach (string name in names)
            {
                string source = Path.Combine(AppContext.BaseDirectory, name);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("找不到 UAC Guard 自動授權元件，請重新建置 Guard Center。", source);
                }
                File.Copy(source, Path.Combine(InstallDirectory, name), true);
            }
        }

        private static bool AreProtectedHostFilesInstalled()
        {
            string[] names =
            {
                "GuardCenter.UacGuardHost.exe",
                "GuardCenter.UacGuardHost.dll",
                "GuardCenter.UacGuardHost.deps.json",
                "GuardCenter.UacGuardHost.runtimeconfig.json",
                "GuardCenter.UacGuardHost.core.dll"
            };
            foreach (string name in names)
            {
                if (!File.Exists(Path.Combine(InstallDirectory, name)))
                {
                    return false;
                }
            }
            return true;
        }

        private static async Task CleanupLegacyCodexAdminArtifactsAsync()
        {
            await RunProcessCaptureAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/End /TN \"" + LegacyTaskName + "\"", 15000);
            await Task.Delay(250);

            if (File.Exists(LegacyTaskbarLauncherPath))
            {
                try
                {
                    await RunProcessCaptureAsync(LegacyTaskbarLauncherPath,
                        "--unpin " + QuoteCommandLineArgument(LegacyStartMenuShortcutPath), 10000);
                    if (File.Exists(LegacyTaskbarIdentityPath))
                    {
                        string identity = File.ReadAllText(LegacyTaskbarIdentityPath).Trim();
                        if (!string.IsNullOrWhiteSpace(identity))
                        {
                            await RunProcessCaptureAsync(LegacyTaskbarLauncherPath,
                                "--unpin-aumid " + QuoteCommandLineArgument(identity), 10000);
                        }
                    }
                }
                catch
                {
                }
            }

            await RunPowerShellAsync("Unregister-ScheduledTask -TaskName " + QuotePowerShell(LegacyTaskName)
                + " -Confirm:$false -ErrorAction SilentlyContinue");

            DeleteFileIfPresent(LegacyShortcutPath);
            DeleteFileIfPresent(LegacyStartMenuShortcutPath);
            DeleteFileIfPresent(LegacyPinnedShortcutPath);

            string[] legacyNames =
            {
                "GuardCenter.CodexAdminLauncher.exe",
                "GuardCenter.CodexAdminLauncher.dll",
                "GuardCenter.CodexAdminLauncher.deps.json",
                "GuardCenter.CodexAdminLauncher.runtimeconfig.json",
                "Microsoft.Windows.SDK.NET.dll",
                "WinRT.Runtime.dll",
                "taskbar-appid.txt",
                "CodexAdministrator.ico",
                "session-status.json"
            };
            foreach (string name in legacyNames)
            {
                DeleteFileIfPresent(Path.Combine(StateDirectory, name));
                DeleteFileIfPresent(Path.Combine(InstallDirectory, name));
            }
            DeleteFileIfPresent(Path.Combine(InstallDirectory, "Start-CodexElevated.ps1"));
        }

        private static void DeleteFileIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static async Task<CodexPackageInfo> FindCodexPackageAsync()
        {
            const string script = "$package = Get-AppxPackage -Name 'OpenAI.Codex' | Sort-Object Version -Descending | Select-Object -First 1; "
                + "if ($package) { [pscustomobject]@{ Version=$package.Version.ToString(); "
                + "Executable=(Join-Path $package.InstallLocation 'app\\ChatGPT.exe') } | ConvertTo-Json -Compress }";
            ProcessResult result = await RunPowerShellAsync(script);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                AppLog.Write("UAC Guard", "Codex package query failed (exit " + result.ExitCode + "): " + result.Output);
                return null;
            }
            try
            {
                using (JsonDocument document = JsonDocument.Parse(result.StandardOutput.Trim()))
                {
                    JsonElement root = document.RootElement;
                    string version = root.GetProperty("Version").GetString() ?? string.Empty;
                    string executable = root.GetProperty("Executable").GetString() ?? string.Empty;
                    if (!File.Exists(executable))
                    {
                        return null;
                    }
                    return new CodexPackageInfo
                    {
                        Version = version,
                        Executable = executable
                    };
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("UAC Guard", "Codex package query returned invalid JSON: " + exception.Message
                    + " | " + result.StandardOutput);
                return null;
            }
        }

        private static async Task<ScheduledTaskInfo> GetScheduledTaskInfoAsync(string taskName)
        {
            string script = "$task = Get-ScheduledTask -TaskName " + QuotePowerShell(taskName)
                + " -ErrorAction SilentlyContinue; if ($task) { [pscustomobject]@{ "
                + "RunLevel=$task.Principal.RunLevel.ToString(); Execute=$task.Actions[0].Execute; "
                + "Arguments=$task.Actions[0].Arguments } | ConvertTo-Json -Compress }";
            ProcessResult result = await RunPowerShellAsync(script);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<ScheduledTaskInfo>(result.StandardOutput.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static Task<bool> IsRunnerAclProtectedAsync()
        {
            try
            {
                var acl = new DirectorySecurity(InstallDirectory, AccessControlSections.Access);
                var dangerousSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "S-1-1-0",
                    "S-1-5-11",
                    "S-1-5-32-545"
                };
                AuthorizationRuleCollection rules = acl.GetAccessRules(
                    true, true, typeof(SecurityIdentifier));
                foreach (AuthorizationRule authorizationRule in rules)
                {
                    if (authorizationRule is not FileSystemAccessRule rule
                        || rule.AccessControlType != AccessControlType.Allow
                        || rule.IdentityReference is not SecurityIdentifier sid
                        || !dangerousSids.Contains(sid.Value))
                    {
                        continue;
                    }
                    if ((rule.FileSystemRights & FileSystemRights.WriteData) != 0)
                    {
                        return Task.FromResult(false);
                    }
                }
                return Task.FromResult(acl.AreAccessRulesProtected);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void ApplyGsudoStatus(UacGuardStatus status)
        {
            try
            {
                GsudoSessionState session = ReadGsudoSessionState();
                if (session == null)
                {
                    return;
                }

                bool lifecycleMode = string.Equals(session.Mode, GuardCenterLifecycleMode,
                    StringComparison.OrdinalIgnoreCase);
                bool targetAlive = lifecycleMode
                    ? session.OwnerProcessId > 0 && IsProcessAlive(session.OwnerProcessId)
                    : session.TargetProcessId > 0 && IsProcessAlive(session.TargetProcessId);
                Process[] cacheProcesses = Process.GetProcessesByName("gsudo");
                bool cacheProcessAlive = cacheProcesses.Length > 0;
                for (int i = 0; i < cacheProcesses.Length; i++)
                {
                    cacheProcesses[i].Dispose();
                }
                status.GsudoTargetProcessId = session.TargetProcessId;
                status.GsudoSessionMode = lifecycleMode ? GuardCenterLifecycleMode : CodexProcessMode;
                status.GsudoSessionActive = targetAlive && cacheProcessAlive && session.Ready;
                status.GsudoSessionMessage = status.GsudoSessionActive
                    ? (session.Message ?? string.Empty)
                    : (lifecycleMode
                        ? "Guard Center 生命週期授權尚未就緒或已撤銷。"
                        : "先前的 gsudo 工作階段已失效或 Codex PID 已結束。");
            }
            catch
            {
            }
        }

        private static GsudoSessionState ReadGsudoSessionState()
        {
            try
            {
                return File.Exists(GsudoSessionPath)
                    ? JsonSerializer.Deserialize<GsudoSessionState>(File.ReadAllText(GsudoSessionPath))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteGsudoSessionState(GsudoSessionState session)
        {
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(GsudoSessionPath, JsonSerializer.Serialize(session), Encoding.UTF8);
        }

        private static bool SessionIdentityMatches(GsudoSessionState current, GsudoSessionState expected)
        {
            if (current == null || expected == null
                || !string.Equals(current.Mode, expected.Mode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return string.Equals(expected.Mode, GuardCenterLifecycleMode, StringComparison.OrdinalIgnoreCase)
                ? current.OwnerProcessId == expected.OwnerProcessId
                : current.TargetProcessId == expected.TargetProcessId;
        }

        private static void DeleteGsudoSessionStateForSession(GsudoSessionState expected)
        {
            try
            {
                GsudoSessionState current = ReadGsudoSessionState();
                if (SessionIdentityMatches(current, expected) && File.Exists(GsudoSessionPath))
                {
                    File.Delete(GsudoSessionPath);
                }
            }
            catch
            {
            }
        }

        private static async Task<int> FindCodexAppServerProcessIdAsync()
        {
            const string script = "$process = Get-CimInstance Win32_Process -Filter \"Name='codex.exe'\" | "
                + "Where-Object { $_.CommandLine -match '(^|\\s)app-server(\\s|$)' } | "
                + "Sort-Object CreationDate -Descending | Select-Object -First 1; "
                + "if ($process) { [string]$process.ProcessId }";
            ProcessResult result = await RunPowerShellAsync(script);
            int processId;
            return result.ExitCode == 0
                && int.TryParse(result.StandardOutput.Trim(), out processId) ? processId : 0;
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool HasGsudoProcess()
        {
            Process[] processes = Process.GetProcessesByName("gsudo");
            bool found = processes.Length > 0;
            for (int i = 0; i < processes.Length; i++)
            {
                processes[i].Dispose();
            }
            return found;
        }

        private static async Task<ProcessResult> RunPowerShellAsync(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return await RunProcessCaptureAsync(PowerShellPath,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
        }

        private static async Task<ProcessResult> RunProcessCaptureAsync(string fileName, string arguments,
            int timeoutMilliseconds = ChildProcessTimeoutMilliseconds)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("無法啟動：" + fileName);
                }
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                Task wait = process.WaitForExitAsync();
                if (await Task.WhenAny(wait, Task.Delay(timeoutMilliseconds)) != wait)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                    }
                    throw new TimeoutException("外部程序超過 " + (timeoutMilliseconds / 1000)
                        + " 秒沒有完成：" + Path.GetFileName(fileName));
                }
                await wait;
                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = await output,
                    StandardError = await error
                };
            }
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string QuoteCommandLineArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static UacGuardActionResult Result(bool success, string message)
        {
            return new UacGuardActionResult
            {
                Success = success,
                Message = message ?? string.Empty,
                Timestamp = DateTimeOffset.Now
            };
        }

        private static void WriteActionResult(UacGuardActionResult result)
        {
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(ResultPath, JsonSerializer.Serialize(result));
        }

        private static void WriteProgress(string message)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                File.AppendAllText(ProgressPath,
                    DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static UacGuardActionResult ReadLastActionResult(int exitCode)
        {
            try
            {
                if (File.Exists(ResultPath))
                {
                    UacGuardActionResult result = JsonSerializer.Deserialize<UacGuardActionResult>(File.ReadAllText(ResultPath));
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
            catch
            {
            }
            return Result(exitCode == 0, exitCode == 0
                ? "UAC Guard 管理操作完成。" : "UAC Guard 管理操作失敗，結束代碼：" + exitCode);
        }

        private static string PowerShellPath
        {
            get
            {
                return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            }
        }

        private sealed class CodexPackageInfo
        {
            public string Version { get; set; } = string.Empty;
            public string Executable { get; set; } = string.Empty;
        }

        private sealed class ScheduledTaskInfo
        {
            public string RunLevel { get; set; } = string.Empty;
            public string Execute { get; set; } = string.Empty;
            public string Arguments { get; set; } = string.Empty;
        }

        private sealed class GsudoSessionState
        {
            public string Mode { get; set; } = CodexProcessMode;
            public int TargetProcessId { get; set; }
            public int OwnerProcessId { get; set; }
            public DateTimeOffset StartedAt { get; set; }
            public int CacheMinutes { get; set; }
            public bool Ready { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string StandardOutput = string.Empty;
            public string StandardError = string.Empty;
            public string Output
            {
                get { return StandardOutput + StandardError; }
            }
        }
    }
}
