using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Her_embrace_uses_accepted_ignites_and_qualified_source_lifetimes(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var integration = new HerEmbraceState { At = 0, Until = 3, PerSecond = 100 };
        integration.Settle(1); Assert.Equal(100, integration.Pending);
        integration.Settle(1); Assert.Equal(100, integration.Pending);
        integration.Settle(10); Assert.Equal(300, integration.Pending);
        integration.Settle(20); Assert.Equal(300, integration.Pending);
        integration.Clear(); Assert.Equal(0, integration.Pending);
        var inventory = Game.ItemsOf(conn);
        var weapon = wear("Oni-Goroshi", PlayerInventories.Weapon1);
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(3000, Get(Game.HerEmbraceGain)); Assert.Equal(30, Get(Game.HerEmbraceDegen));
        Assert.Equal(0, Get("has_her_embrace"));
        int speed = Get("attack_speed_+%"), movement = Get("base_movement_velocity_+%");
        var target = new SpawnedMonster("Her Embrace target", new(100, 100, 1), 100, 8, 1_000_000)
            { Hostile = true, Life = 1_000_000 };
        const uint id = 0x504AE; conn.Monsters[id] = target;
        var ignite = new MonsterIgnites.Settings(100, 10, 10, 10, 10, 4);
        double now = Game.ResourceEffectsTime(conn);
        Assert.Null(Game.IgniteOnHitLocked(conn, id, target, ignite with { Chance = 0 }, false, now));
        Assert.Equal(0, Game.HerEmbraceOf(conn).Until);
        target.KingGrace = true;
        Assert.Null(Game.IgniteOnHitLocked(conn, id, target, ignite, false, now)); target.KingGrace = false;
        Assert.Equal(0, Game.HerEmbraceOf(conn).Until);
        conn.Clock.Start();
        Assert.NotNull(Game.IgniteOnHitLocked(conn, id, target, ignite, false, Game.ResourceEffectsTime(conn)));
        conn.Clock.Stop();
        now = Game.ResourceEffectsTime(conn);
        var state = Game.HerEmbraceOf(conn);
        Assert.Equal(new[] { weapon.Id }, state.Hosts);
        Assert.InRange(state.Until - now, 2.9, 3);
        stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(1, Get("has_her_embrace"));
        Assert.Equal(speed + 20, Get("attack_speed_+%"));
        Assert.Equal(movement + 20, Get("base_movement_velocity_+%"));
        Assert.Equal(123, Get("attack_physical_damage_%_to_add_as_fire"));
        Assert.Equal(30 * (double)conn.Pools.Level * ((double)conn.PlayerMaxLife + conn.Pools.EnergyShield) / 6000,
            state.PerSecond, 6);
        Assert.Equal(1, Get("base_cannot_be_stunned"));
        foreach (string ailment in new[] { "freeze", "chill", "ignite" }) Assert.Equal(1, Get("base_immune_to_" + ailment));
        var conversion = DamageStats.Conversion(stats, Program.Stats, attack: true);
        Assert.Equal(123, DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion)[DamageType.Fire].Min);
        Assert.Equal(0, DamagePacket.Of(DamageType.Physical, 100, 100)
            .Convert(DamageStats.Conversion(stats, Program.Stats, attack: false))[DamageType.Fire].Min);
        Assert.Equal(31, Assert.Single(Game.HerEmbracePresentation(conn)!, p => p[0] == 1 && p[1] == 0xc1).Length);
        Assert.True(state.Buff > 0);
        var carried = Assert.IsType<Game.CarriedHerEmbrace>(Game.CaptureHerEmbrace(conn, now));
        Game.RestoreHerEmbrace(conn, carried, now);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(1, Get("has_her_embrace"));
        state.Pending = 100; state.TakenCarry = state.BypassCarry = 0;
        conn.EnergyShield.Empty(); conn.PlayerLife = conn.PlayerMaxLife;
        var taken = new Dictionary<uint, int>(stats);
        // Incoming mitigation is exercised independently of the outgoing Oni item and its granted immunities.
        uint before = conn.PlayerLife;
        int loss = Game.AdvanceHerEmbraceDamageLocked(conn, now, taken);
        Assert.True(loss > 0); Assert.Equal((uint)loss, before - conn.PlayerLife);
        Assert.Equal(0, Game.AdvanceHerEmbraceDamageLocked(conn, now, taken));
        conn.PlayerLife = conn.PlayerMaxLife;
        Game.RestoreHerEmbrace(conn, carried with { Seconds = 0, Pending = 0 }, now);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("has_her_embrace"));
        Assert.Equal(speed, Get("attack_speed_+%")); Assert.Equal(0, Get("attack_physical_damage_%_to_add_as_fire"));
        conn.Clock.Start();
        Assert.NotNull(Game.IgniteOnHitLocked(conn, id, target, ignite, false, Game.ResourceEffectsTime(conn)));
        conn.Clock.Stop(); stats = Game.CharacterPlayerStats(conn); Assert.Equal(1, Get("has_her_embrace"));
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("has_her_embrace")); Assert.Empty(state.Hosts);
        conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("has_her_embrace"));
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id);
        Game.RestoreHerEmbrace(conn, carried, now);
        stats = Game.CharacterPlayerStats(conn); Assert.Empty(state.Hosts); Assert.Equal(0, Get("has_her_embrace"));
        Assert.Empty(conn.PlayerStats); conn.Monsters.Remove(id);
    }
}
