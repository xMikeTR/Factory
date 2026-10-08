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

    private readonly Queue<string[]> _confirmations = new();
    public List<EquivalenceRequest> EquivalenceRequests { get; } = [];

    /// The next equivalence question confirms these mutant ids (by default none are confirmed).
    public ScriptedReviewer ThenConfirmEquivalent(params string[] ids) { _confirmations.Enqueue(ids); return this; }

    public Task<IReadOnlySet<string>> ConfirmEquivalentsAsync(EquivalenceRequest request, CancellationToken ct)
    {
        EquivalenceRequests.Add(request);
        var ids = _confirmations.Count > 0 ? _confirmations.Dequeue() : [];
        return Task.FromResult<IReadOnlySet<string>>(request.Claims.Select(c => c.Mutant.Id).Where(ids.Contains).ToHashSet());
    }

    public Task<ReviewVerdict> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_verdicts.Count > 0 ? _verdicts.Dequeue() : new ReviewVerdict.Submitted([]));
    }
}
