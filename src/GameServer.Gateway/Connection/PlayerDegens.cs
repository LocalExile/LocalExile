using GameServer.Core.World;

/// <summary>
/// The player's degeneration by source, points a second (Player.Degens). Each source sets its own share under its
/// own key and sets 0 when it ends, so one source ending does not cancel another's (a trial's hot plate and a roomba, a
/// Brine King tide and a ground). The total is what the player's recovery deals.
/// </summary>
public sealed class PlayerDegens
{
    readonly record struct Share(double Physical, double Elemental, double Other)
    {
        public double Total => Physical + Elemental + Other;
    }
    readonly Dictionary<object, Share> shares = new(ReferenceEqualityComparer.Instance);

    /// <summary>All the shares together.</summary>
    public double Total => Physical + Elemental + Other;
    public double Physical { get; private set; }
    public double Elemental { get; private set; }
    public double Other { get; private set; }

    /// <summary>One source's share; setting 0 (or less) removes it.</summary>
    public double this[object source]
    {
        get => shares.GetValueOrDefault(source).Total;
        set => Set(source, 0, 0, value);
    }

    public void Set(object source, DamageType type, double perSecond)
    {
        switch (type)
        {
            case DamageType.Physical: Set(source, perSecond, 0, 0); break;
            case DamageType.Fire or DamageType.Cold or DamageType.Lightning: Set(source, 0, perSecond, 0); break;
            case DamageType.Chaos: Set(source, 0, 0, perSecond); break;
        }
    }

    public void Set(object source, double physical, double elemental, double other)
    {
        var next = new Share(Math.Max(0, physical), Math.Max(0, elemental), Math.Max(0, other));
        if (next.Total > 0) shares[source] = next;
        else shares.Remove(source);
        Physical = Elemental = Other = 0;
        foreach (var share in shares.Values)
        {
            Physical += share.Physical;
            Elemental += share.Elemental;
            Other += share.Other;
        }
    }
}
