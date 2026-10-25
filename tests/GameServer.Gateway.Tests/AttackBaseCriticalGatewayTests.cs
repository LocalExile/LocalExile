using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[CollectionDefinition("Attack base critical Gateway", DisableParallelization = true)]
public sealed class AttackBaseCriticalGatewayCollection;

[Collection("Attack base critical Gateway")]
public sealed class AttackBaseCriticalGatewayTests : IDisposable
{
    readonly string _cwd = Directory.GetCurrentDirectory();
    readonly string _name = "CritBase" + Guid.NewGuid().ToString("N");
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    public AttackBaseCriticalGatewayTests()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables")))
            {
                Directory.SetCurrentDirectory(dir.FullName);
                break;
            }
        TestSaves.Ensure();
    }

    ClientState Character(bool armed)
    {
        var conn = new ClientState(new(0, "CritTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = 1 };
        if (armed)
            PlayerInventories.For(_name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Row(384)!));
        return conn;
    }

    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
    static CharacterStats.Pools Pools(ClientState conn) =>
        (CharacterStats.Pools)Method("PoolsFor").Invoke(null, [conn, 20])!;

    [Theory]
    [InlineData(true, 800)]
    [InlineData(false, 1000)]
    public void Actual_pools_apply_grants_and_raw_increases_then_clear_them(bool armed, int chance)
    {
        var conn = Character(armed);
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, chance);
        Set(conn, "critical_strike_chance_+%", 100);
        conn.Pools = Pools(conn);
        Assert.Equal(chance * 2, conn.Pools.AttackCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(chance * 2, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, 0);
        Assert.Equal(armed ? 1660 : 0, Pools(conn).AttackCriticalStrikeChance);
    }

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    [InlineData("cannot_critical_strike_with_attacks")]
    public void Actual_pools_enforce_cannot_crit_even_with_a_positive_weapon_override(string prohibition)
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, prohibition, 1);
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, prohibition, 0);
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
    }

    [Fact]
    public void A_debug_base_override_replaces_a_passive_value_instead_of_adding_to_it()
    {
        var conn = Character(true);
        conn.Passives.Add(17315); // Overwhelm: supplies 800 through the real passive stat catalog.
        Assert.Equal(800, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 0);
        Assert.Equal(830, Pools(conn).AttackCriticalStrikeChance);
        conn.PlayerStats.Clear();
        Assert.Equal(800, Pools(conn).AttackCriticalStrikeChance);
    }

    [Fact]
    public void A_weapon_in_the_inactive_set_does_not_make_the_active_empty_hand_armed()
    {
        var conn = Character(false);
        var items = PlayerInventories.For(_name, Program.Items);
        items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Row(384)!));
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
        Assert.Equal(1000, Pools(conn).AttackCriticalStrikeChance);
        items.SwapWeaponSets();
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Directory.SetCurrentDirectory(_cwd);
    }
}
