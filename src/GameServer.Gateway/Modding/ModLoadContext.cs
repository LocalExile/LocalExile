using System.Reflection;
using System.Runtime.Loader;
using GameServer.Modding;

/// <summary>
/// One mod's assemblies, collectible: unloading the mod lets the runtime free them once nothing holds one of its objects.
/// A mod sees the framework, the shared contracts (GameServer.Modding, the host's own copy, so <see cref="IMod"/> is one
/// type for every mod), the assemblies of the mods it depends on, and the files in its own folder. Not the server's other
/// assemblies: the Gateway, Core and Data are internal to it.
/// </summary>
sealed class ModLoadContext(string id, string folder, IReadOnlyList<ModLoadContext> dependencies)
    : AssemblyLoadContext($"mod {id}", isCollectible: true)
{
    static readonly string Contracts = typeof(IMod).Assembly.GetName().Name!;

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == Contracts) return null;
        if (name.Name?.StartsWith("GameServer.", StringComparison.Ordinal) == true)
            throw new FileNotFoundException($"a mod can use GameServer.Modding, not {name.Name} (the server's own code)");
        foreach (var dependency in dependencies)
            if (dependency.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name) is { } shared) return shared;
        string path = Path.Combine(folder, name.Name + ".dll");
        // null: the default context's (the framework).
        return File.Exists(path) ? LoadCopy(path) : null;
    }

    /// <summary>Loads the assembly (and its .pdb, for line numbers in errors) from a copy in memory, so the files stay free
    /// for the next build of the mod to overwrite.</summary>
    public Assembly LoadCopy(string path)
    {
        using var dll = new MemoryStream(File.ReadAllBytes(path));
        string pdb = Path.ChangeExtension(path, ".pdb");
        using var symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
        return LoadFromStream(dll, symbols);
    }
}
