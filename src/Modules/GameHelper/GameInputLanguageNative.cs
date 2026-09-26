using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GuardCenter
{
    internal static class GameInputLanguageNative
    {
        internal const int ErrorAccessDenied = 5;
        internal const int WmInputLangChangeRequest = 0x0050;
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint EventSystemMoveSizeEnd = 0x000B;
        internal const uint EventSystemMinimizeStart = 0x0016;
        internal const uint EventSystemMinimizeEnd = 0x0017;
        internal const uint EventSystemDesktopSwitch = 0x0020;
        internal const uint EventSystemForeground = 0x0003;
        internal const uint EventObjectCreate = 0x8000;
        internal const uint EventObjectDestroy = 0x8001;
        internal const uint EventObjectShow = 0x8002;
        internal const uint EventObjectHide = 0x8003;
        internal const uint EventObjectFocus = 0x8005;
        internal const uint EventObjectLocationChange = 0x800B;
        internal const uint EventObjectCloaked = 0x8017;
        internal const uint EventObjectUncloaked = 0x8018;
        internal const int ObjIdWindow = 0;
        internal const int ChildIdSelf = 0;
        internal const uint WineventOutOfContext = 0x0000;
        internal static readonly IntPtr MicrosoftEnglishUsLayout = new IntPtr(0x04090409);

        internal static IntPtr GetThreadLayout(uint threadId)
        {
            return GetKeyboardLayout(threadId);
        }

        internal static bool LayoutsEqual(IntPtr left, IntPtr right)
        {
            return unchecked((uint)left.ToInt64()) == unchecked((uint)right.ToInt64());
        }

        internal static bool TryPostLayout(uint threadId, IntPtr fallbackHwnd, IntPtr layout,
            int preferredCandidateIndex, out int usedCandidateIndex, out int error)
        {
            List<IntPtr> candidates = GetTargetWindows(threadId, fallbackHwnd);
            usedCandidateIndex = -1;
            error = 0;
            if (candidates.Count == 0)
            {
                error = 1400; // ERROR_INVALID_WINDOW_HANDLE
                return false;
            }

            int index = preferredCandidateIndex;
            if (index < 0 || index >= candidates.Count)
            {
                index = 0;
            }

            usedCandidateIndex = index;
            Marshal.GetLastWin32Error();
            if (PostMessage(candidates[index], WmInputLangChangeRequest, IntPtr.Zero, layout))
            {
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }

        internal static List<IntPtr> GetTargetWindows(uint threadId, IntPtr fallbackHwnd)
        {
            var result = new List<IntPtr>(3);
            var info = new GuiThreadInfo
            {
                cbSize = Marshal.SizeOf(typeof(GuiThreadInfo))
            };

            if (GetGUIThreadInfo(threadId, ref info))
            {
                AddCandidate(result, info.hwndFocus, threadId);
                AddCandidate(result, info.hwndActive, threadId);
            }

            AddCandidate(result, fallbackHwnd, threadId);
            return result;
        }

        internal static bool TryGetWindowProcess(IntPtr hwnd, out uint threadId, out uint processId)
        {
            threadId = 0;
            processId = 0;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            threadId = GetWindowThreadProcessId(hwnd, out processId);
            return threadId != 0 && processId != 0;
        }

        internal static bool TryGetProcessPath(uint processId, out string path, out int error)
        {
            path = string.Empty;
            error = 0;
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                int capacity = 1024;
                var buffer = new StringBuilder(capacity);
                if (!QueryFullProcessImageName(process, 0, buffer, ref capacity))
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }

                path = buffer.ToString();
                return !string.IsNullOrWhiteSpace(path);
            }
            finally
            {
                CloseHandle(process);
            }
        }

        internal static IntPtr GetCurrentForegroundWindow()
        {
            return GetForegroundWindow();
        }

        internal static IntPtr InstallForegroundHook(WinEventProc callback)
        {
            return InstallEventHook(EventSystemForeground, callback);
        }

        internal static IntPtr InstallEventHook(uint eventType, WinEventProc callback)
        {
            return SetWinEventHook(eventType, eventType, IntPtr.Zero, callback,
                0, 0, WineventOutOfContext);
        }

        internal static void RemoveForegroundHook(IntPtr hook)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }

        private static void AddCandidate(List<IntPtr> candidates, IntPtr hwnd, uint expectedThreadId)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }

            uint processId;
            uint actualThreadId = GetWindowThreadProcessId(hwnd, out processId);
            if (actualThreadId != expectedThreadId || processId == 0)
            {
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] == hwnd)
                {
                    return;
                }
            }

            candidates.Add(hwnd);
        }

        internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public Rect rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
            StringBuilder executableName, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
