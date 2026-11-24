namespace GameServer.Gateway.Tests;

/// <summary>The fifteen per-aura effect stats scale only their own aura.</summary>
public class PerAuraEffectTests
{
    [Fact]
    public void Per_aura_effect_scales_only_that_aura()
    {
        var skills = Program.Skills;
        var haste = new AuraCatalog.Aura(1, skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000, Level: 20);
        var grace = new AuraCatalog.Aura(2, skills.EffectRow("Grace"), "Grace", "Grace", 1, 143, 0, 5000, Level: 20);
        var plain = AuraCatalog.Bonus([haste, grace], 0, skills, Program.Stats);
        Assert.True(plain.AttackSpeedPct > 0);
        Assert.True(plain.MovementVelocityPct > 0);
        Assert.True(plain.Evasion > 0);

        // +100% effect of Haste only: Haste's own bonuses double, Grace's do not move.
        var stronger = haste with { PerAuraEffectPct = 100 };
        var scaled = AuraCatalog.Bonus([stronger, grace], 0, skills, Program.Stats);
        Assert.Equal(2 * plain.AttackSpeedPct, scaled.AttackSpeedPct);
        Assert.Equal(2 * plain.MovementVelocityPct, scaled.MovementVelocityPct);
        Assert.Equal(plain.Evasion, scaled.Evasion);
    }

    [Fact]
    public void Per_aura_effect_scales_Pride_and_the_mapping_covers_the_fifteen_auras()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("Pride");
        Assert.True(effect >= 0);
        var aura = new AuraCatalog.Aura(0x999, effect, "Pride", "Pride", 1, AuraCatalog.BuffRow("Pride"),
            0, 0, Level: 20, Quality: 0);
        Assert.Equal((19, 39), AuraCatalog.PridePhysicalDamageTaken(aura, 0, skills, Program.Stats));
        Assert.Equal((38, 78), AuraCatalog.PridePhysicalDamageTaken(aura with { PerAuraEffectPct = 100 }, 0, skills, Program.Stats));

        // The mapping covers exactly the fifteen aura-body rows, and every named stat exists in the client's
        // Stats table. Malevolence is the spelling mismatch: its stat is named for Delirium.
        Assert.Equal(15, AuraCatalog.PerAuraEffectStats.Count);
        Assert.Equal(Stat.DeliriumAuraEffectPlusPct, AuraCatalog.PerAuraEffectStats["Malevolence"]);
        foreach (uint key in AuraCatalog.PerAuraEffectStats.Values)
            Assert.True(Program.Stats.Name(key) is not null);
    }
}
