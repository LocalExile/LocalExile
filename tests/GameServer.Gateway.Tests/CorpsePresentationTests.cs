using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class CorpsePresentationTests
{
    // AreaLife writes the death into the frame's delta; give the rule that record with an explicit wall time so a test
    // needs no real wait.
    static AreaDelta.Record Death(uint id) => new(id, InstanceProtocol.LifeUpdate(id, 0), null,
        new WorldOrder(uint.MaxValue), AreaDelta.Kind.World);

    [Fact]
    public void Expired_corpse_stops_being_drawn_but_stays_in_the_instance_for_corpse_skills()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var player = runtime.PlayerOf(instance, wire);
        var area = player.Area;
        var variety = Game.Monsters.All.First();
        const uint oldId = 0x08000100, freshId = 0x08000101;
        foreach (uint id in new[] { oldId, freshId })
        {
            area.Monsters[id] = new SpawnedMonster("victim", new PlayerWalk(110, 100, 30), 60, 8, 1000)
                { Life = 0, Dead = true };
            instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
                new MonsterObject(110, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject),
                (ushort)variety.Row, 1, MonsterRarity.Normal);
        }
        Game.CorpsePresentationWritten(player, new[] { Death(oldId) }, -Game.CorpsePresentationSeconds);
        Game.CorpsePresentationWritten(player, new[] { Death(freshId) }, 0);

        Game.StepCorpsePresentation(area, new[] { player }, 1);
        var records = area.Delta.Take();
        var removals = records.Where(r => r.Entity == oldId && r.Kind == AreaDelta.Kind.Remove).ToArray();
        Assert.Single(removals);
        Assert.Same(player, removals[0].Only);
        Assert.DoesNotContain(records, r => r.Entity == freshId);

        // Presentation only: the corpse and its origin stay for corpse skills and for a client joining later.
        Assert.Contains(oldId, area.Monsters.Keys);
        Assert.Contains(oldId, instance.World.MonsterOrigins.Keys);
        Game.StepCorpsePresentation(area, new[] { player }, 2);
        Assert.DoesNotContain(area.Delta.Take(), r => r.Entity == oldId); // no second removal
    }

    [Fact]
    public void A_death_seen_in_a_live_view_starts_the_visual_clock()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var player = runtime.PlayerOf(instance, wire);
        var area = player.Area;
        var variety = Game.Monsters.All.First();
        const uint id = 0x08000102;
        area.Monsters[id] = new SpawnedMonster("victim", new PlayerWalk(110, 100, 30), 60, 8, 1000);
        instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
            new MonsterObject(110, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject),
            (ushort)variety.Row, 1, MonsterRarity.Normal);

        Game.CorpsePresentationWritten(player, new[] { Death(id) }, 10);
        Game.StepCorpsePresentation(area, new[] { player }, 69.9);
        Assert.Empty(area.Delta.Take());
        Game.StepCorpsePresentation(area, new[] { player }, 70);
        Assert.Contains(area.Delta.Take(), r => r.Entity == id && r.Kind == AreaDelta.Kind.Remove && ReferenceEquals(r.Only, player));
    }
}
