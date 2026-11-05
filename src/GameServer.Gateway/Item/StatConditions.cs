using System.Collections.Concurrent;


/// <summary>
/// Whether a stat's condition holds for a character right now (issue #504). Stats.dat carries the condition in the id
/// itself — <c>attack_speed_while_dual_wielding_+%</c>, <c>chance_to_evade_attacks_%_while_affected_by_grace</c> — and the
/// server previously ignored the suffix, so every such stat counted unconditionally.
/// <para>
/// This is the single place that decides. A caller builds a <see cref="Subject"/> from what it knows about the character
/// and passes it to the stat sum; <see cref="Of"/> then answers per stat id. When no subject is given every condition is
/// <see cref="State.NotModelled"/> and no stat changes, so a caller that cannot supply one keeps its old behaviour.
/// </para>
/// <para>
/// The scope is the conditions the server can already answer from state it keeps: the character's weapons and shield and
/// the auras it runs. A condition whose subject the server does not model yet (<c>while affected by elusive</c>, a shrine,
/// a league buff) is reported as <see cref="State.NotModelled"/> rather than as false, so those stats keep their previous
/// behaviour and can be listed instead of silently changing.
/// </para>
/// </summary>
public static class StatConditions
{
    /// <summary>What the server can say about a stat's condition.</summary>
    public enum State
    {
        /// <summary>The id carries no condition: always counted, as before.</summary>
        None,

        /// <summary>The condition holds for this character now.</summary>
        Holds,

        /// <summary>The condition does not hold for this character now.</summary>
        Fails,

        /// <summary>The condition's subject is not modelled yet: counted unconditionally, as before, and reported.</summary>
        NotModelled,
    }

    /// <summary>Whether a state means the stat's value counts.</summary>
    public static bool Counts(State state) => state is State.None or State.Holds or State.NotModelled;

    /// <summary>The weapon/shield condition of <see cref="Of"/>, as a suffix in the stat id.</summary>
    static readonly string[] WeaponSuffixes =
    [
        DualWield, WieldingShield, UsingShield, HoldingAShield, HoldingShield,
        TwoHanded, UsingTwoHanded, OneHanded, UsingOneHanded,
    ];

    /// <summary>
    /// The conditional ids that already have a consumer of their own, gating on the same state
    /// (<c>Progression</c>, <c>AccuracyRating</c>, <c>AttackBaseCriticalStrike</c>, <c>ChargeGains</c>,
    /// <c>ChargeEffects</c>). Folding one of these onto its plain name as well would count the same mod twice, so they
    /// are left as they are.
    /// <para>
    /// The <c>_per_&lt;type&gt;_charge</c> coefficients are listed by <see cref="NamesACharge"/> instead of by id:
    /// <c>ChargeEffects.PerCharge</c> names 75 of them and expands them against the charge counts already.
    /// </para>
    /// </summary>
    static readonly HashSet<string> DedicatedConsumers = new(StringComparer.Ordinal)
    {
        "accuracy_rating_while_dual_wielding_+%",
        "add_frenzy_charge_on_kill_%_chance_while_dual_wielding",
        "attack_speed_while_dual_wielding_+%",
        "block_while_dual_wielding_%",
        "block_while_dual_wielding_claws_%",
        "critical_strike_chance_while_dual_wielding_+%",
        "critical_strike_chance_while_wielding_shield_+%",
        "critical_strike_multiplier_while_dual_wielding_+",
        "spell_block_while_dual_wielding_%",
        // The "_while_holding_shield" spelling (the shield/jewel mods) has its own consumers too: the two block stats in
        // Progression's shield block, the shield attack speed in its attack-speed block, and the endurance charge on
        // kill in ChargeGains. Left on their own id so those readers still find them and do not double count.
        "attack_speed_+%_while_holding_shield",
        "block_chance_%_while_holding_shield",
        "block_spells_chance_%_while_holding_shield",
        "endurance_charge_on_kill_percent_chance_while_holding_shield",
    };

