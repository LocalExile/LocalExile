namespace GameServer.Gateway.Tests;

public class SkillRestrictionsTests
{
    [Fact]
    public void No_spell_damage_does_not_disable_a_utility_spell_or_prevent_attack_damage()
    {
        var restrictions = new SkillRestrictions(NoSpellDamage: true);
        Assert.Null(restrictions.CannotUse(spell: true, aura: false, warcry: false));
        Assert.False(restrictions.DealsDamage(spell: true));
        Assert.True(restrictions.DealsDamage(spell: false));
    }

    [Fact]
    public void Spell_disabling_rejects_spells_instead_of_only_zeroing_their_damage()
    {
        var restrictions = new SkillRestrictions(SpellsDisabled: true);
        Assert.Equal("Your spells are disabled", restrictions.CannotUse(spell: true, aura: false, warcry: false));
        Assert.Equal("Your spells are disabled", restrictions.CannotUse(spell: true, aura: true, warcry: false));
        Assert.Null(restrictions.CannotUse(spell: false, aura: false, warcry: false));
    }

    [Fact]
    public void Auras_and_warcries_have_their_own_restrictions()
    {
        var restrictions = new SkillRestrictions(AurasDisabled: true, WarcriesDisabled: true);
        Assert.Equal("Your auras are disabled", restrictions.CannotUse(spell: true, aura: true, warcry: false));
        Assert.Equal("Your warcry skills are disabled", restrictions.CannotUse(spell: false, aura: false, warcry: true));
        // A herald is a reserving spell but is not an aura; Coming Calamity must not disable it as one.
        Assert.Null(restrictions.CannotUse(spell: true, aura: false, warcry: false));
    }

    [Theory]
    [InlineData("base_your_auras_are_disabled")]
    [InlineData("your_auras_are_disabled")]
    public void Both_the_base_and_final_aura_flags_are_enforced(string flag) =>
        Assert.True(SkillRestrictions.From(name => name == flag ? 1 : 0).AurasDisabled);

    [Fact]
    public void Actual_node_flags_and_skill_types_distinguish_auras_spells_heralds_warcries_and_attacks()
    {
        if (Data() is not { } data) return;
        var skills = new SkillCatalog(data);
        var stats = new StatCatalog(data);
        var passives = new PassiveTree(data);
        SkillRestrictions Node(ushort id)
        {
            var allocated = passives.Stats([id]);
            return SkillRestrictions.From(name => stats.Key(name) is uint key ? allocated.GetValueOrDefault(key) : 0);
        }
        int clarity = skills.EffectRow("Clarity"), herald = skills.EffectRow("HeraldOfAsh"),
            fireball = skills.EffectRow("Fireball"), warcry = skills.EffectRow("EnduringCry"), attack = skills.EffectRow("LeapSlam");
        Assert.True(skills.IsSpell(clarity));
        Assert.True(skills.IsAura(clarity));
        Assert.True(skills.IsSpell(herald));
        Assert.False(skills.IsAura(herald));
        Assert.True(skills.IsWarcry(warcry));
        Assert.False(skills.IsSpell(warcry));
        Assert.True(skills.IsAttack(attack));
        Assert.False(skills.IsSpell(attack));
        Assert.Equal(new(true, false, true, true), Node(49937)); // Behemoth: Rampager
        Assert.Equal(new(true, false, false, false), Node(20602)); // Herald: Coming Calamity
        Assert.Equal(new(false, true, false, true), Node(5266)); // Polytheist: Devout of Gruthkul
        var sources = passives.Stats([49937]);
        uint noDamage = stats.Key("base_deal_no_damage")!.Value;
        Assert.Equal(1, skills.EffectStatsFor(fireball, 1, 0, sources)[noDamage]);
        Assert.Equal(0, skills.EffectStatsFor(attack, 1, 0, sources).GetValueOrDefault(noDamage));
    }

    private static string? Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            string[] required = ["PassiveSkills", "Stats", "GrantedEffects", "GrantedEffectsPerLevel",
                "GrantedEffectStatSetsPerLevel", "ActiveSkills", "ActiveSkillType"];
            if (required.All(name => File.Exists(Path.Combine(data, "tables", name + ".json")))) return data;
        }
        return null;
    }
}
