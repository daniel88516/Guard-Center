using System;
using System.Collections.Generic;

namespace GuardCenter
{
    internal enum AppActionType
    {
        Manage,
        Run,
        RunAsAdministrator,
        Restart,
        Terminate,
        OpenFileLocation,
        CopyPath,
        Uninstall,
        ModifyOrRepair
    }

    internal sealed class AppCatalogItem
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string TargetPath = string.Empty;
        public string Arguments = string.Empty;
        public string WorkingDirectory = string.Empty;
        public string IconPath = string.Empty;
        public string Publisher = string.Empty;
        public string Source = string.Empty;
        public string ShortcutPath = string.Empty;
        public string InstallLocation = string.Empty;
        public string UninstallCommand = string.Empty;
        public string ModifyCommand = string.Empty;
        public string LogicalId = string.Empty;
        public int OriginalOrder;

        public AppCatalogItem Clone()
        {
            return new AppCatalogItem
            {
                Id = Id,
                Name = Name,
                TargetPath = TargetPath,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                IconPath = IconPath,
                Publisher = Publisher,
                Source = Source,
                ShortcutPath = ShortcutPath,
                InstallLocation = InstallLocation,
                UninstallCommand = UninstallCommand,
                ModifyCommand = ModifyCommand,
                LogicalId = LogicalId,
                OriginalOrder = OriginalOrder
            };
        }
    }

    internal sealed class AppCatalogDetail
    {
        public AppCatalogItem App;
        public bool Loaded;
        public bool IsRunning;
        public List<int> ProcessIds = new List<int>();
        public string Version = string.Empty;
        public string FileDescription = string.Empty;
        public string CompanyName = string.Empty;
        public string ProductName = string.Empty;
        public string InstallLocation = string.Empty;
        public string LastWriteText = string.Empty;
        public string FileSizeText = string.Empty;
        public string Error = string.Empty;
    }

    internal sealed class AppActionRequest
    {
        public AppActionType Action = AppActionType.Manage;
        public string TargetPath = string.Empty;
        public string ShortcutPath = string.Empty;
        public string AppId = string.Empty;
        public bool FromShell;

        public AppActionRequest Clone()
        {
            return new AppActionRequest
            {
                Action = Action,
                TargetPath = TargetPath,
                ShortcutPath = ShortcutPath,
                AppId = AppId,
                FromShell = FromShell
            };
        }
    }

    internal sealed class AppActionResult
    {
        public bool Success;
        public bool Cancelled;
        public string Message = string.Empty;

        public static AppActionResult Ok(string message)
        {
            return new AppActionResult { Success = true, Message = message };
        }

        public static AppActionResult Fail(string message)
        {
            return new AppActionResult { Success = false, Message = message };
        }

        public static AppActionResult Cancel(string message)
        {
            return new AppActionResult { Success = false, Cancelled = true, Message = message };
        }
    }
}
