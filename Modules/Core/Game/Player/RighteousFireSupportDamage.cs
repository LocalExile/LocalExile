public static partial class Game
{
    static void SetRighteousFireSupportProfileLocked(ClientState conn, RighteousFireState rf,
        IReadOnlyDictionary<uint, int> own, double now)
    {
        var more = (own.GetValueOrDefault(Stats.Key(Lifetap.DamageStat)!.Value),
            own.GetValueOrDefault(Stats.Key(InspirationElementalMore)!.Value));
        if (rf.ConditionalSupportMore == more) return;
        rf.ConditionalSupportMore = more;
        RecordRighteousFireSupportChangeLocked(conn, rf, now);
    }

    // Keep earned changes until the player tick consumes them. A new payment or link change replaces
    // the future expiry points. The two more multipliers multiply only while both effects are active.
    static void RecordRighteousFireSupportChangeLocked(ClientState conn, RighteousFireState rf, double now)
    {
        var (lifePct, perCharge) = rf.ConditionalSupportMore;
        double lifeUntil = lifePct != 0 ? conn.Lifetap.Until : now;
        int charges = conn.SupportGems.InspirationCharges;
        double chargeUntil = perCharge != 0 && charges > 0 ? conn.SupportGems.InspirationUntil : now;
        if (rf.SupportDamageChanges is null && lifeUntil <= now && chargeUntil <= now) return;
        var changes = rf.SupportDamageChanges ??= [];
        while (changes.Count > 0 && changes[^1].At >= now) changes.RemoveAt(changes.Count - 1);

        void Add(double at)
        {
            double life = at < lifeUntil ? lifePct / 100d : 0;
            double inspiration = at < chargeUntil ? (double)perCharge * charges / 100 : 0;
            double extra = life + inspiration + life * inspiration;
            if (extra != (changes.Count == 0 ? 0 : changes[^1].Extra)) changes.Add((at, extra));
        }
        Add(now);
        double first = Math.Min(lifeUntil, chargeUntil), last = Math.Max(lifeUntil, chargeUntil);
        if (first > now) Add(first);
        if (last > now && last != first) Add(last);
    }

    static double RighteousFireTimedSupportFactor(RighteousFireState rf, double end, double seconds)
    {
        if (rf.SupportDamageChanges is not { Count: > 0 } changes || seconds <= 0) return 1;
        double start = end - seconds, extra = 0;
        int consumed = 0;
        for (int i = 0; i < changes.Count && changes[i].At <= end; i++)
        {
            var (at, more) = changes[i];
            double next = i + 1 < changes.Count ? changes[i + 1].At : double.PositiveInfinity;
            extra += more * Math.Max(0, Math.Min(end, next) - Math.Max(start, at));
            if (next <= end || more == 0) consumed = i + 1;
        }
        changes.RemoveRange(0, consumed);
        return 1 + extra / seconds;
    }
}
