using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

// Bino's Kitchen Knife (Modules/Core/Game/Combat/PoisonSpreadOnKill.cs): its definition has the spread mod, and a kill of
// a poisoned enemy with it worn spreads the strongest stack within 3 metres and starts the wielder's regeneration.
public sealed class BinosKitchenKnifeTests
{
    [Fact]
    public void Spread_preserves_the_lethal_poison_and_does_not_apply_faster_damage_twice()
    {
        var global = new Dictionary<uint, int>
        {
            [Stat.PoisonDurationPlusPct] = 50, [Stat.BaseAllAilmentDurationPlusPct] = 25,
            [Stat.FasterPoisonPct] = 100, [Stat.DamagingAilmentsDealDamagePlusPctFaster] = 50,
        };
        Assert.Equal(3.5, Game.SpreadPoisonSeconds(global.GetValueOrDefault));
        var poison = new MonsterPoisons();
        poison.Add(0, 200, .2);
        poison.Add(0, 10, 4);
        // The stronger stack delivers the fatal damage before expiring inside this tick. The kill callback may
        // run after the tick or a network await; the surviving weak stack must not replace the lethal snapshot.
        Assert.Equal(47, poison.Advance(.75, 1, lifeAtStart: 30));
        poison.ClearAtDeath(.75, poisonDeath: true);
        Assert.True(poison.DiedPoisoned);
        Assert.Equal(200, poison.Strongest(10)!.Value.DamagePerSecond);
        poison.Clear();
        Assert.Null(poison.Strongest(10));
        poison.Add(10, 50, 1);
        poison.RememberDeath(10);
        poison.Add(11, 25, 1);
        Assert.Equal(25, poison.Strongest(11)!.Value.DamagePerSecond);
        poison.RememberDeath(15);
        Assert.Null(poison.Strongest(15));
        var expired = new MonsterPoisons();
        expired.Add(0, 200, .2);
        expired.Advance(.7, 1);
        expired.Clear();
        Assert.Null(expired.Strongest(.7));

        var regen = new SpreadPoisonRegeneration();
        ushort next = 1;
        regen.Grant(0, 2, trackRecovery: true);
        Assert.NotNull(regen.Present(0, true, () => next++));
        regen.Grant(.5, 2, trackRecovery: true);
        Assert.Null(regen.Present(.5, true, () => next++));
        // A small extension is republished at the previous visible deadline instead of allowing an early icon expiry.
        Assert.NotNull(regen.Present(2, true, () => next++));
        Assert.Equal(1000, regen.Recover(3));
        Assert.Equal(0, regen.Recover(3));
        regen.Grant(4, 2, trackRecovery: true);
        Assert.Equal(400, regen.Recover(5));
        Assert.NotNull(regen.Present(5, false, () => next++));
        Assert.Equal(0, regen.Recover(6));
    }

}
