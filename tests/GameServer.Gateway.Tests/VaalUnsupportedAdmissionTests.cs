using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulGatewayTests
{
    [Theory]
    [InlineData("full")]
    [InlineData("free")]
    [InlineData("reduced")]
    public void Vaal_preflight_does_not_disable_the_same_gems_ordinary_partner(string cost)
    {
        using var f = new Fixture();
        if (cost == "free") Set(f.Conn, VaalSoulCost.NoSouls, 1);
        if (cost == "reduced") Set(f.Conn, VaalSoulCost.FinalPercent, -100);
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var ordinary = skills.Single(s => s.EffectsPerLevelRow == 0);
        Assert.False(ordinary.VaalResourcesEnabled);
        bool started = false;
        Assert.True(Program.TryBeginSkillCooldownUse(f.Conn, ordinary.Id, Program.Skills.EffectRow("Fireball"),
            null, false, () => started = true, out var ticket));
        Assert.True(started); Assert.Null(ticket);
    }

    public static IEnumerable<object[]> UnsupportedVaalInputs()
    {
        foreach (string cost in new[] { "full", "free", "reduced" })
            foreach (var (gem, route) in new[]
            {
                ("SkillGemVaalFireball", "target"), ("SkillGemVaalGroundSlam", "approach"),
                ("SkillGemVaalFireball", "ground"), ("SkillGemVaalGroundSlam", "attack-move"),
                ("SkillGemVaalHaste", "self"), ("SkillGemVaalHaste", "instant"),
                ("SkillGemVaalHaste", "instant-location")
            }) yield return [gem, route, cost];
    }

    [Theory]
    [MemberData(nameof(UnsupportedVaalInputs))]
    public async Task Unsupported_Vaal_input_cannot_start_an_action_or_spend_resources(
        string gemName, string route, string cost)
    {
        using var f = new Fixture(); using var wire = new Wire();
        f.Gems(gemName);
        var gem = f.Items.SocketedGemPlaces().Single().Gem;
        int effect = Program.Items.GemEffects(gem).First(e => Program.Skills.VaalSouls(e, 1) > 0);
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var skill = skills.Single(s => s.EffectsPerLevelRow == Program.Skills.FindByRow(0, effect, 1)!.EffectsPerLevelRow);
        if (cost == "free") Set(f.Conn, VaalSoulCost.NoSouls, 1);
        if (cost == "reduced") Set(f.Conn, VaalSoulCost.FinalPercent, -100);
        Program.RefreshVaalSouls(f.Conn);
        while (f.Kill()) { }
        var budget = f.Budget;
        Assert.Equal(cost == "full" ? budget.Capacity : 0u, budget.Current);
        f.Conn.ActionCount = 20; f.Conn.ChannelSkill = 47;
        f.Conn.Walk.Order(140, 100, f.Conn.Clock.Elapsed.TotalSeconds);
        var destination = f.Conn.Walk.Destination;
        double arrivesAt = f.Conn.Walk.ArrivesAt;
        uint mana = f.Conn.PlayerMana, life = f.Conn.PlayerLife;
        var target = f.Monster(corpse: false, distance: 50);
        uint targetId = route == "self" ? 0x1000u : f.Id(target);
        if (route is "target" or "approach" or "self")
            await (Task)Call("OnUseObject", "vaal-admission-test", wire.Stream, wire.Ciphers, f.Conn,
                new InstanceTransfer.UseSkillOnObject(targetId, skill.Id, 1, route == "approach" ? (ushort)0 : (ushort)1),
                f.Conn.Clock.Elapsed.TotalSeconds);
        else if (route is "instant" or "instant-location")
            await (Task)Call(route == "instant" ? "UseInstantSkill" : "UseInstantSkillAtLocation",
                "vaal-admission-test", wire.Stream, wire.Ciphers, f.Conn, skill.Id,
                route == "instant" ? targetId : ((uint)150, (uint)100),
                route == "instant" ? (100, 100) : (uint?)null);
        else
        {
            ushort id = InstanceProtocol.UseSkillAtLocationId;
            byte[] input = new LoginWriter().U16(id).U32(150).U32(100).U16(skill.Id).U16(1)
                .U16(route == "attack-move" ? (ushort)0 : (ushort)1).ToArray();
            await Program.OnClientMessages("vaal-admission-test", wire.Stream, wire.Ciphers,
                [(id, input)], [], f.Conn);
        }
        // A send-gate barrier proves that no earlier action, instant action or cooldown packet was emitted.
        byte[] marker = InstanceProtocol.ActionEnded(0xfefe);
        await Program.Send("vaal-admission-test", wire.Stream, wire.Ciphers, marker.ToArray(), null);
        Assert.Equal(marker, await wire.Through(marker));
        Assert.Equal(20, f.Conn.ActionCount); Assert.Equal((ushort)47, f.Conn.ChannelSkill);
        Assert.Equal(destination, f.Conn.Walk.Destination); Assert.Equal(arrivesAt, f.Conn.Walk.ArrivesAt);
        Assert.Empty(f.Conn.SingleUseCooldowns); Assert.Empty(f.Conn.SkillReadyAt);
        Assert.Equal(0, f.Conn.NextAttackAt);
        Assert.Equal(mana, f.Conn.PlayerMana); Assert.Equal(life, f.Conn.PlayerLife);
        Assert.Equal(1u, target.Life); Assert.Equal(budget, f.Budget);
    }
}
