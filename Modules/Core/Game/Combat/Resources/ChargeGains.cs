using GameServer.Core.Protocol.Instance;


/// <summary>Something that can grant charges (issue #237): a hit, a kill, being hit, a block, a skill's own grant, or
/// the /charges command.</summary>
public abstract record ChargeEvent
{
    /// <summary>The player hit a monster (one per monster hit). <paramref name="Skill"/>: the stats of the skill used
    /// (its "on hit" chances, e.g. Flicker Strike's 15%); <paramref name="SkillEffect"/>: its GrantedEffects Id;
    /// <paramref name="Killed"/>: the hit killed it; <paramref name="Primary"/>: the use's first target (a skill's
    /// once-per-use grant). <paramref name="Melee"/> null: the weapon decides.</summary>
    public sealed record Hit(bool Critical, bool Spell, bool? Melee, MonsterRarity Rarity, string? SkillEffect = null,
        Func<uint, int>? Skill = null, bool Killed = false, bool Primary = true) : ChargeEvent;

    /// <summary>The player killed a hostile monster; <paramref name="Frozen"/>: it was frozen when it died, shattered
    /// included (MonsterElementalAilments.FrozenAtDeath).</summary>
    public sealed record Kill(MonsterRarity Rarity, bool Frozen = false) : ChargeEvent;

    /// <summary>A monster's hit reached the player: it landed or was blocked, not evaded (blocking keeps the defender's
    /// when-hit effects, wiki Blocking).</summary>
    public sealed record WhenHit : ChargeEvent;

    /// <summary>The player's hit stunned a monster (<see cref="HitReaction"/>).</summary>
    public sealed record Stun : ChargeEvent;

    /// <summary>The player blocked a hit (an attack, or a spell with <paramref name="Spell"/>).</summary>
    public sealed record Block(bool Spell) : ChargeEvent;

    /// <summary>Charges a skill grants outright (Enduring Cry) or the /charges command adds.</summary>
    public sealed record Grant(ChargeType Type, int Count, string Why) : ChargeEvent;

    /// <summary>The /charges command sets a type's count; null type with 0 clears all.</summary>
    public sealed record Set(ChargeType? Type, int Count) : ChargeEvent;

    /// <summary>A skill spends <paramref name="Count"/> charges of a type (Discharge): never below the minimum.</summary>
    public sealed record Spend(ChargeType Type, int Count, string Why) : ChargeEvent;
}

/// <summary>
/// Which charges an event grants (issue #237): the "chance to gain a … Charge on Kill / on Hit / on Critical Strike /
/// when Hit / when you Block" stats of passives, items and skills, as their stat descriptions word them
/// (Metadata/StatDescriptions), each its own roll; "Gain a … Charge …" flags count as 100%. A chance is rolled as
/// <c>rng.Next(100) &lt; chance</c>; 100 and above always gain without a roll.
/// Skill rules: the Frenzy gem gives a Frenzy Charge when it hits (ActiveSkills <c>frenzy</c>: "gives the character a
/// frenzy charge if it hits"), once per use; Power Siphon a Power Charge when its hit kills (ActiveSkills <c>power_siphon</c>; the
/// "or soon after the hit" part is not modelled). Gains that need mechanics the server does not have (freeze,
/// bleed, poison, curses, traps, mines, Vaal souls, support gems) are not here; 11-skills.md §15 lists them.
/// </summary>
public static class ChargeGains
{
    /// <summary>One stat that grants charges: the stat, the charge type (null: a random one of the three), and when it
    /// applies.</summary>
    public sealed record Rule(uint Stat, ChargeType? Type, Func<ChargeEvent, WeaponProfile, bool> When, bool Flag = false);

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
    ];

    /// <summary>One gain: a charge type, how many, and the stat or rule that granted it (for the log).</summary>
    public readonly record struct Gain(ChargeType Type, int Count, string Source);

    /// <summary>Whether a chance (percent) succeeds: never at 0 or below, always at 100 or above, otherwise one
    /// <c>rng.Next(100)</c>.</summary>
    public static bool Rolls(int chance, Random rng) => chance >= 100 || (chance > 0 && rng.Next(100) < chance);

    /// <summary>
    /// The charges an event grants. <paramref name="get"/>: the character's stats (passives, items, auras); a hit adds
    /// the used skill's own stats to its "on hit" chances. Each rule rolls once; a random type is drawn with
    /// <c>rng.Next(3)</c> in the order Endurance, Frenzy, Power.
    /// </summary>
    public static List<Gain> Roll(ChargeEvent e, Func<uint, int> get, WeaponProfile weapon, Random rng)
    {
        var gains = new List<Gain>();
        switch (e)
        {
            case ChargeEvent.Grant g:
                if (g.Count > 0) gains.Add(new(g.Type, g.Count, g.Why));
                return gains;
            case ChargeEvent.Set:
                return gains;
            case ChargeEvent.Spend spend:
                if (spend.Count > 0) gains.Add(new(spend.Type, -spend.Count, spend.Why));
                return gains;
        }
        var skill = (e as ChargeEvent.Hit)?.Skill;
        foreach (var rule in Rules)
        {
            if (!rule.When(e, weapon)) continue;
            int value = get(rule.Stat) + (skill?.Invoke(rule.Stat) ?? 0);
            int chance = rule.Flag ? (value != 0 ? 100 : 0) : value;
            if (!Rolls(chance, rng)) continue;
            var type = rule.Type ?? Charges.Types[rng.Next(Charges.Types.Length)];
            gains.Add(new(type, 1, Stats.Name(rule.Stat)));
        }
        if (e is ChargeEvent.Hit hit)
        {
            // Once per use: an area hit on several monsters gives one charge (its first target).
            if (hit.SkillEffect == "Frenzy" && hit.Primary) gains.Add(new(ChargeType.Frenzy, 1, "Frenzy hit"));
            if (hit.SkillEffect == "PowerSiphon" && hit.Killed) gains.Add(new(ChargeType.Power, 1, "Power Siphon kill"));
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

    public enum PeriodicCondition { Always, HitRecently, Stationary, Moving, NoMineDetonatedRecently }

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

    /// <summary>The timers of the <see cref="Periodics"/> of one character.</summary>
    public sealed class Timers
    {
        private readonly double[] elapsed = new double[Periodics.Count];

        /// <summary>Advances every timer by <paramref name="seconds"/> of game time and returns the charges that came due.
        /// A timer whose stat is absent or whose condition does not hold is reset.</summary>
        public List<Gain> Advance(double seconds, Func<uint, int> get, bool hitRecently, bool moving, Random rng) =>
            Advance(seconds, get, hitRecently, moving, rng, mineDetonatedRecently: false);

        public List<Gain> Advance(double seconds, Func<uint, int> get, bool hitRecently, bool moving, Random rng,
            bool mineDetonatedRecently)
        {
            var gains = new List<Gain>();
            for (int i = 0; i < Periodics.Count; i++)
            {
                var p = Periodics[i];
                int value = get(p.Stat);
                // Not zero rather than above zero: the per-second jewel stats store -1 for their one charge.
                bool holds = value != 0 && p.Condition switch
                {
                    PeriodicCondition.HitRecently => hitRecently,
                    PeriodicCondition.Stationary => !moving,
                    PeriodicCondition.Moving => moving,
                    PeriodicCondition.NoMineDetonatedRecently => !mineDetonatedRecently,
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
