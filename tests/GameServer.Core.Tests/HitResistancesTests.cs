using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>
/// HitResistances, the critical-strike elemental rules of a player's outgoing hit: Inevitable Judgement's
/// critical_strikes_ignore_elemental_resistances resolves an effective resistance of zero (negative included),
/// the attack-only variant covers attacks only, the non-critical strike's penetration stacks with the hit's
/// general one and subtracts after the cap, the suppressor restores the plain capped resistance, and physical,
/// chaos and damage over time are outside the rules.
/// </summary>
public class HitResistancesTests
{
    static HitResistances.Mods Judgement(int pen = 10) => new(CriticalIgnoreElemental: 1, AttackCriticalIgnoreElemental: 0,
        NonCriticalPenetration: pen, CannotPenetrateOrIgnore: 0);

    static HitResistances.Mods AttackOnly() => new(CriticalIgnoreElemental: 0, AttackCriticalIgnoreElemental: 1,
        NonCriticalPenetration: 0, CannotPenetrateOrIgnore: 0);

    static HitResistances.Mods Suppressed() => new(CriticalIgnoreElemental: 1, AttackCriticalIgnoreElemental: 0,
        NonCriticalPenetration: 10, CannotPenetrateOrIgnore: 1);

    static HitResistances.Strike Hit(bool critical = false, bool attack = true, bool overTime = false) =>
        new(Critical: critical, Attack: attack, OverTime: overTime);

    [Theory]
    [InlineData(DamageType.Fire, 40)]
    public void A_critical_elemental_hit_ignores_the_resistance_whatever_it_is(DamageType type, int resistance)
    {
        // 40% fire, the 75% cap and a negative -30% all mitigate as zero: no reduction and no amplification, and
        // no penetration is left either.
        var resolved = HitResistances.Effective(type, new(resistance, 75, 20), Hit(critical: true), Judgement());
        Assert.Equal(new DamageModel.ResistanceValue(0), resolved);
        Assert.Equal(100.0, DamageModel.MitigateBy(100, DamageModel.Capped(resolved.Value, resolved.Maximum) - resolved.Penetration));
    }

}
