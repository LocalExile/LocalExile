using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using GameServer.Modding;

/// <summary>
/// One mod's or module's assemblies, collectible: unloading it lets the runtime free them once nothing holds one of its objects.
/// It sees the framework, the server's own assemblies (GameServer.*, the host's copies, so <see cref="IMod"/> and every server
/// type are one type for all), the assemblies of the mods and modules it depends on, and the files in its own folder. A mod
/// can alter the game as fully as a module (Agents/docs/dev/modding.md).
/// </summary>
public sealed class ModLoadContext(string id, string folder, IReadOnlyList<ModLoadContext> dependencies)
    : AssemblyLoadContext($"mod {id}", isCollectible: true)
{
    static readonly string CopyRoot = Path.Combine(Path.GetTempPath(), "localexile-mod-copy");
    /// <summary>Only Windows refuses to delete a loaded assembly. Elsewhere this file, locked for as long as a copy is in
    /// use, is what keeps <see cref="DeleteOldCopies"/> from it; the system releases it however the process ends.</summary>
    const string InUseFile = "in-use";
    static readonly ConcurrentDictionary<string, FileStream> Holding = new(StringComparer.Ordinal);
    readonly Lock copyGate = new();
    public string CopyDirectory { get; } = Path.Combine(CopyRoot, Guid.NewGuid().ToString("N"));

    void Hold()
    {
        if (OperatingSystem.IsWindows() || Holding.ContainsKey(CopyDirectory)) return;
        Holding[CopyDirectory] = new FileStream(Path.Combine(CopyDirectory, InUseFile), FileMode.Create, FileAccess.Write, FileShare.None);
    }

    static bool InUse(string directory)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            using var probe = new FileStream(Path.Combine(directory, InUseFile), FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // A mod or module can alter the game fully: the server's assemblies are the host's own copies.
        if (name.Name?.StartsWith("GameServer.", StringComparison.Ordinal) == true) return null;
        foreach (var dependency in dependencies)
            if (dependency.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name) is { } shared) return shared;
        // Beside its manifest, or in its build's output (a module). null: the default context's (the framework).
        foreach (string path in new[] { Path.Combine(folder, name.Name + ".dll"), Path.Combine(folder, "bin", name.Name + ".dll") })
            if (File.Exists(path)) return LoadCopy(path);
        return null;
    }

    /// <summary>Loads a private disk copy (and its .pdb), leaving the build output free. Path-backed collectible
    /// assemblies avoid the LoadFromStream loader/finalizer failure observed on the supported Windows runtime.</summary>
    public Assembly LoadCopy(string path)
    {
        lock (copyGate)
        {
            Directory.CreateDirectory(CopyDirectory);
            Hold();
            string copy = Path.Combine(CopyDirectory, Path.GetFileName(path));
            if (!File.Exists(copy))
            {
                File.Copy(path, copy);
                string pdb = Path.ChangeExtension(path, ".pdb");
                if (File.Exists(pdb)) File.Copy(pdb, Path.ChangeExtension(copy, ".pdb"));
            }
            return LoadFromAssemblyPath(copy);
        }
    }

    // Called only after the context is collected and its copied modules have released their file handles.
    public static bool TryDeleteCopy(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(CopyRoot), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return false;
        if (!OperatingSystem.IsWindows() && Holding.TryRemove(full, out var held)) held.Dispose();
        try { if (Directory.Exists(full)) Directory.Delete(full, recursive: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes what earlier runs left behind (a stopped Gateway collects nothing). A copy a running Gateway has
    /// loaded is skipped: its assembly cannot be deleted on Windows, and elsewhere it is held. Recent ones may be a starting Gateway's, before it loaded them.</summary>
    public static void DeleteOldCopies()
    {
        if (!Directory.Exists(CopyRoot)) return;
        var before = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (string directory in Directory.GetDirectories(CopyRoot))
        {
            if (Directory.GetLastWriteTimeUtc(directory) > before || InUse(directory)) continue;
            try { foreach (string dll in Directory.GetFiles(directory, "*.dll")) File.Delete(dll); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            TryDeleteCopy(directory);
        }
    }

    /// <summary>Loads what a mod's files or its compiled scripts were read into, by the same private disk copy as
    /// <see cref="LoadCopy"/>: never from a stream.</summary>
    public Assembly LoadImage(byte[] image, byte[]? symbols)
    {
        lock (copyGate)
        {
            Directory.CreateDirectory(CopyDirectory);
            Hold();
            string copy = Path.Combine(CopyDirectory, Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(copy, image);
            if (symbols is not null) File.WriteAllBytes(Path.ChangeExtension(copy, ".pdb"), symbols);
            return LoadFromAssemblyPath(copy);
        }
    }
}
