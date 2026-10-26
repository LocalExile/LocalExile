using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

[Collection("Ascendancy Gateway integration")]
public sealed class ChargeRuntimeAdapterTests : IDisposable
{
    readonly List<string> characters = [];
    static readonly DateTimeOffset Epoch = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    public ChargeRuntimeAdapterTests() => TestSaves.Ensure();

    ClientState Character(uint ticket = 0)
    {
        string name = "ChargeAdapter" + Guid.NewGuid().ToString("N");
        characters.Add(name);
        return new(new(0, "charge tests", 128, 128, 32, 32, null, []), ticket, 1)
        { Character = name, ClassRow = 1, PlayerLife = 50 };
    }

    static Dictionary<uint, int> Sources(params (string Stat, int Value)[] values) =>
        values.ToDictionary(v => Program.Stats.Key(v.Stat)!.Value, v => v.Value);

    static Dictionary<uint, int> Refresh(ClientState conn, params (string Stat, int Value)[] values)
    {
        var sources = Sources(values);
        Program.ChargeStats(conn, sources);
        return sources;
    }

    static int Get(IReadOnlyDictionary<uint, int> values, string name) => values.GetValueOrDefault(Program.Stats.Key(name)!.Value);
    static SpawnedMonster Target(uint life = 100, bool hostile = true) =>
        new("charge target", new WalkEstimator(50, 32, 0), 0, 0, 100) { Life = life, Hostile = hostile };

    [Fact]
    public void Raw_source_expansion_applies_inherent_charge_values_once_to_fresh_vectors()
    {
        var conn = Character();
        Refresh(conn);
        Assert.True(Program.GrantCharges(conn, ChargeType.Frenzy, 3, "test", Sources()));
        var first = Refresh(conn);
        var second = Refresh(conn);
        Assert.Equal(3, Get(first, "current_frenzy_charges"));
        Assert.Equal(12, Get(first, "attack_speed_+%"));
        Assert.Equal(12, Get(first, "base_cast_speed_+%"));
        Assert.Equal(1.12, ChargeEffects.DamageMore(name => Get(first, name)), 8);
        Assert.Equal(first.OrderBy(k => k.Key), second.OrderBy(k => k.Key));
    }

    [Fact]
    public void Power_Siphon_uses_the_actual_commit_life_and_snapshots_its_kill_chance()
    {
        var conn = Character();
        Refresh(conn);
        var raw = Sources(("add_frenzy_charge_on_kill_%_chance", 100));
        var target = Target(life: 0);
        Assert.True(Program.ChargeHitCommitted(conn, target, new(0, false), critical: false, spell: false,
            melee: false, new(), raw, skillEffect: "PowerSiphon"));
        raw[Program.Stats.Key("add_frenzy_charge_on_kill_%_chance")!.Value] = 0;
        var now = Refresh(conn);
        Assert.Equal(1, Get(now, "current_power_charges"));
        Assert.Equal(1, Get(now, "current_frenzy_charges"));
        Assert.True(Program.ChargeHitCommitted(conn, Target(life: 1), new(1, true), critical: false, spell: false,
            melee: false, new(), Sources(), skillEffect: "PowerSiphon"));
        Assert.Equal(1, Get(Refresh(conn), "current_power_charges"));
    }

    [Fact]
    public void Frenzy_once_per_use_is_claimed_by_the_first_actual_hostile_hit()
    {
        var conn = Character();
        Refresh(conn);
        var use = new Program.ChargeUse();
        Assert.False(Program.ChargeHitCommitted(conn, Target(hostile: false), new(90, false), false, false, true,
            use, Sources(), skillEffect: "Frenzy"));
        Assert.True(Program.ChargeHitCommitted(conn, Target(), new(90, false), false, false, true,
            use, Sources(), skillEffect: "Frenzy"));
        Assert.True(Program.ChargeHitCommitted(conn, Target(), new(80, false), false, false, true,
            use, Sources(), skillEffect: "Frenzy"));
        Assert.Equal(1, Get(Refresh(conn), "current_frenzy_charges"));
    }

