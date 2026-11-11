/// <summary>
/// A player's Storm Call markers. Each strikes when its duration ends, and its strike calls lightning down on every
/// other marker the player has out at that moment (the gem: "causing lightning strikes at any other markers you've
/// cast"), so one due marker takes them all. Markers from an earlier area (another epoch) are gone with that area.
/// </summary>
public sealed class StormCallMarkers
{
    /// <param name="Use">The cast's critical roll, carried to the strike.</param>
    public sealed record Marker(uint Object, ushort Skill, (int X, int Y) At, double StrikeAt, int Repeat, int Epoch,
        int AddedChaos, object? Use);

    readonly List<Marker> pending = [];

    public int Count => pending.Count;

    public void Place(Marker marker) => pending.Add(marker);

    /// <summary>Every marker, when one of this epoch is due at <paramref name="now"/>; none otherwise. Taken markers are
    /// removed, so a second check for the same strike finds nothing. Markers of another epoch are dropped.</summary>
    public IReadOnlyList<Marker> TakeStrikes(double now, int epoch)
    {
        pending.RemoveAll(m => m.Epoch != epoch);
        if (!pending.Exists(m => m.StrikeAt <= now)) return [];
        var all = pending.ToList();
        pending.Clear();
        return all;
    }
}

public sealed partial class ClientState
{
    StormCallMarkers? stormCallMarkers;
    public StormCallMarkers StormCall => System.Threading.LazyInitializer.EnsureInitialized(ref stormCallMarkers);
}
