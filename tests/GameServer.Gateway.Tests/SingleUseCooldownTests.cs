using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SingleUseCooldownTests
{
    static string Data() => BenchCraftingTests.Data();
    static readonly Lazy<SkillCatalog> Skills = new(() => new(Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new(Data()));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static Dictionary<uint, int> Own(string name, int quality = 0) => Skills.Value
        .EffectStats(Skills.Value.EffectRow(name), 1, quality).ToDictionary(s => s.Item1, s => s.Item2);
    static SingleUseCooldown? Plan(string name, Dictionary<uint, int>? player = null,
        Dictionary<uint, int>? own = null) => SingleUseCooldown.ForSkill(Skills.Value, Stats.Value,
            Skills.Value.EffectRow(name), 1, player ?? [], own ?? Own(name));
    static ClientState Connection() => new(new AreaLayout(1, "cooldown-test", 500, 500, 10, 10, null, []), 1, 1)
        { PlayerLife = 100, PlayerMana = 100 };

    [Theory]
    [InlineData(0, 2.5)]
    public void Dash_retains_native_base_and_consumes_base_time_at_the_final_rate(int recovery, double seconds)
    {
        var plan = Plan("Dash", Values(("virtual_cooldown_speed_+%", recovery)))!.Value;
        Assert.Equal(2500, plan.BaseMilliseconds);
        Assert.Equal(recovery, plan.RecoveryPercent);
        var own = Own("Dash");
        own[Stats.Value.Key("virtual_cooldown_speed_+%")!.Value] = plan.RecoveryPercent;
        var native = Skills.Value.FindByRow(0x103, Skills.Value.EffectRow("Dash"), 1,
            own.Select(v => (v.Key, v.Value)).ToArray())!;
        Assert.Equal(2500u, native.CooldownMs); // Native receives base, never 1250 at +100%.
        var state = new SingleUseCooldownState(0, plan.RecoveryRate);
        Assert.True(state.TryUse(0, plan));
        Assert.Equal(seconds, state.RemainingSeconds, 6);
        Assert.False(state.TryUse(seconds - 0.01, plan));
        Assert.True(state.TryUse(seconds, plan));
    }

}
