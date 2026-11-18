using Xunit;

namespace Factory.Integration.Tests.Support;

/// Tests that set or unset process-wide environment variables (FACTORY_GATEWAY_KEY, GH_TOKEN), and tests that build
/// a gateway client, share one collection so xunit never runs two of them at the same time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvCollection
{
    public const string Name = "process-env";
}
