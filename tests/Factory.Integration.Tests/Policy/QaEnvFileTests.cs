using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class QaEnvFileTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string RepoWith(string? yaml)
    {
        var repo = Path.Combine(Path.GetTempPath(), "s2-qaenv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".factory"));
        if (yaml is not null) File.WriteAllText(Path.Combine(repo, ".factory", "qa-env.yaml"), yaml);
        return repo;
    }

    [Fact]
    public void No_file_means_no_QA_environment()
    {
        Assert.Null(QaEnvFile.Load(RepoWith(null)));
    }

    [Fact]
    public void Services_are_pinned_by_digest_and_the_fidelity_gaps_are_declared()
    {
        var env = QaEnvFile.Load(RepoWith($"services:\n  - name: db\n    image: mcr.microsoft.com/mssql/server@{Digest}\nfidelity:\n  - Service Bus emulator has no topic filters\n"))!;

        Assert.Equal("db", env.Services.Single().Name);
        Assert.Equal(["Service Bus emulator has no topic filters"], env.Fidelity);
    }

    [Fact]
    public void An_image_that_floats_on_a_tag_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => QaEnvFile.Load(RepoWith("services:\n  - name: db\n    image: mssql:latest\n")));
    }

    [Fact]
    public void A_service_name_the_command_line_could_not_carry_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => QaEnvFile.Load(RepoWith($"services:\n  - name: 'db; x'\n    image: a@{Digest}\n")));
    }
}
