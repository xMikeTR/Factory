using Factory.Core;
using Factory.S2.Teams;

namespace Factory.Integration.Tests.Support;

/// A reviewer that answers from a queue and approves once it is empty. Records what it was asked.
public sealed class ScriptedReviewer(Independence independence) : IReviewer
{
    public ScriptedReviewer() : this(Independence.Independent) { }

    private readonly Queue<ReviewVerdict> _verdicts = new();
    public List<ReviewRequest> Requests { get; } = [];
    public Independence Independence { get; set; } = independence;

    public ScriptedReviewer Then(params Finding[] findings) { _verdicts.Enqueue(new ReviewVerdict.Submitted(findings)); return this; }
    public ScriptedReviewer ThenMalformed(string reason = "no submit_review") { _verdicts.Enqueue(new ReviewVerdict.Malformed(reason)); return this; }

    public Task<ReviewVerdict> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_verdicts.Count > 0 ? _verdicts.Dequeue() : new ReviewVerdict.Submitted([]));
    }
}
