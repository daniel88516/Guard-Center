using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace GuardCenter
{
    internal static class ExplorerIntegrationService
    {
        private const string VerbPrefix = "GuardCenter.AppGuard.";

        public static bool IsEnabled()
        {
            return IsVerbRegistered(@"Software\Classes\exefile\shell\" + VerbPrefix + "Manage")
                && IsVerbRegistered(@"Software\Classes\lnkfile\shell\" + VerbPrefix + "Manage");
        }

        public static void Enable(string iconPath = null)
        {
            string exePath = GetCurrentExecutablePath();
            string resolvedIconPath = string.IsNullOrWhiteSpace(iconPath) ? exePath : iconPath;
            RegisterForKind(@"Software\Classes\exefile\shell", exePath, resolvedIconPath);
            RegisterForKind(@"Software\Classes\lnkfile\shell", exePath, resolvedIconPath);
        }

        public static void Disable()
        {
            DeleteForKind(@"Software\Classes\exefile\shell");
            DeleteForKind(@"Software\Classes\lnkfile\shell");
        }

        private static void RegisterForKind(string shellPath, string exePath, string iconPath)
        {
            DeleteForKind(shellPath);
            RegisterVerb(shellPath, "Manage", "Guard Center", exePath, iconPath);
        }

        private static void RegisterVerb(string shellPath, string suffix, string title, string exePath,
            string iconPath)
        {
            string verbPath = shellPath + "\\" + VerbPrefix + suffix;
            using (RegistryKey verb = Registry.CurrentUser.CreateSubKey(verbPath))
            {
                verb.SetValue(string.Empty, title, RegistryValueKind.String);
                verb.SetValue("Icon", iconPath, RegistryValueKind.String);
            }

            using (RegistryKey command = Registry.CurrentUser.CreateSubKey(verbPath + "\\command"))
            {
                string args = AppActionCommandLine.BuildManageArgs("%1");
                command.SetValue(string.Empty, WindowsCommandLine.Quote(exePath) + " " + args, RegistryValueKind.String);
            }
        }

        private static void DeleteForKind(string shellPath)
        {
            string[] suffixes = new string[]
            {
                "Manage",
                "Run",
                "RunAsAdmin",
                "Restart",
                "Terminate",
                "Location",
                "CopyPath",
                "Uninstall"
            };

            for (int i = 0; i < suffixes.Length; i++)
            {
                Registry.CurrentUser.DeleteSubKeyTree(shellPath + "\\" + VerbPrefix + suffixes[i], false);
            }
        }

        private static bool IsVerbRegistered(string verbPath)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(verbPath, false))
            {
                return key != null;
            }
        }

        private static string GetCurrentExecutablePath()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    if (process.MainModule != null && File.Exists(process.MainModule.FileName))
                    {
                        return process.MainModule.FileName;
                    }
                }
            }
            catch
            {
            }

            return AppPaths.InstalledExePath;
        }
    }
}
