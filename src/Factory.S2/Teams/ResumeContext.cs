using Factory.Core;

namespace Factory.S2.Teams;

/// The brief inputs a restarted TaskLoop would otherwise lose, derived by a pure fold over the task's ledger events
/// the same way `Core.Replay` derives the state (finding 12). Each rule is the text the live loop sets at that event;
/// keep them in step with TaskLoop.
public static class ResumeContext
{
    /// `Failures`: what the next brief should say went wrong. `VerifyFailed`: the last verify did not pass.
    public static (string? Failures, bool VerifyFailed) Derive(Limits limits, IEnumerable<string> payloads)
    {
        string? failures = null;
        var verifyFailed = false;
        var state = Factory.Core.Core.Queued;
        foreach (var payload in payloads)
        {
            var ev = Factory.Core.Core.DecodeEvent(payload);
            var next = Factory.Core.Core.Transition(limits, state, ev);
            switch (ev)
            {
                case TaskEvent.VerifyCompleted vc:
                    verifyFailed = next.IsReady; // Reviewing is a verify that passed
                    if (next is TaskState.Ready { Item2.IsTestAuthor: true })
                        failures = "With the implementation stashed every test still passes, so the tests do not exercise the change: "
                            + string.Join(", ", vc.stashedImpl.Select(o => o.Name));
                    else if (next.IsReady) failures = Briefs.Failures(vc.green);
                    break;
                case TaskEvent.TestsAuthored: failures = null; break; // fresh red run: the old failures no longer apply
                case TaskEvent.ReturnedToTestAuthor rt:
                    failures = state.IsVerifying
                        ? rt.problem // surviving mutants: the tail itself
                        : "The implementer cannot make your tests pass from its write paths and handed them back:\n" + rt.problem
                            + "\n\nFix the tests inside your write paths. They must still fail against the current code.";
                    break;
                case TaskEvent.ReviewCompleted rc when next.IsReady:
                    failures = "A reviewer who did not write this code found problems that must be fixed before the task can pass:\n" + TaskLoop.FindingLines(rc.Item.Blocking);
                    break;
                case TaskEvent.ReviewGuided g when state is TaskState.Reviewing rv:
                    failures = $"A designer read the open review findings and sends the task back: {g.guidance}\n\nOpen findings:\n{TaskLoop.FindingLines(rv.Item1.OpenFindings)}";
                    break;
            }
            state = next;
        }
        return (failures, verifyFailed);
    }
}
