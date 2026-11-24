using GameServer.Core.Projectiles;


/// <summary>Binds resolved stats to source-neutral projectile profiles and their child stages.</summary>
public static class GatewayProjectileModifiers
{
    public static ProjectileProfile Resolve(ProjectileProfile profile, Func<uint, int> get, bool attack,
        bool arrows = false, bool chainInputConsumed = false, bool intrinsicSplitCount = false,
        int skillExtraProjectiles = 0, int skillPierce = 0)
    {
        var p = profile;
        int splitTargets = Math.Max(get(Stat.ProjectileNumberToSplit), get(Stat.Of("projectile_number_of_targets_to_split_to")));
        int extra = 0;
        if (get(Stat.ModifiersToProjectileCountDoNotApply) == 0)
        {
            extra = skillExtraProjectiles + get(Stat.NumberOfAdditionalProjectiles)
                + get(attack ? Stat.AttacksNumberOfAdditionalProjectiles : Stat.SpellsNumberOfAdditionalProjectiles)
                + (attack && WeaponIdentity.WieldingClawOrDagger(get)
                    ? get(Stat.AttackSkillAdditionalNumProjectilesWhileWieldingClawsDaggers) : 0)
                + (arrows ? AdditionalArrows(get) : 0);
        }
        int pierce = skillPierce + Math.Max(get(Stat.ProjectileNumberOfTargetsToPierce), get(Stat.ProjectileBaseNumberOfTargetsToPierce));
        if (attack) pierce += Math.Max(get(Stat.ArrowNumberOfTargetsToPierce), get(Stat.ArrowBaseNumberOfTargetsToPierce));
        if (get(Stat.AlwaysPierce) != 0 || get(Stat.VirtualAlwaysPierce) != 0
            || attack && (get(Stat.ArrowsAlwaysPierce) != 0 || get(Stat.BaseArrowsAlwaysPierce) != 0)) pierce = -1;
        int chains = Math.Max(get(Stat.NumberOfChains), get(Stat.VirtualNumberOfChainsForProjectiles));
        // "Supported Skills Chain X% more times" (Greater Chain): a more multiplier on the whole count. PoB keeps the
        // resulting fraction instead of rounding up, so a single chain no longer gains one from a fractional result.
        int chainMore = get(Stat.SupportChainCountPlusPctFinal);
        if (chains > 0 && chainMore > 0) chains = (int)(chains * (1 + chainMore / 100.0));
        if (chainInputConsumed) chains = 0;
        bool forks = get(Stat.ProjectilesFork) != 0 || get(Stat.VirtualProjectilesFork) != 0
            || (attack ? get(Stat.AttackProjectilesFork) != 0 || get(Stat.ArrowsFork) != 0
                : get(Stat.ProjectilesFromSpellsFork) != 0);
        int forkCount = Math.Max(get(Stat.VirtualNumberOfForksForProjectilesFinal),
            (forks ? 1 : 0) + get(Stat.NumberOfAdditionalForksBase)
                + (attack ? get(Stat.AttackProjectilesForkAdditionalTimes) : 0));
        bool forksApplyToZigZags = get(Stat.KineticBoltForksApplyToZigZags) != 0;
        int theoreticalForks = Math.Max(get(Stat.TheoreticalNumberOfForks), forkCount);
        int zigZags = Math.Max(0, get(Stat.KineticWandBaseNumberOfZigZags) + (forksApplyToZigZags ? theoreticalForks : 0));
        var modifiers = new ProjectileModifiers
        {
            ExtraProjectiles = extra, Pierce = pierce,
            Fork = forkCount, Chain = chains,
            ForkExtraProjectileChance = Math.Clamp(get(Stat.ChanceToForkExtraProjectilePct), 0, 100),
            TerrainChainChance = Math.Clamp(get(Stat.ProjectileChainFromTerrainChancePct), 0, 100),
            Split = Math.Max(splitTargets, get(Stat.IntermediaryProjectileNumberOfTargetsToSplitTo)),
            Returns = get(Stat.ProjectilesReturn) != 0 || attack && get(Stat.AttackProjectilesReturn) != 0
                || get(Stat.ProjectileReturnPctChance) >= 100,
            CannotPierce = get(Stat.CannotPierce) != 0 || get(Stat.VirtualProjectilesCannotPierce) != 0
                || !attack && get(Stat.ProjectilesFromSpellsCannotPierce) != 0,
            CannotFork = forksApplyToZigZags || get(Stat.VirtualProjectilesCannotFork) != 0
                || !attack && get(Stat.ProjectilesFromSpellsCannotFork) != 0,
            CannotChain = get(Stat.VirtualProjectilesCannotChain) != 0 || !attack && get(Stat.ProjectilesFromSpellsCannotChain) != 0,
            CannotSplit = get(Stat.ProjectilesCannotSplit) != 0,
            CannotReturn = get(Stat.SkillProjectilesCannotReturn) != 0
        };
        // A declared non-primary consumer keeps quantity when the competing split is prohibited.
        bool intrinsicCountOwnsQuantity = p.CountConsumer != ProjectileCountConsumer.Primary
            && (p.CannotSplit || modifiers.CannotSplit);
        // An intrinsic split budget owns this conversion even when the generic stat also enables it.
        if (!intrinsicSplitCount && !intrinsicCountOwnsQuantity
            && get(Stat.ModifiersToNumberOfProjectilesInsteadApplyToSplitting) != 0)
        {
            if (p.CountConsumer != ProjectileCountConsumer.Primary)
                throw new InvalidOperationException("Unresolved competing projectile count conversions: " + p.Id);
            p = p with { Count = 1, Split = p.Split + Math.Max(0, extra) };
            modifiers = modifiers with { ExtraProjectiles = 0 };
        }
        if (intrinsicSplitCount) { p = p with { Split = p.Split + extra }; modifiers = modifiers with { ExtraProjectiles = 0, Split = 0 }; }
        if (get(Stat.ProjectileDistanceOverride) is > 0 and var distance)
        {
            if (p.EndEmission is not null || p.Lobbed)
                throw new InvalidOperationException("Distance override needs an explicit early-end recipe for " + p.Id);
            p = p with { Range = distance };
        }
        // "Projectiles from Supported Skills have X metres maximum Direct Flight": the values are client units (10 per
        // metre), so 48 is 4.8 m. It caps rather than replaces either the baseline or a distance override.
        if (get(Stat.ProjectileMaximumRangeOverride) is > 0 and var maximum)
            p = p with { Range = Math.Min(p.Range, maximum) };
        ProjectileProfile ReturnControls(ProjectileProfile target) => target with
        {
            ReturnChancePercent = Math.Max(target.ReturnChancePercent, Math.Clamp(get(Stat.ProjectileReturnPctChance), 0, 100)),
            PierceAllOnReturn = target.PierceAllOnReturn || get(Stat.ReturningProjectilesAlwaysPierce) != 0,
            ReturnSpeedMultiplier = target.ReturnSpeedMultiplier * Math.Max(.01, 1 + get(Stat.ReturnedProjectileSpeedPlusPct) / 100d)
        };
        p = ReturnControls(ProjectileParameters.Resolve(p with { ZigZags = Math.Max(p.ZigZags, zigZags) }, modifiers));
        // Children receive already resolved continuation grants, never another count expansion.
        if (p.EndEmission is { } endEmission)
            p = p with { EndEmission = endEmission with { Child = ReturnControls(ProjectileParameters.Resolve(endEmission.Child,
                modifiers with { ExtraProjectiles = 0 })) } };
        if (p.TravelEmission is { } travelEmission)
            p = p with { TravelEmission = travelEmission with { Child = ReturnControls(ProjectileParameters.Resolve(travelEmission.Child,
                modifiers with { ExtraProjectiles = 0 })) } };
        if (get(Stat.ProjectilesCanShotgun) != 0)
            p = p with { HitRule = new(ProjectileHitScope.Projectile) };
        return p;
    }

    // Native arrow alias 29cb: the generic additions above plus number_of_additional_arrows and the
    // main-hand-accuracy condition. Dash-recently folds onto that plain arrow stat at its accepted-use owner.
    // A profile passes arrows = true only for a bow attack.
    static int AdditionalArrows(Func<uint, int> get)
    {
        int count = get(Stat.NumberOfAdditionalArrows);
        if (get(Stat.MainHandAccuracyRating) >= 3000)
            count += get(Stat.NumberOfAdditionalArrowsWhileMainHandAccuracyIs3000OrMore);
        return count;
    }
}
