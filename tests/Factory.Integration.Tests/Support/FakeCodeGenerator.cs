using System.Security.Cryptography;
using System.Text;
using Factory.Core;
using Factory.S2;

namespace Factory.Integration.Tests.Support;

/// Stands in for NSwag: each output file is a pure function of the input text and the file name, so determinism and change are testable offline.
public sealed class FakeCodeGenerator(string? failWith = null) : ICodeGenerator
{
    public int Calls { get; private set; }

    public async Task<CodegenRun> GenerateAsync(IReadOnlyList<Codegen.Target> targets, string workDir, CancellationToken ct)
    {
        Calls++;
        var commands = targets.SelectMany(t => Codegen.commands(t)).ToList();
        if (failWith is not null) return new CodegenRun("failed", failWith, commands);
        foreach (var t in targets)
        {
            var text = await File.ReadAllTextAsync(Path.Combine(workDir, "in", t.Source), ct);
            foreach (var f in t.Files)
            {
                var path = Path.Combine(workDir, Codegen.outPath(t.Repo, f));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text + f)))[..12];
                await File.WriteAllTextAsync(path, $"// generated {f} {tag}\n", ct);
            }
        }
        return new CodegenRun("generated", "", commands);
    }
}
