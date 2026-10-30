using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    internal static readonly ushort[][] AntiquarianBuilds =
    [
        [23940, 24105, 48353, 18893, 34223, 34762, 62851, 8636],
        [62851, 8636, 40664, 27339, 61085, 43431, 34708, 36978],
        [34708, 36978, 5913, 55407, 31396, 45368, 23940, 24105],
    ];

    static int AntiquarianStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Antiquarian_legal_build_paid_full_refund_reallocation_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian",
            refundPoints: 100, level: 60);
        var nodes = AntiquarianBuilds[index];
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        Assert.Equal(index == 2 ? 72 : 272, game.State.Pools.Strength);
        Assert.Equal(index == 0 ? 14 : 7, game.State.Pools.Dexterity);
        Assert.Equal(index == 2 ? 14 : 0, game.State.Pools.Intelligence);
        Assert.Equal(index == 0 ? 140 : 40, AntiquarianStat(game.State, "global_defences_+%"));
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        Assert.Equal(14, game.State.Pools.Intelligence);
        Assert.False(Program.PlayerAlwaysHits(game.State));
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(game.State.Pools.Strength, game.Reload().Pools.Strength);
        Assert.Equal((ushort)60, game.Reload().RefundPoints);
    }

}
