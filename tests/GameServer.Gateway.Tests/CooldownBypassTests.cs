namespace GameServer.Gateway.Tests;

// GEPL CooldownBypassType names the charge a cooling skill spends (the client's 0x141ea4290): 1 Endurance, 2 Frenzy,
// 3 Power, 0 and 4 none; the power override (Flicker Strike of Power) wins over the type.
public sealed class CooldownBypassTests
{
    [Fact]
    public void Bypass_types_name_their_charge()
    {
        Assert.Equal(ChargeType.Endurance, CooldownBypass.Charge(1, false));
        Assert.Equal(ChargeType.Frenzy, CooldownBypass.Charge(2, false));
        Assert.Equal(ChargeType.Power, CooldownBypass.Charge(3, false));
        Assert.Null(CooldownBypass.Charge(4, false));
        Assert.Null(CooldownBypass.Charge(0, false));
        Assert.Equal(ChargeType.Power, CooldownBypass.Charge(2, true));
    }
}
