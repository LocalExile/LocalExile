using GameServer.Core.World;


/// <summary>
/// The Offence tab's damaging-ailment rows (issue #75): Bleeding, Ignite and Poison damage per second from a hit of
/// one context (main hand, off hand, spell, secondary), from hits and from critical strikes, as the per-minute
/// minimum and maximum the client divides by 60. The client derives none of them (11-skills.md §4).
/// <para>
/// An ailment's base is the hit's damage of the ailment's type(s) after conversion, before the hit-only modifiers:
/// bleed the physical damage (70% per second, <see cref="Ailments"/>), ignite the fire damage (90%), poison the
/// physical and chaos damage (30%). Each converted portion keeps the increases of every type it visited, as the hit
/// itself does (<see cref="PlayerHitDamage"/>), plus the ailment's own buckets: generic damage, damage over time
/// (generic and the ailment type's), "Damage with Ailments", "Bleeding / Burning / Ignite / Poison Damage", the
/// elemental increase for ignite, then the damage-over-time multipliers (generic, the ailment type's, for ailments, the
/// ailment's own, and for critical strikes <c>critical_ailment_dot_multiplier_+</c> and the ailment's: ailments inflicted
/// by critical strikes gain nothing else from the critical multiplier), the Frenzy Charges' more multiplier, the
/// supports' (<see cref="SupportMore"/>) and Crimson Dance's 50% less bleeding.
/// Modifiers to hits (attack, spell, melee, area and projectile damage) never reach an ailment (wiki Ignite and
/// Bleeding); pinned PoB CalcOffence (16de4b82) builds the bleed, poison and ignite configs without those flags.
/// Conditional sources, "as though dealing more" and the moving bleed bonus (a monster's, MonsterBleeds) are not here.
/// </para>
/// The "from only Chaos Damage" poison rows (<c>_to_always_deal_</c>) show the poison the chaos portion alone deals,
/// when a "Chaos Damage has a chance to Poison" source is present.
/// </summary>
public static class AilmentPanelStats
{
    /// <summary>The hit's context: which rows it fills (<paramref name="Hand"/>: main_hand, off_hand, spell,
    /// secondary), whether it is a spell and whether it is a melee hit.</summary>
    public readonly record struct Context(string Hand, bool Spell, bool Melee = false)
    {
        public bool Attack => Hand is "main_hand" or "off_hand";
        public IReadOnlyDictionary<uint, int>? WeaponStats { get; init; }
    }

    public const int BleedPercent = 70, IgnitePercent = 90, PoisonPercent = 30;

    /// <summary>A support's more multiplier that reaches ailments: the damage type the portion or the ailment needs
    /// (<see cref="DamageTypeHistory.None"/>: any), the ailments it is limited to, and whether only ailments from melee
    /// hits or attacks get it.</summary>
    readonly record struct More(uint Stat, DamageTypeHistory Type = DamageTypeHistory.None, string[]? Only = null,
        bool MeleeHit = false, bool Attack = false);