    /// <summary>The plain stat a conditional id names: <c>spell_damage_+%_while_dual_wielding</c> →
    /// <c>spell_damage_+%</c>, and <c>damage_+%_if_you_have_frozen_enemy_recently</c> → <c>damage_+%</c>. Null when the
    /// id carries no condition this build can move.</summary>
    public static string? PlainOf(string statId)
    {
        foreach (string suffix in WeaponSuffixes)
        {
            int at = statId.IndexOf(suffix, StringComparison.Ordinal);
            if (at < 0) continue;
            return Trim(statId, at, at + suffix.Length);
        }
        // "…_recently": the whole condition sits at the end, so everything before it is the plain stat.
        var recent = RecentId.Match(statId);
        if (recent.Success) return Trim(statId, recent.Index, recent.Index + recent.Length);
        // "…_if_you_have_at_least_N_<attribute>".
        var threshold = ThresholdId.Match(statId);
        if (threshold.Success) return Trim(statId, threshold.Index, threshold.Index + threshold.Length);
        return null;

        static string? Trim(string id, int from, int to)
        {
            string plain = (id[..from] + id[to..]).Trim('_');
            return plain.Length > 0 && !string.Equals(plain, id, StringComparison.Ordinal) ? plain : null;
        }
    }

    /// <summary>
    /// Where a conditional stat goes once its condition holds (issue #504): onto its plain name, which is what the
    /// pools, the panels and the damage pipeline already read. Without this the condition is decided correctly but the
    /// value lands on an id nothing consumes, so a conditional mod still does nothing.
    /// <para>
    /// Null when the id carries no condition this build moves, when it has a consumer of its own, or when the plain
    /// name is not a stat of this build — a caller then keeps the id as it is, exactly as before.
    /// </para>
    /// </summary>
    public static string? Fold(string statId, Func<string, bool> exists)
    {
        // Kintsugi's evasion roll spells the received-hit condition with "evasion", but the evasion consumer reads
        // "evasion_rating". Keep this one table-specific name mapping here rather than guessing aliases for every stat.
        if (statId == "evasion_+%_if_hit_recently")
            return exists("evasion_rating_+%") ? "evasion_rating_+%" : null;
        if (DedicatedConsumers.Contains(statId) || NamesACharge(statId)) return null;
        if (PlainOf(statId) is not { } plain) return null;
        return exists(plain) ? plain : null;
    }

    // Suffixes that name a weapon/shield subject. Checked longest first so the specific one wins.
    const string DualWield = "_while_dual_wielding";
    const string WieldingShield = "_while_wielding_shield";
    const string UsingShield = "_while_using_a_shield";
    const string HoldingShield = "_while_holding_shield";
    const string HoldingAShield = "_while_holding_a_shield";
    const string TwoHanded = "_while_wielding_two_handed";
    const string UsingTwoHanded = "_while_using_two_handed";
    const string OneHanded = "_while_wielding_one_handed";
    const string UsingOneHanded = "_while_using_one_handed";

    const string WhileAffectedBy = "_while_affected_by_";
    const string IfAffectedBy = "_if_affected_by_";

    /// <summary>
    /// The "recently" phrase a stat id uses, the event <see cref="Subject.Recent"/> answers it with, and whether the id
    /// asks for that event's <em>absence</em>. Several phrases name the same event, so they collapse to one key
    /// (<c>hit_an_enemy</c> / <c>enemy_hit</c> are <see cref="HitEnemy"/>, while bare <c>hit</c> is
    /// <see cref="BeenHit"/>); a "not"/"haven't" phrase
    /// names its event with <c>Negated</c> set, which the caller reads as "the event did not happen". Longest phrase
    /// first, so <c>enemy_not_killed</c> wins over <c>killed</c>.
    /// </summary>
    static readonly (string Phrase, string Key, bool Negated)[] RecentEvents =
    [
        ("enemy_not_killed", KilledEnemy, true), ("enemy_shattered", ShatteredEnemy, false),
        ("enemy_killed", KilledEnemy, false), ("killed_cursed_enemy", KilledCursedEnemy, false),
        ("havent_killed", KilledEnemy, true), ("killed", KilledEnemy, false),
        ("have_crit", Crit, false), ("not_blocked", Blocked, true), ("blocked_an_attack", Blocked, false),
        ("blocked_a_spell", Blocked, false), ("blocked_a_stun", BlockedStun, false), ("have_blocked", Blocked, false),
        ("blocked", Blocked, false), ("suppressed_spell", SuppressedSpell, false), ("consumed_corpse", ConsumedCorpse, false),
        ("taken_a_savage_hit", TakenSavageHit, false), ("not_hit", BeenHit, true), ("have_not_been_hit", BeenHit, true),
        ("have_been_hit", BeenHit, false), ("been_hit", BeenHit, false), ("hit_an_enemy", HitEnemy, false),
        ("enemy_hit", HitEnemy, false), ("been_stunned", BeenStunned, false), ("been_frozen", BeenFrozen, false),
        ("used_a_fire_skill", UsedFireSkill, false), ("used_a_cold_skill", UsedColdSkill, false),
        ("used_a_warcry", UsedWarcry, false), ("warcried", UsedWarcry, false),
        ("used_movement_skill", UsedMovementSkill, false), ("used_retaliation", UsedRetaliation, false),
        ("taunted_an_enemy", TauntedEnemy, false), ("detonated_mine", DetonatedMine, false),
        ("frozen_enemy", FrozeEnemy, false), ("used_a_skill", UsedSkill, false), ("spent_mana", SpentMana, false),
        ("crit", Crit, false), ("hit", BeenHit, false),
    ];

