using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace GuardCenter
{
    internal interface IPointerPrecisionService
    {
        bool TryGetEnhancedPointerPrecision(out bool enabled, out string error);
        bool TryDisableEnhancedPointerPrecision(out string error);
    }

    internal sealed class WindowsPointerPrecisionService : IPointerPrecisionService
    {
        private const uint SpiGetMouse = 0x0003;
        private const uint SpiSetMouse = 0x0004;
        private const uint SpifUpdateIniFile = 0x0001;
        private const uint SpifSendChange = 0x0002;

        public bool TryGetEnhancedPointerPrecision(out bool enabled, out string error)
        {
            int[] values = new int[3];
            if (!SystemParametersInfo(SpiGetMouse, 0, values, 0))
            {
                enabled = false;
                error = GetLastErrorMessage();
                return false;
            }

            enabled = values[2] != 0;
            error = string.Empty;
            return true;
        }

        public bool TryDisableEnhancedPointerPrecision(out string error)
        {
            int[] values = new int[3];
            if (!SystemParametersInfo(SpiGetMouse, 0, values, 0))
            {
                error = GetLastErrorMessage();
                return false;
            }

            values[2] = 0;
            if (!SystemParametersInfo(SpiSetMouse, 0, values,
                SpifUpdateIniFile | SpifSendChange))
            {
                error = GetLastErrorMessage();
                return false;
            }

            bool enabled;
            if (!TryGetEnhancedPointerPrecision(out enabled, out error))
            {
                return false;
            }
            if (enabled)
            {
                error = "Windows still reports Enhance pointer precision as enabled.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static string GetLastErrorMessage()
        {
            int error = Marshal.GetLastWin32Error();
            return error == 0 ? "Windows did not return an error code."
                : new Win32Exception(error).Message + " (" + error + ")";
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter,
            [In, Out] int[] values, uint updateFlags);
    }

    internal sealed class PointerPrecisionGuard : IDisposable
    {
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

        private readonly IPointerPrecisionService service;
        private readonly Action<string> reportStatus;
        private readonly TimeSpan interval;
        private readonly object syncRoot = new object();
        private readonly Timer timer;
        private bool enabled;
        private bool disposed;
        private int checkRunning;

        public PointerPrecisionGuard(IPointerPrecisionService service, Action<string> reportStatus)
            : this(service, reportStatus, CheckInterval)
        {
        }

        internal PointerPrecisionGuard(IPointerPrecisionService service, Action<string> reportStatus,
            TimeSpan interval)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.reportStatus = reportStatus;
            this.interval = interval <= TimeSpan.Zero ? CheckInterval : interval;
            timer = new Timer(Timer_Tick, null, Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        internal bool IsMonitoring
        {
            get
            {
                lock (syncRoot)
                {
                    return enabled && !disposed;
                }
            }
        }

        public void SetEnabled(bool value)
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }
                enabled = value;
                timer.Change(value ? interval : Timeout.InfiniteTimeSpan,
                    value ? interval : Timeout.InfiniteTimeSpan);
            }

            if (value)
            {
                CheckNow(true);
            }
        }

        internal void CheckNowForTest()
        {
            CheckNow(false);
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                enabled = false;
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            timer.Dispose();
        }

        private void Timer_Tick(object state)
        {
            CheckNow(false);
        }

        private void CheckNow(bool reportHealthy)
        {
            lock (syncRoot)
            {
                if (disposed || !enabled)
                {
                    return;
                }
            }
            if (Interlocked.Exchange(ref checkRunning, 1) != 0)
            {
                return;
            }

            try
            {
                bool precisionEnabled;
                string error;
                if (!service.TryGetEnhancedPointerPrecision(out precisionEnabled, out error))
                {
                    Report("Pointer acceleration guard could not read the Windows setting: " + error);
                    return;
                }
                if (!precisionEnabled)
                {
                    if (reportHealthy)
                    {
                        Report("Pointer acceleration guard enabled; Enhance pointer precision is off.");
                    }
                    return;
                }

                lock (syncRoot)
                {
                    if (disposed || !enabled)
                    {
                        return;
                    }
                }
                if (service.TryDisableEnhancedPointerPrecision(out error))
                {
                    Report("Pointer acceleration guard turned Enhance pointer precision off.");
                }
                else
                {
                    Report("Pointer acceleration guard could not turn Enhance pointer precision off: "
                        + error);
                }
            }
            finally
            {
                Interlocked.Exchange(ref checkRunning, 0);
            }
        }

        private void Report(string message)
        {
            Action<string> callback = reportStatus;
            if (callback != null)
            {
                callback(message);
            }
        }
    }
}
