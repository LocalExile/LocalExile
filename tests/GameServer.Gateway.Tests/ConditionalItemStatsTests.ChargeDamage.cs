using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_charge_damage_sources_reach_incoming_and_attack_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        byte[] slots = [PlayerInventories.Weapon1, PlayerInventories.Belt, 5];
        var saved = inventory.EquipmentSnapshot().Where(e => slots.Contains(e.Row)).ToArray();
        var amulet = wear("Astramentis", 5);
        var charges = Game.ChargesOf(conn);
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Count(ChargeType type, int count)
        {
            charges.Admit(Game.ResourceEffectsTime(conn), [new ChargeEvent.Set(type, count)], Get,
                () => Game.WeaponProfileFor(conn), true, new Random(1));
            stats = Game.CharacterPlayerStats(conn);
        }
        var hyaon = wear("Hyaon's Fury", PlayerInventories.Weapon1);
        stats = Game.CharacterPlayerStats(conn);
        int coefficient = Get(ItemChargeDamage.TakenPerFrenzy); Assert.True(coefficient > 0);
        Count(ChargeType.Frenzy, 0);
        var baseline = stats;
        foreach (int count in new[] { 1, 3, 0 })
        {
            Count(ChargeType.Frenzy, count);
            foreach (var type in Enum.GetValues<DamageType>())
                foreach (bool melee in new[] { false, true })
                    Assert.Equal(count * coefficient, PlayerDamageTaken.IncreasedPct(type, melee, Get)
                        - PlayerDamageTaken.IncreasedPct(type, melee, n => Program.Stats.Key(n) is uint k ? baseline.GetValueOrDefault(k) : 0));
            // The generic raw input must reach damage over time as well as hits. Keep typed/reduction effects separate.
            int Generic(string name) => name == "base_damage_taken_+%"
                ? Get(name) - baseline.GetValueOrDefault(Program.Stats.Key(name)!.Value) : 0;
            double carry = 0;
            Assert.Equal(1000 + count * coefficient * 10, IncomingResourceDefences.TakenOverTime(1000, Generic, ref carry));
            Assert.Equal(0, carry, 8);
        }
        Count(ChargeType.Frenzy, 3);
        var conversion = wear("Olesya's Delight", PlayerInventories.Belt);
        stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get("current_frenzy_charges"));
        Assert.Equal(baseline.GetValueOrDefault(Program.Stats.Key("base_damage_taken_+%")!.Value), Get("base_damage_taken_+%"));
        inventory.Remove(PlayerInventories.Belt, conversion.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(3 * coefficient, Get("base_damage_taken_+%") - baseline.GetValueOrDefault(Program.Stats.Key("base_damage_taken_+%")!.Value));
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        Assert.True(Game.IsInactive(conn, hyaon.Id)); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get(ItemChargeDamage.TakenPerFrenzy));
        conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
        Assert.False(Game.IsInactive(conn, hyaon.Id));
        inventory.Remove(PlayerInventories.Weapon1, hyaon.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get(ItemChargeDamage.TakenPerFrenzy));

        var auxium = wear("Auxium", PlayerInventories.Belt);
        stats = Game.CharacterPlayerStats(conn);
        coefficient = Get(ItemChargeDamage.AttackElementalPerPower); Assert.True(coefficient > 0);
        Count(ChargeType.Power, 0);
        int ordinary = Get("elemental_damage_with_attack_skills_+%");
        foreach (int count in new[] { 1, 3, 0 })
        {
            Count(ChargeType.Power, count);
            Assert.Equal(ordinary + count * coefficient, Get("elemental_damage_with_attack_skills_+%"));
            int Added(string name) => name == "elemental_damage_with_attack_skills_+%" ? Get(name) - ordinary : 0;
            foreach (var type in Enum.GetValues<DamageType>())
                foreach (bool spell in new[] { false, true })
                    foreach (bool secondary in new[] { false, true })
                    {
                        var hit = PlayerHitDamage.Resolve(DamagePacket.Of(type, 100, 100), Added, new(), spell, secondary: secondary);
                        Assert.Equal(100 + (!spell && !secondary && DamageTypes.IsElemental(type) ? count * coefficient : 0), hit.MinTotal, 8);
                    }
            // A converted elemental portion inherits the elemental attack bucket exactly once.
            var converted = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50);
            var packet = PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Physical, 100, 100), Added, converted, false);
            Assert.Equal(50, packet[DamageType.Physical].Min, 8);
            Assert.Equal(50 * (1 + count * coefficient / 100d), packet[DamageType.Fire].Min, 8);
        }
        Count(ChargeType.Power, 3);
        conversion = wear("Graven's Secret", PlayerInventories.Belt);
        // Graven and Auxium share a slot, so the real swap removes Auxium. A separate expansion control excludes
        // converted Power even for diagnostic combinations that ordinary equipment cannot provide.
        var convertedStats = ItemChargeDamage.Expand(n => n == ItemChargeDamage.AttackElementalPerPower ? coefficient : 0,
            _ => 3, _ => true).ToArray();
        Assert.Empty(convertedStats);
        inventory.Remove(PlayerInventories.Belt, conversion.Id);
        var restored = wear("Auxium", PlayerInventories.Belt);
        stats = Game.CharacterPlayerStats(conn); Assert.Equal(ordinary + 3 * coefficient, Get("elemental_damage_with_attack_skills_+%"));
        inventory.Remove(PlayerInventories.Belt, restored.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(ordinary, Get("elemental_damage_with_attack_skills_+%"));
        foreach (byte row in slots) foreach (var item in inventory.Items(row)) inventory.Remove(row, item.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        Game.ChargesOnDeath(conn); conn.Pools = Game.PoolsFor(conn, 100);
    }
}
