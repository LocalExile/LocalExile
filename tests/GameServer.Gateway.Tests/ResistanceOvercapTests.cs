using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The overcapped-resistance modifiers (ResistanceOvercap): while one of the flag stats is set, the uncapped
/// resistance above its maximum is a generic increased modifier — +1% evasion per 1% of cold overcap, +1% critical
/// strike chance per 1% of lightning overcap (PoB 16de4b8: CalcDefence.lua's EvasionRatingIncreasedByOvercappedColdRes,
/// CalcOffence.lua 745-748's CritChanceIncreasedByOvercappedLightningRes). The pools derive the increases once; the
/// evade calculations, the panel and the spell consumers read the same stored values.
/// </summary>
public class ResistanceOvercapTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static StatCatalog Catalog() => new(Data());

    static Dictionary<uint, int> Sources(StatCatalog stats, params (string Name, int Value)[] entries)
    {
        var dict = new Dictionary<uint, int>();
        foreach (var (name, value) in entries) dict[stats.Key(name)!.Value] = value;
        return dict;
    }

    // 120% cold and lightning resistance, no maximum raises, no penalty: 45% overcap each.
    static CharacterStats.Bonus OvercappedBonus = new(ColdResistance: 120, LightningResistance: 120);

    [Fact]
    public void The_flags_derive_their_increases_from_the_overcap_only()
    {
        var both = ResistanceOvercap.Increases(120, 75, true, 120, 75, true);
        Assert.Equal(45, both.EvasionPct);
        Assert.Equal(45, both.CriticalStrikeChancePct);
        // Each flag drives its own value only; below or at the maximum nothing is overcapped.
        Assert.Equal(new ResistanceOvercap.Derived(45, 0), ResistanceOvercap.Increases(120, 75, true, 75, 75, true));
        Assert.Equal(new ResistanceOvercap.Derived(0, 45), ResistanceOvercap.Increases(75, 75, true, 120, 75, true));
        Assert.Equal(ResistanceOvercap.Derived.None, ResistanceOvercap.Increases(120, 75, false, 120, 75, false));
    }

}

/// <summary>
/// The overcapped-resistance modifiers through the real Gateway paths: the dev inputs (/set_stat) on a fresh
/// connection's <see cref="ClientState.PlayerStats"/> rebuild <see cref="Program.PoolsFor"/>'s pools at once, and
/// <see cref="Program.CharacterPlayerStats"/> hands the derived critical increase to the spell consumers. A bare
/// connection is an unarmed Witch without armour: the numbers below are its exact pools.
/// </summary>
public class OvercapPoolIntegrationTests
{

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static ClientState Conn(StatCatalog stats, params (string Name, int Value)[] devStats)
    {
        TestSaves.Ensure();
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        lock (conn.Monsters)
        {
            foreach (var (name, value) in devStats) conn.PlayerStats[stats.Key(name)!.Value] = value;
            conn.Pools = Program.PoolsFor(conn, 1);
        }
        return conn;
    }

    static readonly (string, int)[] BothFlagsAndResistances =
    [
        (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1),
        ("base_cold_damage_resistance_%", 120), ("base_lightning_damage_resistance_%", 120),
    ];

}
