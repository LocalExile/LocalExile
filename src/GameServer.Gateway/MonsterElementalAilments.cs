using GameServer.Core.World;

/// <summary>
/// The chill, freeze and shock the player's hits put on one monster (wiki Chill, Freeze and Shock; PoB CalcOffence's
/// non-damaging ailments). Each inflicted ailment keeps its own magnitude and duration; only the strongest of a kind
/// applies. They stay on a corpse until they run out. Guarded by the Monsters lock, on the connection's pauseable clock.
/// </summary>
sealed class MonsterElementalAilments
{
    internal enum Kind { Chill, Freeze, Shock }
    internal sealed record Effect(Kind Kind, double From, double Until, double Magnitude);
    readonly List<Effect> effects = [];
    // What the client was last sent: each kind's buff instance and magnitude, the action speed stat and the factor its
    // walk speed carries.
    readonly Dictionary<Kind, (ushort Instance, int Magnitude)> shown = [];
    public int SentActionSpeed;
    public double WalkFactor = 1;

    public bool Idle => effects.Count == 0 && shown.Count == 0 && SentActionSpeed == 0 && WalkFactor == 1;

    public double Magnitude(Kind kind, double now) => effects
        .Where(e => e.Kind == kind && e.From <= now && now < e.Until).Select(e => e.Magnitude).DefaultIfEmpty().Max();

    /// <summary>Action speed: 0 while frozen, else less by the chill's effect.</summary>
    public double ActionFactor(double now) => Magnitude(Kind.Freeze, now) > 0 ? 0 : 1 - Magnitude(Kind.Chill, now) / 100;

    /// <summary>Chill and shock effect: floor(50 × (damage / threshold)^0.4 × effect modifiers), up to the cap, and none
    /// below 5% (the modifiers count before both limits).</summary>
    internal static int Effectiveness(double damage, double threshold, double modifiers, int cap)
    {
        if (damage <= 0 || threshold <= 0) return 0;
        int effect = (int)Math.Min(cap, Math.Floor(50 * Math.Pow(damage / threshold, .4) * modifiers));
        return effect >= 5 ? effect : 0;
    }

    static string Name(Kind kind) => kind.ToString().ToLowerInvariant();

    static bool Immune(Kind kind, Func<string, int> get)
    {
        string past = kind switch { Kind.Chill => "chilled", Kind.Freeze => "frozen", _ => "shocked" };
        return get($"immune_to_{Name(kind)}") != 0 || get($"cannot_be_{past}") != 0 || get($"base_cannot_be_{past}") != 0;
    }

    /// <summary>
    /// One hit's ailments from the damage the monster took per type (critical multiplier included) against its ailment
    /// threshold. Chill: every hit with cold damage, 2 s. Freeze: a critical strike or chance_to_freeze_% with cold
    /// damage, 0.06 s per 1% of the threshold up to 3 s, then duration modifiers, discarded under 0.3 s. Shock: a
    /// critical strike or chance_to_shock_% with lightning damage, and cold damage too with cold_damage_can_shock
    /// (Stormy Seas: the magnitude, not the chance), 2 s. <paramref name="attacker"/> gives the player's and the skill's
    /// summed stats, <paramref name="target"/> the monster's own. The effects added are returned.
    /// </summary>
    public List<Effect> Inflict(double cold, double lightning, bool critical, double threshold, double now,
        Func<string, int> attacker, Func<string, int> target, Random random)
    {
        var added = new List<Effect>();
        if (threshold <= 0) return added;
        double Modifier(string attackerStat, string targetStat) =>
            Math.Max(0, 1 + attacker(attackerStat) / 100.0) * Math.Max(0, 1 + target(targetStat) / 100.0);
        void Add(Kind kind, double magnitude, double baseSeconds)
        {
            double seconds = baseSeconds * Math.Max(0, 1 + attacker($"virtual_{Name(kind)}_duration_+%") / 100.0)
                * Math.Max(0, 1 - target($"base_self_{Name(kind)}_duration_-%") / 100.0);
            if (magnitude <= 0 || seconds <= 0 || kind == Kind.Freeze && seconds < .3 || Immune(kind, target)) return;
            var effect = new Effect(kind, now, now + seconds, magnitude);
            // A weaker one that also ends sooner can never be the strongest again.
            effects.RemoveAll(e => e.Until <= now || e.Kind == kind && e.Magnitude <= magnitude && e.Until <= effect.Until);
            effects.Add(effect);
            added.Add(effect);
        }
        Add(Kind.Chill, Effectiveness(cold, threshold,
            Modifier("virtual_chill_effect_+%", "chill_effectiveness_on_self_+%"), 30), 2);
        if (cold > 0 && (critical || RandomRolls.Chance(attacker("chance_to_freeze_%"), 100, random)))
            Add(Kind.Freeze, 100, Math.Min(3, 6 * cold / threshold));
        double shocking = lightning + (attacker("cold_damage_can_shock") != 0 ? cold : 0);
        if (shocking > 0 && (critical || RandomRolls.Chance(attacker("chance_to_shock_%"), 100, random)))
            Add(Kind.Shock, Effectiveness(shocking, threshold,
                Modifier("virtual_shock_effect_+%", "shocked_effect_on_self_+%"), 50), 2);
        return added;
    }

    /// <summary>The buffs to take off and put on so the client shows what applies at <paramref name="now"/>: a kind
    /// whose strongest magnitude changed is sent again with a new instance; expired effects are dropped.</summary>
    public List<(Kind Kind, ushort? Removed, ushort? Added, int Magnitude)> Present(double now, Func<ushort> instance)
    {
        effects.RemoveAll(e => e.Until <= now);
        var changes = new List<(Kind, ushort?, ushort?, int)>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            int magnitude = (int)Magnitude(kind, now);
            bool had = shown.TryGetValue(kind, out var old);
            if (had ? old.Magnitude == magnitude : magnitude == 0) continue;
            ushort? added = magnitude > 0 ? instance() : null;
            if (added is ushort a) shown[kind] = (a, magnitude);
            else shown.Remove(kind);
            changes.Add((kind, had ? old.Instance : null, added, magnitude));
        }
        return changes;
    }
}
