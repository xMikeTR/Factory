using Factory.Core;

namespace Factory.S2.Teams;

/// What a reviewer is asked: one task's diff against the integration branch, judged against the
/// task's brief. `Open` is what the previous round left blocking (empty on the first).
public sealed record ReviewRequest(string RunId, string TeamId, string TaskId, string Worktree, string Brief, string Diff, int Round, IReadOnlyList<Finding> Open);

/// A reviewer's answer: findings as submitted (not yet checked against the diff), or no usable review.
public abstract record ReviewVerdict
{
    public sealed record Submitted(IReadOnlyList<Finding> Findings) : ReviewVerdict;
    public sealed record Malformed(string Reason) : ReviewVerdict;
}

/// The seam between the task loop and whoever reviews. `Independence` is fixed at start-up from
/// the gateways and stamped on every review.
public interface IReviewer
{
    Independence Independence { get; }
    Task<ReviewVerdict> ReviewAsync(ReviewRequest request, CancellationToken ct);
}
