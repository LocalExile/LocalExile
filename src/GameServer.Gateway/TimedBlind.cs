/// <summary>ReminderTextBlind: 20% less Accuracy/Evasion for four seconds; strongest active effect wins.</summary>
sealed class TimedBlind
{
    readonly List<(double Until, int Less)> effects = [];
    public ushort Buff;
    public (int Less, double Until) Sent;
    public bool Inflict(double now, double seconds, int effectPercent, bool immune)
    {
        if (immune || seconds <= 0) return false;
        int less = (int)Math.Clamp(20L * Math.Max(0L, 100L + effectPercent) / 100, 0, 100);
        if (less == 0) return false;
        effects.Add((now + Math.Min(seconds, 86400), less));
        return true;
    }
    public (int Less, double Until) Read(double now, bool immune = false)
    {
        effects.RemoveAll(e => immune || e.Until <= now);
        int less = effects.Select(e => e.Less).DefaultIfEmpty().Max();
        return (less, effects.Where(e => e.Less == less).Select(e => e.Until).DefaultIfEmpty().Max());
    }
    public int Apply(int rating, double now, bool immune = false) => Apply(rating, Read(now, immune).Less);
    public static int Apply(int rating, int less) => (int)((long)Math.Max(0, rating) * (100 - less) / 100);
    public void Clear() => effects.Clear();
    public (double Seconds, int Less)[] Carry(double now)
    {
        Read(now); var result = effects.Select(e => (e.Until - now, e.Less)).ToArray();
        Clear(); return result;
    }
    public void Restore((double Seconds, int Less)[] values, double now)
    { Clear(); effects.AddRange(values.Where(v => v.Seconds > 0).Select(v => (now + v.Seconds, v.Less))); }
}
