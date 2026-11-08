using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class QuestMarkerTests
{
    [Fact]
    public void CollectedChestMarkersHideWithoutChangingTheChestState()
    {
        string[] components = ["Positioned", "BaseEvents", "Animated", "Chest", "Stats", "ObjectMagicProperties",
            "InteractionAction", "MinimapIcon", "Transitionable"];
        var chest = new QuestObject(120, 240, components, TransitionState: 2, Opened: true);
        byte[] shown = chest.Blob(), hidden = (chest with { HideMinimapIcon = true }).Blob();
        Assert.Equal(shown.Length, hidden.Length);
        Assert.Equal(0, shown[^3]);
        Assert.Equal(6, hidden[^3]); // visibility override present (2) and hidden (4)
        Assert.Equal(new byte[] { 2, 0 }, hidden[^2..]); // opened transition and task terminator
        hidden[^3] = 0;
        Assert.Equal(shown, hidden);

        var at = new GlyphQuest.Placement("Metadata/Chests/QuestChests/RhoaChest", "nest.ao", 120, 240, 0, null, 0);
        byte[] nest = GlyphQuest.NestBlob(at, true, true);
        Assert.Equal(new byte[] { 0, 6, 0 }, nest[^3..]); // InteractionAction, MinimapIcon, tasks
        nest[^2] = 0;
        Assert.Equal(GlyphQuest.NestBlob(at, true), nest);

        var conn = new ClientState(new(1, "quest-markers", 100, 100, 1, 1, null, []), 1, 1);
        var rule = new Quests.ChestRule("area", "chest", "item");
        var info = new QuestObjectTypes.Info("chest", components, 2, 1, false, null);
        Assert.Equal(1, Game.QuestChestState(conn, rule, info, false));
        Assert.Equal(2, Game.QuestChestState(conn, rule, info, true));
    }
}
