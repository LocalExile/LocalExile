using Xunit;

public class StormCallMarkersTests
{
    static StormCallMarkers.Marker Marker(uint id, double strikeAt, int epoch = 1) =>
        new(id, 0x10, (0, 0), strikeAt, Repeat: 0, epoch, AddedChaos: 0, Use: null);

    // One due marker calls down every marker out, the not-yet-due ones included, and only once; markers set in an
    // earlier area (another epoch) are dropped without striking.
    [Fact]
    public void DueMarkerStrikesEveryMarkerOnceAndDropsOldAreas()
    {
        var markers = new StormCallMarkers();
        markers.Place(Marker(1, strikeAt: 1.5));
        markers.Place(Marker(2, strikeAt: 1.9));
        markers.Place(Marker(3, strikeAt: 0.5, epoch: 0));

        Assert.Empty(markers.TakeStrikes(1.4, epoch: 1));
        Assert.Equal(2, markers.Count);

        var struck = markers.TakeStrikes(1.5, epoch: 1);
        Assert.Equal([1u, 2u], struck.Select(m => m.Object));
        Assert.Empty(markers.TakeStrikes(1.9, epoch: 1));
        Assert.Equal(0, markers.Count);
    }
}
