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
        var entrance = layout.EntranceFrom?.Invoke(docks, null);
        Assert.NotNull(entrance);
        var pt = (entrance.Value.X, entrance.Value.Y);
        bool onTransition = layout.AreaTransitions.Any(t => t.Transition.DestinationHash16 == docks && t.Transition.TeleportTo is null
            && Math.Abs(t.Transition.GridX - pt.X) <= 4 && Math.Abs(t.Transition.GridY - pt.Y) <= 4);
        var expected = entrance.Value.NamedPoint && !onTransition && (layout.Walkable is null || layout.Walkable(pt.X, pt.Y))
            ? pt : layout.Near(pt);
        Assert.Equal(expected, layout.Arrival(KitavaActTen.ArrivalSource(KitavaActTen.Area, shore)));
        Assert.NotEqual((layout.SpawnX, layout.SpawnY), layout.Arrival(docks));
    }
}
