using System;

namespace GuardCenter
{
    internal enum InputStackHealth
    {
        Healthy,
        Unknown
    }

    internal enum HuaJuanCompatibilityState
    {
        NotInstalled,
        Original,
        AppliedPendingReboot,
        Applied,
        Partial,
        Unknown
    }

    internal sealed class InputStackSnapshot
    {
        public DateTime CapturedAt;
        public DateTime BootTime;
        public InputStackHealth Health = InputStackHealth.Unknown;
        public string Summary = string.Empty;
        public string DiagnosticError = string.Empty;
        public string[] KeyboardUpperFilters = Array.Empty<string>();
        public string[] MouseUpperFilters = Array.Empty<string>();
        public string[] KeyboardClassDevices = Array.Empty<string>();
        public string[] PointerClassDevices = Array.Empty<string>();
        public int HighestKeyboardClassIndex = -1;
        public int HighestPointerClassIndex = -1;
        public int HidSurpriseRemovalCount;
        public int InputSurpriseRemovalCount;
        public bool KeyboardInterceptionInstalled;
        public bool MouseInterceptionInstalled;
        public bool KeyboardDriverExists;
        public bool MouseDriverExists;
        public bool KeyboardDriverSignatureValid;
        public bool MouseDriverSignatureValid;
        public string KeyboardDriverVersion = string.Empty;
        public string MouseDriverVersion = string.Empty;
        public string KeyboardDriverProduct = string.Empty;
        public string MouseDriverProduct = string.Empty;
        public bool HuaJuanInstalled;
        public bool HuaJuanPayloadAvailable;
        public bool HuaJuanBackupAvailable;
        public bool HuaJuanKeyboardIsolated;
        public bool HuaJuanMouseInterceptionRetained;
        public int HuaJuanCompatibleDllCount;
        public int HuaJuanInterceptionDllCount;
        public DateTime HuaJuanRepairAppliedAt;
        public HuaJuanCompatibilityState HuaJuanCompatibilityState =
            HuaJuanCompatibilityState.NotInstalled;
        public string HuaJuanCompatibilitySummary = string.Empty;

        public bool HasInterception
        {
            get { return KeyboardInterceptionInstalled || MouseInterceptionInstalled; }
        }

        public InputStackSnapshot Clone()
        {
            return new InputStackSnapshot
            {
                CapturedAt = CapturedAt,
                BootTime = BootTime,
                Health = Health,
                Summary = Summary,
                DiagnosticError = DiagnosticError,
                KeyboardUpperFilters = CloneArray(KeyboardUpperFilters),
                MouseUpperFilters = CloneArray(MouseUpperFilters),
                KeyboardClassDevices = CloneArray(KeyboardClassDevices),
                PointerClassDevices = CloneArray(PointerClassDevices),
                HighestKeyboardClassIndex = HighestKeyboardClassIndex,
                HighestPointerClassIndex = HighestPointerClassIndex,
                HidSurpriseRemovalCount = HidSurpriseRemovalCount,
                InputSurpriseRemovalCount = InputSurpriseRemovalCount,
                KeyboardInterceptionInstalled = KeyboardInterceptionInstalled,
                MouseInterceptionInstalled = MouseInterceptionInstalled,
                KeyboardDriverExists = KeyboardDriverExists,
                MouseDriverExists = MouseDriverExists,
                KeyboardDriverSignatureValid = KeyboardDriverSignatureValid,
                MouseDriverSignatureValid = MouseDriverSignatureValid,
                KeyboardDriverVersion = KeyboardDriverVersion,
                MouseDriverVersion = MouseDriverVersion,
                KeyboardDriverProduct = KeyboardDriverProduct,
                MouseDriverProduct = MouseDriverProduct,
                HuaJuanInstalled = HuaJuanInstalled,
                HuaJuanPayloadAvailable = HuaJuanPayloadAvailable,
                HuaJuanBackupAvailable = HuaJuanBackupAvailable,
                HuaJuanKeyboardIsolated = HuaJuanKeyboardIsolated,
                HuaJuanMouseInterceptionRetained = HuaJuanMouseInterceptionRetained,
                HuaJuanCompatibleDllCount = HuaJuanCompatibleDllCount,
                HuaJuanInterceptionDllCount = HuaJuanInterceptionDllCount,
                HuaJuanRepairAppliedAt = HuaJuanRepairAppliedAt,
                HuaJuanCompatibilityState = HuaJuanCompatibilityState,
                HuaJuanCompatibilitySummary = HuaJuanCompatibilitySummary
            };
        }

        private static string[] CloneArray(string[] values)
        {
            return values == null ? Array.Empty<string>() : (string[])values.Clone();
        }
    }
}
