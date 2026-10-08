using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using Factory.Core;

namespace Factory.S2.Quality;

/// Namespace-level dependencies read from compiled assemblies with ArchUnitNET (it reads metadata only, nothing of the
/// target's code runs). An edge is a namespace path ("Pricing/Domain") depending on another; only the assemblies given
/// count as targets, so the framework never shows up. Core's `Fitness.forbidden` judges them like any other edge.
public static class NamespaceEdges
{
    public static IReadOnlyList<CodeEdge> Read(IEnumerable<string> assemblyPaths)
    {
        var paths = assemblyPaths.Where(File.Exists).Distinct().ToList();
        if (paths.Count == 0) return [];
        // MetadataLoadContext: the assemblies are described, never loaded for execution (a module initializer cannot run).
        var runtime = Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
        using var context = new System.Reflection.MetadataLoadContext(new System.Reflection.PathAssemblyResolver(runtime.Concat(paths)));
        var architecture = paths.Aggregate(new ArchLoader(), (loader, path) => loader.LoadAssembly(context.LoadFromAssemblyPath(path))).Build();
        var ours = architecture.Assemblies.ToHashSet();

        return architecture.Types
            .Where(t => ours.Contains(t.Assembly))
            .SelectMany(t => t.Dependencies
                .Select(d => d.Target)
                .Where(target => ours.Contains(target.Assembly))
                .Select(target => new CodeEdge(Path(t.Namespace.FullName), Path(target.Namespace.FullName))))
            .Where(e => e.Source != e.Target && e.Source.Length > 0 && e.Target.Length > 0)
            .Distinct()
            .ToList();
    }

    private static string Path(string ns) => ns.Replace('.', '/');
}
