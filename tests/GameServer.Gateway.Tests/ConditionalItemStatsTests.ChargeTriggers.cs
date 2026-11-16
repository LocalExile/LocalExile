using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Conditional_hit_charge_rows_reach_real_charge_sessions()
    {
        var uniques = new UniqueCatalog(Program.Items);
        foreach (var (name, condition, type, expectedStat) in new[]
        {
            ("The Red Trail", "_while_bleeding", ChargeType.Frenzy, "gain_frenzy_charge_on_hit_while_bleeding"),
            ("Replica Red Trail", "_while_poisoned", ChargeType.Power, "add_power_charge_on_hit_%"),
        })
        {
            var item = uniques.Create(name, new Random(504))!;
            foreach (bool active in new[] { false, true, false })
            {
                var stats = ItemModStats.Sum([item], Program.Items.Mods, subject: new(Flags: new Dictionary<string, bool>
                    { [condition] = active }), statExists: Program.Stats.Has);
                Assert.Equal(active ? (type == ChargeType.Frenzy ? 1 : 100) : 0, stats.GetValueOrDefault(expectedStat));
                foreach (bool spell in new[] { false, true })
                {
                    var session = new ChargeSession(); session.Observe(stats.GetValueOrDefault);
                    var result = session.Admit(0, [new ChargeEvent.Hit(false, spell, !spell, MonsterRarity.Normal)],
                        stats.GetValueOrDefault, () => default, true, new Random(1));
                    Assert.Equal(active ? 1 : 0, session.Effective(type));
                    Assert.Equal(active, result.CountsChanged);
                    session.Refresh(10); Assert.Equal(0, session.Effective(type));
                }
                Assert.DoesNotContain(ChargeGains.Roll(new ChargeEvent.Kill(MonsterRarity.Normal),
                    stats.GetValueOrDefault, default, new Random(1)), gain => gain.Source == expectedStat);
            }
        }
        // The retained Crusader row uses the same power-on-hit consumer; no current factory unique is claimed.
        Assert.True(StatConditions.NamesACharge("add_power_charge_on_kill_%_chance"));
        Assert.False(StatConditions.NamesACharge("add_power_charge_on_skill_hit_%"));
        var legacy = WithMod("PowerChargeOnHit4CrusaderItemsUnique__1", 12);
        foreach (int count in new[] { 3, 4, 5 })
        {
            var stats = ItemModStats.Sum([legacy], Program.Items.Mods,
                subject: new(Value: key => key == "equipped_crusader" ? count : null), statExists: Program.Stats.Has);
            Assert.Equal(count >= 4 ? 12 : 0, stats.GetValueOrDefault("add_power_charge_on_skill_hit_%"));
            var hit = new ChargeEvent.Hit(false, true, false, MonsterRarity.Normal);
            Assert.Equal(count >= 4 ? 1 : 0, ChargeGains.Roll(hit, stats.GetValueOrDefault, default, new PhasingRoll(11)).Count);
            Assert.Empty(ChargeGains.Roll(hit, stats.GetValueOrDefault, default, new PhasingRoll(12)));
        }
    }

    void Shield_property_thresholds_read_qualified_item_block()
    {
        const string input = "life_leech_from_spell_damage_permyriad_if_shield_has_30%_block_chance";
        var catalog = Program.Items; var uniques = new UniqueCatalog(catalog);
        var source = uniques.Create("Cerberus Limb", new Random(504))!;
        int amount = ItemModStats.Sum([source], catalog.Mods).GetValueOrDefault(input);
        Assert.True(amount > 0);
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(input, StatConditions.Subject.Unknown));
        Assert.Equal("base_life_leech_from_spell_damage_permyriad", StatConditions.Fold(input, Program.Stats.Has));
        foreach (int block in new[] { 0, 29, 30, 31, int.MaxValue })
        {
            var stats = ItemModStats.Sum([source], catalog.Mods,
                subject: new(Value: key => key == "shield_block_chance" ? block : null), statExists: Program.Stats.Has);
            double spell = LeechSources.From(stats.GetValueOrDefault, attack: false).Life[DamageType.Fire].Min;
            Assert.Equal(block >= 30 ? amount : 0, spell);
            Assert.Equal(0, LeechSources.From(stats.GetValueOrDefault, attack: true).Life.MinTotal);
        }
        var shield = uniques.Create("Aegis Aurora", new Random(504))!;
        var shieldType = catalog.ByHash(shield.TypeHash)!;
        int realBlock = ItemModStats.ShieldBlock(catalog, shield, shieldType); Assert.True(realBlock >= 30);
        var equipment = new List<(byte Row, InventoryItem Item)> { (PlayerInventories.Weapon1, new(1, 0, 0, source)),
            (PlayerInventories.Offhand1, new(2, 0, 0, shield)) };
        Assert.Equal(realBlock, Game.EquipmentConditionValues(equipment, catalog, [source, shield])["shield_block_chance"]);
        Assert.Equal(0, Game.EquipmentConditionValues(equipment, catalog, [source])["shield_block_chance"]);
        equipment[1] = (PlayerInventories.Offhand2, equipment[1].Item);
        Assert.Equal(0, Game.EquipmentConditionValues(equipment, catalog, [source, shield])["shield_block_chance"]);
        string composed = "life_leech_from_spell_damage_permyriad_while_on_low_life_if_shield_has_30%_block_chance";
        foreach (bool low in new[] { false, true })
            Assert.Equal(low ? StatConditions.State.Holds : StatConditions.State.Fails,
                StatConditions.Of(composed, new(Value: key => key == "shield_block_chance" ? 30 : null,
                    Flags: new Dictionary<string, bool> { ["_while_on_low_life"] = low })));
    }
    void Qualified_charge_triggers_and_shield_leech_use_live_state(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        conn.PlayerLife = conn.PlayerMaxLife;
        var inventory = Game.ItemsOf(conn); var charges = Game.ChargesOf(conn);
        Dictionary<uint, int> stats = [];
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        foreach (bool poison in new[] { false, true })
        {
            var boots = wear(poison ? "Replica Red Trail" : "The Red Trail", PlayerInventories.Boots);
            conn.IncomingAilments.Clear(); stats = Game.CharacterPlayerStats(conn);
            var type = poison ? ChargeType.Power : ChargeType.Frenzy;
            string key = poison ? "add_power_charge_on_hit_%" : "gain_frenzy_charge_on_hit_while_bleeding";
            Assert.Equal(0, Get(key));
            double at = Game.ResourceEffectsTime(conn);
            Assert.True(poison ? conn.IncomingAilments.ApplyPoison(at, 1, 4) : conn.IncomingAilments.ApplyBleed(at, 1, 4));
            stats = Game.CharacterPlayerStats(conn); Assert.True(Get(key) > 0);
            charges.Die();
            charges.Admit(at, [new ChargeEvent.Hit(false, true, false, MonsterRarity.Normal)], Get,
                () => Game.WeaponProfileFor(conn), true, new Random(1));
            Assert.Equal(1, charges.Effective(type));
            inventory.Remove(PlayerInventories.Boots, boots.Id); stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, Get(key));
            charges.Admit(at, [new ChargeEvent.Hit(false, true, false, MonsterRarity.Normal)], Get,
                () => Game.WeaponProfileFor(conn), true, new Random(1));
            Assert.Equal(1, charges.Effective(type));
            conn.IncomingAilments.Clear(); stats = Game.CharacterPlayerStats(conn);
        }
        var weapon = wear("Cerberus Limb", PlayerInventories.Weapon1);
        var shield = wear("Aegis Aurora", PlayerInventories.Offhand1);
        stats = Game.CharacterPlayerStats(conn);
        int coefficient = Get("base_life_leech_from_spell_damage_permyriad"); Assert.True(coefficient > 0);
        var target = new SpawnedMonster("Cerberus target", new(100, 100, 1), 100, 8, 1_000_000)
            { Hostile = true, Life = 1_000_000 };
        var damage = DamagePacket.Of(DamageType.Fire, 1000, 1000);
        var leech = LeechSources.From(Get, attack: false);
        Assert.Equal(coefficient / 10d, leech.Amounts(damage, 1000).Life, 6);
        conn.PlayerLife = conn.PlayerMaxLife / 2;
        conn.Clock.Start(); Game.LeechOnHitLocked(conn, target, damage, 1000, leech); conn.Clock.Stop();
        Assert.True(Game.LeechOf(conn).Active(Game.ResourceEffectsTime(conn)).Life);
        inventory.Remove(PlayerInventories.Offhand1, shield.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get("base_life_leech_from_spell_damage_permyriad"));
        Game.LeechOf(conn).Clear(Game.ResourceEffectsTime(conn));
        conn.Clock.Start(); Game.LeechOnHitLocked(conn, target, damage, 1000, LeechSources.From(Get, attack: false)); conn.Clock.Stop();
        Assert.False(Game.LeechOf(conn).Active(Game.ResourceEffectsTime(conn)).Life);
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id); conn.PlayerLife = conn.PlayerMaxLife;
        stats = Game.CharacterPlayerStats(conn);
    }

}
