using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class HeldBackMonsterTests
{
    // An area's far monsters are the instance's from the first entry (InstanceWorld.HeldBackMonsters): counted, but not
    // created until a player comes near. Whoever enters, leaves or comes back, the instance has them, and every client
    // is shown the ones that were created.
    [Fact]
    public async Task Held_back_monsters_stay_in_the_instance_and_are_created_when_a_player_is_near()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var hostWire = new PartyFollowerTests.Wire();
        using var guestWire = new PartyFollowerTests.Wire();
        using var laterWire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var world = instance.World;
        var host = runtime.Connection(instance, hostWire);
        world.Runner = host;
        var variety = Game.Monsters.All.First(v => v.Supported && Game.Emerges.Kind(v) == EmergeKind.None);
        SavedMonster Hold(int x, int y)
        {
            var held = new SavedMonster(variety.Row, 1, MonsterRarity.Normal, Hostile: true, x, y, uint.MaxValue, Unrisen: true)
                { Planned = true };
            world.HeldBackMonsters.Add(held);
            return held;
        }
        // The players stand at (100, 100). Two monsters beyond the show distance, eight in the ring between the reveal and
        // the show distance (the last ones placed are the nearest), and one created monster as far away as the first.
        var far = Hold(100, 290);
        var never = Hold(290, 290);
        // Two of the ring stand on one cell: equal records, and still two monsters.
        SavedMonster[] ring = [.. Enumerable.Range(0, 8).Select(i => Hold(100, 255 - (i == 2 ? 1 : i)))];
        const uint standing = 0x08000000 - 1;
        world.Monsters[standing] = new SpawnedMonster("standing", new PlayerWalk(110, 290, 30), 60, 8, 1000) { Hostile = true };
        world.MonsterOrigins[standing] = new(InstanceEntities.ObjectTypeHash(variety.Id),
            new MonsterObject(110, 290, 1000, variety.ComponentsOrBase, variety.AnimatedObject), (ushort)variety.Row, 1, MonsterRarity.Normal);
        int Remaining(ClientState conn) { lock (conn.Monsters) return Game.LivingMonstersLocked(conn); }
        Task Sync(ClientState conn, PartyFollowerTests.Wire wire) =>
            Game.SyncInstanceMonsters("held-back", wire.Stream, wire.Ciphers, conn);
        // The created monsters by where they stand: a held-back one is created at a clear cell by its planned one.
        uint[] At(IEnumerable<SavedMonster> held) => [.. world.Monsters.Where(m => m.Key != standing && held.Any(h =>
            Math.Abs(m.Value.Walk.Position(0).X - h.X) <= 3 && Math.Abs(m.Value.Walk.Position(0).Y - h.Y) <= 3)).Select(m => m.Key)];

        Assert.Equal(11, Remaining(host));
        await Sync(host, hostWire);
        // The ring is created a few per pass, nearest first; what is beyond it stays held back.
        Assert.Equal([far, never, .. ring[..^Game.PopulationShownPerPass]], world.HeldBackMonsters);
        Assert.Equal(Game.PopulationShownPerPass, world.Monsters.Count - 1);
        Assert.All(world.Monsters.Keys, id => Assert.Contains(id, host.MonsterViews.Keys));
        await Sync(host, hostWire);
        Assert.Equal([far, never], world.HeldBackMonsters);
        Assert.Equal(ring.Length, At(ring).Length);
        Assert.Empty(At([far]));
        Assert.Equal(11, Remaining(host));

        // A second player counts the same monsters and is shown the created ones wherever they stand. Walking up to a
        // held-back one creates it in the runner's pass, on the runner's client first and then on the walker's.
        var guest = runtime.Connection(instance, guestWire);
        Assert.Equal(11, Remaining(guest));
        guest.Walk.Teleport(100, 280, guest.Clock.Elapsed.TotalSeconds);
        await Sync(guest, guestWire);
        Assert.All(At(ring), id => Assert.Contains(id, guest.MonsterViews.Keys));
        Assert.Contains(far, world.HeldBackMonsters);
        await Sync(host, hostWire);
        uint created = Assert.Single(At([far]));
        Assert.Contains(created, host.MonsterViews.Keys);
        Assert.DoesNotContain(created, guest.MonsterViews.Keys);
        await Sync(guest, guestWire);
        Assert.Contains(created, guest.MonsterViews.Keys);

        // Both leave. The instance still has every monster, whoever enters next runs them and is shown the created
        // ones, and the one nobody came near is still held back.
        host.Closed = true; guest.Closed = true;
        var later = runtime.Connection(instance, laterWire);
        Assert.Equal(11, Remaining(later));
        await Sync(later, laterWire);
        Assert.Same(later, world.Runner);
        Assert.All(world.Monsters.Keys, id => Assert.Contains(id, later.MonsterViews.Keys));
        Assert.Equal(10, world.Monsters.Count);
        Assert.Equal([never], world.HeldBackMonsters);
    }
}
