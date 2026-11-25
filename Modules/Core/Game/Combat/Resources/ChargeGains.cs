using GameServer.Core.Protocol.Instance;


/// <summary>Something that can grant charges (issue #237): a hit, a kill, being hit, a block, a skill's own grant, or
/// the /charges command.</summary>
public abstract record ChargeEvent : CombatEvent
{
    /// <summary>For a monster's hit on the player (WhenHit, Block), what its site had: the player's stats as it read
    /// them, its own time for the hit, and the monster's id in the area. What an effect on the attacker (an ailment, a
    /// blind) is worked out from; null or 0 where the site gave none.</summary>
    public IReadOnlyDictionary<uint, int>? PlayerStats { get; init; }
    public double? HitAt { get; init; }
    public uint MonsterId { get; init; }

    /// <summary>For a monster's hit on the player (WhenHit, Block): it was a melee attack (an attack that is not a
    /// projectile), and the physical damage it dealt after the player's mitigation. What a "damage" action that
    /// returns damage to the attacker reads.</summary>
    public bool MeleeAttacker { get; init; }
    public int PhysicalTaken { get; init; }

    /// <summary>The player hit a monster (one per monster hit). <paramref name="Skill"/>: the stats of the skill used
    /// (its "on hit" chances, e.g. Flicker Strike's 15%); <paramref name="SkillEffect"/>: its GrantedEffects Id;
    /// <paramref name="Killed"/>: the hit killed it; <paramref name="Primary"/>: the use's first target (a skill's
    /// once-per-use grant). <paramref name="Melee"/> null: the weapon decides. The target's state at the hit:
    /// <paramref name="Frozen"/>, <paramref name="Bleeding"/>, <paramref name="Marked"/> (by this player's own Mark);
    /// the player's own: <paramref name="SelfBleeding"/>, <paramref name="SelfPoisoned"/>.</summary>
    public sealed record Hit(bool Critical, bool Spell, bool? Melee, MonsterRarity Rarity, string? SkillEffect = null,
        Func<uint, int>? Skill = null, bool Killed = false, bool Primary = true, bool Frozen = false,
        bool Bleeding = false, bool Marked = false, bool SelfBleeding = false, bool SelfPoisoned = false) : ChargeEvent;

    /// <summary>The player killed a hostile monster; <paramref name="Frozen"/>: it was frozen when it died, shattered
    /// included (MonsterElementalAilments.FrozenAtDeath); <paramref name="Poisons"/>: the poisons on it when it died;
    /// <paramref name="Skill"/>: the stats of the skill whose hit killed it (its supports' included), when the kill came
    /// from a hit that had them: a support's "on Kill" chance (Ice Bite's Frenzy Charge on Killing a Frozen Enemy) is
    /// its skill's stat, not the character's.</summary>
    public sealed record Kill(MonsterRarity Rarity, bool Frozen = false, int Poisons = 0, Func<uint, int>? Skill = null) : ChargeEvent;

    /// <summary>A monster's hit reached the player: it landed or was blocked, not evaded (blocking keeps the defender's
    /// when-hit effects, wiki Blocking). <paramref name="Critical"/>: the monster's hit was a critical strike.</summary>
    public sealed record WhenHit(bool Critical = false) : ChargeEvent;

    /// <summary>The player's hit stunned a monster (<see cref="HitReaction"/>): <paramref name="Melee"/> a melee hit,
    /// <paramref name="Skill"/> the stunning skill's own stats (its supports' included).</summary>
    public sealed record Stun(bool Melee = false, Func<uint, int>? Skill = null) : ChargeEvent;

    /// <summary>The player used a skill (after its cost): a Vaal skill, a curse spell or a trap throw.
    /// <paramref name="Effect"/>: its GrantedEffects row; <paramref name="At"/>: where it was aimed;
    /// <paramref name="SkillId"/>: its id among the character's skills.</summary>
    public sealed record SkillUse(bool Vaal, bool Curse, bool Trap, int Effect = -1, (int X, int Y)? At = null,
        ushort SkillId = 0) : ChargeEvent;

    /// <summary>The player used a flask and its charges were paid: the flask's item id, and what it is
    /// ("utility", "life", "mana" or "hybrid").</summary>
    public sealed record FlaskUse(uint ItemId, string Kind) : ChargeEvent;

