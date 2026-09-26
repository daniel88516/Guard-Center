using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class Program
{
    private const string PayloadName = "GuardCenter.Payload.zip";
    private const string DataRootVariable = "GUARD_CENTER_PORTABLE_DATA_ROOT";
    private const string LauncherVariable = "GUARD_CENTER_PORTABLE_LAUNCHER";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string application = ExtractApplication();
            if (args.Length == 1 && args[0] == "--verify-package")
            {
                return File.Exists(application) ? 0 : 2;
            }

            string executableDirectory = Path.GetDirectoryName(application)!;
            var start = new ProcessStartInfo(application)
            {
                WorkingDirectory = executableDirectory,
                UseShellExecute = false
            };
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            start.Environment[DataRootVariable] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Guard Center", "Portable", "State");
            start.Environment[LauncherVariable] = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the portable launcher.");
            using Process? process = Process.Start(start);
            if (process == null)
            {
                throw new InvalidOperationException("Guard Center did not start.");
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox(IntPtr.Zero, error.Message, "Guard Center startup failed", 0x10);
            return 1;
        }
    }

    private static string ExtractApplication()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Guard Center", "Portable");
        Directory.CreateDirectory(root);

        using Stream payload = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(PayloadName)
            ?? throw new InvalidOperationException("The embedded application package is missing.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(payload))[..24];
        string destination = Path.Combine(root, fingerprint);
        string application = Path.Combine(destination, "Guard Center.exe");

        using var mutex = new Mutex(false, "Local\\GuardCenterPortablePackage");
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The previous extraction stopped; the incomplete directory is checked below.
        }
        try
        {
            if (!File.Exists(application))
            {
                string staging = Path.Combine(root, fingerprint + ".staging");
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                Directory.CreateDirectory(staging);
                payload.Position = 0;
                ZipFile.ExtractToDirectory(payload, staging);
                if (!File.Exists(Path.Combine(staging, "Guard Center.exe"))
                    || !File.Exists(Path.Combine(staging, "GuardCenter.UacGuardHost.exe"))
                    || !File.Exists(Path.Combine(staging, "GuardCenter.UacGuardHost.core.dll")))
                {
                    throw new InvalidDataException("The embedded application package is incomplete.");
                }
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }
                Directory.Move(staging, destination);
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
        return application;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
