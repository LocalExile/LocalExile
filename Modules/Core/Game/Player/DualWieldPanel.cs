using GameServer.Core.World;


/// <summary>
/// The Offence tab's Damage per Use, Damage per Second and Attacks per Second of a dual-wielded attack (issue #75), as
/// Path of Building's CalcOffence combines the two weapon passes: the hands alternate, so a use deals the average of
/// both hands' critical-weighted hits (<c>combineStat("AverageDamage", "DPS")</c> halved) and the attack rate is the
/// harmonic mean of the two (<c>combineStat("Speed", "HARMONICMEAN")</c>: a main and an off swing take
/// <c>main + off</c> seconds); a skill that hits with both at once (<c>skill_double_hits_when_dual_wielding</c>) deals
/// both hits per use at the same rate. Damage per second is damage per use at that rate. With one hand the rows are
/// that hand's (<see cref="SkillCatalog.Unarmed.Stats"/>).
/// </summary>
public static class DualWieldPanel
{
    /// <summary><paramref name="damageFactor"/> weights the main hand's hits (critical chance and multiplier, chance to
    /// hit); <paramref name="offDamageFactor"/> the off hand's own, which differ when the weapons' local critical or
    /// accuracy stats do (null: the same as the main hand's). <paramref name="both"/>: the hands hit together;
    /// <paramref name="bothSeconds"/> is then the use's own time (null: the average of the two).</summary>
    public static (int PerUse, int PerSecond, int HundredTimesAttacksPerSecond) Average(DamagePacket mainHit, double mainSeconds,
        DamagePacket offHit, double offSeconds, double damageFactor, double? offDamageFactor = null, bool both = false,
        double? bothSeconds = null)
    {
        double main = SpellDamage.Display((mainHit.MinTotal, mainHit.MaxTotal), 0, damageFactor).PerUse;
        double off = SpellDamage.Display((offHit.MinTotal, offHit.MaxTotal), 0, offDamageFactor ?? damageFactor).PerUse;
        double perUse = both ? main + off : (main + off) / 2.0;
        double seconds = both && bothSeconds is double own ? own : (mainSeconds + offSeconds) / 2;
        return (Native(perUse), Native(perUse / seconds), Native(100 / seconds));
    }

    static int Native(double value) => double.IsNaN(value) || value <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(value));

    /// <summary>The off hand's swing time: its own duration (local speed included) at the off-hand increased attack
    /// speed, at least one server tick.</summary>
    public static double OffHandSeconds(SkillCatalog.Unarmed offHand, int offHandAttackSpeedPct) =>
        Math.Max(Program.TickSeconds, offHand.AttackMs / 1000.0 / Math.Max(0.01, 1 + offHandAttackSpeedPct / 100.0));
}
