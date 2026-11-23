using GameServer.Core.World;

/// <summary>
/// The character panel's "sum" stats (issue #75): virtual stats that the server works out from the base forms that
/// passives, items and auras give, because the client derives none of them (11-skills.md §4). A passive grants
/// <c>base_stun_recovery_+%</c>, the panel's Stun Recovery row reads <c>stun_recovery_+%</c>. Each display stat is its
/// own value plus every unconditional source below: the base form, and the combined stats that apply to it too (Avoid
/// Elemental Ailments counts for each avoid row, "increased Duration of Elemental Ailments" for each duration).
/// Main hand / off hand rows also add that weapon's local stats (its own chance to bleed, penetration, life gain on hit) and,
/// while the hand holds a weapon, the "with Weapons" penetration.
/// Conditional stats (while, if, per charge, against …) are not counted. Flags (cannot be, immune to) are 1 when any
/// source is set. The pools' own stats (life, mana, armour, evasion, attributes, resistances, suppression, block,
/// movement and attack speed, crit) are not here: <see cref="CharacterStats"/> computes those, and
/// <see cref="FromPools"/> adds the rows that combine them with a stat.
/// </summary>
public static class PanelStats
{
    /// <summary>A source stat, scaled: × <paramref name="Mul"/> ÷ <paramref name="Div"/> (a "-%" stat counts negative;
    /// a 1/100 000 leech counts ÷ 10 as permyriad). Player-level sources multiply before integer division.</summary>
    public readonly record struct Src(uint Name, int Mul = 1, int Div = 1, bool PerPlayerLevel = false)
    {
        public static implicit operator Src(uint name) => new(name);
    }

    /// <summary>A display stat and what it sums; <paramref name="Local"/> are the weapon's own stats (hand rows), and
    /// <paramref name="Armed"/> the character's stats that count only while that hand holds a weapon ("with Weapons").</summary>
    public sealed record Sum(uint Display, Src[] Sources, bool Flag = false, Src[]? Local = null, Hand Hand = Hand.None,
        Src[]? Armed = null);

    public enum Hand { None, Main, Off }

    const uint ElementalAilmentDuration = Stat.BaseElementalStatusAilmentDurationPlusPct;
    const uint AilmentDuration = Stat.BaseAllAilmentDurationPlusPct;
    const uint NonDamagingAilmentEffect = Stat.NonDamagingAilmentEffectPlusPct;
    const uint AvoidElementalAilments = Stat.AvoidAllElementalStatusPct;
    const uint SelfElementalAilmentDuration = Stat.SelfElementalStatusDurationMinusPct;
    const uint ChargeDuration = Stat.ChargeDurationPlusPct, BaseChargeDuration = Stat.BaseChargeDurationPlusPct;
    const uint LeechSpeed = Stat.LifeManaEsLeechSpeedPlusPct;
    const uint ElementalPenetration = Stat.ReduceEnemyElementalResistancePct, PenetrateElemental = Stat.BasePenetrateElementalResistancesPct;
    const uint WeaponElementalPenetration = Stat.ReduceEnemyElementalResistanceWithWeaponsPct;
    const uint IgniteChance = Stat.BaseChanceToIgnitePct, FreezeShockIgniteChance = Stat.ChanceToFreezeShockIgnitePct;
    const uint ProjectileBleed = Stat.ProjectileAttacksChanceToBleedOnHitPctIfYouHaveBeastMinion;
    const uint ProjectilePoison = Stat.ProjectileAttacksChanceToPoisonOnHitPctIfYouHaveBeastMinion;
    const uint ProjectileMaim = Stat.ProjectileAttacksChanceToMaimOnHitPctIfYouHaveBeastMinion;

    static readonly Src[] GlobalFlaskDurationSources =
        [Stat.FlaskDurationPlusPct, new(Stat.FlaskDurationPlusPctPerLevel, PerPlayerLevel: true)];

