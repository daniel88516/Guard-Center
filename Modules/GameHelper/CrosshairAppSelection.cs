using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GuardCenter
{
    internal sealed class CrosshairSelectedApp
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string TargetPath = string.Empty;
        public string Publisher = string.Empty;
        public string Source = string.Empty;
        public string IconPath = string.Empty;
    }

    internal sealed class CrosshairAppSelectionIndex
    {
        private readonly HashSet<string> executableIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CrosshairAppSelectionIndex(IEnumerable<CrosshairSelectedApp> apps)
        {
            if (apps == null)
            {
                return;
            }

            foreach (CrosshairSelectedApp app in apps)
            {
                string id = CrosshairAppSelectionCodec.CreateExecutableId(
                    app == null ? string.Empty : app.TargetPath);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    executableIds.Add(id);
                }
            }
        }

        public bool ContainsExecutable(string executablePath)
        {
            string id = CrosshairAppSelectionCodec.CreateExecutableId(executablePath);
            return !string.IsNullOrWhiteSpace(id) && executableIds.Contains(id);
        }
    }

    internal static class CrosshairAppSelectionCodec
    {
        public static List<CrosshairSelectedApp> Decode(string encodedText)
        {
            var result = new List<CrosshairSelectedApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(encodedText))
            {
                return result;
            }

            string[] records = encodedText.Split(new char[] { ',' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < records.Length; i++)
            {
                try
                {
                    string record = Encoding.UTF8.GetString(Convert.FromBase64String(records[i]));
                    string[] parts = record.Split('\t');
                    string path = NormalizeExecutablePath(parts.Length > 1 ? parts[1] : string.Empty);
                    string id = CreateExecutableId(path);
                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    {
                        continue;
                    }

                    string name = AppIdentityService.CleanDisplayName(
                        parts.Length > 0 ? parts[0] : string.Empty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = Path.GetFileNameWithoutExtension(path) ?? "Application";
                    }

                    result.Add(new CrosshairSelectedApp
                    {
                        Id = id,
                        Name = name,
                        TargetPath = path,
                        Publisher = AppIdentityService.CleanField(parts.Length > 2 ? parts[2] : string.Empty),
                        Source = AppIdentityService.CleanField(parts.Length > 3 ? parts[3] : string.Empty),
                        IconPath = AppIdentityService.NormalizeIconPath(
                            parts.Length > 4 ? parts[4] : string.Empty)
                    });
                }
                catch
                {
                    // Ignore one malformed record without discarding the remaining selections.
                }
            }
            return result;
        }

        public static string Encode(IEnumerable<CrosshairSelectedApp> apps)
        {
            var records = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (apps == null)
            {
                return string.Empty;
            }

            foreach (CrosshairSelectedApp app in apps)
            {
                string path = NormalizeExecutablePath(app == null ? string.Empty : app.TargetPath);
                string id = CreateExecutableId(path);
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                string name = AppIdentityService.CleanDisplayName(app.Name);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Path.GetFileNameWithoutExtension(path) ?? "Application";
                }
                string record = AppIdentityService.CleanField(name) + "\t"
                    + AppIdentityService.CleanField(path) + "\t"
                    + AppIdentityService.CleanField(app.Publisher) + "\t"
                    + AppIdentityService.CleanField(app.Source) + "\t"
                    + AppIdentityService.CleanField(app.IconPath);
                records.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(record)));
            }
            return string.Join(",", records.ToArray());
        }

        public static string CreateExecutableId(string executablePath)
        {
            string normalized = NormalizeExecutablePath(executablePath);
            return string.IsNullOrWhiteSpace(normalized)
                ? string.Empty
                : AppIdentityService.CreateExecutableId(normalized);
        }

        public static string NormalizeExecutablePath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)
                || !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(
                    executablePath.Trim().Trim('"'))))
            {
                return string.Empty;
            }

            string normalized = AppIdentityService.NormalizeExecutablePath(executablePath);
            return string.Equals(Path.GetExtension(normalized), ".exe",
                    StringComparison.OrdinalIgnoreCase)
                ? normalized
                : string.Empty;
        }
    }

    internal static class CrosshairVisibilityPolicy
    {
        public static bool ShouldExist(bool crosshairEnabled, int previewCount)
        {
            return crosshairEnabled || previewCount > 0;
        }

        public static bool ShouldShow(bool crosshairEnabled, bool restrictToSelectedApps,
            bool foregroundExecutableMatched, int previewCount)
        {
            return previewCount > 0
                || (crosshairEnabled
                    && (!restrictToSelectedApps || foregroundExecutableMatched));
        }
    }
}
