

/// <summary>The ordinary ranged attack's projectile from the active weapon set. These are exact Projectiles.dat ids,
/// not gem effects: the native default attack still uses the character's first skill and weapon stance.</summary>
static class OrdinaryRangedAttack
{
    public const string Arrow = "Metadata/Projectiles/DefaultArrow";
    public const string Wand = "Metadata/Projectiles/WandProjectile";

    // WandProjectile's own rig has impact at index 0 (once, 500 ms), followed by projectile_01 at index 1.
    // Playing that clip triggers the AO's WandTrailImpact sound and wand_impact particles; this is not an AO path.
    public const uint WandImpactAnimation = 0;
    public const double WandImpactSeconds = 0.5;
    /// <summary>How far in front of the body a shot leaves when the rig gives no release point: 1.5 cells, as the
    /// fireball's <c>Game_PROJECTILE_AHEAD</c> (11-skills.md §10).</summary>
    public const double ReleaseAheadCells = 1.5;

    // DefaultArrow has no impact clip; the stopped arrow object stays this long where it hit (a feel value, not measured).
    public const double ArrowStuckSeconds = 1.0;

    public static string? ProjectileId(WeaponProfile weapon) => weapon.Bow ? Arrow : weapon.Wand ? Wand : null;
}
