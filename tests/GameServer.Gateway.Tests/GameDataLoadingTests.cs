using System.Text.RegularExpressions;

namespace GameServer.Gateway.Tests;

/// <summary>Game data is built at startup, in order (WorldLoader and each module's LoadData), never on first use: a
/// Lazy or LazyInitializer in the servers or the modules is one of the uses below, each for a reason that is not game data.</summary>
public sealed class GameDataLoadingTests
{
    // (file under the repository, a name on the line): what is created on first use there, and why it may be.
    static readonly (string File, string Name)[] Allowed =
    [
        // A connection's own state, made the first time that connection needs it: hot reload adds such fields to
        // connections that are already open, which never ran a constructor that would set them.
        ("src/GameServer.Gateway/Player/Player.cs", "automaticNpcTalkRows"),
        // An instance's or a lone player's frame driver, made when the area first needs it.
        ("src/GameServer.Gateway/Connection/InstanceWorld.cs", "ref frames"),
        ("src/GameServer.Gateway/Player/Player.PlayerUpdates.cs", "ref ownFrames"),
        ("src/GameServer.Gateway/Monster/GolemState.cs", "golems"),
        ("src/GameServer.Gateway/Monster/SummonCatalog.cs", "summons"),
        ("src/GameServer.Gateway/Monster/SummonCatalog.cs", "monsterPlaced"),
        ("src/GameServer.Gateway/Skill/PlacedSkills.cs", "placedSkills"),
        ("src/GameServer.Gateway/Skill/StormCallMarkers.cs", "stormCallMarkers"),
        // Not game data: the packet log's writer, the compiler references of mod scripts, a folder probe, the
        // configuration files.
        ("src/GameServer.Gateway/Connection/Program.Connection.cs", "PacketSummaries"),
        ("src/GameServer.Gateway/Modding/ModScripts.cs", "Platform"),
        ("src/GameServer.Gateway/World/AreaLayouts.cs", "folderWritable"),
        ("src/GameServer.Core/Accounts/LeagueCatalog.cs", "_startup"),
        ("src/GameServer.Core/Config/ServerConfig.cs", "File"),
        // One shop opening's pool, which most openings do not need.
        ("src/GameServer.Gateway/NPC/VendorStock.cs", "pool"),
        // Built for every league rule set by the Vendors module's LoadData and Game.LoadHardModeData; the Lazy keeps one
        // per key under races.
        ("src/GameServer.Gateway/NPC/GemVendorStock.cs", "Lazy<GemVendorStock>"),
    ];

    [Fact]
    public void Game_data_is_built_at_startup_not_on_first_use()
    {
        var lazy = new Regex(@"\bLazy<|\bLazyInitializer\.", RegexOptions.Compiled);
        var found = new List<string>();
        foreach (string folder in new[] { "src/GameServer.Gateway", "src/GameServer.Core", "Modules" })
            foreach (string path in Directory.EnumerateFiles(Path.Combine(TestEnvironment.Repository, folder), "*.cs", SearchOption.AllDirectories))
            {
                string file = Path.GetRelativePath(TestEnvironment.Repository, path).Replace('\\', '/');
                if (file.Split('/').Any(part => part is "bin" or "obj")) continue;
                foreach (var (line, number) in File.ReadLines(path).Select((l, i) => (l, i + 1)))
                    if (lazy.IsMatch(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && !Allowed.Any(a => a.File == file && line.Contains(a.Name, StringComparison.Ordinal)))
                        found.Add($"{file}:{number}: {line.Trim()}");
            }
        Assert.True(found.Count == 0, "Build this game data in its catalog's constructor or its type's loader (WorldLoader, a "
            + "module's LoadData) instead of on first use:\n" + string.Join('\n', found));
    }
}
