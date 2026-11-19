using GameServer.Core.World;

// Support gems whose more damage depends on the target or the player at the hit (poedb): Hypothermia "more Damage with
// Hits and Ailments against Chilled Enemies", Behead "against Enemies on Low Life" (half its maximum Life or less, PoB
// LowPoolThreshold), Damage on Full Life "Supported Attack Skills deal more Damage while on Full Life", Vile Toxins
// "5% more Damage with Hits for each Poison on the Enemy, up to a maximum of 8 Poisons", Bloodlust "more Melee Physical
// Damage against Bleeding Enemies" and Power Charge on Critical "more Damage per Power Charge". GemSupportDamage builds
// the skill's hit without them, once per cast; these scale each target's roll.
public static partial class Game
{
    public static double ConditionalSupportMore(IReadOnlyDictionary<uint, int>? skill, bool attack, bool chilled,
        bool lowLife, bool fullLife, bool lifetap, int poisonStacks = 0, bool bleeding = false, int powerCharges = 0,
        bool arcaneSurge = false, int recentIgnites = 0)
    {
        int Get(uint name) => skill is not null && name is var k ? skill.GetValueOrDefault(k) : 0;
        double more = 1;
        void Apply(bool applies, uint name) { if (applies) more *= Math.Max(0, 1 + Get(name) / 100.0); }
        Apply(chilled, Stat.SupportHypothermiaDamagePlusPctVsChilledEnemiesFinal);
        Apply(lowLife, Stat.SupportExecutionerDamageVsEnemiesOnLowLifePlusPctFinal);
        Apply(attack && fullLife, Stat.SupportDamageWhileOnFullLifePlusPctFinal);
        Apply(lifetap, Stat.SupportLifetapDamagePlusPctFinalWhileBuffed);
        Apply(bleeding, Stat.SupportBloodlustMeleePhysicalDamagePlusPctFinalVsBleedingEnemies);
        Apply(arcaneSurge, Stat.SupportArcaneSurgeSpellDamagePlusPctFinalWhileYouHaveArcaneSurge);
        // The per-stack share applies to the stacks that count (PoB: capped by the support's maximum).
        int counted = Math.Clamp(poisonStacks, 0, Math.Max(0, Get(Stat.SupportDebilitateHitDamageMaxPoisonStacks)));
        if (counted > 0) more *= Math.Max(0, 1 + Get(Stat.SupportDebilitateHitDamagePlusPctFinalPerPoisonStack) * counted / 100.0);
        if (powerCharges > 0)
            more *= Math.Max(0, 1 + Get(Stat.SupportPowerChargeOnCritDamagePlusPctFinalPerPowerCharge) * powerCharges / 100.0);
        // Controlled Blaze: "(3)% less Damage per Enemy Ignited Recently", floored by the support's minimum.
        int recentPer = Get(Stat.SupportRecentIgnitesDamagePerRecentIgnitePlusPctFinal);
        if (recentPer != 0)
        {
            int recentMin = Get(Stat.SupportRecentIgnitesDamagePerRecentIgnitePlusPctFinalMinimum);
            long total = Math.Max(recentMin, (long)recentPer * recentIgnites);
            more *= Math.Max(0, 1 + total / 100.0);
        }
        return more;
    }

    // Call under the Monsters lock, after the per-target outgoing damage hooks.
    public static DamagePacket WithConditionalSupportDamageLocked(Player conn, IReadOnlyDictionary<uint, int>? skill,
        bool attack, SpawnedMonster target, DamagePacket rolled, double now)
    {
        int Get(uint name) => skill is not null && name is var k ? skill.GetValueOrDefault(k) : 0;
        bool fullLife = attack && Get(Stat.SupportDamageWhileOnFullLifePlusPctFinal) != 0
            && conn.PlayerLife > 0 && conn.PlayerMaxLife > 0
            && (conn.PlayerLife >= conn.PlayerMaxLife
                || CountsAsFullLife(conn.PlayerLife, conn.PlayerMaxLife, SelfCurseConditionInputs(conn)));
        double more = ConditionalSupportMore(skill, attack, target.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0,
            FlaskRecovery.LowLife(target.Life, target.MaxLife), fullLife,
            Lifetap.ActiveAt(conn, now), target.Poisons.Stacks(now), target.Bleeds.Burning,
            ChargesOf(conn).Effective(ChargeType.Power), ArcaneSurgeActive(conn, now), RecentlyIgnitedCount(conn, now));
        var packet = more == 1 ? rolled : rolled.Scale(more);
        // Immolate: added Fire Damage against Burning Enemies (the stat description's own wording).
        if (target.Ignites.Burning)
        {
            int min = Get(Stat.GlobalMinimumAddedFireDamageVsBurningEnemies);
            int max = Get(Stat.GlobalMaximumAddedFireDamageVsBurningEnemies);
            if (min > 0 || max > 0)
            {
                double roll = min + AreaRng(conn).NextDouble() * (Math.Max(min, max) - min);
                packet = packet.Add(DamageType.Fire, new(roll, roll));
            }
        }
        // Bloodthirst: "gain X% of Maximum Life as added Physical Damage with Weapons while on Low Life".
        if (conn.PlayerLife < conn.PlayerMaxLife * 0.35)
        {
            int percent = Get(Stat.BloodPriceGainPctMaximumLifeAsAddedPhysicalDamageWithWeaponsWhileOnLowLife);
            if (percent > 0)
            {
                double flat = conn.PlayerMaxLife * percent / 100.0;
                packet = packet.Add(DamageType.Physical, new(flat, flat));
            }
        }
        // Bonechill: "Chills also grant Cold Damage Taken +% equal to the Slow amount" for the hit's cold part.
        double chillMagnitude = target.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now);
        if (Get(Stat.SupportChillsAlsoGrantColdDamageTakenPlusPctEqualToSlowAmount) != 0 && chillMagnitude > 0)
            packet = packet.Select((type, range) => type == DamageType.Cold ? range.Scale(1 + chillMagnitude / 100.0) : range);
        // Inspiration: "Supported Skills deal (3-7)% more Elemental Damage per Inspiration Charge".
        int charges = conn.SupportGems.InspirationCharges;
        int perCharge = Get(Stat.ElementalDamagePlusPctFinalPerRighteousCharge);
        if (charges > 0 && perCharge > 0)
        {
            double elemental = Math.Max(0, 1 + (double)perCharge * charges / 100.0);
            packet = packet.Select((type, range) => type is DamageType.Fire or DamageType.Cold or DamageType.Lightning
                ? range.Scale(elemental) : range);
        }
        return packet;
    }
}
