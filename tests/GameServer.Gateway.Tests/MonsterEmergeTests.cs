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

    [Fact]
    public void The_client_scripts_decide_who_waits_buried_or_lurking()
    {
        // Untargetable while it waits: nothing may reach it on the server either.
        Assert.Equal(EmergeKind.Buried, MonsterEmerge.KindOf(Entry(GravelEaterType)));
        Assert.Equal(EmergeKind.Buried, MonsterEmerge.KindOf(Entry("Metadata/Monsters/RootSpider/RootSpiderHidden")));
        // Frozen in its emerge pose but targetable on the client: it stays hittable.
        Assert.Equal(EmergeKind.Lurking, MonsterEmerge.KindOf(Entry(TideStriderType)));
        // Hillock's Transitionable is his sword stage (BossFights), not an emerge, with or without the Emerge action.
        Assert.Equal(EmergeKind.None, MonsterEmerge.KindOf(Entry(HillockType)));
        Assert.Equal(EmergeKind.None, MonsterEmerge.KindOf(Entry("Metadata/Monsters/SwordStageWithEmerge")));
        // num_states given: created transitioned as before.
        Assert.Equal(EmergeKind.None, MonsterEmerge.KindOf(Entry("Metadata/Monsters/Spiders/SpiderBoss/SpiderBoss")));
    }

    [Fact]
    public void Varieties_take_their_types_kind_but_unique_monsters_keep_their_own_entrance()
    {
        var e = Emerge();
        Assert.Equal(EmergeKind.Buried, e.Kind(Variety()));
        Assert.Equal(EmergeKind.Lurking, e.Kind(Variety("Metadata/Monsters/WaterElemental/WaterElementalTidal", TideStriderType)));
        Assert.Equal(EmergeKind.Buried, e.Kind(Variety(), MonsterRarity.Rare));
        Assert.Equal(EmergeKind.None, e.Kind(Variety(), MonsterRarity.Unique));
        Assert.Equal(EmergeKind.None, e.Kind(Variety("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal", HillockType)));
        Assert.Equal(EmergeKind.None, e.Kind(Variety(states: 3)));
    }

    [Fact]
    public void New_monsters_wait_in_state_1_and_kept_risen_ones_come_back_in_state_2()
    {
        var gravelEater = Variety();
        Assert.Equal(1, MonsterEmerge.CreateState(gravelEater, EmergeKind.Buried, unrisen: true));
        Assert.Equal(2, MonsterEmerge.CreateState(gravelEater, EmergeKind.Buried, unrisen: false));
        Assert.Equal(2, MonsterEmerge.CreateState(gravelEater, EmergeKind.Lurking, unrisen: false));
        // Not an emerge monster: the type's own state, as before.
        Assert.Equal(3, MonsterEmerge.CreateState(Variety(states: 3), EmergeKind.None, unrisen: true));
    }

    [Fact]
    public void Packs_place_emerge_monsters_only_when_they_can_rise()
    {
        var e = Emerge();
        Assert.True(e.CanPlace(Variety()));
        Assert.True(e.CanPlace(Variety("Metadata/Monsters/WaterElemental/WaterElementalSubmerged_", TideStriderType)));
        // An "Emerge" variety whose Transitionable the server does not play would wait for good.
        Assert.False(e.CanPlace(Variety("Metadata/Monsters/X/ThreeStateEmerge", states: 3)));
        // Without a Transitionable it is an ordinary monster.
        Assert.True(e.CanPlace(Variety("Metadata/Monsters/X/DwellerEmerge", "Metadata/Monsters/Dweller", MonsterObject.BaseComponents)));
    }

    [Fact]
    public void Rising_takes_the_emerge_clip()
    {
        var e = Emerge();
        Assert.Equal(1.0, e.Seconds(Variety()));
        Assert.Equal(3.666, e.Seconds(Variety("Metadata/Monsters/Slow")), 3);
        Assert.Equal(MonsterEmerge.DefaultSeconds, e.Seconds(Variety("Metadata/Monsters/Unknown")));
    }

    [Fact]
    public void Without_the_data_nothing_waits()
    {
        var e = new MonsterEmerge(Path.Combine(_folder, "missing"));
        Assert.Null(e.SkillId);
        Assert.Equal(EmergeKind.None, e.Kind(Variety()));
    }

    [Fact]
    public void The_rise_is_the_emerge_action_then_the_transition()
    {
        var messages = MonsterEmerge.RiseMessages(0x3000, (10, 11), 0x0101, 11, 2);
        Assert.Equal(new[] { 0x1ab, 0x2ba }, messages.Select(m => m[0] << 8 | m[1]));
        Assert.Equal(Convert.ToHexString(InstanceProtocol.ActionStarted(0x3000, 10, 11, 10, 11, 0x0101, 0xffff, 0x0408)),
            Convert.ToHexString(messages[0]));
        Assert.Equal(Convert.ToHexString(InstanceEntities.TransitionableUpdate(0x3000, 11, 2)), Convert.ToHexString(messages[1]));
        Assert.Equal(0x1ac, InstanceProtocol.ActionEnded(0x3000) is var end ? end[0] << 8 | end[1] : 0);
    }

    static ClientState Conn() => new(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);

    static SpawnedMonster Monster(int x, int y, int aggro = 105, uint life = 30) =>
        new("Gravel Eater@2", new WalkEstimator(x, y, 20, null, 2000, 2000), aggroRange: aggro, stopRange: 3, maxLife: 30)
        {
            Variety = Variety(), Level = 2, Life = life,
        };

    [Fact]
    public void Spawning_puts_a_buried_monster_aside_and_a_lurking_one_among_the_monsters_but_busy()
    {
        var conn = Conn();
        var buried = Monster(10, 10);
        var lurking = Monster(20, 20);
        var risen = Monster(30, 30);
        lock (conn.Monsters)
        {
            Assert.True(MonsterEmerge.AddLocked(conn, 0x3000, buried, EmergeKind.Buried, unrisen: true));
            Assert.True(MonsterEmerge.AddLocked(conn, 0x3001, lurking, EmergeKind.Lurking, unrisen: true));
            Assert.False(MonsterEmerge.AddLocked(conn, 0x3002, risen, EmergeKind.Buried, unrisen: false));
        }
        Assert.Same(buried, Assert.Single(MonsterEmerge.Buried(conn)).Value);
        Assert.False(conn.Monsters.ContainsKey(0x3000));   // nothing targets a buried monster
        Assert.Same(lurking, conn.Monsters[0x3001]);       // a lurking one can be hit
        Assert.True(double.IsPositiveInfinity(lurking.BusyUntil));   // but the AI leaves it until it rises
        Assert.Same(risen, conn.Monsters[0x3002]);
        Assert.Equal(0, risen.BusyUntil);
        Assert.Equal(new[] { buried, lurking }, MonsterEmerge.Unrisen(conn));
    }

    [Fact]
    public void A_buried_monster_rises_only_when_the_player_is_near_and_then_joins_the_monsters()
    {
        var e = Emerge();
        var conn = Conn();
        var near = Monster(100, 100);
        var far = Monster(100, 160);
        lock (conn.Monsters)
        {
            MonsterEmerge.AddLocked(conn, 0x3000, near, EmergeKind.Buried, true);
            MonsterEmerge.AddLocked(conn, 0x3001, far, EmergeKind.Buried, true);
            Assert.True(MonsterEmerge.StartWatch(conn));
            Assert.False(MonsterEmerge.StartWatch(conn));   // one watch per connection
        }

        var risen = e.RiseLocked(conn, (100, 100 - MonsterEmerge.Range), now: 5, out bool watching);

        var (id, m, at, seconds) = Assert.Single(risen);
        Assert.Equal((0x3000u, (100, 100), 1.0), (id, at, seconds));
        Assert.Same(near, conn.Monsters[0x3000]);
        Assert.True(m.Aggro);
        Assert.True(m.BusyUntil > 6);   // busy until the clip has played
        Assert.True(watching);
        Assert.Same(far, Assert.Single(MonsterEmerge.Buried(conn)).Value);

        Assert.Single(e.RiseLocked(conn, (100, 150), now: 7, out watching));
        Assert.False(watching);         // none left: the watch ends
        Assert.Empty(MonsterEmerge.Buried(conn));
        Assert.True(MonsterEmerge.StartWatch(conn));
    }

    [Fact]
    public void A_lurking_monster_rises_when_hit_from_afar()
    {
        var e = Emerge();
        var conn = Conn();
        var lurking = Monster(100, 100);
        lock (conn.Monsters) MonsterEmerge.AddLocked(conn, 0x3000, lurking, EmergeKind.Lurking, true);
        Assert.Empty(e.RiseLocked(conn, (100, 180), now: 1, out bool watching));
        Assert.True(watching);

        lurking.Life = 20;   // a spell from 80 cells away
        var (id, m, _, _) = Assert.Single(e.RiseLocked(conn, (100, 180), now: 2, out watching));
        Assert.Equal(0x3000u, id);
        Assert.Equal(2 + 1.0 + 0.3, m.BusyUntil, 3);
        Assert.False(watching);
    }

    [Fact]
    public void A_short_aggro_range_is_the_reach()
    {
        var e = Emerge();
        var conn = Conn();
        lock (conn.Monsters) MonsterEmerge.AddLocked(conn, 0x3000, Monster(100, 100, aggro: 10), EmergeKind.Buried, true);
        Assert.Empty(e.RiseLocked(conn, (100, 115), now: 1, out _));   // within 24, beyond its 10
        Assert.Single(e.RiseLocked(conn, (100, 109), now: 2, out _));
    }

    [Fact]
    public void Monsters_waiting_to_rise_are_kept_waiting_and_risen_ones_come_back_risen()
    {
        var conn = Conn();
        var risen = Monster(10, 10);
        var buried = Monster(50, 50);
        var lurking = Monster(70, 70);
        lock (conn.Monsters)
        {
            MonsterEmerge.AddLocked(conn, 0x3000, risen, EmergeKind.Buried, unrisen: false);
            MonsterEmerge.AddLocked(conn, 0x3001, buried, EmergeKind.Buried, unrisen: true);
            MonsterEmerge.AddLocked(conn, 0x3002, lurking, EmergeKind.Lurking, unrisen: true);
        }
        var saved = SavedMonster.Save(conn.Monsters.Values, now: 0, MonsterEmerge.Unrisen(conn));

        Assert.Equal([(10, false), (70, true), (50, true)], saved.Select(s => (s.X, s.Unrisen)));
        // The risen one is re-created transitioned: the client runs create_transitioned (EnableTargetable).
        Assert.Equal(2, MonsterEmerge.CreateState(Variety(), Emerge().Kind(Variety()), saved[0].Unrisen));
        Assert.Equal(1, MonsterEmerge.CreateState(Variety(), Emerge().Kind(Variety()), saved[2].Unrisen));
    }
}
