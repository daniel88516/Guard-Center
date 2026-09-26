using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GuardCenter
{
    internal static class AppIdentityService
    {
        public static string CreateExecutableId(string executablePath)
        {
            string normalized = NormalizeExecutablePath(executablePath);
            return string.IsNullOrWhiteSpace(normalized)
                ? string.Empty
                : "exe:" + normalized.ToUpperInvariant();
        }

        public static string NormalizeExecutablePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string path = Environment.ExpandEnvironmentVariables(value.Trim());
            if (path.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = path.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    path = path.Substring(1, endQuote - 1);
                }
            }

            path = path.Trim().Trim('"');
            if (path.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                return string.Empty;
            }

            return path;
        }

        public static string NormalizeFolderPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                return string.Empty;
            }

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string NormalizeIconPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string path = value.Trim();
            int comma = path.LastIndexOf(',');
            if (comma >= 0)
            {
                path = path.Substring(0, comma);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return NormalizeExecutablePath(path);
        }

        public static string GetProcessNameFromPath(string executablePath)
        {
            string normalized = NormalizeExecutablePath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            return Path.GetFileNameWithoutExtension(normalized);
        }

        public static string CleanDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string result = value.Trim();
            string[] suffixes = new string[] { ".lnk", ".url", ".exe" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (result.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Substring(0, result.Length - suffixes[i].Length).Trim();
                }
            }

            return result;
        }

        public static string CleanField(string value)
        {
            return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        public static int CompareName(AppCatalogItem left, AppCatalogItem right)
        {
            string leftName = string.IsNullOrWhiteSpace(left == null ? string.Empty : left.Name)
                ? "(unnamed)"
                : left.Name;
            string rightName = string.IsNullOrWhiteSpace(right == null ? string.Empty : right.Name)
                ? "(unnamed)"
                : right.Name;

            int value = string.Compare(leftName, rightName, StringComparison.CurrentCultureIgnoreCase);
            if (value != 0)
            {
                return value;
            }

            value = string.Compare(left == null ? string.Empty : left.Publisher,
                right == null ? string.Empty : right.Publisher, StringComparison.CurrentCultureIgnoreCase);
            if (value != 0)
            {
                return value;
            }

            return string.Compare(left == null ? string.Empty : left.TargetPath,
                right == null ? string.Empty : right.TargetPath, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Matches(AppCatalogItem item, string query)
        {
            return MatchesAppSearch(
                item == null ? string.Empty : item.Name,
                item == null ? string.Empty : item.Publisher,
                item == null ? string.Empty : item.Source,
                item == null ? string.Empty : item.TargetPath,
                query);
        }

        public static bool MatchesAppSearch(string name, string publisher, string source,
            string targetPath, string query)
        {
            string normalized = query == null ? string.Empty : query.Trim();
            if (normalized.Length == 0)
            {
                return true;
            }

            // A single character must visibly narrow an app list. Hidden metadata such as
            // A common installation-directory prefix otherwise makes single letters appear
            // to do nothing.
            if (normalized.Length == 1)
            {
                return ContainsIgnoreCase(name, normalized);
            }

            return ContainsIgnoreCase(name, normalized)
                || ContainsIgnoreCase(publisher, normalized)
                || ContainsIgnoreCase(source, normalized)
                || ContainsIgnoreCase(targetPath, normalized);
        }

        public static bool ContainsIgnoreCase(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return (text ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        public static string GetFileVersion(string path, out string description, out string company, out string product)
        {
            description = string.Empty;
            company = string.Empty;
            product = string.Empty;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                description = info.FileDescription ?? string.Empty;
                company = info.CompanyName ?? string.Empty;
                product = info.ProductName ?? string.Empty;
                return info.FileVersion ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string FormatBytes(long bytes)
        {
            double value = bytes;
            string[] units = new string[] { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024.0;
                unit++;
            }

            return unit == 0 ? bytes + " " + units[unit] : value.ToString("0.0") + " " + units[unit];
        }
    }
}
