using Factory.Core;
using Factory.Harness.Policy;

namespace Factory.S2.Verify;

/// The contract gate of a split project, run in integration verify after both repos' own checks. The commands come from
/// `contract:` in policy.yaml and S2 knows no tool: exit 0 passes. The breaking-change check runs in the Api repo's
/// integration checkout, the client compile (generate the client from the pinned contract, then compile) in the Ui repo's.
/// Both always run, so one failure does not hide the other; a failure is an outcome like any other, so the run ends
/// `integration_failed` with the command's output.
public static class ContractGate
{
    public const string BreakingChange = "contract: breaking change";
    public const string ClientCompile = "contract: client compile";

    public static async Task<IReadOnlyList<VerifyOutcome>> RunAsync(VerifyRunner verify, ContractConvention contract, string runId,
        string apiDir, string uiDir, TimeSpan timeout, CancellationToken ct)
    {
        var outcomes = new List<VerifyOutcome>();
        if (contract.BreakingChangeCommand is { } breaking)
            outcomes.Add(await verify.RunCommandAsync(runId, BreakingChange, breaking, apiDir, timeout, ct));
        if (contract.ClientCompileCommand is { } compile)
            outcomes.Add(await verify.RunCommandAsync(runId, ClientCompile, compile, uiDir, timeout, ct));
        return outcomes;
    }
}
