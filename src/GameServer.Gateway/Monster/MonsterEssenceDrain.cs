/// <summary>
/// The player's Essence Drain debuff on a monster (BuffDefinitions row 503 <c>siphon_damage</c>): chaos damage over time
/// put on by the projectile's hit. Instances do not stack: each hit adds one with its own expiry and only the strongest
/// active one deals damage, so a weaker hit never shortens or weakens a stronger debuff and takes over when it ends.
/// Contagion spreads it (ActiveSkills essence_drain: "The debuff is spread by Contagion"): see <see cref="TakeSpread"/>.
/// </summary>
public sealed class MonsterEssenceDrain
{
    /// <summary>What one hit put on the monster: damage per second before the monster's mitigation, duration, the share of
    /// the damage dealt that the caster leeches as life (siphon_life_leech_from_damage_permyriad), the caster's skill id and
    /// the caster: only that player leeches, also from a Contagion spread of it.</summary>
    public sealed record Cast(double DamagePerSecond, double Seconds, int LeechPermyriad = 0, ushort SkillId = 0,
        ClientState? Caster = null);

    // Each hit's instance counts from Start (when it landed) to Until.
    readonly List<(Cast Cast, double Start, double Until)> instances = [];
    double last, carry;
    // The strongest instance it had when it died (Clear), with its expiry, for Contagion's spread on death.
    (Cast Cast, double Start, double Until)? diedWith;

    /// <summary>The strongest instance affecting the monster at the last advance; null when none is.</summary>
    public Cast? Active => Strongest(last)?.Cast;

    public bool Idle => instances.Count == 0;

    /// <summary>When the last instance ends.</summary>
    public double Until => instances.Count == 0 ? 0 : instances.Max(i => i.Until);

    public void Apply(double now, Cast cast)
    {
        if (!(cast.DamagePerSecond > 0) || !(cast.Seconds > 0)) return;
        if (instances.Count == 0)
        {
            last = now;
            carry = 0;
        }
        // A hit timed before the last advance starts counting from that advance (that time is already paid).
        double start = Math.Max(now, last);
        instances.Add((cast, start, start + cast.Seconds));
    }

    /// <summary>The whole chaos damage dealt from the last advance to <paramref name="now"/> by the strongest instance at
    /// each moment, multiplied by the monster's chaos mitigation <paramref name="factor"/>; the fraction carries to the next
    /// advance. Ended instances are dropped. Also returns the life leeched from that damage, and hands each dealing
    /// instance's cast, the damage it dealt and its leech to <paramref name="dealt"/> in time order (instances may have
    /// different casters: the leech and a kill belong to the one dealing).</summary>
    public (int Damage, double Leech) Advance(double now, double factor, Action<Cast, double, double>? dealt = null)
    {
        if (instances.Count == 0 || !(now > last)) return (0, 0);
        double damage = carry, leech = 0, t = last;
        while (t < now)
        {
            // Up to the next start or expiry of any instance (the strongest can change there) or now; an instance applied
            // after an earlier one expired deals nothing for the gap before it landed.
            double next = now;
            foreach (var i in instances)
            {
                if (i.Until > t) next = Math.Min(next, i.Until);
                if (i.Start > t) next = Math.Min(next, i.Start);
            }
            if (Strongest(t) is { } strongest)
            {
                double segment = strongest.Cast.DamagePerSecond * (next - t) * Math.Max(0, factor);
                double share = segment * strongest.Cast.LeechPermyriad / 10000.0;
                damage += segment;
                leech += share;
                dealt?.Invoke(strongest.Cast, segment, share);
            }
            t = next;
        }
        last = now;
        instances.RemoveAll(i => i.Until <= now);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, damage)));
        carry = instances.Count == 0 ? 0 : damage - whole;
        return (whole, leech);
    }

    public void Clear()
    {
        if (Strongest(last) is { } strongest) diedWith = strongest;
        instances.Clear();
        carry = 0;
    }

    /// <summary>The debuff Contagion spreads from this monster on its death, once: its strongest instance now, else the one
    /// it died with (a tick may clear a dead monster before the kill's spread runs), with what is left of its duration as
    /// Seconds (wiki: the proc spreads "the remaining duration of both effects"); null when none is left.</summary>
    public Cast? TakeSpread(double now)
    {
        var taken = Strongest(now) ?? Strongest(last) ?? diedWith;
        diedWith = null;
        instances.Clear();
        carry = 0;
        return taken is { } t && t.Until > now ? t.Cast with { Seconds = t.Until - now } : null;
    }

    (Cast Cast, double Start, double Until)? Strongest(double t)
    {
        (Cast Cast, double Start, double Until)? best = null;
        foreach (var i in instances)
            if (i.Start <= t && i.Until > t && (best is null || i.Cast.DamagePerSecond > best.Value.Cast.DamagePerSecond)) best = i;
        return best;
    }
}
