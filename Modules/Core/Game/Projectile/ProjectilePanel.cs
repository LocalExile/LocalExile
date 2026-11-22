using GameServer.Core.Projectiles;

public static partial class Game
{
    // These are native description inputs, projected only onto the outgoing skill record.
    // Never put final counts back into supported/raw stats and resolve quantity modifiers twice.
    public static void ApplyProjectilePanelStats(int effect, Dictionary<uint, int> stats)
    {
        if (ResolveProjectilePanelParameters(effect) is not { } parameters) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        ProjectileProfile profile;
        bool arrows = ArrowsForSkill(effect);
        try { profile = parameters.Resolve(Get, 1, arrows); }
        catch (InvalidOperationException ex)
        {
            // An unavailable recipe must not prevent the rest of the skill list from reaching the client.
            Console.WriteLine($"[gateway] {Skills.EffectId(effect)} projectile panel unavailable: {ex.Message}");
            return;
        }
        // Arrow Nova's record describes the volley that leaves the landing cell, not its single payload arrow: the
        // client builds that volley from total_number_of_projectiles_to_fire, and 1 left it with one arrow
        // (live 2026-11-04: one SplitArrowDefault child instead of the nine the stats resolve to).
        int total = Get(Stat.ProjectilesRain) != 0 && profile.EndEmission is { } rain ? rain.Child.Count : profile.Count;
        Put(Stat.TotalNumberOfProjectilesToFire, total);
        Put(Stat.VirtualTotalNumberOfProjectiles, total);
        if (arrows)
        {
            Put(Stat.TotalNumberOfArrowsToFire, total);
            Put(Stat.VirtualTotalNumberOfArrows, total);
        }
        Put(Stat.ProjectileNumberOfTargetsToPierce, profile.CannotPierce || profile.Pierce < 0 ? 0 : profile.Pierce);
        Put(Stat.VirtualAlwaysPierce, !profile.CannotPierce && profile.Pierce < 0 ? 1 : 0);
        Put(Stat.VirtualProjectilesCannotPierce, profile.CannotPierce ? 1 : 0);
        // The native generic pierce rows require both competing arrow aliases to be zero.
        // Their applicable contributions are already included in the profile's combined budget.
        Put(Stat.ArrowNumberOfTargetsToPierce, 0);
        Put(Stat.ArrowsAlwaysPierce, 0);
        // Ground bounces are the skill's chains too (Rolling Magma's display_projectiles_chain_when_impacting_ground): the
        // client draws its own orb's bounces from this count, and with 0 it stopped at the first landing (live 2026-11-01).
        Put(Stat.VirtualNumberOfChainsForProjectiles, profile.CannotChain ? 0 : profile.Chain + profile.GroundChains);
        Put(Stat.VirtualNumberOfForksForProjectilesFinal, profile.CannotFork ? 0 : profile.Fork);
        Put(Stat.VirtualProjectilesCannotChain, profile.CannotChain ? 1 : 0);
        Put(Stat.VirtualProjectilesCannotFork, profile.CannotFork ? 1 : 0);
        if (Get(Stat.KineticBoltForksApplyToZigZags) != 0 || profile.ZigZags > 0)
        {
            Put(Stat.KineticBoltNumberOfZigZags, profile.ZigZags);
            if (Get(Stat.KineticBoltForksApplyToZigZags) != 0)
                Put(Stat.TheoreticalNumberOfForks, Math.Max(Get(Stat.TheoreticalNumberOfForks), profile.Fork));
        }
        Put(Stat.VirtualProjectileNumberToSplit, profile.CannotSplit ? 0 : profile.Split);
        Put(Stat.ProjectilesCannotSplit, profile.CannotSplit ? 1 : 0);
        Put(Stat.ProjectilesReturn, !profile.CannotReturn && profile.Returns ? 1 : 0);
        Put(Stat.ProjectileReturnPctChance, profile.CannotReturn ? 0 : profile.ReturnChancePercent);
        Put(Stat.SkillProjectilesCannotReturn, profile.CannotReturn ? 1 : 0);
        Put(Stat.ReturningProjectilesAlwaysPierce, !profile.CannotReturn && !profile.CannotPierce
            && (profile.Returns || profile.ReturnChancePercent > 0) && profile.PierceAllOnReturn ? 1 : 0);
        if (profile.NativeVolley?.Nova == true) Put(Stat.ProjectilesNova, 1);
        parameters.PanelStats?.Invoke(profile, stats);

        void Put(uint name, int value) { if (name is var key) stats[key] = value; }
    }
}
