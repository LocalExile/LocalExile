/// <summary>
/// The player's Contagion on a monster (BuffDefinitions row 504 <c>contagion</c>): one chaos damage over time. Contagion does
/// not stack: each application keeps its own duration and only the strongest active one deals damage, so a weaker cast
/// never lengthens a stronger one and takes over when it ends. When the monster dies while affected, the strongest
/// application spreads (Game.SpreadContagion) with the time it had left, as in PoE1 (the spread does not refresh it).
/// </summary>
public sealed class MonsterContagion
{
    /// <summary>What a cast put on the monster: its damage per second before the monster's mitigation, its duration, the
    /// radius it spreads in, the damage a spread keeps (Contagion of Subsiding: three quarters each time) and the caster's
    /// skill id: the contagion buff (BuffDefinitions __col7 8, as siphon_damage) names it as its source skill. SpreadAo is
    /// the caster's Contagion skin art drawn at each spread (null: the default).</summary>
    public sealed record Cast(double DamagePerSecond, double Seconds, int Radius, double SpreadMore = 1, ushort SkillId = 0,
        string? SpreadAo = null);

    // Each application counts from Start (when it landed) to Until.
    readonly List<(Cast Cast, double Start, double Until)> instances = [];
    // Applications that ended in the last Advance: a death inside that Advance may still have been affected by one.
    readonly List<(Cast Cast, double Start, double Until)> ended = [];
    double previous, last, carry;
    // What spreads on death: the cast with its remaining duration already set as Seconds.
    Cast? diedWith;

    /// <summary>The strongest application affecting the monster at the last advance; null when none is.</summary>
    public Cast? Active => Strongest(instances, last)?.Cast;

    /// <summary>The native <c>contagion</c> buff instance on the monster.</summary>
    public ushort? Buff;

    public bool Idle => instances.Count == 0 && Buff is null;

    /// <summary>When the last application ends.</summary>
    public double Until => instances.Count == 0 ? 0 : instances.Max(i => i.Until);

    public void Apply(double now, Cast cast)
    {
        if (!(cast.DamagePerSecond > 0) || !(cast.Seconds > 0)) return;
        if (instances.Count == 0)
        {
            previous = last = now;
            carry = 0;
        }
        // A cast timed before the last advance counts from that advance (that time is already paid).
        double start = Math.Max(now, last), until = start + cast.Seconds;
        // An application no stronger than this one that ends no later can never deal damage again.
        instances.RemoveAll(i => i.Start <= start && i.Cast.DamagePerSecond <= cast.DamagePerSecond && i.Until <= until);
        instances.Add((cast, start, until));
    }

    /// <summary>The whole chaos damage dealt from the last advance to <paramref name="now"/> by the strongest application at
    /// each moment, multiplied by the monster's chaos mitigation <paramref name="factor"/>; the fraction carries to the next
    /// advance. Ended applications are dropped.</summary>
    public int Advance(double now, double factor)
    {
        ended.Clear();
        if (instances.Count == 0 || !(now > last)) return 0;
        double damage = carry, t = last;
        while (t < now)
        {
            // Up to the next start or end of any application (the strongest can change there) or now.
            double next = now;
            foreach (var i in instances)
            {
                if (i.Until > t) next = Math.Min(next, i.Until);
                if (i.Start > t) next = Math.Min(next, i.Start);
            }
            if (Strongest(instances, t) is { } strongest)
                damage += strongest.Cast.DamagePerSecond * (next - t) * Math.Max(0, factor);
            t = next;
        }
        previous = last;
        last = now;
        ended.AddRange(instances.Where(i => i.Until <= now));
        instances.RemoveAll(i => i.Until <= now);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, damage)));
        carry = instances.Count == 0 ? 0 : damage - whole;
        return whole;
    }

    /// <summary>Ends the debuff (the monster died or left). What it was affected by at the last advance is kept for the spread
    /// on death, with the time it had left then.</summary>
    public void Clear() => Clear(last);

    /// <summary>The monster died of the damage the last Advance returned, <paramref name="fraction"/> of the way through that
    /// Advance's interval (its life before the tick over the tick's damage). The application affecting it at that moment
    /// spreads with the time it had left then, also when the same Advance ended it; nothing spreads when none was left.</summary>
    public void Died(double fraction = 1) => Clear(previous + Math.Clamp(fraction, 0, 1) * (last - previous));

    void Clear(double at)
    {
        var strongest = Strongest(instances, at) ?? Strongest(ended, at);
        if (strongest is { } s && s.Until > at) diedWith = s.Cast with { Seconds = s.Until - at };
        instances.Clear();
        ended.Clear();
        carry = 0;
    }

    /// <summary>The cast to spread on death, once, with its remaining duration as Seconds: the strongest application now, else
    /// the one it died with; null when none had time left.</summary>
    public Cast? TakeSpread(double now)
    {
        var current = Strongest(instances, now);
        var cast = current is { } c ? c.Cast with { Seconds = c.Until - now } : diedWith;
        diedWith = null;
        instances.Clear();
        ended.Clear();
        carry = 0;
        return cast is { Seconds: > 0 } ? cast : null;
    }

    static (Cast Cast, double Start, double Until)? Strongest(List<(Cast Cast, double Start, double Until)> list, double t)
    {
        (Cast Cast, double Start, double Until)? best = null;
        foreach (var i in list)
            if (i.Start <= t && i.Until > t && (best is null || i.Cast.DamagePerSecond > best.Value.Cast.DamagePerSecond)) best = i;
        return best;
    }
}
