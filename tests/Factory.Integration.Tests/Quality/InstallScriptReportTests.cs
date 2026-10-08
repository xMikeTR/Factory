using Factory.S2.Quality;
using Xunit;

namespace Factory.Integration.Tests.Quality;

public class InstallScriptReportTests
{
    [Fact]
    public void Lists_each_skipped_script_once_across_restore_rows()
    {
        var rows = new[]
        {
            """{"skipped_install_scripts":["web: sharp","web: fsevents"]}""",
            """{"skipped_install_scripts":["web: sharp"]}""",
            """{"projects":[]}""",
        };
        var section = InstallScriptReport.Section(rows)!;
        Assert.Contains("### Install scripts", section);
        Assert.Equal(1, section.Split("web: sharp").Length - 1);
        Assert.Contains("web: fsevents", section);
        Assert.Contains("npm.rebuild", section);
    }

    [Fact]
    public void Nothing_to_report_is_no_section()
    {
        Assert.Null(InstallScriptReport.Section(["""{"skipped_install_scripts":[]}""", """{"x":1}"""]));
    }
}
