using GameServer.Core.Protocol.Instance;
using GameServer.Modules.M_questobjects;

namespace GameServer.Gateway.Tests;
public sealed class PietySinsTests
{
    [Fact]
    public void Sins_room_creates_Piety_and_transitions_her_when_seen()
    {
        const string type = "Metadata/NPC/Act2/PietySins";
        const string area = "1_2_6_2";
        var rules = QuestObjectsModule.Acts().Single(a => a.Number == 2).Table.SelectMany(q => q).ToList();
        var scene = Assert.Single(rules.OfType<Quests.ObjectRule>(), r => r.Area == area && r.ObjectType == type);
        var near = Assert.Single(rules.OfType<Quests.NearRule>(), r => r.Area == area && r.ObjectType == type);
        Assert.Contains(scene.TransitionedBy!, near.Flags);
        var info = Assert.IsType<QuestObjectTypes.Info>(QuestObjectTypes.Find(type));
        Assert.True(QuestObject.CanSerialize(info.Components));
        Assert.Contains("Transitionable", info.Components);
        Assert.Contains("MinimapIcon", info.Components);
        Assert.NotEmpty(new QuestObjectTypes.AreaPlacements(area, 1, []).Of(type));
    }
}
