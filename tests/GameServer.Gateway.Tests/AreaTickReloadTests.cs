using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class AreaTickReloadTests
{
    static Player InArea(params string[] modules) => new(new(1, "area-tick-test", 1000, 1000, 100, 100, null, []), 1, 1)
    {
        LeagueModule = new(new League("area-tick-test", "Standard", false, false), new HashSet<string>(modules))
    };

    [Fact]
    public async Task Area_tick_applies_enable_unload_and_reload_to_an_existing_player()
    {
        const string owner = "area-tick-reload-test";
        var player = InArea(owner);
        var runner = new GameEvents.AreaTickRunner(player);
        List<string> ticks = [];
        await runner.Run(.05);
        Assert.Empty(ticks);
        Assert.False(runner.NeedsTick);

        var start = GameEvents.Always((p, _) =>
        {
            Assert.Same(player, p);
            ticks.Add("old");
            return Task.CompletedTask;
        });
        using var original = GameEvents.AreaTick.Add(owner, start);
        using var second = GameEvents.AreaTick.Add(owner, start);
        Assert.True(runner.NeedsTick);
        await runner.Run(.05);
        Assert.Equal(["old", "old"], ticks);

        original.Dispose();
        await runner.Run(.05);
        Assert.Equal(["old", "old", "old"], ticks);

        GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
        Assert.True(runner.NeedsTick);
        await runner.Run(.05);
        Assert.Equal(["old", "old", "old"], ticks);
        Assert.False(runner.NeedsTick);

        using var replacement = GameEvents.AreaTick.Add(owner, GameEvents.Always((_, _) =>
        {
            ticks.Add("new");
            return Task.CompletedTask;
        }));
        await runner.Run(.05);
        Assert.Equal(["old", "old", "old", "new"], ticks);
    }

    [Fact]
    public async Task Area_tick_preserves_other_bindings_when_a_provider_re_registers_its_factory()
    {
        const string stable = "area-tick-stable-test", replaced = "area-tick-replaced-test";
        var runner = new GameEvents.AreaTickRunner(InArea(stable, replaced));
        int skippedStarts = 0, replacedStarts = 0;
        List<string> ticks = [];
        using var steady = GameEvents.AreaTick.Add(stable, _ =>
        {
            int count = 0;
            return (_, _) =>
            {
                ticks.Add($"stable:{++count}");
                return Task.CompletedTask;
            };
        });
        using var skipped = GameEvents.AreaTick.Add(stable, _ => { skippedStarts++; return null; });
        Func<Player, Func<Player, double, Task>?> start = _ =>
        {
            int generation = ++replacedStarts;
            return (_, _) =>
            {
                ticks.Add($"replaced:{generation}");
                return Task.CompletedTask;
            };
        };
        using var original = GameEvents.AreaTick.Add(replaced, start);
        await runner.Run(.05);
        GameEventOwners.Release(replaced, GameEventOwners.ModuleRank);
        using var replacement = GameEvents.AreaTick.Add(replaced, start);
        await runner.Run(.05);
        await runner.Run(.05);

        Assert.Equal(1, skippedStarts);
        Assert.Equal(2, replacedStarts);
        Assert.Equal(["stable:1", "replaced:1", "stable:2", "replaced:2", "stable:3", "replaced:2"], ticks);
    }
}
