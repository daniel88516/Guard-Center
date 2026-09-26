using System;
using System.Threading;
using System.Windows;

namespace GuardCenter
{
    public partial class App : Application
    {
        private const string ActivationEventName = "GuardCenter.ActivateMainWindow";

        private Mutex singleInstanceMutex;
        private EventWaitHandle activationEvent;
        private RegisteredWaitHandle activationWaitHandle;
        private GuardCenterController controller;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SliderMouseWheelBehavior.Initialize();

            if (UacGuardModule.TryHandleCommandLine(e.Args))
            {
                Shutdown(Environment.ExitCode);
                return;
            }

            if (VsrGuardModule.TryHandleCommandLine(e.Args))
            {
                Shutdown(Environment.ExitCode);
                return;
            }

            if (GameHelperElevationHost.TryHandleCommandLine(e.Args))
            {
                Shutdown();
                return;
            }

            if (DeviceGuardElevation.TryHandleCommandLine(e.Args))
            {
                Shutdown();
                return;
            }

            if (HuaJuanCompatibilityElevation.TryHandleCommandLine(e.Args))
            {
                Shutdown(Environment.ExitCode);
                return;
            }

            AppActionRequest appActionRequest;
            string appActionError;
            bool hasAppAction = AppActionCommandLine.TryParse(e.Args, out appActionRequest, out appActionError);
            if (hasAppAction && appActionRequest == null)
            {
                MessageBox.Show(appActionError, "App Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);

            bool createdNew;
            singleInstanceMutex = new Mutex(true, "GuardCenter.SingleInstance", out createdNew);
            if (!createdNew)
            {
                if (hasAppAction && appActionRequest != null)
                {
                    string sendError;
                    if (!AppActionIpcClient.TrySend(appActionRequest, 1800, out sendError))
                    {
                        MessageBox.Show(sendError, "App Guard", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                activationEvent.Set();
                Shutdown();
                return;
            }

            bool startMinimized = HasArg(e.Args, "--minimized") || HasArg(e.Args, "/minimized");
            controller = new GuardCenterController(startMinimized, appActionRequest);
            activationWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                activationEvent,
                OnActivationRequested,
                null,
                -1,
                false);
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            if (activationWaitHandle != null)
            {
                activationWaitHandle.Unregister(null);
                activationWaitHandle = null;
            }

            if (controller != null)
            {
                controller.Dispose();
                controller = null;
            }

            if (activationEvent != null)
            {
                activationEvent.Dispose();
                activationEvent = null;
            }

            if (singleInstanceMutex != null)
            {
                singleInstanceMutex.Dispose();
                singleInstanceMutex = null;
            }
        }

        private void OnActivationRequested(object state, bool timedOut)
        {
            if (timedOut)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (controller != null)
                {
                    controller.ShowMainWindow();
                }
            }));
        }

        private static bool HasArg(string[] args, string value)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