    [Fact]
    public void Wand_gated_hit_gain_uses_the_active_set_at_commit_not_the_later_set()
    {
        var conn = Character();
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Row(384)!));
        Refresh(conn);
        var raw = Sources(("gain_power_charge_on_critical_strike_with_wands_%", 100));
        Assert.True(Program.ChargeHitCommitted(conn, Target(), new(90, false), true, false, false, new(), raw));
        items.SwapWeaponSets();
        Assert.Equal(0, Get(Refresh(conn), "current_power_charges"));
        Assert.True(Program.ChargeHitCommitted(conn, Target(), new(90, false), true, false, false, new(), raw));
        items.SwapWeaponSets();
        Assert.Equal(1, Get(Refresh(conn), "current_power_charges"));
    }

    [Fact]
    public void Incoming_commit_distinguishes_block_from_hit_and_lethal_hits_clear_while_paused()
    {
        var conn = Character();
        Refresh(conn);
        var raw = Sources(("chance_to_gain_endurance_charge_when_hit_%", 100),
            ("chance_to_gain_frenzy_charge_on_block_%", 100));
        Assert.True(Program.ChargeIncomingCommitted(conn, blocked: true, spell: false, raw));
        var blocked = Refresh(conn);
        Assert.Equal(1, Get(blocked, "current_frenzy_charges"));
        Assert.Equal(0, Get(blocked, "current_endurance_charges"));
        Assert.True(Program.ChargeIncomingCommitted(conn, blocked: false, spell: false, raw));
        Assert.Equal(1, Get(Refresh(conn), "current_endurance_charges"));
        conn.Clock.Stop();
        conn.PlayerLife = 0;
        Assert.False(Program.ChargeIncomingCommitted(conn, blocked: false, spell: false, raw));
        var dead = Refresh(conn);
        Assert.Equal(0, Get(dead, "current_frenzy_charges"));
        Assert.Equal(0, Get(dead, "current_endurance_charges"));
    }

    [Fact]
    public void Minimum_charges_remove_native_buffs_on_death_and_return_only_after_resurrection()
    {
        var conn = Character();
        Refresh(conn, ("base_minimum_frenzy_charges", 2));
        var gained = Assert.Single(Program.ChargePresentation(conn));
        Assert.Equal(2, gained.Count);
        ushort unrelatedBuff = conn.AllocateBuff();
        Assert.NotEqual(gained.Add, unrelatedBuff);
        conn.PlayerLife = 0;
        conn.Clock.Stop();
        Program.ChargesDied(conn);
        var removed = Assert.Single(Program.ChargePresentation(conn));
        Assert.Equal(gained.Add, removed.Remove);
        Assert.Equal(0, removed.Add);
        Assert.Empty(Program.ChargePresentation(conn));
        Assert.Equal(0, Get(Refresh(conn, ("base_minimum_frenzy_charges", 2)), "current_frenzy_charges"));
        conn.PlayerLife = 50;
        conn.Clock.Start();
        Assert.Equal(2, Get(Refresh(conn, ("base_minimum_frenzy_charges", 2)), "current_frenzy_charges"));
        var returned = Assert.Single(Program.ChargePresentation(conn));
        Assert.Equal(2, returned.Count);
        Assert.NotEqual(gained.Add, returned.Add);
        Assert.NotEqual(unrelatedBuff, returned.Add);
    }

    [Fact]
    public void Transfer_flushes_pending_gains_closes_old_session_and_consumes_exact_ticket_once()
    {
        uint destination = (uint)Random.Shared.Next(1, int.MaxValue);
        var old = Character();
        Refresh(old);
        Assert.True(Program.GrantCharges(old, ChargeType.Power, 2, "pending before transfer", Sources()));
        old.LeavingArea = true;
        Program.CarryCharges(old, destination, Epoch);
        old.LeavingArea = false;
        Assert.False(Program.GrantCharges(old, ChargeType.Power, 1, "stale old connection", Sources()));

        var wrong = Character(destination + 1);
        Refresh(wrong);
        Assert.False(Program.RestoreCharges(wrong, Epoch.AddSeconds(4)));
        var next = Character(destination);
        Refresh(next);
        Assert.True(Program.RestoreCharges(next, Epoch.AddSeconds(4)));
        Assert.Equal(2, Get(Refresh(next), "current_power_charges"));
        var shown = Assert.Single(Program.ChargePresentation(next));
        Assert.InRange(shown.Remaining!.Value, 5.8, 6.1);
        Assert.False(Program.RestoreCharges(next, Epoch.AddSeconds(4)));
        var duplicate = Character(destination);
        Refresh(duplicate);
        Assert.False(Program.RestoreCharges(duplicate, Epoch.AddSeconds(4)));
        Assert.Equal(0, Get(Refresh(duplicate), "current_power_charges"));
    }

    [Fact]
    public void Normal_disconnect_and_minion_kills_cannot_grant_or_carry_owner_charges()
    {
        var conn = Character();
        Refresh(conn);
        var raw = Sources(("add_frenzy_charge_on_kill_%_chance", 100));
        Assert.False(Program.ChargeKillCommitted(conn, Target(life: 0), playerKill: false, raw));
        Assert.Equal(0, Get(Refresh(conn), "current_frenzy_charges"));
        Assert.True(Program.GrantCharges(conn, ChargeType.Power, 2, "logout control", Sources()));
        Program.CloseCharges(conn);
        Assert.False(Program.GrantCharges(conn, ChargeType.Power, 1, "stale after logout", Sources()));
        var reconnect = Character();
        Refresh(reconnect);
        Assert.False(Program.RestoreCharges(reconnect, Epoch));
        Assert.Equal(0, Get(Refresh(reconnect), "current_power_charges"));
    }

    [Fact]
    public void A_destination_without_observed_limits_cannot_consume_the_ticket_lease()
    {
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        var old = Character();
        Refresh(old);
        Assert.True(Program.GrantCharges(old, ChargeType.Power, 3, "transfer control", Sources()));
        Program.CarryCharges(old, ticket, Epoch);
        var next = Character(ticket);
        Assert.False(Program.RestoreCharges(next, Epoch.AddSeconds(1)));
        Refresh(next);
        Assert.True(Program.RestoreCharges(next, Epoch.AddSeconds(1)));
        Assert.Equal(3, Get(Refresh(next), "current_power_charges"));
    }

    [Fact]
    public void Source_and_pool_construction_do_not_hold_the_combat_lock()
    {
        var conn = Character();
        var built = new List<string>();
        var publication = Program.PrepareChargePublication(conn, c =>
        {
            Assert.False(Monitor.IsEntered(c.Monsters));
            built.Add("sources");
            return Refresh(c);
        }, (c, level) =>
        {
            Assert.False(Monitor.IsEntered(c.Monsters));
            built.Add("pools");
            return c.Pools with { Level = level, AttackCriticalStrikeChance = 750 };
        });
        Assert.NotNull(publication);
        Assert.Contains("sources", built);
        Assert.Contains("pools", built);
        Assert.True(Program.ChargePublicationCurrent(conn, publication));
    }

    [Fact]
    public async Task A_charge_expiring_while_queued_for_the_cipher_gate_emits_no_stale_stack_or_cipher_bytes()
    {
        var conn = Character();
        Refresh(conn);
        Assert.True(Program.GrantCharges(conn, ChargeType.Power, 2, "gate expiry control", Sources()));
        Refresh(conn);
        var publication = Program.PrepareChargePublication(conn, c => Refresh(c), (c, _) => c.Pools with { Accuracy = 99 });
        Assert.NotNull(publication);
        Assert.True(Program.ChargePublicationCurrent(conn, publication));
        ushort nextBuff = conn.NextBuff;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accept;
        using var capture = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = client.GetStream().CopyToAsync(capture, timeout.Token);
        byte[] key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var cipher = new ConnectionCiphers(key, isClient: false);
        var receiving = new ConnectionCiphers(key, isClient: true);
        var gate = Program.SendGates.GetValue(cipher, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(timeout.Token);
        double publicationAt = conn.Clock.Elapsed.TotalSeconds;
        bool current = true;
        Task queued = Program.Send("charge-adapter-test", server.GetStream(), cipher, [], null, atGate: () =>
        {
            byte[]? bytes = Program.ChargeBuffPublication(conn, publication, at: publicationAt);
            current = bytes is not null;
            return bytes;
        });
        Assert.False(queued.IsCompleted);
        // Advance the injected game-time boundary, without sleeping or altering any process/game clock.
        publicationAt += Charges.BaseDurationMs / 1000.0 + 1;
        gate.Release();
        await queued.WaitAsync(timeout.Token);
        Assert.False(current);
        Assert.False(Program.CompleteChargePublication(conn, publication, accepted: current));
        Assert.False(Program.ChargePublicationCurrent(conn, publication, at: publicationAt));
        Assert.Equal(nextBuff, conn.NextBuff); // the rejected add did not allocate/remember a native buff

        byte[] control = InstanceProtocol.LifeUpdate(0x1000, 50);
        byte[] expected = [.. control];
        await Program.Send("charge-adapter-test", server.GetStream(), cipher, control, null);
        server.Client.Shutdown(SocketShutdown.Send);
        await reading;
        byte[] actual = capture.ToArray();
        receiving.Receive.Process(actual);
        Assert.Equal(expected, actual); // a null factory consumed neither cipher bytes nor stream bytes
    }

    [Fact]
    public void A_concurrent_pool_rebuild_invalidates_a_prepared_charge_publication()
    {
        var conn = Character();
        var publication = Program.PrepareChargePublication(conn, c => Refresh(c), (c, _) => c.Pools with { Accuracy = 99 });
        Assert.NotNull(publication);
        conn.Pools = conn.Pools with { Accuracy = 100 };
        Assert.False(Program.ChargePublicationCurrent(conn, publication));
        Assert.Null(Program.ChargeBuffPublication(conn, publication));
        Assert.False(Program.CompleteChargePublication(conn, publication, accepted: true));
    }

    public void Dispose()
    {
        foreach (string name in characters) PlayerInventories.Forget(name);
    }
}