    public static readonly Sum[] Sums =
    [
        // Offence
        // arcane_surge_cast_speed_+%: the Arcane Surge buff's increased cast speed, present only while it runs.
        new(Stat.CastSpeedPlusPct, [Stat.BaseCastSpeedPlusPct, Stat.AttackAndCastSpeedPlusPct, Stat.ArcaneSurgeCastSpeedPlusPct]),
        new(Stat.GlobalChanceToKnockbackPct, [Stat.BaseGlobalChanceToKnockbackPct]),
        new(Stat.VirtualKnockbackDistancePlusPct, [Stat.KnockbackDistancePlusPct]),
        new(Stat.StunDurationPlusPct, [Stat.BaseStunDurationPlusPct]),
        new(Stat.VirtualChanceToBlindOnHitPct, [Stat.GlobalChanceToBlindOnHitPct]),
        new(Stat.VirtualIgniteDurationPlusPct, [Stat.IgniteDurationPlusPct, ElementalAilmentDuration, AilmentDuration,
            Stat.FireAilmentDurationPlusPct, Stat.SkillEffectAndDamagingAilmentDurationPlusPct]),
        new(Stat.VirtualScorchDurationPlusPct, [ElementalAilmentDuration, AilmentDuration, Stat.FireAilmentDurationPlusPct]),
        new(Stat.VirtualScorchEffectPlusPct, [Stat.ScorchEffectPlusPct, NonDamagingAilmentEffect]),
        new(Stat.VirtualChillDurationPlusPct, [Stat.ChillDurationPlusPct, Stat.ChillAndFreezeDurationPlusPct, ElementalAilmentDuration,
            AilmentDuration, Stat.ColdAilmentDurationPlusPct]),
        new(Stat.VirtualChillEffectPlusPct, [Stat.ChillEffectPlusPct, Stat.ColdAilmentEffectPlusPct, NonDamagingAilmentEffect]),
        new(Stat.ChanceToFreezePct, [Stat.BaseChanceToFreezePct, FreezeShockIgniteChance]),
        new(Stat.VirtualFreezeDurationPlusPct, [Stat.FreezeDurationPlusPct, Stat.ChillAndFreezeDurationPlusPct, ElementalAilmentDuration,
            AilmentDuration, Stat.ColdAilmentDurationPlusPct]),
        new(Stat.VirtualFrostburnDurationPlusPct, [ElementalAilmentDuration, AilmentDuration, Stat.ColdAilmentDurationPlusPct]),
        new(Stat.VirtualFrostburnEffectPlusPct, [Stat.ColdAilmentEffectPlusPct, NonDamagingAilmentEffect]),
        new(Stat.ChanceToShockPct, [Stat.BaseChanceToShockPct, FreezeShockIgniteChance]),
        new(Stat.VirtualShockDurationPlusPct, [Stat.ShockDurationPlusPct, ElementalAilmentDuration, AilmentDuration,
            Stat.LightningAilmentDurationPlusPct]),
        new(Stat.VirtualShockEffectPlusPct, [Stat.ShockEffectPlusPct, Stat.LightningAilmentEffectPlusPct, NonDamagingAilmentEffect]),
        new(Stat.VirtualSapDurationPlusPct, [ElementalAilmentDuration, AilmentDuration, Stat.LightningAilmentDurationPlusPct]),
        new(Stat.VirtualSapEffectPlusPct, [Stat.LightningAilmentEffectPlusPct, NonDamagingAilmentEffect]),
        // Spells and secondary damage (no weapon)
        new(Stat.VirtualTotalSpellChanceToIgniteOnHitPct, [IgniteChance, FreezeShockIgniteChance]),
        new(Stat.VirtualTotalSecondaryChanceToIgniteOnHitPct, [IgniteChance, FreezeShockIgniteChance]),
        new(Stat.VirtualTotalSpellChanceToPoisonOnHitPct, [Stat.BaseChanceToPoisonOnHitPct, Stat.SpellsChanceToPoisonOnHitPct]),
        new(Stat.VirtualTotalSecondaryChanceToPoisonOnHitPct, [Stat.BaseChanceToPoisonOnHitPct]),
        new(Stat.VirtualTotalSpellImpaleOnHitPctChance, [Stat.SpellsImpaleOnHitPctChance, Stat.ImpaleOnHitPctChance]),
        new(Stat.ReduceEnemyFireResistancePct, [Stat.BaseReduceEnemyFireResistancePct, ElementalPenetration, PenetrateElemental]),
        new(Stat.ReduceEnemyColdResistancePct, [Stat.BaseReduceEnemyColdResistancePct, ElementalPenetration, PenetrateElemental]),
        new(Stat.ReduceEnemyLightningResistancePct, [Stat.BaseReduceEnemyLightningResistancePct, ElementalPenetration, PenetrateElemental]),
        // Damage immunities (the Defence tab's "Immune to X" rows). A source sets the base form; the elemental
        // immunity covers fire, cold and lightning, and chaos keeps its own older name (chaos_immunity). The display
        // stat itself counts too (Compute adds get(display)), so a mod that sets fire_damage_immunity directly works.
        new(Stat.FireDamageImmunity, [Stat.BaseFireImmunity, Stat.ElementalDamageImmunity], Flag: true),
        new(Stat.ColdDamageImmunity, [Stat.BaseColdImmunity, Stat.ElementalDamageImmunity], Flag: true),
        new(Stat.LightningDamageImmunity, [Stat.BaseLightningImmunity, Stat.ElementalDamageImmunity], Flag: true),
        new(Stat.ChaosDamageImmunity, [Stat.ChaosImmunity, Stat.KeystoneChaosInoculation], Flag: true),
        new(Stat.PhysicalImmunity, [], Flag: true),
        // Main hand and off hand: the global stats and that weapon's own
        .. Hands(h => new(Stat.Of($"{h}_chance_to_blind_on_hit_%"), [Stat.GlobalChanceToBlindOnHitPct, Stat.AttacksChanceToBlindOnHitPct],
            Local: [Stat.LocalChanceToBlindOnHitPct])),
        .. Hands(h => new(Stat.Of($"virtual_total_{h}_chance_to_bleed_on_hit_%"), [Stat.BleedOnHitWithAttacksPct],
            Local: [Stat.LocalChanceToBleedOnHitPct, new(Stat.LocalChanceToBleedOnHit25Pct, Mul: 25),
                new(Stat.LocalChanceToBleedOnHit50Pct, Mul: 50)])),
        .. Hands(h => new(Stat.Of($"virtual_total_{h}_chance_to_ignite_on_hit_%"), [IgniteChance, FreezeShockIgniteChance])),
        .. Hands(h => new(Stat.Of($"virtual_total_{h}_chance_to_poison_on_hit_%"),
            [Stat.BaseChanceToPoisonOnHitPct, Stat.ChanceToPoisonOnHitWithAttacksPct], Local: [Stat.LocalPoisonOnHitPct])),
        .. Hands(h => new(Stat.Of($"{h}_maim_on_hit_%"), [Stat.MaimOnHitPct])),
        .. Hands(h => new(Stat.Of($"virtual_total_{h}_impale_on_hit_%_chance"), [Stat.AttacksImpaleOnHitPctChance, Stat.ImpaleOnHitPctChance],
            Local: [Stat.LocalAttacksImpaleOnHitPctChance])),
        .. Hands(h => new(Stat.Of($"{h}_attack_damage_penetrates_fire_resist_%"), [Stat.BaseReduceEnemyFireResistancePct, ElementalPenetration,
            PenetrateElemental, Stat.BaseAttackDamagePenetratesFireResistPct, Stat.BaseAttackDamagePenetratesElementalResistPct],
            Local: [Stat.LocalFirePenetrationPct, Stat.LocalElementalPenetrationPct],
            Armed: [Stat.ReduceEnemyFireResistanceWithWeaponsPct, WeaponElementalPenetration])),
        .. Hands(h => new(Stat.Of($"{h}_attack_damage_penetrates_cold_resist_%"), [Stat.BaseReduceEnemyColdResistancePct, ElementalPenetration,
            PenetrateElemental, Stat.BaseAttackDamagePenetratesColdResistPct, Stat.BaseAttackDamagePenetratesElementalResistPct],
            Local: [Stat.LocalColdPenetrationPct, Stat.LocalElementalPenetrationPct],
            Armed: [Stat.ReduceEnemyColdResistanceWithWeaponsPct, WeaponElementalPenetration])),
        .. Hands(h => new(Stat.Of($"{h}_attack_damage_penetrates_lightning_resist_%"), [Stat.BaseReduceEnemyLightningResistancePct,
            ElementalPenetration, PenetrateElemental, Stat.BaseAttackDamagePenetratesLightningResistPct,
            Stat.BaseAttackDamagePenetratesElementalResistPct], Local: [Stat.LocalLightningPenetrationPct, Stat.LocalElementalPenetrationPct],
            Armed: [Stat.ReduceEnemyLightningResistanceWithWeaponsPct, WeaponElementalPenetration])),
        .. Hands(h => new(Stat.Of($"{h}_attack_damage_penetrates_chaos_resist_%"), [Stat.ReduceEnemyChaosResistancePct,
            Stat.BaseAttackDamagePenetratesChaosResistPct],
            Local: [Stat.LocalChaosPenetrationPct])),
        .. Hands(h => new(Stat.Of($"{h}_stun_threshold_reduction_+%"), [Stat.BaseStunThresholdReductionPlusPct],
            Local: [Stat.LocalStunThresholdReductionPlusPct])),
        .. Hands(h => new(Stat.Of($"{h}_chance_to_knockback_%"), [Stat.BaseGlobalChanceToKnockbackPct])),
        .. Hands(h => new(Stat.Of($"{h}_knockback_on_crit"), [Stat.GlobalKnockbackOnCrit], Flag: true)),
        .. Hands(h => new(Stat.Of($"{h}_hit_causes_monster_flee_%"), [Stat.GlobalHitCausesMonsterFleePct])),
        .. Hands(h => new(Stat.Of($"{h}_reduce_enemy_block_%"), [Stat.GlobalReduceEnemyBlockPct])),
        .. Hands(h => new(Stat.Of($"{h}_double_damage_to_chilled_enemies"), [], Flag: true, Local: [Stat.LocalDoubleDamageToChilledEnemies])),
        .. Hands(h => new(Stat.Of($"{h}_local_life_gain_per_target"), [Stat.BaseLifeGainPerTarget], Local: [Stat.LocalLifeGainPerTarget])),
        .. Hands(h => new(Stat.Of($"{h}_local_mana_gain_per_target"), [Stat.ManaGainPerTarget], Local: [Stat.LocalManaGainPerTarget])),
        .. HandLeech("life"), .. HandLeech("mana"),
        new(Stat.VirtualLeechEnergyShieldInsteadOfLife, [Stat.LeechEnergyShieldInsteadOfLife, Stat.KeystoneGhostReaver], Flag: true),
        // Eldritch Battery: network stats run no calculator, so without these the client spent its Mana on costs and drew
        // no Energy Shield over the Mana globe.
        new(Stat.VirtualSpendEnergyShieldForCostsBeforeMana, [Stat.SpendEnergyShieldForCostsBeforeMana, Stat.KeystoneEldritchBattery], Flag: true),
        new(Stat.VirtualEnergyShieldProtectsMana, [Stat.EnergyShieldProtectsMana, Stat.KeystoneEldritchBattery], Flag: true),
        new(Stat.VirtualLifeLeechDoesNotStopAtFullLife, [Stat.LifeLeechDoesNotStopAtFullLife], Flag: true),
        // Defence
        new(Stat.VirtualEnergyShieldDelayMinusPct, [Stat.EnergyShieldDelayMinusPct]),
        new(Stat.VirtualBlockRecoveryPlusPct, [Stat.BlockRecoveryPlusPct, Stat.BaseStunRecoveryPlusPct, Stat.StunRecoveryPlusPct]),
        new(Stat.StunRecoveryPlusPct, [Stat.BaseStunRecoveryPlusPct]),
        new(Stat.AvoidStunPct, [Stat.BaseAvoidStunPct]),
        new(Stat.CannotBeStunned, [Stat.BaseCannotBeStunned], Flag: true),
        new(Stat.ExtraDamageTakenFromCritPlusPct, [new(Stat.BaseSelfCriticalStrikeMultiplierMinusPct, Mul: -1)]),
        new(Stat.AvoidBleedPct, [Stat.BaseAvoidBleedPct]),
        new(Stat.CannotGainBleeding, [Stat.BaseCannotGainBleeding], Flag: true),
        new(Stat.VirtualImmuneToBleeding, [Stat.ImmuneToBleeding], Flag: true),
        new(Stat.CombinedBleedingDurationOnSelfPlusPct, [Stat.SelfBleedDurationPlusPct]),
        .. Ailment("ignite", "ignited"), .. Ailment("chill", "chilled"), .. Ailment("freeze", "frozen"), .. Ailment("shock", "shocked"),
        new(Stat.AvoidPoisonPct, [Stat.BaseAvoidPoisonPct]),
        new(Stat.VirtualCannotBePoisoned, [Stat.CannotBePoisoned], Flag: true),
        new(Stat.VirtualImmuneToPoison, [Stat.ImmuneToPoison], Flag: true),
        new(Stat.CombinedPoisonDurationOnSelfPlusPct, [Stat.SelfPoisonDurationPlusPct]),
        .. new[] { "physical", "fire", "cold", "lightning", "chaos" }.SelectMany(type => new Sum[]
        {
            new(Stat.Of($"minimum_{type}_damage_to_return_to_melee_attacker"), [Stat.Of($"{type}_damage_to_return_to_melee_attacker")]),
            new(Stat.Of($"maximum_{type}_damage_to_return_to_melee_attacker"), [Stat.Of($"{type}_damage_to_return_to_melee_attacker")]),
        }),
        new(Stat.IntermediaryCombinedActionSpeedPlusPct, [new(Stat.ActionSpeedMinusPct, Mul: -1)]),
        // "Cannot be Stunned by Spells / Attacks": raw flags of their own (Stormshroud-style sources set them directly);
        // the all-stun cannot_be_stunned row stays separate, as the panel reads both.
        new(Stat.CannotBeStunnedBySpells, [], Flag: true),
        new(Stat.CannotBeStunnedByAttacks, [], Flag: true),
        // Misc
        new(Stat.Ascendance, [Stat.BaseAscendance]),
        new(Stat.SkillAreaOfEffectPlusPct, [Stat.BaseSkillAreaOfEffectPlusPct]),
        new(Stat.VirtualCurseEffectPlusPct, [Stat.CurseEffectPlusPct]),
        new(Stat.VirtualFlaskDurationPlusPct, GlobalFlaskDurationSources),
        .. Leech("life"), .. Leech("mana"), .. Leech("energy_shield"),
        new(Stat.LifeGainedOnSpellHit, [Stat.BaseLifeGainedOnSpellHit]),
        new(Stat.VirtualEnergyShieldGainPerTarget, [Stat.EnergyShieldGainPerTarget]),
        new(Stat.LifeGainedOnEnemyDeath, [Stat.BaseLifeGainedOnEnemyDeath,
            new(Stat.LifeGainedOnEnemyDeathPerLevel, PerPlayerLevel: true)]),
        new(Stat.ManaGainedOnEnemyDeath, [Stat.BaseManaGainedOnEnemyDeath,
            new(Stat.ManaGainedOnEnemyDeathPerLevel, PerPlayerLevel: true)]),
        new(Stat.EnergyShieldGainedOnEnemyDeath, [Stat.BaseEnergyShieldGainedOnEnemyDeath,
            new(Stat.EnergyShieldGainedOnEnemyDeathPerLevel, PerPlayerLevel: true)]),
        new(Stat.VirtualMinionDamagePlusPct, [Stat.MinionDamagePlusPct]),
        new(Stat.ProjectileSpeedPlusPct, [Stat.BaseProjectileSpeedPlusPct]),
        new(Stat.DisplaySkillIncreasedItemRarityPlusPct, [Stat.BaseItemFoundRarityPlusPct, Stat.ItemFoundRarityPlusPct,
            Stat.BaseKilledMonsterDroppedItemRarityPlusPct]),
        new(Stat.DisplaySkillIncreasedItemQuantityPlusPct, [Stat.BaseItemFoundQuantityPlusPct, Stat.ItemFoundQuantityPlusPct,
            Stat.BaseKilledMonsterDroppedItemQuantityPlusPct]),
        new(Stat.VirtualTrapThrowingSpeedPlusPct, [Stat.TrapThrowingSpeedPlusPct, Stat.TrapAndMineThrowingSpeedPlusPct]),
        new(Stat.VirtualMineThrowingSpeedPlusPct, [Stat.MineLayingSpeedPlusPct, Stat.TrapAndMineThrowingSpeedPlusPct]),
        new(Stat.VirtualCooldownSpeedPlusPct, [Stat.BaseCooldownSpeedPlusPct]),
        // "Attached Brand Limit per Target": the brand skill's own base (Storm Brand grants 1) plus "an additional brand
        // attached to an enemy" sources; a selected brand sums both (Selected), the character alone shows its extras.
        new(Stat.BrandsPerTargetLimit, [Stat.BaseNumberOfSigilsAllowedPerTarget]),
        // Charges
        new(Stat.VirtualEnduranceChargeDurationPlusPct, [Stat.EnduranceChargeDurationPlusPct, ChargeDuration, BaseChargeDuration]),
        new(Stat.VirtualFrenzyChargeDurationPlusPct, [Stat.BaseFrenzyChargeDurationPlusPct, ChargeDuration, BaseChargeDuration]),
        new(Stat.VirtualPowerChargeDurationPlusPct, [Stat.PowerChargeDurationPlusPct, ChargeDuration, BaseChargeDuration]),
        new(Stat.AttackSpeedPlusPctPerFrenzyCharge, [Stat.BaseAttackSpeedPlusPctPerFrenzyCharge, Stat.AttackAndCastSpeedPlusPctPerFrenzyCharge]),
        new(Stat.CastSpeedPlusPctPerFrenzyCharge, [Stat.BaseCastSpeedPlusPctPerFrenzyCharge, Stat.AttackAndCastSpeedPlusPctPerFrenzyCharge]),
        // Spirit maximum sources and Siphoning's per-charge display coefficient. The Siphoning owner publishes its
        // count and equipment-qualified maximum separately; this display coefficient is not a degeneration rate.
        new(Stat.VirtualMaximumSpiritCharges, [Stat.MaximumSpiritCharges]),
        new(Stat.VirtualPhysicalDamageTakenPerMinutePerSiphoningCharge,
            [Stat.PhysicalDamageTakenPerMinutePerSiphoningChargeIfHaveUsedASkillRecently]),
    ];

