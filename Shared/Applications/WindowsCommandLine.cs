using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GuardCenter
{
    internal static class WindowsCommandLine
    {
        public static string Quote(string value)
        {
            if (value == null)
            {
                value = string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(c);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        public static bool TryParse(string commandLine, out string fileName, out string arguments, out string error)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            error = string.Empty;

            string expanded = Environment.ExpandEnvironmentVariables((commandLine ?? string.Empty).Trim());
            if (expanded.Length == 0)
            {
                error = "Command is empty.";
                return false;
            }

            int argc;
            IntPtr argv = CommandLineToArgvW(expanded, out argc);
            if (argv == IntPtr.Zero || argc == 0)
            {
                error = "CommandLineToArgvW failed: " + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                string[] parts = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    IntPtr ptr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    parts[i] = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                }

                fileName = parts[0];
                if (parts.Length > 1)
                {
                    var args = new List<string>();
                    for (int i = 1; i < parts.Length; i++)
                    {
                        args.Add(Quote(parts[i]));
                    }
                    arguments = string.Join(" ", args.ToArray());
                }

                return true;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int numArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
