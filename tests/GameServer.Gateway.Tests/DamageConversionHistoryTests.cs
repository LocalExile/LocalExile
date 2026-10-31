using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class DamageConversionHistoryTests
{
    static Dictionary<string, int> Sources(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
    static DamagePacket Packet(params (DamageType Type, double Min, double Max)[] ranges)
    {
        var result = DamagePacket.Empty;
        foreach (var (type, min, max) in ranges) result = result.Add(type, new(min, max));
        return result;
    }
    static void EqualPacket(DamagePacket expected, DamagePacket actual)
    {
        foreach (var type in DamageTypes.ConversionOrder)
        {
            Assert.Equal(expected[type].Min, actual[type].Min, 8);
            Assert.Equal(expected[type].Max, actual[type].Max, 8);
        }
    }
    static Dictionary<uint, int> NativeSources(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(v => Program.Stats.Key(v.Key) ?? throw new InvalidDataException("Missing native stat " + v.Key), v => v.Value);
    sealed record Example(DamagePacket Raw, DamageConversion Conversion, Dictionary<string, int> Sources,
        DamagePacket Expected, bool Spell = false, bool Melee = false, bool Projectile = false,
        IReadOnlyList<(string Name, int Value)>? Supports = null, IReadOnlySet<int>? Types = null);

    // Fixed arithmetic from pinned PoB16de4b82 CalcOffence35-140,1938-1982. Expected values are independent
    // constants; no production calculation or a live local result is used to construct the oracle.
    [Theory]
    [InlineData("C01")]
    [InlineData("C02")]
    [InlineData("C03")]
    [InlineData("C04")]
    [InlineData("C05")]
    [InlineData("C06")]
    [InlineData("C07")]
    [InlineData("C08")]
    [InlineData("C09")]
    [InlineData("C10")]
    [InlineData("C11")]
    [InlineData("C12")]
    [InlineData("C13")]
    [InlineData("C14")]
    [InlineData("C15")]
    public void Fixed_reference_conversion_and_gain_examples(string id)
    {
        var c = ReferenceExample(id);
        var actual = PlayerHitDamage.Resolve(c.Raw, name => c.Sources.GetValueOrDefault(name), c.Conversion,
            spell: c.Spell, melee: c.Melee, projectile: c.Projectile,
            supportModifiers: c.Supports, skillTypes: c.Types);
        EqualPacket(c.Expected, actual);
    }

    static Example ReferenceExample(string id) => id switch
    {
        "C01" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100),
            Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50)), Packet((DamageType.Fire, 200d, 200d))),
        "C02" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 50),
            Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50)), Packet((DamageType.Physical, 75d, 75d), (DamageType.Fire, 100d, 100d))),
        "C03" => new(Packet((DamageType.Physical, 100d, 100d), (DamageType.Fire, 50d, 50d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100),
            Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50)), Packet((DamageType.Fire, 285d, 285d))),
        "C04" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Lightning, 100).AddGlobal(DamageType.Lightning, DamageType.Cold, 100).AddGlobal(DamageType.Cold, DamageType.Fire, 100),
            Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("lightning_damage_+%", 40), ("cold_damage_+%", 50), ("fire_damage_+%", 60), ("elemental_damage_+%", 70)), Packet((DamageType.Fire, 370d, 370d))),
        "C05" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGain(DamageType.Physical, DamageType.Fire, 50),
            Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50)), Packet((DamageType.Physical, 150d, 150d), (DamageType.Fire, 100d, 100d))),
        "C06" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100),
            Sources(("physical_damage_+%", -150), ("fire_damage_+%", 200)), Packet((DamageType.Fire, 150d, 150d))),
        "C07" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100),
            Sources(("damage_+%", 20)), Packet((DamageType.Fire, 120d, 120d))),
        "C08" => new(Packet((DamageType.Lightning, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Lightning, DamageType.Cold, 100).AddGlobal(DamageType.Cold, DamageType.Fire, 100),
            Sources(("elemental_damage_+%", 50)), Packet((DamageType.Fire, 150d, 150d))),
        "C09" => new(Packet((DamageType.Lightning, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Lightning, DamageType.Chaos, 100),
            Sources(), Packet((DamageType.Chaos, 150d, 150d)), Projectile: true, Supports: [("support_attack_skills_elemental_damage_+%_final", 50)], Types: new HashSet<int> { 0, 2 }),
        "C10" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Lightning, 100).AddGlobal(DamageType.Lightning, DamageType.Chaos, 100),
            Sources(), Packet((DamageType.Chaos, 150d, 150d)), Projectile: true, Supports: [("support_attack_skills_elemental_damage_+%_final", 50)], Types: new HashSet<int> { 0, 2 }),
        "C11" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100),
            Sources(), Packet((DamageType.Fire, 149d, 149d)), Melee: true, Supports: [("support_melee_physical_damage_+%_final", 49)], Types: new HashSet<int> { 0, 19 }),
        "C12" => new(Packet((DamageType.Lightning, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Lightning, DamageType.Chaos, 100),
            Sources(), Packet((DamageType.Chaos, 100d, 100d)), Supports: [("deal_no_elemental_damage", 1)]),
        "C13" => new(Packet((DamageType.Fire, 9d, 14d)),
            new DamageConversion().AddGlobal(DamageType.Fire, DamageType.Chaos, 100),
            Sources(("damage_+%", 20), ("spell_damage_+%", 30), ("fire_damage_+%", 50), ("elemental_damage_+%", 40), ("chaos_damage_+%", 80)), Packet((DamageType.Chaos, 28.8d, 44.8d)), Spell: true),
        "C14" => new(Packet((DamageType.Physical, 9d, 23.4d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100),
            Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50)), Packet((DamageType.Fire, 18d, 46.8d))),
        "C15" => new(Packet((DamageType.Physical, 100d, 100d)),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Lightning, 100),
            Sources(("wrath_aura_spell_lightning_damage_+%_final", 20)), Packet((DamageType.Lightning, 120d, 120d)), Spell: true),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    [Fact]
    public void Source_destination_and_intermediate_histories_remain_distinct_until_their_complete_bucket_is_known()
    {
        var raw = Packet((DamageType.Physical, 100, 100), (DamageType.Fire, 50, 50));
        var conversion = new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 50)
            .AddGlobal(DamageType.Physical, DamageType.Lightning, 50)
            .AddGlobal(DamageType.Lightning, DamageType.Fire, 100);
        var source = Sources(("physical_damage_+%", 30), ("lightning_damage_+%", 100), ("fire_damage_+%", 50));
        var result = PlayerHitDamage.Resolve(raw, k => source.GetValueOrDefault(k), conversion, spell: false);
        // Direct Physical->Fire50*1.8=90; via-Lightning50*2.8=140; nativeFire50*1.5=75.
        EqualPacket(Packet((DamageType.Fire, 305, 305)), result);
    }

    [Theory]
    [InlineData(false, 505d)]
    [InlineData(true, 585d)]
    public void Attack_context_and_transferred_spell_increases_enter_one_history_bucket(bool melee, double expected)
    {
        var s = Sources(("damage_+%", 20), ("attack_damage_+%", 30), ("melee_damage_+%", 40),
            ("melee_physical_damage_+%", 50), ("area_damage_+%", 60), ("projectile_damage_+%", 70),
            ("physical_damage_+%", 80), ("fire_damage_+%", 90), ("elemental_damage_+%", 100),
            (DamageStats.SpellDamageIncreasesApplyToAttacks150, 1), ("spell_damage_+%", 10));
        var hit = PlayerHitDamage.Resolve(Packet((DamageType.Physical, 100, 100)), k => s.GetValueOrDefault(k),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100), spell: false,
            melee: melee, area: melee, projectile: !melee);
        EqualPacket(Packet((DamageType.Fire, expected, expected)), hit);
    }

    [Fact]
    public void Half_percent_attack_increase_keeps_the_exact_existing_publication_boundary()
    {
        var s = Sources((DamageStats.SpellDamageIncreasesApplyToAttacks150, 1), ("spell_damage_+%", 1));
        var hit = PlayerHitDamage.Resolve(Packet((DamageType.Physical, 200, 200)), k => s.GetValueOrDefault(k),
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100), spell: false);
        Assert.Equal(203d, hit[DamageType.Fire].Min);
        Assert.Equal(203, PanelStats.HandDamage(hit, "main_hand").Single(v => v.Stat == "main_hand_minimum_fire_damage").Value);
    }

    [Fact]
    public void Each_distinct_more_source_applies_once_even_when_multiple_elemental_types_were_visited()
    {
        var hit = PlayerHitDamage.Resolve(Packet((DamageType.Lightning, 100, 100)), _ => 0,
            new DamageConversion().AddGlobal(DamageType.Lightning, DamageType.Cold, 100)
                .AddGlobal(DamageType.Cold, DamageType.Fire, 100), spell: false,
            supportModifiers: [("support_attack_skills_elemental_damage_+%_final", 10),
                ("support_attack_skills_elemental_damage_+%_final", 20)]);
        EqualPacket(Packet((DamageType.Fire, 132, 132)), hit); //1.1*1.2, not1.3 or repeated per elemental hop.
    }

    [Theory]
    [InlineData("deal_no_elemental_damage", 0d, 100d)]
    [InlineData("base_deal_no_chaos_damage", 100d, 0d)]
    [InlineData("base_deal_no_damage", 0d, 0d)]
    public void Global_and_support_prohibitions_test_the_destination_and_clear_without_stale_zeroes(
        string flag, double fire, double chaos)
    {
        var raw = Packet((DamageType.Physical, 100, 100), (DamageType.Lightning, 100, 100));
        var conversion = new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100)
            .AddGlobal(DamageType.Lightning, DamageType.Chaos, 100);
        var s = Sources((flag, 1));
        var expected = Packet((DamageType.Fire, fire, fire), (DamageType.Chaos, chaos, chaos));
        EqualPacket(expected, PlayerHitDamage.Resolve(raw, k => s.GetValueOrDefault(k), conversion, spell: false));
        s[flag] = 0;
        EqualPacket(Packet((DamageType.Fire, 100, 100), (DamageType.Chaos, 100, 100)),
            PlayerHitDamage.Resolve(raw, k => s.GetValueOrDefault(k), conversion, spell: false));
        s.Remove(flag);
        var supports = new List<(string Name, int Value)> { (flag, 1) };
        EqualPacket(expected, PlayerHitDamage.Resolve(raw, _ => 0, conversion, spell: false, supportModifiers: supports));
        supports.Clear();
        EqualPacket(Packet((DamageType.Fire, 100, 100), (DamageType.Chaos, 100, 100)),
            PlayerHitDamage.Resolve(raw, _ => 0, conversion, spell: false, supportModifiers: supports));
    }

    [Fact]
    public void A_contradictory_native_family_set_cannot_enable_attack_or_melee_more_on_a_spell()
    {
        var hit = PlayerHitDamage.Resolve(Packet((DamageType.Physical, 100, 100)), _ => 0,
            new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100), spell: true, melee: true,
            supportModifiers: [("support_melee_physical_damage_+%_final", 100),
                ("support_attack_skills_elemental_damage_+%_final", 100)], skillTypes: new HashSet<int> { 0, 19 });
        EqualPacket(Packet((DamageType.Fire, 100, 100)), hit);
    }

    [Fact]
    public void A_contradictory_spell_type_cannot_enable_controlled_destruction_on_an_attack()
    {
        var hit = PlayerHitDamage.Resolve(Packet((DamageType.Physical, 100, 100)), _ => 0, new(), spell: false,
            supportModifiers: [("support_controlled_destruction_spell_damage_+%_final", 100)],
            skillTypes: new HashSet<int> { 1 });
        EqualPacket(Packet((DamageType.Physical, 100, 100)), hit);
    }

    [Fact]
    public void Native_area_and_projectile_support_types_do_not_force_the_same_increased_damage_context()
    {
        var s = Sources(("area_damage_+%", 100), ("projectile_damage_+%", 100));
        var hit = PlayerHitDamage.Resolve(Packet((DamageType.Fire, 100, 100)), k => s.GetValueOrDefault(k), new(),
            spell: false, area: false, projectile: false,
            supportModifiers: [("support_area_concentrate_area_damage_+%_final", 50),
                ("support_slower_projectiles_damage_+%_final", 25)], skillTypes: new HashSet<int> { 0, 2, 7 });
        EqualPacket(Packet((DamageType.Fire, 187.5, 187.5)), hit);
    }

    [Fact]
    public void Spell_aura_charge_and_generic_support_grouping_keeps_117_for_both_actor_ranges_and_roll()
    {
        var s = Sources(("spell_damage_+%", 50), ("spell_damage_aura_spell_damage_+%_final", 20),
            ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("current_frenzy_charges", 1));
        var raw = Packet((DamageType.Fire, 50, 50));
        var hit = PlayerHitDamage.Resolve(raw, k => s.GetValueOrDefault(k), new(), spell: true,
            supportModifiers: [("support_controlled_destruction_spell_damage_+%_final", 25)]);
        Assert.Equal(117d, hit[DamageType.Fire].Min);
        var panel = SpellPanelStats.Compute(new(SkillHitKind.Spell, raw), hit, 1).ToDictionary(v => v.Stat, v => v.Value);
        Assert.Equal(117, panel["spell_minimum_fire_damage"]);
        Assert.Equal(117, panel["spell_maximum_total_damage"]);
        Assert.Equal(117, hit.RollTotal(new Random(75)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordered_generic_then_elemental_support_products_keep_27_to_45_before_and_after_conversion(bool toChaos)
    {
        var raw = Packet((DamageType.Fire, 15, 25));
        var conversion = new DamageConversion();
        if (toChaos) conversion.AddGlobal(DamageType.Fire, DamageType.Chaos, 100);
        var hit = PlayerHitDamage.Resolve(raw, _ => 0, conversion, spell: true,
            supportModifiers: [("support_controlled_destruction_spell_damage_+%_final", 25),
                ("support_gem_elemental_damage_+%_final", 44)]);
        var type = toChaos ? DamageType.Chaos : DamageType.Fire;
        Assert.Equal(27d, hit[type].Min);
        Assert.Equal(45d, hit[type].Max);
        var panel = SpellPanelStats.Compute(new(SkillHitKind.Spell, raw), hit, 1).ToDictionary(v => v.Stat, v => v.Value);
        Assert.Equal(27, panel[$"spell_minimum_{type.ToString().ToLowerInvariant()}_damage"]);
        Assert.Equal(45, panel["spell_maximum_total_damage"]);
    }

    [Theory]
    [InlineData(0, 200d)]
    [InlineData(1, 208d)]
    [InlineData(3, 224d)]
    public void Ordinary_melee_ranged_and_attack_gem_paths_apply_charge_more_once(int held, double expected)
    {
        var names = Sources(("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50),
            ("physical_damage_%_to_convert_to_fire", 100),
            ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("current_frenzy_charges", held));
        var stats = NativeSources(names);
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var conversion = DamageStats.Conversion(stats, Program.Stats, attack: true);
        var wanted = Packet((DamageType.Fire, expected, expected));
        EqualPacket(wanted, Program.AttackHitConverted(weapon, stats, melee: true, conversion));
        EqualPacket(wanted, AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, 10000, melee: false, projectile: true));
        EqualPacket(wanted, OrdinaryRangedShot.Capture(weapon, stats, [], Program.Stats, 100, 0, 150).Hit);
        EqualPacket(wanted, SpellHit.Resolve(new(SkillHitKind.Spell, Packet((DamageType.Physical, 100, 100))),
            Program.Stats, stats, new Dictionary<uint, int>(), conversion));
    }

    [Fact]
    public void A_released_converted_ranged_packet_retains_its_charges_and_modifiers_when_sources_change()
    {
        var source = NativeSources(Sources(("physical_damage_%_to_convert_to_fire", 100), ("fire_damage_+%", 100),
            ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("current_frenzy_charges", 3)));
        var shot = OrdinaryRangedShot.Capture(new(1000, 100, 100, 8), source, [], Program.Stats, 100, 0, 150);
        source[Program.Stats.Key("current_frenzy_charges")!.Value] = 0;
        source[Program.Stats.Key("fire_damage_+%")!.Value] = 0;
        EqualPacket(Packet((DamageType.Fire, 224, 224)), shot.Hit);
        var fresh = OrdinaryRangedShot.Capture(new(1000, 100, 100, 8), source, [], Program.Stats, 100, 0, 150);
        EqualPacket(Packet((DamageType.Fire, 100, 100)), fresh.Hit);
    }

    [Fact]
    public void Spell_only_power_charge_more_does_not_leak_into_attacks()
    {
        var source = Sources(("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("current_frenzy_charges", 3),
            ("object_inherent_spell_damage_+%_final_per_power_charge", 5), ("current_power_charges", 2));
        var raw = Packet((DamageType.Physical, 100, 100));
        EqualPacket(Packet((DamageType.Physical, 112, 112)),
            PlayerHitDamage.Resolve(raw, k => source.GetValueOrDefault(k), new(), spell: false));
        EqualPacket(Packet((DamageType.Physical, 123.2, 123.2)),
            PlayerHitDamage.Resolve(raw, k => source.GetValueOrDefault(k), new(), spell: true));
    }

    [Fact]
    public void A_real_Fireball_plan_and_hitless_Righteous_Fire_keep_their_native_base_and_added_damage_boundary()
    {
        var source = NativeSources(Sources(("damage_+%", 20), ("spell_damage_+%", 30), ("fire_damage_+%", 50),
            ("elemental_damage_+%", 40), ("chaos_damage_+%", 80), ("spell_minimum_added_cold_damage", 100),
            ("spell_maximum_added_cold_damage", 100)));
        var plan = SkillHitPlan.ForSkill(Program.Skills, Program.Skills.EffectRow("Fireball"), 1);
        EqualPacket(Packet((DamageType.Fire, 9, 14)), plan.SpellBase);
        source.Remove(Program.Stats.Key("spell_minimum_added_cold_damage")!.Value);
        source.Remove(Program.Stats.Key("spell_maximum_added_cold_damage")!.Value);
        EqualPacket(Packet((DamageType.Chaos, 28.8, 44.8)), SpellHit.Resolve(plan, Program.Stats, source,
            new Dictionary<uint, int>(), new DamageConversion().AddGlobal(DamageType.Fire, DamageType.Chaos, 100)));
        source[Program.Stats.Key("spell_minimum_added_cold_damage")!.Value] = 100;
        source[Program.Stats.Key("spell_maximum_added_cold_damage")!.Value] = 100;
        var rf = SkillHitPlan.ForSkill(Program.Skills, Program.Skills.EffectRow("RighteousFire"), 1);
        Assert.False(rf.CanHit);
        Assert.True(SpellHit.Resolve(rf, Program.Stats, source, new Dictionary<uint, int>(), new()).IsZero);
    }
}

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(false, 0, 28d, 72.8d)]
    [InlineData(false, 1, 29.12d, 75.712d)]
    [InlineData(false, 3, 31.36d, 81.536d)]
    [InlineData(true, 0, 28.8d, 44.8d)]
    [InlineData(true, 1, 29.952d, 46.592d)]
    [InlineData(true, 3, 32.256d, 50.176d)]
    public void Actual_supported_native_attack_and_spell_packets_share_history_charge_scaling_and_actor_ranges(
        bool spell, int held, double minimum, double maximum)
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Elementalist",
            classRow: SkillCatalog.WitchRow, baseClass: 3);
        var catalog = Program.Items;
        var skills = Program.Skills;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        inventory.Add(6, 0, 0, ItemCatalog.Create(catalog.Find("Iron Ring")!) with
            { Mods = [PercentageAttributeMod(catalog, "additional_all_attributes", 200)] });
        var weaponType = catalog.Find(spell ? "Rusted Sword" : "Crude Bow")!;
        inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(weaponType) with { Quality = 0 });
        var gemType = catalog.Find("Metadata/Items/Gems/" + (spell ? "SkillGemFireball" : "SkillGemBurningArrow"))!;
        var gem = ItemCatalog.Create(gemType) with { GemExperience = catalog.Gem(gemType)!.ExperienceFor(1), Quality = 0, GemFlags = 4 };
        inventory.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(catalog.Find("Plate Vest")!) with
            { Sockets = [new(SocketColour.White, gem)], Links = [1] });
        // Real gem/catalog/charge-session path, with controlled raw source overrides to isolate its modifier arithmetic.
        foreach (var (name, value) in spell
            ? new (string, int)[] { ("damage_+%", 20), ("spell_damage_+%", 30), ("fire_damage_+%", 50),
                ("elemental_damage_+%", 40), ("chaos_damage_+%", 80), ("fire_damage_%_to_convert_to_chaos", 100) }
            : new (string, int)[] { ("damage_+%", 20), ("physical_damage_+%", 30), ("fire_damage_+%", 50) })
            game.State.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        var charges = ProgramValue<ChargeSession>("ChargesOf", game.State);
        charges.State.Set(ChargeType.Frenzy, held, game.State.Clock.Elapsed.TotalSeconds, new(3, 0, Charges.BaseDurationMs));
        game.State.Pools = Program.PoolsFor(game.State, 100);
        var gems = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)typeof(Program)
            .GetMethod("GemSkillEffects", BindingFlags.Static | BindingFlags.NonPublic, binder: null,
                types: [typeof(ClientState)], modifiers: null)!.Invoke(null, [game.State])!).ToArray();
        var selected = Assert.Single(gems);
        Assert.Equal(spell ? "Fireball" : "BurningArrow", skills.EffectId(selected.Effect));
        if (!spell)
        {
            var basis = skills.WeaponBase(weaponType.Row)!.Value;
            Assert.Equal((5, 13), (basis.Min, basis.Max));
            Assert.Equal(28000, skills.DamageEffectiveness(selected.Effect, 1)); // raw 18000: 280% of base
        }
        var hit = ProgramValue<DamagePacket>("SupportedHit", game.State, selected.Id);
        var type = spell ? DamageType.Chaos : DamageType.Fire;
        Assert.Equal(minimum, hit[type].Min, 7);
        Assert.Equal(maximum, hit[type].Max, 7);
        Assert.Equal(minimum, hit.MinTotal, 7);
        Assert.Equal(maximum, hit.MaxTotal, 7);
        var actor = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State).Single(s => s.Id == selected.Id);
        var stats = actor.Stats!.ToDictionary(s => s.Key, s => s.Value);
        string prefix = spell ? "spell" : "main_hand";
        Assert.Equal((int)Math.Floor(minimum), stats[Program.Stats.Key($"{prefix}_minimum_{type.ToString().ToLowerInvariant()}_damage")!.Value]);
        Assert.Equal((int)Math.Floor(maximum), stats[Program.Stats.Key($"{prefix}_maximum_total_damage")!.Value]);
    }
}
