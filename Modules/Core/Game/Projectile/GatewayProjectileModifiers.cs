using GameServer.Core.Projectiles;


/// <summary>Binds resolved stats to source-neutral projectile profiles and their child stages.</summary>
public static class GatewayProjectileModifiers
{
    public static ProjectileProfile Resolve(ProjectileProfile profile, Func<string, int> get, bool attack,
        bool arrows = false, bool chainInputConsumed = false, bool intrinsicSplitCount = false,
        int skillExtraProjectiles = 0, int skillPierce = 0)
    {
        var p = profile;
        int splitTargets = Math.Max(get("projectile_number_to_split"), get("projectile_number_of_targets_to_split_to"));
        int extra = skillExtraProjectiles + get("number_of_additional_projectiles")
            + get(attack ? "attacks_number_of_additional_projectiles" : "spells_number_of_additional_projectiles")
            + (attack && WeaponIdentity.WieldingClawOrDagger(get)
                ? get("attack_skill_additional_num_projectiles_while_wielding_claws_daggers") : 0)
            + (arrows ? AdditionalArrows(get) : 0);
        int pierce = skillPierce + Math.Max(get("projectile_number_of_targets_to_pierce"), get("projectile_base_number_of_targets_to_pierce"));
        if (attack) pierce += Math.Max(get("arrow_number_of_targets_to_pierce"), get("arrow_base_number_of_targets_to_pierce"));
        if (get("always_pierce") != 0 || get("virtual_always_pierce") != 0
            || attack && (get("arrows_always_pierce") != 0 || get("base_arrows_always_pierce") != 0)) pierce = -1;
        int chains = Math.Max(get("number_of_chains"), get("virtual_number_of_chains_for_projectiles"));
        // "Supported Skills Chain X% more times" (Greater Chain): a more multiplier on the whole count. PoB keeps the
        // resulting fraction instead of rounding up, so a single chain no longer gains one from a fractional result.
        int chainMore = get("support_chain_count_+%_final");
        if (chains > 0 && chainMore > 0) chains = (int)(chains * (1 + chainMore / 100.0));
        if (chainInputConsumed) chains = 0;
        bool forks = get("projectiles_fork") != 0 || get("virtual_projectiles_fork") != 0
            || (attack ? get("attack_projectiles_fork") != 0 || get("arrows_fork") != 0
                : get("projectiles_from_spells_fork") != 0);
        int forkCount = Math.Max(get("virtual_number_of_forks_for_projectiles_final"),
            (forks ? 1 : 0) + get("number_of_additional_forks_base")
                + (attack ? get("attack_projectiles_fork_additional_times") : 0));
        bool forksApplyToZigZags = get("kinetic_bolt_forks_apply_to_zig_zags") != 0;
        int theoreticalForks = Math.Max(get("theoretical_number_of_forks"), forkCount);
        int zigZags = Math.Max(0, get("kinetic_wand_base_number_of_zig_zags") + (forksApplyToZigZags ? theoreticalForks : 0));
        bool fixedCount = get("modifiers_to_projectile_count_do_not_apply") != 0;
        var scaling = new CountScaling(get);
        var modifiers = new ProjectileModifiers
        {
            ExtraProjectiles = fixedCount ? 0 : extra, Pierce = pierce,
            Fork = forkCount, Chain = chains,
            ForkExtraProjectileChance = Math.Clamp(get("chance_to_fork_extra_projectile_%"), 0, 100),
            TerrainChainChance = Math.Clamp(get("projectile_chain_from_terrain_chance_%"), 0, 100),
            Split = Math.Max(splitTargets, get("intermediary_projectile_number_of_targets_to_split_to")),
            Returns = get("projectiles_return") != 0 || attack && get("attack_projectiles_return") != 0
                || get("projectile_return_%_chance") >= 100,
            CannotPierce = get("cannot_pierce") != 0 || get("virtual_projectiles_cannot_pierce") != 0
                || !attack && get("projectiles_from_spells_cannot_pierce") != 0,
            CannotFork = forksApplyToZigZags || get("virtual_projectiles_cannot_fork") != 0
                || !attack && get("projectiles_from_spells_cannot_fork") != 0,
            CannotChain = get("virtual_projectiles_cannot_chain") != 0 || !attack && get("projectiles_from_spells_cannot_chain") != 0,
            CannotSplit = get("projectiles_cannot_split") != 0,
            CannotReturn = get("skill_projectiles_cannot_return") != 0
        };
        // A declared non-primary consumer keeps quantity when the competing split is prohibited.
        bool intrinsicCountOwnsQuantity = p.CountConsumer != ProjectileCountConsumer.Primary
            && (p.CannotSplit || modifiers.CannotSplit);
        Func<int, int>? finalCount = null;
        if (intrinsicSplitCount || !intrinsicCountOwnsQuantity
            && get("modifiers_to_number_of_projectiles_instead_apply_to_splitting") != 0)
        {
            if (p.CountConsumer != ProjectileCountConsumer.Primary)
                throw new InvalidOperationException("Unresolved competing projectile count conversions: " + p.Id);
            int basis = p.Split + (intrinsicSplitCount ? 0 : splitTargets);
            // Arrow split caps the converted total before subtracting the original arrow. Wand split caps afterwards.
            int split = arrows ? scaling.Total(1 + basis + extra) - 1
                : fixedCount ? basis : scaling.Converted(basis + extra);
            p = p with { Split = Math.Max(split, intrinsicSplitCount ? 0 : get("intermediary_projectile_number_of_targets_to_split_to")) };
            modifiers = modifiers with { ExtraProjectiles = 0, Split = 0 };
        }
        else if (!fixedCount && scaling.ChangesCount)
        {
            // Clustering modifies the explosions at each contact. The projectile itself keeps its base count.
            finalCount = p.CountConsumer == ProjectileCountConsumer.ContactAreas ? scaling.Converted : scaling.Total;
        }
        if (get("projectile_distance_override") is > 0 and var distance)
        {
            if (p.EndEmission is not null || p.Lobbed)
                throw new InvalidOperationException("Distance override needs an explicit early-end recipe for " + p.Id);
            p = p with { Range = distance };
        }
        // "Projectiles from Supported Skills have X metres maximum Direct Flight": the values are client units (10 per
        // metre), so 48 is 4.8 m. It caps rather than replaces either the baseline or a distance override.
        if (get("projectile_maximum_range_override") is > 0 and var maximum)
            p = p with { Range = Math.Min(p.Range, maximum) };
        int pierceFailure = Math.Clamp(get("projectile_chance_to_not_pierce_%"), 0, 100);
        int forkAngle = get("forking_angle_+%");
        // The 3.29.3c pierce decision tests this same arrow stat after either an executed fork or chain.
        bool pierceAfterForkOrChain = arrows && get("arrows_always_pierce_after_forking") != 0;
        int returnedSpeed = get("returned_projectile_speed_+%");
        double returnSpeedMultiplier = returnedSpeed == 0 ? 1
            : ProjectileSpeed.ReturnMultiplier(get(ProjectileSpeed.ModifierStat(attack && arrows)), returnedSpeed);
        ProjectileProfile ContinuationControls(ProjectileProfile target) => target with
        {
            ForkAnglePercent = target.ForkAnglePercent + forkAngle,
            PierceFailureChancePercent = Math.Max(target.PierceFailureChancePercent, pierceFailure),
            PierceAllAfterForkOrChain = target.PierceAllAfterForkOrChain || pierceAfterForkOrChain,
            ReturnChancePercent = Math.Max(target.ReturnChancePercent, Math.Clamp(get("projectile_return_%_chance"), 0, 100)),
            PierceAllOnReturn = target.PierceAllOnReturn || get("returning_projectiles_always_pierce") != 0,
            ReturnSpeedMultiplier = target.ReturnSpeedMultiplier * returnSpeedMultiplier
        };
        p = ContinuationControls(ProjectileParameters.Resolve(p with { ZigZags = Math.Max(p.ZigZags, zigZags) }, modifiers, finalCount));
        // Children receive already resolved continuation grants, never another count expansion.
        if (p.EndEmission is { } endEmission)
            p = p with { EndEmission = endEmission with { Child = ContinuationControls(ProjectileParameters.Resolve(endEmission.Child,
                modifiers with { ExtraProjectiles = 0 })) } };
        if (p.TravelEmission is { } travelEmission)
            p = p with { TravelEmission = travelEmission with { Child = ContinuationControls(ProjectileParameters.Resolve(travelEmission.Child,
                modifiers with { ExtraProjectiles = 0 })) } };
        if (get("projectiles_can_shotgun") != 0)
            p = p with { HitRule = new(ProjectileHitScope.Projectile) };
        return p;
    }

