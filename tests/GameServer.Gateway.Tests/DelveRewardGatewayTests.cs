using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class DelveRewardGatewayTests
{
    [Fact]
    public async Task Arrival_waits_for_its_enemies_then_spills_world_items_once_and_saves_depth()
    {
        using var game = new VaalSoulGatewayTests.Fixture(area: InstanceDelve.MineArea);
        using var wire = new VaalSoulGatewayTests.Wire();
        var conn = game.Conn;
        var trip = new DelveJourney([(100, 100), (110, 100)], 24);
        conn.DelveJourney = trip;
        conn.DelveCrawlerAt = (110, 100);
        var enemy = game.Monster(corpse: false);
        trip.EncounterMonsters.Add(game.Id(enemy));
        var unrelated = game.Monster(corpse: false);
        var method = typeof(Program).GetMethod("CompleteDelveTrip", BindingFlags.NonPublic | BindingFlags.Static)!;
        Task Finish() => (Task)method.Invoke(null, ["delve-reward-test", wire.Stream, wire.Ciphers, conn, trip, (byte)35, (Func<bool>)(() => true)])!;
        await Finish();
        Assert.Empty(conn.Instance.Ground.All());
        trip.Advance(trip.Length, _ => true);
        await Finish();
        Assert.False(trip.Completed);
        enemy.Life = 0;
        await Finish();
        Assert.True(trip.Completed);
        var drops = conn.Instance.Ground.All();
        Assert.InRange(drops.Count, 16, 22);
        Assert.True(unrelated.Life > 0);
        Assert.Equal(2, CharacterSaves.Load(conn.Character!)!.CompletedDelveDepth);
        await Finish();
        Assert.Equal(drops.Count, conn.Instance.Ground.All().Count);
        Assert.All(drops, d => Assert.NotEmpty(d.Item.Blob()));
    }
}
