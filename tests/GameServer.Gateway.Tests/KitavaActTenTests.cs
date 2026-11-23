namespace GameServer.Gateway.Tests;

public sealed class KitavaActTenTests
{
    [GameFilesFact]
    public void Direct_epilogue_portal_uses_the_shore_entrance_connected_to_the_docks()
    {
        ushort shore = AreaTransitionCatalog.HashOf(KitavaActTen.Epilogue)!.Value;
        ushort docks = AreaTransitionCatalog.HashOf(KitavaActTen.Docks)!.Value;
        Assert.Equal(docks, KitavaActTen.ArrivalSource(KitavaActTen.Area, shore));
        Assert.Null(KitavaActTen.ArrivalSource(KitavaActTen.Area, docks));
        var layout = AreaLayouts.For(shore, 1);
        var entrance = layout.EntranceFrom?.Invoke(docks);
        Assert.NotNull(entrance);
        Assert.Equal(layout.Near(entrance.Value), layout.Arrival(KitavaActTen.ArrivalSource(KitavaActTen.Area, shore)));
        Assert.NotEqual((layout.SpawnX, layout.SpawnY), layout.Arrival(docks));
    }
}
