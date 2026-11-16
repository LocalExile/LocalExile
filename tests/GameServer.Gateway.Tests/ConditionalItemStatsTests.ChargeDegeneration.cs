using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Charge_degeneration_integrates_actual_sources_and_boundaries()
    {
        var uniques = new UniqueCatalog(Program.Items);
        var rows = new[] { ("Nebuloch", ChargeDegeneration.Fire, ChargeType.Endurance, 0),
            ("Hopeshredder", ChargeDegeneration.Cold, ChargeType.Frenzy, 1),
            ("Shimmeron", ChargeDegeneration.Lightning, ChargeType.Power, 2) };
        foreach (var (name, stat, type, index) in rows)
        {
            var item = uniques.Create(name, new Random(504))!;
            var raw = ItemModStats.Sum([item], Program.Items.Mods);
            int coefficient = raw.GetValueOrDefault(stat); Assert.True(coefficient > 0, name);
            double clock = 0;
            // Movement ends halfway through a large update; the session asks for the actual moving overlap.
            double Moving(double from, double until) => Math.Max(0, Math.Min(until, 1.5) - Math.Max(from, .5));
            var session = new ChargeSession(clock: () => clock, movingSeconds: Moving);
            session.Observe(raw.GetValueOrDefault);
            session.Degeneration.PerMinute[index] = coefficient;
            void Admit(double at, params ChargeEvent[] events)
            {
                clock = at; session.Admit(at, events, raw.GetValueOrDefault, () => default, true, new Random(1));
            }
            Admit(0, new ChargeEvent.Set(type, 3));
            session.SettleDegeneration(.25);
            Assert.Equal(0, session.Degeneration.Pending[index]); // No movement, hit or critical yet.
            Admit(.5, new ChargeEvent.WhenHit(), new ChargeEvent.Hit(true, false, true, MonsterRarity.Normal));
            session.SettleDegeneration(2);
            double expected = coefficient / 60d * 3 * (index == 1 ? 1 : 1.5);
            Assert.Equal(expected, session.Degeneration.Pending[index], 6);
            session.SettleDegeneration(2); Assert.Equal(expected, session.Degeneration.Pending[index], 6);
            session.SettleDegeneration(20);
            Assert.Equal(coefficient / 60d * 3 * (index == 1 ? 1 : 4), session.Degeneration.Pending[index], 6);

            // Expiry splits a recent interval, while a minimum charge remains and a converted charge never counts.
            clock = 0; session = new ChargeSession(clock: () => clock, movingSeconds: (from, until) => Math.Max(0, until - from));
            raw["base_minimum_" + Charges.Name(type) + "_charges"] = 1;
            raw["charge_duration_+%"] = -90;
            session.Observe(raw.GetValueOrDefault); session.Degeneration.PerMinute[index] = coefficient;
            Admit(0, new ChargeEvent.Set(type, 3), new ChargeEvent.WhenHit(), new ChargeEvent.Hit(true, false, true, MonsterRarity.Normal));
            clock = 2; session.Refresh(2);
            Assert.Equal(coefficient / 60d * 4, session.Degeneration.Pending[index], 6); // 3*1 + 1*1
            double prior = session.Degeneration.Pending[index];
            raw[Charges.ConversionFlag(type)] = 1; session.Observe(raw.GetValueOrDefault);
            session.SettleDegeneration(3); Assert.Equal(prior, session.Degeneration.Pending[index]);
            session.Die(); Assert.Equal(0, session.Degeneration.Pending[index]);
        }
    }

    void Qualified_charge_degeneration_reaches_resources(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        conn.PlayerLife = conn.PlayerMaxLife;
        var source = wear("Shimmeron", PlayerInventories.Weapon1);
        conn.Pools = Game.PoolsFor(conn, 100);
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var charges = Game.ChargesOf(conn); var state = charges.Degeneration;
        Assert.True(state.PerMinute[2] > 0); Assert.Contains(source.Id, state.Hosts);
        Assert.False(Game.ChargeDegenerationActive(conn));
        double at = Game.ResourceEffectsTime(conn);
        charges.Admit(at, [new ChargeEvent.Set(ChargeType.Power, 3),
            new ChargeEvent.Hit(true, true, false, MonsterRarity.Normal)], Get,
            () => Game.WeaponProfileFor(conn), true, new Random(1));
        Assert.True(Game.ChargeDegenerationActive(conn));
        conn.EnergyShield.Empty(); conn.PlayerLife = conn.PlayerMaxLife;
        // The actual coefficient and held charges create the pending amount; mitigation then consumes it once.
        charges.SettleDegeneration(at + .5);
        double raw = state.PerMinute[2] / 60d * 3 * .5;
        Assert.Equal(raw, state.Pending[2], 6);
        uint before = conn.PlayerLife;
        int damage = Game.AdvanceChargeDegenerationLocked(conn, at + .5, stats);
        Assert.True(damage > 0); Assert.Equal((uint)damage, before - conn.PlayerLife);
        Assert.Equal(0, Game.AdvanceChargeDegenerationLocked(conn, at + .5, stats));
        var carried = Game.CaptureChargeDegeneration(conn, at + .5);
        Game.RestoreChargeDegeneration(conn, carried, at);
        Assert.Equal(3.5, state.CritUntil - at, 6); Assert.Equal(at, state.At);
        inventory.Remove(PlayerInventories.Weapon1, source.Id);
        stats = Game.CharacterPlayerStats(conn); Assert.Empty(state.Hosts); Assert.False(state.Active);
        state.Clear(at); charges.SettleDegeneration(at + 1); Assert.All(state.Pending, p => Assert.Equal(0, p));
        inventory.Put(PlayerInventories.Weapon1, source);
        conn.PlayerLife = conn.PlayerMaxLife;
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        Game.CharacterPlayerStats(conn); Assert.Empty(state.Hosts);
        conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
        Game.CharacterPlayerStats(conn); Assert.Contains(source.Id, state.Hosts);
        Game.ChargesOnDeath(conn); Assert.All(state.Pending, p => Assert.Equal(0, p));
        inventory.Remove(PlayerInventories.Weapon1, source.Id); Game.CharacterPlayerStats(conn);
        var attributes = wear("Astramentis", 5);
        source = wear("Nebuloch", PlayerInventories.Weapon1); stats = Game.CharacterPlayerStats(conn);
        at = Game.ResourceEffectsTime(conn);
        charges.Admit(at, [new ChargeEvent.Set(ChargeType.Endurance, 3)], Get,
            () => Game.WeaponProfileFor(conn), true, new Random(1));
        conn.Clock.Start();
        Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 10, block: false, spell: false,
            suppress: false, out _));
        conn.Clock.Stop();
        at = Game.ResourceEffectsTime(conn);
        charges.Step(at, Get, () => Game.WeaponProfileFor(conn), true, false, false, new Random(1));
        Assert.True(state.HitUntil > at); // A real accepted contact counts even when armour reduces its damage to zero.
        Assert.True(Game.ChargeDegenerationActive(conn));
        inventory.Remove(PlayerInventories.Weapon1, source.Id); inventory.Remove(5, attributes.Id); Game.CharacterPlayerStats(conn);
    }
}
