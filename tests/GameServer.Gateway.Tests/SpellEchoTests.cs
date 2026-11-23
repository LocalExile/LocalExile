using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class SpellEchoTests
{
    static int Speed(int increased, bool fishingRod, params (string Name, int Value)[] stats) =>
        Game.FinalCastSpeedPct(name => stats.FirstOrDefault(s => Stat.Of(s.Name) == name).Value, () => fishingRod, increased);

    // The client's cast_speed_+% (0x1427101c0) puts its combined "more" final (0x14270ed40) on the summed increases.
    [Fact]
    public void More_cast_speed_multiplies_as_the_clients_combined_final()
    {
        const string echo = "support_multicast_cast_speed_+%_final", fanaticism = "fanaticism_hand_cast_speed_+%_final",
            reels = "cast_speed_+%_final_while_holding_fishing_rod";
        Assert.Equal(40, Speed(0, false, (echo, 40)));
        Assert.Equal(82, Speed(30, false, (echo, 40)));
        Assert.Equal(12, Speed(0, false, (echo, 40), ("active_skill_cast_speed_+%_final", -20)));
        Assert.Equal(20, Speed(0, false, ("casting_spell", 1), (fanaticism, 20)));
        Assert.Equal(0, Speed(0, false, ("casting_spell", 1), ("is_not_hand_cast", 1), (fanaticism, 20)));
        Assert.Equal(0, Speed(0, false, (reels, 50)));
        Assert.Equal(50, Speed(0, true, (reels, 50)));
        Assert.Equal(-99, Speed(-50, false, ("active_skill_cast_speed_+%_final", -99)));
        Assert.Equal(-100, Speed(-100, false));
    }

    // Spell Echo (base_spell_repeat_count 1, 40% more cast speed at level 1) repeats Fireball once;
    // ordinary Ice Nova likewise releases one ring on each cast.

}
