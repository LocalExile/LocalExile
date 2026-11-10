using GameServer.Core.Projectiles;


/// <summary>Basic straight-flight behavior, independent of any concrete skill provider.</summary>
public static class GatewayProjectileDefaults
{
    public static ProjectileProfile Resolve(string metadata, Func<string, int> get, double speed, bool attack,
        bool arrows = false) =>
        GatewayProjectileModifiers.Resolve(Create(metadata, get, speed, arrows), get, attack, arrows);

    /// <summary>Unresolved common baseline. Attach intrinsic stages before resolving modifiers once.</summary>
    public static ProjectileProfile Create(string metadata, Func<string, int> get, double speed, bool arrows = false)
    {
        int baseCount = get("base_number_of_projectiles") + (arrows ? get("base_number_of_arrows") : 0);
        return new(metadata)
        {
            Speed = speed,
            // One movement instance: the client's 15 m cap, tunable through the game-data setting.
            Range = SystemSettings.Default.ProjectileMaximumRangeCells,
            Count = baseCount > 0 ? baseCount : 1,
            AimSpreadDistance = 40,
            SkipDeniedContacts = true,
            SkipDeniedReturnContacts = true,
            ExcludeTurnaroundTarget = true,
            ReturnOvershoot = 150
        };
    }
}
