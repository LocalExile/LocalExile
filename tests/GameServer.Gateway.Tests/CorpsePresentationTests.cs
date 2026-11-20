using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class CorpsePresentationTests
{
    [Fact]
    public async Task Expired_corpse_stops_being_drawn_but_stays_in_the_instance_for_corpse_skills()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var conn = runtime.Connection(instance, wire);
        conn.LockstepClient = true;
        instance.World.Runner = conn;
        var variety = Game.Monsters.All.First();
        const uint oldId = 0x08000100, freshId = 0x08000101;
        double first = await Program.BeginWorldFrames(conn, .033);
        var creations = new List<Task<bool>>();
        foreach (uint id in new[] { oldId, freshId })
        {
            var monster = new SpawnedMonster("victim", new PlayerWalk(110, 100, 30), 60, 8, 1000)
                { Life = 0, Dead = true };
            instance.World.Monsters[id] = monster;
            instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
                new MonsterObject(110, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject),
                (ushort)variety.Row, 1, MonsterRarity.Normal);
            conn.Monsters[id] = monster;
            creations.Add(Game.SendWorldCreate("corpse", wire.Stream, wire.Ciphers, conn, id,
                InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(variety.Id),
                    new MonsterObject(110, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject).Blob())));
        }
        await Program.EndWorldFrame(conn, (first, first + .033));
        Assert.All(await Task.WhenAll(creations), created => Assert.True(created));
        conn.MonsterViews[oldId] = new((110, 100), (110, 100), 0) { DeadSince = -Game.CorpsePresentationSeconds };
        conn.MonsterViews[freshId] = new((110, 100), (110, 100), 0) { DeadSince = conn.Clock.Elapsed.TotalSeconds };
        string Removal(uint id)
        {
            var bytes = InstanceProtocol.RemoveObject(id);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            return Convert.ToHexString(bytes);
        }
        string oldRemoval = Removal(oldId);
        string freshRemoval = Removal(freshId);
        int Count(string hex) => Program.HistoryFor(wire.Ciphers).Lines()
            .Count(line => line.Contains(hex, StringComparison.Ordinal));

        await Game.SyncInstanceMonsters("corpse", wire.Stream, wire.Ciphers, conn);
        await Program.EndWorldFrame(conn, (first + .033, first + .066));

        Assert.Equal(1, Count(oldRemoval));
        Assert.Equal(0, Count(freshRemoval));
        Assert.DoesNotContain(oldId, conn.MonsterViews.Keys);
        Assert.Contains(freshId, conn.MonsterViews.Keys);
        // Presentation only: both corpses are still authoritative, so corpse skills keep their former rules.
        Assert.Contains(oldId, instance.World.Monsters.Keys);
        Assert.Contains(oldId, instance.World.MonsterOrigins.Keys);
        Assert.Contains(oldId, conn.Monsters.Keys);
        int removals = Count(oldRemoval);
        await Game.SyncInstanceMonsters("corpse", wire.Stream, wire.Ciphers, conn);
        await Program.EndWorldFrame(conn, (first + .066, first + .099));
        Assert.Equal(removals, Count(oldRemoval)); // no recreate, no second removal
    }

    [Fact]
    public async Task A_death_seen_in_a_live_view_starts_the_visual_clock()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var conn = runtime.Connection(instance, wire);
        instance.World.Runner = conn;
        var variety = Game.Monsters.All.First();
        const uint id = 0x08000102;
        var monster = new SpawnedMonster("victim", new PlayerWalk(110, 100, 30), 60, 8, 1000);
        instance.World.Monsters[id] = monster;
        instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
            new MonsterObject(110, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject),
            (ushort)variety.Row, 1, MonsterRarity.Normal);
        conn.Monsters[id] = monster;
        conn.MonsterViews[id] = new((110, 100), (110, 100), 1000);
        monster.Dead = true;
        monster.Life = 0;

        await Game.SyncInstanceMonsters("corpse", wire.Stream, wire.Ciphers, conn);

        Assert.Equal(0u, conn.MonsterViews[id].Life);
        Assert.False(double.IsNaN(conn.MonsterViews[id].DeadSince));
    }
}
