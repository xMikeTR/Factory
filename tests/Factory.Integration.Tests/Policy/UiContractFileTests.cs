using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class UiContractFileTests
{
    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-uic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, UiContractFile.RelativePath), yaml);
        return repo;
    }

    [Fact]
    public void No_file_is_an_empty_contract()
    {
        Assert.Empty(UiContractFile.Load(RepoWith(null)));
    }

    [Fact]
    public void Each_list_becomes_items_of_its_kind()
    {
        var items = UiContractFile.Load(RepoWith("testIds: [export-btn, name]\nroutes: [/reports]\nuiTests: [\"exports a report\"]\n"));

        Assert.Equal(
            [new UiItem(UiItemKind.TestId, "export-btn"), new UiItem(UiItemKind.TestId, "name"), new UiItem(UiItemKind.Route, "/reports"), new UiItem(UiItemKind.UiTest, "exports a report")],
            items);
    }

    [Fact]
    public void A_blank_entry_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => UiContractFile.Load(RepoWith("testIds: [\"\"]\n")));
    }
}
