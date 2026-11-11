using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Elusive_uses_actual_critical_sources_decay_and_conditional_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var model = new ElusiveState { Source = "model", Initial = 100 };
        Assert.Equal(100, model.Effect(0)); Assert.Equal(80, model.Effect(1)); Assert.Equal(0, model.Effect(5));
        model.Initial = 140; Assert.Equal(20, model.Effect(6)); Assert.Equal(0, model.Effect(7));
        model.Minimum = 20; Assert.Equal(0, model.Effect(6));
        model.Minimum = 0; model.RisingSeconds = 1; model.Initial = 100;
        Assert.Equal(120, model.Effect(1)); Assert.Equal(100, model.Effect(2)); Assert.Equal(0, model.Effect(7));
        var inventory = Game.ItemsOf(conn);
        ItemMod Rolled(string name) => Program.Items.Mods.ById(name)!.Roll(ModSection.Explicit, new Random(504));
        var helmet = inventory.Add(PlayerInventories.Helmet, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!)
                with { Rarity = ItemRarity.Rare, Mods = [Rolled("Strength10")] });
        var source = wear("Replica Harvest", PlayerInventories.Weapon1);
        var badge = wear("Badge of the Brotherhood", 5);
        foreach (var old in inventory.Items(PlayerInventories.Boots)) inventory.Remove(PlayerInventories.Boots, old.Id);
        var diagnostic = inventory.Add(PlayerInventories.Boots, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/Boots/BootsDex1")!)
                with { Mods = [Rolled("EnchantmentElusive"), Rolled("AnimalCharmNoExtraDamageFromCriticalStrikesIfElusive")] });
        conn.Pools = Game.PoolsFor(conn, 100);
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string n) => stats.GetValueOrDefault(Program.Stats.Key(n)!.Value);
        Assert.Equal(100, Get(Game.ElusiveOnCritical)); Assert.Equal(0, Get("affected_by_elusive"));
        Assert.Equal(0, Get("avoid_all_elemental_status_%")); Assert.Equal(0, Get("self_take_no_extra_damage_from_critical_strikes"));
        int move = Get("base_movement_velocity_+%"), avoid = Get("avoid_damage_%");
        var charges = Game.ChargesOf(conn);
        charges.Admit(Game.ResourceEffectsTime(conn), [new ChargeEvent.Set(ChargeType.Power, 3)], Get,
            () => Game.WeaponProfileFor(conn), true, new PhasingRoll(99));
        stats = Game.CharacterPlayerStats(conn);
        int coefficient = Get("elusive_effect_on_self_+%_per_power_charge"); Assert.InRange(coefficient, 7, 10);
        conn.Clock.Start();
        Assert.False(Game.TryGainElusiveLocked(conn, false, false, Game.ResourceEffectsTime(conn), new PhasingRoll(99)));
        Assert.True(Game.TryGainElusiveLocked(conn, true, false, Game.ResourceEffectsTime(conn), new PhasingRoll(99)));
        conn.Clock.Stop();
        double now = Game.ResourceEffectsTime(conn); var state = Game.ElusiveOf(conn);
        Assert.Equal(100 + 3 * coefficient, state.Initial);
        double started = state.Started;
        conn.Clock.Start();
        Assert.False(Game.TryGainElusiveLocked(conn, true, true, Game.ResourceEffectsTime(conn), new PhasingRoll(0)));
        conn.Clock.Stop(); Assert.Equal(started, state.Started);
        var carried = Assert.IsType<Game.CarriedElusive>(Game.CaptureElusive(conn, Game.ResourceEffectsTime(conn)));
        now = Game.ResourceEffectsTime(conn);
        Game.RestoreElusive(conn, carried with { Age = 0 }, now);
        Assert.True(Game.EnsureConditionalItemPools(conn)); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(1, Get("affected_by_elusive")); Assert.Equal(20, Get("avoid_all_elemental_status_%"));
        Assert.Equal(1, Get("self_take_no_extra_damage_from_critical_strikes"));
        Assert.Equal(MonsterModEffects.CriticalMultiplier, Game.SelfCriticalReduction(Get));
        Assert.Equal(move + (int)Math.Floor(state.Initial * .3), Get("base_movement_velocity_+%"));
        Assert.Equal(Math.Min(75, avoid + (int)Math.Floor(state.Initial * .15)), PlayerDamageTaken.AvoidPct(DamageType.Fire, Get));
        var ailments = new PlayerElementalAilments();
        ailments.Inflict(PlayerElementalAilments.Kind.Shock, 20, 2, now, Get, new PhasingRoll(19));
        Assert.Equal(0, ailments.Snapshot(now).Shock);
        ailments.Inflict(PlayerElementalAilments.Kind.Shock, 20, 2, now, Get, new PhasingRoll(20));
        Assert.Equal(20, ailments.Snapshot(now).Shock);
        Assert.Equal(31, Assert.Single(Game.ElusivePresentation(conn)!, p => p[0] == 1 && p[1] == 0xc1).Length);
        Game.RestoreElusive(conn, carried with { Age = 1 }, now);
        Assert.True(Game.EnsureConditionalItemPools(conn)); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(move + (int)Math.Floor((state.Initial - 20) * .3), Get("base_movement_velocity_+%"));
        Game.RestoreElusive(conn, carried with { Age = 100 }, now);
        Assert.True(Game.EnsureConditionalItemPools(conn)); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get("affected_by_elusive")); Assert.Equal(0, Get("avoid_all_elemental_status_%"));
        Assert.Equal(move, Get("base_movement_velocity_+%")); Assert.Equal(0, Get("self_take_no_extra_damage_from_critical_strikes"));
        conn.Clock.Start();
        Assert.True(Game.TryGainElusiveLocked(conn, true, false, Game.ResourceEffectsTime(conn), new PhasingRoll(0)));
        conn.Clock.Stop();
        Assert.Equal(2, charges.Effective(ChargeType.Power));
        Assert.Equal(100 + 2 * coefficient, state.Initial);
        Game.RestoreElusive(conn, carried with { Age = 100 }, Game.ResourceEffectsTime(conn));
        var target = new SpawnedMonster("critical producer", new(100, 100, 1), 100, 8, 1000) { Hostile = true, Life = 1000 };
        conn.Clock.Start(); Game.ChargesOnHit(conn, target, critical: true, spell: true); conn.Clock.Stop();
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(1, Get("affected_by_elusive"));
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("affected_by_elusive")); Assert.Equal("", state.Source);
        conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(0, Get("affected_by_elusive"));
        inventory.Remove(PlayerInventories.Weapon1, source.Id); Game.RestoreElusive(conn, carried, now);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal("", state.Source); Assert.Equal(0, Get("affected_by_elusive"));
        inventory.Remove(5, badge.Id); inventory.Remove(PlayerInventories.Helmet, helmet.Id);
        inventory.Remove(PlayerInventories.Boots, diagnostic.Id); charges.Die();
        Assert.Empty(conn.PlayerStats);
    }
}
