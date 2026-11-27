using GameServer.Core.World;

/// <summary>
/// Resolves raw player hit damage through conversion before applying modifiers. A converted portion inherits each
/// visited damage type, so its increased bucket and typed more modifiers cannot be reconstructed from merged totals.
/// Pinned PoB CalcOffence.lua (16de4b82), calcDamage:35-140, uses the same visited-type set.
/// </summary>
public static class PlayerHitDamage
{
    public const uint VsBlindedDamageStat = Stat.HitAndAilmentDamagePlusPctVsBlindedEnemies;

    // Each damage type's increased stat ({type}_damage_+%), by DamageType.
    static readonly uint[] TypedIncreased = [.. Enum.GetValues<DamageType>().Select(t => Stat.Of($"{t.ToString().ToLowerInvariant()}_damage_+%"))];

    public static DamagePacket Resolve(DamagePacket raw, Func<uint, int> get, DamageConversion conversion,
        bool spell, bool melee = false, bool area = false, bool projectile = false,
        IEnumerable<(uint Id, int Value)>? supportModifiers = null, IReadOnlySet<int>? skillTypes = null,
        bool secondary = false, bool wieldingWand = false, bool retaliation = false, long additionalIncreased = 0, bool wieldingBow = false,
        HitIncreases additional = default, bool trackPhysicalOrigin = false)
    {
        // Secondary damage (Bear Trap, Herald of Ice's shatter) is neither an attack nor a spell: only the generic,
        // area and projectile buckets apply, and no attack/spell support classification.
        spell = spell && !secondary;
        melee = !spell && !secondary && melee;
        var modifiers = supportModifiers?.ToArray() ?? [];
        // Preserve native area/projectile support classification, but do not let contradictory supplied flags turn
        // an attack into a spell or admit melee-only modifiers on a spell. The explicit hit context owns that family.
        var supportTypes = new HashSet<int>(skillTypes ?? ContextTypes(spell, melee, area, projectile));
        supportTypes.Remove(0);
        supportTypes.Remove(1);
        supportTypes.Remove(19);
        if (!secondary) supportTypes.Add(spell ? 1 : 0);
        if (melee) supportTypes.Add(19);
        double common = (double)get(Stat.DamagePlusPct) + (secondary ? 0 : get(spell ? Stat.SpellDamagePlusPct : Stat.AttackDamagePlusPct))
            + (melee ? get(Stat.MeleeDamagePlusPct) : 0) + (area ? get(Stat.AreaDamagePlusPct) : 0)
            + (projectile ? get(Stat.ProjectileDamagePlusPct) : 0)
            + (supportTypes.Contains(64) || get(Stat.SkillGetsBrandDamageCritModifiers) != 0 ? get(Stat.SigilDamagePlusPct) : 0)
            + (spell || secondary ? 0 : DamageStats.SpellAttackIncrease(get, wieldingWand, retaliation))
            + additionalIncreased;
        // Iron Will: Strength's damage bonus applies to spell damage.
        double strength = DamageStats.StrengthDamageBonus(get);
        if (spell && get(Stat.StrongCasting) != 0) common += strength;
        bool lifeLeech = get(Stat.IsLifeLeeching) != 0, manaLeech = get(Stat.IsManaLeeching) != 0,
            shieldLeech = get(Stat.IsEsLeeching) != 0, fullShield = get(Stat.OnFullEnergyShield) != 0;
        bool anyLeech = lifeLeech || manaLeech || shieldLeech;
        common += (anyLeech ? (double)get(Stat.DamagePlusPctWhileLeeching) + (spell || secondary ? 0 : get(Stat.AttackDamagePlusPctWhileLeeching)) : 0)
            + (lifeLeech ? get(Stat.DamagePlusPctWhileLifeLeeching) : 0)
            + (manaLeech ? get(Stat.DamagePlusPctWhileManaLeeching) : 0)
            + (shieldLeech ? get(Stat.DamagePlusPctWhileEsLeeching) : 0)
            + (fullShield ? get(Stat.DamagePlusPctOnFullEnergyShield) : 0);
        bool bowAttack = wieldingBow && !spell && !secondary;
        if (bowAttack) common += (double)get(Stat.BowDamagePlusPct) + get(Stat.BowHitAndAilmentDamagePlusPct);
        bool attack = !spell && !secondary;
        // Precise Technique: "40% more Attack Damage if Accuracy Rating is higher than Maximum Life".
        bool preciseTechnique = attack && get(Stat.KeystonePreciseTechnique) != 0
            && get(Stat.AccuracyRating) > get(Stat.MaximumLife);
        var typed = DamageTypes.ConversionOrder.Select(type =>
            (History: DamageTypes.HistoryOf(type), Increased: get(TypedIncreased[(int)type]))).ToArray();
        int elemental = get(Stat.ElementalDamagePlusPct)
            + (attack ? get(Stat.ElementalDamageWithAttackSkillsPlusPct) : 0), meleePhysical = melee ? get(Stat.MeleePhysicalDamagePlusPct) : 0;
        int physicalAttack = attack ? get(Stat.PhysicalAttackDamagePlusPct) : 0;
        int attackFire = attack ? get(Stat.FireDamageWithAttackSkillsPlusPct) : 0,
            attackCold = attack ? get(Stat.ColdDamageWithAttackSkillsPlusPct) : 0,
            attackLightning = attack ? get(Stat.LightningDamageWithAttackSkillsPlusPct) : 0;
        double charges = ChargeEffects.DamageMore(get, spell);
        double elementalMore = DamageStats.ElementalMore(get(Stat.CombinedElementalDamagePlusPctFinal));
        var result = DamagePacket.Empty;
        foreach (var path in conversion.Trace(raw))
        {
            // Prohibitions constrain the final damage dealt, not a type the portion used to have.
            if (DamageStats.Prohibited(path.Type, get, attack: attack)
                || GemSupportDamage.Prohibited(path.Type, modifiers)) continue;
            double increased = common;
            if (bowAttack && (path.History & DamageTypeHistory.Physical) != 0) increased += get(Stat.PhysicalBowDamagePlusPct);
            if (bowAttack && (path.History & DamageTypeHistory.Elemental) != 0) increased += get(Stat.BowElementalDamagePlusPct);
            foreach (var source in typed)
                if ((path.History & source.History) != 0) increased += source.Increased;
            if (attackFire != 0 && (path.History & DamageTypeHistory.Fire) != 0) increased += attackFire;
            if (attackCold != 0 && (path.History & DamageTypeHistory.Cold) != 0) increased += attackCold;
            if (attackLightning != 0 && (path.History & DamageTypeHistory.Lightning) != 0) increased += attackLightning;
            if ((path.History & DamageTypeHistory.Elemental) != 0) increased += elemental + additional.Elemental;
            if ((path.History & DamageTypeHistory.Physical) != 0)
                increased += (double)physicalAttack + meleePhysical + additional.Physical
                    + (!melee && !spell && projectile && get(Stat.KeystoneStrongBowman) != 0 ? strength : 0)
                    + (lifeLeech ? (double)get(Stat.PhysicalDamagePlusPctWhileLifeLeeching) : 0);
            if (additional.Typed is { } extra)
                foreach (var source in extra)
                    if ((path.History & source.History) != 0) increased += source.Increased;

            // Preserve the existing attack multiply-before-divide projection and spell factor construction.
            // Clamp the complete increased bucket once: a negative original-type subtotal must survive until here.
            var range = spell
                ? path.Range.Scale(Math.Max(0, 1 + increased / 100.0) * (SpellMore(get, path.History) * charges))
                : DamageStats.IncreaseAttackRange(path.Range, increased);
            var support = GemSupportDamage.Multipliers(path.History, modifiers, supportTypes, fullShield, shieldLeech);
            range = range.Scale(support.GeneralOrPhysical).Scale(support.ElementalOrChaos);
            if ((path.History & DamageTypeHistory.Physical) != 0)
            {
                range = range.Scale(Math.Max(0, 1 + get(Stat.HeraldOfPurityPhysicalDamagePlusPctFinal) / 100.0));
                if (attack) range = range.Scale(Math.Max(0, 1 + get(Stat.CombinedAttackPhysicalDamagePlusPctFinal) / 100.0));
                if (melee && additional.MeleePhysicalMorePct != 0)
                    range = range.Scale(Math.Max(0, 1 + additional.MeleePhysicalMorePct / 100d));
            }
            if (melee && area)
                range = range.Scale(Math.Max(0, 1 + get(get(Stat.InSandStance) != 0
                    ? Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinalInSandStance
                    : Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinalInBloodStance) / 100.0));
            if (preciseTechnique) range = range.Scale(1.4);
            // Elemental Overload: "40% more Elemental Damage with Hits and Ailments" while its window is open. The
            // stat is the client's own carrier, published by the live stat view; a converted portion is elemental by
            // its history, as the other elemental buckets are.
            if ((path.History & DamageTypeHistory.Elemental) != 0)
                range = range.Scale(elementalMore * Math.Max(0, 1 + get(Stat.KeystoneElementalOverloadDamagePlusPctFinal) / 100.0));
            if ((path.History & additional.MaximumMore.History) != 0 && additional.MaximumMore.Percent != 0)
                range = range with { Max = Math.Max(range.Min,
                    range.Max * Math.Max(0, 100d + additional.MaximumMore.Percent) / 100d) };
            result = result.Add(path.Type, range, trackPhysicalOrigin && (path.History & DamageTypeHistory.Physical) != 0);
        }
        // WithCharges expands increases and additions, but leaves more multipliers separate. Spell portions retain
        // their existing aura/charge factor grouping before support scaling; attack packets receive the common
        // multiplier here. Both routes apply it once. Rage remains outside this resolver in the attack caller.
        if (skillTypes?.Contains(32) == true || skillTypes?.Contains(35) == true)
            result = result.Scale(Math.Max(0, 1 + get(Stat.SkitterbotsTrapMineDamagePlusPctFinal) / 100.0));
        // Ritual of Awakening: "3% more Damage per Summoned Totem", the count the server sends (Hierophant). Here, not
        // per hit, so the skill's damage stats (its tooltip) carry it too; a totem's hits read its owner's stats.
        if (get(Stat.HierophantPassiveDamagePlusPctFinalPerTotem) is var perTotem and not 0)
            result = result.Scale(Math.Max(0, 1 + (double)perTotem * get(Stat.NumberOfActiveTotems) / 100));
        if (attack && additional.AttackMorePct is int attackMore and not 0)
            result = result.Scale(Math.Max(0, 1 + attackMore / 100d));
        return spell || charges == 1 ? result : result.Scale(charges);
    }

