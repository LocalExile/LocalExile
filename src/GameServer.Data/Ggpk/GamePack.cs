using System.IO.Compression;

namespace GameServer.Data.Ggpk;

/// <summary>
/// The game files the servers read as they run, copied out of <c>Content.ggpk</c> by scripts/re/game-pack
/// (scripts/dev/extract-data.ps1): every file of the types the level generator reads, whichever rooms and tiles a seed
/// picks, its tables, and the passive graphs the Phrecia classes verify. A path of a packed type that is not in the pack
/// does not exist in the game either (the generator asks for such files). Any other path throws: it was never copied, so
/// the pack cannot say. Thread-safe: reads are serialized by one lock (a zip archive shares its stream).
/// </summary>
public sealed class GamePack : IDisposable
{
    public const string DefaultFile = "data/game-files.zip";

    /// <summary>The file types packed whole. .ot, .ao and .tmd are probed for files that may not exist.</summary>
    static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ao", ".arm", ".ddt", ".dgr", ".ecf", ".et", ".fgp", ".gft", ".gt", ".ot", ".rs", ".tdt", ".tgr", ".tmd", ".toy", ".tsi", ".tst",
    };

    /// <summary>The single files packed beside them.</summary>
    static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data/CoreLeagues.datc64", "Data/ExtraTerrainFeatures.datc64", "Data/Hideouts.datc64", "Data/LabyrinthNodeOverrides.datc64",
        "Data/Labyrinths.datc64", "Data/LabyrinthSecrets.datc64", "Data/LabyrinthSection.datc64", "Data/LabyrinthSectionLayout.datc64",
        "Data/SanctumRooms.datc64", "Data/Topologies.datc64", "Data/WorldAreaLeagueChances.datc64", "Data/WorldAreas.datc64",
        "metadata/passiveskillgraph.psg", "metadata/alternateskillgraphs/customascendancypassiveskillgraph.psg",
    };

    /// <summary>Whether the pack holds this path when the game has it.</summary>
    public static bool Covers(string path) => Files.Contains(path) || Types.Contains(Path.GetExtension(path));

    static readonly Lazy<GamePack> Shared = new(() => Open(DefaultFile));

    /// <summary>The pack in data/, opened on first use and kept open.</summary>
    public static GamePack Default => Shared.Value;

    readonly ZipArchive _zip;
    readonly Dictionary<string, ZipArchiveEntry> _entries;
    readonly Lock _gate = new();

    GamePack(ZipArchive zip)
    {
        _zip = zip;
        _entries = new(zip.Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries) _entries[entry.FullName] = entry;
    }

    public static GamePack Open(string file) => new(ZipFile.OpenRead(file));

    /// <summary>The file's bytes, or null when the game has no such file.</summary>
    public byte[]? TryRead(string path)
    {
        if (!Covers(path)) throw new NotSupportedException($"{path} is not of a type in {DefaultFile}: add it to GamePack and bump game-pack in extract-data.ps1");
        if (!_entries.TryGetValue(path, out var entry)) return null;
        lock (_gate)
        {
            using var stream = entry.Open();
            byte[] data = new byte[entry.Length];
            stream.ReadExactly(data);
            return data;
        }
    }

    public byte[] Read(string path) => TryRead(path) ?? throw new FileNotFoundException($"file not in {DefaultFile}: {path}", path);

    public void Dispose() => _zip.Dispose();
}
