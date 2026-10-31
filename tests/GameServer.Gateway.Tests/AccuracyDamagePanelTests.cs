using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(null, "Rusted Sword")]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", "Rusted Sword")]
    [InlineData("BurningArrow", "Crude Bow")]
    [InlineData("Fireball", "Rusted Sword")]
    public void Damage_estimates_weight_landing_and_independent_critical_confirmation(string? gem, string weapon)
    {
        using var f = new Contact(new Draws(), gem, weapon);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var main = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!;
        var hit = gem is null ? Program.AttackHitConverted(main, Program.CharacterPlayerStats(f.Conn), weapon != "Crude Bow", null) : f.Hit;
        double seconds = gem is null ? main.AttackSeconds(Program.CharacterPlayerStats(f.Conn))
            : gem == "Fireball" ? (int)Method("CastMs").Invoke(null, [f.Conn, Program.Skills.EffectRow(gem), f.Skill])! / 1000.0
            : (double)Method("SupportedAttackSeconds").Invoke(null, [f.Conn, f.Skill])!;
        foreach (int luck in new[] { -1, 0, 1 })
        {
            f.Set("extra_critical_rolls", luck);
            var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            int Get(string name) => panel[Program.Stats.Key(name)!.Value];
            double p = Get(gem == "Fireball" ? "spell_critical_strike_chance" : "main_hand_critical_strike_chance") / 10000.0;
            Assert.InRange(p, .01, .99);
            double critical = luck < 0 ? p * p : luck > 0 ? 1 - (1 - p) * (1 - p) : p;
            double landed = gem == "Fireball" ? 1 : .05;
            double confirmed = critical * landed;
            double average = hit.AverageTotal * landed * (1 - confirmed + confirmed * f.Multiplier / 100.0);
            Assert.Equal((int)Math.Round(average * 100), Get("display_hundred_times_damage_per_skill_use"));
            Assert.Equal((int)Math.Round(average * 100 / seconds), Get("hundred_times_damage_per_second"));
            Assert.Equal((int)Math.Floor(hit.MaxTotal), Get("main_hand_maximum_total_damage"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("BurningArrow")]
    public void Raw_cannot_evade_updates_estimates_and_both_reference_chances_and_removes_cleanly(string? gem)
    {
        using var f = new Contact(new Draws(), gem, "Crude Bow");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("global_cannot_crit", 1);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var main = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!;
        var hit = gem is null ? Program.AttackHitConverted(main, Program.CharacterPlayerStats(f.Conn), false, null) : f.Hit;
        foreach (int cannotEvade in new[] { 0, 1, 0 })
        {
            f.Set("global_always_hit", cannotEvade);
            var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            int Get(string name) => panel[Program.Stats.Key(name)!.Value];
            int chance = cannotEvade == 1 ? 100 : 5;
            Assert.Equal(chance, Get("main_hand_chance_to_hit_%"));
            Assert.Equal(chance, Get("main_hand_chance_to_hit_evasive_monsters_%"));
            Assert.Equal((int)Math.Round(hit.AverageTotal * chance), Get("display_hundred_times_damage_per_skill_use"));
            Assert.Equal(0, Get("main_hand_accuracy_rating"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("BurningArrow")]
    public void Diagnostic_chance_alias_and_target_masteries_do_not_replace_reference_accuracy_for_DPS(string? gem)
    {
        using var f = new Contact(new Draws(), gem, "Crude Bow");
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 500 };
        Dictionary<uint, int> Panel() => Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        var before = Panel();
        uint hitChance = Program.Stats.Key("main_hand_chance_to_hit_%")!.Value;
        uint perUse = Program.Stats.Key("display_hundred_times_damage_per_skill_use")!.Value;
        uint dps = Program.Stats.Key("hundred_times_damage_per_second")!.Value;
        Assert.InRange(before[hitChance], 6, 99);
        f.Conn.Pools = f.Conn.Pools with { AccuracyMoreVsUniquePct = 10000, AccuracyMoreInCloseRangePct = 10000 };
        f.Set("main_hand_chance_to_hit_%", 0);
        var after = Panel();
        Assert.Equal(0, after[hitChance]); // Explicit display replacement is retained, not used as a combat input.
        Assert.Equal(before[perUse], after[perUse]);
        Assert.Equal(before[dps], after[dps]);
    }

    [Fact]
    public void Qualified_local_cannot_evade_updates_ordinary_estimates_and_clears_when_removed()
    {
        using var f = new Contact(new Draws(), weaponName: "Crude Bow");
        f.Set("global_cannot_crit", 1);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        var mod = Program.Items.Mods.ById("AlwaysHits")!;
        Assert.Contains(mod.Stats, s => s.Id == "local_always_hit" && s.Local);
        foreach (bool equipped in new[] { true, false })
        {
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, main.Id,
                item => item with { Mods = equipped ? [new(ModSection.Explicit, mod.Hash, [1])] : [] }));
            var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(equipped ? 100 : 5, panel[Program.Stats.Key("main_hand_chance_to_hit_%")!.Value]);
            Assert.Equal(equipped ? 100000 : 5000, panel[Program.Stats.Key("display_hundred_times_damage_per_skill_use")!.Value]);
        }
    }

    [Fact]
    public void Real_accuracy_support_changes_selected_estimates_without_changing_actor_or_hit_ranges()
    {
        using var f = new Contact(new Draws(), "BurningArrow", "Crude Bow");
        f.Set("global_cannot_crit", 1);
        f.Conn.Pools = f.Conn.Pools with
        { Accuracy = AccuracyRating.Calculate(f.Conn.Pools.AccuracyBase, f.Conn.Pools.AccuracyPct) };
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var host = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
        var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemAdditionalAccuracy")!) with { Quality = 20 };
        Dictionary<uint, int> Panel(ushort id) => Assert.Single(f.CurrentSkills(), s => s.Id == id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        uint perUse = Program.Stats.Key("display_hundred_times_damage_per_skill_use")!.Value;
        uint dps = Program.Stats.Key("hundred_times_damage_per_second")!.Value;
        uint chance = Program.Stats.Key("main_hand_chance_to_hit_%")!.Value;
        var actor = Panel(SkillCatalog.FirstSkillId);
        var before = Panel(f.Skill);
        var hit = f.Hit;
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, host.Id,
            item => item with { Sockets = [.. item.Sockets, new(SocketColour.White, support)], Links = [3] }));
        var selected = Panel(f.Skill);
        int rating = (int)((f.Conn.Pools.AccuracyBase + 74) * (120 + f.Conn.Pools.AccuracyPct) / 100);
        int expectedChance = DamageModel.AccuracyHitChance(before[Program.Stats.Key("character_sheet_reference_evasion")!.Value], rating);
        Assert.True(expectedChance > before[chance]);
        Assert.Equal(expectedChance, selected[chance]);
        Assert.Equal((int)Math.Round(hit.AverageTotal * expectedChance), selected[perUse]);
        Assert.True(selected[dps] > before[dps]);
        Assert.Equal(actor[perUse], Panel(SkillCatalog.FirstSkillId)[perUse]);
        Assert.Equal(before[Program.Stats.Key("main_hand_maximum_total_damage")!.Value],
            selected[Program.Stats.Key("main_hand_maximum_total_damage")!.Value]);
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, host.Id, _ => host.Item));
        Assert.Equal(before[perUse], Panel(f.Skill)[perUse]);
        Assert.Equal(before[dps], Panel(f.Skill)[dps]);
    }
}
