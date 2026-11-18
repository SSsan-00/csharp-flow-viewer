using System.Windows;

namespace Flow.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // These are inherited by the separate Roslyn design-time build host.
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", "true");
        Environment.SetEnvironmentVariable("MSBUILDDISABLENUGETSDKRESOLVER", "true");
        Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver", "false");
        base.OnStartup(e);
    }
}
