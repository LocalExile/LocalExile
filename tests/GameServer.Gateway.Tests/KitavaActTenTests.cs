using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class KitavaActTenTests
{
    [GameFilesFact]
    public async Task Campaign_Kitava_uses_the_authored_body_position_and_does_not_turn_its_attacks()
    {
        using var runtime = new MinionUtilityTests.Fixture();
        foreach (var (area, varietyId, level) in new[]
            { (KitavaActFive.Area, KitavaActFive.First, (byte)45), (KitavaActTen.Area, KitavaActTen.Body, (byte)67) })
        {
            const uint seed = 101;
            ushort hash = AreaTransitionCatalog.HashOf(area)!.Value;
            var layout = AreaLayouts.For(hash, seed);
            var point = Assert.Single(AreaPopulation.LoadPoints(area, seed, []), p => p.Name == "force:mapboss");
            var player = new Player(layout, seed, 1)
            {
                Instance = new(), PlayerLife = 10000,
                Connection = { PlayerSent = true, Session = new("kitava-anchor", runtime.Stream, runtime.Ciphers) },
            };
            try
            {
                var variety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(varietyId));
                var proposed = layout.Near((point.X, point.Y));
                uint id = await Game.SpawnMonster(player, variety, level, MonsterRarity.Unique, proposed.X, proposed.Y);
                var origin = player.Instance.World.MonsterOrigins[id].Object;
                Assert.Equal((point.X, point.Y, point.Angle), (origin.GridX, origin.GridY, origin.Rotation));
                Assert.Equal((point.X, point.Y), player.Area.Monsters[id].Walk.Position(0));
                Assert.NotEmpty(origin.Skills!);
                Assert.All(origin.Skills!, skill => Assert.Contains((Stat.ActionDoNotFaceTarget, 1), skill.Stats!));
            }
            finally { player.Closed = true; }
        }
    }

    [GameFilesFact]
    public void Direct_epilogue_portal_uses_the_shore_entrance_connected_to_the_docks()
    {
        ushort shore = AreaTransitionCatalog.HashOf(KitavaActTen.Epilogue)!.Value;
        ushort docks = AreaTransitionCatalog.HashOf(KitavaActTen.Docks)!.Value;
        Assert.Equal(docks, KitavaActTen.ArrivalSource(KitavaActTen.Area, shore));
        Assert.Null(KitavaActTen.ArrivalSource(KitavaActTen.Area, docks));
        var layout = AreaLayouts.For(shore, 1);
        var entrance = layout.EntranceFrom?.Invoke(docks, null);
        Assert.NotNull(entrance);
        var pt = (entrance.Value.X, entrance.Value.Y);
        bool onTransition = layout.AreaTransitions.Any(t => t.Transition.DestinationHash16 == docks && t.Transition.TeleportTo is null
            && Math.Abs(t.Transition.GridX - pt.X) <= 4 && Math.Abs(t.Transition.GridY - pt.Y) <= 4);
        var expected = entrance.Value.NamedPoint && !onTransition && (layout.Walkable is null || layout.Walkable(pt.X, pt.Y))
            ? pt : layout.Near(pt);
        Assert.Equal(expected, layout.Arrival(KitavaActTen.ArrivalSource(KitavaActTen.Area, shore)));
        Assert.NotEqual((layout.SpawnX, layout.SpawnY), layout.Arrival(docks));
    }
}
