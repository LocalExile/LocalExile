using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WeaponLocalGatewayTests : IDisposable
{
    readonly string name = "WeaponLocal" + Guid.NewGuid().ToString("N");
    readonly ClientState conn;
    PlayerInventories Inventory => PlayerInventories.For(name, Program.Items);
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;
    static ItemMod Mod(params (string Stat, int Value)[] values) => ItemWeaponStatsTests.Mod(values);
    static T Invoke<T>(string method, params object?[] args) => (T)typeof(Program)
        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    SkillCatalog.Unarmed Weapon() => Invoke<SkillCatalog.Unarmed>("WeaponFor", conn);
    Dictionary<uint, int> Published() => Invoke<List<(uint, int)>>("PlayerStatsWithCleared", conn)
        .ToDictionary(s => s.Item1, s => s.Item2);
    DamagePacket SupportedHit() => Invoke<DamagePacket>("SupportedHit", conn, Program.GemSkillIdPool.Value[0]);

    public WeaponLocalGatewayTests()
    {
        TestSaves.Ensure();
        conn = new(new(0, "WeaponLocal", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = SkillCatalog.WitchRow, PlayerLife = 100 };
        conn.Clock.Stop();
        Recompute();
    }

    void Recompute() => conn.Pools = Program.PoolsFor(conn, 100);
    static Item Item(string type, byte quality = 0, params ItemMod[] mods) =>
        ItemCatalog.Create(Program.Items.Find(type)!) with { Quality = quality, Mods = mods };
    InventoryItem Equip(string type = "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", byte quality = 0,
        params ItemMod[] mods) => Inventory.Add(PlayerInventories.Weapon1, 0, 0, Item(type, quality, mods));
    void Socket(string effect, bool brutality = false)
    {
        var gem = ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/SkillGem{effect}")!);
        var body = Item("Metadata/Items/Armours/BodyArmours/BodyStr1") with { Sockets = [new(SocketColour.White, gem)] };
        if (brutality)
            body = body with { Links = [2], Sockets = [new(SocketColour.White, gem), new(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Brutality Support")!))] };
        // Qualify the real attack/support gems through a native global attribute source, rather than bypassing admission.
        var attributes = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == "additional_all_attributes" && !s.Local));
        Inventory.Add(6, 0, 0, Item("Iron Ring") with { Mods = [new(ModSection.Explicit, attributes.Hash,
            attributes.Stats.Select(s => s.Id == "additional_all_attributes" ? 100 : 0).ToArray())] });
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, body);
        Recompute();
    }

    [Fact]
    public void Real_equipped_weapon_packet_reaches_ordinary_attacks_with_the_actual_attack_modifier_scope()
    {
        Equip(quality: 20, mods:
        [
            Mod(("local_minimum_added_physical_damage", 15), ("local_maximum_added_physical_damage", 30)),
            Mod(("local_physical_damage_+%", 50)),
            Mod(("local_minimum_added_fire_damage", 2), ("local_maximum_added_fire_damage", 4)),
            Mod(("local_minimum_added_chaos_damage", 3), ("local_maximum_added_chaos_damage", 5)),
        ]);
        Recompute();
        var weapon = Weapon();
        var stats = new Dictionary<uint, int>
        {
            [Key("damage_+%")] = 25, [Key("attack_damage_+%")] = 50,
            [Key("physical_damage_+%")] = 100, [Key("melee_damage_+%")] = 20,
            [Key("melee_physical_damage_+%")] = 30, [Key("elemental_damage_+%")] = 40,
            [Key("fire_damage_+%")] = 10, [Key("projectile_damage_+%")] = 25,
        };
        Assert.Equal(new DamageRange(36, 70), weapon.LocalDamage![DamageType.Physical]);
        var hit = Program.AttackHit(weapon, stats, melee: false);
        // A wand is projectile, so the 25% projectile input applies and the two melee inputs stay out.
        Assert.Equal(new DamageRange(108, 210), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(5, 10), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(6, 10), hit[DamageType.Chaos]);
        var meleeHit = Program.AttackHit(weapon, stats, melee: true);
        Assert.Equal(new DamageRange(117, 227.5), meleeHit[DamageType.Physical]);
        Assert.Equal(new DamageRange(4.9, 9.8), meleeHit[DamageType.Fire]);
        Assert.Equal(new DamageRange(5.85, 9.75), meleeHit[DamageType.Chaos]);
        Assert.DoesNotContain(Key("local_physical_damage_+%"), Program.CharacterPlayerStats(conn).Keys);
    }

    [Theory]
    [InlineData("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")]
    [InlineData("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")]
    public void Ordinary_wand_and_bow_release_freeze_local_typed_damage_before_later_item_changes(string type)
    {
        var equipped = Equip(type, 20,
            Mod(("local_minimum_added_lightning_damage", 4), ("local_maximum_added_lightning_damage", 8)));
        Recompute();
        var stats = new Dictionary<uint, int>();
        var shot = OrdinaryRangedShot.Capture(Weapon(), stats, [], Program.Stats, 100, 0, 150);
        Inventory.Change(PlayerInventories.Weapon1, equipped.Id, item => item with { Mods = [] });
        var next = OrdinaryRangedShot.Capture(Weapon(), stats, [], Program.Stats, 100, 0, 150);

        Assert.Equal(new DamageRange(4, 8), shot.Hit[DamageType.Lightning]);
        Assert.Equal(DamageRange.Zero, next.Hit[DamageType.Lightning]);
        Assert.Equal(new DamageRange(4, 8), shot.Hit[DamageType.Lightning]);
        Assert.Equal(shot.Hit[DamageType.Physical], next.Hit[DamageType.Physical]);
    }

    [Theory]
    [InlineData("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", 18.75)]
    [InlineData("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1", 23.75)]
    public void Actual_ordinary_ranged_capture_uses_projectile_increases_for_local_and_added_ranges(
        string type, double maximumPhysical)
    {
        Equip(type, 0, Mod(("local_minimum_added_fire_damage", 2), ("local_maximum_added_fire_damage", 4)));
        Recompute();
        var stats = new Dictionary<uint, int>
        {
            [Key("projectile_damage_+%")] = 25,
            [Key("melee_damage_+%")] = 1000, [Key("melee_physical_damage_+%")] = 1000,
            [Key("attack_minimum_added_physical_damage")] = 4,
            [Key("attack_maximum_added_physical_damage")] = 6,
            [Key("attack_minimum_added_fire_damage")] = 3,
            [Key("attack_maximum_added_fire_damage")] = 5,
        };
        var shot = OrdinaryRangedShot.Capture(Weapon(), stats, [], Program.Stats, 100, 0, 150);

        Assert.Equal(new DamageRange(11.25, maximumPhysical), shot.Hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(6.25, 11.25), shot.Hit[DamageType.Fire]);
        var panel = Program.AttackHit(Weapon(), stats, melee: false);
        foreach (var damageType in DamageTypes.ConversionOrder)
            Assert.Equal(panel[damageType], shot.Hit[damageType]);
    }

    [Theory]
    [InlineData("BurningArrow", "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1", true)]
    [InlineData("Cyclone", "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1", true)]
    [InlineData("Fireball", "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", false)]
    public void Actual_socketed_attack_skills_consume_typed_weapon_damage_and_spells_stay_independent(
        string effect, string type, bool attack)
    {
        var equipped = Equip(type, 20,
            Mod(("local_minimum_added_chaos_damage", 40), ("local_maximum_added_chaos_damage", 60)));
        Socket(effect);
        var before = SupportedHit();
        Inventory.Change(PlayerInventories.Weapon1, equipped.Id, item => item with { Mods =
            [Mod(("local_minimum_added_chaos_damage", 80), ("local_maximum_added_chaos_damage", 120))] });
        Recompute();
        var after = SupportedHit();

        if (attack)
        {
            Assert.True(before[DamageType.Chaos].Min > 0);
            Assert.Equal(before[DamageType.Chaos].Min * 2, after[DamageType.Chaos].Min, 6);
            Assert.Equal(before[DamageType.Chaos].Max * 2, after[DamageType.Chaos].Max, 6);
        }
        foreach (var damageType in DamageTypes.ConversionOrder)
            if (!attack || damageType != DamageType.Chaos)
                Assert.Equal(before[damageType], after[damageType]);
        Assert.True(after.MinTotal > 0);
    }

    [Fact]
    public void Local_no_physical_weapon_does_not_suppress_character_added_physical_damage()
    {
        Equip(mods: [Mod(("local_weapon_no_physical_damage", 1))]);
        Recompute();
        var stats = new Dictionary<uint, int>
        {
            [Key("attack_minimum_added_physical_damage")] = 3,
            [Key("attack_maximum_added_physical_damage")] = 5,
        };
        Assert.Equal(DamageRange.Zero, Weapon().LocalDamage![DamageType.Physical]);
        Assert.Equal(new DamageRange(3, 5), Program.AttackHit(Weapon(), stats, melee: false)[DamageType.Physical]);
    }

    [Theory]
    [InlineData("Cyclone", "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1", false)]
    [InlineData("BurningArrow", "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1", true)]
    public void Actual_granted_attack_replaces_every_range_with_the_final_Brutality_packet(
        string effect, string type, bool zeroHit)
    {
        Equip(type, 20,
            Mod(("local_minimum_added_fire_damage", 20), ("local_maximum_added_fire_damage", 40)));
        Socket(effect, brutality: true);
        ushort id = Program.GemSkillIdPool.Value[0];
        var skill = Invoke<IReadOnlyList<GrantedSkill>>("PlayerSkills", conn).Single(s => s.Id == id);
        var stats = skill.Stats!.ToDictionary(s => s.Key, s => s.Value);
        var hit = SupportedHit();

        Assert.Single(skill.Supports);
        Assert.Equal(zeroHit, hit.IsZero);
        foreach (string slug in new[] { "total", "physical", "fire", "cold", "lightning", "chaos" })
        {
            var range = slug == "total" ? new DamageRange(hit.MinTotal, hit.MaxTotal)
                : hit[Enum.Parse<DamageType>(slug, ignoreCase: true)];
            Assert.Equal((int)Math.Floor(range.Min), stats[Key($"main_hand_minimum_{slug}_damage")]);
            Assert.Equal((int)Math.Floor(range.Max), stats[Key($"main_hand_maximum_{slug}_damage")]);
        }
        Assert.Equal(0, stats[Key("main_hand_minimum_fire_damage")]);
        Assert.Equal(0, stats[Key("main_hand_maximum_fire_damage")]);
        if (zeroHit)
        {
            Assert.Equal(0, stats[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
            Assert.Equal(0, stats[StatKeys.HundredTimesDamagePerSecond]);
        }
    }

    [Fact]
    public void Local_speed_is_applied_once_to_the_item_duration_and_stays_out_of_character_speed()
    {
        var equipped = Equip();
        Recompute();
        var before = Program.CharacterPlayerStats(conn);
        Assert.Equal(0, conn.Pools.AttackSpeedPct);
        Assert.Equal(667, Weapon().AttackMs);
        Inventory.Change(PlayerInventories.Weapon1, equipped.Id, item => item with
            { Mods = [Mod(("local_attack_speed_+%", 20))] });
        Recompute();
        var after = Program.CharacterPlayerStats(conn);

        Assert.Equal(0, conn.Pools.AttackSpeedPct);
        Assert.Equal(before[StatKeys.MainHandAttackSpeedPct], after[StatKeys.MainHandAttackSpeedPct]);
        Assert.Equal(556, Weapon().AttackMs);
        Assert.Equal(.556, Weapon().AttackSeconds(after), 6);
        Assert.Equal(556, Published()[StatKeys.MainHandAttackDurationMs]);
    }

    [Fact]
    public void Dual_wield_hands_keep_independent_local_durations_and_the_same_character_speed_factor()
    {
        Equip(mods: [Mod(("local_attack_speed_+%", 20))]);
        Inventory.Add(PlayerInventories.Offhand1, 0, 0,
            Item("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", 0, Mod(("local_attack_speed_+%", 50))));
        Recompute();
        var stats = Published();

        Assert.Equal(10, conn.Pools.AttackSpeedPct); // The inherent dual-wield more applies once, separate from both items.
        Assert.Equal(556, stats[StatKeys.MainHandAttackDurationMs]);
        Assert.Equal(445, stats[StatKeys.OffHandAttackDurationMs]);
        Assert.Equal(10, stats[StatKeys.MainHandAttackSpeedPct]);
        Assert.Equal(10, stats[StatKeys.OffHandAttackSpeedPct]);
    }

    [Fact]
    public void Socketed_attack_timing_inherits_local_speed_once_without_rescaling_skill_increases()
    {
        var equipped = Equip("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1");
        Socket("BurningArrow");
        double before = Invoke<double>("SupportedAttackSeconds", conn, Program.GemSkillIdPool.Value[0]);
        int beforeMs = Weapon().AttackMs;
        Inventory.Change(PlayerInventories.Weapon1, equipped.Id, item => item with
            { Mods = [Mod(("local_attack_speed_+%", 20))] });
        Recompute();
        double after = Invoke<double>("SupportedAttackSeconds", conn, Program.GemSkillIdPool.Value[0]);

        Assert.Equal(0, conn.Pools.AttackSpeedPct);
        Assert.Equal(before * Weapon().AttackMs / beforeMs, after, 6);
        Assert.True(after < before);
    }

    [Theory]
    [InlineData(PlayerInventories.Weapon2, "virtual_total_main_hand_chance_to_bleed_on_hit_%")]
    [InlineData(PlayerInventories.Offhand2, "virtual_total_off_hand_chance_to_bleed_on_hit_%")]
    public void Inactive_set_local_sources_stay_absent_until_swapped_in_and_clear_when_removed(byte row, string stat)
    {
        var equipped = Inventory.Add(row, 0, 0,
            Item("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", 0, Mod(("local_chance_to_bleed_on_hit_%", 50))));
        Recompute();
        uint key = Key(stat);
        Assert.Equal(0, Published().GetValueOrDefault(key));
        Inventory.SwapWeaponSets();
        Recompute();
        Assert.Equal(50, Published()[key]);
        byte active = row == PlayerInventories.Weapon2 ? PlayerInventories.Weapon1 : PlayerInventories.Offhand1;
        Assert.NotNull(Inventory.Remove(active, equipped.Id));
        Recompute();
        Assert.Equal(0, Published().GetValueOrDefault(key));
    }

    [Fact]
    public void Requirement_disabled_main_hand_uses_unarmed_damage_and_no_local_panel_sources()
    {
        var type = Program.Items.Find("Prophecy Wand")!;
        Assert.True(Program.Items.Requirements(type).Int > SkillCatalog.ClassInfo.Witch.Intelligence);
        var equipped = Equip(type.Id, 20, Mod(("local_chance_to_bleed_on_hit_%", 50)),
            Mod(("local_minimum_added_chaos_damage", 40), ("local_maximum_added_chaos_damage", 60)));
        Recompute();

        Assert.Contains(equipped.Id, Program.InactiveItems[name]);
        Assert.Null(Weapon().LocalDamage);
        Assert.Equal(Program.Skills.UnarmedFor(conn.ClassRow), Weapon());
        Assert.Equal(0, Published().GetValueOrDefault(Key("virtual_total_main_hand_chance_to_bleed_on_hit_%")));
    }

    void StoreInactiveDualWands()
    {
        Inventory.Add(PlayerInventories.Weapon2, 0, 0,
            Item("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", 0, Mod(("local_attack_speed_+%", 50))));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Item("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1"));
    }

    [Fact]
    public void An_inactive_dual_pair_grants_neither_inherent_speed_nor_block_to_the_active_single_wand()
    {
        Equip();
        StoreInactiveDualWands();
        Recompute();

        Assert.False(Program.WeaponProfileFor(conn).DualWield);
        Assert.Equal(0, conn.Pools.AttackSpeedPct);
        Assert.Equal(0, conn.Pools.BlockChanceUncapped);
        Assert.Equal(150, Published()[StatKeys.HundredTimesAttacksPerSecond]);

        Inventory.SwapWeaponSets();
        Recompute();
        Assert.True(Program.WeaponProfileFor(conn).DualWield);
        Assert.Equal(10, conn.Pools.AttackSpeedPct);
        Assert.Equal(20, conn.Pools.BlockChanceUncapped);
        Assert.Equal(445, Published()[StatKeys.MainHandAttackDurationMs]);

        Inventory.SwapWeaponSets();
        Recompute();
        Assert.Equal(0, conn.Pools.AttackSpeedPct);
        Assert.Equal(0, conn.Pools.BlockChanceUncapped);
    }

    [Fact]
    public void The_real_dual_wield_speed_passive_applies_only_after_its_weapon_pair_becomes_active()
    {
        var node = Assert.IsType<PassiveTree.Node>(new PassiveTree().Find(15331));
        Assert.False(node.IsAscendancy);
        Assert.Contains((Key("attack_speed_while_dual_wielding_+%"), 4), node.Stats);
        conn.Passives.Add(node.GraphId);
        Equip();
        StoreInactiveDualWands();
        Recompute();
        Assert.Equal(0, conn.Pools.AttackSpeedPct);

        Inventory.SwapWeaponSets();
        Recompute();
        Assert.Equal(14, conn.Pools.AttackSpeedPct); // Native4% increased times inherent10% more, rounded to14%.
        Inventory.SwapWeaponSets();
        Recompute();
        Assert.Equal(0, conn.Pools.AttackSpeedPct);
    }

    [Fact]
    public void A_real_bow_speed_passive_cannot_read_a_bow_stored_behind_the_active_wand()
    {
        var node = Assert.IsType<PassiveTree.Node>(new PassiveTree().Find(2185));
        Assert.Contains((Key("bow_attack_speed_+%"), 5), node.Stats);
        conn.Passives.Add(node.GraphId);
        Equip();
        Inventory.Add(PlayerInventories.Weapon2, 0, 0, Item("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1"));
        Recompute();
        Assert.False(Program.WeaponProfileFor(conn).Bow);
        Assert.Equal(0, conn.Pools.AttackSpeedPct);

        Inventory.SwapWeaponSets();
        Recompute();
        Assert.True(Program.WeaponProfileFor(conn).Bow);
        Assert.Equal(5, conn.Pools.AttackSpeedPct);
    }

    [Fact]
    public void The_real_shield_spell_block_passive_and_base_block_wait_for_the_active_shield()
    {
        var node = Assert.IsType<PassiveTree.Node>(new PassiveTree().Find(62214));
        Assert.Contains((Key("shield_spell_block_%"), 3), node.Stats);
        conn.Passives.Add(node.GraphId);
        Equip();
        Inventory.Add(PlayerInventories.Weapon2, 0, 0, Item("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1"));
        var shield = Program.Items.Find("Twig Spirit Shield")!;
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Item(shield.Id));
        Recompute();
        Assert.False(Program.WeaponProfileFor(conn).Shield);
        Assert.Equal(0, conn.Pools.BlockChanceBase);
        Assert.Equal(0, conn.Pools.SpellBlockChance);

        Inventory.SwapWeaponSets();
        Recompute();
        Assert.True(Program.WeaponProfileFor(conn).Shield);
        Assert.Equal(22, Program.Items.ShieldBlock(shield));
        Assert.Equal(22, conn.Pools.BlockChanceBase);
        Assert.Equal(3, conn.Pools.SpellBlockChance);
    }

    [Fact]
    public void Active_shield_base_block_cannot_be_replaced_by_the_larger_inactive_shield()
    {
        Equip();
        var active = Program.Items.Find("Twig Spirit Shield")!;
        var inactive = Program.Items.Find("Splintered Tower Shield")!;
        Assert.Equal(22, Program.Items.ShieldBlock(active));
        Assert.Equal(24, Program.Items.ShieldBlock(inactive));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Item(active.Id));
        Inventory.Add(PlayerInventories.Weapon2, 0, 0, Item("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1"));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Item(inactive.Id));
        Recompute();
        Assert.Equal(22, conn.Pools.BlockChanceBase);
        Assert.Equal(22, Invoke<int>("ShieldBaseBlock", conn));

        Inventory.SwapWeaponSets();
        Recompute();
        Assert.Equal(24, conn.Pools.BlockChanceBase);
        Assert.Equal(24, Invoke<int>("ShieldBaseBlock", conn));
    }

    [Fact]
    public void Requirement_disabled_active_shield_supplies_no_base_block()
    {
        Equip();
        var type = Program.Items.Find("Pinnacle Tower Shield")!;
        Assert.True(Program.Items.Requirements(type).Str > SkillCatalog.ClassInfo.Witch.Strength);
        var shield = Inventory.Add(PlayerInventories.Offhand1, 0, 0, Item(type.Id));
        Recompute();

        Assert.Contains(shield.Id, Program.InactiveItems[name]);
        Assert.False(Program.WeaponProfileFor(conn).Shield);
        Assert.Equal(0, conn.Pools.BlockChanceBase);
        Assert.Equal(0, Invoke<int>("ShieldBaseBlock", conn));
    }

    [Fact]
    public void Actual_Faster_Attacks_timing_cannot_gain_a_dual_wield_factor_from_the_inactive_set()
    {
        Equip("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1");
        Socket("BurningArrow");
        var body = Inventory.Items(PlayerInventories.BodyArmour).Single();
        Inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        {
            Links = [2], Sockets = [item.Sockets[0], new(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Faster Attacks Support")!))],
        });
        Recompute();
        int effect = Program.Skills.EffectRow("BurningArrow");
        int faster = Program.Skills.EffectRow("SupportFasterAttacks");
        Assert.Contains((Key("attack_speed_+%"), 20), Program.Skills.EffectStats(faster, 1));
        double expected = .714 / 1.20 / Program.Supports.AttackSpeedMultiplier(effect, 1);
        double baseline = Invoke<double>("SupportedAttackSeconds", conn, Program.GemSkillIdPool.Value[0]);
        Assert.Equal(expected, baseline, 6);

        StoreInactiveDualWands();
        Recompute();
        Assert.Equal(0, conn.Pools.AttackSpeedPct);
        Assert.Equal(baseline, Invoke<double>("SupportedAttackSeconds", conn, Program.GemSkillIdPool.Value[0]), 6);
    }

    public void Dispose()
    {
        conn.Closed = true;
        PlayerInventories.Forget(name);
        Program.InactiveItems.TryRemove(name, out _);
        Program.GemSkillIdsOf.TryRemove(name, out _);
    }
}