    static double SpellMore(Func<uint, int> get, DamageTypeHistory history) =>
        Math.Max(0, 1 + get(Stat.SpellDamageAuraSpellDamagePlusPctFinal) / 100.0)
        * Math.Max(0, 1 + get(Stat.RighteousFireSpellDamagePlusPctFinal) / 100.0)
        // Arcane Surge's own more Spell Damage (Arcane Blessing), a stat of the buff on the player.
        * Math.Max(0, 1 + get(Stat.SpellDamagePlusPctFinalFromArcaneSurge) / 100.0)
        // Pain Attunement: "30% more Spell Damage when on Low Life".
        * (get(Stat.KeystonePainAttunement) != 0 && get(Stat.OnLowLife) != 0 ? 1.3 : 1)
        * ((history & DamageTypeHistory.Lightning) != 0
            ? Math.Max(0, 1 + get(Stat.WrathAuraSpellLightningDamagePlusPctFinal) / 100.0) : 1);

    static IReadOnlySet<int> ContextTypes(bool spell, bool melee, bool area, bool projectile)
    {
        var types = new HashSet<int> { spell ? 1 : 0 };
        if (melee) types.Add(19);
        if (area) types.Add(7);
        if (projectile) types.Add(2);
        return types;
    }
}

/// <summary>Additional modifiers supplied by the caller. Typed modifiers follow the conversion history so a portion keeps
/// each damage type it inherited.</summary>
public readonly record struct HitIncreases(int Physical = 0, int Elemental = 0,
    IReadOnlyList<(DamageTypeHistory History, int Increased)>? Typed = null)
{
    public int MeleePhysicalMorePct { get; init; }
    public (DamageTypeHistory History, int Percent) MaximumMore { get; init; }
    public int AttackMorePct { get; init; }
}