    /// <summary>The player blocked a hit (an attack, or a spell with <paramref name="Spell"/>).</summary>
    public sealed record Block(bool Spell) : ChargeEvent;

    /// <summary>Charges a skill grants outright (Enduring Cry) or the /charges command adds.</summary>
    public sealed record Grant(ChargeType Type, int Count, string Why) : ChargeEvent;

    /// <summary>The /charges command sets a type's count; null type with 0 clears all.</summary>
    public sealed record Set(ChargeType? Type, int Count) : ChargeEvent;

    /// <summary>What an effect rule's "charge" does: one gain or loss, as a rule of <see cref="ChargeGains"/> gives it.</summary>
    public sealed record RuleGain(ChargeGains.Gain Gain) : ChargeEvent;

    /// <summary>A skill spends <paramref name="Count"/> charges of a type (Discharge): never below the minimum.</summary>
    public sealed record Spend(ChargeType Type, int Count, string Why) : ChargeEvent;
}

/// <summary>
/// Which charges an event grants (issue #237): the "chance to gain a … Charge on Kill / on Hit / on Critical Strike /
/// when Hit / when you Block" stats of passives, items and skills, as their stat descriptions word them
/// (Metadata/StatDescriptions), each its own roll; "Gain a … Charge …" flags count as 100%. A chance is rolled as
/// <c>rng.Next(100) &lt; chance</c>; 100 and above always gain without a roll.
/// Skill rules: the Frenzy gem gives a Frenzy Charge when it hits (ActiveSkills <c>frenzy</c>: "gives the character a
/// frenzy charge if it hits"), once per use. Power Siphon's 3.29 stats grant its Power Charge on hitting a rare or unique
/// enemy (the rule above); its ActiveSkills text still names a kill, which no stat carries. Gains that need mechanics the
/// server does not have (freeze, ignite or taunt as one attack's own result, mines, totems) are not here; 11-skills.md §15
/// lists them. A hit and a stun count the used skill's own stats with its supports' (Power Charge on Critical, Endurance
/// Charge on Melee Stun).
/// </summary>
public static class ChargeGains
{
    /// <summary>What a rule does when it rolls: gain one charge, gain up to the maximum, or lose every charge of the type
    /// (above the minimum).</summary>
    public enum Outcome { Gain, ToMaximum, LoseAll, LoseOne }

    /// <summary>One stat that grants (or takes) charges: the stat, the charge type (null: a random one of the three),
    /// when it applies, and what it does.</summary>
    public sealed record Rule(uint Stat, ChargeType? Type, Func<ChargeEvent, WeaponProfile, bool> When, bool Flag = false,
        Outcome Does = Outcome.Gain);

    static bool Melee(ChargeEvent.Hit h, WeaponProfile w) => h.Melee ?? (!h.Spell && !w.Bow && !w.Wand);
    static bool RareOrUnique(MonsterRarity r) => r is MonsterRarity.Rare or MonsterRarity.Unique;

