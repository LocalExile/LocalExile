// Earned recipient debuffs outlive their emitting reservation; no Game callback is retained.
sealed class SpiderWebState
{
    public sealed record Web(uint Caster, double From, double Until, int Taken, int Resistance, int Damage);
    public readonly List<Web> Webs = new();
    public ushort PresentedBuff, HinderBuff, ShroudBuff;
    public int PresentedCount;
    public double PresentedUntil;
    public readonly Dictionary<uint, int> PresentedStats = new();
    public double ExpiryRate = 1;

    public int Count(double now, uint? caster = null) => Webs.Count(w => w.From <= now && w.Until > now && (caster is null || w.Caster == caster));
    public double Until(double now) => Webs.Where(w => w.From <= now && w.Until > now).Select(w => w.Until).DefaultIfEmpty().Max();
    public int Taken(double now) => Webs.Where(w => w.From <= now && w.Until > now).GroupBy(w => w.Caster)
        .Select(g => g.Sum(w => w.Taken)).DefaultIfEmpty().Max();
    public int Resistance(double now) => Webs.Where(w => w.From <= now && w.Until > now).Select(w => w.Resistance).DefaultIfEmpty().Min();
    public int Damage(double now) => Webs.Where(w => w.From <= now && w.Until > now).Select(w => w.Damage).DefaultIfEmpty().Min();
    public void Apply(uint caster, double now, double seconds, int maximum, int taken, int resistance, int damage)
    {
        Expire(now);
        if (maximum <= 0 || seconds <= 0) return;
        while (Webs.Count(w => w.Caster == caster) >= maximum)
            Webs.Remove(Webs.Where(w => w.Caster == caster).MinBy(w => w.Until)!);
        Webs.Add(new(caster, now, now + seconds / ExpiryRate, taken, resistance, damage));
    }
    public void Expire(double now) => Webs.RemoveAll(w => w.Until <= now);
    public void SetExpiryRate(double now, double rate)
    {
        rate = Math.Max(.25, rate);
        if (rate == ExpiryRate) return;
        for (int i = 0; i < Webs.Count; i++)
            Webs[i] = Webs[i] with { Until = RecipientEffectTime.RetimedDeadline(Webs[i].Until, now, ExpiryRate, rate) };
        ExpiryRate = rate;
    }
}
