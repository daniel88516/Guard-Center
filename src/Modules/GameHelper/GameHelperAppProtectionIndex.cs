using System;
using System.Collections.Generic;

namespace GuardCenter
{
    internal sealed class GameHelperAppProtectionIndex
    {
        private readonly Dictionary<string, GameHelperProtectedApp> appsByExecutable =
            new Dictionary<string, GameHelperProtectedApp>(StringComparer.OrdinalIgnoreCase);

        public GameHelperAppProtectionIndex(IEnumerable<GameHelperProtectedApp> apps)
        {
            if (apps == null)
            {
                return;
            }

            foreach (GameHelperProtectedApp app in apps)
            {
                string key = CreateKey(app == null ? string.Empty : app.TargetPath);
                if (!string.IsNullOrWhiteSpace(key) && !appsByExecutable.ContainsKey(key))
                {
                    appsByExecutable.Add(key, app);
                }
            }
        }

        public bool TryGet(string executablePath, out GameHelperProtectedApp app)
        {
            string key = CreateKey(executablePath);
            if (string.IsNullOrWhiteSpace(key))
            {
                app = null;
                return false;
            }

            return appsByExecutable.TryGetValue(key, out app);
        }

        internal static string CreateKey(string executablePath)
        {
            return AppIdentityService.CreateExecutableId(executablePath);
        }
    }
}