    /// <summary>The recent-event keys <see cref="Subject.Recent"/> carries, as this class uses them.</summary>
    public const string HitEnemy = "hit_enemy", KilledEnemy = "killed_enemy", KilledCursedEnemy = "killed_cursed_enemy",
        ShatteredEnemy = "shattered_enemy", FrozeEnemy = "froze_enemy", Crit = "crit", Blocked = "blocked",
        BlockedStun = "blocked_stun", TakenSavageHit = "taken_savage_hit", BeenHit = "been_hit", BeenStunned = "been_stunned",
        BeenFrozen = "been_frozen", SuppressedSpell = "suppressed_spell", ConsumedCorpse = "consumed_corpse",
        UsedFireSkill = "used_fire_skill", UsedColdSkill = "used_cold_skill", UsedWarcry = "used_warcry",
        UsedMovementSkill = "used_movement_skill", UsedRetaliation = "used_retaliation", TauntedEnemy = "taunted_enemy",
        DetonatedMine = "detonated_mine", UsedSkill = "used_skill", SpentMana = "spent_mana";

    /// <summary>
    /// The condition of one stat id against one subject.
    /// <para>
    /// The families are the ones issue #504 names. A weapon or shield condition follows the weapons held; a
    /// <c>while affected by</c> one follows the auras running; a <c>recently</c> one follows the events the Subject
    /// reports for the last four seconds, and reads "not X" as the absence of X; an "if you have at least N X" one
    /// compares the attribute the Subject reads. Every one of them is <see cref="State.NotModelled"/> when the subject
    /// cannot answer it, so a caller without that state keeps its old behaviour.
    /// </para>
    /// <para>
    /// Per-charge and on-kill-grant ids are deliberately not decided here: <c>ChargeEffects</c> expands the
    /// <c>_per_frenzy_charge</c> / <c>per_power_charge</c> / <c>per_endurance_charge</c> coefficients against the counts
    /// and <c>ChargeGains</c> hands the charges out, both already, and editing the same ids twice would count them
    /// twice.
    /// </para>
    /// </summary>
    public static State Of(string statId, in Subject subject)
    {
        if (statId.Contains(DualWield, StringComparison.Ordinal))
            return subject.DualWield is bool d ? (d ? State.Holds : State.Fails) : State.NotModelled;
        if (statId.Contains(WieldingShield, StringComparison.Ordinal) ||
            statId.Contains(UsingShield, StringComparison.Ordinal) ||
            statId.Contains(HoldingShield, StringComparison.Ordinal) ||
            statId.Contains(HoldingAShield, StringComparison.Ordinal))
            return subject.Shield is bool s ? (s ? State.Holds : State.Fails) : State.NotModelled;
        if (statId.Contains(TwoHanded, StringComparison.Ordinal) ||
            statId.Contains(UsingTwoHanded, StringComparison.Ordinal))
            return subject.TwoHanded is bool t ? (t ? State.Holds : State.Fails) : State.NotModelled;
        if (statId.Contains(OneHanded, StringComparison.Ordinal) ||
            statId.Contains(UsingOneHanded, StringComparison.Ordinal))
            return subject.OneHanded is bool o ? (o ? State.Holds : State.Fails) : State.NotModelled;

        // "while affected by X": X is a buff the character may carry. Only an ordinary aura the server already runs can
        // be answered; a shrine, elusive or a league buff is a subject it does not model yet, so those stats keep their
        // previous behaviour rather than being treated as "the condition is false".
        if (TryAuraName(statId, out string family))
        {
            if (subject.Auras is not { } running || !IsModelledAura(family)) return State.NotModelled;
            return AuraHolds(family, running) ? State.Holds : State.Fails;
        }

        // "recently": the event happened within the last four seconds. The subject carries the events that did.
        if (TryRecentEvent(statId, out string key, out bool negated))
        {
            if (subject.Recent is not { } recent) return State.NotModelled;
            bool happened = recent.Contains(key);
            return (negated ? !happened : happened) ? State.Holds : State.Fails;
        }

        // "if you have at least N <attribute>": compare the attribute the subject reads.
        if (TryThreshold(statId, out string attribute, out int needed))
        {
            if (subject.Value is not { } value || value(attribute) is not int actual) return State.NotModelled;
            return actual >= needed ? State.Holds : State.Fails;
        }

        // Per-charge coefficients and on-kill grants are ChargeEffects' and ChargeGains' already: an id naming a charge
        // type is left to them, not folded here, so the same value is not counted twice.
        if (NamesACharge(statId)) return State.NotModelled;

        // Anything else that still looks conditional is not modelled: keep it as it was, and let a caller list it.
        if (statId.Contains(WhileAffectedBy, StringComparison.Ordinal) ||
            statId.Contains(IfAffectedBy, StringComparison.Ordinal) ||
            statId.Contains("_while_", StringComparison.Ordinal) ||
            statId.Contains("_if_you_have_", StringComparison.Ordinal) ||
            statId.Contains("_if_", StringComparison.Ordinal))
            return State.NotModelled;

        return State.None;
    }

