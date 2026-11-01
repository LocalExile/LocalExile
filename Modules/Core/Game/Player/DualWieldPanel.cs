using GameServer.Core.World;


/// <summary>
/// The Offence tab's Damage per Use, Damage per Second and Attacks per Second of a dual-wielded attack (issue #75):
/// the hands alternate, so the panel shows the average of both hands, as Path of Building's CalcOffence combines the
/// two weapon passes (<c>combineStat(..., "AVERAGE")</c>): each hand's own critical-weighted average hit and its own
/// attack rate, averaged. With one hand the rows are that hand's (<see cref="SkillCatalog.Unarmed.Stats"/>).
/// </summary>
static class DualWieldPanel
{
    /// <summary><paramref name="damageFactor"/> weights the main hand's hits (critical chance and multiplier, chance to
    /// hit); <paramref name="offDamageFactor"/> the off hand's own, which differ when the weapons' local critical or
    /// accuracy stats do (null: the same as the main hand's).</summary>
    public static (int PerUse, int PerSecond, int HundredTimesAttacksPerSecond) Average(DamagePacket mainHit, double mainSeconds,
        DamagePacket offHit, double offSeconds, double damageFactor, double? offDamageFactor = null)
    {
        var main = SpellDamage.Display((mainHit.MinTotal, mainHit.MaxTotal), 1 / mainSeconds, damageFactor);
        var off = SpellDamage.Display((offHit.MinTotal, offHit.MaxTotal), 1 / offSeconds, offDamageFactor ?? damageFactor);
        return (Native((main.PerUse + off.PerUse) / 2.0), Native((main.PerSecond + off.PerSecond) / 2.0),
            Native(100 * (1 / mainSeconds + 1 / offSeconds) / 2));
    }

    static int Native(double value) => double.IsNaN(value) || value <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(value));

    /// <summary>The off hand's swing time: its own duration (local speed included) at the off-hand increased attack
    /// speed, at least one server tick.</summary>
    public static double OffHandSeconds(SkillCatalog.Unarmed offHand, int offHandAttackSpeedPct) =>
        Math.Max(Program.TickSeconds, offHand.AttackMs / 1000.0 / Math.Max(0.01, 1 + offHandAttackSpeedPct / 100.0));
}
