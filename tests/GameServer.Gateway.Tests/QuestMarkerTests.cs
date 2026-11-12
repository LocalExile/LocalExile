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

    [Fact]
    public void EncounterChestRequiresTheRetreatAndTheLastLivingGuard()
    {
        var instance = new InstanceState();
        var conn = new ClientState(new(1, "crematorium", 100, 100, 1, 1, null, []), 1, 1) { Instance = instance };
        const string guardId = "Metadata/Monsters/Axis/AxisCasterGuardTolman";
        var gate = Quests.InstanceFlag("CrematoriumPietyFled");
        var rule = new Quests.ChestRule("1_3_3_1", "TolmanChair", "TolmanBracelet", gate) { Guards = [guardId] };
        var info = new QuestObjectTypes.Info("chair", ["Transitionable"], 3, 1, false, null);
        Assert.Equal(1, Game.QuestChestState(conn, rule, info, false));
        var variety = Assert.IsType<MonsterCatalog.Variety>(new MonsterCatalog().Find(guardId));
        var guard = new SpawnedMonster("guard", new(0, 0, 1), 0, 0, 10) { Variety = variety, Hostile = true };
        conn.Monsters[12] = guard;
        instance.SetState(gate);
        Assert.Equal(1, Game.QuestChestState(conn, rule, info, false));
        guard.Dead = true;
        Assert.Equal(2, Game.QuestChestState(conn, rule, info, false));
        Assert.Equal(3, Game.QuestChestState(conn, rule, info, true));
        Assert.False(conn.HasQuestFlag(gate));
        Assert.False(new InstanceState().HasState(gate));
    }

    [Fact]
    public void PersonalClaimsAndSharedChestsKeepIndependentOpenState()
    {
        var instance = new InstanceState();
        Assert.True(instance.OpenChest(10, 20, "First"));
        Assert.False(instance.OpenChest(10, 20, "First"));
        Assert.False(instance.ChestOpened(10, 20, "Second"));
        Assert.True(instance.OpenChest(10, 20, "Second"));
        Assert.False(instance.ChestOpened(10, 20));
        Assert.True(instance.OpenChest(10, 20));
        Assert.False(instance.OpenChest(10, 20));

        ClientState Player(string name) => new(new(1, "crematorium", 100, 100, 1, 1, null, []), 1, 1)
            { Instance = instance, Character = name };
        var first = Player("First");
        var reconnect = Player("First");
        var second = Player("Second");
        var bracelet = ItemCatalog.Create(Program.Items.Find("Metadata/Items/QuestItems/TolmanBracelet")!);
        uint drop = instance.Ground.Add(new WorldItemObject(10, 20, bracelet), first, shared: false);
        Assert.True(Game.QuestDropVisibleTo(reconnect, drop));
        Assert.False(Game.QuestDropVisibleTo(second, drop));
    }
}
