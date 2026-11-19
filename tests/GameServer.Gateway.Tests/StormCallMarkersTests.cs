using GameServer.Core.Protocol.Instance;
using GameServer.Gateway.Tests;
using Xunit;

public class StormCallMarkersTests
{
    static StormCallMarkers.Marker Marker(uint id, double strikeAt, int epoch = 1) =>
        new(id, 0x10, (0, 0), strikeAt, Repeat: 0, epoch, AddedChaos: 0, Use: null);

    // One due marker calls down every marker out, the not-yet-due ones included, and only once; markers set in an
    // earlier area (another epoch) are dropped without striking.
    [Fact]
    public void DueMarkerStrikesEveryMarkerOnceAndDropsOldAreas()
    {
        var markers = new StormCallMarkers();
        markers.Place(Marker(1, strikeAt: 1.5));
        markers.Place(Marker(2, strikeAt: 1.9));
        markers.Place(Marker(3, strikeAt: 0.5, epoch: 0));

        Assert.Empty(markers.TakeStrikes(1.4, epoch: 1));
        Assert.Equal(2, markers.Count);

        var struck = markers.TakeStrikes(1.5, epoch: 1);
        Assert.Equal([1u, 2u], struck.Select(m => m.Object));
        Assert.Empty(markers.TakeStrikes(1.9, epoch: 1));
        Assert.Equal(0, markers.Count);
    }

    [Fact]
    public async Task GreaterEchoStrikesShowAndHitTheSameRadius()
    {
        using var battle = new ArrowRainAndMirageTests.Battle("Storm Call");
        var conn = battle.Conn;
        conn.Clock.Start();
        var items = Game.ItemsOf(conn);
        var host = items.EquipmentSnapshot().Single();
        var staff = Game.Uniques.Create("Pledge of Hands", new Random(731))!;
        items.Remove(host.Row, host.Item.Id);
        items.Add(host.Row, 0, 0, staff with { Sockets = host.Item.Item.Sockets, Links = host.Item.Item.Links });
        var gem = Game.GemSkillEffects(conn).Single();
        Assert.Equal(2, Game.SpellRepeats(conn, gem.Id, gem.Effect));
        var sacrifice = new Game.SacrificeUse(conn, gem.Id, gem.Effect, gem.Gem);
        var area = Program.Stats.Key(SkillArea.BaseIncreased)!.Value;
        string art = Game.Art.Animated("storm_call_impact", 0)!;
        var impacts = new List<byte[]>();
        var outgoing = Program.Outgoing;
        try
        {
            Program.Outgoing = (ciphers, packet) =>
            {
                if (ReferenceEquals(ciphers, battle.Ciphers) && PacketHistory.IdOf(packet) == 0x02bb)
                {
                    impacts.Add(packet.ToArray());
                    conn.PlayerStats[area] = 50; // The strike's target scan follows the impact send.
                }
                outgoing?.Invoke(ciphers, packet);
            };
            int[] radii = [20, 21, 23];
            for (int repeat = 0; repeat < radii.Length; repeat++)
            {
                int radius = radii[repeat];
                conn.PlayerStats[area] = 0;
                impacts.Clear();
                var inside = conn.Monsters[300] = new("edge", new(100 + radius, 100, 0), 0, 0, 100000) { Hostile = true };
                var outside = conn.Monsters[301] = new("outside", new(101 + radius, 100, 0), 0, 0, 100000) { Hostile = true };
                var marker = new StormCallMarkers.Marker(123, gem.Id, (100, 100), 0, repeat, conn.ProjectileEpoch, 0,
                    new Game.StormCallUse(null, sacrifice, 1, repeat == radii.Length - 1));

                await Game.StormCallStrike("test", battle.Stream, battle.Ciphers, conn, marker, removeMarker: true);

                // The default lightning art covers 23 cells at unit scale.
                var expected = new EffectObject(100, 100, art) { Flags0 = EffectObject.FixedRotation, Scale = radius / 23f };
                Assert.True(Assert.Single(impacts).AsSpan().EndsWith(expected.Blob()));
                Assert.True(inside.Life < inside.MaxLife);
                Assert.Equal(outside.MaxLife, outside.Life);
            }
        }
        finally { Program.Outgoing = outgoing; }
    }
}
