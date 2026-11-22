using GameServer.Core.Projectiles;

/// <summary>The unobstructed outward spiral of the player Ball Lightning action, behaviour variation 1.</summary>
sealed class OrbitingBallLightningLegs(double speed) : IProjectileLegs
{
    const int WaypointCount = 216;
    const float MaximumRadius = 48;
    const float Sweep = 26.703516f;
    ProjectileVector centre;
    float startAngle;
    int waypoint;

    public double Lifetime => GatewayProjectileSkillProfiles.BallLightningLifetimeSeconds;
    public bool EndsAfterLegs => true;

    public static float SpeedVariation(ushort sequence, uint emitter, uint repeatsLeft, Func<string, int> get)
    {
        // The detached first projectile has derived byte 2. Both draws occur once, including a zero-width range.
        var random = new AnimationVariantSelector.NativeRandom([sequence, 2, emitter, repeatsLeft, 0, 0, 0, 0]);
        float offHand = get("active_skill_projectile_speed_+%_variation_final_from_off_hand") / 200f;
        float main = get("active_skill_projectile_speed_+%_variation_final") / 200f;
        return (1 + random.Range(-offHand, offHand)) * (1 + random.Range(-main, main));
    }

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        centre = Cell(from);
        var delta = Cell(aim) - centre;
        startAngle = (MathF.Atan2((float)delta.Y, (float)delta.X) + MathF.PI / 2) % MathF.Tau;
        if (startAngle < 0) startAngle += MathF.Tau;
        return Next(from, false);
    }

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        // 3.29.3c BallLightning action 0x141a38fd0. Both offsets truncate toward zero before adding the centre.
        var previous = Cell(from);
        ProjectileVector target;
        do
        {
            float progress = ++waypoint / (float)WaypointCount;
            float radius = MaximumRadius * progress;
            float angle = startAngle + progress * Sweep;
            target = centre + new ProjectileVector((int)(MathF.Sin(angle) * radius), (int)(MathF.Cos(angle) * radius));
        } while (target == previous && waypoint < WaypointCount);
        return new(target, speed);
    }

    public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked) =>
        blocked || waypoint == WaypointCount ? null : new ProjectileLegTurn(Next(from, false));

    static ProjectileVector Cell(ProjectileVector point) =>
        new(Math.Floor(point.X + .5), Math.Floor(point.Y + .5));
}
