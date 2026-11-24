using GameServer.Core.Projectiles;

static partial class Game
{
    static readonly IReadOnlySet<ProjectileEndReason> SurgingEndReasons = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Enemy };

    static bool IsGalvanicArrow(string? effect) => effect is "GalvanicArrow" or "GalvanicArrowAltX" or "GalvanicArrowAltY";

    static ProjectileUse GalvanicArrowRelease(ProjectileReleaseContext context, ProjectileUse use,
        string effect, Dictionary<uint, int> areaStats)
    {
        bool energy = effect == "GalvanicArrowAltX", surging = effect == "GalvanicArrowAltY";
        int speedBonus = context.Get(Stat.GalvanicArrowProjectileSpeedPlusPct);
        int ordinarySpeed = context.PlayerStat(Stat.ProjectileSpeedPlusPct) + context.Own(Stat.BaseProjectileSpeedPlusPct);
        int Area(uint key) => areaStats.GetValueOrDefault(key);
        int radius = SkillArea.Radius(SkillArea.BaseRadius(effect, Area), Area, spell: false);
        var cone = new ProjectileAreaRecipe("galvanic cone", radius)
        {
            ConeHalfAngle = Math.PI / 4,
            ExcludeContactTarget = surging,
            ReplacesDirectContact = false,
        };
        double flightFactor = Math.Max(.01, 1 + (ordinarySpeed + speedBonus) / 100d)
            * Math.Max(.01, 1 + context.Own(Stat.SupportSlowerProjectilesProjectileSpeedPlusPctFinal) / 100d)
            * Math.Max(.01, 1 + context.Get(Stat.ActiveSkillProjectileSpeedPlusPctFinal) / 100d);
        double speedFactor = Math.Max(1d, 100 + ordinarySpeed + speedBonus) / Math.Max(1d, 100 + ordinarySpeed);
        var shot = (ProjectileOffense)use.Payload!;
        return use with
        {
            Profile = use.Profile with
            {
                Count = energy ? 0 : use.Profile.Count,
                Speed = use.Profile.Speed * speedFactor,
                Range = Math.Min(use.Profile.Range, Math.Max(1, 28 * flightFactor)),
                LaunchArea = surging ? null : cone,
                EndArea = surging ? cone : null,
                EndAreaReasons = surging ? SurgingEndReasons : null,
            },
            Payload = shot with
            {
                AreaVisual = Art.AnimatedInPlaceOf("Metadata/Effects/Spells/shrapnel_shot/shrapnelshot_aoe.ao", context.Variation),
            },
        };
    }
}
