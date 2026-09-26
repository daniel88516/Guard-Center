using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GuardCenter
{
    internal enum AudioZeroDeviceEvent
    {
        DeviceStateChanged,
        DeviceAdded,
        DeviceRemoved,
        DefaultRenderChanged,
        PropertyChanged
    }

    internal enum AudioZeroEventAction
    {
        Ignore,
        CheckTopology,
        ZeroImmediately
    }

    internal static class AudioZeroEventPolicy
    {
        public static AudioZeroEventAction Resolve(AudioZeroDeviceEvent deviceEvent,
            bool reactToPropertyChanges, uint newDeviceState = DeviceStateMask.Active)
        {
            if (deviceEvent == AudioZeroDeviceEvent.PropertyChanged)
            {
                return reactToPropertyChanges
                    ? AudioZeroEventAction.CheckTopology
                    : AudioZeroEventAction.Ignore;
            }

            if (deviceEvent == AudioZeroDeviceEvent.DeviceStateChanged
                && (newDeviceState & DeviceStateMask.Active) == 0)
            {
                return AudioZeroEventAction.CheckTopology;
            }

            return AudioZeroEventAction.ZeroImmediately;
        }
    }

    internal sealed class AudioZeroModule : IDisposable
    {
        private readonly object zeroLock = new object();
        private readonly object logLock = new object();
        private readonly System.Windows.Forms.Timer topologyTimer;
        private readonly NotificationClient notificationClient;
        private Guid eventContext = new Guid("6B612612-0E8A-45F7-B55F-AFB39F9328DD");
        private IMMDeviceEnumerator notificationEnumerator;
        private AudioZeroSettings settings;
        private bool disposed;
        private bool running;
        private int pendingZero;
        private int pollingTopology;
        private int zeroCount;
        private string lastTopologySignature;
        private string lastStatus = "Audio Zero Guard idle.";

        public event EventHandler StatusChanged;

        public AudioZeroModule(AudioZeroSettings settings)
        {
            this.settings = settings.Clone();
            notificationClient = new NotificationClient(this);

            topologyTimer = new System.Windows.Forms.Timer();
            topologyTimer.Interval = ClampPollInterval(this.settings.PollIntervalMs);
            topologyTimer.Tick += delegate { QueueTopologyCheck("timer", false); };
        }

        public string StatusText
        {
            get { return lastStatus; }
        }

        public void Start()
        {
            if (disposed || running)
            {
                return;
            }

            running = true;
            lastStatus = "Audio Zero Guard starting.";
            Log("Starting Audio Zero Guard.");

            try
            {
                RegisterForAudioEvents();
                topologyTimer.Interval = ClampPollInterval(settings.PollIntervalMs);
                topologyTimer.Start();
                QueueTopologyCheck("initial", true);
                if (settings.ZeroOnEnable)
                {
                    ScheduleZero("guard-enabled");
                }
                SetStatus("Audio Zero Guard running.");
            }
            catch (Exception ex)
            {
                SetStatus("Audio Zero Guard failed to start: " + ex.Message);
                Log("Start failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            if (!running)
            {
                return;
            }

            running = false;
            topologyTimer.Stop();
            UnregisterForAudioEvents();
            SetStatus("Audio Zero Guard disabled.");
            Log("Stopped Audio Zero Guard.");
        }

        public void ApplySettings(AudioZeroSettings newSettings)
        {
            settings = newSettings.Clone();
            topologyTimer.Interval = ClampPollInterval(settings.PollIntervalMs);

            if (settings.Enabled)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }

        public void ZeroNow(string reason)
        {
            ScheduleZero(reason);
        }

        public void OnAudioDeviceChanged(AudioZeroDeviceEvent deviceEvent, string reason,
            uint newDeviceState = DeviceStateMask.Active)
        {
            if (!running)
            {
                return;
            }

            AudioZeroEventAction action = AudioZeroEventPolicy.Resolve(deviceEvent,
                settings.ReactToPropertyChanges, newDeviceState);
            if (action == AudioZeroEventAction.Ignore)
            {
                return;
            }

            Log("CoreAudio event: " + reason);
            if (action == AudioZeroEventAction.CheckTopology)
            {
                QueueTopologyCheck(reason, false);
                return;
            }

            ScheduleZero(reason);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Stop();
            topologyTimer.Dispose();
        }

        private void RegisterForAudioEvents()
        {
            UnregisterForAudioEvents();
            notificationEnumerator = CreateEnumerator();
            CheckHR(notificationEnumerator.RegisterEndpointNotificationCallback(notificationClient),
                "RegisterEndpointNotificationCallback");
            Log("Registered CoreAudio endpoint notification callback.");
        }

        private void UnregisterForAudioEvents()
        {
            if (notificationEnumerator == null)
            {
                return;
            }

            try
            {
                CheckHR(notificationEnumerator.UnregisterEndpointNotificationCallback(notificationClient),
                    "UnregisterEndpointNotificationCallback");
            }
            catch (Exception ex)
            {
                Log("Unregister failed: " + ex.Message);
            }
            finally
            {
                SafeRelease(notificationEnumerator);
                notificationEnumerator = null;
            }
        }

        private void QueueTopologyCheck(string source, bool verbose)
        {
            if (disposed || !running)
            {
                return;
            }

            if (Interlocked.Exchange(ref pollingTopology, 1) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    CheckTopologyChangeWorker(source, verbose);
                }
                finally
                {
                    Interlocked.Exchange(ref pollingTopology, 0);
                }
            });
        }

        private void CheckTopologyChangeWorker(string source, bool verbose)
        {
            try
            {
                if (verbose)
                {
                    Log("Topology read begin: " + source);
                }

                string current = ReadTopologySignature(verbose);

                if (verbose)
                {
                    Log("Topology read end: " + source + " => " + current);
                }

                if (lastTopologySignature == null)
                {
                    lastTopologySignature = current;
                    return;
                }

                if (!string.Equals(current, lastTopologySignature, StringComparison.Ordinal))
                {
                    Log("Topology changed. Old=[" + lastTopologySignature + "] New=[" + current + "]");
                    lastTopologySignature = current;
                    ScheduleZero("topology-poll-changed");
                }
            }
            catch (Exception ex)
            {
                Log("Topology poll failed: " + ex.Message);
            }
        }

        private void ScheduleZero(string reason)
        {
            if (disposed || !settings.Enabled)
            {
                return;
            }

            if (Interlocked.Exchange(ref pendingZero, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        ZeroAllPlaybackDevices(reason);

                        int[] delays = ParseRetryDelays(settings.RetryDelaysCsv);
                        for (int i = 0; i < delays.Length; i++)
                        {
                            Thread.Sleep(delays[i]);
                            ZeroAllPlaybackDevices(reason + " retry-" + delays[i] + "ms");
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref pendingZero, 0);
                    }
                });
            }
            else
            {
                Log("Zero already pending; coalesced reason: " + reason);
            }
        }

        private void ZeroAllPlaybackDevices(string reason)
        {
            lock (zeroLock)
            {
                Log("Zero begin: " + reason);

                int affected = 0;
                int failed = 0;

                IMMDeviceEnumerator enumerator = null;
                IMMDeviceCollection collection = null;

                try
                {
                    enumerator = CreateEnumerator();
                    CheckHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateMask.All, out collection),
                        "EnumAudioEndpoints");

                    uint count;
                    CheckHR(collection.GetCount(out count), "IMMDeviceCollection.GetCount");

                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice device = null;
                        IAudioEndpointVolume volume = null;
                        object activated = null;

                        try
                        {
                            CheckHR(collection.Item(i, out device), "IMMDeviceCollection.Item");

                            uint state;
                            CheckHR(device.GetState(out state), "IMMDevice.GetState");
                            if ((state & DeviceStateMask.Active) == 0)
                            {
                                continue;
                            }

                            Guid iid = typeof(IAudioEndpointVolume).GUID;
                            CheckHR(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out activated),
                                "IMMDevice.Activate(IAudioEndpointVolume)");
                            volume = (IAudioEndpointVolume)activated;

                            if (settings.SetMute)
                            {
                                CheckHR(volume.SetMute(true, ref eventContext), "IAudioEndpointVolume.SetMute");
                            }

                            if (settings.SetVolumeZero)
                            {
                                CheckHR(volume.SetMasterVolumeLevelScalar(0.0f, ref eventContext),
                                    "IAudioEndpointVolume.SetMasterVolumeLevelScalar");
                            }

                            affected++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Log("Zero device failed: " + ex.Message);
                        }
                        finally
                        {
                            if (volume != null)
                            {
                                SafeRelease(volume);
                            }
                            else
                            {
                                SafeRelease(activated);
                            }
                            SafeRelease(device);
                        }
                    }

                    zeroCount++;
                    SetStatus(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} | {1} | zeroed {2} devices, {3} failed",
                        DateTime.Now, reason, affected, failed));
                    Log(lastStatus);
                }
                catch (Exception ex)
                {
                    SetStatus(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} | {1} | failed: {2}",
                        DateTime.Now, reason, ex.Message));
                    Log(lastStatus);
                }
                finally
                {
                    SafeRelease(collection);
                    SafeRelease(enumerator);
                }
            }
        }

        private string ReadTopologySignature(bool verbose)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;

            try
            {
                if (verbose)
                {
                    Log("Topology: create enumerator");
                }

                enumerator = CreateEnumerator();

                var parts = new List<string>();

                if (verbose) Log("Topology: get default console");
                parts.Add("default-console=" + GetDefaultId(enumerator, ERole.eConsole));

                if (verbose) Log("Topology: get default multimedia");
                parts.Add("default-multimedia=" + GetDefaultId(enumerator, ERole.eMultimedia));

                if (verbose) Log("Topology: get default communications");
                parts.Add("default-communications=" + GetDefaultId(enumerator, ERole.eCommunications));

                if (verbose) Log("Topology: enum active render endpoints");
                CheckHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateMask.Active, out collection),
                    "EnumAudioEndpoints(active)");

                uint count;
                CheckHR(collection.GetCount(out count), "IMMDeviceCollection.GetCount(active)");

                if (verbose)
                {
                    Log("Topology: active count=" + count);
                }

                var activeIds = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        CheckHR(collection.Item(i, out device), "IMMDeviceCollection.Item(active)");

                        string id;
                        CheckHR(device.GetId(out id), "IMMDevice.GetId");
                        activeIds.Add(id ?? "(null)");
                    }
                    finally
                    {
                        SafeRelease(device);
                    }
                }

                activeIds.Sort(StringComparer.Ordinal);
                parts.Add("active=" + string.Join(",", activeIds.ToArray()));

                return string.Join(" | ", parts.ToArray());
            }
            finally
            {
                SafeRelease(collection);
                SafeRelease(enumerator);
            }
        }

        private static string GetDefaultId(IMMDeviceEnumerator enumerator, ERole role)
        {
            IMMDevice device = null;
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out device);
                if (hr < 0 || device == null)
                {
                    return "(none:" + hr.ToString("X8") + ")";
                }

                string id;
                hr = device.GetId(out id);
                if (hr < 0)
                {
                    return "(id-error:" + hr.ToString("X8") + ")";
                }

                return id ?? "(null)";
            }
            finally
            {
                SafeRelease(device);
            }
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

        private void Log(string message)
        {
            try
            {
                AppPaths.EnsureRoot();
                lock (logLock)
                {
                    File.AppendAllText(AppPaths.LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never interfere with audio protection.
            }
        }

        private static int[] ParseRetryDelays(string csv)
        {
            var delays = new List<int>();
            string[] pieces = (csv ?? string.Empty).Split(',');
            for (int i = 0; i < pieces.Length; i++)
            {
                int value;
                if (int.TryParse(pieces[i].Trim(), out value) && value >= 0 && value <= 10000)
                {
                    delays.Add(value);
                }
            }

            if (delays.Count == 0)
            {
                delays.Add(100);
                delays.Add(400);
                delays.Add(1000);
            }

            return delays.ToArray();
        }

        private static int ClampPollInterval(int value)
        {
            return Math.Max(50, Math.Min(5000, value));
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
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioZeroModule module;

        public NotificationClient(AudioZeroModule module)
        {
            this.module = module;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            module.OnAudioDeviceChanged(AudioZeroDeviceEvent.DeviceStateChanged,
                "device-state-changed device=" + FormatDeviceId(deviceId)
                + " state=0x" + newState.ToString("X8"), newState);
            return HResult.SOk;
        }

        public int OnDeviceAdded(string deviceId)
        {
            module.OnAudioDeviceChanged(AudioZeroDeviceEvent.DeviceAdded,
                "device-added device=" + FormatDeviceId(deviceId));
            return HResult.SOk;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            module.OnAudioDeviceChanged(AudioZeroDeviceEvent.DeviceRemoved,
                "device-removed device=" + FormatDeviceId(deviceId));
            return HResult.SOk;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
        {
            if (flow == EDataFlow.eRender)
            {
                module.OnAudioDeviceChanged(AudioZeroDeviceEvent.DefaultRenderChanged,
                    "default-render-changed role=" + role
                    + " device=" + FormatDeviceId(defaultDeviceId));
            }

            return HResult.SOk;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            module.OnAudioDeviceChanged(AudioZeroDeviceEvent.PropertyChanged,
                "device-property-changed device=" + FormatDeviceId(deviceId)
                + " key={" + key.fmtid.ToString("D") + "}," + key.pid);
            return HResult.SOk;
        }

        private static string FormatDeviceId(string deviceId)
        {
            return string.IsNullOrEmpty(deviceId) ? "(none)" : deviceId;
        }
    }
}

