using Xunit;
using Factory.S2;

namespace Factory.Integration.Tests.Cli;

public sealed class RepoMapTests
{
    [Fact]
    public void A_plain_path_is_the_single_repo_form_and_has_no_names()
    {
        var m = RepoMap.Parse(["."]);
        Assert.Empty(m.Errors);
        Assert.Equal(".", m.Primary);
        Assert.Empty(m.Named);
    }

    [Fact]
    public void Name_equals_path_pairs_form_a_map_and_the_first_is_primary()
    {
        var m = RepoMap.Parse(["Api=../api", "Ui=../ui"]);
        Assert.Empty(m.Errors);
        Assert.Equal("../api", m.Primary);
        Assert.Equal(["Api", "Ui"], m.Named.Keys);
        Assert.Equal("../ui", m.Named["Ui"]);
    }

    [Fact]
    public void A_path_that_merely_contains_an_equals_sign_is_still_a_plain_path()
    {
        var m = RepoMap.Parse(["./a=b"]);
        Assert.Empty(m.Errors);
        Assert.Equal("./a=b", m.Primary);
        Assert.Empty(m.Named);
    }

    [Fact]
    public void Mixing_forms_a_repeated_name_or_an_empty_path_is_refused()
    {
        Assert.NotEmpty(RepoMap.Parse([".", "Ui=../ui"]).Errors);
        Assert.NotEmpty(RepoMap.Parse(["Api=../a", "Api=../b"]).Errors);
        Assert.NotEmpty(RepoMap.Parse(["Api="]).Errors);
        Assert.NotEmpty(RepoMap.Parse([".", "other"]).Errors);
    }

    [Fact]
    public void No_flag_at_all_is_not_an_error_here_the_caller_decides()
    {
        var m = RepoMap.Parse([]);
        Assert.Empty(m.Errors);
        Assert.Null(m.Primary);
    }

    [Fact]
    public void Flags_are_read_from_the_command_line_even_when_repeated()
    {
        var m = RepoMap.FromArgs(["run", "--repo", "Api=../api", "--handover", "h.json", "--repo", "Ui=../ui"]);
        Assert.Equal(["Api", "Ui"], m.Named.Keys);
    }
}