    // A row for each hand: main_hand_… with the main hand weapon's local stats, off_hand_… with the off hand's.
    static Sum[] Hands(Func<string, Sum> row) =>
        [row("main_hand") with { Hand = Hand.Main }, row("off_hand") with { Hand = Hand.Off }];

    // An elemental ailment's defence rows: avoid (with Avoid Elemental Ailments), the duration on self (with the elemental
    // ailment duration on self), cannot be (flag) and immune (flag; "Immune to Ignite and Shock" counts for both).
    static Sum[] Ailment(string ailment, string past) =>
    [
        new(Stat.Of($"avoid_{ailment}_%"), [Stat.Of($"base_avoid_{ailment}_%"), AvoidElementalAilments]),
        new(Stat.Of($"self_{ailment}_duration_-%"), [Stat.Of($"base_self_{ailment}_duration_-%"), SelfElementalAilmentDuration,
            new(Stat.BaseAllAilmentDurationOnSelfPlusPct, Mul: -1)]),
        new(Stat.Of($"cannot_be_{past}"), [Stat.Of($"base_cannot_be_{past}")], Flag: true),
        new(Stat.Of($"immune_to_{ailment}"), ailment is "ignite" or "shock"
            ? [Stat.Of($"base_immune_to_{ailment}"), Stat.ImmuneToIgniteAndShock] : [Stat.Of($"base_immune_to_{ailment}")], Flag: true),
    ];

