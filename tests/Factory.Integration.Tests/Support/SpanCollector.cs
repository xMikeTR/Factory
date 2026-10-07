using System.Collections.Concurrent;
using System.Diagnostics;
using Factory.Harness.Telemetry;

namespace Factory.Integration.Tests.Support;

/// An in-memory exporter for one test: starts a root span and keeps every Factory span in its
/// trace. The listener is process-wide, so the trace id is what keeps parallel tests apart.
public sealed class SpanCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly Activity _root;

    public SpanCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == FactoryTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.TraceId == TraceId) _spans.Enqueue(a); },
        };
        ActivitySource.AddActivityListener(_listener);
        _root = new Activity("test").Start();
    }

    public ActivityTraceId TraceId => _root.TraceId;
    public IReadOnlyList<Activity> Spans => _spans.ToList();

    public void Dispose()
    {
        _root.Stop();
        _listener.Dispose();
    }
}
