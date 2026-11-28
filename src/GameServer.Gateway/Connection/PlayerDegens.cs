/// <summary>
/// The player's degeneration by source, points a second (Player.Degens). Each source sets its own share under its
/// own key and sets 0 when it ends, so one source ending does not cancel another's (a trial's hot plate and a roomba, a
/// Brine King tide and a ground). The total is what the player's recovery deals.
/// </summary>
public sealed class PlayerDegens
{
    readonly Dictionary<object, (double Total, double Elemental)> _shares = new(ReferenceEqualityComparer.Instance);

    /// <summary>All the shares together.</summary>
    public double Total { get; private set; }

    /// <summary>The share belonging to elemental damage.</summary>
    public double Elemental { get; private set; }

    /// <summary>One source's share; setting 0 (or less) removes it.</summary>
    public double this[object source]
    {
        get => _shares.GetValueOrDefault(source).Total;
        set => Set(source, value, 0);
    }

    public void Set(object source, double amount, double elemental)
    {
        if (amount > 0) _shares[source] = (amount, Math.Clamp(elemental, 0, amount));
        else _shares.Remove(source);
        Total = Elemental = 0;
        foreach (var share in _shares.Values)
        {
            Total += share.Total;
            Elemental += share.Elemental;
        }
    }
}
