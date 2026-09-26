using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace GuardCenter
{
    internal sealed class AppActionService
    {
        private readonly Func<Dictionary<string, List<int>>> runningProcessSnapshotProvider;

        public AppActionService()
            : this(null)
        {
        }

        internal AppActionService(Func<Dictionary<string, List<int>>> runningProcessSnapshotProvider)
        {
            this.runningProcessSnapshotProvider = runningProcessSnapshotProvider
                ?? ReadRunningProcessSnapshot;
        }

        public AppActionResult Execute(AppCatalogItem app, AppActionType action)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.TargetPath))
            {
                return AppActionResult.Fail("No application target is available.");
            }
            if (IsBlockedSelfAction(app, action))
            {
                return AppActionResult.Fail("Guard Center cannot terminate or restart its current process from App Guard.");
            }

            switch (action)
            {
                case AppActionType.Run:
                    return Run(app, false);
                case AppActionType.RunAsAdministrator:
                    return Run(app, true);
                case AppActionType.Restart:
                    return Restart(app);
                case AppActionType.Terminate:
                    return Terminate(app);
                case AppActionType.OpenFileLocation:
                    return OpenFileLocation(app);
                case AppActionType.Uninstall:
                    return RunCommand(app.UninstallCommand, "uninstaller");
                case AppActionType.ModifyOrRepair:
                    return RunCommand(app.ModifyCommand, "modify/repair command");
                default:
                    return AppActionResult.Fail("Unsupported action.");
            }
        }

        internal static bool IsCurrentApplication(AppCatalogItem app)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.TargetPath))
            {
                return false;
            }

            string currentPath = string.Empty;
            try
            {
                currentPath = Environment.ProcessPath;
            }
            catch
            {
            }

            return !string.IsNullOrWhiteSpace(currentPath)
                && string.Equals(AppIdentityService.NormalizeExecutablePath(app.TargetPath),
                    AppIdentityService.NormalizeExecutablePath(currentPath), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsBlockedSelfAction(AppCatalogItem app, AppActionType action)
        {
            return IsCurrentApplication(app)
                && (action == AppActionType.Terminate || action == AppActionType.Restart);
        }

        public AppCatalogDetail LoadDetail(AppCatalogItem app)
        {
            Dictionary<string, List<int>> runningProcesses = runningProcessSnapshotProvider();
            return LoadDetailCore(app, runningProcesses);
        }

        public List<AppCatalogDetail> LoadDetails(IList<AppCatalogItem> apps)
        {
            var result = new List<AppCatalogDetail>();
            if (apps == null || apps.Count == 0)
            {
                return result;
            }

            Dictionary<string, List<int>> runningProcesses = runningProcessSnapshotProvider();
            for (int i = 0; i < apps.Count; i++)
            {
                result.Add(LoadDetailCore(apps[i], runningProcesses));
            }
            return result;
        }

        private static AppCatalogDetail LoadDetailCore(AppCatalogItem app,
            Dictionary<string, List<int>> runningProcesses)
        {
            var detail = new AppCatalogDetail { App = app, Loaded = true };
            if (app == null)
            {
                detail.Error = "No application selected.";
                return detail;
            }

            try
            {
                string normalizedPath = AppIdentityService.NormalizeExecutablePath(app.TargetPath);
                List<int> processIds;
                if (!string.IsNullOrWhiteSpace(normalizedPath) && runningProcesses != null
                    && runningProcesses.TryGetValue(normalizedPath, out processIds))
                {
                    detail.IsRunning = processIds.Count > 0;
                    detail.ProcessIds.AddRange(processIds);
                }

                if (File.Exists(app.TargetPath))
                {
                    string description;
                    string company;
                    string product;
                    detail.Version = AppIdentityService.GetFileVersion(app.TargetPath, out description, out company, out product);
                    detail.FileDescription = description;
                    detail.CompanyName = string.IsNullOrWhiteSpace(app.Publisher) ? company : app.Publisher;
                    detail.ProductName = product;

                    var info = new FileInfo(app.TargetPath);
                    detail.LastWriteText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                    detail.FileSizeText = AppIdentityService.FormatBytes(info.Length);
                }

                detail.InstallLocation = !string.IsNullOrWhiteSpace(app.InstallLocation)
                    ? app.InstallLocation
                    : (Path.GetDirectoryName(app.TargetPath) ?? string.Empty);
            }
            catch (Exception ex)
            {
                detail.Error = ex.Message;
            }

            return detail;
        }

        private static Dictionary<string, List<int>> ReadRunningProcessSnapshot()
        {
            var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                try
                {
                    ProcessModule module = process.MainModule;
                    string path = module == null
                        ? string.Empty
                        : AppIdentityService.NormalizeExecutablePath(module.FileName);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    List<int> processIds;
                    if (!result.TryGetValue(path, out processIds))
                    {
                        processIds = new List<int>();
                        result[path] = processIds;
                    }
                    processIds.Add(process.Id);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return result;
        }

        public List<Process> FindMatchingProcesses(string executablePath)
        {
            var result = new List<Process>();
            string normalized = AppIdentityService.NormalizeExecutablePath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return result;
            }

            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                bool matched = false;
                try
                {
                    ProcessModule module = process.MainModule;
                    string path = module == null ? string.Empty : module.FileName;
                    matched = string.Equals(AppIdentityService.NormalizeExecutablePath(path),
                        normalized, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    matched = false;
                }

                if (matched)
                {
                    result.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }

            return result;
        }

        private AppActionResult Run(AppCatalogItem app, bool elevated)
        {
            try
            {
                if (!File.Exists(app.TargetPath))
                {
                    return AppActionResult.Fail("Executable was not found: " + app.TargetPath);
                }

                var info = new ProcessStartInfo
                {
                    FileName = app.TargetPath,
                    Arguments = app.Arguments ?? string.Empty,
                    WorkingDirectory = Directory.Exists(app.WorkingDirectory)
                        ? app.WorkingDirectory
                        : (Path.GetDirectoryName(app.TargetPath) ?? string.Empty),
                    UseShellExecute = true
                };
                if (elevated)
                {
                    info.Verb = "runas";
                }

                Process.Start(info);
                return AppActionResult.Ok((elevated ? "Started as administrator: " : "Started: ") + app.Name);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    return AppActionResult.Cancel("Administrator approval was canceled.");
                }

                return AppActionResult.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return AppActionResult.Fail(ex.Message);
            }
        }

        private AppActionResult Restart(AppCatalogItem app)
        {
            AppActionResult terminate = Terminate(app);
            if (!terminate.Success && !terminate.Cancelled)
            {
                return terminate;
            }

            return Run(app, false);
        }

        private AppActionResult Terminate(AppCatalogItem app)
        {
            List<Process> processes = FindMatchingProcesses(app.TargetPath);
            if (processes.Count == 0)
            {
                return AppActionResult.Ok("No matching running process was found.");
            }

            int killed = 0;
            int failed = 0;
            for (int i = 0; i < processes.Count; i++)
            {
                using (Process process = processes[i])
                {
                    try
                    {
                        process.Kill();
                        killed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }

            if (failed > 0)
            {
                return AppActionResult.Fail("Terminated " + killed + " process(es), " + failed + " failed.");
            }

            return AppActionResult.Ok("Terminated " + killed + " process(es).");
        }

        private AppActionResult OpenFileLocation(AppCatalogItem app)
        {
            try
            {
                if (!File.Exists(app.TargetPath))
                {
                    return AppActionResult.Fail("Executable was not found: " + app.TargetPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select," + WindowsCommandLine.Quote(app.TargetPath),
                    UseShellExecute = false
                });
                return AppActionResult.Ok("Opened file location.");
            }
            catch (Exception ex)
            {
                return AppActionResult.Fail(ex.Message);
            }
        }

        private AppActionResult RunCommand(string command, string label)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return AppActionResult.Fail("No " + label + " is available.");
            }

            string fileName;
            string arguments;
            string error;
            if (!WindowsCommandLine.TryParse(command, out fileName, out arguments, out error))
            {
                return AppActionResult.Fail(error);
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true
                });
                return AppActionResult.Ok("Started " + label + ".");
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    return AppActionResult.Cancel("Administrator approval was canceled.");
                }

                return AppActionResult.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return AppActionResult.Fail(ex.Message);
            }
        }
    }
}
