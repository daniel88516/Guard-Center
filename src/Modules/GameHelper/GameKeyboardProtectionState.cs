namespace GuardCenter
{
    internal readonly struct GameKeyboardDecision
    {
        public GameKeyboardDecision(bool suppress, bool showDesktop)
        {
            Suppress = suppress;
            ShowDesktop = showDesktop;
        }

        public bool Suppress { get; }
        public bool ShowDesktop { get; }

        public static GameKeyboardDecision Pass
        {
            get { return new GameKeyboardDecision(false, false); }
        }
    }

    internal sealed class GameKeyboardProtectionState
    {
        internal const int VkControl = 0x11;
        internal const int VkRightControl = 0xA3;
        internal const int VkD = 0x44;
        internal const int VkLeftWin = 0x5B;
        internal const int VkRightWin = 0x5C;
        internal const int LlkhfExtended = 0x01;

        private bool suppressLeftWin;
        private bool suppressRightWin;
        private bool rightControlDown;
        private bool dDown;
        private bool dDownSuppressed;
        private bool desktopChordFired;

        internal GameKeyboardDecision Process(int virtualKey, int flags, bool keyDown, bool keyUp,
            bool blockWindowsKey, bool rightControlDShowsDesktop)
        {
            if (!keyDown && !keyUp)
            {
                return GameKeyboardDecision.Pass;
            }

            if (virtualKey == VkLeftWin || virtualKey == VkRightWin)
            {
                return ProcessWindowsKey(virtualKey, keyDown, keyUp, blockWindowsKey);
            }

            if (IsRightControl(virtualKey, flags))
            {
                return ProcessRightControl(keyDown, keyUp, rightControlDShowsDesktop);
            }

            if (virtualKey == VkD)
            {
                return ProcessD(keyDown, keyUp, rightControlDShowsDesktop);
            }

            return GameKeyboardDecision.Pass;
        }

        internal void Reset()
        {
            suppressLeftWin = false;
            suppressRightWin = false;
            rightControlDown = false;
            dDown = false;
            dDownSuppressed = false;
            desktopChordFired = false;
        }

        internal bool IsClean
        {
            get
            {
                return !suppressLeftWin && !suppressRightWin && !rightControlDown
                    && !dDown && !dDownSuppressed && !desktopChordFired;
            }
        }

        private GameKeyboardDecision ProcessWindowsKey(int virtualKey, bool keyDown, bool keyUp,
            bool blockWindowsKey)
        {
            bool left = virtualKey == VkLeftWin;
            bool suppress = left ? suppressLeftWin : suppressRightWin;
            if (keyDown)
            {
                if (suppress || blockWindowsKey)
                {
                    if (left) suppressLeftWin = true;
                    else suppressRightWin = true;
                    return new GameKeyboardDecision(true, false);
                }
                return GameKeyboardDecision.Pass;
            }

            if (keyUp && suppress)
            {
                if (left) suppressLeftWin = false;
                else suppressRightWin = false;
                return new GameKeyboardDecision(true, false);
            }
            return GameKeyboardDecision.Pass;
        }

        private GameKeyboardDecision ProcessRightControl(bool keyDown, bool keyUp,
            bool rightControlDShowsDesktop)
        {
            if (keyDown)
            {
                rightControlDown = true;
                if (dDown && !desktopChordFired && rightControlDShowsDesktop)
                {
                    desktopChordFired = true;
                    return new GameKeyboardDecision(false, true);
                }
            }
            else if (keyUp)
            {
                rightControlDown = false;
                if (!dDown) desktopChordFired = false;
            }
            return GameKeyboardDecision.Pass;
        }

        private GameKeyboardDecision ProcessD(bool keyDown, bool keyUp,
            bool rightControlDShowsDesktop)
        {
            if (keyDown)
            {
                dDown = true;
                if (desktopChordFired)
                {
                    return new GameKeyboardDecision(dDownSuppressed, false);
                }
                if (rightControlDown && rightControlDShowsDesktop)
                {
                    desktopChordFired = true;
                    dDownSuppressed = true;
                    return new GameKeyboardDecision(true, true);
                }
                return GameKeyboardDecision.Pass;
            }

            if (keyUp)
            {
                bool suppress = dDownSuppressed;
                dDown = false;
                dDownSuppressed = false;
                if (!rightControlDown) desktopChordFired = false;
                return new GameKeyboardDecision(suppress, false);
            }
            return GameKeyboardDecision.Pass;
        }

        private static bool IsRightControl(int virtualKey, int flags)
        {
            return virtualKey == VkRightControl
                || (virtualKey == VkControl && (flags & LlkhfExtended) != 0);
        }
    }
}
