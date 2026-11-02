using GameServer.Core.Protocol.Instance;


/// <summary>Something that can grant charges (issue #237): a hit, a kill, being hit, a block, a skill's own grant, or
/// the /charges command.</summary>
abstract record ChargeEvent
{
    /// <summary>The player hit a monster (one per monster hit). <paramref name="Skill"/>: the stats of the skill used
    /// (its "on hit" chances, e.g. Flicker Strike's 15%); <paramref name="SkillEffect"/>: its GrantedEffects Id;
    /// <paramref name="Killed"/>: the hit killed it; <paramref name="Primary"/>: the use's first target (a skill's
    /// once-per-use grant). <paramref name="Melee"/> null: the weapon decides.</summary>
    public sealed record Hit(bool Critical, bool Spell, bool? Melee, MonsterRarity Rarity, string? SkillEffect = null,
        Func<string, int>? Skill = null, bool Killed = false, bool Primary = true) : ChargeEvent;

    /// <summary>The player killed a hostile monster; <paramref name="Frozen"/>: it was frozen when it died, shattered
    /// included (MonsterElementalAilments.FrozenAtDeath).</summary>
    public sealed record Kill(MonsterRarity Rarity, bool Frozen = false) : ChargeEvent;

    /// <summary>A monster's hit landed on the player (not evaded, not blocked).</summary>
    public sealed record WhenHit : ChargeEvent;

    /// <summary>The player blocked a hit (an attack, or a spell with <paramref name="Spell"/>).</summary>
    public sealed record Block(bool Spell) : ChargeEvent;

    /// <summary>Charges a skill grants outright (Enduring Cry) or the /charges command adds.</summary>
    public sealed record Grant(ChargeType Type, int Count, string Why) : ChargeEvent;

    /// <summary>The /charges command sets a type's count; null type with 0 clears all.</summary>
    public sealed record Set(ChargeType? Type, int Count) : ChargeEvent;
}

/// <summary>
/// Which charges an event grants (issue #237): the "chance to gain a … Charge on Kill / on Hit / on Critical Strike /
/// when Hit / when you Block" stats of passives, items and skills, as their stat descriptions word them
/// (Metadata/StatDescriptions), each its own roll; "Gain a … Charge …" flags count as 100%. A chance is rolled as
/// <c>rng.Next(100) &lt; chance</c>; 100 and above always gain without a roll.
/// Skill rules: the Frenzy gem gives a Frenzy Charge when it hits (ActiveSkills <c>frenzy</c>: "gives the character a
/// frenzy charge if it hits"), once per use; Power Siphon a Power Charge when its hit kills (ActiveSkills <c>power_siphon</c>; the
/// "or soon after the hit" part is not modelled). Gains that need mechanics the server does not have (stun, freeze,
/// bleed, poison, curses, traps, mines, Vaal souls, support gems) are not here; 11-skills.md §15 lists them.
/// </summary>
static class ChargeGains
{
    /// <summary>One stat that grants charges: the stat, the charge type (null: a random one of the three), and when it
    /// applies.</summary>
    public sealed record Rule(string Stat, ChargeType? Type, Func<ChargeEvent, WeaponProfile, bool> When, bool Flag = false);

    static bool Melee(ChargeEvent.Hit h, WeaponProfile w) => h.Melee ?? (!h.Spell && !w.Bow && !w.Wand);
    static bool RareOrUnique(MonsterRarity r) => r is MonsterRarity.Rare or MonsterRarity.Unique;

