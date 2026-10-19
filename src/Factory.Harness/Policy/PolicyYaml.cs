using Factory.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Factory.Harness.Policy;

/// Reads one of the target repo's policy files. A file that is not valid YAML is a rejected policy that names the file
/// and the position, like any other invalid policy value, not an unhandled parser exception.
internal static class PolicyYaml
{
    /// null when the file is absent or empty.
    public static T? Read<T>(IDeserializer yaml, string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return yaml.Deserialize<T?>(File.ReadAllText(path)); }
        catch (YamlException e) { throw new FactoryRejectedException($"{path} is invalid", [e.Message]); }
    }
}
