using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulCompositionTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static object Call(string name, params object[] args) => typeof(Program)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    static async Task Command(VaalSoulGatewayTests.Fixture f, VaalSoulGatewayTests.Wire w, string text)
    {
        f.Conn.Commands ??= (ChatCommands)Call("Commands", "vaal-composition", w.Stream, w.Ciphers, f.Conn);
        var bytes = new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray();
        var messages = InstanceProtocol.SplitClientMessages(bytes, out var partial, out var unknown);
        Assert.Empty(partial); Assert.Empty(unknown);
        await Program.OnClientMessages("vaal-composition", w.Stream, w.Ciphers, messages, [], f.Conn)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Override_removal_restores_cost_and_gain_without_refilling_the_budget()
    {
        using var f = new VaalSoulGatewayTests.Fixture(); using var w = new VaalSoulGatewayTests.Wire();
        for (int i = 0; i < 20; i++) Assert.True(f.Kill());
        await Command(f, w, "/set_stat virtual_non_aura_vaal_skill_soul_requirement_multipler 100");
        Assert.Equal(new VaalSoulBudget(87, 60, 20), f.Budget);
        await Command(f, w, "/set_stat virtual_non_aura_vaal_skill_soul_requirement_multipler off");
        Assert.Equal(new VaalSoulBudget(87, 30, 20), f.Budget);
        await Command(f, w, "/set_stat has_soul_gain_prevention 1");
        Assert.False(f.Kill()); Assert.Equal(20u, f.Budget.Current);
        await Command(f, w, "/set_stat has_soul_gain_prevention off");
        Assert.True(f.Kill()); Assert.Equal(21u, f.Budget.Current);
    }

}
