using System;
using System.IO;
using System.Reflection;

namespace GuardCenter.UacGuardHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string privateCore = Path.Combine(AppContext.BaseDirectory,
                    "GuardCenter.UacGuardHost.core.dll");
                string guardCenterAssembly = File.Exists(privateCore)
                    ? privateCore
                    : Path.Combine(AppContext.BaseDirectory, "Guard Center.dll");
                if (!File.Exists(guardCenterAssembly))
                {
                    return 2;
                }

                Assembly assembly = Assembly.LoadFrom(guardCenterAssembly);
                Type module = assembly.GetType("GuardCenter.UacGuardModule", true);
                MethodInfo handler = module.GetMethod("TryHandleCommandLine",
                    BindingFlags.Public | BindingFlags.Static);
                if (handler == null)
                {
                    return 3;
                }

                object handledValue = handler.Invoke(null, new object[] { args });
                if (!(handledValue is bool) || !(bool)handledValue)
                {
                    return 4;
                }
                return Environment.ExitCode;
            }
            catch
            {
                return 1;
            }
        }
    }
}
