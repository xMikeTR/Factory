using System.Diagnostics;
using Factory.Core;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Factory.Harness.Telemetry;

/// The one ActivitySource every Factory span comes from, and the attribute contract on it.
///
/// Exporter-agnostic: spans leave the process only through OTLP, configured by the standard
/// OTEL_EXPORTER_OTLP_* variables. Langfuse is one OTLP endpoint among others; the `langfuse.*`
/// attributes are its documented OTel mapping (trace tags, session) and are harmless elsewhere.
/// Langfuse does not ingest scores over OTel, so `task_passed` travels as an attribute and a
/// `score` span event; posting it to the Langfuse scores API is the consumer's job.
public static class FactoryTelemetry
{
    public const string SourceName = "Factory.S2";
    public const string Tag = "designc";
    public static readonly ActivitySource Source = new(SourceName);

    public static class Attr
    {
        public const string System = "factory.system";
        public const string RunId = "factory.run_id";
        public const string Team = "factory.team";
        public const string Task = "factory.task";
        public const string Element = "factory.element";     // string[]
        public const string Decision = "factory.decision";   // string[]
        public const string Attempt = "factory.attempt";     // verify attempts so far
        public const string TaskPassed = "factory.score.task_passed"; // 1 | 0
        public const string LangfuseTags = "langfuse.trace.tags";
        public const string LangfuseSession = "langfuse.session.id";
    }

    /// The root span of one run: planning, dispatch, every task and the merge are under it, so a
    /// run is one Langfuse trace and every ledger row of the run carries its trace_id.
    public static Activity? StartRun(string runId) =>
        Source.StartActivity($"run {runId}", ActivityKind.Internal, default(ActivityContext), new ActivityTagsCollection
        {
            [Attr.System] = "S2",
            [Attr.RunId] = runId,
            [Attr.LangfuseTags] = new[] { Tag },
            [Attr.LangfuseSession] = runId,
        });

    /// The span around one task, from its current state to Passed, Blocked or Failed.
    /// Null when nothing listens (no exporter, no test collector).
    public static Activity? StartTask(string runId, string teamId, PlannedTask task) =>
        Source.StartActivity($"task {task.Id.Value}", ActivityKind.Internal, default(ActivityContext), new ActivityTagsCollection
        {
            [Attr.System] = "S2",
            [Attr.RunId] = runId,
            [Attr.Team] = teamId,
            [Attr.Task] = task.Id.Value,
            [Attr.Element] = task.Elements.Select(e => e.Value).ToArray(),
            [Attr.Decision] = task.Covers.Select(d => d.Value).ToArray(),
            [Attr.Attempt] = 0,
            [Attr.LangfuseTags] = new[] { Tag },
            [Attr.LangfuseSession] = runId,
        });

    public static void RecordAttempt(Activity? task, int attempt) => task?.SetTag(Attr.Attempt, attempt);

    /// The `task_passed` score: 1 for Passed, 0 for Failed. A Blocked task has no score yet.
    public static void RecordTaskPassed(Activity? task, bool passed)
    {
        if (task is null) return;
        var value = passed ? 1 : 0;
        task.SetTag(Attr.TaskPassed, value);
        task.AddEvent(new ActivityEvent("score", tags: new ActivityTagsCollection { ["name"] = "task_passed", ["value"] = value }));
        task.SetStatus(passed ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    /// FACTORY_OTEL_CAPTURE_CONTENT=1 puts prompts and replies on model spans. Off by default:
    /// briefs carry repository content.
    public static bool CaptureContent(IReadOnlyDictionary<string, string> env) =>
        env.TryGetValue("FACTORY_OTEL_CAPTURE_CONTENT", out var v) && v == "1";

    /// Langfuse's own variables (LANGFUSE_HOST, LANGFUSE_PUBLIC_KEY, LANGFUSE_SECRET_KEY) are enough to
    /// export: they give the OTLP endpoint, protocol and Basic auth header, so nobody builds the base64
    /// header by hand. An explicit OTEL_EXPORTER_OTLP_ENDPOINT wins; with any of the three missing nothing changes.
    public static IReadOnlyDictionary<string, string> WithLangfuseDefaults(IReadOnlyDictionary<string, string> env)
    {
        if (env.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var set) && !string.IsNullOrWhiteSpace(set)) return env;
        if (!env.TryGetValue("LANGFUSE_HOST", out var host) || string.IsNullOrWhiteSpace(host)
            || !env.TryGetValue("LANGFUSE_PUBLIC_KEY", out var pk) || string.IsNullOrWhiteSpace(pk)
            || !env.TryGetValue("LANGFUSE_SECRET_KEY", out var sk) || string.IsNullOrWhiteSpace(sk)) return env;
        var derived = new Dictionary<string, string>(env)
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = host.Trim().TrimEnd('/') + "/api/public/otel",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{pk.Trim()}:{sk.Trim()}")),
        };
        return derived;
    }

    /// An OTLP exporter for Factory spans when OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise null
    /// and no span is ever recorded. Read from `env` with the spec's meaning:
    /// OTEL_EXPORTER_OTLP_ENDPOINT (base URL; /v1/traces is appended for http/protobuf),
    /// OTEL_EXPORTER_OTLP_HEADERS (k=v,k=v) and OTEL_EXPORTER_OTLP_PROTOCOL (grpc | http/protobuf).
    public static TracerProvider? CreateTracerProvider(IReadOnlyDictionary<string, string> envIn)
    {
        var env = WithLangfuseDefaults(envIn);
        if (!env.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var endpoint) || string.IsNullOrWhiteSpace(endpoint)) return null;
        var http = env.TryGetValue("OTEL_EXPORTER_OTLP_PROTOCOL", out var protocol) && protocol == "http/protobuf";
        return Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("factory-s2"))
            .AddSource(SourceName)
            .AddOtlpExporter(o =>
            {
                o.Protocol = http ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                o.Endpoint = new Uri(http ? endpoint.TrimEnd('/') + "/v1/traces" : endpoint);
                if (env.TryGetValue("OTEL_EXPORTER_OTLP_HEADERS", out var headers)) o.Headers = headers;
            })
            .Build();
    }
}