    /// <summary>The gain stats the server rolls, by event.</summary>
    public static readonly IReadOnlyList<Rule> Rules =
    [
        // On kill
        new(Stat.EnduranceChargeOnKillPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Kill),
        new(Stat.AddFrenzyChargeOnKillPctChance, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill),
        new(Stat.AddPowerChargeOnKillPctChance, ChargeType.Power, (e, _) => e is ChargeEvent.Kill),
        new(Stat.AddEnduranceChargeOnKill, ChargeType.Endurance, (e, _) => e is ChargeEvent.Kill, Flag: true),
        new(Stat.AddFrenzyChargeOnKill, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill, Flag: true),
        new(Stat.PowerFrenzyOrEnduranceChargeOnKillPct, null, (e, _) => e is ChargeEvent.Kill),
        new(Stat.EnduranceChargeOnKillPercentChanceWhileHoldingShield, ChargeType.Endurance, (e, w) => e is ChargeEvent.Kill && w.Shield),
        new(Stat.AddFrenzyChargeOnKillPctChanceWhileDualWielding, ChargeType.Frenzy, (e, w) => e is ChargeEvent.Kill && w.DualWield),
        new(Stat.ChanceToGainPowerChargeOnKillingFrozenEnemyPct, ChargeType.Power, (e, _) => e is ChargeEvent.Kill { Frozen: true }),
        new(Stat.ChanceToGainFrenzyChargeOnKillingFrozenEnemyPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill { Frozen: true }),
        // On hit
        new(Stat.AddFrenzyChargeOnSkillHitPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit),
        new(Stat.AddPowerChargeOnHitPct, ChargeType.Power, (e, _) => e is ChargeEvent.Hit),
        new(Stat.AddPowerChargeOnSkillHitPct, ChargeType.Power, (e, _) => e is ChargeEvent.Hit),
        new(Stat.GainFrenzyChargeOnHitWhileBleeding, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit, Flag: true),
        new(Stat.GainFrenzyChargeOnHittingRareOrUniqueEnemyPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit h && RareOrUnique(h.Rarity)),
        new(Stat.ChanceToGainPowerChargeOnRareOrUniqueEnemyHitPct, ChargeType.Power, (e, _) => e is ChargeEvent.Hit h && RareOrUnique(h.Rarity)),
        // On critical strike
        new(Stat.AddPowerChargeOnCriticalStrikePct, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new(Stat.ChanceToGainEnduranceChargeOnCritPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new(Stat.ChanceToGainEnduranceChargeOnMeleeCritPct, ChargeType.Endurance, (e, w) => e is ChargeEvent.Hit { Critical: true } h && Melee(h, w)),
        new(Stat.GainFrenzyChargeOnCriticalStrikePct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new(Stat.AddFrenzyChargeOnCriticalStrike, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Critical: true }, Flag: true),
        new(Stat.GainFrenzyChargePerEnemyYouCritPctChance, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new(Stat.GainPowerChargePerEnemyYouCrit, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: true }, Flag: true),
        new(Stat.GainPowerChargeOnCriticalStrikeWithWandsPct, ChargeType.Power, (e, w) => e is ChargeEvent.Hit { Critical: true, Spell: false } && w.Wand),
        // On non-critical strike
        new(Stat.GainPowerChargeOnNonCriticalStrikePct, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: false }),
        new(Stat.UniqueGainPowerChargeOnNonCrit, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: false }, Flag: true),
        new(Stat.PowerChargeOnNonCriticalStrikePctChanceWithClawsDaggers, ChargeType.Power,
            (e, w) => e is ChargeEvent.Hit { Critical: false, Spell: false } && (w.Claw || w.Dagger)),
        // When hit
        new(Stat.ChanceToGainEnduranceChargeWhenHitPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.WhenHit),
        new(Stat.AddFrenzyChargeWhenHitPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.WhenHit),
        new(Stat.ChanceToGainEnduranceChargeWhenYouStunEnemyPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Stun),
        // On block
        new(Stat.ChanceToGainEnduranceChargeOnBlockPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Block),
        new(Stat.ChanceToGainFrenzyChargeOnBlockPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Block),
        new(Stat.ChanceToGainFrenzyChargeOnBlockAttackPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Block { Spell: false }),
        new(Stat.PowerChargeOnBlockPctChance, ChargeType.Power, (e, _) => e is ChargeEvent.Block),
        new(Stat.ChanceToGainPowerChargeWhenBlockPct, ChargeType.Power, (e, _) => e is ChargeEvent.Block),
        new(Stat.PowerChargeOnSpellBlockPctChance, ChargeType.Power, (e, _) => e is ChargeEvent.Block { Spell: true }),
        new(Stat.GainRandomChargeOnBlock, null, (e, _) => e is ChargeEvent.Block, Flag: true),
        // More on hit, by the target's or the player's state
        new(Stat.AddEnduranceChargeOnSkillHitPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Hit),
        new(Stat.GainFrenzyChargeOnHittingUniqueEnemyPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Rarity: MonsterRarity.Unique }),
        new(Stat.GainPowerChargeOnHitPctChanceAgainstFrozenEnemy, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Frozen: true }),
        new(Stat.ChanceToGainEnduranceChargeOnHitPctVsBleedingEnemy, ChargeType.Endurance, (e, _) => e is ChargeEvent.Hit { Bleeding: true }),
        new(Stat.GainFrenzyChargeOnHittingMarkedEnemyPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Marked: true }),
        new(Stat.GainPowerChargeOnHitWhileBleeding, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { SelfBleeding: true }, Flag: true),
        new(Stat.AddPowerChargeOnHitWhilePoisonedPct, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { SelfPoisoned: true }),
        // One skill's own (the enchantments): Holy Sweep on hit, Ice Spear on critical strike, Power Siphon's
        // additional charge on kill and Frenzy's additional charge.
        new(Stat.SweepAddEnduranceChargeOnHitPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Hit { SkillEffect: "Sweep" }),
        new(Stat.IceSpearPctChanceToGainPowerChargeOnCriticalStrike, ChargeType.Power,
            (e, _) => e is ChargeEvent.Hit { Critical: true, SkillEffect: "IceSpear" }),
        new(Stat.PowerSiphonPctChanceToGainPowerChargeOnKill, ChargeType.Power,
            (e, _) => e is ChargeEvent.Hit { Killed: true, SkillEffect: "PowerSiphon" }),
        new(Stat.FrenzyPctChanceToGainAdditionalFrenzyCharge, ChargeType.Frenzy,
            (e, _) => e is ChargeEvent.Hit { Primary: true, SkillEffect: "Frenzy" }),
        // "Lose all Power Charges on Critical Strike" (Romira's Banquet)
        new(Stat.UniqueLoseAllPowerChargesOnCrit, ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: true }, Flag: true,
            Does: Outcome.LoseAll),
        // On kill, by the poisons on the enemy (Maligaro's Cruelty)
        new(Stat.GainFrenzyChargeOnKillVsEnemiesWith5PlusPoisonsPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill { Poisons: >= 5 }),
        new(Stat.GainPowerChargeOnKillVsEnemiesWithLessThan5PoisonsPct, ChargeType.Power, (e, _) => e is ChargeEvent.Kill { Poisons: < 5 }),
        // On stun: any hit, or a melee one; Endurance Charge on Melee Stun's flag is the support's own (Stun.Skill)
        new(Stat.ChanceToGainPowerChargeOnStunPct, ChargeType.Power, (e, _) => e is ChargeEvent.Stun),
        new(Stat.ChanceToGainFrenzyChargeOnStunPct, ChargeType.Frenzy, (e, _) => e is ChargeEvent.Stun),
        new(Stat.ChanceToGainPowerChargeOnMeleeStunPct, ChargeType.Power, (e, _) => e is ChargeEvent.Stun { Melee: true }),
        new(Stat.GainEnduranceChargeOnMeleeStunPct, ChargeType.Endurance, (e, _) => e is ChargeEvent.Stun { Melee: true }),
        new(Stat.GainEnduranceChargeOnMeleeStun, ChargeType.Endurance, (e, _) => e is ChargeEvent.Stun { Melee: true }, Flag: true),
        // When hit: Ambu's Charge on a critical strike taken, Daresso's Defiance and the power one's losses
        new(Stat.GainMaximumEnduranceChargesWhenCritChancePct, ChargeType.Endurance, (e, _) => e is ChargeEvent.WhenHit { Critical: true },
            Does: Outcome.ToMaximum),
        new(Stat.UniqueLoseAllEnduranceChargesWhenHit, ChargeType.Endurance, (e, _) => e is ChargeEvent.WhenHit, Flag: true,
            Does: Outcome.LoseAll),
        new(Stat.UniqueLoseAPowerChargeWhenHit, ChargeType.Power, (e, _) => e is ChargeEvent.WhenHit, Flag: true, Does: Outcome.LoseOne),
        // "Lose all Power Charges when you Block" (Willclash)
        new(Stat.LoseAllPowerChargesOnBlock, ChargeType.Power, (e, _) => e is ChargeEvent.Block, Flag: true, Does: Outcome.LoseAll),
        // On using a skill: a Vaal skill (Yaomac's Accord), a curse (Anathema), a trap (Jaws of Agony)
        new(Stat.GainFrenzyPowerEnduranceChargesOnVaalSkillUse, ChargeType.Endurance, (e, _) => e is ChargeEvent.SkillUse { Vaal: true }, Flag: true),
        new(Stat.GainFrenzyPowerEnduranceChargesOnVaalSkillUse, ChargeType.Frenzy, (e, _) => e is ChargeEvent.SkillUse { Vaal: true }, Flag: true),
        new(Stat.GainFrenzyPowerEnduranceChargesOnVaalSkillUse, ChargeType.Power, (e, _) => e is ChargeEvent.SkillUse { Vaal: true }, Flag: true),
        new(Stat.GainPowerChargeOnVaalSkillUsePct, ChargeType.Power, (e, _) => e is ChargeEvent.SkillUse { Vaal: true }),
        new(Stat.GainPowerChargeOnCurseCastPct, ChargeType.Power, (e, _) => e is ChargeEvent.SkillUse { Curse: true }),
        new(Stat.GainPowerChargeWhenThrowingTrapPct, ChargeType.Power, (e, _) => e is ChargeEvent.SkillUse { Trap: true }),
    ];

    /// <summary>One gain: a charge type, how many (negative: a loss; <see cref="All"/>: every charge above the
    /// minimum), and the stat or rule that granted it (for the log). <paramref name="ToMaximum"/>: up to the maximum.</summary>
    public readonly record struct Gain(ChargeType Type, int Count, string Source, bool ToMaximum = false)
    {
        public const int All = -int.MaxValue;
    }

    /// <summary>Whether a chance (percent) succeeds: never at 0 or below, always at 100 or above, otherwise one
    /// <c>rng.Next(100)</c>.</summary>
    public static bool Rolls(int chance, Random rng) => chance >= 100 || (chance > 0 && rng.Next(100) < chance);

    /// <summary>
    /// The charges an event grants. <paramref name="get"/>: the character's stats (passives, items, auras); a hit adds
    /// the used skill's own stats to its "on hit" chances. Each rule rolls once; a random type is drawn with
    /// <c>rng.Next(3)</c> in the order Endurance, Frenzy, Power.
    /// </summary>
    /// <paramref name="ruled"/>: the stats an effect rule is live for on this character (EffectRules.LiveFor); the rule
    /// rolls those, the skill's own part included, so they are left out here.
    public static List<Gain> Roll(ChargeEvent e, Func<uint, int> get, WeaponProfile weapon, Random rng, Func<uint, bool>? ruled = null)
    {
        var gains = new List<Gain>();
        switch (e)
        {
            case ChargeEvent.RuleGain rule:
                gains.Add(rule.Gain);
                return gains;
            case ChargeEvent.Grant g:
                if (g.Count > 0) gains.Add(new(g.Type, g.Count, g.Why));
                return gains;
            case ChargeEvent.Set:
                return gains;
            case ChargeEvent.Spend spend:
                if (spend.Count > 0) gains.Add(new(spend.Type, -spend.Count, spend.Why));
                return gains;
        }
        var skill = e switch { ChargeEvent.Hit h => h.Skill, ChargeEvent.Stun s => s.Skill, ChargeEvent.Kill k => k.Skill, _ => null };
        foreach (var rule in Rules)
        {
            if (!rule.When(e, weapon)) continue;
            if (ruled is not null && ruled(rule.Stat)) continue;
            int value = get(rule.Stat) + (skill?.Invoke(rule.Stat) ?? 0);
            int chance = rule.Flag ? (value != 0 ? 100 : 0) : value;
            if (!Rolls(chance, rng)) continue;
            var type = rule.Type ?? Charges.Types[rng.Next(Charges.Types.Length)];
            string label = Stats.Name(rule.Stat);
            gains.Add(rule.Does switch
            {
                Outcome.LoseAll => new(type, Gain.All, label),
                Outcome.LoseOne => new(type, -1, label),
                Outcome.ToMaximum => new(type, 1, label, ToMaximum: true),
                _ => new(type, 1, label),
            });
        }
        if (e is ChargeEvent.Hit hit)
        {
            // Once per use: an area hit on several monsters gives one charge (its first target).
            if (hit.SkillEffect == "Frenzy" && hit.Primary) gains.Add(new(ChargeType.Frenzy, 1, "Frenzy hit"));
        }
        return gains;
    }

    /// <summary>"% chance that if you would gain … Charges, you instead gain up to maximum … Charges".</summary>
    public static uint ToMaximumStat(ChargeType type) => ToMaximumStats[(int)type is >= 0 and < 3 ? (int)type : throw new ArgumentOutOfRangeException(nameof(type))];

    // Built once: every stat snapshot observes them (ChargeSession.Observe).
    static readonly uint[] ToMaximumStats =
        [.. Charges.Types.Select(t => Stat.Of($"gain_maximum_{Charges.Name(t)}_charges_on_{Charges.Name(t)}_charge_gained_%_chance"))];

    /// <summary>A charge granted over time, while its condition holds: every <c>Period</c> seconds, <c>Count</c>
    /// charges.</summary>
    public sealed record Periodic(uint Stat, ChargeType? Type, PeriodicCondition Condition, Func<int, (double Period, int Count)> Rate);

    public enum PeriodicCondition { Always, HitRecently, Stationary, Moving, NoMineDetonatedRecently, NotLostRecently }

    /// <summary>
    /// The "gain a … Charge every …" stats the server can time (game clock, alive, outside towns): each second, every
    /// x milliseconds (the body armour implicits), every 4 seconds while stationary, each second if hit recently (the
    /// last 4 seconds, PoE's "recently"), every X seconds while stationary or moving (the walk estimate). The timer of
    /// a conditional one starts again when its condition stops holding.
    /// </summary>
    public static readonly IReadOnlyList<Periodic> Periodics =
    [
        new(Stat.GainXEnduranceChargesPerSecond, ChargeType.Endurance, PeriodicCondition.Always, EachSecond),
        new(Stat.GainXFrenzyChargesPerSecond, ChargeType.Frenzy, PeriodicCondition.Always, EachSecond),
        new(Stat.GainXPowerChargesPerSecond, ChargeType.Power, PeriodicCondition.Always, EachSecond),
        new(Stat.BodyArmourImplicitGainEnduranceChargeEveryXMs, ChargeType.Endurance, PeriodicCondition.Always, v => (v / 1000.0, 1)),
        new(Stat.BodyArmourImplicitGainFrenzyChargeEveryXMs, ChargeType.Frenzy, PeriodicCondition.Always, v => (v / 1000.0, 1)),
        new(Stat.BodyArmourImplicitGainPowerChargeEveryXMs, ChargeType.Power, PeriodicCondition.Always, v => (v / 1000.0, 1)),
        new(Stat.GainXRandomChargesEvery6Seconds, null, PeriodicCondition.Always, v => (6, v)),
        new(Stat.GainEnduranceChargePerSecondIfHaveBeenHitRecently, ChargeType.Endurance, PeriodicCondition.HitRecently, v => (1, v)),
        new(Stat.NumberOfEnduranceChargesToGainEvery4SecondsWhileStationary, ChargeType.Endurance, PeriodicCondition.Stationary, v => (4, v)),
        new(Stat.GainAEnduranceChargeEveryXSecondsWhileStationary, ChargeType.Endurance, PeriodicCondition.Stationary, v => (v, 1)),
        new(Stat.GainRandomChargePerSecondWhileStationary, null, PeriodicCondition.Stationary, v => (1, v)),
        new(Stat.GainAFrenzyChargeEveryXSecondsWhileMoving, ChargeType.Frenzy, PeriodicCondition.Moving, v => (v, 1)),
        // "Gain a Power Charge every Second if you haven't lost Power Charges Recently" (Willclash).
        new(Stat.GainPowerChargePerSecondIfHaveNotLostPowerChargeRecently, ChargeType.Power,
            PeriodicCondition.NotLostRecently, v => (1, v)),
        new(Stat.LosePowerChargeEachSecondIfNotDetonatedMinesRecently, ChargeType.Power,
            PeriodicCondition.NoMineDetonatedRecently, _ => (1, -1)),
    ];

    /// <summary>
    /// The "gain X ... Charges per second" jewels: Replica Fragility, Replica Pacifism and Replica
    /// Powerlessness store <c>-1</c>, and the tooltip has no number ("Lose an Endurance Charge each
    /// second"): the value is the count, one charge each second, negative for the loss. Live 2026-10-27
    /// no charge was gained or lost, because a timer only ran for a value above zero.
    /// </summary>
    static (double Period, int Count) EachSecond(int value) => (1, value);

    // The same list as the fx tables give it ("interval" rules that give or take charges), each stat at its place in
    // Periodics so a character's timers keep their slots; a stat only the tables have follows. Null until they load.
    static volatile IReadOnlyList<Periodic>? tablePeriodics;

    /// <summary>The periodic charges in use: the fx tables' while the effect rules are on, the list above otherwise.</summary>
    public static IReadOnlyList<Periodic> ActivePeriodics => EffectRules.Enabled && tablePeriodics is { } table ? table : Periodics;

    /// <summary>Every periodic charge stat either list has (what a character's charge session watches).</summary>
    public static IReadOnlyList<Periodic> KnownPeriodics => tablePeriodics ?? Periodics;

    /// <summary>Takes the periodic charges the fx tables describe (FxRules.Periodics), when the game data loads. A stat
    /// of the list above that the tables do not have keeps its entry.</summary>
    public static void LoadPeriodics(IReadOnlyList<Periodic> fromTables)
    {
        if (fromTables.Count == 0) { tablePeriodics = null; return; }
        var merged = new List<Periodic>(Periodics);
        foreach (var periodic in fromTables)
        {
            int at = merged.FindIndex(p => p.Stat == periodic.Stat);
            if (at >= 0) merged[at] = periodic;
            else merged.Add(periodic);
        }
        tablePeriodics = merged;
    }

    /// <summary>The timers of the <see cref="Periodics"/> of one character.</summary>
    public sealed class Timers
    {
        private double[] elapsed = new double[Periodics.Count];

        /// <summary>Advances every timer by <paramref name="seconds"/> of game time and returns the charges that came due.
        /// A timer whose stat is absent or whose condition does not hold is reset. <paramref name="lostRecently"/>: whether
        /// a charge of the type was lost in the last 4 seconds (null: never).</summary>
        public List<Gain> Advance(double seconds, Func<uint, int> get, bool hitRecently, bool moving, Random rng) =>
            Advance(seconds, get, hitRecently, moving, rng, mineDetonatedRecently: false);

        public List<Gain> Advance(double seconds, Func<uint, int> get, bool hitRecently, bool moving, Random rng,
            bool mineDetonatedRecently, Func<ChargeType, bool>? lostRecently = null)
        {
            var gains = new List<Gain>();
            var periodics = ActivePeriodics;
            if (elapsed.Length < periodics.Count) Array.Resize(ref elapsed, periodics.Count);
            for (int i = 0; i < periodics.Count; i++)
            {
                var p = periodics[i];
                int value = get(p.Stat);
                // Not zero rather than above zero: the per-second jewel stats store -1 for their one charge.
                bool holds = value != 0 && p.Condition switch
                {
                    PeriodicCondition.HitRecently => hitRecently,
                    PeriodicCondition.Stationary => !moving,
                    PeriodicCondition.Moving => moving,
                    PeriodicCondition.NoMineDetonatedRecently => !mineDetonatedRecently,
                    PeriodicCondition.NotLostRecently => p.Type is { } lost && lostRecently?.Invoke(lost) != true,
                    _ => true,
                };
                var (period, count) = value != 0 ? p.Rate(value) : (0, 0);
                // A negative count takes charges away (Replica Fragility and its two siblings); a random type
                // ("gain X random charges") only ever adds them.
                if (!holds || period <= 0 || count == 0 || (p.Type is null && count < 0)) { elapsed[i] = 0; continue; }
                elapsed[i] += Math.Max(0, seconds);
                while (elapsed[i] >= period)
                {
                    elapsed[i] -= period;
                    if (p.Type is { } type) gains.Add(new(type, count, Stats.Name(p.Stat)));
                    else for (int n = 0; n < count; n++) gains.Add(new(Charges.Types[rng.Next(Charges.Types.Length)], 1, Stats.Name(p.Stat)));
                }
            }
            return gains;
        }

        /// <summary>Every timer starts again (death).</summary>
        public void Reset() => Array.Clear(elapsed);
    }
}
