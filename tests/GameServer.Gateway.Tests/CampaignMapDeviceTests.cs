namespace GameServer.Gateway.Tests;

public sealed class CampaignMapDeviceTests
{
    [Fact]
    public void Reverie_requires_its_quest_map_and_keeps_the_return_after_six_entries()
    {
        var sanctum = new MapDevice.MapArea(2979, MapDevice.CampaignMapArea, "Maligaro's Sanctum", 0) { Level = 52 };
        var device = new MapDevice(new Dictionary<uint, MapDevice.MapArea>(), worldAreas: [sanctum]);
        var questMap = new MapDevice.Slot(MapDevice.CampaignMapHash, "QuestItem", "Maligaro's Map", 1583, [], 0);
        Assert.True(MapDevice.AcceptsItem(questMap.TypeHash, questMap.Class));
        Assert.False(MapDevice.AcceptsItem(1, "QuestItem"));
        Assert.Null(device.Check([], null, null, out _, campaign: true));
        Assert.Null(device.Check([questMap, questMap], null, null, out _, campaign: true));
        Assert.Null(device.Check([questMap with { TypeHash = 1, Class = "Map" }], null, null, out _, campaign: true));
        Assert.Null(device.Check([questMap], out _));
        Assert.Equal(sanctum, device.Check([questMap], null, null, out _, campaign: true)?.Area);

        const string character = "ReverieDeviceRegression";
        var origin = new MapDevice.Origin(14014, 1234, 609, 630);
        try
        {
            MapDevice.Activate(character, sanctum, returnTo: origin);
            MapDevice.Entered(character, sanctum, 5678);
            for (int i = 0; i < MapDevice.PortalCount; i++)
            {
                var taken = MapDevice.TakePortal(character, i);
                Assert.NotNull(taken);
                Assert.Equal(5678ul, taken.Value.Map.Instance);
                Assert.Equal(origin, taken.Value.Map.ReturnTo);
                Assert.Equal(5 - i, taken.Value.Left);
                Assert.Null(MapDevice.TakePortal(character, i));
            }
            Assert.Equal(0, MapDevice.OpenOf(character)!.PortalsLeft);
            Assert.Equal(origin, MapDevice.Current(character, sanctum.Hash16)!.ReturnTo);
        }
        finally { MapDevice.Close(character); }
    }
}
