using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SkillReviewRegressionTests
{
    [Fact]
    public void Trigger_ailments_apply_generic_CwC_damage_but_not_CoC_spell_damage()
    {
        var raw = DamagePacket.Of(DamageType.Fire, 100, 200)
            .Add(DamageType.Physical, new(100, 200)).Add(DamageType.Chaos, new(100, 200));
        var baseline = AilmentPanelStats.Compute(new("spell", true), raw, new(), _ => 0).ToDictionary(p => p.Stat, p => p.Value);
        foreach (var (stat, percent, multiplier) in new[] {
            ("support_cast_on_crit_spell_damage_+%_final", -19, 1d),
            ("support_cast_while_channelling_triggered_skill_damage_+%_final", -20, .8) })
        {
            var rows = AilmentPanelStats.Compute(new("spell", true), raw, new(), name => name == stat ? percent : 0);
            Assert.Contains(rows, p => p.Value > 0 && p.Stat.Contains("ignite"));
            Assert.Contains(rows, p => p.Value > 0 && p.Stat.Contains("poison"));
            foreach (var row in rows) Assert.Equal((int)Math.Round(baseline[row.Stat] * multiplier), row.Value);
        }
    }

    [Fact]
    public void Persistent_gem_edits_can_reuse_an_already_identified_item_record()
    {
        var original = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
        var changed = original with { GemExperience = 1 };
        object identity = PlayerInventories.GemIdentity(original);
        Assert.NotSame(identity, PlayerInventories.GemIdentity(changed));
        Assert.Same(changed, PlayerInventories.PreserveGemIdentity(original, changed));
        Assert.Same(identity, PlayerInventories.GemIdentity(changed));
        PlayerInventories.PreserveGemIdentity(original, changed);
        PlayerInventories.PreserveGemIdentity(changed, original);
        Assert.Same(identity, PlayerInventories.GemIdentity(original));
        Assert.Same(identity, PlayerInventories.GemIdentity(changed));
    }
}
