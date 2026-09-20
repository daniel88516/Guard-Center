using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace GuardCenter
{
    internal sealed class AppCatalogService
    {
        public List<AppCatalogItem> GetInstalledApps()
        {
            var apps = new Dictionary<string, AppCatalogItem>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            AddStartMenuApps(apps, ref order);
            AddUninstallRegistryApps(apps, ref order);

            var result = CollapseEquivalentApps(apps.Values);
            result.Sort(AppIdentityService.CompareName);
            for (int i = 0; i < result.Count; i++)
            {
                result[i].OriginalOrder = i;
            }

            return result;
        }

        public AppCatalogItem ResolvePathAsApp(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (expanded.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                ShortcutInfo shortcut = ResolveShortcut(expanded);
                if (shortcut == null)
                {
                    return null;
                }

                return CreateItem(Path.GetFileNameWithoutExtension(expanded), shortcut.TargetPath,
                    shortcut.Arguments, shortcut.WorkingDirectory, string.Empty,
                    string.IsNullOrWhiteSpace(shortcut.Source) ? "Shortcut" : shortcut.Source,
                    shortcut.IconPath, expanded, string.Empty, string.Empty, string.Empty, 0);
            }

            if (expanded.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                ShortcutInfo shortcut = ResolveUrlShortcut(expanded, Path.GetFileNameWithoutExtension(expanded));
                if (shortcut == null)
                {
                    return null;
                }

                return CreateItem(shortcut.DisplayName, shortcut.TargetPath,
                    shortcut.Arguments, shortcut.WorkingDirectory, shortcut.Publisher, shortcut.Source,
                    shortcut.IconPath, expanded, string.Empty, string.Empty, string.Empty, 0,
                    shortcut.LogicalId);
            }

            string executable = AppIdentityService.NormalizeExecutablePath(expanded);
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return null;
            }

            return CreateItem(Path.GetFileNameWithoutExtension(executable), executable, string.Empty,
                Path.GetDirectoryName(executable) ?? string.Empty, string.Empty, "Executable",
                executable, string.Empty, Path.GetDirectoryName(executable) ?? string.Empty,
                string.Empty, string.Empty, 0);
        }

        public AppCatalogItem FindOrImport(IList<AppCatalogItem> apps, string targetPath)
        {
            AppCatalogItem target = ResolvePathAsApp(targetPath);
            if (target == null)
            {
                return null;
            }

            if (apps != null)
            {
                for (int i = 0; i < apps.Count; i++)
                {
                    if (string.Equals(apps[i].Id, target.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        return apps[i];
                    }
                }
            }

            return target;
        }

        private void AddStartMenuApps(Dictionary<string, AppCatalogItem> apps, ref int order)
        {
            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            };

            for (int i = 0; i < roots.Length; i++)
            {
                AddShortcutAppsFromRoot(apps, roots[i], ref order);
            }
        }

        private void AddShortcutAppsFromRoot(Dictionary<string, AppCatalogItem> apps, string root, ref int order)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            string[] shortcuts;
            try
            {
                shortcuts = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                shortcuts = new string[0];
            }

            for (int i = 0; i < shortcuts.Length; i++)
            {
                ShortcutInfo shortcut = ResolveShortcut(shortcuts[i]);
                if (shortcut == null)
                {
                    continue;
                }

                AddItem(apps, CreateItem(Path.GetFileNameWithoutExtension(shortcuts[i]), shortcut.TargetPath,
                    shortcut.Arguments, shortcut.WorkingDirectory, string.Empty, "Start Menu",
                    shortcut.IconPath, shortcuts[i], string.Empty, string.Empty, string.Empty, order++));
            }

            string[] urlShortcuts;
            try
            {
                urlShortcuts = Directory.GetFiles(root, "*.url", SearchOption.AllDirectories);
            }
            catch
            {
                urlShortcuts = new string[0];
            }

            for (int i = 0; i < urlShortcuts.Length; i++)
            {
                string displayName = Path.GetFileNameWithoutExtension(urlShortcuts[i]);
                ShortcutInfo shortcut = ResolveUrlShortcut(urlShortcuts[i], displayName);
                if (shortcut == null)
                {
                    continue;
                }

                AddItem(apps, CreateItem(
                    string.IsNullOrWhiteSpace(shortcut.DisplayName) ? displayName : shortcut.DisplayName,
                    shortcut.TargetPath, shortcut.Arguments, shortcut.WorkingDirectory, shortcut.Publisher,
                    shortcut.Source, shortcut.IconPath, urlShortcuts[i], string.Empty, string.Empty, string.Empty,
                    order++, shortcut.LogicalId));
            }
        }

        private void AddUninstallRegistryApps(Dictionary<string, AppCatalogItem> apps, ref int order)
        {
            AddUninstallRegistryApps(apps, Registry.CurrentUser, RegistryView.Default, ref order);
            AddUninstallRegistryApps(apps, Registry.LocalMachine, RegistryView.Registry64, ref order);
            AddUninstallRegistryApps(apps, Registry.LocalMachine, RegistryView.Registry32, ref order);
        }

        private void AddUninstallRegistryApps(Dictionary<string, AppCatalogItem> apps,
            RegistryKey baseKey, RegistryView view, ref int order)
        {
            RegistryKey root = baseKey;
            bool disposeRoot = false;
            if (baseKey.Name == Registry.LocalMachine.Name && view != RegistryView.Default)
            {
                root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                disposeRoot = true;
            }

            try
            {
                using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                {
                    if (uninstall == null)
                    {
                        return;
                    }

                    string[] subkeys = uninstall.GetSubKeyNames();
                    for (int i = 0; i < subkeys.Length; i++)
                    {
                        using (RegistryKey appKey = uninstall.OpenSubKey(subkeys[i], false))
                        {
                            if (appKey == null || IsHiddenUninstallEntry(appKey))
                            {
                                continue;
                            }

                            string name = ReadRegistryString(appKey, "DisplayName");
                            string publisher = ReadRegistryString(appKey, "Publisher");
                            string displayIcon = ReadRegistryString(appKey, "DisplayIcon");
                            string installLocation = ReadRegistryString(appKey, "InstallLocation");
                            string uninstallCommand = ReadRegistryString(appKey, "UninstallString");
                            string modifyCommand = ReadRegistryString(appKey, "ModifyPath");
                            string targetPath = ExtractExecutablePath(displayIcon);
                            string logicalId = GetSteamLogicalId(subkeys[i], uninstallCommand);

                            if (string.IsNullOrWhiteSpace(targetPath))
                            {
                                targetPath = FindLikelyExecutable(installLocation, name);
                            }

                            AddItem(apps, CreateItem(name, targetPath, string.Empty,
                                AppIdentityService.NormalizeFolderPath(installLocation), publisher,
                                "Installed apps", displayIcon, string.Empty, installLocation,
                                uninstallCommand, modifyCommand, order++, logicalId));
                        }
                    }
                }
            }
            finally
            {
                if (disposeRoot)
                {
                    root.Dispose();
                }
            }
        }

        private static AppCatalogItem CreateItem(string name, string targetPath, string arguments,
            string workingDirectory, string publisher, string source, string iconPath, string shortcutPath,
            string installLocation, string uninstallCommand, string modifyCommand, int order,
            string logicalId = "")
        {
            string normalizedPath = AppIdentityService.NormalizeExecutablePath(targetPath);
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(normalizedPath)
                || !File.Exists(normalizedPath))
            {
                return null;
            }

            return new AppCatalogItem
            {
                Id = AppIdentityService.CreateExecutableId(normalizedPath),
                Name = AppIdentityService.CleanDisplayName(name),
                TargetPath = normalizedPath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = AppIdentityService.NormalizeFolderPath(workingDirectory),
                Publisher = publisher ?? string.Empty,
                Source = source ?? string.Empty,
                IconPath = AppIdentityService.NormalizeIconPath(iconPath),
                ShortcutPath = shortcutPath ?? string.Empty,
                InstallLocation = AppIdentityService.NormalizeFolderPath(installLocation),
                UninstallCommand = uninstallCommand ?? string.Empty,
                ModifyCommand = modifyCommand ?? string.Empty,
                LogicalId = logicalId ?? string.Empty,
                OriginalOrder = order
            };
        }

        internal static List<AppCatalogItem> CollapseEquivalentApps(IEnumerable<AppCatalogItem> candidates)
        {
            var result = new List<AppCatalogItem>();
            if (candidates == null)
            {
                return result;
            }

            foreach (AppCatalogItem candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                int equivalentIndex = -1;
                for (int i = 0; i < result.Count; i++)
                {
                    if (AreEquivalentApps(result[i], candidate))
                    {
                        equivalentIndex = i;
                        break;
                    }
                }

                if (equivalentIndex < 0)
                {
                    result.Add(candidate.Clone());
                    continue;
                }

                AppCatalogItem existing = result[equivalentIndex];
                string preferredDisplayName = GetInstalledProductDisplayName(existing, candidate);
                if (GetLaunchTargetQuality(candidate) > GetLaunchTargetQuality(existing))
                {
                    AppCatalogItem preferred = candidate.Clone();
                    Merge(preferred, existing);
                    if (!string.IsNullOrWhiteSpace(preferredDisplayName))
                    {
                        preferred.Name = preferredDisplayName;
                    }
                    preferred.OriginalOrder = Math.Min(existing.OriginalOrder, candidate.OriginalOrder);
                    result[equivalentIndex] = preferred;
                }
                else
                {
                    Merge(existing, candidate);
                    if (!string.IsNullOrWhiteSpace(preferredDisplayName))
                    {
                        existing.Name = preferredDisplayName;
                    }
                    existing.OriginalOrder = Math.Min(existing.OriginalOrder, candidate.OriginalOrder);
                }
            }

            var launchable = new List<AppCatalogItem>();
            for (int i = 0; i < result.Count; i++)
            {
                if (IsExecutableTarget(result[i].TargetPath))
                {
                    launchable.Add(result[i]);
                }
            }
            return launchable;
        }

        private static string GetInstalledProductDisplayName(AppCatalogItem left, AppCatalogItem right)
        {
            bool leftInstalled = HasSource(left, "Installed apps");
            bool rightInstalled = HasSource(right, "Installed apps");
            if (leftInstalled && !rightInstalled)
            {
                return left.Name;
            }
            if (rightInstalled && !leftInstalled)
            {
                return right.Name;
            }
            return string.Empty;
        }

        private static bool AreEquivalentApps(AppCatalogItem left, AppCatalogItem right)
        {
            if (!string.IsNullOrWhiteSpace(left.LogicalId)
                && string.Equals(left.LogicalId, right.LogicalId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool leftInstalled = HasSource(left, "Installed apps");
            bool rightInstalled = HasSource(right, "Installed apps");
            bool leftLaunchSource = HasSource(left, "Start Menu") || HasSource(left, "Steam");
            bool rightLaunchSource = HasSource(right, "Start Menu") || HasSource(right, "Steam");
            if (!((leftInstalled && rightLaunchSource) || (rightInstalled && leftLaunchSource)))
            {
                return false;
            }

            bool sameDisplayName = string.Equals(AppIdentityService.CleanDisplayName(left.Name),
                AppIdentityService.CleanDisplayName(right.Name), StringComparison.CurrentCultureIgnoreCase);
            if (!sameDisplayName && !NamesMatchInstalledProductFamily(left, right))
            {
                return false;
            }

            if (!PublishersAreCompatible(left.Publisher, right.Publisher))
            {
                return false;
            }

            return IsTargetInsideInstallLocation(left.TargetPath, right.InstallLocation)
                || IsTargetInsideInstallLocation(right.TargetPath, left.InstallLocation);
        }

        private static bool NamesMatchInstalledProductFamily(AppCatalogItem left, AppCatalogItem right)
        {
            AppCatalogItem installed = HasSource(left, "Installed apps") ? left : right;
            AppCatalogItem launcher = ReferenceEquals(installed, left) ? right : left;
            string installFolder = AppIdentityService.NormalizeFolderPath(installed.InstallLocation);
            if (string.IsNullOrWhiteSpace(installFolder))
            {
                return false;
            }

            string family = NormalizeNameForMatch(Path.GetFileName(
                installFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            if (family.Length < 4)
            {
                return false;
            }

            string installedName = NormalizeNameForMatch(installed.Name);
            string launcherName = RemoveLaunchSuffix(NormalizeNameForMatch(launcher.Name));
            return installedName.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                && string.Equals(launcherName, family, StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveLaunchSuffix(string value)
        {
            string[] suffixes = new string[] { "launcher", "launch", "client" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                string suffix = suffixes[i];
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && value.Length - suffix.Length >= 4)
                {
                    return value.Substring(0, value.Length - suffix.Length);
                }
            }
            return value;
        }

        private static bool HasSource(AppCatalogItem item, string source)
        {
            return item != null && (item.Source ?? string.Empty)
                .IndexOf(source, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool PublishersAreCompatible(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left)
                || string.IsNullOrWhiteSpace(right)
                || string.Equals(left.Trim(), right.Trim(), StringComparison.CurrentCultureIgnoreCase);
        }

        private static bool IsTargetInsideInstallLocation(string targetPath, string installLocation)
        {
            string target = AppIdentityService.NormalizeExecutablePath(targetPath);
            string folder = AppIdentityService.NormalizeFolderPath(installLocation);
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            string prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExecutableTarget(string targetPath)
        {
            return string.Equals(Path.GetExtension(targetPath ?? string.Empty), ".exe",
                StringComparison.OrdinalIgnoreCase);
        }

        private static int GetLaunchTargetQuality(AppCatalogItem item)
        {
            if (item == null || !IsExecutableTarget(item.TargetPath))
            {
                return int.MinValue;
            }

            string file = (Path.GetFileNameWithoutExtension(item.TargetPath) ?? string.Empty).ToLowerInvariant();
            int quality = HasSource(item, "Start Menu") || HasSource(item, "Steam") ? 100 : 60;
            string[] nonLaunchMarkers = new string[]
            {
                "installer", "install", "setup", "uninstall", "unins", "updater", "update",
                "helper", "service", "crash", "launcher"
            };
            for (int i = 0; i < nonLaunchMarkers.Length; i++)
            {
                if (file.IndexOf(nonLaunchMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return quality - 80;
                }
            }

            return quality;
        }

        internal static string GetSteamLogicalId(string registryKeyName, string uninstallCommand)
        {
            string appId = ExtractDigitsAfterMarker(registryKeyName, "Steam App ");
            if (string.IsNullOrWhiteSpace(appId))
            {
                appId = ExtractSteamAppId(uninstallCommand);
            }

            return string.IsNullOrWhiteSpace(appId) ? string.Empty : "steam:" + appId;
        }

        private static void AddItem(Dictionary<string, AppCatalogItem> apps, AppCatalogItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
            {
                return;
            }

            AppCatalogItem existing;
            if (apps.TryGetValue(item.Id, out existing))
            {
                Merge(existing, item);
                return;
            }

            apps[item.Id] = item;
        }

        private static void Merge(AppCatalogItem existing, AppCatalogItem incoming)
        {
            bool matchingLogicalPublisher = !string.IsNullOrWhiteSpace(existing.LogicalId)
                && string.Equals(existing.LogicalId, incoming.LogicalId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Publisher, "Steam", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(incoming.Publisher, "Steam", StringComparison.OrdinalIgnoreCase);
            if ((string.IsNullOrWhiteSpace(existing.Publisher) || matchingLogicalPublisher)
                && !string.IsNullOrWhiteSpace(incoming.Publisher))
            {
                existing.Publisher = incoming.Publisher;
            }
            if (string.IsNullOrWhiteSpace(existing.IconPath) && !string.IsNullOrWhiteSpace(incoming.IconPath))
            {
                existing.IconPath = incoming.IconPath;
            }
            if (string.IsNullOrWhiteSpace(existing.ShortcutPath) && !string.IsNullOrWhiteSpace(incoming.ShortcutPath))
            {
                existing.ShortcutPath = incoming.ShortcutPath;
            }
            if (string.IsNullOrWhiteSpace(existing.InstallLocation) && !string.IsNullOrWhiteSpace(incoming.InstallLocation))
            {
                existing.InstallLocation = incoming.InstallLocation;
            }
            if (string.IsNullOrWhiteSpace(existing.UninstallCommand) && !string.IsNullOrWhiteSpace(incoming.UninstallCommand))
            {
                existing.UninstallCommand = incoming.UninstallCommand;
            }
            if (string.IsNullOrWhiteSpace(existing.ModifyCommand) && !string.IsNullOrWhiteSpace(incoming.ModifyCommand))
            {
                existing.ModifyCommand = incoming.ModifyCommand;
            }
            if (string.IsNullOrWhiteSpace(existing.LogicalId) && !string.IsNullOrWhiteSpace(incoming.LogicalId))
            {
                existing.LogicalId = incoming.LogicalId;
            }
            if (!string.IsNullOrWhiteSpace(incoming.Source)
                && existing.Source.IndexOf(incoming.Source, StringComparison.OrdinalIgnoreCase) < 0)
            {
                existing.Source = string.IsNullOrWhiteSpace(existing.Source)
                    ? incoming.Source
                    : existing.Source + " + " + incoming.Source;
            }
        }

        private static bool IsHiddenUninstallEntry(RegistryKey key)
        {
            object systemComponent = key.GetValue("SystemComponent");
            if (systemComponent is int && (int)systemComponent == 1)
            {
                return true;
            }

            string parent = ReadRegistryString(key, "ParentKeyName");
            string releaseType = ReadRegistryString(key, "ReleaseType");
            return !string.IsNullOrWhiteSpace(parent)
                || string.Equals(releaseType, "Hotfix", StringComparison.OrdinalIgnoreCase)
                || string.Equals(releaseType, "Security Update", StringComparison.OrdinalIgnoreCase)
                || string.Equals(releaseType, "Update Rollup", StringComparison.OrdinalIgnoreCase);
        }

        private static ShortcutInfo ResolveShortcut(string shortcutPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return null;
                }

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });

                Type shortcutType = shortcut.GetType();
                string targetPath = Convert.ToString(shortcutType.InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, shortcut, null));
                string arguments = Convert.ToString(shortcutType.InvokeMember("Arguments",
                    BindingFlags.GetProperty, null, shortcut, null));
                string workingDirectory = Convert.ToString(shortcutType.InvokeMember("WorkingDirectory",
                    BindingFlags.GetProperty, null, shortcut, null));
                string iconPath = Convert.ToString(shortcutType.InvokeMember("IconLocation",
                    BindingFlags.GetProperty, null, shortcut, null));

                return new ShortcutInfo
                {
                    TargetPath = targetPath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    IconPath = iconPath,
                    Source = "Start Menu"
                };
            }
            catch
            {
                return null;
            }
        }

        private static ShortcutInfo ResolveUrlShortcut(string shortcutPath, string displayName)
        {
            try
            {
                string[] lines = File.ReadAllLines(shortcutPath);
                string url = string.Empty;
                string iconFile = string.Empty;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        url = lines[i].Substring(4).Trim();
                    }
                    else if (lines[i].StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        iconFile = lines[i].Substring(9).Trim();
                    }
                }

                if (url.IndexOf("steam://", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ResolveSteamShortcut(url, displayName, iconFile);
                }
            }
            catch
            {
            }

            return null;
        }

        private static ShortcutInfo ResolveSteamShortcut(string url, string displayName, string iconFile)
        {
            string appId = ExtractSteamAppId(url);
            if (string.IsNullOrWhiteSpace(appId))
            {
                return null;
            }

            SteamAppInfo app = FindSteamApp(appId);
            if (app == null || string.IsNullOrWhiteSpace(app.InstallFolder))
            {
                return null;
            }

            string targetPath = FindLikelyGameExecutable(app.InstallFolder, displayName, app.Name, app.AppId);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            return new ShortcutInfo
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? app.Name : displayName,
                TargetPath = targetPath,
                WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty,
                IconPath = AppIdentityService.NormalizeIconPath(iconFile),
                Publisher = "Steam",
                Source = "Steam",
                LogicalId = "steam:" + appId
            };
        }

        private static string ExtractSteamAppId(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            string[] markers = new string[] { "rungameid/", "launch/", "uninstall/" };
            for (int i = 0; i < markers.Length; i++)
            {
                string appId = ExtractDigitsAfterMarker(url, markers[i]);
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    return appId;
                }
            }

            return string.Empty;
        }

        private static string ExtractDigitsAfterMarker(string value, string marker)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(marker))
            {
                return string.Empty;
            }

            int markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            int start = markerIndex + marker.Length;
            var builder = new StringBuilder();
            while (start < value.Length && char.IsDigit(value[start]))
            {
                builder.Append(value[start]);
                start++;
            }
            return builder.ToString();
        }

        private static SteamAppInfo FindSteamApp(string appId)
        {
            List<string> libraries = GetSteamLibraryFolders();
            for (int i = 0; i < libraries.Count; i++)
            {
                string manifestPath = Path.Combine(libraries[i], "steamapps", "appmanifest_" + appId + ".acf");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    string manifest = File.ReadAllText(manifestPath, Encoding.UTF8);
                    string name = ReadVdfValue(manifest, "name");
                    string installDir = ReadVdfValue(manifest, "installdir");
                    string installFolder = Path.Combine(libraries[i], "steamapps", "common", installDir);
                    if (!Directory.Exists(installFolder))
                    {
                        continue;
                    }

                    return new SteamAppInfo
                    {
                        AppId = appId,
                        Name = name,
                        InstallFolder = installFolder
                    };
                }
                catch
                {
                    continue;
                }
            }

            return null;
        }

        private static List<string> GetSteamLibraryFolders()
        {
            var libraries = new List<string>();
            string steamPath = GetSteamInstallPath();
            AddUniqueExistingFolder(libraries, steamPath);

            string libraryFile = string.IsNullOrWhiteSpace(steamPath)
                ? string.Empty
                : Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                try
                {
                    string text = File.ReadAllText(libraryFile, Encoding.UTF8);
                    string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        AddUniqueExistingFolder(libraries, ReadVdfLineValue(lines[i], "path"));
                    }
                }
                catch
                {
                }
            }

            return libraries;
        }

        private static string GetSteamInstallPath()
        {
            string path = ReadRegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = ReadRegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            }

            return AppIdentityService.NormalizeFolderPath((path ?? string.Empty).Replace('/', '\\'));
        }

        private static void AddUniqueExistingFolder(List<string> folders, string folder)
        {
            string normalized = AppIdentityService.NormalizeFolderPath((folder ?? string.Empty).Replace(@"\\", @"\"));
            if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
            {
                return;
            }

            for (int i = 0; i < folders.Count; i++)
            {
                if (string.Equals(folders[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            folders.Add(normalized);
        }

        private static string FindLikelyExecutable(string installLocation, string displayName)
        {
            string folder = AppIdentityService.NormalizeFolderPath(installLocation);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return string.Empty;
            }

            try
            {
                string[] executables = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
                return PickBestExecutable(executables, displayName);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FindLikelyGameExecutable(string installFolder, string displayName, string appName, string appId)
        {
            string folder = AppIdentityService.NormalizeFolderPath(installFolder);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return string.Empty;
            }

            try
            {
                string[] topLevel = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
                string picked = PickBestExecutable(topLevel, string.IsNullOrWhiteSpace(appName) ? displayName : appName);
                if (!string.IsNullOrWhiteSpace(picked))
                {
                    return picked;
                }

                string[] all = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories);
                return PickBestExecutable(all, string.IsNullOrWhiteSpace(appName) ? displayName : appName);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string PickBestExecutable(string[] executables, string displayName)
        {
            if (executables == null || executables.Length == 0)
            {
                return string.Empty;
            }

            string cleaned = NormalizeNameForMatch(displayName);
            string best = string.Empty;
            int bestScore = int.MinValue;
            for (int i = 0; i < executables.Length; i++)
            {
                string path = executables[i];
                string file = NormalizeNameForMatch(Path.GetFileNameWithoutExtension(path));
                int score = 0;
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    if (string.Equals(file, cleaned, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 100;
                    }
                    else if (file.IndexOf(cleaned, StringComparison.OrdinalIgnoreCase) >= 0
                        || cleaned.IndexOf(file, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 40;
                    }
                }

                string lower = file.ToLowerInvariant();
                if (lower.IndexOf("unins") >= 0 || lower.IndexOf("setup") >= 0
                    || lower.IndexOf("install") >= 0 || lower.IndexOf("crash") >= 0
                    || lower.IndexOf("helper") >= 0 || lower.IndexOf("service") >= 0)
                {
                    score -= 30;
                }

                try
                {
                    score += (int)Math.Min(20, new FileInfo(path).Length / (1024 * 1024));
                }
                catch
                {
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = path;
                }
            }

            return best;
        }

        private static string NormalizeNameForMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private static string ExtractExecutablePath(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string expanded = Environment.ExpandEnvironmentVariables(text.Trim());
            if (expanded.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = expanded.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    return AppIdentityService.NormalizeExecutablePath(expanded.Substring(1, endQuote - 1));
                }
            }

            string[] parts = expanded.Split(new char[] { ',' }, 2);
            string candidate = parts[0].Trim();
            if (File.Exists(candidate))
            {
                return AppIdentityService.NormalizeExecutablePath(candidate);
            }

            string parsedFile;
            string parsedArgs;
            string error;
            if (WindowsCommandLine.TryParse(expanded, out parsedFile, out parsedArgs, out error)
                && File.Exists(parsedFile))
            {
                return AppIdentityService.NormalizeExecutablePath(parsedFile);
            }

            return string.Empty;
        }

        private static string ReadRegistryString(RegistryKey key, string name)
        {
            object value = key == null ? null : key.GetValue(name);
            return value == null ? string.Empty : Convert.ToString(value);
        }

        private static string ReadRegistryValue(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path, false))
                {
                    return ReadRegistryString(key, name);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadVdfValue(string text, string key)
        {
            string[] lines = (text ?? string.Empty).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string value = ReadVdfLineValue(lines[i], key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string ReadVdfLineValue(string line, string key)
        {
            string trimmed = (line ?? string.Empty).Trim();
            string quotedKey = "\"" + key + "\"";
            if (!trimmed.StartsWith(quotedKey, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            int firstQuote = trimmed.IndexOf('"', quotedKey.Length);
            if (firstQuote < 0)
            {
                return string.Empty;
            }
            int secondQuote = trimmed.IndexOf('"', firstQuote + 1);
            if (secondQuote <= firstQuote)
            {
                return string.Empty;
            }

            return trimmed.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Replace(@"\\", @"\");
        }

        private sealed class ShortcutInfo
        {
            public string DisplayName = string.Empty;
            public string TargetPath = string.Empty;
            public string Arguments = string.Empty;
            public string WorkingDirectory = string.Empty;
            public string IconPath = string.Empty;
            public string Publisher = string.Empty;
            public string Source = string.Empty;
            public string LogicalId = string.Empty;
        }

        private sealed class SteamAppInfo
        {
            public string AppId = string.Empty;
            public string Name = string.Empty;
            public string InstallFolder = string.Empty;
        }
    }
}
