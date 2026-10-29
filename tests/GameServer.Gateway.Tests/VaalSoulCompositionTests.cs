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

    [Fact]
    public async Task Per_instance_aura_restriction_and_cost_selector_compose_with_explicit_removal()
    {
        using var f = new VaalSoulGatewayTests.Fixture(); using var w = new VaalSoulGatewayTests.Wire();
        f.Gems("SkillGemVaalHaste", "SkillGemVaalGrace", "SkillGemVaalFireball");
        await Command(f, w, "/set_stat virtual_aura_vaal_skill_soul_requirement_multipler 100");
        await Command(f, w, "/set_stat base_your_auras_are_disabled 1");
        foreach (var b in f.Conn.VaalSouls.Read())
            Assert.Equal(b.ActiveSkillsRow == 87 ? 30u : 100u, b.Capacity);
        for (int i = 0; i < 6; i++) Assert.True(f.Kill());
        IReadOnlyList<GrantedSkill> Skills() => (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var haste = Program.Skills.FindByRow(0, 1963, 1)!.EffectsPerLevelRow;
        Dictionary<uint, int> Haste() => Skills().Single(s => s.EffectsPerLevelRow == haste).Stats!
            .ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(1, Haste()[Key("skill_is_aura_skill")]);
        Assert.Equal(1, Haste()[OwnAuraRestriction.SkillDisabled]);
        await Command(f, w, "/set_stat skill_is_aura_skill 0");
        Assert.Equal(50u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 239).Capacity);
        Assert.Equal(0, Haste()[Key("skill_is_aura_skill")]);
        Assert.Equal(1, Haste()[OwnAuraRestriction.SkillDisabled]);
        await Command(f, w, "/set_stat skill_is_aura_skill off");
        Assert.Equal(100u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 239).Capacity);
        Assert.Equal(1, Haste()[Key("skill_is_aura_skill")]);
        Assert.All(f.Conn.VaalSouls.Read(), b => Assert.Equal(2u, b.Current));
    }

    [Fact]
    public async Task Actual_chaos_hit_death_clears_souls_and_healing_does_not_restore_the_old_balance()
    {
        using var f = new VaalSoulGatewayTests.Fixture(); using var w = new VaalSoulGatewayTests.Wire();
        for (int i = 0; i < 3; i++) Assert.True(f.Kill());
        await Command(f, w, "/set_stat base_armour_applies_to_chaos_damage 1");
        Assert.True(f.Conn.Pools.ArmourAppliesToChaosDamage);
        await Command(f, w, "/hit chaos 100000");
        Assert.Equal(0u, f.Conn.PlayerLife);
        Assert.Equal(0u, f.Budget.Current);
        Assert.False(f.Kill());
        byte[] expected = InstanceEntities.PlayerSkillsChange(0x1000, [], [], [], [new(87, 0, 30)]);
        await w.Through(expected);
        await Command(f, w, "/heal");
        Assert.True(f.Conn.PlayerLife > 0); Assert.Equal(0u, f.Budget.Current);
        Assert.True(f.Kill()); Assert.Equal(1u, f.Budget.Current);
    }
}
