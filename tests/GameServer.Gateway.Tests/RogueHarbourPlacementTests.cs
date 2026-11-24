namespace GameServer.Gateway.Tests;

/// <summary>The Rogue Harbour's NPCs and stashes stand where its hub room's own lines put them (TownNpcs, StashCatalog),
/// not in a ring of offsets around the arrival.</summary>
public sealed class RogueHarbourPlacementTests
{
    [Fact]
    public void The_harbour_places_its_thirteen_NPCs_and_both_stashes_from_the_room()
    {
        var npcs = NpcCatalog.In(StashCatalog.HeistHub, 39403887);
        Assert.Equal(13, npcs.Count);
        Assert.All(npcs, p => Assert.True(p.FromRoom));
        // Faustus (room line 89, 290) and Karst (130, 405), moved by the room's origin (92, 92).
        Assert.Contains(npcs, p => p.Id == "Metadata/NPC/League/Heist/Fence" && (p.X, p.Y) == (181, 382));
        Assert.Contains(npcs, p => p.Id == "Metadata/NPC/League/Heist/Lockpick1" && (p.X, p.Y) == (222, 497));

        Assert.False(StashCatalog.FromArrival(StashCatalog.HeistHub));
        var stash = StashCatalog.In(StashCatalog.HeistHub)!;
        var guild = StashCatalog.GuildIn(StashCatalog.HeistHub)!;
        Assert.Equal((266, 390), (stash.X, stash.Y));
        Assert.Equal((266, 379), (guild.X, guild.Y));
    }
}