    // Raw sources only: feeding another derived alias into these sums would count general sources twice.
    static Src[] LeechSources(string pool, string type) =>
    [
        Stat.Of($"{pool}_leech_from_any_damage_permyriad"), Stat.Of($"base_{pool}_leech_from_{type}_damage_permyriad"),
        .. type is "fire" or "cold" or "lightning" ? new Src[] { Stat.Of($"base_{pool}_leech_from_elemental_damage_permyriad") } : [],
        .. pool == "life" && type != "spell" ? new Src[] { new(Stat.Of($"horrible_ugly_life_leech_from_{type}_damage_perhundredthousand"), Div: 10) } : [],
    ];

    static Sum[] HandLeech(string pool) => Hands(h => new(Stat.Of($"{h}_{pool}_leech_from_physical_attack_damage_permyriad"),
        [.. LeechSources(pool, "physical"), Stat.Of($"{pool}_leech_from_physical_attack_damage_permyriad"),
            Stat.LifeAndManaLeechFromPhysicalDamagePermyriad,
            Stat.Of($"base_{pool}_leech_from_attack_damage_permyriad"),
            .. pool == "mana" ? new Src[] { Stat.AttackSkillManaLeechFromAnyDamagePermyriad } : []],
        Local: [Stat.Of($"local_{pool}_leech_from_physical_damage_permyriad"), Stat.LocalLifeAndManaLeechFromPhysicalDamagePermyriad,
            Stat.Of($"local_{pool}_leech_from_any_damage_permyriad")]));

