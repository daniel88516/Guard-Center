using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal enum HuaJuanCompatibilityOperation
    {
        Apply,
        Restore
    }

    internal sealed class HuaJuanCompatibilityResult
    {
        public string OperationId = string.Empty;
        public bool Succeeded;
        public bool Canceled;
        public bool RebootRequired;
        public string Message = string.Empty;
    }

    internal sealed class HuaJuanCompatibilitySnapshot
    {
        public bool Installed;
        public bool PayloadAvailable;
        public bool BackupAvailable;
        public bool KeyboardIsolated;
        public bool MouseInterceptionRetained;
        public int CompatibleDllCount;
        public int InterceptionDllCount;
        public DateTime AppliedAt;
        public HuaJuanCompatibilityState State = HuaJuanCompatibilityState.NotInstalled;
        public string Summary = string.Empty;
    }

    internal static class HuaJuanCompatibilityPaths
    {
        public static readonly string HuaJuanRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "HuaJuan");
        public static readonly string ShimPayloadPath = Path.Combine(AppContext.BaseDirectory,
            "Assets", "Interception", "interception-shim.dll");
        public static readonly string RealPayloadPath = Path.Combine(AppContext.BaseDirectory,
            "Assets", "Interception", "interception-sparse.dll");
        public static readonly string ConfigPayloadPath = Path.Combine(AppContext.BaseDirectory,
            "Assets", "Interception", "guardcenter-huajuan-shim.ini");
        public static readonly string StateRoot = AppPaths.InputStackCompatibilityRoot;
        public static readonly string ActiveManifestPath = Path.Combine(StateRoot,
            "active-backup.json");
        public static readonly string LastResultPath = Path.Combine(StateRoot,
            "last-operation.json");

        public static readonly string[] RelativeDllPaths =
        {
            Path.Combine("tools", "interception.dll"),
            Path.Combine("_internal", "interception.dll"),
            Path.Combine("_internal", "src", "drivers", "interception.dll")
        };
    }

    internal interface IUpperFilterStore
    {
        string[] ReadKeyboard();
        string[] ReadMouse();
        void WriteKeyboard(string[] values);
    }

    internal interface IHuaJuanPostRebootStore
    {
        Dictionary<string, string> ReadConflicting();
        void RemoveConflicting();
        void Restore(Dictionary<string, string> values);
    }

    internal sealed class WindowsUpperFilterStore : IUpperFilterStore
    {
        private const string KeyboardClassRegistry =
            @"SYSTEM\CurrentControlSet\Control\Class\{4D36E96B-E325-11CE-BFC1-08002BE10318}";
        private const string MouseClassRegistry =
            @"SYSTEM\CurrentControlSet\Control\Class\{4D36E96F-E325-11CE-BFC1-08002BE10318}";

        public string[] ReadKeyboard()
        {
            return Read(KeyboardClassRegistry);
        }

        public string[] ReadMouse()
        {
            return Read(MouseClassRegistry);
        }

        public void WriteKeyboard(string[] values)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                KeyboardClassRegistry, true))
            {
                if (key == null)
                    throw new InvalidOperationException("找不到 Windows Keyboard class registry。");
                key.SetValue("UpperFilters", values ?? Array.Empty<string>(),
                    RegistryValueKind.MultiString);
            }
        }

        private static string[] Read(string path)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path, false))
            {
                object value = key == null ? null : key.GetValue("UpperFilters");
                if (value is string[] values) return (string[])values.Clone();
                if (value is string single && !string.IsNullOrWhiteSpace(single))
                    return new[] { single };
                return Array.Empty<string>();
            }
        }
    }

    internal sealed class WindowsHuaJuanPostRebootStore : IHuaJuanPostRebootStore
    {
        private const string RunOncePath =
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        internal static readonly string[] ConflictingValueNames =
        {
            "HuaJuanInterceptionRepair",
            "HuaJuanFullRecoveryVerify"
        };

        public Dictionary<string, string> ReadConflicting()
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                RunOncePath, false))
            {
                if (key == null) return result;
                for (int i = 0; i < ConflictingValueNames.Length; i++)
                {
                    object value = key.GetValue(ConflictingValueNames[i], null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value is string command && !string.IsNullOrWhiteSpace(command))
                        result[ConflictingValueNames[i]] = command;
                }
            }
            return result;
        }

        public void RemoveConflicting()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                RunOncePath, true))
            {
                if (key == null) return;
                for (int i = 0; i < ConflictingValueNames.Length; i++)
                    key.DeleteValue(ConflictingValueNames[i], false);
            }
        }

        public void Restore(Dictionary<string, string> values)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                RunOncePath, true))
            {
                for (int i = 0; i < ConflictingValueNames.Length; i++)
                    key.DeleteValue(ConflictingValueNames[i], false);
                if (values == null) return;
                foreach (KeyValuePair<string, string> item in values)
                    key.SetValue(item.Key, item.Value, RegistryValueKind.String);
            }
        }
    }

    internal sealed class HuaJuanCompatibilityEngine
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        private readonly string huaJuanRoot;
        private readonly string shimPayloadPath;
        private readonly string realPayloadPath;
        private readonly string configPayloadPath;
        private readonly string stateRoot;
        private readonly string activeManifestPath;
        private readonly IUpperFilterStore upperFilters;
        private readonly IHuaJuanPostRebootStore postRebootStore;
        private readonly Func<bool> isHuaJuanRunning;

        public HuaJuanCompatibilityEngine()
            : this(HuaJuanCompatibilityPaths.HuaJuanRoot,
                HuaJuanCompatibilityPaths.ShimPayloadPath,
                HuaJuanCompatibilityPaths.RealPayloadPath,
                HuaJuanCompatibilityPaths.ConfigPayloadPath,
                HuaJuanCompatibilityPaths.StateRoot,
                new WindowsUpperFilterStore(), new WindowsHuaJuanPostRebootStore(),
                IsDefaultHuaJuanRunning)
        {
        }

        internal HuaJuanCompatibilityEngine(string huaJuanRoot, string shimPayloadPath,
            string realPayloadPath, string configPayloadPath, string stateRoot,
            IUpperFilterStore upperFilters, IHuaJuanPostRebootStore postRebootStore,
            Func<bool> isHuaJuanRunning)
        {
            this.huaJuanRoot = Path.GetFullPath(huaJuanRoot);
            this.shimPayloadPath = Path.GetFullPath(shimPayloadPath);
            this.realPayloadPath = Path.GetFullPath(realPayloadPath);
            this.configPayloadPath = Path.GetFullPath(configPayloadPath);
            this.stateRoot = Path.GetFullPath(stateRoot);
            activeManifestPath = Path.Combine(this.stateRoot, "active-backup.json");
            this.upperFilters = upperFilters;
            this.postRebootStore = postRebootStore;
            this.isHuaJuanRunning = isHuaJuanRunning;
        }

        public HuaJuanCompatibilitySnapshot Inspect(DateTime bootTime,
            string[] keyboardFilters, string[] mouseFilters)
        {
            var result = new HuaJuanCompatibilitySnapshot
            {
                Installed = Directory.Exists(huaJuanRoot),
                PayloadAvailable = PayloadsAvailable(),
                KeyboardIsolated = !Contains(keyboardFilters, "keyboard"),
                MouseInterceptionRetained = Contains(mouseFilters, "mouse")
            };
            if (!result.Installed)
            {
                result.State = HuaJuanCompatibilityState.NotInstalled;
                result.Summary = "未偵測到 HuaJuan。";
                return result;
            }

            string payloadHash = result.PayloadAvailable
                ? HashFile(shimPayloadPath) : string.Empty;
            string[] targets = GetExistingTargetPaths();
            result.InterceptionDllCount = targets.Length;
            for (int i = 0; i < targets.Length; i++)
            {
                if (!string.IsNullOrEmpty(payloadHash)
                    && string.Equals(HashFile(targets[i]), payloadHash,
                        StringComparison.OrdinalIgnoreCase))
                    result.CompatibleDllCount++;
            }

            HuaJuanBackupManifest manifest = TryReadManifest(activeManifestPath);
            result.BackupAvailable = ManifestBackupsExist(manifest);
            if (manifest != null && manifest.AppliedAtUtc != default)
                result.AppliedAt = manifest.AppliedAtUtc.ToLocalTime();

            bool everyDllCompatible = result.InterceptionDllCount > 0
                && result.CompatibleDllCount == result.InterceptionDllCount
                && ManagedRuntimePayloadsAreCurrent(targets);
            if (everyDllCompatible && result.KeyboardIsolated
                && result.MouseInterceptionRetained)
            {
                bool pendingReboot = manifest != null && manifest.AppliedAtUtc != default
                    && bootTime.ToUniversalTime() < manifest.AppliedAtUtc;
                result.State = pendingReboot
                    ? HuaJuanCompatibilityState.AppliedPendingReboot
                    : HuaJuanCompatibilityState.Applied;
                result.Summary = pendingReboot
                    ? "鍵盤 Interception 已隔離；HuaJuan 滑鼠已鎖定硬體身分。重開機後鍵盤才會完全退出目前的裝置堆疊。"
                    : "鍵盤使用 Windows kbdclass；HuaJuan 僅保留滑鼠輸出，並依硬體身分固定目標。";
            }
            else if (result.CompatibleDllCount > 0 || result.KeyboardIsolated)
            {
                result.State = HuaJuanCompatibilityState.Partial;
                result.Summary = "鍵盤隔離或 HuaJuan 硬體身分 shim 狀態不完整；請重新套用或使用備份還原。";
            }
            else
            {
                result.State = HuaJuanCompatibilityState.Original;
                result.Summary = "鍵盤仍經過 Interception；可隔離鍵盤，並將 HuaJuan 滑鼠改為輸出專用硬體身分模式。";
            }
            return result;
        }

        public HuaJuanCompatibilityResult Apply()
        {
            try
            {
                return ApplyCore();
            }
            catch (Exception ex)
            {
                AppLog.Write("Input Stack Compatibility",
                    "Apply preparation failed: " + ex);
                return Failure("無法準備鍵盤 Interception 隔離：" + ex.Message);
            }
        }

        private HuaJuanCompatibilityResult ApplyCore()
        {
            if (!PayloadsAvailable())
                return Failure("找不到 Guard Center 的 HuaJuan shim、sparse backend 或設定 payload。");
            if (!Directory.Exists(huaJuanRoot))
                return Failure("找不到 HuaJuan 安裝目錄。");
            if (isHuaJuanRunning())
                return Failure("請先完全關閉 HuaJuan，再套用修復。");

            string[] targets = GetExistingTargetPaths();
            if (targets.Length == 0)
                return Failure("HuaJuan 安裝目錄內沒有 interception.dll。");

            Directory.CreateDirectory(stateRoot);
            string payloadHash = HashFile(shimPayloadPath);
            string[] originalKeyboardFilters = upperFilters.ReadKeyboard();
            string[] mouseFilters = upperFilters.ReadMouse();
            Dictionary<string, string> conflictingPostReboot =
                postRebootStore.ReadConflicting();
            if (!Contains(mouseFilters, "mouse"))
                return Failure("Mouse UpperFilters 沒有 Interception mouse；為避免破壞 HuaJuan，修復已停止。");

            HuaJuanBackupManifest active = TryReadManifest(activeManifestPath);
            int compatibleCount = CountMatchingHashes(targets, payloadHash);
            if (compatibleCount > 0 && active == null)
                return Failure("偵測到部分相容 DLL，但找不到原版備份；不會覆寫無法還原的狀態。");

            if (compatibleCount == targets.Length
                && ManagedRuntimePayloadsAreCurrent(targets)
                && !Contains(originalKeyboardFilters, "keyboard"))
            {
                postRebootStore.RemoveConflicting();
                return Success("鍵盤 Interception 已經隔離；衝突的舊版開機後修復已取消。",
                    RequiresReboot(active));
            }

            HuaJuanBackupManifest manifest = active;
            if (manifest == null)
            {
                manifest = CreateBackupManifest(targets, originalKeyboardFilters, payloadHash);
            }
            else if (!ManifestMatchesTargets(manifest, targets))
            {
                return Failure("現有備份與 HuaJuan 安裝內容不一致；請先還原或清理不完整狀態。");
            }

            try
            {
                WriteManifest(activeManifestPath, manifest);
                ReplaceTargets(targets, shimPayloadPath);
                DeployManagedRuntimePayloads(targets);
                upperFilters.WriteKeyboard(BuildIsolatedKeyboardFilters(
                    manifest.OriginalKeyboardUpperFilters));
                postRebootStore.RemoveConflicting();
                manifest.AppliedAtUtc = DateTime.UtcNow;
                manifest.PayloadSha256 = payloadHash;
                WriteManifest(activeManifestPath, manifest);
                AppLog.Write("Input Stack Compatibility",
                    "Applied HuaJuan output-only hardware identity shim with sparse backend; keyboard UpperFilters="
                    + string.Join(",", upperFilters.ReadKeyboard())
                    + "; mouse UpperFilters=" + string.Join(",", mouseFilters)
                    + "; canceled conflicting RunOnce=" + conflictingPostReboot.Count);
                return Success("已隔離鍵盤 Interception、固定 HuaJuan 滑鼠輸出，並取消衝突的舊版開機後修復。請重新啟動電腦。",
                    true);
            }
            catch (Exception ex)
            {
                try
                {
                    RestoreFiles(manifest);
                    RemoveManagedRuntimePayloads(targets);
                    upperFilters.WriteKeyboard(manifest.OriginalKeyboardUpperFilters);
                    postRebootStore.Restore(conflictingPostReboot);
                }
                catch (Exception rollback)
                {
                    AppLog.Write("Input Stack Compatibility",
                        "Apply rollback failed: " + rollback);
                    return Failure("修復失敗，而且自動回復未完整完成：" + rollback.Message);
                }
                TryDelete(activeManifestPath);
                AppLog.Write("Input Stack Compatibility", "Apply failed and rolled back: " + ex);
                return Failure("修復失敗，已還原原始狀態：" + ex.Message);
            }
        }

        public HuaJuanCompatibilityResult Restore()
        {
            try
            {
                return RestoreCore();
            }
            catch (Exception ex)
            {
                AppLog.Write("Input Stack Compatibility",
                    "Restore preparation failed: " + ex);
                return Failure("無法準備鍵盤 Interception 隔離還原：" + ex.Message);
            }
        }

        private HuaJuanCompatibilityResult RestoreCore()
        {
            if (isHuaJuanRunning())
                return Failure("請先完全關閉 HuaJuan，再執行還原。");
            HuaJuanBackupManifest manifest = TryReadManifest(activeManifestPath);
            if (!ManifestBackupsExist(manifest))
                return Failure("找不到可用的鍵盤 Interception 隔離備份。");

            string[] beforeKeyboard = upperFilters.ReadKeyboard();
            string rollbackRoot = Path.Combine(stateRoot,
                "restore-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rollbackRoot);
            var rollbackFiles = new List<HuaJuanBackupFile>();
            try
            {
                for (int i = 0; i < manifest.Files.Count; i++)
                {
                    string target = ResolveTarget(manifest.Files[i].RelativePath);
                    if (!File.Exists(target)) continue;
                    string rollbackPath = Path.Combine(rollbackRoot, i + ".dll");
                    File.Copy(target, rollbackPath, true);
                    rollbackFiles.Add(new HuaJuanBackupFile
                    {
                        RelativePath = manifest.Files[i].RelativePath,
                        BackupPath = rollbackPath
                    });
                }

                RestoreFiles(manifest);
                RemoveManagedRuntimePayloads(GetExistingTargetPaths());
                upperFilters.WriteKeyboard(manifest.OriginalKeyboardUpperFilters);
                manifest.RestoredAtUtc = DateTime.UtcNow;
                WriteManifest(Path.Combine(manifest.BackupRoot, "restored-manifest.json"), manifest);
                TryDelete(activeManifestPath);
                TryDeleteDirectory(rollbackRoot);
                AppLog.Write("Input Stack Compatibility",
                    "Restored original HuaJuan DLLs and keyboard UpperFilters.");
                return Success("已還原原版 HuaJuan DLL 與 Keyboard UpperFilters。請重新啟動電腦。",
                    true);
            }
            catch (Exception ex)
            {
                try
                {
                    RestoreFileList(rollbackFiles);
                    DeployManagedRuntimePayloads(GetExistingTargetPaths());
                    upperFilters.WriteKeyboard(beforeKeyboard);
                }
                catch (Exception rollback)
                {
                    AppLog.Write("Input Stack Compatibility",
                        "Restore rollback failed: " + rollback);
                    return Failure("還原失敗，而且回復還原前狀態未完整完成：" + rollback.Message);
                }
                AppLog.Write("Input Stack Compatibility", "Restore failed and rolled back: " + ex);
                return Failure("還原失敗，已回復執行前狀態：" + ex.Message);
            }
        }

        internal static string[] BuildIsolatedKeyboardFilters(string[] original)
        {
            var values = new List<string>();
            bool hasKbdClass = false;
            if (original != null)
            {
                for (int i = 0; i < original.Length; i++)
                {
                    string value = original[i] == null ? string.Empty : original[i].Trim();
                    if (value.Length == 0
                        || string.Equals(value, "keyboard", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(value, "kbdclass", StringComparison.OrdinalIgnoreCase))
                        hasKbdClass = true;
                    if (!Contains(values.ToArray(), value)) values.Add(value);
                }
            }
            if (!hasKbdClass) values.Add("kbdclass");
            return values.ToArray();
        }

        private HuaJuanBackupManifest CreateBackupManifest(string[] targets,
            string[] keyboardFilters, string payloadHash)
        {
            string backupRoot = Path.Combine(stateRoot,
                "backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupRoot);
            var manifest = new HuaJuanBackupManifest
            {
                Version = 1,
                CreatedAtUtc = DateTime.UtcNow,
                BackupRoot = backupRoot,
                PayloadSha256 = payloadHash,
                OriginalKeyboardUpperFilters = keyboardFilters == null
                    ? Array.Empty<string>() : (string[])keyboardFilters.Clone()
            };
            for (int i = 0; i < targets.Length; i++)
            {
                string backup = Path.Combine(backupRoot, i + "-interception.dll");
                File.Copy(targets[i], backup, false);
                manifest.Files.Add(new HuaJuanBackupFile
                {
                    RelativePath = Path.GetRelativePath(huaJuanRoot, targets[i]),
                    BackupPath = backup,
                    OriginalSha256 = HashFile(targets[i])
                });
            }
            WriteManifest(Path.Combine(backupRoot, "original-manifest.json"), manifest);
            return manifest;
        }

        private void ReplaceTargets(string[] targets, string source)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                ReplaceFile(targets[i], source);
            }
        }

        private void DeployManagedRuntimePayloads(string[] targets)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Length; i++)
            {
                string directory = Path.GetDirectoryName(targets[i]) ?? string.Empty;
                if (!directories.Add(directory)) continue;
                ReplaceFile(Path.Combine(directory, "guardcenter-interception-real.dll"),
                    realPayloadPath);
                ReplaceFile(Path.Combine(directory, "guardcenter-huajuan-shim.ini"),
                    configPayloadPath);
            }
        }

        private void RemoveManagedRuntimePayloads(string[] targets)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Length; i++)
            {
                string directory = Path.GetDirectoryName(targets[i]) ?? string.Empty;
                if (!directories.Add(directory)) continue;
                TryDelete(Path.Combine(directory, "guardcenter-interception-real.dll"));
                TryDelete(Path.Combine(directory, "guardcenter-huajuan-shim.ini"));
            }
        }

        private bool ManagedRuntimePayloadsAreCurrent(string[] targets)
        {
            if (!PayloadsAvailable()) return false;
            string realHash = HashFile(realPayloadPath);
            string configHash = HashFile(configPayloadPath);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Length; i++)
            {
                string directory = Path.GetDirectoryName(targets[i]) ?? string.Empty;
                if (!directories.Add(directory)) continue;
                string real = Path.Combine(directory, "guardcenter-interception-real.dll");
                string config = Path.Combine(directory, "guardcenter-huajuan-shim.ini");
                if (!File.Exists(real) || !File.Exists(config)
                    || !string.Equals(HashFile(real), realHash,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(HashFile(config), configHash,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return directories.Count > 0;
        }

        private bool PayloadsAvailable()
        {
            return File.Exists(shimPayloadPath)
                && File.Exists(realPayloadPath)
                && File.Exists(configPayloadPath);
        }

        private static void ReplaceFile(string target, string source)
        {
            string temporary = target + ".guardcenter-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, true);
                File.Move(temporary, target, true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        private void RestoreFiles(HuaJuanBackupManifest manifest)
        {
            RestoreFileList(manifest.Files);
        }

        private void RestoreFileList(List<HuaJuanBackupFile> files)
        {
            for (int i = 0; i < files.Count; i++)
            {
                string target = ResolveTarget(files[i].RelativePath);
                string temporary = target + ".guardcenter-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(files[i].BackupPath, temporary, true);
                    File.Move(temporary, target, true);
                }
                finally
                {
                    TryDelete(temporary);
                }
            }
        }

        private string ResolveTarget(string relativePath)
        {
            string root = huaJuanRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(huaJuanRoot, relativePath));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("備份包含 HuaJuan 目錄以外的路徑。");
            return target;
        }

        private string[] GetExistingTargetPaths()
        {
            var result = new List<string>();
            for (int i = 0; i < HuaJuanCompatibilityPaths.RelativeDllPaths.Length; i++)
            {
                string path = ResolveTarget(HuaJuanCompatibilityPaths.RelativeDllPaths[i]);
                if (File.Exists(path)) result.Add(path);
            }
            return result.ToArray();
        }

        private bool ManifestMatchesTargets(HuaJuanBackupManifest manifest, string[] targets)
        {
            if (!ManifestBackupsExist(manifest) || manifest.Files.Count != targets.Length)
                return false;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Length; i++)
                expected.Add(Path.GetRelativePath(huaJuanRoot, targets[i]));
            for (int i = 0; i < manifest.Files.Count; i++)
                if (!expected.Contains(manifest.Files[i].RelativePath)) return false;
            return true;
        }

        private static bool ManifestBackupsExist(HuaJuanBackupManifest manifest)
        {
            if (manifest == null || manifest.Files == null || manifest.Files.Count == 0)
                return false;
            for (int i = 0; i < manifest.Files.Count; i++)
                if (string.IsNullOrWhiteSpace(manifest.Files[i].BackupPath)
                    || !File.Exists(manifest.Files[i].BackupPath)) return false;
            return true;
        }

        private static bool RequiresReboot(HuaJuanBackupManifest manifest)
        {
            if (manifest == null || manifest.AppliedAtUtc == default) return false;
            DateTime boot = DateTime.UtcNow
                - TimeSpan.FromMilliseconds(Environment.TickCount64);
            return boot < manifest.AppliedAtUtc;
        }

        private static int CountMatchingHashes(string[] targets, string hash)
        {
            int result = 0;
            for (int i = 0; i < targets.Length; i++)
                if (string.Equals(HashFile(targets[i]), hash,
                    StringComparison.OrdinalIgnoreCase)) result++;
            return result;
        }

        internal static bool Contains(string[] values, string expected)
        {
            if (values == null) return false;
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static string HashFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static HuaJuanBackupManifest TryReadManifest(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<HuaJuanBackupManifest>(
                    File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex)
            {
                AppLog.Write("Input Stack Compatibility",
                    "Could not read compatibility manifest: " + ex.Message);
                return null;
            }
        }

        private static void WriteManifest(string path, HuaJuanBackupManifest manifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporary, path, true);
        }

        private static HuaJuanCompatibilityResult Success(string message, bool reboot)
        {
            return new HuaJuanCompatibilityResult
            {
                Succeeded = true,
                RebootRequired = reboot,
                Message = message
            };
        }

        private static HuaJuanCompatibilityResult Failure(string message)
        {
            return new HuaJuanCompatibilityResult { Message = message };
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static bool IsDefaultHuaJuanRunning()
        {
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                using (processes[i])
                {
                    try
                    {
                        if (string.Equals(processes[i].ProcessName, "ApexRecoilControl",
                            StringComparison.OrdinalIgnoreCase)) return true;
                        string path = processes[i].MainModule == null
                            ? string.Empty : processes[i].MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path)
                            && path.StartsWith(HuaJuanCompatibilityPaths.HuaJuanRoot
                                + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch
                    {
                    }
                }
            }
            return false;
        }
    }

    internal sealed class HuaJuanBackupManifest
    {
        public int Version;
        public DateTime CreatedAtUtc;
        public DateTime AppliedAtUtc;
        public DateTime RestoredAtUtc;
        public string BackupRoot = string.Empty;
        public string PayloadSha256 = string.Empty;
        public string[] OriginalKeyboardUpperFilters = Array.Empty<string>();
        public List<HuaJuanBackupFile> Files = new List<HuaJuanBackupFile>();
    }

    internal sealed class HuaJuanBackupFile
    {
        public string RelativePath = string.Empty;
        public string BackupPath = string.Empty;
        public string OriginalSha256 = string.Empty;
    }

    internal static class HuaJuanCompatibilityElevation
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };

        public static async Task<HuaJuanCompatibilityResult> RunAsync(
            HuaJuanCompatibilityOperation operation)
        {
            if (IsAdministrator())
                return RunLocal(operation);

            string operationId = Guid.NewGuid().ToString("N");
            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                executablePath = AppPaths.InstalledExePath;
            Directory.CreateDirectory(HuaJuanCompatibilityPaths.StateRoot);

            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--input-stack-compatibility-helper --operation "
                        + (operation == HuaJuanCompatibilityOperation.Apply ? "apply" : "restore")
                        + " --operation-id " + operationId,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppPaths.Root,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new HuaJuanCompatibilityResult
                {
                    OperationId = operationId,
                    Canceled = true,
                    Message = "Administrator approval was canceled."
                };
            }
            if (process == null)
                return new HuaJuanCompatibilityResult
                    { OperationId = operationId, Message = "無法啟動系統管理員 Helper。" };

            using (process)
                await process.WaitForExitAsync().ConfigureAwait(false);

            try
            {
                HuaJuanCompatibilityResult result =
                    JsonSerializer.Deserialize<HuaJuanCompatibilityResult>(
                        File.ReadAllText(HuaJuanCompatibilityPaths.LastResultPath), JsonOptions);
                if (result != null && string.Equals(result.OperationId, operationId,
                    StringComparison.Ordinal)) return result;
            }
            catch (Exception ex)
            {
                AppLog.Write("Input Stack Compatibility",
                    "Could not read elevated result: " + ex.Message);
            }
            return new HuaJuanCompatibilityResult
            {
                OperationId = operationId,
                Message = "系統管理員 Helper 未回傳有效結果。"
            };
        }

        public static bool TryHandleCommandLine(string[] args)
        {
            if (!HasArg(args, "--input-stack-compatibility-helper")) return false;

            string operationId = GetArgValue(args, "--operation-id");
            string operationValue = GetArgValue(args, "--operation");
            bool validOperation = string.Equals(operationValue, "apply",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(operationValue, "restore", StringComparison.OrdinalIgnoreCase);
            HuaJuanCompatibilityOperation operation =
                string.Equals(operationValue, "restore", StringComparison.OrdinalIgnoreCase)
                    ? HuaJuanCompatibilityOperation.Restore
                    : HuaJuanCompatibilityOperation.Apply;
            HuaJuanCompatibilityResult result;
            if (!Guid.TryParseExact(operationId, "N", out _))
                result = new HuaJuanCompatibilityResult { Message = "Invalid operation id." };
            else if (!validOperation)
                result = new HuaJuanCompatibilityResult { Message = "Invalid operation." };
            else if (!IsAdministrator())
                result = new HuaJuanCompatibilityResult { Message = "Administrator access is required." };
            else
            {
                using (var mutex = new Mutex(false, "GuardCenter.InputStackCompatibility"))
                {
                    bool acquired = false;
                    try
                    {
                        acquired = mutex.WaitOne(0);
                        result = acquired
                            ? RunLocal(operation)
                            : new HuaJuanCompatibilityResult
                                { Message = "另一個輸入堆疊修復正在執行。" };
                    }
                    finally
                    {
                        if (acquired) mutex.ReleaseMutex();
                    }
                }
            }

            result.OperationId = operationId;
            try
            {
                Directory.CreateDirectory(HuaJuanCompatibilityPaths.StateRoot);
                string temporary = HuaJuanCompatibilityPaths.LastResultPath
                    + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
                File.Move(temporary, HuaJuanCompatibilityPaths.LastResultPath, true);
            }
            catch (Exception ex)
            {
                AppLog.Write("Input Stack Compatibility",
                    "Could not persist elevated result: " + ex);
            }
            Environment.ExitCode = result.Succeeded ? 0 : 1;
            return true;
        }

        private static HuaJuanCompatibilityResult RunLocal(
            HuaJuanCompatibilityOperation operation)
        {
            try
            {
                var engine = new HuaJuanCompatibilityEngine();
                return operation == HuaJuanCompatibilityOperation.Apply
                    ? engine.Apply() : engine.Restore();
            }
            catch (Exception ex)
            {
                AppLog.Write("Input Stack Compatibility",
                    "Unhandled compatibility operation failure: " + ex);
                return new HuaJuanCompatibilityResult
                {
                    Message = "輸入堆疊操作失敗：" + ex.Message
                };
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool HasArg(string[] args, string expected)
        {
            return Array.FindIndex(args, delegate(string value)
            {
                return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
            }) >= 0;
        }

        private static string GetArgValue(string[] args, string expected)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return string.Empty;
        }
    }
}