    // PoB 16de4b82 sup_*.lua: the support more multipliers with no Hit, Attack, Spell, Melee, Area or Projectile flag
    // (GemSupportDamage applies those to the hit, with the generic and typed ones here).
    static readonly More[] SupportMore =
    [
        new(Stat.DoedreAuraDamagePlusPctFinal), new(Stat.CastOnDamageTakenDamagePlusPctFinal),
        new(Stat.SupportCastWhileChannellingTriggeredSkillDamagePlusPctFinal),
        new(Stat.SupportDivineCryDamagePlusPctFinal),
        new(Stat.SupportManaforgedArrowsDamagePlusPctFinal),
        new(Stat.SupportEchoDamagePlusPctFinal), new(Stat.SupportReducedDurationDamagePlusPctFinal),
        new(Stat.SupportSpiritStrikeDamagePlusPctFinal), new(Stat.SupportStormBarrierDamagePlusPctFinal),
        new(Stat.SupportPureShockDamagePlusPctFinal), new(Stat.SupportBloodThirstDamagePlusPctFinal),
        new(Stat.SupportSpellCascadeDamagePlusPctFinal), new(Stat.SupportBarrageDamagePlusPctFinal),
        new(Stat.SupportTrapDamagePlusPctFinal), new(Stat.SupportTrapAndMineDamagePlusPctFinal),
        new(Stat.SupportClustertrapDamagePlusPctFinal), new(Stat.SupportMultithrowDamagePlusPctFinal),
        new(Stat.SupportGemMineDamagePlusPctFinal), new(Stat.SupportRemoteMine2DamagePlusPctFinal),
        new(Stat.SupportLocusMineDamagePlusPctFinal), new(Stat.SupportAdditionalTotemDamagePlusPctFinal),
        new(Stat.SupportFocusedBallistaTotemDamagePlusPctFinal),
        new(Stat.SupportBetterAilmentsAilmentDamagePlusPctFinal), new(Stat.SupportUnboundAilmentsAilmentDamagePlusPctFinal),
        new(Stat.SupportEfficacyDamageOverTimePlusPctFinal), new(Stat.SupportRapidDecayDamageOverTimePlusPctFinal),
        new(Stat.SupportBrutalityPhysicalDamagePlusPctFinal, DamageTypeHistory.Physical),
        new(Stat.SupportMaimChancePhysicalDamagePlusPctFinal, DamageTypeHistory.Physical),
        new(Stat.SupportVoidManipulationChaosDamagePlusPctFinal, DamageTypeHistory.Chaos),
        new(Stat.SupportBonechillColdDamagePlusPctFinal, DamageTypeHistory.Cold),
        new(Stat.SupportChanceToIgniteFireDamagePlusPctFinal, DamageTypeHistory.Fire),
        new(Stat.SupportGemElementalDamagePlusPctFinal, DamageTypeHistory.Elemental),
        new(Stat.SupportAttackSkillsElementalDamagePlusPctFinal, DamageTypeHistory.Elemental, Attack: true),
        new(Stat.SupportChanceToBleedBleedingDamagePlusPctFinal, Only: ["bleeding"]),
        new(Stat.SupportPhysChaosProjectilePhysicalDamageOverTimePlusPctFinal, Only: ["bleeding"]),
        new(Stat.SupportDebilitatePoisonDamagePlusPctFinal, Only: ["poison"]),
        new(Stat.SupportLethalDosePoisonDamagePlusPctFinal, Only: ["poison"]),
        new(Stat.SupportPhysChaosProjectileChaosDamageOverTimePlusPctFinal, Only: ["poison"]),
        new(Stat.CrueltyDamageOverTimePlusPctFinal),
        new(Stat.SupportIgniteProlifIgniteDamagePlusPctFinal, Only: ["ignite"]),
        new(Stat.SupportBurningDamagePlusPctFinal, Only: ["ignite"]),
        new(Stat.SupportMeleePhysicalDamagePoisonAndBleedingDamagePlusPctFinalFromMeleeHits, Only: ["bleeding", "poison"],
            MeleeHit: true),
        new(Stat.SupportFortifyAilmentDamagePlusPctFinalFromMeleeHits, MeleeHit: true),
    ];

    public static List<(uint Stat, int Value)> Compute(Context context, DamagePacket raw, DamageConversion conversion,
        Func<uint, int> get, long additionalIncreased = 0) =>
        Compute(context, raw, conversion, get, additionalIncreased, igniteOnly: false);

    public static List<(uint Stat, int Value)> Ignite(Context context, DamagePacket raw, DamageConversion conversion,
        Func<uint, int> get, long additionalIncreased) =>
        Compute(context, raw, conversion, get, additionalIncreased, igniteOnly: true);

