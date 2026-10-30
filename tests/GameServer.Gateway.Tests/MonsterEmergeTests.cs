using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Monsters that rise from the ground (MonsterEmerge.cs): which types wait buried or lurking, how long they take to
/// rise, that a buried monster stays out of the connection's monsters (so nothing hits it) until the player is near, and
/// that a lurking one stays hittable and rises when hit.
/// </summary>
public class MonsterEmergeTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"localexile-emerge-{Guid.NewGuid():n}");

    const string GravelEaterType = "Metadata/Monsters/SandSpitterEmerge/SandSpitterEmerge";
    const string TideStriderType = "Metadata/Monsters/WaterElemental/WaterElementalHidden";
    const string HillockType = "Metadata/Monsters/ZombieBoss/ZombieBoss";
    static readonly string[] EmergeComponents = [.. MonsterObject.BaseComponents, "Transitionable"];

    // Entries as scripts/re/ot_components.py writes them (data/object-types.json, 3.29.3b), cut to the fields used.
    const string Base = "\"read\": 12, \"components\": [\"Positioned\", \"Stats\", \"ObjectMagicProperties\", \"BaseEvents\", " +
        "\"Pathfinding\", \"Buffs\", \"Life\", \"Animated\", \"Inventories\", \"Actor\", \"Monster\", \"Transitionable\", \"Render\", \"Targetable\"]";
    const string Actions = "\"basic_actions\": [\"Move\", \"Flee\", \"StrafeLeft\", \"StrafeRight\", \"Advance\", \"DoNothing\", \"MonsterPickup\", \"Emerge\"]";
    static readonly string Types = $$"""
        {"types": [
          {"type": "{{GravelEaterType}}", {{Base}}, {{Actions}}, "transitionable": {"num_states": 1, "no_serialise": false},
           "client_transitionable": {"create_untransitioned": "PlayAnimation( emerge ); PauseAnimation();",
             "create_transitioned": "EnableTargetable();", "on_transition": "EnableTargetable();"}, "client_targetable": false},
          {"type": "{{TideStriderType}}", {{Base}}, {{Actions}}, "transitionable": {"num_states": 1, "no_serialise": false},
           "client_transitionable": {"create_untransitioned": "PlayAnimation( emerge ); PauseAnimation();"}, "client_targetable": null},
          {"type": "Metadata/Monsters/RootSpider/RootSpiderHidden", {{Base}}, {{Actions}}, "transitionable": {"num_states": 1},
           "client_transitionable": {"create_untransitioned": "PlayAnimation( emerge ); PauseAnimation(); DisableTargetable(); RemoveCollidability();",
             "on_transition": "EnableTargetable();"}, "client_targetable": null},
          {"type": "{{HillockType}}", {{Base}}, "basic_actions": ["Move", "Flee", "Revive"], "transitionable": {"num_states": 1},
           "client_transitionable": {"on_transition": "HideMeshSegment( ZombieBoss_mesh|Sword|SwordShape );"}, "client_targetable": null},
          {"type": "Metadata/Monsters/SwordStageWithEmerge", {{Base}}, {{Actions}}, "transitionable": {"num_states": 1},
           "client_transitionable": {"on_transition": "HideMeshSegment( ZombieBoss_mesh|Sword|SwordShape );"}, "client_targetable": null},
          {"type": "Metadata/Monsters/Spiders/SpiderBoss/SpiderBoss", {{Base}}, {{Actions}}, "transitionable": {"num_states": 3},
           "client_transitionable": {"create_untransitioned": "PlayAnimation( emerge ); PauseAnimation();"}, "client_targetable": false}
        ]}
        """;

    public MonsterEmergeTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "tables"));
        File.WriteAllText(Path.Combine(_folder, "object-types.json"), Types);
        File.WriteAllText(Path.Combine(_folder, "monster-takehit.json"), """
            {"Metadata/Monsters/SandSpitterEmerge/SandSpitterEmergeStandard":
               {"index": 12, "indexes": [12], "lengths": [1000], "emerge": {"clip": "emerge", "index": 10, "length": 1000}},
             "Metadata/Monsters/Slow": {"emerge": {"clip": "grasp_emerge", "index": 38, "length": 3666}}}
            """);
        File.WriteAllText(Path.Combine(_folder, "tables", "ActionTypes.json"), """
            [{"Id": "Move", "HASH16": 10505}, {"Id": "Emerge", "HASH16": 16641}, {"Id": "Emerge2", "HASH16": 9115}]
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static MonsterCatalog.Variety Variety(string id = "Metadata/Monsters/SandSpitterEmerge/SandSpitterEmergeStandard",
        string type = GravelEaterType, IReadOnlyList<string>? components = null, byte states = 1, int aggro = 105) =>
        new(513, id, "Gravel Eater", type, null, components ?? EmergeComponents, TransitionStates: states, AggroRange: aggro);

    MonsterEmerge Emerge() => new(_folder);

    static JsonElement Entry(string type)
    {
        using var doc = JsonDocument.Parse(Types);
        return doc.RootElement.GetProperty("types").EnumerateArray().First(t => t.GetProperty("type").GetString() == type).Clone();
    }

    [Fact]
    public void The_emerge_action_is_the_basic_actions_skill_id()
    {
        Assert.Equal((ushort)0x0101, Emerge().SkillId);
    }

    static ClientState Conn() => new(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);

    static SpawnedMonster Monster(int x, int y, int aggro = 105, uint life = 30) =>
        new("Gravel Eater@2", new WalkEstimator(x, y, 20, null, 2000, 2000), aggroRange: aggro, stopRange: 3, maxLife: 30)
        {
            Variety = Variety(), Level = 2, Life = life,
        };

}
