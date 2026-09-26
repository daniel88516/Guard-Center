using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GuardCenter
{
    internal interface IWindowsObjectNamespace
    {
        string[] EnumerateDeviceObjects();
    }

    internal sealed class WindowsObjectNamespace : IWindowsObjectNamespace
    {
        private const uint DirectoryQuery = 0x0001;
        private const uint ObjCaseInsensitive = 0x00000040;
        private const int StatusNoMoreEntries = unchecked((int)0x8000001A);
        private const int QueryBufferSize = 4096;

        public string[] EnumerateDeviceObjects()
        {
            IntPtr pathBuffer = IntPtr.Zero;
            IntPtr unicodeBuffer = IntPtr.Zero;
            IntPtr queryBuffer = IntPtr.Zero;
            IntPtr handle = IntPtr.Zero;
            try
            {
                const string path = "\\Device";
                pathBuffer = Marshal.StringToHGlobalUni(path);
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(path.Length * 2)),
                    MaximumLength = checked((ushort)((path.Length + 1) * 2)),
                    Buffer = pathBuffer
                };
                unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                Marshal.StructureToPtr(unicode, unicodeBuffer, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    ObjectName = unicodeBuffer,
                    Attributes = ObjCaseInsensitive
                };

                int status = NativeMethods.NtOpenDirectoryObject(out handle, DirectoryQuery, ref attributes);
                if (status < 0)
                    throw new InvalidOperationException("NtOpenDirectoryObject failed: 0x"
                        + status.ToString("X8") + ".");

                queryBuffer = Marshal.AllocHGlobal(QueryBufferSize);
                uint context = 0;
                bool restart = true;
                var names = new List<string>();
                while (true)
                {
                    uint returned;
                    status = NativeMethods.NtQueryDirectoryObject(handle, queryBuffer, QueryBufferSize,
                        true, restart, ref context, out returned);
                    restart = false;
                    if (status == StatusNoMoreEntries)
                        break;
                    if (status < 0)
                        throw new InvalidOperationException("NtQueryDirectoryObject failed: 0x"
                            + status.ToString("X8") + ".");

                    ObjectDirectoryInformation entry =
                        Marshal.PtrToStructure<ObjectDirectoryInformation>(queryBuffer);
                    string name = ReadUnicode(entry.Name);
                    string type = ReadUnicode(entry.TypeName);
                    if (!string.IsNullOrWhiteSpace(name)
                        && string.Equals(type, "Device", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                return names.ToArray();
            }
            finally
            {
                if (handle != IntPtr.Zero) NativeMethods.NtClose(handle);
                if (queryBuffer != IntPtr.Zero) Marshal.FreeHGlobal(queryBuffer);
                if (unicodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeBuffer);
                if (pathBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pathBuffer);
            }
        }

        private static string ReadUnicode(UnicodeString value)
        {
            return value.Buffer == IntPtr.Zero || value.Length == 0
                ? string.Empty
                : Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? string.Empty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectDirectoryInformation
        {
            public UnicodeString Name;
            public UnicodeString TypeName;
        }

        private static class NativeMethods
        {
            [DllImport("ntdll.dll")]
            public static extern int NtOpenDirectoryObject(out IntPtr handle, uint desiredAccess,
                ref ObjectAttributes objectAttributes);

            [DllImport("ntdll.dll")]
            public static extern int NtQueryDirectoryObject(IntPtr handle, IntPtr buffer, uint length,
                [MarshalAs(UnmanagedType.Bool)] bool returnSingleEntry,
                [MarshalAs(UnmanagedType.Bool)] bool restartScan, ref uint context, out uint returnLength);

            [DllImport("ntdll.dll")]
            public static extern int NtClose(IntPtr handle);
        }
    }
}
