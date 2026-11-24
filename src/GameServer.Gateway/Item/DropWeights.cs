/// <summary>
/// Factors on a kill's drop weights (1: the server's own), set by mods through <see cref="DropRoll.Weights"/>. The kinds
/// scale stage 2 of <see cref="ItemDrops"/>' roll on top of the <c>drop_multiplier_*</c> settings; <see cref="Cards"/> and
/// <see cref="Scarabs"/> scale a map kill's base
/// card and scarab chances; <see cref="Items"/> scales single currency, card and scarab types in their picks. A factor that
/// is negative or not a number counts as 0, which leaves that kind or type out.
/// </summary>
public sealed class DropWeights
{
    double _currency = 1, _gems = 1, _flasks = 1, _equipment = 1, _cards = 1, _scarabs = 1;

    public double Currency { get => _currency; set => _currency = Clean(value); }
    public double Gems { get => _gems; set => _gems = Clean(value); }
    public double Flasks { get => _flasks; set => _flasks = Clean(value); }
    public double Equipment { get => _equipment; set => _equipment = Clean(value); }
    public double Cards { get => _cards; set => _cards = Clean(value); }
    public double Scarabs { get => _scarabs; set => _scarabs = Clean(value); }

    /// <summary>A factor per <c>BaseItemTypes</c> Id; a type not listed keeps 1.</summary>
    public Dictionary<string, double> Items { get; } = new(StringComparer.Ordinal);

    public double Item(string id) => Items.Count > 0 && Items.TryGetValue(id, out double factor) ? Clean(factor) : 1;

    static double Clean(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
