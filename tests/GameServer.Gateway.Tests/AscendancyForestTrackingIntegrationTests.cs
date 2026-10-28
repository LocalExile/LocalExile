namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Forest_Tracking_allocation_paid_refund_and_reload_rebuild_the_current_evasion_bonus()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 3, baseClass: 2, choice: "Daughter of Oshabi",
            refundPoints: 60, level: 100, god: true);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        ushort[] path = PathTo(definition, 6281);
        uint attack = Program.Stats.Key("attack_damage_+%")!.Value;
        int Attack(ClientState conn) => Program.CharacterPlayerStats(conn).GetValueOrDefault(attack);
        Assert.Null((await game.Change(path, allocate: true)).Error);
        await game.Command("/set_stat evasion_rating 900");
        Assert.Equal(6, Attack(game.State));
        Assert.Equal(6, Attack(game.State));
        var loaded = game.Reload();
        Assert.Equal(loaded.Pools.Evasion / 450 * 3, Attack(loaded));
        ushort before = game.State.RefundPoints;
        Assert.Null((await game.Change([6281], allocate: false, refundPoints: true)).Error);
        Assert.Equal(before - 5, game.State.RefundPoints);
        Assert.Equal(0, Attack(game.State));
        Assert.Equal(0, Attack(game.Reload()));
        Assert.Null((await game.Change([6281], allocate: true)).Error);
        Assert.Equal(6, Attack(game.State));
    }
}
