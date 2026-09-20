using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GuardCenter
{
    internal sealed class AudioMixerModule
    {
        private readonly AudioMixerSettings settings;
        private readonly HashSet<string> defaultedAppKeys;
        private readonly HashSet<string> manualAppKeys;
        private readonly object operationGate = new object();
        private Guid eventContext = new Guid("1640067E-5CE7-4A7F-A24C-BA10E2C2DAB8");
        private string lastStatus = "Audio mixer ready.";
        private bool settingsDirty;

        public event EventHandler StatusChanged;

        public AudioMixerModule(AudioMixerSettings settings)
        {
            this.settings = settings;
            defaultedAppKeys = DecodeKeySet(settings.DefaultedAppKeys);
            manualAppKeys = DecodeKeySet(settings.ManualAppKeys);
        }

        public string StatusText
        {
            get { return lastStatus; }
        }

        public List<AudioAppVolume> GetAppVolumes()
        {
            lock (operationGate)
            {
                return GetAppVolumesCore();
            }
        }

        private List<AudioAppVolume> GetAppVolumesCore()
        {
            var groups = new Dictionary<string, AudioAppVolumeGroup>(StringComparer.Ordinal);
            List<AudioSessionSnapshot> sessions = EnumerateSessions(true);
            int defaultedCount = 0;

            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionSnapshot session = sessions[i];
                if (session.State == AudioSessionState.Expired)
                {
                    continue;
                }

                string key = GetSessionKey(session);
                AudioAppVolumeGroup group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new AudioAppVolumeGroup
                    {
                        Key = key,
                        Name = session.DisplayName,
                        ProcessId = session.ProcessId,
                        IsSystemSounds = session.IsSystemSounds,
                        IconPath = session.IconPath
                    };
                    groups.Add(key, group);
                }

                group.SessionCount++;
                group.VolumeSum += session.VolumePercent;
                group.MutedCount += session.Muted ? 1 : 0;
                defaultedCount += session.DefaultedToBaseline ? 1 : 0;
                if (session.State == AudioSessionState.Active)
                {
                    group.ActiveCount++;
                }
            }

            var items = new List<AudioAppVolume>();
            foreach (AudioAppVolumeGroup group in groups.Values)
            {
                int volume = group.SessionCount > 0
                    ? (int)Math.Round(group.VolumeSum / (double)group.SessionCount)
                    : 0;

                string state = group.ActiveCount > 0 ? "Active" : "Idle";
                string description = group.IsSystemSounds
                    ? "System audio session · " + state
                    : "PID " + group.ProcessId + " · " + group.SessionCount + " session"
                        + (group.SessionCount == 1 ? string.Empty : "s") + " · " + state;

                items.Add(new AudioAppVolume
                {
                    Key = group.Key,
                    Name = group.Name,
                    Description = description,
                    IconPath = group.IconPath,
                    IsSystemSounds = group.IsSystemSounds,
                    VolumePercent = Clamp(volume, 0, 100),
                    Muted = group.MutedCount > 0,
                    Active = group.ActiveCount > 0
                });
            }

            items.Sort(delegate(AudioAppVolume left, AudioAppVolume right)
            {
                if (left.IsSystemSounds != right.IsSystemSounds)
                {
                    return left.IsSystemSounds ? -1 : 1;
                }

                int activeCompare = right.Active.CompareTo(left.Active);
                return activeCompare != 0
                    ? activeCompare
                    : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            SetStatus("Audio mixer found " + items.Count + " app session" + (items.Count == 1 ? "." : "s.")
                + (defaultedCount > 0 ? " Defaulted " + defaultedCount + " to " + settings.DefaultVolumePercent + "%." : string.Empty));
            return items;
        }

        public void SetAppVolume(string key, int volumePercent)
        {
            lock (operationGate)
            {
                SetAppVolumeCore(key, volumePercent);
            }
        }

        private void SetAppVolumeCore(string key, int volumePercent)
        {
            var touchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int affected = ForEachMatchingSession(key, delegate(ISimpleAudioVolume volume)
            {
                float scalar = Clamp(volumePercent, 0, 100) / 100.0f;
                CheckHR(volume.SetMasterVolume(scalar, ref eventContext), "ISimpleAudioVolume.SetMasterVolume");
            }, delegate(AudioSessionSnapshot session)
            {
                if (!string.IsNullOrWhiteSpace(session.StableKey))
                {
                    touchedKeys.Add(session.StableKey);
                }
            });

            MarkManualAppKeys(touchedKeys);

            SetStatus("Set app volume to " + Clamp(volumePercent, 0, 100) + "% for " + affected + " session"
                + (affected == 1 ? "." : "s."));
        }

        public void SetAppMute(string key, bool muted)
        {
            lock (operationGate)
            {
                SetAppMuteCore(key, muted);
            }
        }

        private void SetAppMuteCore(string key, bool muted)
        {
            int affected = ForEachMatchingSession(key, delegate(ISimpleAudioVolume volume)
            {
                CheckHR(volume.SetMute(muted, ref eventContext), "ISimpleAudioVolume.SetMute");
            });

            SetStatus((muted ? "Muted " : "Unmuted ") + affected + " app session"
                + (affected == 1 ? "." : "s."));
        }

        public bool ConsumeSettingsDirty()
        {
            lock (operationGate)
            {
                bool dirty = settingsDirty;
                settingsDirty = false;
                return dirty;
            }
        }

        private int ForEachMatchingSession(string key, Action<ISimpleAudioVolume> action)
        {
            return ForEachMatchingSession(key, action, null);
        }

        private int ForEachMatchingSession(string key, Action<ISimpleAudioVolume> action, Action<AudioSessionSnapshot> onMatched)
        {
            int affected = 0;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessionEnumerator = null;
            object activated = null;

            try
            {
                enumerator = CreateEnumerator();
                CheckHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device),
                    "GetDefaultAudioEndpoint");

                Guid iid = typeof(IAudioSessionManager2).GUID;
                CheckHR(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out activated),
                    "IMMDevice.Activate(IAudioSessionManager2)");
                manager = (IAudioSessionManager2)activated;

                CheckHR(manager.GetSessionEnumerator(out sessionEnumerator), "IAudioSessionManager2.GetSessionEnumerator");

                int count;
                CheckHR(sessionEnumerator.GetCount(out count), "IAudioSessionEnumerator.GetCount");

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl control = null;
                    try
                    {
                        CheckHR(sessionEnumerator.GetSession(i, out control), "IAudioSessionEnumerator.GetSession");
                        IAudioSessionControl2 control2 = control as IAudioSessionControl2;
                        ISimpleAudioVolume volume = control as ISimpleAudioVolume;
                        if (control2 == null || volume == null)
                        {
                            continue;
                        }

                        AudioSessionSnapshot session = ReadSessionSnapshot(control, control2, volume);
                        if (session.State != AudioSessionState.Expired && string.Equals(GetSessionKey(session), key, StringComparison.Ordinal))
                        {
                            action(volume);
                            if (onMatched != null)
                            {
                                onMatched(session);
                            }
                            affected++;
                        }
                    }
                    finally
                    {
                        SafeRelease(control);
                    }
                }

                return affected;
            }
            finally
            {
                SafeRelease(sessionEnumerator);
                if (manager != null)
                {
                    SafeRelease(manager);
                }
                else
                {
                    SafeRelease(activated);
                }
                SafeRelease(device);
                SafeRelease(enumerator);
            }
        }

        private List<AudioSessionSnapshot> EnumerateSessions(bool applyDefaultVolume)
        {
            var sessions = new List<AudioSessionSnapshot>();
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessionEnumerator = null;
            object activated = null;

            try
            {
                enumerator = CreateEnumerator();
                CheckHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device),
                    "GetDefaultAudioEndpoint");

                Guid iid = typeof(IAudioSessionManager2).GUID;
                CheckHR(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out activated),
                    "IMMDevice.Activate(IAudioSessionManager2)");
                manager = (IAudioSessionManager2)activated;

                CheckHR(manager.GetSessionEnumerator(out sessionEnumerator), "IAudioSessionManager2.GetSessionEnumerator");

                int count;
                CheckHR(sessionEnumerator.GetCount(out count), "IAudioSessionEnumerator.GetCount");

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl control = null;
                    try
                    {
                        CheckHR(sessionEnumerator.GetSession(i, out control), "IAudioSessionEnumerator.GetSession");
                        IAudioSessionControl2 control2 = control as IAudioSessionControl2;
                        ISimpleAudioVolume volume = control as ISimpleAudioVolume;
                        if (control2 == null || volume == null)
                        {
                            continue;
                        }

                        AudioSessionSnapshot session = ReadSessionSnapshot(control, control2, volume);
                        if (applyDefaultVolume)
                        {
                            ApplyDefaultVolumeIfNeeded(session, volume);
                        }
                        sessions.Add(session);
                    }
                    finally
                    {
                        SafeRelease(control);
                    }
                }

                return sessions;
            }
            finally
            {
                SafeRelease(sessionEnumerator);
                if (manager != null)
                {
                    SafeRelease(manager);
                }
                else
                {
                    SafeRelease(activated);
                }
                SafeRelease(device);
                SafeRelease(enumerator);
            }
        }

        private static AudioSessionSnapshot ReadSessionSnapshot(
            IAudioSessionControl control,
            IAudioSessionControl2 control2,
            ISimpleAudioVolume volume)
        {
            AudioSessionState state;
            CheckHR(control.GetState(out state), "IAudioSessionControl.GetState");

            uint processId;
            CheckHR(control2.GetProcessId(out processId), "IAudioSessionControl2.GetProcessId");

            string sessionId;
            control2.GetSessionInstanceIdentifier(out sessionId);

            string displayName;
            control.GetDisplayName(out displayName);

            float level;
            CheckHR(volume.GetMasterVolume(out level), "ISimpleAudioVolume.GetMasterVolume");

            bool muted;
            CheckHR(volume.GetMute(out muted), "ISimpleAudioVolume.GetMute");

            bool isSystemSounds = control2.IsSystemSoundsSession() == HResult.SOk;

            return new AudioSessionSnapshot
            {
                ProcessId = processId,
                InstanceId = sessionId ?? string.Empty,
                DisplayName = ResolveDisplayName(processId, displayName, isSystemSounds),
                IconPath = ResolveIconPath(processId, isSystemSounds),
                StableKey = ResolveStableKey(processId, displayName, isSystemSounds),
                VolumePercent = Clamp((int)Math.Round(level * 100.0f), 0, 100),
                Muted = muted,
                State = state,
                IsSystemSounds = isSystemSounds
            };
        }

        private void ApplyDefaultVolumeIfNeeded(AudioSessionSnapshot session, ISimpleAudioVolume volume)
        {
            if (session.State == AudioSessionState.Expired || string.IsNullOrWhiteSpace(session.StableKey))
            {
                return;
            }

            if (manualAppKeys.Contains(session.StableKey) || defaultedAppKeys.Contains(session.StableKey))
            {
                return;
            }

            int target = Clamp(settings.DefaultVolumePercent, 0, 100);
            float scalar = target / 100.0f;
            CheckHR(volume.SetMasterVolume(scalar, ref eventContext), "ISimpleAudioVolume.SetMasterVolume(default)");
            session.VolumePercent = target;
            session.DefaultedToBaseline = true;

            defaultedAppKeys.Add(session.StableKey);
            settings.DefaultedAppKeys = EncodeKeySet(defaultedAppKeys);
            settingsDirty = true;
        }

        private static string ResolveDisplayName(uint processId, string sessionDisplayName, bool isSystemSounds)
        {
            if (isSystemSounds)
            {
                return "System sounds";
            }

            if (!string.IsNullOrWhiteSpace(sessionDisplayName))
            {
                string trimmed = sessionDisplayName.Trim();
                if (trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    string resolved = ResolveIndirectString(trimmed);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved.Trim();
                    }

                    if (trimmed.IndexOf("AudioSrv", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "System sounds";
                    }
                }

                return trimmed;
            }

            if (processId == 0)
            {
                return "Unknown app";
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    try
                    {
                        string description = process.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            return description.Trim();
                        }
                    }
                    catch
                    {
                        // Process module metadata can be inaccessible across integrity levels.
                    }

                    return process.ProcessName;
                }
            }
            catch
            {
                return "Process " + processId;
            }
        }

        private static string ResolveIconPath(uint processId, bool isSystemSounds)
        {
            if (isSystemSounds || processId == 0)
            {
                return string.Empty;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return process.MainModule.FileName ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveStableKey(uint processId, string sessionDisplayName, bool isSystemSounds)
        {
            if (isSystemSounds)
            {
                return "system-sounds";
            }

            if (processId != 0)
            {
                string path = ResolveIconPath(processId, false);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return "exe:" + path.ToUpperInvariant();
                }
            }

            if (!string.IsNullOrWhiteSpace(sessionDisplayName))
            {
                return "display:" + sessionDisplayName.Trim().ToUpperInvariant();
            }

            return processId != 0 ? "pid:" + processId : string.Empty;
        }

        private static string ResolveIndirectString(string indirectString)
        {
            try
            {
                var builder = new StringBuilder(512);
                int hr = SHLoadIndirectString(indirectString, builder, builder.Capacity, IntPtr.Zero);
                return hr == HResult.SOk ? builder.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetSessionKey(AudioSessionSnapshot session)
        {
            if (session.IsSystemSounds)
            {
                return "system-sounds";
            }

            if (session.ProcessId != 0)
            {
                return "pid:" + session.ProcessId;
            }

            return "session:" + session.InstanceId;
        }

        private void SetStatus(string status)
        {
            lastStatus = status;
            EventHandler handler = StatusChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void MarkManualAppKeys(HashSet<string> keys)
        {
            bool changed = false;
            foreach (string key in keys)
            {
                if (manualAppKeys.Add(key))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                settings.ManualAppKeys = EncodeKeySet(manualAppKeys);
                settingsDirty = true;
            }
        }

        private static HashSet<string> DecodeKeySet(string encoded)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] pieces = (encoded ?? string.Empty).Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pieces.Length; i++)
            {
                try
                {
                    string key = Encoding.UTF8.GetString(Convert.FromBase64String(pieces[i]));
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        set.Add(key);
                    }
                }
                catch
                {
                    // Ignore malformed persisted keys.
                }
            }

            return set;
        }

        private static string EncodeKeySet(HashSet<string> keys)
        {
            var encoded = new List<string>();
            foreach (string key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    encoded.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)));
                }
            }

            encoded.Sort(StringComparer.Ordinal);
            return string.Join(",", encoded.ToArray());
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static void CheckHR(int hr, string operation)
        {
            if (hr < 0)
            {
                throw new InvalidOperationException(operation + " failed with HRESULT 0x" + hr.ToString("X8"),
                    Marshal.GetExceptionForHR(hr));
            }
        }

        private static void SafeRelease(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }

        private static IMMDeviceEnumerator CreateEnumerator()
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            return (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int SHLoadIndirectString(string source, StringBuilder outputBuffer,
            int outputBufferChars, IntPtr reserved);

        private sealed class AudioSessionSnapshot
        {
            public uint ProcessId;
            public string InstanceId;
            public string DisplayName;
            public string IconPath;
            public string StableKey;
            public int VolumePercent;
            public bool Muted;
            public AudioSessionState State;
            public bool IsSystemSounds;
            public bool DefaultedToBaseline;
        }

        private sealed class AudioAppVolumeGroup
        {
            public string Key;
            public string Name;
            public string IconPath;
            public uint ProcessId;
            public bool IsSystemSounds;
            public int SessionCount;
            public int ActiveCount;
            public int MutedCount;
            public int VolumeSum;
        }
    }

    internal sealed class AudioAppVolume
    {
        public string Key;
        public string Name;
        public string Description;
        public string IconPath;
        public bool IsSystemSounds;
        public int VolumePercent;
        public bool Muted;
        public bool Active;
    }
}
