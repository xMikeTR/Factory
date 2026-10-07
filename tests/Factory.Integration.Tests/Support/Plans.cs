using Factory.Core;

namespace Factory.Integration.Tests.Support;

using Core = Factory.Core.Core;

public static class Plans
{
    public static PathPattern Pattern(string raw) => PathPatternModule.create(raw).ResultValue;

    public static VerifyEntry Auto(string name, string run, string? cleanup = null, int timeoutSeconds = 30) =>
        VerifyEntry.NewAutomated(new AutomatedVerify(name, cleanup is null ? AutoVerifyKind.Invariant : AutoVerifyKind.SelfSeeded,
            run, cleanup is null ? null : Microsoft.FSharp.Core.FSharpOption<string>.Some(cleanup), timeoutSeconds));

    public static VerifyEntry Manual(string name) => VerifyEntry.NewManual(new ManualVerify(name, "look at it"));

    public static PlannedTask Task(string id, params VerifyEntry[] verify) =>
        new(TaskId.NewTaskId(id), "title", "instructions", Core.ToList([ElementId.NewElementId("Pricing Platform/Quotes DB")]),
            Core.ToList([Pattern("src/Pricing.Data/**")]), Core.ToList([Pattern("tests/Pricing.Data.Tests/**")]), Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList(verify));
}
