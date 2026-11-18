using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Flow.Desktop;

public partial class App : Application
{
    private const string InstanceName = @"Local\csharp-flow-viewer-8F70FA62";
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, InstanceName, out var created);
        if (!created)
        {
            instance.Dispose();
            instance = null;
            // The first process may still be creating its activation event.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (EventWaitHandle.TryOpenExisting(InstanceName + "-activate", out var existing))
                {
                    using (existing) existing.Set();
                    break;
                }
                Thread.Sleep(50);
            }
            Shutdown();
            return;
        }

        activation = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-activate");
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var window = Windows.OfType<Window>().FirstOrDefault(item => item.IsActive)
                    ?? Windows.OfType<Window>().LastOrDefault(item => item.IsVisible);
                if (window is null) return;
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
            });
        }, null, Timeout.Infinite, false);

        // These are inherited by the separate Roslyn design-time build host.
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", "true");
        Environment.SetEnvironmentVariable("MSBUILDDISABLENUGETSDKRESOLVER", "true");
        Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver", "false");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null);
        activation?.Dispose();
        if (instance is not null)
        {
            instance.ReleaseMutex();
            instance.Dispose();
        }
        base.OnExit(e);
    }
}
