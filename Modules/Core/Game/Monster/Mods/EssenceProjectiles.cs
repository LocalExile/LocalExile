using GameServer.Core;

/// <summary>Essence daemons whose spells fire a projectile the client tables give no MonsterProjectile row: Torment's orb
/// (Ball Lightning) and Hysteria's flame golem (Fireball). Registered as general monster-projectile overrides at startup so
/// the generic monster-projectile resolver (<c>BossKits</c>) draws them; the skill/boss classes stay unaware of essences.</summary>
public static partial class Game
{
    public static void LoadEssenceProjectiles()
    {
        // EssenceDaemonBallLightning: a slow ball (60 units/s) that ticks a lightning area as it drifts.
        SkillCatalog.MonsterProjectileOverrides[2500] =
            new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BallLightning", 60, Lobbed: false, 1)
                { AnimationKey = "SpellProjectile" };
        // EssenceFireballBurst: an ordinary fireball (1040 units/s) that explodes on impact.
        SkillCatalog.MonsterProjectileOverrides[31] =
            new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/Fireball", 1040, Lobbed: false, 1)
                { AnimationKey = "SpellProjectile" };
    }
}