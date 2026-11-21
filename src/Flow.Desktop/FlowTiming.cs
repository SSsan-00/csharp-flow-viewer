using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Flow.Desktop;

[EventSource(Name = "CSharpFlowViewer-Performance")]
internal sealed class FlowTiming : EventSource
{
    internal static readonly FlowTiming Log = new();

    [NonEvent]
    internal void Record(string phase, long started, int version)
    {
        if (IsEnabled()) Completed(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds, version);
    }

    [Event(1, Level = EventLevel.Informational)]
    public void Completed(string phase, double elapsedMs, int version) => WriteEvent(1, phase, elapsedMs, version);
}
