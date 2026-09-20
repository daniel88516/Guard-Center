using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace GuardCenter
{
    internal interface IPowerRequestLease : IDisposable
    {
        bool KeepDisplayOn { get; }
    }

    internal interface IPowerRequestLeaseFactory
    {
        bool TryCreate(bool keepDisplayOn, out IPowerRequestLease lease, out string error);
    }

    internal sealed class WindowsPowerRequestLeaseFactory : IPowerRequestLeaseFactory
    {
        private const uint PowerRequestContextVersion = 0;
        private const uint PowerRequestContextSimpleString = 0x00000001;
        private const string Reason = "Guard Center Power Guard is temporarily keeping the system awake.";

        public bool TryCreate(bool keepDisplayOn, out IPowerRequestLease lease, out string error)
        {
            lease = null;
            error = string.Empty;
            IntPtr reasonString = IntPtr.Zero;
            SafePowerRequestHandle handle = null;
            bool systemSet = false;
            bool displaySet = false;

            try
            {
                reasonString = Marshal.StringToHGlobalUni(Reason);
                var context = new ReasonContext
                {
                    Version = PowerRequestContextVersion,
                    Flags = PowerRequestContextSimpleString,
                    Reason = new ReasonContextUnion { SimpleReasonString = reasonString }
                };

                handle = NativeMethods.PowerCreateRequest(ref context);
                if (handle == null || handle.IsInvalid)
                {
                    int code = Marshal.GetLastWin32Error();
                    if (handle != null)
                    {
                        handle.Dispose();
                    }
                    error = BuildError("Windows 無法建立保持清醒要求", code);
                    return false;
                }

                if (!NativeMethods.PowerSetRequest(handle, PowerRequestType.SystemRequired))
                {
                    int code = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    error = BuildError("Windows 無法啟用系統保持清醒", code);
                    return false;
                }
                systemSet = true;

                if (keepDisplayOn)
                {
                    if (!NativeMethods.PowerSetRequest(handle, PowerRequestType.DisplayRequired))
                    {
                        int code = Marshal.GetLastWin32Error();
                        ClearRequest(handle, PowerRequestType.SystemRequired, "rollback system request");
                        handle.Dispose();
                        error = BuildError("Windows 無法啟用螢幕恆亮", code);
                        return false;
                    }
                    displaySet = true;
                }

                lease = new WindowsPowerRequestLease(handle, systemSet, displaySet);
                handle = null;
                return true;
            }
            catch (Exception ex)
            {
                if (handle != null)
                {
                    if (displaySet)
                    {
                        ClearRequest(handle, PowerRequestType.DisplayRequired, "exception display request");
                    }
                    if (systemSet)
                    {
                        ClearRequest(handle, PowerRequestType.SystemRequired, "exception system request");
                    }
                    handle.Dispose();
                }
                error = "Windows 保持清醒 API 呼叫失敗：" + ex.Message;
                AppLog.Write("Power Guard", "Power request creation failed: " + ex);
                return false;
            }
            finally
            {
                if (reasonString != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(reasonString);
                }
            }
        }

        internal static bool ClearRequest(SafePowerRequestHandle handle, PowerRequestType type, string operation)
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed)
            {
                return true;
            }

            if (NativeMethods.PowerClearRequest(handle, type))
            {
                return true;
            }

            int code = Marshal.GetLastWin32Error();
            AppLog.Write("Power Guard", operation + " failed. Win32 error " + code + ": "
                + new Win32Exception(code).Message);
            return false;
        }

        private static string BuildError(string prefix, int code)
        {
            return prefix + "。Windows 錯誤 " + code + "：" + new Win32Exception(code).Message;
        }

        private sealed class WindowsPowerRequestLease : IPowerRequestLease
        {
            private SafePowerRequestHandle handle;
            private readonly bool systemSet;
            private readonly bool displaySet;
            private int disposed;

            public WindowsPowerRequestLease(SafePowerRequestHandle handle, bool systemSet, bool displaySet)
            {
                this.handle = handle;
                this.systemSet = systemSet;
                this.displaySet = displaySet;
            }

            public bool KeepDisplayOn
            {
                get { return displaySet; }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                SafePowerRequestHandle current = Interlocked.Exchange(ref handle, null);
                if (current == null)
                {
                    return;
                }

                if (displaySet)
                {
                    ClearRequest(current, PowerRequestType.DisplayRequired, "clear display request");
                }
                if (systemSet)
                {
                    ClearRequest(current, PowerRequestType.SystemRequired, "clear system request");
                }
                current.Dispose();
            }
        }

        internal enum PowerRequestType
        {
            DisplayRequired = 0,
            SystemRequired = 1,
            AwayModeRequired = 2,
            ExecutionRequired = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            public ReasonContextUnion Reason;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct ReasonContextUnion
        {
            [FieldOffset(0)]
            public ReasonContextDetailed Detailed;

            [FieldOffset(0)]
            public IntPtr SimpleReasonString;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ReasonContextDetailed
        {
            public IntPtr LocalizedReasonModule;
            public uint LocalizedReasonId;
            public uint ReasonStringCount;
            public IntPtr ReasonStrings;
        }

        internal sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafePowerRequestHandle()
                : base(true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return NativeMethods.CloseHandle(handle);
            }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern SafePowerRequestHandle PowerCreateRequest(ref ReasonContext context);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PowerSetRequest(SafePowerRequestHandle powerRequest,
                PowerRequestType requestType);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PowerClearRequest(SafePowerRequestHandle powerRequest,
                PowerRequestType requestType);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
        }
    }
}
