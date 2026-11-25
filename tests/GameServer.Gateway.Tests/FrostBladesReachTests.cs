using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class FrostBladesReachTests
{
    [Fact]
    public async Task Katabasis_checks_the_reach_of_its_primary_strike()
    {
        var (farHit, _) = await Strike(168, 168, quality: 0, named: true);
        Assert.False(farHit);

        var (qualityHit, qualityNeighbour) = await Strike(168, 168, quality: 20, named: true);
        Assert.True(qualityHit);
        Assert.False(qualityNeighbour);

        var (movingHit, movingNeighbour) = await Strike(142, 160, quality: 0, named: true);
        Assert.True(movingHit);
        Assert.False(movingNeighbour);

        var (groundHit, groundNeighbour) = await Strike(142, 142, quality: 0, named: false);
        Assert.True(groundHit);
        Assert.False(groundNeighbour);
    }

    static async Task<(bool Hit, bool Neighbour)> Strike(int currentX, int aimedX, byte quality, bool named)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "katabasis", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));

        var items = Game.ItemsOf(player);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        var gem = Program.Items.FindGem("Frost Blades of Katabasis")!.Value;
        var sword = ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
        {
            Sockets = [new(SocketColour.White, ItemCatalog.Create(gem.Type) with
                { GemVariant = gem.Variant.Index, Quality = quality })],
            Links = [1],
        };
        items.Change(PlayerInventories.Weapon1, host.Id, _ => sword);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
            }
            return values;
        });
        var skill = Assert.Single(Game.GemSkillEffects(player));
        const uint totalLife = 10_000;
        SpawnedMonster Monster(uint id, int x)
        {
            var m = new SpawnedMonster("strike witness", new(x, 100, 0), 0, 0, totalLife)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = m;
            player.Area.Grid.Set(new(id), (x, 100));
            return m;
        }
        var target = Monster(901, currentX);
        var neighbour = Monster(902, currentX + 2);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            await Game.AreaSkillHit(player, skill.Id, skill, (100, 100), (aimedX, 100),
                immediate: true, sequence: 0x8002, targetId: named ? target.ObjectId : 0);
            return (target.Life < totalLife, neighbour.Life < totalLife);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
