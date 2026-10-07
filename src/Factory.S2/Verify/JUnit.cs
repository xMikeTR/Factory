using System.Xml.Linq;
using Factory.Core;

namespace Factory.S2.Verify;

/// JUnit XML, the one results format every stack can emit (dotnet's JunitXml logger, pytest
/// --junitxml, surefire). A test is "classname.name"; failure or error fails it; skipped tests
/// are left out, so skipping a test that passed before counts as a regression.
public static class JUnit
{
    public static List<TestResult> Parse(string xml) =>
        XDocument.Parse(xml).Descendants("testcase")
            .Where(c => c.Element("skipped") is null)
            .Select(c => new TestResult(
                c.Attribute("classname")?.Value is { Length: > 0 } cls ? $"{cls}.{c.Attribute("name")?.Value}" : c.Attribute("name")?.Value ?? "",
                c.Element("failure") is null && c.Element("error") is null))
            .ToList();
}
