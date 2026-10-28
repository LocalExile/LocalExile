namespace GameServer.Core.World;

/// <summary>
/// The resistance one type of a player's outgoing hit faces, once the hit's critical-strike elemental rules are
/// applied (Path of Building's CalcOffence.lua, pinned 16de4b8: <c>useThisResist</c> at 494..509 with the hit's
/// effMult branch at 3581..3594). The critical strike is the hit's own already-rolled boolean, decided before
/// mitigation: an elemental hit that critically strikes with <c>critical_strikes_ignore_elemental_resistances</c>
/// (Stats row 3625, key 3626) mitigates with an effective resistance of zero, so even a negative resistance no
/// longer amplifies the hit, and it overrides penetration; <c>attack_critical_strikes_ignore_elemental_resistances</c>
/// (row 12971, key 12972) is the attack-only form. A non-critical elemental hit keeps its capped resistance, and
/// <c>non_critical_strikes_penetrate_elemental_resistances_%</c> (row 3884, key 3885, Inevitable Judgement's 10)
/// stacks with the hit's general penetration (a target's <see cref="DamageModel.ResistanceValue.Penetration"/>,
/// such as the unconditional pen stats) and the pass subtracts the sum after the cap, as PoB's <c>resist − pen</c>
/// without another floor. <c>cannot_penetrate_or_ignore_elemental_resistances</c> (row 12051, key 12052) suppresses
/// both the ignore and every elemental penetration (PoB's <c>CannotElePenIgnore</c>). Physical and chaos are not
/// elemental — chaos keeps its own attack-only penetration path — and a damage-over-time tick is not a strike: it
/// can neither critically strike nor penetrate. A target's own "resistance cannot be penetrated" is a penetration
/// guard only; nothing here treats it as ignore protection.
///
/// Inevitable Judgement (AscendancyInquisitor8, graph node 48214) grants the generic critical ignore (row 3625,
/// key 3626) and the 10% non-critical penetration (row 3884, key 3885); ModParser.lua 2909/2910/3581: the ignore
/// flag carries the CriticalStrike condition (the attack-only one also SkillType Attack), the penetration a
/// negated CriticalStrike.
/// </summary>
public static class HitResistances
{
    /// <summary>The hit's resistance stats as a stats dictionary holds them: 1 for the flags, a percentage for the
    /// penetration.</summary>
    public readonly record struct Mods(int CriticalIgnoreElemental, int AttackCriticalIgnoreElemental,
        int NonCriticalPenetration, int CannotPenetrateOrIgnore)
    {
        /// <summary>The four stats by name, through the caller's name → key lookup (the client's stat key is the
        /// Stats.dat row + 1). A stat the source does not carry reads 0.</summary>
        public static Mods From(IReadOnlyDictionary<uint, int> stats, Func<string, uint?> key) => new(
            Get(stats, key("critical_strikes_ignore_elemental_resistances")),
            Get(stats, key("attack_critical_strikes_ignore_elemental_resistances")),
            Get(stats, key("non_critical_strikes_penetrate_elemental_resistances_%")),
            Get(stats, key("cannot_penetrate_or_ignore_elemental_resistances")));

        private static int Get(IReadOnlyDictionary<uint, int> stats, uint? key) => key is uint k ? stats.GetValueOrDefault(k) : 0;
    }

    /// <summary>The already-decided facts the resistance needs: whether the hit critically strikes, whether it is
    /// an attack (rather than a spell), and whether it is a damage-over-time tick (a hit pass is never one; the
    /// default covers that case for the callers that only build strikes).</summary>
    public readonly record struct Strike(bool Critical, bool Attack, bool OverTime = false);

    /// <summary>Whether the hit's ignore flags apply: a critical strike with the generic stat, or an attack with
    /// the attack-only one. A damage-over-time tick can never meet the critical condition.</summary>
    private static bool Ignored(in Strike strike, in Mods mods) =>
        strike.Critical && (mods.CriticalIgnoreElemental != 0 ||
            (strike.Attack && mods.AttackCriticalIgnoreElemental != 0));

    /// <summary>
    /// The resistance one type of the hit mitigates with, resolved for the pass that applies cap-then-pen
    /// (<see cref="DamageModel.Mitigate(DamagePacket, Func{DamageType, DamageModel.ResistanceValue})"/>: the value
    /// is capped and the penetration subtracted once, no second clamp). The ignore returns a zero resistance with
    /// no penetration — the effective resistance is zero, negative included, and nothing is left to penetrate.
    /// The suppressor zeroes the penetration (the target's and the conditional one) and keeps the plain capped
    /// value. Otherwise the non-critical hit's conditional penetration stacks with the target's own general one;
    /// a critical hit without the ignore takes no conditional penetration, because the stat's condition is a
    /// negated CriticalStrike. A non-elemental part or a damage-over-time tick passes the target through untouched,
    /// keeping its own penetration path.
    /// </summary>
    public static double FinalValue(DamageType type, DamageModel.ResistanceValue target,
        in Strike strike, in Mods mods, bool invertElemental = false)
    {
        var effective = Effective(type, target, strike, mods);
        double resistance = DamageModel.Capped(effective.Value, effective.Maximum);
        if (invertElemental && !strike.OverTime && DamageTypes.IsElemental(type)
            && !(mods.CannotPenetrateOrIgnore == 0 && Ignored(strike, mods))) resistance = -resistance;
        return resistance - effective.Penetration;
    }

    public static DamageModel.ResistanceValue Effective(DamageType type, DamageModel.ResistanceValue target,
        in Strike strike, in Mods mods)
    {
        if (!DamageTypes.IsElemental(type) || strike.OverTime) return target;
        if (mods.CannotPenetrateOrIgnore != 0) return target with { Penetration = 0 };
        if (Ignored(strike, mods)) return new(0);
        return target with { Penetration = target.Penetration + (strike.Critical ? 0 : mods.NonCriticalPenetration) };
    }

    /// <summary>What the rules did to one type, for the hit's log line: "fire 40 ignored", "cold 30→20 (pen 10)",
    /// or "fire 40 (cannot penetrate or ignore)" when the suppressor held a rule back; null when the type mitigated
    /// plainly (a rule that would not have changed the resistance names nothing).</summary>
    public static string? Describe(DamageType type, DamageModel.ResistanceValue target, in Strike strike, in Mods mods)
    {
        if (!DamageTypes.IsElemental(type) || strike.OverTime) return null;
        string name = DamageTypes.Name(type).ToLowerInvariant();
        int capped = DamageModel.Capped(target.Value, target.Maximum);
        if (mods.CannotPenetrateOrIgnore != 0)
        {
            bool ignored = Ignored(strike, mods), held = !strike.Critical && mods.NonCriticalPenetration != 0;
            return ignored || held ? $"{name} {capped} (cannot penetrate or ignore)" : null;
        }
        if (Ignored(strike, mods)) return $"{name} {capped} ignored";
        int pen = target.Penetration + (strike.Critical ? 0 : mods.NonCriticalPenetration);
        return pen != 0 ? $"{name} {capped}→{capped - pen} (pen {pen})" : null;
    }
}
