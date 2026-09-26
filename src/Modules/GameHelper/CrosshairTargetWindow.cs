using System;
using System.Runtime.InteropServices;

namespace GuardCenter
{
    internal static class CrosshairTargetWindow
    {
        internal static IntPtr Resolve(IntPtr foregroundHwnd)
        {
            uint foregroundThreadId;
            uint processId;
            if (!GameInputLanguageNative.TryGetWindowProcess(foregroundHwnd,
                out foregroundThreadId, out processId))
            {
                return IntPtr.Zero;
            }

            IntPtr bestHwnd = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint threadId;
                uint candidateProcessId;
                if (!GameInputLanguageNative.TryGetWindowProcess(hwnd,
                        out threadId, out candidateProcessId)
                    || candidateProcessId != processId
                    || !IsWindowVisible(hwnd)
                    || IsIconic(hwnd))
                {
                    return true;
                }

                NativeRect bounds;
                if (!TryGetClientBounds(hwnd, out bounds))
                {
                    return true;
                }

                long area = (long)(bounds.Right - bounds.Left)
                    * (bounds.Bottom - bounds.Top);
                if (area > bestArea
                    || (area == bestArea && hwnd == foregroundHwnd))
                {
                    bestArea = area;
                    bestHwnd = hwnd;
                }
                return true;
            }, IntPtr.Zero);

            if (bestHwnd != IntPtr.Zero)
            {
                return bestHwnd;
            }

            NativeRect foregroundBounds;
            return TryGetClientBounds(foregroundHwnd, out foregroundBounds)
                ? foregroundHwnd
                : IntPtr.Zero;
        }

        internal static bool TryGetClientBounds(IntPtr hwnd, out NativeRect bounds)
        {
            bounds = default(NativeRect);
            NativeRect clientRect;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out clientRect))
            {
                return false;
            }

            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var topLeft = new NativePoint
            {
                X = clientRect.Left,
                Y = clientRect.Top
            };
            var bottomRight = new NativePoint
            {
                X = clientRect.Right,
                Y = clientRect.Bottom
            };
            if (!ClientToScreen(hwnd, ref topLeft)
                || !ClientToScreen(hwnd, ref bottomRight)
                || bottomRight.X <= topLeft.X
                || bottomRight.Y <= topLeft.Y)
            {
                return false;
            }

            bounds = new NativeRect
            {
                Left = topLeft.X,
                Top = topLeft.Y,
                Right = bottomRight.X,
                Bottom = bottomRight.Y
            };
            return true;
        }

        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public double CenterX
            {
                get { return Left + ((Right - Left) / 2.0); }
            }

            public double CenterY
            {
                get { return Top + ((Bottom - Top) / 2.0); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    }
}
