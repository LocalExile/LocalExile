using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using System.Reflection;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Support_refresh_keeps_passive_granted_skill_identity_and_unmodified_socket_quality()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(choice: "Paladin", level: 20, refundPoints: 60);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 39868), allocate: true)).Error);
        var passive = Assert.Single(game.GemEffects());
        game.State.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] = 1000;
        var helmet = ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = new[] { "Fireball", "Faster Casting Support" }.Select(name =>
            {
                var gem = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.Blue, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index });
            }).ToArray(),
            Links = [2],
        };
        game.Items.Add(PlayerInventories.Helmet, 0, 0, helmet);
        var active = game.GemEffects().Single(g => g.Gem != 0);
        for (int i = 0; i < 3; i++)
        {
            var supported = Program.SupportedSkill(game.State, active.Id);
            Assert.Single(supported.Supports);
            Assert.Equal(0, supported.Quality);
            var granted = Program.SupportedSkill(game.State, passive.Id);
            Assert.Equal((passive.Effect, passive.Level, 0), (granted.Effect, granted.Level, granted.Quality));
            Assert.Empty(granted.Supports);
            Assert.Equal(passive.Id, game.GemEffects().Single(g => g.Gem == 0).Id);
        }
        var records = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State);
        Assert.Single(records.Single(s => s.Id == active.Id).Supports!);
        Assert.Empty(records.Single(s => s.Id == passive.Id).Supports ?? []);
    }

    [Fact]
    public async Task Support_levels_compose_with_global_ascendancy_levels_without_changing_intrinsic_gem()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 90, refundPoints: 60);
        ushort[] path = [54114, 48846, 24607, 46836, 27473, 30851];
        Assert.Null((await game.Change(path, allocate: true)).Error);
        var activeGem = Program.Items.FindGem("Fireball")!.Value;
        game.State.PlayerStats[Program.Stats.Key("virtual_strength_for_requirements")!.Value] = 1000;
        var supportGem = Program.Items.FindGem("Empower Support")!.Value;
        var support = ItemCatalog.Create(supportGem.Type) with
        {
            GemVariant = supportGem.Variant.Index,
            GemExperience = Program.Items.Gem(supportGem.Type)!.ExperienceFor(3),
        };
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [new(SocketColour.Blue, ItemCatalog.Create(activeGem.Type) with { GemVariant = activeGem.Variant.Index }),
                new(SocketColour.Red, support)], Links = [2],
        });
        var grant = Assert.Single(game.GemEffects());
        Assert.Equal((2, 20), (grant.Level, grant.Quality));
        var supported = Program.SupportedSkill(game.State, grant.Id);
        var applied = Assert.Single(supported.Supports);
        uint levelKey = Program.Stats.Key("supported_active_skill_gem_level_+")!.Value;
        int addition = applied.Stats.Single(s => s.Key == levelKey).Value;
        Assert.Equal(2 + addition, supported.Level);
        Assert.Equal(20, supported.Quality);
        var intrinsic = game.Items.Items(PlayerInventories.Helmet).Single().Item.Sockets[0].Gem!;
        Assert.Equal((1, (byte)0), (Program.Items.GemLevel(intrinsic), intrinsic.Quality));
    }

    [Fact]
    public void Supported_attack_uses_canonical_base_added_derived_Rage_and_captured_conversion_inputs()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter();
        int effect = Program.Skills.EffectRow("HeavyStrike");
        var own = Program.Skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        (uint, int)[] modifiers = [(Program.Stats.Key("support_melee_physical_damage_+%_final")!.Value, 49)];
        own = Program.SumSkillInputs(own, modifiers);
        var supported = new Program.SupportedGem(effect, 1, 0, [], own, modifiers, 1);
        var raw = new Dictionary<uint, int>();
        foreach (var (name, value) in new[]
        {
            (DerivedDamageIncreases.SpellToAttack, 1), ("spell_damage_+%", 1),
            ("attack_minimum_added_physical_damage", 4), ("attack_maximum_added_physical_damage", 8),
            (DamageStats.MaxManaAddedLightning, 6), ("maximum_mana", 1000),
            ("attack_damage_+%_final_from_rage", 30), ("skill_physical_damage_%_to_convert_to_fire", 100),
        }) raw[Program.Stats.Key(name)!.Value] = value;
        var weapon = ProgramValue<SkillCatalog.Unarmed>("WeaponFor", game.State);
        var beforeConversion = AttackSkillHit.BaseHit(weapon, Program.SumSkillInputs(raw, own.Select(s => (s.Key, s.Value))),
            Program.Stats, true, Program.Skills.AttackBaseMultiplier(effect, 1), Program.Skills.DamageEffectiveness(effect, 1));
        // Only physical receives the melee support; added mana lightning retains its own typed increase path.
        var expected = beforeConversion.Select((type, range) => type == DamageType.Physical ? range.Scale(1.49) : range)
            .Convert(DamageStats.Conversion(raw, Program.Stats, attack: true));
        game.State.PlayerStats[Program.Stats.Key("skill_physical_damage_%_to_convert_to_fire")!.Value] = 0;
        game.State.PlayerStats[Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value] = 9999;
        var actual = Program.SupportedHit(game.State, 0, raw, supported);
        foreach (var type in DamageTypes.ConversionOrder) Assert.Equal(expected[type], actual[type]);
        Assert.True(actual[DamageType.Physical].IsZero);
        Assert.True(actual[DamageType.Lightning].Min > 0);
    }

    [Fact]
    public void Supported_attack_crit_uses_shared_half_up_formula_and_both_prohibition_inputs()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter();
        int effect = Program.Skills.EffectRow("HeavyStrike");
        var own = new Dictionary<uint, int>();
        var captured = new Program.SupportedGem(effect, 1, 0, [], own, [], 1);
        var player = new Dictionary<uint, int> { [Program.Stats.Key(AttackBaseCriticalStrike.UnarmedBaseStat)!.Value] = 50 };
        game.State.Pools = game.State.Pools with { CriticalStrikeChancePct = 1 };
        Assert.Equal(51, Program.SupportedCriticalChance(game.State, 0, player, captured));
        own[Program.Stats.Key("virtual_global_cannot_crit")!.Value] = 1;
        Assert.Equal(0, Program.SupportedCriticalChance(game.State, 0, player, captured));
        own.Clear();
        player[Program.Stats.Key("cannot_critical_strike_with_attacks")!.Value] = 1;
        Assert.Equal(0, Program.SupportedCriticalChance(game.State, 0, player, captured));
        player.Remove(Program.Stats.Key("cannot_critical_strike_with_attacks")!.Value);
        player[Program.Stats.Key(AttackBaseCriticalStrike.UnarmedBaseStat)!.Value] = int.MaxValue;
        own[Program.Stats.Key("additional_base_critical_strike_chance")!.Value] = 1;
        Assert.Equal(10000, Program.SupportedCriticalChance(game.State, 0, player, captured));
        own[Program.Stats.Key("additional_base_critical_strike_chance")!.Value] = int.MinValue;
        Assert.Equal(0, Program.SupportedCriticalChance(game.State, 0, player, captured));
        own[Program.Stats.Key("additional_base_critical_strike_chance")!.Value] = int.MaxValue;
        captured = captured with { Effect = Program.Skills.EffectRow("Fireball") };
        Assert.Equal(10000, Program.SupportedCriticalChance(game.State, 0, player, captured));
    }

    [Fact]
    public void Linked_cost_multiplier_raw_flat_and_signed_cost_inputs_agree_in_record_preflight_and_payment()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 20);
        foreach (var (name, value) in new[]
        {
            ("virtual_intelligence_for_requirements", 1000), ("base_mana_cost_+", 3),
            ("base_cost_+%", 25), ("mana_cost_+%", 25), ("base_mana_cost_-%", -50),
        }) game.State.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = new[] { "Fireball", "Controlled Destruction Support" }.Select(name =>
            {
                var gem = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.Blue, ItemCatalog.Create(gem.Type) with
                    { GemVariant = gem.Variant.Index, Quality = name == "Fireball" ? (byte)20 : (byte)0 });
            }).ToArray(), Links = [2],
        });
        var grant = Assert.Single(game.GemEffects());
        var supported = Program.SupportedSkill(game.State, grant.Id);
        Assert.Single(supported.Supports);
        Assert.Equal((1, 20), (supported.Level, supported.Quality));
        var cost = ProgramValue<SkillCost>("CostFor", game.State, grant.Effect, grant.Level, grant.Quality, grant.Id);
        uint baseKey = Program.Stats.Key("base_mana_cost")!.Value;
        uint expected = (uint)Math.Truncate((Math.Floor(supported.Stats[baseKey] * supported.CostMultiplier) + 3) * 1.25 * .75);
        Assert.Equal(expected, cost.Mana);
        var record = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State).Single(s => s.Id == grant.Id);
        Assert.Equal((int)expected, record.Stats!.Single(s => s.Key == Program.Stats.Key("mana_cost")).Value);
        game.State.PlayerMana = expected - 1;
        uint life = game.State.PlayerLife;
        var args = new object?[] { game.State, grant.Effect, grant.Level, grant.Quality, null, grant.Id };
        var pay = typeof(Program).GetMethod("PaySkillCost", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.False((bool)pay.Invoke(null, args)!);
        Assert.Equal(expected - 1, game.State.PlayerMana);
        Assert.Equal(life, game.State.PlayerLife);
        game.State.PlayerMana = 100;
        Assert.True((bool)pay.Invoke(null, args)!);
        Assert.Equal(cost, (SkillCost)args[4]!);
        Assert.Equal(100 - expected, game.State.PlayerMana);
        Assert.Equal(life, game.State.PlayerLife);
    }
}
