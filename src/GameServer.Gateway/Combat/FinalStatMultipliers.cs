/// <summary>Additional independent more/less factors. A module registers the identifiers it provides in its stat maps.</summary>
internal sealed class FinalStatMultipliers
{
    readonly Dictionary<uint, int> registrations = [];
    volatile uint[] keys = [];

    public IDisposable Register(uint stat)
    {
        lock (registrations)
        {
            if (registrations.TryGetValue(stat, out int count)) registrations[stat] = count + 1;
            else
            {
                registrations.Add(stat, 1);
                keys = [.. registrations.Keys];
            }
        }
        return new Registration(this, stat);
    }

    public double Factor(Func<uint, int> get)
    {
        double factor = 1;
        foreach (uint key in keys) factor *= Math.Max(0, 1 + get(key) / 100d);
        return factor;
    }

    void Unregister(uint stat)
    {
        lock (registrations)
        {
            int count = registrations[stat];
            if (count > 1) registrations[stat] = count - 1;
            else
            {
                registrations.Remove(stat);
                keys = [.. registrations.Keys];
            }
        }
    }

    sealed class Registration(FinalStatMultipliers owner, uint stat) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Unregister(stat);
        }
    }
}
