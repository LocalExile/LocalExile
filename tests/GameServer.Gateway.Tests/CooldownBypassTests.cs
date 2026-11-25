using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// GEPL CooldownBypassType names the charge a cooling skill spends (the client's 0x141ea4290): 1 Endurance, 2 Frenzy,
// 3 Power, 0 and 4 none; the power override (Flicker Strike of Power) wins over the type.
public sealed class CooldownBypassTests
{
    [Fact]
    public void Vigilant_Strike_spends_its_discounted_cost_and_bypass_charge_together()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skillId = Assert.Single(fixture.Equip(("Vigilant Strike", 20)));
        var gloves = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Wyrmsign", new Random(1)));
        Game.ItemsOf(player).Add(PlayerInventories.Gloves, 0, 0, gloves);
        var charge = Game.ChargesOf(player);
        var limits = charge.Limits(ChargeType.Endurance);
        var skill = Game.SupportedSkill(player, skillId);
        var plan = Game.CooldownForSkill(player, skillId);
        Assert.NotNull(plan);

        Assert.True(Game.PayMana(player, skillId, out uint normalCost, out _));
        charge.State.Set(ChargeType.Endurance, limits.Minimum + 1, Game.AreaTime(player), limits);
        Assert.True(Game.PayMana(player, skillId, out uint discountedCost, out _));
        Assert.True(discountedCost < normalCost);

        int acceptedActions = 0;
        bool Admit() => Game.TryBeginSkillCooldownUse(player, skillId, skill.Effect, plan,
            false, () => acceptedActions++, out _);

        Assert.True(Admit());
        var state = Game.SingleUseState(player, skill.Effect, skillId)!;
        Assert.Equal(1, state.SpentUses);
        player.PlayerMana = discountedCost - 1;
        Assert.False(Admit());
        Assert.Equal(discountedCost - 1, player.PlayerMana);
        Assert.Equal(limits.Minimum + 1, charge.Effective(ChargeType.Endurance));
        Assert.Equal(1, acceptedActions);

        player.PlayerMana = discountedCost;
        Assert.True(Admit());
        Assert.Equal(0u, player.PlayerMana);
        Assert.Equal(limits.Minimum, charge.Effective(ChargeType.Endurance));
        Assert.Equal(2, acceptedActions);
        Assert.Equal(1, state.SpentUses);
    }

    [Fact]
    public void Bypass_types_name_their_charge()
    {
        Assert.Equal(ChargeType.Endurance, CooldownBypass.Charge(1, false));
        Assert.Equal(ChargeType.Frenzy, CooldownBypass.Charge(2, false));
        Assert.Equal(ChargeType.Power, CooldownBypass.Charge(3, false));
        Assert.Null(CooldownBypass.Charge(4, false));
        Assert.Null(CooldownBypass.Charge(0, false));
        Assert.Equal(ChargeType.Power, CooldownBypass.Charge(2, true));
        // The Crucible override reaches Flicker Strike and Vigilant Strike (transfigured too) when they bypass at all.
        Assert.True(CooldownBypass.FlickerVigilantOverride("FlickerStrike", 2));
        Assert.True(CooldownBypass.FlickerVigilantOverride("FlickerStrikeAltX", 2));
        Assert.True(CooldownBypass.FlickerVigilantOverride("VigilantStrike", 1));
        Assert.False(CooldownBypass.FlickerVigilantOverride("VaalFlickerStrike", 4));
        Assert.False(CooldownBypass.FlickerVigilantOverride("FlickerStrikeProphecy", 4));
        Assert.False(CooldownBypass.FlickerVigilantOverride("VigilantStrikeMercenary", 1));
        Assert.False(CooldownBypass.FlickerVigilantOverride("ColdSnap", 2));
    }
}
