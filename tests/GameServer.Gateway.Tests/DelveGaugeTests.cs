using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class DelveGaugeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(1200)]
    public async Task Opening_the_chart_synchronizes_saved_resources_and_settles_the_paused_gauge(ushort amount)
    {
        using var game = new VaalSoulGatewayTests.Fixture(area: InstanceDelve.MineArea);
        using var wire = new VaalSoulGatewayTests.Wire();
        game.Conn.DelveChartProbe = (0, 0, []);
        CharacterSaves.SetSulphite(game.Conn.Character!, amount);
        await Program.OnClientMessages("delve-gauge-test", wire.Stream, wire.Ciphers, [], [0x02, 0x3f], game.Conn);
        byte[] update = InstanceDelve.SulphiteChanged(amount, amount);
        byte[] expected = [.. InstanceDelve.Chart(), .. InstanceDelve.DelveData(amount), .. update, .. update];
        Assert.Equal(expected, await wire.Read(expected.Length));
        Assert.Equal(amount, CharacterSaves.Load(game.Conn.Character!)!.Sulphite);
    }
}