    /// <summary>Whether an id is one ChargeEffects (<c>_per_&lt;type&gt;_charge</c>) or ChargeGains
    /// (<c>add_&lt;type&gt;_charge_on_kill</c>) already owns.</summary>
    public static bool NamesACharge(string statId) =>
        PerChargeType.IsMatch(statId) || AddChargeOnKill.IsMatch(statId);

    static readonly System.Text.RegularExpressions.Regex PerChargeType =
        new(@"^(?:.*_)?(?:per|current|minimum|maximum)_(?:frenzy|power|endurance|inspiration|challenger|siphoning|brutal|absorption|affliction)_charges?", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex AddChargeOnKill =
        new(@"^add_(?:frenzy|power|endurance|inspiration|challenger|brutal|absorption)_charge", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex RecentId =
        new(@"(?:^|_)(?:if_)?(?:you_)?(?:have_)?(?<negation>havent_|not_)?(?<phrase>[a-z_]+?)_recently(?:_|$|%|\+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The event a <c>recently</c> id names, and whether the id asks for the event's absence.</summary>
    public static bool TryRecentEvent(string statId, out string key, out bool negated)
    {
        key = ""; negated = false;
        var m = RecentId.Match(statId);
        if (!m.Success) return false;
        string phrase = m.Groups["phrase"].Value;
        // A "not"/"haven't" prefix negates whatever event the rest of the phrase names.
        bool phraseNegated = phrase.StartsWith("not_", StringComparison.Ordinal)
            || phrase.StartsWith("havent_", StringComparison.Ordinal);
        bool writtenNegated = m.Groups["negation"].Success ^ phraseNegated;
        string bare = phraseNegated ? phrase[(phrase.IndexOf('_') + 1)..] : phrase;
        foreach (var (p, k, tableNegated) in RecentEvents)
            if (bare == p || bare.EndsWith("_" + p, StringComparison.Ordinal))
            {
                key = k;
                negated = writtenNegated ^ tableNegated;
                return true;
            }
        return false;
    }

    static readonly System.Text.RegularExpressions.Regex ThresholdId =
        new(@"_if_you_have_at_least_(?<n>\d+)_(?<attr>[a-z]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The attribute and the minimum an "if you have at least N X" id asks for.</summary>
    public static bool TryThreshold(string statId, out string attribute, out int needed)
    {
        attribute = ""; needed = 0;
        var m = ThresholdId.Match(statId);
        if (!m.Success || !int.TryParse(m.Groups["n"].Value, out needed)) return false;
        attribute = m.Groups["attr"].Value switch
        {
            "strength" => "strength", "dexterity" => "dexterity", "intelligence" => "intelligence",
            "rage" => "rage", "mana" => "mana", "life" => "life",
            _ => "",
        };
        return attribute.Length > 0;
    }

    /// <summary>The subject a <c>while_affected_by_X</c> / <c>if_affected_by_X</c> stat names, without its qualifiers.</summary>
    public static bool TryAuraName(string statId, out string family)
    {
        family = "";
        int at = statId.IndexOf(WhileAffectedBy, StringComparison.Ordinal);
        int skip = WhileAffectedBy.Length;
        if (at < 0)
        {
            at = statId.IndexOf(IfAffectedBy, StringComparison.Ordinal);
            skip = IfAffectedBy.Length;
        }
        if (at < 0) return false;
        family = statId[(at + skip)..];
        return family.Length > 0;
    }

    /// <summary>Whether the named family holds: an exact name, or an <c>X_and_Y</c> list where all must hold.</summary>
    public static bool AuraHolds(string family, IReadOnlySet<string> running)
    {
        var parts = family.Split("_and_", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts) if (!running.Contains(part)) return false;
        return true;
    }

    /// <summary>Every aura family name the given client state is running, as the stat ids spell them.</summary>
    public static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new();

    /// <summary>The ordinary auras a stat id can name, lower-cased as the ids spell them. This is the server's own
    /// ordinary aura set (<c>AuraCatalog</c>), so a name outside it is a subject nothing models yet.</summary>
    static readonly HashSet<string> ModelledAuras = new(StringComparer.Ordinal)
    {
        "clarity", "vitality", "precision", "haste", "determination", "grace", "discipline",
        "anger", "hatred", "wrath", "zealotry", "malevolence", "envy", "pride",
        "purity_of_elements", "purity_of_fire", "purity_of_ice", "purity_of_lightning",
        "a_herald", "herald_of_ash", "herald_of_ice", "herald_of_thunder", "herald_of_purity", "herald_of_agony",
    };

    /// <summary>Whether every name in a <c>X_and_Y</c> family is an aura the server runs.</summary>
    public static bool IsModelledAura(string family) =>
        family.Split("_and_", StringSplitOptions.RemoveEmptyEntries).All(p => ModelledAuras.Contains(p));

    /// <summary>The stat-id spelling of an aura the server runs, from an <c>AuraCatalog</c> effect id ("Grace" → "grace").
    /// Null when it is not an ordinary aura this build knows.</summary>
    public static string? StatNameOf(string effectId)
    {
        if (string.IsNullOrEmpty(effectId)) return null;
        var snake = new System.Text.StringBuilder(effectId.Length + 4);
        foreach (char c in effectId)
        {
            if (char.IsUpper(c) && snake.Length > 0) snake.Append('_');
            snake.Append(char.ToLowerInvariant(c));
        }
        string name = snake.ToString();
        return ModelledAuras.Contains(name) ? name : null;
    }

    /// <summary>Builds the aura set a stat's <c>while_affected_by_X</c> is answered from: every ordinary aura the given
    /// names cover, in stat-id spelling.</summary>
    public static IReadOnlySet<string> AuraSet(IEnumerable<string> effectIds)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in effectIds)
            if (StatNameOf(id) is { } name) set.Add(name);
        return set;
    }

    /// <summary>What a caller can answer about the character a stat is being summed for. Null fields are unknown.</summary>
    public readonly record struct Subject(
        bool? DualWield = null, bool? Shield = null, bool? TwoHanded = null, bool? OneHanded = null,
        IReadOnlySet<string>? Auras = null,
        IReadOnlySet<string>? Recent = null,
        Func<string, int?>? Value = null)
    {
        /// <summary>Nothing known: every condition is NotModelled and no stat changes.</summary>
        public static Subject Unknown => new();
    }
}
