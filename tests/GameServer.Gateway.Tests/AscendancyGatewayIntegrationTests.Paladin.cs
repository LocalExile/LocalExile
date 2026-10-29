using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Eight paid points each; the Paladin Aura options are free. With the Discipline swap they reach every node.
    internal static readonly ushort[][] PaladinBuilds =
    [
        [363, 13763, 29126, 40299, 2437, 52104, 43835, 46492],
        [38874, 44795, 58846, 2134, 40171, 49271, 34448, 54067, 42396],
        [54067, 42396, 11127, 34448, 49271, 40171, 38874, 44795, 28569],
    ];
    static readonly ushort[] PaladinSmallNodes = [2134, 49271, 363, 29126, 54067, 43835, 2437, 38874, 11127];

    static int PaladinStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Fact]
    public void Paladin_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(4, AscendancyFamily.Phrecia, "Paladin")!;
        Assert.Equal("Champion", definition.Identity.Id);
        Assert.Equal((ushort)24984, definition.StartNode);
        Assert.Empty(definition.Nodes[24984].Stats);
        Assert.Equal(new ushort[] { 24984, 2134, 49271, 363, 29126, 54067, 43835, 13763, 40299, 46492, 52104, 42396, 40171,
            2437, 34448, 44795, 58846, 28569, 39868, 38874, 11127 }.Order(), definition.Nodes.Keys.Order());
        Assert.True(definition.Nodes[44795].IsChoice);
        Assert.All(new ushort[] { 58846, 28569, 39868 }, n => Assert.True(definition.Nodes[n].IsChoiceOption));
        Assert.Equal(definition.Nodes.Keys.Where(n => n != definition.StartNode).Order(),
            PaladinBuilds.SelectMany(b => b).Append((ushort)39868).Distinct().Order());
        Assert.All(PaladinBuilds, b => Assert.Equal(8, b.Sum(n => definition.Nodes[n].PointCost)));
        string Named(IReadOnlyList<(uint Key, int Value)> stats) =>
            string.Join(",", stats.Select(s => $"{Program.Stats.Name(s.Key)}={s.Value}").Order());
        Assert.All(PaladinSmallNodes, n => Assert.Equal("life_regeneration_rate_per_minute_%=30,non_curse_aura_effect_+%=5",
            Named(definition.Nodes[n].Stats)));
        Assert.Equal("consecrated_ground_effect_+%=50,consecrated_ground_while_stationary_radius=18," +
            "your_consecrated_ground_effect_lingers_for_ms_after_leaving_the_area=4000", Named(definition.Nodes[52104].Stats));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Paladin_legal_build_paid_full_refund_reallocation_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Paladin", refundPoints: 100);
        var nodes = PaladinBuilds[index];
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, Program.AscendancyRules.SpentPoints(4, game.State.MainAscendancy));
        Assert.Equal(20, PaladinStat(game.State, "non_curse_aura_effect_+%")); // four small nodes in each build
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        if (index == 1)
        {
            // Changing the Paladin Aura costs the option's refund; the parent stays and the new option is free.
            Assert.Null((await game.Change([58846], false, refundPoints: true)).Error);
            Assert.Equal((ushort)95, game.State.RefundPoints);
            Assert.Null((await game.Change([39868], true)).Error);
            nodes = [.. nodes.Where(n => n != 58846), 39868];
            Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        }
        Assert.Null((await game.Change([.. nodes.Reverse()], false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)(index == 1 ? 55 : 60), game.State.RefundPoints); // the choice shell refunds for free
        Assert.Equal(0, PaladinStat(game.State, "non_curse_aura_effect_+%"));
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Equal(game.State.RefundPoints, game.Reload().RefundPoints);
    }

    [Fact]
    public async Task Paladin_small_nodes_regenerate_like_the_plain_stat_and_raise_the_aura_effect()
    {
        if (!DataAvailable()) return;
        using var plain = new GatewayCharacter(choice: "Paladin", level: 70);
        using var game = new GatewayCharacter(choice: "Paladin", level: 70);
        double Regen(ClientState state) => ProgramValue<double>("LifeRegenPerSecond", state);
        double baseline = Regen(plain.State);
        int baseAura = plain.State.Pools.AuraEffectPct;
        await plain.Command("/set_stat life_regeneration_rate_per_minute_% 30");
        Assert.Null((await game.Change([2134], true)).Error);
        Assert.Equal(Regen(plain.State), Regen(game.State), 6);
        Assert.Equal(baseline + game.State.Pools.Life * .005, Regen(game.State), 6);
        Assert.Equal(baseAura + 5, game.State.Pools.AuraEffectPct);

        // The aura effect reaches a running aura's bonuses only: without one both characters have the same armour.
        Assert.Equal(plain.State.Pools.Armour, game.State.Pools.Armour);
        int row = Program.Skills.EffectRow("Determination");
        var determination = new AuraCatalog.Aura(0x103, row, "Determination", "Determination", 7, AuraCatalog.BuffRow("Determination"), 0, 0, Level: 20);
        var plainBonus = AuraCatalog.Bonus([determination], baseAura, Program.Skills, Program.Stats);
        var nodeBonus = AuraCatalog.Bonus([determination], game.State.Pools.AuraEffectPct, Program.Skills, Program.Stats);
        Assert.True(nodeBonus.Armour > plainBonus.Armour && nodeBonus.ArmourMorePct > plainBonus.ArmourMorePct);
        foreach (var character in new[] { plain, game })
        {
            character.State.Auras[determination.SkillId] = determination;
            character.State.Pools = Program.PoolsFor(character.State, character.State.Pools.Level);
        }
        Assert.True(game.State.Pools.Armour > plain.State.Pools.Armour);

        // The most a legal build holds: the six start neighbours, Absolution and 49271 behind it (seven small nodes).
        Assert.Null((await game.Change([363, 2437, 38874, 43835, 54067, 40171, 49271], true)).Error);
        Assert.Equal(baseline + game.State.Pools.Life * .035, Regen(game.State), 6);
        Assert.Equal(baseAura + 35, game.State.Pools.AuraEffectPct);
    }

    [Fact]
    public async Task Anointed_Champion_consecrates_the_ground_while_stationary_with_more_effect_and_a_linger()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Paladin", level: 70);
        Assert.Null((await game.Change([2437, 52104], true)).Error); // the stats send configures the stationary radius
        var state = game.State;
        int Regen() => PaladinStat(state, "life_regeneration_rate_per_minute_%");
        Assert.Equal(30, Regen());
        Assert.False(await game.GroundsTick(.5)); // standing since the start: ground made, on it
        Assert.Equal(30 + 450, Regen());
        // Walking (kept walking by new orders) takes the ground away; its effect lingers 4 s after the last covered tick.
        state.Walk.Order(120, 32, 1);
        Assert.False(await game.GroundsTick(1.1));
        Assert.False(state.FlaskGrounds.Covers(FlaskGrounds.Kind.ConsecratedGround, state.Walk.Position(1.1)));
        Assert.Equal(30 + 450, Regen());
        state.Walk.Order(10, 32, 2);
        state.Walk.Order(120, 32, 3);
        state.Walk.Order(10, 32, 4);
        Assert.False(await game.GroundsTick(4.45));
        Assert.Equal(30 + 450, Regen());
        Assert.True(state.Walk.ArrivesAt > 4.6);
        Assert.False(await game.GroundsTick(4.55));
        Assert.Equal(30, Regen());
        // Standing again: a new ground where the player stopped.
        double stop = state.Walk.ArrivesAt;
        Assert.False(await game.GroundsTick(stop + .1));
        Assert.Equal(30 + 450, Regen());
        // Refunded: the ground goes and the ticker stops once nothing is left.
        Assert.Null((await game.Change([52104], false, refundPoints: true)).Error);
        Assert.True(await game.GroundsTick(stop + .2));
        Assert.Equal(30, Regen());
        Assert.False(state.FlaskGrounds.Any);

        // A plain Sulphur Flask ground without the node: the base 300, gone with the ground (no linger).
        using var plain = new GatewayCharacter(choice: "Paladin", level: 70);
        var at = plain.State.Walk.Position(1);
        plain.State.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, at.X, at.Y, 18, 2));
        Assert.False(await plain.GroundsTick(1));
        Assert.Equal(300, PaladinStat(plain.State, "life_regeneration_rate_per_minute_%"));
        Assert.True(await plain.GroundsTick(2.1));
        Assert.Equal(0, PaladinStat(plain.State, "life_regeneration_rate_per_minute_%"));
    }

    // Each Paladin Aura option grants its aura at the row's fixed level; it casts, reserves, restores and goes as a gem's.
    [Theory]
    [InlineData((ushort)58846, 17772, "Determination", 30)]
    [InlineData((ushort)28569, 17460, "Grace", 30)]
    [InlineData((ushort)39868, 17316, "Discipline", 40)]
    public async Task Paladin_Aura_option_grants_a_castable_aura_equal_to_a_plain_one(ushort option, int gepl, string effectId, int level)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Paladin", level: 70, refundPoints: 100);
        using var plain = new GatewayCharacter(choice: "Paladin", level: 70);
        var before = game.State.Pools;
        IReadOnlyList<GrantedSkill> Skills(ClientState state) => ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", state);
        Assert.Null((await game.Change([38874, 44795, option], true)).Error);
        Assert.Null((await plain.Change([38874], true)).Error); // the same 5% aura effect, no option
        var skill = Assert.Single(Skills(game.State), s => s.EffectsPerLevelRow == gepl);
        Assert.Empty(skill.Supports);
        Assert.DoesNotContain(Skills(plain.State), s => s.EffectsPerLevelRow == gepl);

        // Cast as the client casts an aura (0x24 on the player): on, at the row's level, with the row's reservation.
        await game.UseOnSelf(skill.Id);
        int effect = Program.Skills.EffectRow(effectId);
        var aura = Assert.Single(game.State.Auras.Values);
        Assert.Equal((skill.Id, effect, level, 0), (aura.SkillId, aura.Effect, aura.Level, aura.Quality));
        Assert.Equal(Program.Skills.Reservation(effect, level)!.Value.Percent, aura.ManaPercent);
        plain.State.Auras[0x103] = new(0x103, effect, effectId, effectId, 7, AuraCatalog.BuffRow(effectId), 0, aura.ManaPercent, level);
        plain.State.Pools = Program.PoolsFor(plain.State, 70);
        var (pools, reference) = (game.State.Pools, plain.State.Pools);
        Assert.Equal((reference.Armour, reference.Evasion, reference.EnergyShield), (pools.Armour, pools.Evasion, pools.EnergyShield));
        Assert.Equal(AuraCatalog.ReservedMana(reference, plain.State.Auras.Values), AuraCatalog.ReservedMana(pools, game.State.Auras.Values));
        switch (effectId)
        {
            case "Determination": Assert.True(pools.Armour > before.Armour); break;
            case "Grace": Assert.True(pools.Evasion > before.Evasion); break;
            default:
                // Discipline's 356 flat energy shield, 5% more from 38874, before the increases; 30% faster recharge.
                Assert.Equal(0u, before.EnergyShield);
                Assert.Equal(EnergyShield.Max(374, pools.EnergyShieldPct), pools.EnergyShield);
                Assert.Equal(32, PaladinStat(game.State, "energy_shield_recharge_rate_+%"));
                Assert.Equal((int)Math.Round(pools.EnergyShield * 20 * 1.32),
                    PaladinStat(game.State, "energy_shield_recharge_rate_per_minute"));
                break;
        }

        // A new connection restores the saved toggle on the same skill id.
        var restored = Assert.Single(game.Reload().Auras.Values);
        Assert.Equal((skill.Id, level), (restored.SkillId, restored.Level));
        // Refunding the option takes the skill, the aura and its reservation away.
        Assert.Null((await game.Change([option], false, refundPoints: true)).Error);
        Assert.Empty(game.State.Auras);
        Assert.DoesNotContain(Skills(game.State), s => s.EffectsPerLevelRow == gepl);
        Assert.Equal((before.Armour, before.Evasion, before.EnergyShield),
            (game.State.Pools.Armour, game.State.Pools.Evasion, game.State.Pools.EnergyShield));
    }

    [Fact]
    public void Paladin_stays_behind_the_closed_gate_and_its_refusal_names_its_checklist()
    {
        if (Environment.GetEnvironmentVariable("Game_VERIFIED_PALADIN") != "1")
            Assert.Throws<AscendancyRuleException>(() => AscendancyClientCompatibility.RequirePlayable(
                new(AscendancyFamily.Phrecia, "Champion", "3.29.3b")));
        Assert.Contains("paladin.md", Assert.Throws<AscendancyRuleException>(
            () => VerifiedGamblerGraph.Verify([], [], "Paladin")).Message);
    }

    sealed partial class GatewayCharacter
    {
        // C→S 0x24 with the player as the target: how the client casts an aura or another self skill.
        public Task<(string? Error, byte[] Bytes)> UseOnSelf(ushort skill) =>
            Dispatch(new LoginWriter().U16(0x24).U32(Program.PlayerId).U16(skill).U16(1).U16(0).ToArray());

        // One flask-grounds step at a chosen game time (Program.FlaskGroundsTick): true when its ticker would stop.
        public async Task<bool> GroundsTick(double now)
        {
            bool stop = false;
            await Exchange(async (stream, cipher) =>
            {
                stop = await (Task<bool>)GatewayMethod("FlaskGroundsTick").Invoke(null, ["paladin-integration", stream, cipher, State, now])!;
                return null;
            });
            return stop;
        }
    }
}
