using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Start 409 opens three seven-node rings; each build is one whole ring plus the next ring's first small node.
    internal static readonly ushort[][] PolytheistBuilds =
    [
        [27664, 22056, 26231, 32742, 21979, 61900, 59443, 46465],
        [46465, 64171, 51229, 43703, 29846, 1830, 6605, 30769],
        [30769, 5266, 322, 24342, 33835, 23429, 25478, 27664],
    ];
    static readonly ushort[] PolytheistSmallNodes = [27664, 46465, 6605, 33835, 30769, 29846, 21979, 59443, 322, 51229, 26231, 25478];

    static int PolytheistStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Fact]
    public void Polytheist_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(5, AscendancyFamily.Phrecia, "Polytheist")!;
        Assert.Equal(new AscendancyIdentity(AscendancyFamily.Phrecia, "Guardian", "3.29.3b"), definition.Identity);
        Assert.Equal((ushort)409, definition.StartNode);
        Assert.Equal(new ushort[] { 409, 27664, 32742, 43703, 24342, 46465, 6605, 33835, 30769, 29846, 64171, 22056,
            1830, 21979, 23429, 59443, 61900, 322, 5266, 51229, 26231, 25478 }.Order(), definition.Nodes.Keys.Order());
        Assert.Equal(definition.Nodes.Keys.Where(n => n != 409).Order(), PolytheistBuilds.SelectMany(b => b).Distinct().Order());
        Assert.All(definition.Nodes.Values, n => Assert.Equal(0, n.PointsGranted));
        // The default gate stays closed; the opt-in's refusal names this class's page.
        if (Environment.GetEnvironmentVariable("Game_VERIFIED_POLYTHEIST") != "1")
            Assert.Throws<AscendancyRuleException>(() => AscendancyClientCompatibility.RequirePlayable(definition.Identity));
        Assert.Contains("polytheist.md", Assert.Throws<AscendancyRuleException>(
            () => VerifiedGamblerGraph.Verify([], [], "Polytheist")).Message);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Polytheist_legal_build_paid_refund_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", refundPoints: 100, level: 60);
        var nodes = PolytheistBuilds[index];
        var plain = game.State.Pools;
        int small = nodes.Count(PolytheistSmallNodes.Contains);
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        AssertNodeConsumers(game.State, true);
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        AssertNodeConsumers(game.Reload(), true);

        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        AssertNodeConsumers(game.State, false);
        if (index == 2)
        {
            // A plain character is frozen and chilled by a critical cold hit; Brine King then drops both on the next tick.
            var state = game.State;
            var cold = DamagePacket.Of(DamageType.Cold, state.PlayerMaxLife, state.PlayerMaxLife);
            double now = state.Clock.Elapsed.TotalSeconds;
            lock (state.Monsters)
                Program.ApplyPlayerAilmentsLocked(state, cold, cold, true, false, now, Program.CharacterPlayerStats(state));
            Assert.Equal((false, 30, true, 0), state.ElementalAilments.Snapshot(now));
            Assert.Null((await game.Change(nodes, true)).Error);
            lock (state.Monsters)
                Program.AdvancePlayerAilmentsLocked(state, now, now + .25, Program.CharacterPlayerStats(state));
            Assert.Equal((false, 0, false, 0), state.ElementalAilments.Snapshot(now + .25));
        }
        else Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal((ushort)60, game.Reload().RefundPoints);

        void AssertNodeConsumers(ClientState state, bool allocated)
        {
            // Small nodes: 10% increased Defences reach every defence's increase; light radius goes to the client.
            int defences = allocated ? 10 * small : 0;
            Assert.Equal(defences, PolytheistStat(state, "global_defences_+%"));
            Assert.Equal(plain.ArmourPct + defences, state.Pools.ArmourPct);
            Assert.Equal(plain.EvasionPct + defences, state.Pools.EvasionPct);
            Assert.Equal(plain.EnergyShieldPct + defences, state.Pools.EnergyShieldPct);
            Assert.True(allocated ? state.Pools.Evasion > plain.Evasion : state.Pools.Evasion == plain.Evasion);
            Assert.Equal(allocated ? 5 * small : 0, PolytheistStat(state, "light_radius_+%"));
            // Brine King: maximum cold resistance, and the same critical cold hit neither chills nor freezes.
            bool brine = allocated && index == 2;
            Assert.Equal(plain.ColdMaxResistance + (brine ? 10 : 0), state.Pools.ColdMaxResistance);
            var probe = new PlayerElementalAilments();
            var stats = Program.CharacterPlayerStats(state);
            var hit = DamagePacket.Of(DamageType.Cold, 1000, 1000);
            probe.Apply(hit, hit, true, 1000, 0, n => stats.GetValueOrDefault(Program.Stats.Key(n)!.Value), new Random(1));
            Assert.Equal(brine ? (false, 0, false, 0) : (false, 30, true, 0), probe.Snapshot(0));
            // Ralakesh: three of each charge without gaining any, in the stats and in the charge session.
            int minimum = allocated && index == 1 ? 3 : 0;
            var session = ProgramValue<ChargeSession>("ChargesOf", state);
            foreach (var type in Charges.Types)
            {
                Assert.Equal(minimum, PolytheistStat(state, $"current_{Charges.Name(type)}_charges"));
                Assert.Equal(minimum, session.Limits(type).Minimum);
                Assert.Equal(minimum, session.Effective(type));
            }
        }
    }
}
