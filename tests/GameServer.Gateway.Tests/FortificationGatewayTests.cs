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
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
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

    [Fact]
    public void Paused_contacts_do_not_grant_and_derived_counts_cannot_be_forged_by_dev_stats()
    {
        var conn = Character(); conn.Clock.Stop();
        Set(conn, FortificationSession.Current, 999);
        Assert.False(Program.FortificationOnHitLocked(conn, Target(), 100, true, Program.CharacterPlayerStats(conn)));
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Program.Stats.Key(FortificationSession.Current)!.Value]);
    }

    [Fact]
    public void One_count_drives_attack_pools_regeneration_panel_and_hit_mitigation_then_refunds()
    {
        var conn = Character();
        var stats = Program.CharacterPlayerStats(conn);
        Assert.True(Program.FortificationOnHitLocked(conn, Target(), 100, true, stats));
        stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(20, stats[Program.Stats.Key(FortificationSession.Current)!.Value]);
        Assert.Equal(240, stats[Program.Stats.Key(FortificationSession.Regeneration)!.Value]);
        Assert.Equal(32, conn.Pools.AttackSpeedPct); // 20% increased, then the existing unarmed 10% more.
        Assert.Equal(80, Program.FortificationDamageTaken(conn, 100));
        Set(conn, FortificationState.AttackSpeedPerStack, 0);
        Set(conn, FortificationState.LifeRegenPerStack, 0);
        conn.Pools = Program.PoolsFor(conn, 1);
        Assert.Equal(10, conn.Pools.AttackSpeedPct);
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Program.Stats.Key(FortificationSession.Regeneration)!.Value]);
        Assert.Equal(80, Program.FortificationDamageTaken(conn, 100)); // Source removal doesn't erase already gained stacks.
    }

    [Theory]
    [InlineData(false, 20, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, true)]
    public async Task Parsed_default_attack_keeps_its_direct_roll_separate_from_Impale_and_excludes_wands(
        bool wand, int count, bool fractionalImpale)
    {
        var conn = Character();
        if (wand)
            PlayerInventories.For(name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        var target = Target(fractionalImpale ? 400u : 100u); conn.Monsters.Add(42, target);
        if (fractionalImpale)
        {
            Set(conn, "attack_minimum_added_chaos_damage", 1);
            Set(conn, "attack_maximum_added_chaos_damage", 1);
            Assert.True(target.Impale.Apply(1)); // Stored reflected damage is 0.1, not part of the direct 1-damage hit.
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var use = InstanceTransfer.ReadUseSkillOnObject(new LoginWriter().U16(0x24).U32(42)
            .U16(SkillCatalog.FirstSkillId).U16(0).U16(0x408).ToArray());
        await ((Task)Method("OnUseObject").Invoke(null, ["fortification-test", writer.GetStream(),
            new ConnectionCiphers(new byte[64], false), conn, use, conn.Clock.Elapsed.TotalSeconds])!)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fractionalImpale ? 399u : 0u, target.Life);
        Assert.Equal(count, Program.FortificationOf(conn).Value.Current);
    }

    [Theory]
    [InlineData("Cyclone", 30, false)]
    [InlineData("Cyclone", 30, true)]
    [InlineData("Fireball", 0, false)]
    public async Task Actual_supported_area_skill_uses_its_melee_type_and_one_shared_AoE_gain_interval(
        string effect, int count, bool delayedNotifications)
    {
        var conn = Character(); conn.God = true;
        var gem = ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/SkillGem{effect}")!);
        var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
        { Sockets = [new(SocketColour.White, gem)] };
        PlayerInventories.For(name, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0, body);
        Set(conn, "attack_minimum_added_chaos_damage", 10000);
        Set(conn, "attack_maximum_added_chaos_damage", 10000);
        conn.Pools = Program.PoolsFor(conn, 100);
        var first = Target(); var second = Target();
        conn.Monsters.Add(42, first); conn.Monsters.Add(43, second);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var writer = new TcpClient(); var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        ushort id = Program.GemSkillIdPool.Value[0];
        var entry = (id, Program.Skills.EffectRow(effect), effect, 1, 0, gem.TypeHash);
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var gate = Program.SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        if (delayedNotifications) await gate.WaitAsync();
        var use = (Task)Method("AreaSkillHit").Invoke(null, ["fortification-area-test", writer.GetStream(),
            ciphers, conn, id, entry, (32, 32), (34, 32), true, (ushort)0, null, null])!;
        if (delayedNotifications)
        {
            Assert.True(first.Life < first.MaxLife);
            await Task.Delay(150); // Deliberately longer than the hit-gain interval, with first-target sends blocked.
            gate.Release();
        }
        await use.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Life < first.MaxLife);
        Assert.True(second.Life < second.MaxLife);
        Assert.Equal(count, Program.FortificationOf(conn).Value.Current);
        Assert.Equal(count, Program.FortificationOf(conn).Value.Uncapped); // Not two grants in the same sweep.
    }

    [Fact]
    public async Task Cached_recovery_baseline_never_includes_temporary_regeneration_during_expiry()
    {
        var conn = Character();
        var session = Program.FortificationOf(conn);
        session.State.GrantExplicitStacks(conn.Clock.Elapsed.TotalSeconds, 20, -99);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(0d, (double)Method("LifeRegenPerSecond").Invoke(null, [conn])!);
            await Task.Delay(5);
        }
        Assert.Equal(0, session.Read(conn.Clock.Elapsed.TotalSeconds).Current);
    }

    [Theory]
    [InlineData(DamageType.Physical)]
    [InlineData(DamageType.Fire)]
    [InlineData(DamageType.Chaos)]
    public void Dev_hit_route_applies_Fortification_after_type_mitigation(DamageType type)
    {
        var conn = Character();
        Program.FortificationOf(conn).State.GrantExplicitStacks(conn.Clock.Elapsed.TotalSeconds, 20);
        Assert.Equal(type == DamageType.Physical ? 80 : 40,
            (int)Method("MitigateTaken").Invoke(null, [conn, type, 100, 50, 75])!);
    }

    [Fact]
    public async Task Queued_presentation_rechecks_owner_death_inside_the_send_gate()
    {
        var conn = Character();
        Assert.True(Program.FortificationOnHitLocked(conn, Target(), 100, true, Program.CharacterPlayerStats(conn)));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var writer = new TcpClient(); var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var gate = Program.SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        var pending = Program.SendFortification("fortification-gate-test", writer.GetStream(), ciphers, conn);
        Assert.False(pending.IsCompleted);
        conn.PlayerLife = 0;
        gate.Release();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Program.FortificationOf(conn).Value.Current);
        Assert.Equal((ushort)0, Program.FortificationOf(conn).Buff);
        var bytes = new byte[reader.Available];
        await reader.GetStream().ReadExactlyAsync(bytes);
        new ConnectionCiphers(new byte[64], true).Receive.Process(bytes);
        Assert.True(bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(Program.PlayerId,
            [(Program.Stats.Key(FortificationSession.Current)!.Value, 0)]).AsSpan(13)) >= 0);
    }

    [Fact]
    public async Task Queued_charge_refresh_cannot_restore_expired_Fortification_after_its_own_refresh()
    {
        var conn = Character();
        Program.FortificationOf(conn).State.GrantExplicitStacks(conn.Clock.Elapsed.TotalSeconds, 20, -99);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var writer = new TcpClient(); var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var gate = Program.SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        var fortification = Program.SendFortification("fortification-queue-test", writer.GetStream(), ciphers, conn);
        var charge = (Task)Method("SendChargeStats").Invoke(null,
            ["fortification-queue-test", writer.GetStream(), ciphers, conn, "test"] )!;
        await Task.Delay(100);
        gate.Release();
        await Task.WhenAll(fortification, charge).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Program.FortificationOf(conn).Value.Current);
        Assert.Equal(10, conn.Pools.AttackSpeedPct);
        var bytes = new byte[reader.Available];
        await reader.GetStream().ReadExactlyAsync(bytes);
        new ConnectionCiphers(new byte[64], true).Receive.Process(bytes);
        Assert.True(bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(Program.PlayerId,
            [(Program.Stats.Key(FortificationSession.Current)!.Value, 20)]).AsSpan(13)) < 0);
    }

    [Fact]
    public async Task Reassigning_an_older_immutable_pool_does_not_acknowledge_a_newer_expiry_revision()
    {
        var conn = Character();
        Program.FortificationOf(conn).State.GrantExplicitStacks(conn.Clock.Elapsed.TotalSeconds, 30, -99);
        var before = Program.PoolsFor(conn, 1);
        Assert.Equal(43, before.AttackSpeedPct);
        await Task.Delay(100);
        _ = Program.CharacterPlayerStats(conn);
        conn.Pools = before; // A delayed older writer must not hide the expired source snapshot.
        _ = Program.CharacterPlayerStats(conn);
        Assert.Equal(10, conn.Pools.AttackSpeedPct);
        Assert.True(conn.Pools.FortificationRevision > before.FortificationRevision);
    }

    [Theory]
    [InlineData(30, 90, 63)]
    [InlineData(20, 100, 80)]
    [InlineData(100, 100, 0)]
    public void Hit_reduction_uses_exact_integer_percentage_arithmetic(int count, int damage, int expected)
    {
        var conn = Character();
        Set(conn, FortificationState.MaximumStat, 100);
        conn.Pools = Program.PoolsFor(conn, 1);
        Program.FortificationOf(conn).State.GrantExplicitStacks(conn.Clock.Elapsed.TotalSeconds, count);
        Assert.Equal(expected, Program.FortificationDamageTaken(conn, damage));
    }

    public void Dispose() => PlayerInventories.Forget(name);
}
