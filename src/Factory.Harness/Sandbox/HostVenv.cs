using System.Text.RegularExpressions;

namespace Factory.Harness.Sandbox;

/// A registry's `.venv` was built on the host: in the runner image its python is the image's own, without the
/// packages the venv holds. The image's `python3` is a venv with designc and PyYAML (docker/runner/Dockerfile),
/// so a command that starts with the registry venv's python runs on that instead. Nothing else is rewritten.
public static partial class HostVenv
{
    public static string InContainer(string command) => LeadingVenvPython().Replace(command, "python3", 1);

    [GeneratedRegex(@"^\s*(\./)?\.venv/bin/python(3(\.\d+)?)?(?=\s|$)")]
    private static partial Regex LeadingVenvPython();
}
