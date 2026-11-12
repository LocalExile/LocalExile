using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_physical_weapon_attribute_damage_reaches_hits_and_ailments(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        void Same(DamagePacket a, DamagePacket b) => Assert.Equal(a.Components, b.Components);
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8)
        {
            ItemClass = "Staff", LocalDamage = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100)),
        };
        var values = new Dictionary<uint, int>
        {
            [Key(WeaponAttributeDamage.PerTenStrength)] = 16, [Key("strength")] = 121,
            [Key("damage_+%")] = 20, [Key("physical_damage_+%")] = 50,
            [Key("global_minimum_added_physical_damage")] = 50, [Key("global_maximum_added_physical_damage")] = 50,
            [Key("global_minimum_added_fire_damage")] = 30, [Key("global_maximum_added_fire_damage")] = 30,
            [Key("physical_damage_%_to_convert_to_fire")] = 50, [Key("main_hand_weapon_type")] = WeaponTypeStats.Of("Staff"),
        };
        int Get(string name) => Program.Stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;
        var conversion = DamageStats.Conversion(values, Program.Stats, attack: true);
        var hit = Game.AttackHitConverted(weapon, values, true, conversion);
        Assert.Equal(271.5, hit[DamageType.Physical].Min, 8); Assert.Equal(427.5, hit[DamageType.Fire].Min, 8);
        var gemHit = AttackSkillHit.Resolve(weapon, values, [], Program.Stats, 10000, melee: true);
        Same(hit, gemHit);
        var ranged = OrdinaryRangedShot.Capture(weapon with { Melee = false }, values, [], Program.Stats, 100, 0, 150);
        Same(hit, ranged.Hit);
        Assert.Equal(100, weapon.LocalDamage![DamageType.Physical].Min, 8);
        Assert.Equal(150, SupportedAttackDamage.Raw(weapon, Get)[DamageType.Physical].Min, 8);
        var raw = SupportedAttackDamage.Raw(weapon, Get);
        var ailments = AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true), raw, conversion, Get);
        int Row(string ailment) => ailments.Single(r => r.Stat == $"minimum_{ailment}_damage_to_deal_per_minute_from_non_critical_main_hand_damage").Value;
        Assert.Equal(11403, Row("bleeding")); Assert.Equal(4887, Row("poison")); Assert.Equal(23085, Row("ignite"));
        var spell = SpellDamage.Packet(raw, Get);
        var unarmed = Game.AttackHitConverted(weapon with { ItemClass = null }, values, true, conversion);
        values[Key(WeaponAttributeDamage.PerTenStrength)] = 0;
        Same(spell, SpellDamage.Packet(raw, Get)); Same(unarmed, Game.AttackHitConverted(weapon, values, true, conversion));
        Assert.Equal(50, values[Key("physical_damage_+%")]);
        foreach (var (strength, expected) in new[] { (9, 0), (10, 16), (19, 16), (20, 32) })
        {
            int Read(string name) => name == WeaponAttributeDamage.PerTenStrength ? 16 : name == "strength" ? strength : 0;
            Assert.Equal(expected, WeaponAttributeDamage.Read(true, Read)("physical_damage_+%"));
            Assert.Equal(0, WeaponAttributeDamage.Read(false, Read)("physical_damage_+%"));
        }

        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        try
        {
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            var amulet = wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            var full = wear("Pillar of the Caged God", PlayerInventories.Weapon1);
            Item Gem(string name) => ItemCatalog.Create(Program.Items.Find(name)!);
            var socketed = inventory.Change(PlayerInventories.Weapon1, full.Id, item => item with
            {
                Sockets = [new(SocketColour.White, Gem("Double Strike")), new(SocketColour.White, Gem("Fireball"))], Links = [1, 1],
            })!;
            conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.IsInactive(conn, full.Id));
            var stats = Game.CharacterPlayerStats(conn); Assert.Equal(16, stats[Key(WeaponAttributeDamage.PerTenStrength)]);
            var actualWeapon = Game.WeaponFor(conn); Assert.Equal("Warstaff", actualWeapon.ItemClass);
            ushort Skill(string effect) => Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == effect).Id;
            ushort attack = Skill("DoubleStrike"), fireball = Skill("Fireball");
            var captured = Game.SupportedHitInputsFor(conn, attack)!;
            var beforeHit = Game.SupportedHitFrom(captured, false);
            var beforeSpell = Game.SupportedHit(conn, fireball);
            var beforeAilments = Game.SupportedAilmentRows(conn, attack, "main_hand");
            var beforeDefault = Game.AttackHit(actualWeapon, stats, true);
            var source = socketed.Item;
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, full.Id, item => item with
            {
                Mods = [.. item.Mods.Where(m => Program.Items.Mods.ByHash(m.Hash)?.Stats.All(s => s.Id != WeaponAttributeDamage.PerTenStrength) != false)],
            }));
            var control = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, control.GetValueOrDefault(Key(WeaponAttributeDamage.PerTenStrength)));
            Assert.Equal(stats[Key("strength")], control[Key("strength")]);
            Same(actualWeapon.LocalDamage!, Game.WeaponFor(conn).LocalDamage!);
            Same(beforeSpell, Game.SupportedHit(conn, fireball));
            Assert.True(beforeDefault.MinTotal > Game.AttackHit(Game.WeaponFor(conn), control, true).MinTotal);
            Assert.True(beforeHit.MinTotal > Game.SupportedHit(conn, attack).MinTotal);
            var controlAilments = Game.SupportedAilmentRows(conn, attack, "main_hand");
            string bleed = "minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage";
            Assert.True(beforeAilments.Single(r => r.Stat == bleed).Value > controlAilments.Single(r => r.Stat == bleed).Value);
            Same(beforeHit, Game.SupportedHitFrom(captured, false)); // Captured source inputs do not follow a later change.
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, full.Id, _ => source));
            Same(beforeHit, Game.SupportedHit(conn, attack));
            inventory.Remove(5, amulet.Id); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.IsInactive(conn, full.Id));
            Assert.True(Game.CharacterPlayerStats(conn)[Key("strength")] < stats[Key("strength")]);
            Assert.True(Game.SupportedHit(conn, attack).MinTotal < beforeHit.MinTotal);
            inventory.Remove(PlayerInventories.Weapon1, full.Id);
            Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key(WeaponAttributeDamage.PerTenStrength)));
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
        }
    }
}
