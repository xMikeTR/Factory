using Factory.Core;
using Factory.S2.Teams;
using Microsoft.FSharp.Collections;
using Xunit;

namespace Factory.Integration.Tests.Teams;

/// Finding 12: a restarted TaskLoop rebuilds the brief's failure text from the ledger instead of starting empty.
public class ResumeContextTests
{
    private static readonly Limits Lim = new(5, 5);
    private static VerifyOutcome Out(string name, int code, string tail = "") => new(name, code, 10L, tail);
    private static FSharpList<VerifyOutcome> L(params VerifyOutcome[] o) => ListModule.OfSeq(o);

    private static TaskEvent Authored()
    {
        var red = RedEvidence.Create(L(Out("t", 1)), RedKind.AssertionFailure);
        return TaskEvent.NewTestsAuthored(red.ResultValue);
    }

    private static (string? Failures, bool VerifyFailed) Derive(params TaskEvent[] events) =>
        ResumeContext.Derive(Lim, events.Select(Core.Core.EncodeEvent));

    [Fact]
    public void A_failed_verify_leaves_its_output_for_the_next_brief()
    {
        var (failures, verifyFailed) = Derive(Authored(), TaskEvent.NewTurnStarted(ModelTier.Standard), TaskEvent.TurnEnded, TaskEvent.VerifyRequested,
            TaskEvent.NewVerifyCompleted(L(Out("tests", 1, "Assert.Equal failed")), FSharpList<VerifyOutcome>.Empty));
        Assert.True(verifyFailed);
        Assert.Contains("Assert.Equal failed", failures);
    }

    [Fact]
    public void Fresh_tests_clear_the_old_failures_and_a_new_task_starts_empty()
    {
        Assert.Equal((null, false), Derive());
        var (failures, _) = Derive(Authored(), TaskEvent.NewTurnStarted(ModelTier.Standard), TaskEvent.TurnEnded, TaskEvent.VerifyRequested,
            TaskEvent.NewVerifyCompleted(L(Out("tests", 1, "boom")), FSharpList<VerifyOutcome>.Empty),
            TaskEvent.NewReturnedToTestAuthor("x"), Authored());
        Assert.Null(failures);
    }

    [Fact]
    public void A_blocking_review_leaves_its_findings()
    {
        var finding = new Finding(FindingCategory.Intent, "src/Q.cs", 4, "wrong thing");
        var green = L(Out("tests", 0));
        var (failures, verifyFailed) = Derive(Authored(), TaskEvent.NewTurnStarted(ModelTier.Standard), TaskEvent.TurnEnded, TaskEvent.VerifyRequested,
            TaskEvent.NewVerifyCompleted(green, L(Out("t", 1))),
            TaskEvent.NewReviewCompleted(new ReviewResult(ListModule.OfSeq(new[] { finding }), FSharpList<Finding>.Empty, Independence.Independent)));
        Assert.False(verifyFailed);
        Assert.Contains("src/Q.cs:4: wrong thing", failures);
    }
}