    static List<(uint Stat, int Value)> Compute(Context context, DamagePacket raw, DamageConversion conversion,
        Func<uint, int> get, long additionalIncreased, bool igniteOnly)
    {
        get = WeaponAttributeDamage.Read(context.Attack && !context.Spell && WeaponAttributeDamage.Armed(
            get(context.Hand == "off_hand" ? Stat.OffHandWeaponType : Stat.MainHandWeaponType)), get);
        var rows = new List<(uint, int)>();
        var paths = conversion.Trace(raw).ToList();
        double charges = ChargeEffects.DamageMore(get, context.Spell, ailment: true);
        void Add(string ailment, DamageType ailmentType, DamageType[] sources, int percent, bool chaosOnly = false)
        {
            foreach (bool critical in new[] { false, true })
            {
                var (min, max) = Range(paths, ailment, sources, ailmentType, context, get, critical, additionalIncreased);
                string kind = critical ? "critical" : "non_critical";
                string name = $"{ailment}_damage_to_{(chaosOnly ? "always_" : "")}deal_per_minute_from_{kind}_{context.Hand}_damage";
                rows.Add((Stat.Of($"minimum_{name}"), PerMinute(min * percent / 100.0 * charges)));
                rows.Add((Stat.Of($"maximum_{name}"), PerMinute(max * percent / 100.0 * charges)));
            }
        }
        bool tinctureWeapon = context.Attack && !context.Spell && get(Stat.TinctureApplied) != 0;
        if (context.Attack && !igniteOnly) Add("bleeding", DamageType.Physical, [DamageType.Physical], BleedPercent);
        DamageType[] ignite = get(Stat.AllDamageCanIgnite) != 0
            || tinctureWeapon && get(Stat.AllDamageWithTincturedWeaponsCanIgnite) != 0
            ? DamageTypes.ConversionOrder : get(Stat.UniqueColdDamageIgnites) != 0
                ? [DamageType.Fire, DamageType.Cold] : [DamageType.Fire];
        if (get(Stat.UniqueFireDamageShocks) != 0) ignite = [.. ignite.Where(type => type != DamageType.Fire)];
        Add("ignite", DamageType.Fire, ignite, IgnitePercent);
        if (igniteOnly) return rows;
        Add("poison", DamageType.Chaos, get(Stat.AllDamageCanPoison) != 0
            || context.Attack && (context.WeaponStats?.GetValueOrDefault(Stat.LocalAllDamageCanPoison) ?? 0) != 0
            || tinctureWeapon && get(Stat.AllDamageWithTincturedWeaponsCanPoison) != 0
            ? DamageTypes.ConversionOrder : [DamageType.Physical, DamageType.Chaos], PoisonPercent);
        if (get(Stat.ChaosDamageChanceToPoisonPct) != 0)
            Add("poison", DamageType.Chaos, [DamageType.Chaos], PoisonPercent, chaosOnly: true);
        return rows;
    }

