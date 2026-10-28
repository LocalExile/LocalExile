namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Whaletooth_allocation_reload_and_paid_refund_rebuild_duration_from_current_sources()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian", refundPoints: 60, level: 20);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        ushort[] path = PathTo(definition, 27339);
        int Duration(ClientState state) => Program.CharacterPlayerStats(state)[Program.Stats.Key(SkillDurationModifiers.IncreasedDuration)!.Value];
        Assert.Equal(0, Duration(game.State));
        Assert.Null((await game.Change(path, allocate: true)).Error);
        Assert.Equal(game.State.Pools.Strength / 10, Duration(game.State));
        Assert.Equal(Duration(game.State), Duration(game.Reload()));
        await game.Command("/set_stat strength 455");
        Assert.Equal(45, Duration(game.State));
        ushort before = game.State.RefundPoints;
        Assert.Null((await game.Change([27339], allocate: false, refundPoints: true)).Error);
        Assert.Equal(before - 5, game.State.RefundPoints);
        Assert.Equal(0, Duration(game.State));
        Assert.Equal(0, Duration(game.Reload()));
        Assert.Null((await game.Change([27339], allocate: true)).Error);
        Assert.Equal(45, Duration(game.State));
        await game.Command("/set_stat skill_effect_duration_+% 20");
        Assert.Equal(20, Duration(game.State));
    }
}
