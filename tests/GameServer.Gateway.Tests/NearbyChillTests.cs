namespace GameServer.Gateway.Tests;

public sealed class NearbyChillTests
{
    [Fact]
    public async Task Module_unload_clears_its_nearby_chill_and_preserves_another_source()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var player = runtime.PlayerOf(new(), wire);
        var lifetime = Assert.IsType<ModuleLifetime>(Game.CombatLifetime);
        lifetime.Own(Game.CleanupNearbyChills());
        player.Clock.Stop();
        player.Walk.Teleport(100, 100, 0);

        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, sources) =>
            ReferenceEquals(owner, player)
                ? new Dictionary<uint, int>(sources) { [Stat.NearbyEnemiesAreChilled] = 1 }
                : sources);
        const uint targetId = 10;
        var target = new SpawnedMonster("nearby-chill-test", new(110, 100, 0), 0, 0, 1000) { Hostile = true };
        player.Area.Monsters[targetId] = target;
        player.Area.Grid.Set(new(player.Id), (100, 100));
        player.Area.Grid.Set(new(targetId), (110, 100));

        const MonsterElementalAilments.Kind chill = MonsterElementalAilments.Kind.Chill;
        double now = Game.AreaTime(player);
        target.Ailments.ApplyAura(chill, 42, 10, now, _ => 0, _ => 0, stated: 5);
        await Game.EnemyDebuffsTick(player, now);
        player.Area.Zones.Update(moved: true);
        await Game.EnemyDebuffsTick(player, now);
        Assert.Equal(10, target.Ailments.Magnitude(chill, now));

        lifetime.Stop(ex => throw ex);
        if (player.Area.Driven) player.Area.Step(player.Area.Frame + 1, now);
        Assert.Equal(5, target.Ailments.Magnitude(chill, now));
        Assert.Equal(0, target.Ailments.Magnitude(chill, now + 11));
    }
}
