using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class DelveStashTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static PlayerInventories.Shape? Shape(Item item) => Catalog.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Theory]
    [InlineData("Upgrade", 1)] [InlineData("Upgrade", 2)] [InlineData("Upgrade", 3)] [InlineData("Upgrade", 4)]
    public void Empty_legacy_resonators_migrate_to_stackable_with_exact_unlinked_delve_sockets(string family, int count)
    {
        string legacyId = $"Metadata/Items/Delve/DelveSocketableCurrency{family}{count}";
        var legacy = Enumerable.Range(294, 8).Select(Catalog.Row).Single(t => t?.Id == legacyId)!;
        var old = new CharacterSaves.SavedItem(legacy.Hash,
            Sockets: [new(SocketColour.White), new(SocketColour.White)], Links: [2]);
        var item = CharacterSaves.FromSaved(old, Catalog);
        var type = Catalog.ByHash(item.TypeHash)!;
        Assert.Contains("DelveStackableSocketable", type.Id);
        Assert.True(type.Stackable);
        Assert.Equal(type.Hash, Catalog.Find(legacyId)!.Hash);
        Assert.Equal(count, item.Sockets.Count);
        Assert.All(item.Sockets, socket => Assert.Equal(SocketColour.Delve, socket.Colour));
        Assert.Null(item.Links);
        Assert.Equal(count, ItemCatalog.Create(type).Sockets.Count);
        Assert.Equal(count, Catalog.DefaultSockets(type).Sockets.Count);
        Assert.Equal(item.TypeHash, CharacterSaves.FromSaved(CharacterSaves.ToSaved(item), Catalog).TypeHash);
    }

}
