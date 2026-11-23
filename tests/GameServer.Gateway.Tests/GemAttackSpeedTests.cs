using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class GemAttackSpeedTests
{
    // Tornado Shot's GEPL AttackSpeedMultiplier is -20; the supported attack pace must apply it, and the character's
    // increased attack speed must still scale that pace. A Crude Bow's base attack time is 714 ms.

    // Projectile speed is the Projectiles.dat row speed plus the player's virtual projectile_speed_+% (which already
    // includes base_projectile_speed_+%) and the skill's raw base input. Tornado Shot's row is 2173 world units/s.
    [Fact]
    public void Projectile_speed_applies_player_and_skill_increases()
    {
        const double baseline = 2173 / (250.0 / 23.0);
        Assert.Equal(baseline, ProjectileSpeed.CellsPerSecond(2173, _ => 0, _ => 0), 4);
        Assert.Equal(baseline * 1.2, ProjectileSpeed.CellsPerSecond(2173,
            name => name == Stat.ProjectileSpeedPlusPct ? 20 : 0, _ => 0), 4);
        Assert.Equal(baseline * 1.1, ProjectileSpeed.CellsPerSecond(2173, _ => 0,
            name => name == Stat.BaseProjectileSpeedPlusPct ? 10 : 0), 4);
    }
}