    static int PerMinute(double perSecond) =>
        double.IsNaN(perSecond) || perSecond <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(perSecond * 60));

    static (double Min, double Max) Range(List<DamageConversionPath> paths, string ailment, DamageType[] sources,
        DamageType ailmentType, Context context, Func<uint, int> get, bool critical, long additionalIncreased)
    {
        // The ailment deals its own type: a prohibition of that type stops it whatever its base (PoB CalcOffence gates
        // bleed on canDeal.Physical, poison on canDeal.Chaos and ignite on canDeal.Fire).
        if (get(Stat.NeverDamagingAilments) != 0 || DamageStats.Prohibited(ailmentType, get, context.Attack))
            return (0, 0);
        bool tinctureWeapon = context.Attack && !context.Spell && get(Stat.TinctureApplied) != 0;
        double common = (double)get(Stat.DamagePlusPct) + get(Stat.DamageOverTimePlusPct) + get(Stat.BaseAilmentDamagePlusPct)
            + get(Stat.Of(Slug(ailmentType) + "_damage_+%")) + get(Stat.Of(Slug(ailmentType) + "_damage_over_time_+%"))
            + additionalIncreased
            + ailmentType switch
            {
                DamageType.Physical => get(Stat.BleedingDamagePlusPct),
                DamageType.Fire => get(Stat.BurnDamagePlusPct) + get(Stat.IgniteDamagePlusPct) + get(Stat.ElementalDamagePlusPct)
                    + (tinctureWeapon ? get(Stat.IgniteDamagePlusPctWithTincturedWeapons) : 0),
                _ => get(Stat.BasePoisonDamagePlusPct)
                    + (get(Stat.Dexterity) >= 300 ? get(Stat.PoisonDamagePlusPctWithOver300Dexterity) : 0)
                    + (tinctureWeapon ? get(Stat.PoisonDamagePlusPctWithTincturedWeapons) : 0),
            };
        bool attack = context.Attack && !context.Spell;
        bool bowAttack = attack
            && get(context.Hand == "off_hand" ? Stat.OffHandWeaponType : Stat.MainHandWeaponType) == WeaponTypeStats.Bow;
        if (attack) common += get(Stat.AttackAilmentDamagePlusPct);
        if (bowAttack) common += (double)get(Stat.BowDamagePlusPct) + get(Stat.BowAilmentDamagePlusPct)
            + get(Stat.BowHitAndAilmentDamagePlusPct) + get(Stat.DamageOverTimePlusPctWithBowSkills);
        // The tree's "with <weapon type>" rows reach an ailment as they reach the hit (issue #1102): the plain and
        // hit-and-ailment rows, the family's own ailment row, and the two families' shared ailment rows.
        (int weaponAny, HitIncreases weaponExtra) = context.Attack && !context.Spell
            ? WeaponTypeDamage.Ailment(get(context.Hand == "off_hand" ? Stat.OffHandWeaponType : Stat.MainHandWeaponType), get)
            : (0, default);
        if (attack && context.WeaponStats?.GetValueOrDefault(Stat.LocalAttacksWithThisWeaponElementalDamagePlusPct) is not (null or 0) and int localElemental)
            weaponExtra = weaponExtra with { Elemental = ActorScale.Saturate((long)weaponExtra.Elemental + localElemental) };
        common += weaponAny;
        var more = SupportMore.Where(m => (m.Only is null || m.Only.Contains(ailment)) && (!m.MeleeHit || context.Melee)
            && (!m.Attack || context.Attack) && get(m.Stat) != 0).ToList();
        more.Add(new(Stat.DeliriumAuraDamageOverTimePlusPctFinal));
        if (ailment == "poison") more.Add(new(Stat.HeraldOfAgonyPoisonDamagePlusPctFinal));
        if (ailmentType == DamageType.Physical) more.Add(new(Stat.HeraldOfPurityPhysicalDamagePlusPctFinal));
        // Elemental Overload: "Ailments never count as being from Critical Strikes" for as long as the keystone is
        // allocated, so the critical rows lose the critical ailment multipliers (the application side rolls the hit's
        // chance instead of a critical strike's guaranteed one, MonsterIgnites.Settings.NeverFromCrit).
        bool neverFromCrit = get(Stat.KeystoneElementalOverload) != 0;
        // Perfect Agony: "Damage over Time Multiplier for Ailments is equal to Critical Strike Multiplier" — the hit's
        // critical strike multiplier replaces the whole damage-over-time multiplier bucket (generic, typed, ailment and
        // critical bonuses are ignored); the more/less damage factors below still apply. The base critical strike
        // multiplier is 150% plus base_critical_strike_multiplier_+ (the generic bonuses; a weapon's local critical
        // multiplier is not in this merged view).
        bool perfectAgony = get(Stat.KeystoneAilmentCrit) != 0;
        int localDotMultiplier = context.Attack && context.WeaponStats is { } local ? ailment switch
        {
            "bleeding" => local.GetValueOrDefault(Stat.LocalBleedingAilmentDotMultiplierPlus),
            "poison" => local.GetValueOrDefault(Stat.LocalPoisonDotMultiplierPlus),
            _ => 0,
        } : 0;
        double dotMultiplier = perfectAgony
            ? Math.Max(0, (150 + get(Stat.BaseCriticalStrikeMultiplierPlus)
                + (tinctureWeapon ? get(Stat.CriticalStrikeMultiplierPlusWithTincturedWeapons) : 0)) / 100.0)
            : Math.Max(0, 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.Of(Slug(ailmentType) + "_dot_multiplier_+"))
                + get(Stat.AilmentDotMultiplierPlus) + get(Stat.Of(ailment + "_dot_multiplier_+")) + localDotMultiplier
                + (ailmentType == DamageType.Fire && context.Attack ? get(Stat.FireDamageOverTimeMultiplierPlusWithAttacks) : 0)
                + (context.Spell && ailment == "poison" ? get(Stat.PoisonDotMultiplierPlusWithSpells) : 0)
                + (bowAttack ? get(Stat.DotMultiplierPlusWithBowSkills) : 0)
                + (tinctureWeapon ? get(Stat.DamageOverTimeMultiplierPlusWithTincturedWeapons) : 0)
                + (critical && !neverFromCrit ? get(Stat.CriticalAilmentDotMultiplierPlus) + get(Stat.Of("critical_" + ailment + "_dot_multiplier_+")) : 0)) / 100);
        int localIgniteMore = attack && ailment == "ignite"
            ? context.WeaponStats?.GetValueOrDefault(Stat.LocalIgniteDamagePlusPctFinalWithThisWeapon) ?? 0 : 0;
        double multiplier = dotMultiplier * Math.Max(0, 1 + localIgniteMore / 100d)
            * (ailment == "poison" && critical && !neverFromCrit
                ? Math.Max(0, 1 + get(Stat.PoisonFromCriticalStrikesDamagePlusPctFinal) / 100d) : 1)
            // Burning Arrow's quality: 1% more ignite damage per quality (GrantedEffectQualityStats).
            * (ailmentType == DamageType.Fire ? Math.Max(0, 1 + get(Stat.ActiveSkillIgniteDamagePlusPctFinal) / 100.0) : 1)
            // Puncture's "49% more Damage with Bleeding".
            * (ailment == "bleeding" ? Math.Max(0, 1 + get(Stat.ActiveSkillBleedingDamagePlusPctFinal) / 100.0)
                * Math.Max(0, 1 + get(Stat.PunctureBleedingDamagePlusPctFinal) / 100.0) : 1)
            // Crimson Dance: "50% less Damage with Bleeding" (ReminderTextKeystoneCrimsonDance).
            * (ailment == "bleeding" && get(Stat.KeystoneCrimsonDance) != 0 ? .5 : 1);
        var typed = DamageTypes.ConversionOrder.Select(type => (History: DamageTypes.HistoryOf(type), Increased: get(Stat.Of(Slug(type) + "_damage_+%")))).ToArray();
        int elemental = get(Stat.ElementalDamagePlusPct);
        int attackFire = attack ? get(Stat.FireDamageWithAttackSkillsPlusPct) : 0,
            attackCold = attack ? get(Stat.ColdDamageWithAttackSkillsPlusPct) : 0,
            attackLightning = attack ? get(Stat.LightningDamageWithAttackSkillsPlusPct) : 0;
        double min = 0, max = 0;
        foreach (var path in paths)
        {
            if (!sources.Contains(path.Type)) continue;
            // The hit's prohibitions (PlayerHitDamage): a portion the hit cannot deal contributes no ailment base.
            if (DamageStats.Prohibited(path.Type, get, context.Attack)) continue;
            double increased = common;
            if (bowAttack && (path.History & DamageTypeHistory.Physical) != 0) increased += get(Stat.PhysicalBowDamagePlusPct);
            if (bowAttack && ((path.History | DamageTypes.HistoryOf(ailmentType)) & DamageTypeHistory.Elemental) != 0)
                increased += get(Stat.BowElementalDamagePlusPct);
            if ((path.History & DamageTypeHistory.Physical) != 0) increased += weaponExtra.Physical;
            if (((path.History | DamageTypes.HistoryOf(ailmentType)) & DamageTypeHistory.Elemental) != 0)
                increased += weaponExtra.Elemental;
            if (weaponExtra.Typed is { } weaponTyped)
                foreach (var source in weaponTyped)
                    if ((path.History & source.History) != 0) increased += source.Increased;
            // The types the portion visited count once each; the ailment's own type is already in the common bucket.
            foreach (var source in typed)
                if ((path.History & source.History) != 0 && source.History != DamageTypes.HistoryOf(ailmentType)) increased += source.Increased;
            if ((path.History & DamageTypeHistory.Elemental) != 0 && ailmentType != DamageType.Fire) increased += elemental;
            // A typed support counts the portion's visited types and the ailment's own (PoB's typeFlags).
            var types = path.History | DamageTypes.HistoryOf(ailmentType);
            if (attackFire != 0 && (types & DamageTypeHistory.Fire) != 0) increased += attackFire;
            if (attackCold != 0 && (types & DamageTypeHistory.Cold) != 0) increased += attackCold;
            if (attackLightning != 0 && (types & DamageTypeHistory.Lightning) != 0) increased += attackLightning;
            double factor = Math.Max(0, 1 + increased / 100) * multiplier * more
                .Where(m => m.Type == DamageTypeHistory.None || (types & m.Type) != 0)
                .Aggregate(1.0, (v, m) => v * Math.Max(0, 1 + get(m.Stat) / 100.0));
            // Elemental Overload's window reaches an elemental ailment (ignite) as it reaches the hit; bleed and poison
            // deal physical and chaos damage, whatever their portion converted from.
            if ((DamageTypes.HistoryOf(ailmentType) & DamageTypeHistory.Elemental) != 0)
                factor *= DamageStats.ElementalMore(get(Stat.CombinedElementalDamagePlusPctFinal))
                    * Math.Max(0, 1 + get(Stat.KeystoneElementalOverloadDamagePlusPctFinal) / 100.0);
            min += path.Range.Min * factor;
            max += path.Range.Max * factor;
        }
        return (min, max);
    }

    static string Slug(DamageType type) => type.ToString().ToLowerInvariant();

    /// <summary>Every row name of a context, for callers that clear the rows of a hand that is no longer swung.</summary>
    public static IEnumerable<uint> Rows(string hand)
    {
        foreach (string ailment in new[] { "bleeding", "ignite", "poison" })
        {
            if (ailment == "bleeding" && hand is not ("main_hand" or "off_hand")) continue;
            foreach (string kind in new[] { "non_critical", "critical" })
                foreach (string bound in new[] { "minimum", "maximum" })
                {
                    yield return Stat.Of($"{bound}_{ailment}_damage_to_deal_per_minute_from_{kind}_{hand}_damage");
                    if (ailment == "poison") yield return Stat.Of($"{bound}_poison_damage_to_always_deal_per_minute_from_{kind}_{hand}_damage");
                }
        }
    }
}
