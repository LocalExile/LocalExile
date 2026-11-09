using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CastOnCritTests
{
    [Fact]
    public void Cast_on_crit_cycles_linked_spells_and_skips_ones_on_cooldown()
    {
        var state = new CastOnCritState();
        (uint, int) link = (7, 0);
        int[] spells = [10, 20];
        static double Cooldown(int _) => 0.15;

        // Successive crits cycle through the link's spells in socket order.
        Assert.Equal(0, state.Pick(link, spells, 0, Cooldown));
        Assert.Equal(1, state.Pick(link, spells, 0.01, Cooldown));
        // Both on cooldown: nothing triggers.
        Assert.Null(state.Pick(link, spells, 0.05, Cooldown));
        // Each spell's own cooldown: the first is ready again at 0.15 s, the second at 0.16 s.
        Assert.Equal(0, state.Pick(link, spells, 0.155, Cooldown));
        Assert.Equal(1, state.Pick(link, spells, 0.2, Cooldown));
        // The next in turn (the first) is ready again at 0.305 s; at 0.32 s both are cooling.
        Assert.Equal(0, state.Pick(link, spells, 0.31, Cooldown));
        Assert.Null(state.Pick(link, spells, 0.32, Cooldown));
        // Copies of one spell share its cooldown, also across another link.
        Assert.Null(state.Pick((8, 1), [20], 0.25, Cooldown));

        // Recovery rate: 100% increased halves the 150 ms; -100% never recovers.
        Assert.Equal(0.075, CastOnCritState.CooldownSeconds(150, 100), 9);
        Assert.Equal(double.PositiveInfinity, CastOnCritState.CooldownSeconds(150, -100));
    }

    [Fact]
    public void Cast_on_crit_supports_the_attack_and_the_spell_with_the_data_values()
    {
        var skills = Program.Skills;
        GemSupports.Candidate[] gem =
        [
            new(skills.EffectRow("SupportCastOnCriticalStrike"), 1, 0, 1),
            new(skills.EffectRow("SupportCastOnCritTriggered"), 1, 0, 1),
        ];
        static int Stat(IReadOnlyList<GemSupports.Applied> supports, string name) =>
            Program.Stats.Key(name) is uint key ? supports.Sum(s => s.Stats.Where(v => v.Key == key).Sum(v => v.Value)) : 0;

        var onAttack = Game.Supports.Resolve(skills.EffectRow("DoubleStrike"), gem, skills);
        Assert.Equal(100, Stat(onAttack, CastOnCritState.AttackStat));
        Assert.Equal(0, Stat(onAttack, CastOnCritState.SpellStat));

        var onSpell = Game.Supports.Resolve(skills.EffectRow("Fireball"), gem, skills);
        Assert.Equal(0, Stat(onSpell, CastOnCritState.AttackStat));
        Assert.Equal(1, Stat(onSpell, CastOnCritState.SpellStat));
        Assert.Equal(1, Stat(onSpell, "spell_uncastable_if_triggerable"));
        Assert.Equal(-19, Stat(onSpell, "support_cast_on_crit_spell_damage_+%_final"));
        var trigger = onSpell.Single(s => s.Effect.Id == "SupportCastOnCritTriggered");
        Assert.Equal(150, skills.CooldownMs(trigger.Effect.Row, trigger.Gem.Level));

        // The less spell damage reaches a spell's hit, not an attack's.
        var lessSpell = new[] { ("support_cast_on_crit_spell_damage_+%_final", -19) };
        Assert.Equal(0.81, GemSupportDamage.Multipliers(DamageTypeHistory.Fire, lessSpell, new HashSet<int> { 1 }).GeneralOrPhysical, 9);
        Assert.Equal(1, GemSupportDamage.Multipliers(DamageTypeHistory.Fire, lessSpell, new HashSet<int> { 0 }).GeneralOrPhysical, 9);
    }
}
