using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_low_life_claw_damage_reaches_the_attacking_hand(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        // A type-independent weapon increase is added before conversion, once for local and globally added damage.
        var probe = new SkillCatalog.Unarmed(1000, 100, 100, 8)
        {
            ItemClass = "Claw", LocalDamage = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100)),
        };
        var values = new Dictionary<uint, int> { [Key("damage_+%")] = 50, [Key("claw_damage_+%")] = 70,
            [Key("physical_damage_%_to_convert_to_fire")] = 50 };
        var conversion = DamageStats.Conversion(values, Program.Stats, attack: true);
        var converted = Game.AttackHitConverted(probe, values, melee: true, conversion);
        Assert.Equal(110, converted[DamageType.Physical].Min, 8);
        Assert.Equal(330, converted[DamageType.Fire].Min, 8);
        Assert.Equal(440, converted.MinTotal, 8);
        int Get(string name) => Program.Stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;
        Assert.Equal(440, AttackSkillHit.Resolve(probe, values, [], Program.Stats, 10000, melee: true).MinTotal, 8);
        Assert.Equal(440, OrdinaryRangedShot.Capture(probe, values, [], Program.Stats, 1000, 0, 150).Hit.MinTotal, 8);
        Assert.Equal(440, SupportedAttackDamage.Hit(probe, Get, new HashSet<int> { 0, 19 }).MinTotal, 8);
        Assert.Equal(300, Game.AttackHitConverted(probe with { ItemClass = "Dagger" }, values, true, conversion).MinTotal, 8);
        Assert.Equal(300, Game.AttackHitConverted(probe with { ItemClass = null }, values, true, conversion).MinTotal, 8);

        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot().ToArray();
        var overrides = conn.PlayerStats.ToArray(); uint lifeBefore = conn.PlayerLife;
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        conn.PlayerStats.Clear(); conn.PlayerLife = 1000; conn.Experience = CharacterStats.ExperienceFor(100);
        try
        {
            wear("Astramentis", 5);
            var source = wear("Replica Last Resort", PlayerInventories.Weapon1);
            const string conditional = "claw_damage_+%_while_on_low_life";
            int coefficient = ItemModStats.Sum([source.Item], Program.Items.Mods).GetValueOrDefault(conditional);
            Assert.True(coefficient > 0);
            var gems = new[] { "SkillGemDoubleStrike", "SkillGemFireball" }.Select(name =>
                new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!))).ToArray();
            inventory.Change(PlayerInventories.Weapon1, source.Id, item => item with { Sockets = gems, Links = [1, 1] });
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            var high = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, high.GetValueOrDefault(Key("claw_damage_+%")));
            Assert.Equal(1, high.GetValueOrDefault(Key("base_deal_no_damage")));
            Assert.Equal(0, Game.AttackHit(Game.WeaponFor(conn), high, true).MinTotal);
            conn.PlayerLife = Math.Max(1, conn.PlayerMaxLife / 2);
            var low = Game.CharacterPlayerStats(conn); Assert.Equal(coefficient, low.GetValueOrDefault(Key("claw_damage_+%")));
            Assert.Equal(0, low.GetValueOrDefault(Key("base_deal_no_damage")));
            var weapon = Game.WeaponFor(conn); Assert.Equal("Claw", weapon.ItemClass);
            var control = new Dictionary<uint, int>(low) { [Key("claw_damage_+%")] = 0 };
            var hit = Game.AttackHit(weapon, low, true);
            Assert.True(hit.MinTotal > Game.AttackHit(weapon, control, true).MinTotal);
            Assert.Equal((int)Math.Floor(hit[DamageType.Physical].Min), low[StatKeys.MainHandMinPhysical]);
            // A different attacking hand cannot borrow the main hand's weapon family.
            var dagger = weapon with { ItemClass = "Dagger" };
            Assert.Equal(Game.AttackHit(dagger, control, true).MinTotal, Game.AttackHit(dagger, low, true).MinTotal, 8);
            foreach (string sourceStat in new[] { "attack_minimum_added_fire_damage", "attack_maximum_added_fire_damage",
                         "attack_minimum_added_physical_damage", "attack_maximum_added_physical_damage" })
                low[Key(sourceStat)] = control[Key(sourceStat)] = 100;
            Assert.Equal(Game.DefaultAttackIgnite(conn, control, weapon), Game.DefaultAttackIgnite(conn, low, weapon));
            Assert.Equal(Game.DefaultAttackBleed(conn, control, weapon), Game.DefaultAttackBleed(conn, low, weapon));
            Assert.Equal(Game.DefaultAttackPoison(conn, control, weapon), Game.DefaultAttackPoison(conn, low, weapon));

            var attack = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "DoubleStrike");
            var spell = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball");
            var attackInputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(conn, attack.Id));
            var spellInputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(conn, spell.Id));
            Assert.Equal(coefficient, attackInputs.WeaponIncreased); Assert.Equal(0, spellInputs.WeaponIncreased);
            Assert.True(Game.SupportedHitFrom(attackInputs, false).MinTotal
                > Game.SupportedHitFrom(attackInputs with { WeaponIncreased = 0 }, false).MinTotal);
            Assert.Equal(Game.SupportedHitFrom(spellInputs, false).MinTotal,
                Game.SupportedHitFrom(spellInputs with { WeaponIncreased = coefficient }, false).MinTotal, 8);
            Assert.Equal(Game.SupportedAilmentRowsFrom(attackInputs, "main_hand"),
                Game.SupportedAilmentRowsFrom(attackInputs with { WeaponIncreased = 0 }, "main_hand"));
            var withTargetTerm = attackInputs with
            {
                FrozenStats = new Dictionary<uint, int>(attackInputs.FrozenStats) { [Key("physical_damage_+%_vs_ignited_enemies")] = 1 },
                Get = name => name == "physical_damage_+%_vs_ignited_enemies" ? 1 : attackInputs.Get(name),
            };
            // A target-relative rebuild retains the release's weapon increase instead of losing or duplicating it.
            var frozen = Assert.IsType<SpiderDamageSource>(Game.CaptureSpiderDamage(conn, withTargetTerm, 1));
            Assert.Equal(Game.SupportedHitFrom(attackInputs, false).MinTotal,
                Game.SpiderDamageProfile(frozen, 0, false).Hit.MinTotal, 8);
            var frozenPoison = Game.SupportedPoison(conn, attack.Id, false, low, capturedInputs: attackInputs);
            conn.PlayerLife = conn.PlayerMaxLife;
            high = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, high.GetValueOrDefault(Key("claw_damage_+%")));
            Assert.Equal(0, Game.SupportedHitFor(conn, spell.Id, suppress: false).MinTotal);
            Assert.Equal(coefficient, attackInputs.WeaponIncreased);
            Assert.Equal(frozenPoison, Game.SupportedPoison(conn, attack.Id, false, low, capturedInputs: attackInputs));
            var countedLow = wear("Coward's Legacy", PlayerInventories.Belt);
            conn.PlayerLife = conn.PlayerMaxLife;
            var counted = Game.CharacterPlayerStats(conn);
            Assert.Equal(coefficient, counted.GetValueOrDefault(Key("claw_damage_+%")));
            Assert.Equal(0, counted.GetValueOrDefault(Key("base_deal_no_damage")));
            Assert.True(Game.AttackHit(weapon, counted, true).MinTotal > 0);
            inventory.Remove(PlayerInventories.Belt, countedLow.Id);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            Assert.Equal(1, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("base_deal_no_damage")));
            conn.PlayerLife = Math.Max(1, conn.PlayerMaxLife / 2);
            Assert.Equal(coefficient, Game.SupportedHitInputsFor(conn, attack.Id)!.WeaponIncreased);
            inventory.Remove(PlayerInventories.Weapon1, source.Id);
            Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("claw_damage_+%")));
            Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("base_deal_no_damage")));
            var equipped = inventory.Add(PlayerInventories.Weapon1, 0, 0, source.Item);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = Math.Max(1, conn.PlayerMaxLife / 2);
            Assert.Equal(coefficient, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("claw_damage_+%")));
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, equipped.Id));
            Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("claw_damage_+%")));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = Math.Max(1, conn.PlayerMaxLife / 2);
            Assert.Equal(coefficient, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("claw_damage_+%")));
        }
        finally
        {
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.PlayerStats.Clear(); foreach (var p in overrides) conn.PlayerStats[p.Key] = p.Value;
            conn.Experience = CharacterStats.ExperienceFor(100); conn.PlayerLife = Math.Max(1, lifeBefore);
            conn.Pools = Game.PoolsFor(conn, 100); Game.CharacterPlayerStats(conn);
        }
    }
}
