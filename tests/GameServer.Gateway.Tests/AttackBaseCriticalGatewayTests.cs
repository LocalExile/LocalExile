using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AttackBaseCriticalGatewayTests : IDisposable
{
    readonly string _name = "CritBase" + Guid.NewGuid().ToString("N");
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    public AttackBaseCriticalGatewayTests() => TestSaves.Ensure();

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
    public void Actual_pools_apply_grants_and_raw_increases_then_clear_them(bool armed, int chance)
    {
        var conn = Character(armed);
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, chance);
        Set(conn, "critical_strike_chance_+%", 100);
        conn.Pools = Pools(conn);
        Assert.Equal(chance * 2, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(100, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(100, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(1000, conn.Pools.SpellCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(chance * 2, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(100, shown[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(conn.Pools.SpellCriticalStrikeChance,
            SpellCriticalStrike.Chance(500, Program.Stats, shown, new Dictionary<uint, int>()));
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, 0);
        Assert.Equal(armed ? 1660 : 0, Pools(conn).AttackCriticalStrikeChance);
    }

    public void Dispose() => PlayerInventories.Forget(_name);
}
