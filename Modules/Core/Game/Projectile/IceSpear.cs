using GameServer.Core.Projectiles;

public static partial class Game
{
    internal static bool IsIceSpear(string? effect) => effect is "IceSpear" or "IceSpearAltX" or "IceSpearAltY" or "IceSpearRoyale";

    internal static ProjectileProfile IceSpearForms(string effect, ProjectileProfile profile, Func<uint, int> get)
    {
        double secondSpeed = Math.Max(.01, 1 + get(Stat.IceSpearSecondFormProjectileSpeedPlusPctFinal) / 100d);
        if (effect == "IceSpearAltX")
            return profile with { SplitForm = new(secondSpeed, Pierce: -1) };

        // The native action compares total travelled distance against its 20.199-cell base (0x141a0a6a0).
        double distance = 20.199 * Math.Max(0, 1 + get(Stat.IceSpearDistanceBeforeFormChangePlusPct) / 100d);
        return profile with { FormChange = new(distance, secondSpeed, Pierce: -1) };
    }

    static ProjectileUse IceSpearRelease(ProjectileReleaseContext context, ProjectileUse use)
    {
        var profile = use.Profile;
        if (profile.FormChange is not null && profile.Count > 1
            && SpellTiming(context.Player, context.Gem.Effect, context.PlayerStat, context.Own, context.Get)
                is { DurationSeconds: double duration, BaseMilliseconds: > 0 } timing)
        {
            // The first spear releases with the cast, then each extra spear after its share of the cast-speed-scaled window.
            double interval = .35 / profile.Count * duration / (timing.BaseMilliseconds / 1000d);
            profile = profile with { SequentialDelay = Math.Max(1, ProjectileRuntime.Time(interval)) };
        }
        var shot = (ProjectileOffense)use.Payload!;
        int increase = context.Get(Stat.IceSpearSecondFormCriticalStrikeChancePlusPct);
        int multiplier = context.Get(Stat.IceSpearSecondFormCriticalStrikeMultiplierPlus);
        if (increase != 0)
            shot = shot with
            {
                SecondFormCriticalChance = SupportedCriticalChanceFor(context.Player, context.SkillId, release: true, increase),
                SecondFormOptions = new(SupportedCriticalChanceVsBlinded(context.Player, context.SkillId,
                        release: true, weaponCritical: null, extraIncrease: increase),
                    SupportedCriticalChanceVsGround(context.Player, context.SkillId,
                        release: true, weaponCritical: null, extraIncrease: increase), multiplier),
            };
        else if (multiplier != 0) shot = shot with { SecondFormOptions = new(null, null, multiplier) };
        return use with { Profile = profile, Payload = shot };
    }
}