    readonly struct CountScaling(Func<string, int> get)
    {
        readonly float skill = Math.Max(0, (100 + get("number_of_projectiles_+%_final_from_skill")) / 100f);
        readonly float locus = Math.Max(0, (100 + get("number_of_projectiles_+%_final_from_locus_mine_support")) / 100f);
        readonly int minimum = get("minimum_number_of_projectiles_to_fire_is_1") != 0 ? 1 : 0;
        readonly int maximum = get("maximum_number_of_projectiles_to_fire_is_1") != 0 ? 1 : int.MaxValue;

        public bool ChangesCount => skill != 1 || locus != 1 || maximum == 1;

        // The client truncates after both single-precision products. Converted budgets include the original shot.
        public int Total(int count) => Math.Clamp((int)(count * skill * locus), minimum, maximum);
        public int Converted(int count) => Math.Clamp((int)((1 + count) * skill * locus) - 1, minimum, maximum);
    }

    // Native arrow alias 29cb: the generic additions above plus number_of_additional_arrows and the
    // main-hand-accuracy condition. Dash-recently folds onto that plain arrow stat at its accepted-use owner.
    // A profile passes arrows = true only for a bow attack.
    static int AdditionalArrows(Func<string, int> get)
    {
        int count = get("number_of_additional_arrows");
        if (get("main_hand_accuracy_rating") >= 3000)
            count += get("number_of_additional_arrows_while_main_hand_accuracy_is_3000_or_more");
        return count;
    }
}