    /// <summary>The gain stats the server rolls, by event.</summary>
    public static readonly IReadOnlyList<Rule> Rules =
    [
        // On kill
        new("endurance_charge_on_kill_%", ChargeType.Endurance, (e, _) => e is ChargeEvent.Kill),
        new("add_frenzy_charge_on_kill_%_chance", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill),
        new("add_power_charge_on_kill_%_chance", ChargeType.Power, (e, _) => e is ChargeEvent.Kill),
        new("add_endurance_charge_on_kill", ChargeType.Endurance, (e, _) => e is ChargeEvent.Kill, Flag: true),
        new("add_frenzy_charge_on_kill", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill, Flag: true),
        new("power_frenzy_or_endurance_charge_on_kill_%", null, (e, _) => e is ChargeEvent.Kill),
        new("endurance_charge_on_kill_percent_chance_while_holding_shield", ChargeType.Endurance, (e, w) => e is ChargeEvent.Kill && w.Shield),
        new("add_frenzy_charge_on_kill_%_chance_while_dual_wielding", ChargeType.Frenzy, (e, w) => e is ChargeEvent.Kill && w.DualWield),
        new("chance_to_gain_power_charge_on_killing_frozen_enemy_%", ChargeType.Power, (e, _) => e is ChargeEvent.Kill { Frozen: true }),
        new("chance_to_gain_frenzy_charge_on_killing_frozen_enemy_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Kill { Frozen: true }),
        // On hit
        new("add_frenzy_charge_on_skill_hit_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit),
        new("gain_frenzy_charge_on_hitting_rare_or_unique_enemy_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit h && RareOrUnique(h.Rarity)),
        new("chance_to_gain_power_charge_on_rare_or_unique_enemy_hit_%", ChargeType.Power, (e, _) => e is ChargeEvent.Hit h && RareOrUnique(h.Rarity)),
        // On critical strike
        new("add_power_charge_on_critical_strike_%", ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new("chance_to_gain_endurance_charge_on_crit_%", ChargeType.Endurance, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new("chance_to_gain_endurance_charge_on_melee_crit_%", ChargeType.Endurance, (e, w) => e is ChargeEvent.Hit { Critical: true } h && Melee(h, w)),
        new("gain_frenzy_charge_on_critical_strike_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new("add_frenzy_charge_on_critical_strike", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Critical: true }, Flag: true),
        new("gain_frenzy_charge_per_enemy_you_crit_%_chance", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Hit { Critical: true }),
        new("gain_power_charge_per_enemy_you_crit", ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: true }, Flag: true),
        new("gain_power_charge_on_critical_strike_with_wands_%", ChargeType.Power, (e, w) => e is ChargeEvent.Hit { Critical: true, Spell: false } && w.Wand),
        // On non-critical strike
        new("gain_power_charge_on_non_critical_strike_%", ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: false }),
        new("unique_gain_power_charge_on_non_crit", ChargeType.Power, (e, _) => e is ChargeEvent.Hit { Critical: false }, Flag: true),
        new("power_charge_on_non_critical_strike_%_chance_with_claws_daggers", ChargeType.Power,
            (e, w) => e is ChargeEvent.Hit { Critical: false, Spell: false } && (w.Claw || w.Dagger)),
        // When hit
        new("chance_to_gain_endurance_charge_when_hit_%", ChargeType.Endurance, (e, _) => e is ChargeEvent.WhenHit),
        new("add_frenzy_charge_when_hit_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.WhenHit),
        // On block
        new("chance_to_gain_endurance_charge_on_block_%", ChargeType.Endurance, (e, _) => e is ChargeEvent.Block),
        new("chance_to_gain_frenzy_charge_on_block_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Block),
        new("chance_to_gain_frenzy_charge_on_block_attack_%", ChargeType.Frenzy, (e, _) => e is ChargeEvent.Block { Spell: false }),
        new("power_charge_on_block_%_chance", ChargeType.Power, (e, _) => e is ChargeEvent.Block),
        new("chance_to_gain_power_charge_when_block_%", ChargeType.Power, (e, _) => e is ChargeEvent.Block),
        new("power_charge_on_spell_block_%_chance", ChargeType.Power, (e, _) => e is ChargeEvent.Block { Spell: true }),
        new("gain_random_charge_on_block", null, (e, _) => e is ChargeEvent.Block, Flag: true),
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
    public static List<Gain> Roll(ChargeEvent e, Func<string, int> get, WeaponProfile weapon, Random rng)
    {
        var gains = new List<Gain>();
        switch (e)
        {
            case ChargeEvent.Grant g:
                if (g.Count > 0) gains.Add(new(g.Type, g.Count, g.Why));
                return gains;
            case ChargeEvent.Set:
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
            gains.Add(new(type, 1, rule.Stat));
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
    public static string ToMaximumStat(ChargeType type) => ToMaximumStats[(int)type is >= 0 and < 3 ? (int)type : throw new ArgumentOutOfRangeException(nameof(type))];

    // Built once: every stat snapshot observes them (ChargeSession.Observe).
    static readonly string[] ToMaximumStats =
        [.. Charges.Types.Select(t => $"gain_maximum_{Charges.Name(t)}_charges_on_{Charges.Name(t)}_charge_gained_%_chance")];

    /// <summary>A charge granted over time, while its condition holds: every <c>Period</c> seconds, <c>Count</c>
    /// charges.</summary>
    public sealed record Periodic(string Stat, ChargeType? Type, PeriodicCondition Condition, Func<int, (double Period, int Count)> Rate);

    public enum PeriodicCondition { Always, HitRecently, Stationary, Moving }

    /// <summary>
    /// The "gain a … Charge every …" stats the server can time (game clock, alive, outside towns): each second, every
    /// x milliseconds (the body armour implicits), every 4 seconds while stationary, each second if hit recently (the
    /// last 4 seconds, PoE's "recently"), every X seconds while stationary or moving (the walk estimate). The timer of
    /// a conditional one starts again when its condition stops holding.
    /// </summary>
    public static readonly IReadOnlyList<Periodic> Periodics =
    [
        new("gain_X_endurance_charges_per_second", ChargeType.Endurance, PeriodicCondition.Always, EachSecond),
        new("gain_X_frenzy_charges_per_second", ChargeType.Frenzy, PeriodicCondition.Always, EachSecond),
        new("gain_X_power_charges_per_second", ChargeType.Power, PeriodicCondition.Always, EachSecond),
        new("body_armour_implicit_gain_endurance_charge_every_x_ms", ChargeType.Endurance, PeriodicCondition.Always, v => (v / 1000.0, 1)),
        new("body_armour_implicit_gain_frenzy_charge_every_x_ms", ChargeType.Frenzy, PeriodicCondition.Always, v => (v / 1000.0, 1)),
        new("body_armour_implicit_gain_power_charge_every_x_ms", ChargeType.Power, PeriodicCondition.Always, v => (v / 1000.0, 1)),
        new("gain_X_random_charges_every_6_seconds", null, PeriodicCondition.Always, v => (6, v)),
        new("gain_endurance_charge_per_second_if_have_been_hit_recently", ChargeType.Endurance, PeriodicCondition.HitRecently, v => (1, v)),
        new("number_of_endurance_charges_to_gain_every_4_seconds_while_stationary", ChargeType.Endurance, PeriodicCondition.Stationary, v => (4, v)),
        new("gain_a_endurance_charge_every_X_seconds_while_stationary", ChargeType.Endurance, PeriodicCondition.Stationary, v => (v, 1)),
        new("gain_random_charge_per_second_while_stationary", null, PeriodicCondition.Stationary, v => (1, v)),
        new("gain_a_frenzy_charge_every_X_seconds_while_moving", ChargeType.Frenzy, PeriodicCondition.Moving, v => (v, 1)),
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
        public List<Gain> Advance(double seconds, Func<string, int> get, bool hitRecently, bool moving, Random rng)
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
                    if (p.Type is { } type) gains.Add(new(type, count, p.Stat));
                    else for (int n = 0; n < count; n++) gains.Add(new(Charges.Types[rng.Next(Charges.Types.Length)], 1, p.Stat));
                }
            }
            return gains;
        }

        /// <summary>Every timer starts again (death).</summary>
        public void Reset() => Array.Clear(elapsed);
    }
}
