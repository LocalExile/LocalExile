/// <summary>One-way life thresholds for scripted encounters. A burst stops at the next pending phase;
/// completing that phase unlocks the next interval. Call under the instance's monster lock.</summary>
public sealed class LifePhaseGate(IReadOnlyList<int> thresholds)
{
    public int Completed { get; private set; }
    public bool Active { get; private set; }
    public int DamageTakenPercent { get; set; } = 100;
    public int NextPercent => Completed < thresholds.Count ? thresholds[Completed] : 0;
    public uint Damage(uint life, uint maximum, int damage)
    {
        if (Active || damage <= 0) return life;
        uint floor = NextPercent == 0 ? 0 : Math.Max(1, (uint)((ulong)maximum * (uint)NextPercent / 100));
        return (uint)Math.Max(Math.Min(life, floor), (long)life - (long)damage * Math.Clamp(DamageTakenPercent, 0, 100) / 100);
    }
    public bool Begin(uint life, uint maximum)
    {
        if (Active || NextPercent == 0 || life > Math.Max(1, (ulong)maximum * (uint)NextPercent / 100)) return false;
        Active = true;
        return true;
    }
    public void Finish()
    {
        if (!Active) return;
        Active = false;
        Completed++;
    }
}
