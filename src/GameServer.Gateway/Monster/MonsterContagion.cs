/// <summary>
/// The player's Contagion on a monster (BuffDefinitions row 504 <c>contagion</c>): one chaos damage over time. Contagion does
/// not stack: casting it again on an affected monster keeps the stronger damage and the later expiry. When the monster dies
/// while affected, the debuff spreads (Game.SpreadContagion) with the damage and duration it was cast with.
/// </summary>
public sealed class MonsterContagion
{
    /// <summary>What a cast put on the monster: its damage per second before the monster's mitigation, its full duration, the
    /// radius it spreads in, the damage a spread keeps (Contagion of Subsiding: three quarters each time) and the caster's
    /// skill id: the contagion buff (BuffDefinitions __col7 8, as siphon_damage) names it as its source skill. SpreadAo is
    /// the caster's Contagion skin art drawn at each spread (null: the default).</summary>
    public sealed record Cast(double DamagePerSecond, double Seconds, int Radius, double SpreadMore = 1, ushort SkillId = 0,
        string? SpreadAo = null);

    // owed: the damage (before mitigation) of the time up to a recast, at the damage it had then.
    double until, last, carry, owed;
    // The cast whose damage the last Advance returned (null when it returned none). If that damage killed the monster
    // (Died), it is what spreads, even when the same Advance also expired it.
    Cast? dealt;

    /// <summary>The cast affecting the monster now; null when none is.</summary>
    public Cast? Active { get; private set; }

    /// <summary>The cast it was affected by when it died (Clear), for the spread on death.</summary>
    public Cast? DiedWith { get; private set; }

    /// <summary>The native <c>contagion</c> buff instance on the monster.</summary>
    public ushort? Buff;

    public bool Idle => Active is null && Buff is null;

    public double Until => until;

    public void Apply(double now, Cast cast)
    {
        if (!(cast.DamagePerSecond > 0) || !(cast.Seconds > 0)) return;
        if (Active is not null && now >= until)
        {
            // The old application expired before this recast (with no Advance in between): it no longer competes, but
            // the damage it still owed up to its expiry is kept.
            owed += Active.DamagePerSecond * Math.Max(0, until - last);
            last = now;
            Active = cast;
            until = now + cast.Seconds;
            return;
        }
        if (Active is null)
        {
            last = now;
            carry = 0;
            owed = 0;
            Active = cast;
            until = now + cast.Seconds;
            return;
        }
        if (now > last)
        {
            owed += Active.DamagePerSecond * Math.Max(0, Math.Min(now, until) - last);
            last = now;
        }
        if (cast.DamagePerSecond >= Active.DamagePerSecond) Active = cast;
        until = Math.Max(until, now + cast.Seconds);
    }

    /// <summary>The whole chaos damage dealt from the last advance to <paramref name="now"/>, multiplied by the monster's chaos
    /// mitigation <paramref name="factor"/>; the fraction carries to the next advance. Expires the debuff at its end.</summary>
    public int Advance(double now, double factor)
    {
        dealt = null;
        if (Active is null || !(now > last)) return 0;
        double end = Math.Min(now, until);
        double damage = carry + (owed + Active.DamagePerSecond * Math.Max(0, end - last)) * Math.Max(0, factor);
        owed = 0;
        last = now;
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, damage)));
        if (whole > 0) dealt = Active;
        if (now >= until)
        {
            Active = null;
            carry = 0;
        }
        else carry = damage - whole;
        return whole;
    }

    public void Clear()
    {
        if (Active is not null) DiedWith = Active;
        Active = null;
        carry = 0;
        owed = 0;
        dealt = null;
    }

    /// <summary>The monster died of the damage the last Advance returned: keeps that cast for the spread on death (also
    /// when that Advance expired it), then clears. A nonlethal expiry never calls this, so it leaves no spread.</summary>
    public void Died()
    {
        var cast = Active ?? dealt;
        Clear();
        DiedWith = cast;
    }

    /// <summary>The cast to spread on death, once: the active one or the one it died with.</summary>
    public Cast? TakeSpread()
    {
        var cast = Active ?? DiedWith;
        DiedWith = null;
        Clear();
        DiedWith = null;
        return cast;
    }
}