    // Each displayed scope includes applicable general sources; callers must not add these overlapping totals.
    static Sum[] Leech(string pool) =>
    [
        .. new[] { "spell", "physical", "fire", "cold", "lightning", "chaos" }.Select(type => new Sum(
            Stat.Of($"{(pool == "energy_shield" ? "virtual_" : "")}{pool}_leech_from_{type}_damage_permyriad"), LeechSources(pool, type))),
        .. pool != "energy_shield" ? new Sum[] { new(Stat.Of($"virtual_{pool}_leech_from_attack_damage_permyriad"),
            [Stat.Of($"{pool}_leech_from_any_damage_permyriad"), Stat.Of($"base_{pool}_leech_from_attack_damage_permyriad"),
                .. pool == "mana" ? new Src[] { Stat.AttackSkillManaLeechFromAnyDamagePermyriad } : []]) } : [],
        new(Stat.Of($"virtual_{pool}_leech_speed_+%"), [Stat.Of($"{pool}_leech_speed_+%"), LeechSpeed]),
    ];

    /// <summary>Every display stat with a non-zero value, from the stats as the server holds them (<paramref name="get"/>:
    /// by Stats.dat Id, 0 when absent) and each hand's local weapon stats, and whether each hand holds a weapon.</summary>
    public static List<(uint Stat, int Value)> Compute(Func<uint, int> get,
        Func<uint, int>? mainHand = null, Func<uint, int>? offHand = null, int playerLevel = 1,
        bool mainArmed = false, bool offArmed = false, bool mainProjectileAttack = false,
        bool offProjectileAttack = false)
    {
        playerLevel = Math.Clamp(playerLevel, 1, Progression.MaxLevel);
        var result = new List<(uint, int)>();
        foreach (var sum in Sums)
        {
            var local = sum.Hand switch { Hand.Main => mainHand, Hand.Off => offHand, _ => null };
            bool blocked = Penetration.IsElementalDisplay(sum.Display) && get(Penetration.ElementalProhibition) != 0;
            bool armed = sum.Hand switch { Hand.Main => mainArmed, Hand.Off => offArmed, _ => false };
            long raw = RawSum(sum, get, local, playerLevel) + (armed ? Total(sum.Armed, get, playerLevel) : 0)
                + QualifiedProjectileChance(sum, get, mainProjectileAttack, offProjectileAttack);
            int value = blocked ? 0 : Project(sum, BleedFlags(sum, raw, name => get(name), local));
            if (value != 0 || blocked) result.Add((sum.Display, value));
        }
        return result;
    }

    /// <summary>One display stat's sum as <see cref="Compute"/> gives it, from raw stats (a skill's own); a stat with no
    /// row is its own value.</summary>
    public static int Of(uint display, Func<uint, int> get) =>
        Sums.FirstOrDefault(s => s.Display == display && s.Hand == Hand.None && !s.Flag) is { } sum
            ? (int)Math.Clamp(get(display) + sum.Sources.Sum(s => (long)get(s.Name) * s.Mul / s.Div), int.MinValue, int.MaxValue)
            : get(display);
    static long RawSum(Sum sum, Func<uint, int> get, Func<uint, int>? local, int level) =>
        get(sum.Display) + Total(sum.Sources, get, level) + Total(sum.Local, local, level);

