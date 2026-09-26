using System;

namespace GuardCenter
{
    internal static class AppActionCommandLine
    {
        public const string ManageTargetArg = "--app-guard-target";

        public static bool TryParse(string[] args, out AppActionRequest request, out string error)
        {
            request = null;
            error = string.Empty;
            if (args == null || args.Length == 0)
            {
                return false;
            }

            string target = string.Empty;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ManageTargetArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    target = args[++i];
                }
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            request = new AppActionRequest
            {
                Action = AppActionType.Manage,
                TargetPath = target ?? string.Empty,
                FromShell = true
            };
            return true;
        }

        public static string BuildManageArgs(string targetPath)
        {
            return ManageTargetArg + " " + WindowsCommandLine.Quote(targetPath ?? string.Empty);
        }
    }
}
