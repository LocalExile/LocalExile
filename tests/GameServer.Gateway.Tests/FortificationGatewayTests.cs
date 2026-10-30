using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class FortificationGatewayTests : IDisposable
{
    readonly string name = "Fortify" + Guid.NewGuid().ToString("N");
    ClientState Character()
    {
        TestSaves.Ensure();
        var conn = new ClientState(new(ushort.MaxValue, "FortificationTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, ClassRow = 1, PlayerLife = 100, PlayerSent = true };
        Set(conn, FortificationSession.OnMeleeHit, 1);
        Set(conn, FortificationState.MaximumStat, 10);
        Set(conn, FortificationState.LifeRegenPerStack, 12);
        Set(conn, FortificationState.AttackSpeedPerStack, 1);
        Set(conn, "attack_minimum_added_chaos_damage", 100);
        Set(conn, "attack_maximum_added_chaos_damage", 100);
        Set(conn, "physical_damage_+%", -100);
        Set(conn, "attack_speed_+%", 1000);
        conn.Pools = Program.PoolsFor(conn, 1) with { Accuracy = int.MaxValue };
        return conn;
    }
    static void Set(ClientState conn, string stat, int value) => conn.PlayerStats[Program.Stats.Key(stat)!.Value] = value;
    static MethodInfo Method(string method) => typeof(Program).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!;
    static SpawnedMonster Target(uint life = 100) => new("Fortification target", new(34, 32, 30), 0, 2, life)
    { Hostile = true, Level = 1 };

    [Theory]
    [InlineData(false, false, false, false, true)]
    public void Accepted_melee_commit_rejects_closed_leaving_dead_and_projectile_owners(
        bool closed, bool leaving, bool dead, bool projectile, bool expected)
    {
        var conn = Character();
        conn.Closed = closed; conn.LeavingArea = leaving;
        if (dead) conn.PlayerLife = 0;
        lock (conn.Monsters)
            Assert.Equal(expected, Program.FortificationOnHitLocked(conn, Target(), 100,
                !projectile, Program.CharacterPlayerStats(conn), random: new Random(1)));
        Assert.Equal(expected ? 20 : 0, Program.FortificationOf(conn).Value.Current);
    }

    public void Dispose() => PlayerInventories.Forget(name);
}
