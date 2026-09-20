using System;
using System.Collections.Generic;

namespace GuardCenter
{
    internal sealed class GameInputLanguageOriginalCache
    {
        private readonly Dictionary<string, IntPtr> layouts =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        public void Remember(uint threadId, string executablePath, IntPtr layout)
        {
            if (threadId == 0 || layout == IntPtr.Zero)
            {
                return;
            }

            layouts[CreateKey(threadId, executablePath)] = layout;
        }

        public bool TryGet(uint threadId, string executablePath, out IntPtr layout)
        {
            return layouts.TryGetValue(CreateKey(threadId, executablePath), out layout)
                && layout != IntPtr.Zero;
        }

        public void Forget(uint threadId, string executablePath, IntPtr restoredLayout)
        {
            string key = CreateKey(threadId, executablePath);
            IntPtr remembered;
            if (layouts.TryGetValue(key, out remembered)
                && GameInputLanguageNative.LayoutsEqual(remembered, restoredLayout))
            {
                layouts.Remove(key);
            }
        }

        public void Clear()
        {
            layouts.Clear();
        }

        internal int Count
        {
            get { return layouts.Count; }
        }

        private static string CreateKey(uint threadId, string executablePath)
        {
            return threadId + "|" + (executablePath ?? string.Empty);
        }
    }
}