    static int Project(Sum sum, long value) => sum.Flag ? value > 0 ? 1 : 0
        : ImpaleStats.IsDisplay(sum.Display) || IsAilmentChanceDisplay(sum.Display) ? (int)Math.Clamp(value, 0, 100)
        : (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    static bool IsAilmentChanceDisplay(uint display) => display is
        Stat.VirtualTotalMainHandChanceToBleedOnHitPct or Stat.VirtualTotalOffHandChanceToBleedOnHitPct or
        Stat.VirtualTotalMainHandChanceToPoisonOnHitPct or Stat.VirtualTotalOffHandChanceToPoisonOnHitPct or
        Stat.MainHandMaimOnHitPct or Stat.OffHandMaimOnHitPct;

    static long BleedFlags(Sum sum, long chance, Func<uint, long> get, Func<uint, int>? local)
    {
        if (sum.Display is not (Stat.VirtualTotalMainHandChanceToBleedOnHitPct
            or Stat.VirtualTotalOffHandChanceToBleedOnHitPct)) return chance;
        if (get(Stat.CannotCauseBleeding) != 0 || get(Stat.NeverDamagingAilments) != 0) return 0;
        return get(Stat.GlobalBleedOnHit) != 0 || (local?.Invoke(Stat.LocalBleedOnHit) ?? 0) != 0 ? 100 : chance;
    }

    static uint? QualifiedProjectileSource(Sum sum) => sum.Display switch
    {
        Stat.VirtualTotalMainHandChanceToBleedOnHitPct or Stat.VirtualTotalOffHandChanceToBleedOnHitPct =>
            ProjectileBleed,
        Stat.VirtualTotalMainHandChanceToPoisonOnHitPct or Stat.VirtualTotalOffHandChanceToPoisonOnHitPct =>
            ProjectilePoison,
        Stat.MainHandMaimOnHitPct or Stat.OffHandMaimOnHitPct => ProjectileMaim,
        _ => null,
    };

    static long QualifiedProjectileChance(Sum sum, Func<uint, int> get,
        bool mainProjectileAttack, bool offProjectileAttack)
    {
        if (sum.Hand == Hand.Main ? !mainProjectileAttack : sum.Hand == Hand.Off && !offProjectileAttack) return 0;
        return QualifiedProjectileSource(sum) is { } source ? get(source) : 0;
    }

    static readonly Sum[] SelectedSums = [.. Sums.Where(s => s.Display is Stat.SkillAreaOfEffectPlusPct
        or Stat.DisplaySkillIncreasedItemRarityPlusPct or Stat.DisplaySkillIncreasedItemQuantityPlusPct or Stat.VirtualMinionDamagePlusPct or Stat.BrandsPerTargetLimit
        or Stat.ReduceEnemyFireResistancePct or Stat.ReduceEnemyColdResistancePct
        or Stat.ReduceEnemyLightningResistancePct
        or Stat.VirtualIgniteDurationPlusPct or Stat.VirtualScorchDurationPlusPct or Stat.VirtualScorchEffectPlusPct
        or Stat.VirtualChillDurationPlusPct or Stat.VirtualChillEffectPlusPct or Stat.VirtualFreezeDurationPlusPct
        or Stat.VirtualFrostburnDurationPlusPct or Stat.VirtualFrostburnEffectPlusPct
        or Stat.VirtualShockDurationPlusPct or Stat.VirtualShockEffectPlusPct
        or Stat.VirtualSapDurationPlusPct or Stat.VirtualSapEffectPlusPct
        or Stat.VirtualTotalMainHandImpaleOnHitPctChance or Stat.VirtualTotalOffHandImpaleOnHitPctChance
        or Stat.VirtualTotalSpellImpaleOnHitPctChance
        || Program.Stats.Name(s.Display).Contains("_chance_to_poison_on_hit_%", StringComparison.Ordinal)
        || Program.Stats.Name(s.Display).Contains("_chance_to_bleed_on_hit_%", StringComparison.Ordinal)
        || Program.Stats.Name(s.Display).Contains("_maim_on_hit_%", StringComparison.Ordinal)
        || Program.Stats.Name(s.Display).Contains("_chance_to_ignite_on_hit_%", StringComparison.Ordinal)
        || Program.Stats.Name(s.Display).Contains("_attack_damage_penetrates_", StringComparison.Ordinal)
        || Program.Stats.Name(s.Display).Contains("_leech_from_", StringComparison.Ordinal)
        || Program.Stats.Name(s.Display).EndsWith("_leech_speed_+%", StringComparison.Ordinal))];

    /// <summary>Selected-skill aliases from original raw character and qualifying skill/support inputs.
    /// Derive each alias once, clamp the completed sum, and retain zero to clear a removed contribution.
    /// Published character aliases must not be passed back as raw seeds.</summary>
    public static List<(uint Stat, int Value)> Selected(Func<uint, int> character, Func<uint, int> own,
        int playerLevel = 1, Func<uint, int>? mainHand = null, Func<uint, int>? offHand = null,
        bool mainArmed = false, bool offArmed = false, bool mainProjectileAttack = false,
        bool offProjectileAttack = false)
    {
        int level = Math.Clamp(playerLevel, 1, Progression.MaxLevel);
        bool blockElemental = character(Penetration.ElementalProhibition) != 0 || own(Penetration.ElementalProhibition) != 0;
        long Combined(uint name) => (long)character(name) + own(name);
        long SelectedSum(Sum sum) => Combined(sum.Display)
            + sum.Sources.Sum(s => Combined(s.Name) * s.Mul * (s.PerPlayerLevel ? level : 1) / s.Div)
            + Total(sum.Local, sum.Hand switch { Hand.Main => mainHand, Hand.Off => offHand, _ => null }, level)
            + (sum.Hand switch { Hand.Main => mainArmed, Hand.Off => offArmed, _ => false }
                ? sum.Armed?.Sum(s => Combined(s.Name) * s.Mul / s.Div) ?? 0 : 0)
            + (QualifiedProjectileSource(sum) is { } source
                && (sum.Hand == Hand.Main ? mainProjectileAttack : sum.Hand == Hand.Off && offProjectileAttack)
                    ? Combined(source) : 0);
        return [.. SelectedSums.Select(sum => (sum.Display,
            blockElemental && Penetration.IsElementalDisplay(sum.Display) ? 0
                : Project(sum, BleedFlags(sum, SelectedSum(sum), Combined,
                    sum.Hand switch { Hand.Main => mainHand, Hand.Off => offHand, _ => null }))))];
    }

    /// <summary>The global duration coefficient shared by the panel and actual flask uses. The display's virtual
    /// seed does not modify gameplay; item-local duration, quality and more modifiers remain separate.</summary>
    public static long FlaskDurationIncrease(Func<uint, int> get, int playerLevel = 1) =>
        Total(GlobalFlaskDurationSources, get, Math.Clamp(playerLevel, 1, Progression.MaxLevel));

    // Sum one raw snapshot. The level is the player level supplied by the caller, never a native stat/gem level.
    static long Total(IEnumerable<Src>? sources, Func<uint, int>? get, int playerLevel) =>
        get is null || sources is null ? 0 : sources.Sum(s =>
            (long)get(s.Name) * s.Mul * (s.PerPlayerLevel ? playerLevel : 1) / s.Div);

    /// <summary>
    /// The Offence tab's per-hand damage rows (issue #75): the hit the server actually swings with, per damage type and
    /// in total. The client shows whole numbers, so each range is rounded down. An empty packet adds no rows, which
    /// leaves those rows blank, as the game does for an attack that cannot happen.
    /// </summary>
    /// <summary>The twelve damage row names of a hand context (main_hand, off_hand, secondary): total then each type,
    /// minimum then maximum, for callers that clear or fill every row.</summary>
    public static IEnumerable<uint> DamageRows(string hand)
    {
        foreach (string type in new[] { "total", "physical", "fire", "cold", "lightning", "chaos" })
            foreach (string bound in new[] { "minimum", "maximum" })
                yield return Stat.Of($"{hand}_{bound}_{type}_damage");
    }

    public static List<(uint Stat, int Value)> HandDamage(DamagePacket packet, string hand)
    {
        var result = new List<(uint, int)>();
        if (packet.IsZero) return result;
        static int Whole(double value) => (int)Math.Floor(Math.Max(0, value));
        result.Add((Stat.Of($"{hand}_minimum_total_damage"), Whole(packet.MinTotal)));
        result.Add((Stat.Of($"{hand}_maximum_total_damage"), Whole(packet.MaxTotal)));
        foreach (var (type, range) in packet.Components)
        {
            string? slug = type switch
            {
                DamageType.Physical => "physical",
                DamageType.Fire => "fire",
                DamageType.Cold => "cold",
                DamageType.Lightning => "lightning",
                DamageType.Chaos => "chaos",
                _ => null,
            };
            if (slug is null) continue;
            result.Add((Stat.Of($"{hand}_minimum_{slug}_damage"), Whole(range.Min)));
            result.Add((Stat.Of($"{hand}_maximum_{slug}_damage"), Whole(range.Max)));
        }
        return result;
    }

    /// <summary>
    /// The rows that combine a pool with a stat: block against projectiles (attack block + <c>additional_block_chance_against_projectiles_%</c>,
    /// spell block + <c>base_spell_projectile_block_%</c>, each capped at its maximum; the spell row shows when
    /// <c>spell_block_differs_against_projectiles</c> is set) and chaos resistance against damage over time (chaos
    /// resistance + <c>additional_chaos_resistance_against_damage_over_time_%</c>, capped at the chaos maximum).
    /// </summary>
    public static List<(uint Stat, int Value)> FromPools(Func<uint, int> get, CharacterStats.Pools pools)
    {
        var result = new List<(uint, int)>();
        int projectileBlock = get(Stat.AdditionalBlockChanceAgainstProjectilesPct);
        if (projectileBlock != 0)
            result.Add((Stat.ProjectileAttackBlockPct, pools.AttackBlockAtMaximum ? pools.BlockChanceMax
                : Math.Clamp(pools.BlockChanceUncapped + projectileBlock, 0, pools.BlockChanceMax)));
        int spellProjectileBlock = get(Stat.BaseSpellProjectileBlockPct);
        if (spellProjectileBlock != 0)
        {
            result.Add((Stat.SpellProjectileBlockPct, pools.SpellBlockAtMaximum ? pools.SpellBlockChanceMax
                : Math.Clamp(pools.SpellBlockChance + spellProjectileBlock, 0, pools.SpellBlockChanceMax)));
            result.Add((Stat.SpellBlockDiffersAgainstProjectiles, 1));
        }
        int chaosDot = get(Stat.AdditionalChaosResistanceAgainstDamageOverTimePct);
        if (chaosDot != 0)
        {
            int uncapped = pools.ChaosResistance + chaosDot;
            result.Add((Stat.UncappedChaosDamageOverTimeResistancePct, uncapped));
            result.Add((Stat.ChaosDamageOverTimeResistancePct, DamageModel.Capped(uncapped, pools.ChaosMaxResistance)));
        }
        return result;
    }

    /// <summary>Path of Building's <c>EvadeChanceCap</c>: the 5% minimum hit chance leaves evasion at most 95%.</summary>
    public const int EvadeChanceCap = 95;

    /// <summary>
    /// The Defence tab's estimate rows (issue #75), which the client derives from nothing: the server works them out
    /// against the reference monster of the character's level (DefaultMonsterStats, as Path of Building's CalcDefence
    /// does with its monster damage and accuracy tables).
    /// <list type="bullet">
    /// <item>Estimated Physical Damage reduction: armour against the reference hit (<see cref="DamageModel.ArmourReduction"/>,
    /// armour / (armour + 5 × damage)) plus the additional physical reduction the charges and passives give
    /// (<see cref="ChargeEffects.Reduction"/>), capped at 90%.</item>
    /// <item>Armour against Projectiles and its estimate, when "increased Armour against Projectiles" applies: the
    /// projectile increase joins the armour increases; <c>armour_differs_against_projectiles</c> makes the panel draw
    /// the rows.</item>
    /// <item>Estimated reduction against Hits, when "additional Physical Damage Reduction against Hits" applies
    /// (<c>armour_differs_against_hits</c>).</item>
    /// <item>Estimated chance to Evade Attacks: 100 − the reference monster's chance to hit this evasion
    /// (<see cref="DamageModel.EvadeChance"/>) plus flat "chance to Evade Attacks" sources, capped at 95%.</item>
    /// <item>Ward Restoration time (<c>ward_delay</c>, milliseconds) when the character has Ward.</item>
    /// <item>Mana Recharge per second when Energy Shield Recharge applies to Mana: the recharge rate on maximum mana.
    /// Nothing in 3.29 applies recharge to Life, so the Life row stays blank.</item>
    /// <item>Omniscience for attribute requirements: Omniscience × "can be satisfied by % of Omniscience".</item>
    /// </list>
    /// <paramref name="referenceDamage"/> and <paramref name="referenceAccuracy"/> are the reference monster's values;
    /// null (no DefaultMonsterStats row for the level) leaves the rows that need them out rather than estimating
    /// against a 0-damage, 0-accuracy attacker.
    /// </summary>
    public static List<(uint Stat, int Value)> Defence(Func<uint, int> get, CharacterStats.Pools pools,
        int? referenceDamage, int? referenceAccuracy)
    {
        var result = new List<(uint, int)>();
        int additional = ChargeEffects.Reduction(get).Physical;
        static int Estimate(int armour, int damage, int extra) =>
            Math.Clamp(DamageModel.ArmourReduction(armour, damage) + Math.Max(0, extra), 0, DamageModel.DamageReductionCap);
        if (referenceDamage is int damage)
        {
            result.Add((Stat.DisplayEstimatedPhysicalDamageReducitonPct, Estimate(pools.Armour, damage, additional)));
            if (get(Stat.PhysicalDamageReductionRatingPlusPctAgainstProjectiles) is int projectilePct and not 0)
            {
                // The projectile-only increase joins the ordinary increases; the pools hold their total, so scale by ratio.
                long baseline = 100L + pools.ArmourPct;
                int armour = baseline <= 0 ? pools.Armour
                    : (int)Math.Clamp((long)pools.Armour * Math.Max(0, baseline + projectilePct) / baseline, 0, int.MaxValue);
                result.Add((Stat.PhysicalDamageReductionRatingAgainstProjectiles, armour));
                result.Add((Stat.ArmourDiffersAgainstProjectiles, 1));
                result.Add((Stat.DisplayEstimatedPhysicalDamageReducitonAgainstProjectilesPct, Estimate(armour, damage, additional)));
            }
            if (get(Stat.AdditionalPhysicalDamageReductionPctAgainstHits) is int againstHits and not 0)
            {
                result.Add((Stat.ArmourDiffersAgainstHits, 1));
                result.Add((Stat.DisplayEstimatedPhysicalDamageReductionAgainstHitsPct,
                    Estimate(pools.Armour, damage, additional + againstHits)));
            }
        }
        if (referenceAccuracy is int accuracy)
            result.Add((Stat.ChanceToEvadePct, get(Stat.KeystoneUnwaveringStance) != 0 ? 0
                : Math.Clamp(DamageModel.EvadeChance(pools.Evasion, accuracy)
                    + get(Stat.ChanceToEvadeAttacksPct), 0, EvadeChanceCap)));
        if (pools.Ward > 0)
            result.Add((Stat.WardDelay, (int)Math.Clamp(Math.Round(WardState.Delay(get) * 1000), 0, int.MaxValue)));
        if (get(Stat.EnergyShieldRechargeApplyToMana) != 0)
            result.Add((Stat.ManaRechargeRatePerMinute, RecoveryValue(EnergyShield.RechargePerMinute(pools.Mana, get))));
        if (get(Stat.AttributeRequirementsCanBeSatisfiedByPctOfAscendance) is int omniscience and not 0)
            result.Add((Stat.OmniscienceForAttributeRequirements, (int)Math.Clamp(
                ((long)get(Stat.Ascendance) + get(Stat.BaseAscendance)) * omniscience / 100, int.MinValue, int.MaxValue)));
        return result;
    }

    /// <summary>Character.ot's <c>mana_regeneration_rate_per_minute_%</c>: 105, i.e. 1.75% of maximum mana per second.</summary>
    public const int BaseManaRegenPctPerMinute = 105;

    /// <summary>
    /// The Defence tab's regeneration rows (issue #75), as the per-minute totals the client divides by 60: life
    /// (<see cref="LifePerMinute"/>) and mana (<see cref="ManaPerMinute"/>). The same formulas feed the player tick, so
    /// the panel shows the regeneration the server actually applies. A character with no life regeneration gets no life
    /// row; mana always has its base rate. The client also needs life_recovery_per_minute / mana_recovery_per_minute
    /// for its own pool stepping: the Life constructor (0x141e6de70) binds these keys to the pools, and 0x141e718c0
    /// applies the rate / 60 times the client tick duration. The display totals alone do not drive that stepping.
    /// </summary>
    public static List<(uint Stat, int Value)> Recovery(Func<uint, int> get, uint maxLife, uint maxMana)
    {
        var result = new List<(uint, int)>();
        var (life, _) = LifeRegeneration(get, maxLife);
        if (life != 0)
        {
            result.Add((Stat.TotalLifeRecoveryPerMinuteFromRegeneration, RecoveryValue(life)));
            result.Add((Stat.LifeRecoveryPerMinute, RecoveryValue(life)));
        }
        int mana = RecoveryValue(ManaPerMinute(get, maxMana));
        result.Add((Stat.TotalManaRecoveryPerMinuteFromRegeneration, mana));
        result.Add((Stat.ManaRecoveryPerMinute, mana));
        return result;
    }

    /// <summary>The recovery inputs with the "while moving" stats added while the player moves (Devout of Garukhan; PoB
    /// Condition Moving). Only the recovery finals read them; the raw inputs the player stats carry stay unchanged.</summary>
    public static Func<uint, int> WhileMoving(Func<uint, int> get, bool moving) => !moving ? get : name => get(name) + name switch
    {
        Stat.LifeRegenerationRatePerMinutePct => get(Stat.LifeRegenerationRatePerMinutePctWhileMoving),
        Stat.ManaRegenerationRatePlusPct => get(Stat.ManaRegenerationRatePlusPctWhileMoving),
        _ => 0,
    };

    static int RecoveryValue(double rate) => (int)Math.Clamp(Math.Round(rate), int.MinValue, int.MaxValue);

    /// <summary>Ordinary Life regeneration increases and recovery increases are distinct multiplicative buckets.
    /// Shared with the expiry-aware Fortification recovery writer so its added source follows the same rules.</summary>
    public static double LifeRegenerationMultiplier(Func<uint, int> get) =>
        get(Stat.NoLifeRegeneration) != 0 || get(Stat.CannotRecoverLife) != 0 ? 0 :
        Math.Max(0, 1 + get(Stat.LifeRegenerationRatePlusPct) / 100.0)
        * Math.Max(0, 1 + get(Stat.LifeRecoveryRatePlusPct) / 100.0);

    /// <summary>Flat plus percentage Life regeneration, scaled by regeneration and recovery increases.
    /// PoB 3.29.x CalcDefence's regeneration calculation; conditional/conversion operators remain separate.
    /// Fire Mastery's flat regeneration per 1% uncapped fire resistance counts only positive resistance.</summary>
    public static double LifePerMinute(Func<uint, int> get, uint maxLife) =>
        (get(Stat.BaseLifeRegenerationRatePerMinute)
            + (double)get(Stat.LifeRegenerationPerMinutePer1PctUncappedFireDamageResistance)
                * Math.Max(0, get(Stat.UncappedFireDamageResistancePct))
            + maxLife * get(Stat.LifeRegenerationRatePerMinutePct) / 100.0) * LifeRegenerationMultiplier(get);

    /// <summary>Zealot's Oath applies life regeneration to Energy Shield instead of Life; Geofri's Sanctuary carries
    /// the same stat under its item modifier name.</summary>
    public static bool ZealotsOath(Func<uint, int> get) =>
        get(Stat.KeystoneZealotsOath) != 0 || get(Stat.RegenerateEnergyShieldInsteadOfLife) != 0;

    /// <summary>Life regeneration per minute, split into the part applied to Life and the part applied to Energy
    /// Shield by Zealot's Oath. An ES recovery prohibition drops the converted part; it never falls back to Life.</summary>
    public static (double Life, double EnergyShield) LifeRegeneration(Func<uint, int> get, uint maxLife)
    {
        double life = LifePerMinute(get, maxLife);
        bool oath = ZealotsOath(get);
        return (oath ? 0 : life, oath && EnergyShield.ConversionAllowed(get) ? life : 0);
    }

    /// <summary>
    /// Character.ot's <see cref="BaseManaRegenPctPerMinute"/> plus <c>mana_regeneration_rate_per_minute_%</c> of maximum
    /// mana and flat <c>base_mana_regeneration_rate_per_minute</c> (Clarity), raised by regeneration then recovery increases.
    /// </summary>
    public static double ManaPerMinute(Func<uint, int> get, uint maxMana) =>
        get(Stat.NoManaRegeneration) != 0 || get(Stat.CannotLeechOrRegenerateMana) != 0 ? 0 :
        (maxMana * (BaseManaRegenPctPerMinute + (double)get(Stat.ManaRegenerationRatePerMinutePct)) / 100.0
            + get(Stat.BaseManaRegenerationRatePerMinute))
        * Math.Max(0, 1 + get(Stat.ManaRegenerationRatePlusPct) / 100.0)
        * Math.Max(0, 1 + get(Stat.ManaRecoveryRatePlusPct) / 100.0);
}
