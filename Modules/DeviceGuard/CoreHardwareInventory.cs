using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuardCenter
{
    internal static class DeviceClassGuids
    {
        public const string Bluetooth = "{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}";
        public const string Display = "{4D36E968-E325-11CE-BFC1-08002BE10318}";
        public const string Net = "{4D36E972-E325-11CE-BFC1-08002BE10318}";
        public const string Media = "{4D36E96C-E325-11CE-BFC1-08002BE10318}";
        public const string Camera = "{CA3E7AB9-B4C3-4AE6-8251-579EF933890F}";
        public const string Image = "{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}";
        public const string Usb = "{36FC9E60-C465-11CF-8056-444553540000}";
        public const string Keyboard = "{4D36E96B-E325-11CE-BFC1-08002BE10318}";
        public const string Mouse = "{4D36E96F-E325-11CE-BFC1-08002BE10318}";
        public const string Hid = "{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}";
        public const string DiskDrive = "{4D36E967-E325-11CE-BFC1-08002BE10318}";
        public const string ScsiAdapter = "{4D36E97B-E325-11CE-BFC1-08002BE10318}";
        public const string Volume = "{71A27CDD-812A-11D0-BEC7-08002BE2092F}";
    }

    internal sealed class CoreHardwareBaselineDocument
    {
        public int SchemaVersion = CoreHardwareBaselineStore.CurrentSchemaVersion;
        public List<CoreHardwareBaselineEntry> Entries = new List<CoreHardwareBaselineEntry>();
    }

    internal sealed class CoreHardwareBaselineEntry
    {
        public string Id = string.Empty;
        public CoreHardwareCapability Capability;
        public string DisplayName = string.Empty;
        public string LastInstanceId = string.Empty;
        public string ParentInstanceId = string.Empty;
        public string[] HardwareIds = Array.Empty<string>();
        public string[] LocationPaths = Array.Empty<string>();
        public string DriverInfPath = string.Empty;
        public string DriverVersion = string.Empty;
        public string Manufacturer = string.Empty;
        public DateTime FirstHealthyUtc;
        public DateTime LastHealthyUtc;
    }

    internal sealed class CoreHardwareResolver
    {
        public List<CoreHardwareItem> Resolve(WindowsPnPSnapshot snapshot,
            CoreHardwareBaselineDocument baseline)
        {
            var result = new List<CoreHardwareItem>();
            if (snapshot == null)
            {
                return result;
            }

            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                PnPDeviceNode node = snapshot.Devices[i];
                CoreHardwareCapability capability;
                if (!TryClassifyAnchor(node, out capability))
                {
                    continue;
                }
                result.Add(CreateItem(snapshot, node, capability));
            }

            ApplyBaseline(result, baseline);
            AttachUsbRecoveryTargets(result, snapshot);
            result.Sort(delegate(CoreHardwareItem left, CoreHardwareItem right)
            {
                int capability = left.Capability.CompareTo(right.Capability);
                return capability != 0 ? capability : string.Compare(left.DisplayName, right.DisplayName,
                    StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        public CoreHardwareItem ResolveOne(WindowsPnPSnapshot snapshot,
            CoreHardwareBaselineDocument baseline, string id)
        {
            List<CoreHardwareItem> items = Resolve(snapshot, baseline);
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return items[i];
                }
            }
            return null;
        }

        internal static bool TryClassifyAnchor(PnPDeviceNode node, out CoreHardwareCapability capability)
        {
            capability = CoreHardwareCapability.Bluetooth;
            if (node == null || string.IsNullOrWhiteSpace(node.ClassGuid))
            {
                return false;
            }

            string enumerator = node.EnumeratorName ?? string.Empty;
            if (GuidEquals(node.ClassGuid, DeviceClassGuids.Bluetooth)
                && IsOneOf(enumerator, "USB", "PCI", "ACPI"))
            {
                capability = CoreHardwareCapability.Bluetooth;
                return true;
            }
            if (GuidEquals(node.ClassGuid, DeviceClassGuids.Display)
                && IsOneOf(enumerator, "PCI", "ACPI"))
            {
                capability = CoreHardwareCapability.Graphics;
                return true;
            }
            if (GuidEquals(node.ClassGuid, DeviceClassGuids.Net)
                && (IsOneOf(enumerator, "PCI", "ACPI")
                    || (string.Equals(enumerator, "USB", StringComparison.OrdinalIgnoreCase)
                        && node.RemovalPolicy == 1)))
            {
                capability = CoreHardwareCapability.Network;
                return true;
            }
            if (GuidEquals(node.ClassGuid, DeviceClassGuids.Media)
                && (IsOneOf(enumerator, "HDAUDIO", "INTELAUDIO")
                    || (string.Equals(enumerator, "USB", StringComparison.OrdinalIgnoreCase)
                        && node.RemovalPolicy == 1)))
            {
                capability = CoreHardwareCapability.Audio;
                return true;
            }
            if ((GuidEquals(node.ClassGuid, DeviceClassGuids.Camera)
                    || GuidEquals(node.ClassGuid, DeviceClassGuids.Image))
                && (string.Equals(enumerator, "ACPI", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(enumerator, "USB", StringComparison.OrdinalIgnoreCase)
                        && node.RemovalPolicy == 1)))
            {
                capability = CoreHardwareCapability.Camera;
                return true;
            }
            if (GuidEquals(node.ClassGuid, DeviceClassGuids.Usb)
                && string.Equals(enumerator, "PCI", StringComparison.OrdinalIgnoreCase))
            {
                capability = CoreHardwareCapability.Usb;
                return true;
            }
            return false;
        }

        internal static RepairRiskLevel RestartRisk(CoreHardwareCapability capability)
        {
            if (capability == CoreHardwareCapability.Graphics
                || capability == CoreHardwareCapability.Network)
            {
                return RepairRiskLevel.High;
            }
            if (capability == CoreHardwareCapability.Usb)
            {
                return RepairRiskLevel.Critical;
            }
            return RepairRiskLevel.Medium;
        }

        internal static RepairRiskLevel RemoveRisk(CoreHardwareCapability capability)
        {
            return capability == CoreHardwareCapability.Bluetooth
                || capability == CoreHardwareCapability.Audio
                || capability == CoreHardwareCapability.Camera
                ? RepairRiskLevel.High
                : RepairRiskLevel.Critical;
        }

        private static CoreHardwareItem CreateItem(WindowsPnPSnapshot snapshot, PnPDeviceNode anchor,
            CoreHardwareCapability capability)
        {
            List<PnPDeviceNode> descendants = snapshot.GetDescendants(anchor.InstanceId);
            var nodeIds = new List<string> { anchor.InstanceId };
            for (int i = 0; i < descendants.Count; i++)
            {
                nodeIds.Add(descendants[i].InstanceId);
            }

            var item = new CoreHardwareItem
            {
                Id = CreateStableId(capability, anchor.HardwareIds, anchor.LocationPaths, anchor.InstanceId),
                Capability = capability,
                DisplayName = string.IsNullOrWhiteSpace(anchor.DisplayName)
                    ? CapabilityDisplayName(capability)
                    : anchor.DisplayName,
                AnchorInstanceId = anchor.InstanceId,
                NodeInstanceIds = nodeIds.ToArray(),
                HardwareIds = Clone(anchor.HardwareIds),
                LocationPaths = Clone(anchor.LocationPaths),
                ParentInstanceId = anchor.ParentInstanceId,
                DriverInfPath = anchor.DriverInfPath,
                DriverVersion = anchor.DriverVersion,
                Manufacturer = anchor.Manufacturer,
                ProblemCode = anchor.ProblemCode,
                IsPresent = anchor.IsPresent,
                IsStarted = anchor.IsStarted,
                Health = DetermineHealth(anchor)
            };

            if (capability == CoreHardwareCapability.Usb)
            {
                EvaluateUsbRestartSafety(item, descendants);
            }
            return item;
        }

        private static void EvaluateUsbRestartSafety(CoreHardwareItem item, IList<PnPDeviceNode> descendants)
        {
            for (int i = 0; i < descendants.Count; i++)
            {
                PnPDeviceNode node = descendants[i];
                if (!node.IsPresent)
                {
                    continue;
                }
                if (GuidEquals(node.ClassGuid, DeviceClassGuids.Keyboard)
                    || GuidEquals(node.ClassGuid, DeviceClassGuids.Mouse)
                    || GuidEquals(node.ClassGuid, DeviceClassGuids.Hid))
                {
                    item.UsbRestartBlocked = true;
                    item.UsbRestartBlockReason = "A currently present keyboard, mouse, or HID input device depends on this controller.";
                    return;
                }
                if (GuidEquals(node.ClassGuid, DeviceClassGuids.DiskDrive)
                    || GuidEquals(node.ClassGuid, DeviceClassGuids.ScsiAdapter)
                    || GuidEquals(node.ClassGuid, DeviceClassGuids.Volume))
                {
                    item.UsbRestartBlocked = true;
                    item.UsbRestartBlockReason = "A currently present storage or volume device depends on this controller.";
                    return;
                }
                if (GuidEquals(node.ClassGuid, DeviceClassGuids.Net)
                    || GuidEquals(node.ClassGuid, DeviceClassGuids.Display))
                {
                    item.UsbRestartBlocked = true;
                    item.UsbRestartBlockReason = "A currently present network or display device depends on this controller.";
                    return;
                }
            }
        }

        private static CoreHardwareHealth DetermineHealth(PnPDeviceNode node)
        {
            if (!node.IsPresent)
            {
                return CoreHardwareHealth.Missing;
            }
            if (node.ProblemCode == 22)
            {
                return CoreHardwareHealth.Disabled;
            }
            if (node.ProblemCode == 28)
            {
                return CoreHardwareHealth.DriverMissing;
            }
            if (node.ProblemCode == 14)
            {
                return CoreHardwareHealth.RebootRequired;
            }
            if (node.ProblemCode != 0 || !node.IsStarted)
            {
                return CoreHardwareHealth.Degraded;
            }
            return CoreHardwareHealth.Healthy;
        }

        private static void ApplyBaseline(List<CoreHardwareItem> items,
            CoreHardwareBaselineDocument baseline)
        {
            if (baseline == null || baseline.Entries == null)
            {
                return;
            }
            var matchedItems = new HashSet<CoreHardwareItem>();
            for (int b = 0; b < baseline.Entries.Count; b++)
            {
                CoreHardwareBaselineEntry entry = baseline.Entries[b];
                var matches = new List<CoreHardwareItem>();
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Capability == entry.Capability && BaselineMatches(items[i], entry))
                    {
                        matches.Add(items[i]);
                    }
                }
                if (matches.Count == 1)
                {
                    CoreHardwareItem item = matches[0];
                    item.Id = entry.Id;
                    item.FromBaseline = true;
                    HydrateMissingMetadata(item, entry);
                    matchedItems.Add(item);
                }
                else if (matches.Count > 1)
                {
                    items.Add(CreateMissingFromBaseline(entry, true));
                }
                else
                {
                    items.Add(CreateMissingFromBaseline(entry, false));
                }
            }
        }

        private static void HydrateMissingMetadata(CoreHardwareItem item,
            CoreHardwareBaselineEntry entry)
        {
            if (string.IsNullOrWhiteSpace(item.DisplayName)) item.DisplayName = entry.DisplayName;
            if (string.IsNullOrWhiteSpace(item.ParentInstanceId)) item.ParentInstanceId = entry.ParentInstanceId;
            if (string.IsNullOrWhiteSpace(item.DriverInfPath)) item.DriverInfPath = entry.DriverInfPath;
            if (string.IsNullOrWhiteSpace(item.DriverVersion)) item.DriverVersion = entry.DriverVersion;
            if (string.IsNullOrWhiteSpace(item.Manufacturer)) item.Manufacturer = entry.Manufacturer;
            if (item.HardwareIds == null || item.HardwareIds.Length == 0)
                item.HardwareIds = Clone(entry.HardwareIds);
            if (item.LocationPaths == null || item.LocationPaths.Length == 0)
                item.LocationPaths = Clone(entry.LocationPaths);
        }

        private static void AttachUsbRecoveryTargets(List<CoreHardwareItem> items,
            WindowsPnPSnapshot snapshot)
        {
            for (int i = 0; i < items.Count; i++)
            {
                CoreHardwareItem item = items[i];
                if (!SupportsUsbRecovery(item.Capability) || !HasUsbTransportEvidence(item)
                    || item.IsPresent || !item.FromBaseline || item.IdentityAmbiguous)
                {
                    continue;
                }

                var matches = new List<PnPDeviceNode>();
                for (int n = 0; n < snapshot.Devices.Count; n++)
                {
                    PnPDeviceNode candidate = snapshot.Devices[n];
                    if (IsUsbDescriptorFailure(candidate)
                        && PhysicalUsbPortMatches(item.LocationPaths, candidate.LocationPaths))
                    {
                        matches.Add(candidate);
                    }
                }

                if (matches.Count == 1)
                {
                    PnPDeviceNode recovery = matches[0];
                    item.RecoveryCandidate = new CoreHardwareRecoveryCandidate
                    {
                        Kind = CoreHardwareRecoveryKind.UsbDescriptorFailure,
                        InstanceId = recovery.InstanceId,
                        DisplayName = recovery.DisplayName,
                        ProblemCode = recovery.ProblemCode,
                        LocationPath = FirstValue(recovery.LocationPaths),
                        MatchEvidence = "Unique USB descriptor failure on the saved physical port."
                    };
                }
                else if (matches.Count > 1)
                {
                    item.IdentityAmbiguous = true;
                    item.Health = CoreHardwareHealth.Ambiguous;
                }
            }
        }

        internal static bool SupportsUsbRecovery(CoreHardwareCapability capability)
        {
            return capability == CoreHardwareCapability.Bluetooth
                || capability == CoreHardwareCapability.Audio
                || capability == CoreHardwareCapability.Camera;
        }

        internal static bool HasUsbTransportEvidence(CoreHardwareItem item)
        {
            return item != null && (StartsWithUsb(item.AnchorInstanceId)
                || AnyStartsWithUsb(item.HardwareIds)
                || AnyContains(item.LocationPaths, "#USBROOT("));
        }

        internal static bool IsUsbDescriptorFailure(PnPDeviceNode node)
        {
            return node != null && node.IsPresent && node.ProblemCode == 43
                && GuidEquals(node.ClassGuid, DeviceClassGuids.Usb)
                && string.Equals(node.EnumeratorName, "USB", StringComparison.OrdinalIgnoreCase)
                && (Contains(node.HardwareIds, @"USB\DEVICE_DESCRIPTOR_FAILURE")
                    || Contains(node.CompatibleIds, @"USB\DEVICE_DESCRIPTOR_FAILURE"));
        }

        internal static bool PhysicalUsbPortMatches(string[] expectedLocationPaths,
            string[] candidateLocationPaths)
        {
            if (expectedLocationPaths == null || candidateLocationPaths == null)
            {
                return false;
            }
            for (int e = 0; e < expectedLocationPaths.Length; e++)
            {
                string expected = NormalizePhysicalUsbPortPath(expectedLocationPaths[e]);
                if (string.IsNullOrWhiteSpace(expected))
                {
                    continue;
                }
                for (int c = 0; c < candidateLocationPaths.Length; c++)
                {
                    string candidate = NormalizePhysicalUsbPortPath(candidateLocationPaths[c]);
                    if (string.Equals(expected, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string NormalizePhysicalUsbPortPath(string value)
        {
            string path = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            string[] segments = path.Split('#');
            bool underAcpiRootHub = false;
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "ACPI(RHUB)", StringComparison.OrdinalIgnoreCase))
                {
                    underAcpiRootHub = true;
                    continue;
                }
                if (underAcpiRootHub && (segments[i].StartsWith("ACPI(HS", StringComparison.OrdinalIgnoreCase)
                        || segments[i].StartsWith("ACPI(SS", StringComparison.OrdinalIgnoreCase))
                    && segments[i].EndsWith(")", StringComparison.Ordinal))
                {
                    return string.Join("#", segments, 0, i + 1);
                }
            }

            var kept = new List<string>(segments);
            if (kept.Count > 0 && kept[kept.Count - 1].StartsWith("USBMI(",
                    StringComparison.OrdinalIgnoreCase))
            {
                kept.RemoveAt(kept.Count - 1);
            }
            if (kept.Count >= 2
                && kept[kept.Count - 1].StartsWith("USB(", StringComparison.OrdinalIgnoreCase)
                && string.Equals(kept[kept.Count - 1], kept[kept.Count - 2],
                    StringComparison.OrdinalIgnoreCase))
            {
                kept.RemoveAt(kept.Count - 1);
            }
            return string.Join("#", kept.ToArray());
        }

        private static bool StartsWithUsb(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AnyStartsWithUsb(string[] values)
        {
            if (values == null)
            {
                return false;
            }
            for (int i = 0; i < values.Length; i++)
            {
                if (StartsWithUsb(values[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AnyContains(string[] values, string expected)
        {
            if (values == null)
            {
                return false;
            }
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i])
                    && values[i].IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Contains(string[] values, string expected)
        {
            if (values == null)
            {
                return false;
            }
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FirstValue(string[] values)
        {
            return values == null || values.Length == 0 ? string.Empty : values[0];
        }

        private static bool BaselineMatches(CoreHardwareItem item, CoreHardwareBaselineEntry entry)
        {
            if (string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(entry.LastInstanceId)
                    && string.Equals(item.AnchorInstanceId, entry.LastInstanceId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return ArraysOverlap(item.HardwareIds, entry.HardwareIds)
                && ArraysOverlap(item.LocationPaths, entry.LocationPaths);
        }

        private static CoreHardwareItem CreateMissingFromBaseline(CoreHardwareBaselineEntry entry,
            bool ambiguous)
        {
            return new CoreHardwareItem
            {
                Id = entry.Id,
                Capability = entry.Capability,
                DisplayName = entry.DisplayName,
                AnchorInstanceId = entry.LastInstanceId,
                HardwareIds = Clone(entry.HardwareIds),
                LocationPaths = Clone(entry.LocationPaths),
                ParentInstanceId = entry.ParentInstanceId,
                DriverInfPath = entry.DriverInfPath,
                DriverVersion = entry.DriverVersion,
                Manufacturer = entry.Manufacturer,
                Health = ambiguous ? CoreHardwareHealth.Ambiguous : CoreHardwareHealth.Missing,
                FromBaseline = true,
                IdentityAmbiguous = ambiguous,
                IsPresent = false,
                IsStarted = false
            };
        }

        internal static string CreateStableId(CoreHardwareCapability capability, string[] hardwareIds,
            string[] locationPaths, string fallbackInstanceId)
        {
            string evidence = JoinNormalized(locationPaths) + "|" + JoinNormalized(hardwareIds);
            if (evidence == "|")
            {
                evidence = (fallbackInstanceId ?? string.Empty).ToUpperInvariant();
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(capability + "|" + evidence));
                var text = new StringBuilder();
                for (int i = 0; i < 12; i++)
                {
                    text.Append(hash[i].ToString("X2"));
                }
                return "core:" + capability.ToString().ToLowerInvariant() + ":" + text;
            }
        }

        internal static string CapabilityDisplayName(CoreHardwareCapability capability)
        {
            if (capability == CoreHardwareCapability.Usb) return "USB host controller";
            return capability.ToString();
        }

        private static string JoinNormalized(string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return string.Empty;
            }
            var copy = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i])) copy.Add(values[i].Trim().ToUpperInvariant());
            }
            copy.Sort(StringComparer.Ordinal);
            return string.Join(";", copy.ToArray());
        }

        private static bool ArraysOverlap(string[] left, string[] right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                for (int j = 0; j < right.Length; j++)
                {
                    if (string.Equals(left[i], right[j], StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static bool GuidEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOneOf(string value, params string[] choices)
        {
            for (int i = 0; i < choices.Length; i++)
            {
                if (string.Equals(value, choices[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string[] Clone(string[] values)
        {
            return values == null ? Array.Empty<string>() : (string[])values.Clone();
        }
    }

    internal static class CoreHardwareRepairPolicy
    {
        public static RepairRiskLevel MaximumRiskForRepairAll(CoreHardwareCapability capability)
        {
            return CoreHardwareResolver.RestartRisk(capability) <= RepairRiskLevel.Medium
                ? RepairRiskLevel.Medium : RepairRiskLevel.Low;
        }

        public static bool MayRestart(DeviceRepairTarget target, CoreHardwareHealth initialHealth,
            CoreHardwareItem current)
        {
            if (target == null || CoreHardwareResolver.RestartRisk(target.CoreCapability)
                > target.MaximumAuthorizedRisk)
                return false;
            if (target.Mode == DeviceRepairMode.RepairAll && initialHealth == CoreHardwareHealth.Healthy)
                return false;
            if (target.CoreCapability == CoreHardwareCapability.Usb
                && (target.Mode == DeviceRepairMode.RepairAll || current == null || current.UsbRestartBlocked))
                return false;
            return true;
        }

        public static bool MayRestartRecoveryTarget(DeviceRepairTarget target,
            CoreHardwareItem current)
        {
            return target != null && current != null
                && target.CoreCapability == current.Capability
                && CoreHardwareResolver.SupportsUsbRecovery(current.Capability)
                && CoreHardwareResolver.HasUsbTransportEvidence(current)
                && current.FromBaseline && !current.IsPresent && !current.IdentityAmbiguous
                && current.RecoveryCandidate != null
                && current.RecoveryCandidate.Kind == CoreHardwareRecoveryKind.UsbDescriptorFailure
                && current.RecoveryCandidate.ProblemCode == 43
                && !string.IsNullOrWhiteSpace(current.RecoveryCandidate.InstanceId)
                && CoreHardwareResolver.RestartRisk(current.Capability) <= target.MaximumAuthorizedRisk;
        }

        public static bool MayRemoveRecoveryTarget(DeviceRepairTarget target,
            CoreHardwareItem current)
        {
            return MayRestartRecoveryTarget(target, current)
                && target.Mode == DeviceRepairMode.Manual
                && CoreHardwareResolver.RemoveRisk(current.Capability) <= target.MaximumAuthorizedRisk;
        }
    }

    internal sealed class CoreHardwareBaselineStore
    {
        public const int CurrentSchemaVersion = 1;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true
        };

        private readonly string path;

        public CoreHardwareBaselineStore(string path)
        {
            this.path = path;
        }

        public CoreHardwareBaselineDocument Load()
        {
            CoreHardwareBaselineDocument document;
            if (TryLoad(path, out document))
            {
                return document;
            }
            if (File.Exists(path))
            {
                ArchiveCorruptPrimary();
            }
            string backup = path + ".bak";
            if (TryLoad(backup, out document))
            {
                AppLog.Write("Device Guard", "Recovered core hardware baseline from backup.");
                try
                {
                    SaveAtomic(document);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Device Guard", "Could not restore recovered baseline primary: " + ex.Message);
                }
                return document;
            }
            return new CoreHardwareBaselineDocument();
        }

        public bool MergeHealthy(CoreHardwareBaselineDocument document, IList<CoreHardwareItem> items)
        {
            if (document == null || items == null)
            {
                return false;
            }
            bool changed = false;
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < items.Count; i++)
            {
                CoreHardwareItem item = items[i];
                if (item.Health != CoreHardwareHealth.Healthy || !item.IsPresent
                    || item.IdentityAmbiguous || string.IsNullOrWhiteSpace(item.Id))
                {
                    continue;
                }
                CoreHardwareBaselineEntry entry = Find(document, item.Id);
                if (entry == null)
                {
                    entry = new CoreHardwareBaselineEntry { Id = item.Id, FirstHealthyUtc = now };
                    document.Entries.Add(entry);
                }
                entry.Capability = item.Capability;
                entry.DisplayName = item.DisplayName;
                entry.LastInstanceId = item.AnchorInstanceId;
                entry.ParentInstanceId = item.ParentInstanceId;
                entry.HardwareIds = item.HardwareIds == null ? Array.Empty<string>() : (string[])item.HardwareIds.Clone();
                entry.LocationPaths = item.LocationPaths == null ? Array.Empty<string>() : (string[])item.LocationPaths.Clone();
                entry.DriverInfPath = item.DriverInfPath;
                entry.DriverVersion = item.DriverVersion;
                entry.Manufacturer = item.Manufacturer;
                entry.LastHealthyUtc = now;
                changed = true;
            }
            if (changed)
            {
                try
                {
                    SaveAtomic(document);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Device Guard", "Could not persist the core hardware baseline: " + ex);
                    return false;
                }
            }
            return changed;
        }

        public void SaveAtomic(CoreHardwareBaselineDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.SchemaVersion = CurrentSchemaVersion;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak", true);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static CoreHardwareBaselineEntry Find(CoreHardwareBaselineDocument document, string id)
        {
            for (int i = 0; i < document.Entries.Count; i++)
            {
                if (string.Equals(document.Entries[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return document.Entries[i];
                }
            }
            return null;
        }

        private static bool TryLoad(string candidate, out CoreHardwareBaselineDocument document)
        {
            document = null;
            if (!File.Exists(candidate))
            {
                return false;
            }
            try
            {
                document = JsonSerializer.Deserialize<CoreHardwareBaselineDocument>(
                    File.ReadAllText(candidate, Encoding.UTF8), JsonOptions);
                if (document == null || document.SchemaVersion != CurrentSchemaVersion
                    || document.Entries == null)
                {
                    document = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("Device Guard", "Core hardware baseline load failed for " + candidate + ": " + ex);
                return false;
            }
        }

        private void ArchiveCorruptPrimary()
        {
            try
            {
                string archive = path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json";
                File.Move(path, archive);
                AppLog.Write("Device Guard", "Archived corrupt core hardware baseline to " + archive);
            }
            catch (Exception ex)
            {
                AppLog.Write("Device Guard", "Could not archive corrupt core hardware baseline: " + ex);
            }
        }
    }
}
