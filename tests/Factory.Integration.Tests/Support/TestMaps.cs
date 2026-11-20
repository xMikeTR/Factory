using Factory.Core;
using Microsoft.FSharp.Core;
using FsCore = global::Factory.Core.Core;

namespace Factory.Integration.Tests.Support;

/// A handover carrying a designc test map (`tests.json`), built without a handover directory.
public static class TestMaps
{
    public static NamedTest Named(string kind, string path, string? command, string? via = null) =>
        new(kind, path, command is null ? FSharpOption<string>.None : FSharpOption<string>.Some(command), via is null ? FSharpOption<string>.None : FSharpOption<string>.Some(via));

    public static ElementTests Element(string element, NamedTest[] failToPass, NamedTest[] passToPass, string? repo = null) =>
        new(element, repo is null ? FSharpOption<string>.None : FSharpOption<string>.Some(repo), FsCore.ToList(failToPass), FsCore.ToList(passToPass));

    public static TestMap Map(ElementTests[] elements, params (string Element, string Reason)[] unmapped) =>
        new(FsCore.ToList(elements), FsCore.ToList(unmapped.Select(u => Tuple.Create(u.Element, u.Reason))));

    public static Handover WithTests(this Handover b, TestMap? map) =>
        new(b.Id, b.Digest, b.Elements, b.Decisions, b.ProtectedPaths, b.EventVocabulary, b.Relationships, b.AcceptanceSpecs,
            b.WorkItem, b.DesignTokens, b.DesignVersion, b.AcceptanceCriteria, b.LoopSpecs, b.References, b.Contracts, b.Uncovered, b.ApiContracts,
            b.RequirementRisk, b.RequirementDataClass, b.Ui, map is null ? FSharpOption<TestMap>.None : FSharpOption<TestMap>.Some(map), b.ScenarioTests, b.ScenarioSkipped, b.Interfaces, b.ApiContractTexts);

    /// The handover with these scenario screens (and no skipped ones).
    public static Handover WithScenarios(this Handover b, IEnumerable<ScenarioScreen> screens) =>
        new(b.Id, b.Digest, b.Elements, b.Decisions, b.ProtectedPaths, b.EventVocabulary, b.Relationships, b.AcceptanceSpecs,
            b.WorkItem, b.DesignTokens, b.DesignVersion, b.AcceptanceCriteria, b.LoopSpecs, b.References, b.Contracts, b.Uncovered, b.ApiContracts,
            b.RequirementRisk, b.RequirementDataClass, b.Ui, b.Tests, Microsoft.FSharp.Collections.ListModule.OfSeq(screens),
            Microsoft.FSharp.Collections.ListModule.Empty<Tuple<string, string>>(), b.Interfaces, b.ApiContractTexts);

    /// The handover with one served operation interface on `element` over this OpenAPI text, as a frozen handover would carry them.
    public static Handover WithContract(this Handover b, string element, string path, string text) =>
        new(b.Id, b.Digest, b.Elements, b.Decisions, b.ProtectedPaths, b.EventVocabulary, b.Relationships, b.AcceptanceSpecs,
            b.WorkItem, b.DesignTokens, b.DesignVersion, b.AcceptanceCriteria, b.LoopSpecs, b.References, b.Contracts, b.Uncovered, b.ApiContracts,
            b.RequirementRisk, b.RequirementDataClass, b.Ui, b.Tests, b.ScenarioTests, b.ScenarioSkipped,
            Microsoft.FSharp.Collections.ListModule.OfSeq([new ElementInterface(element, "serves", element, "Consumer", "operation", "op", FSharpOption<string>.Some("GET"), FSharpOption<string>.Some("/x"), FSharpOption<string>.Some(path), "1")]),
            Microsoft.FSharp.Collections.ListModule.OfSeq([new UiText(path, Factory.Harness.Registry.DesigncHandoverReader.Sha256(text), text)]));
}
