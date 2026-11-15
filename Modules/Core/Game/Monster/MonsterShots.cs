using GameServer.Core.Projectiles;

/// <summary>
/// Every monster shot from one place: which Projectiles row a monster's projectile skill fires, how many, whether the
/// client draws it, and the hidden or visible flight the server simulates for its hit (issue 1004).
///
/// The row is the one the client's action fires (static RE of the action classes, 3.29.3b; the shared flight resolver
/// 0x141ba43e0 takes the class's own row from vtable +0x508, else art slot 0):
/// <c>monster_mortar_attack</c> its MonsterMortar row (SkillID); <c>monster_projectile_spell</c> and
/// <c>monster_projectile_weapon</c> the MonsterProjectileSpell / MonsterProjectileAttack row its
/// <c>monster_projectile_variation</c> names; <c>skeleton_projectile_*</c> its fixed SkeletonProjectile row; a ranged
/// <c>melee</c> attack a bow's <c>default_ranged</c> art (by <c>arrow_projectile_variation</c>) or a wand's
/// WandProjectile; <c>split_arrow</c> the same choice with its own art; anything else its skill's art slot 0.
///
/// GGG's server creates no object for any of these (its 3.30.0b servers, predictive and lockstep: Goatman Shaman
/// fireballs, Burning Bowman and Brittle Poacher arrows, Axiom Frostguard cold projectiles, Bramble Cobra spit): the
/// client draws them from the actor record's count, and the hit arrives as the flight would reach the player (the
/// delay grows with distance at the resolved row's speed). So a shot the server can reproduce is the client's: its
/// record carries the count and the server flies it hidden. One the server cannot reproduce yet (a mover or column
/// it does not model, <see cref="SkillCatalog.MonsterProjectileInfo.ServerReason"/>) carries no count and flies as the
/// server's object, so the player never sees one shot and takes another.
/// </summary>
public static class MonsterShots
{
    // Families whose action flies a straight shot that hits on contact, which the server reproduces with the shared
    // volley, range and contact rules (Game.ClientDrawnProfile). Any other art-slot family flies as the server's object.
    static readonly HashSet<string> StraightFamilies =
    [
        "monster_projectile_spell", "monster_projectile_weapon", "monster_throw_equipped_weapon",
        "skeleton_projectile_cold", "skeleton_projectile_fire", "skeleton_projectile_lightning",
        "melee", "split_arrow", "fireball", "lightning_arrow", "burning_arrow", "ice_shot",
    ];

    // monster_projectile_weapon variations whose mover (0x141b6b740) does what the server does not: 4, 8, 24, 26 and 29
    // jitter the target from the action's seeded context and rescale it to 150; 3 flies at unlimited height.
    static readonly HashSet<int> WeaponMoverVariations = [3, 4, 8, 24, 26, 29];

    /// <summary>The shot a monster's granted effect fires at <paramref name="level"/>, or null when it fires none the
    /// server can name (it then keeps its contact hit). <paramref name="variety"/> decides a ranged melee attack's arrow
    /// or wand bolt; without it such an attack resolves to nothing. <paramref name="monsterStats"/> are the monster's own
    /// mod stats by name (MonsterModEffects, a map's monster stats among them): they add to the skill's, so a monster that
    /// fires additional projectiles or faster ones fires them in the client's count and the server's flight alike.</summary>
    public static SkillCatalog.MonsterProjectileInfo? Resolve(SkillCatalog catalog, MonsterCatalog.Variety? variety, int effect, int level,
        IReadOnlyDictionary<string, int>? monsterStats = null)
    {
        if (effect < 0) return null;
        var stats = new Dictionary<uint, int>();
        foreach (var (key, value) in catalog.EffectStats(effect, level)) stats.TryAdd(key, value);
        foreach (var (name, value) in monsterStats ?? new Dictionary<string, int>())
            if (value != 0 && Stats.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        bool aimTranslates = Get("maintain_projectile_direction_when_using_contact_position") != 0;

        if (catalog.MonsterProjectile(effect) is { } mortar)
            // The attack form is the client's (its MonsterMortar row and count on the record, traced); the spell forms keep
            // the server's object and their landing model.
            return mortar with
            {
                ServerReason = mortar.ClientDraws ? null : "spell mortar action not traced",
                AimTranslates = aimTranslates, SkillStats = stats,
                // The skill's count (SkillCatalog.MonsterProjectile) and the monster's own additional projectiles.
                Count = mortar.Count + (monsterStats?.GetValueOrDefault("number_of_additional_projectiles") ?? 0),
            };

        string? family = catalog.ActiveSkillId(effect);
        if (family is null) return null;
        string? id = null, animation = null, reason = null;
        bool arrows = false;
        var target = SkillCatalog.MonsterShotTarget.Ordered;
        switch (family)
        {
            case "monster_projectile_spell" or "monster_projectile_weapon":
            {
                int variation = Get("monster_projectile_variation");
                bool spell = family == "monster_projectile_spell";
                // An unknown variation throws in the client: no shot, and no count that would make it try.
                if (catalog.MonsterProjectileRow(spell, variation) is not { } row) return null;
                id = row.Projectile;
                animation = row.AnimationKey;
                if (spell && row.Extra.Skip(1).Any(v => v != 0))
                    reason = "MonsterProjectileSpell end height or lateral offset column";
                if (!spell && (row.Extra[3] != 0 || WeaponMoverVariations.Contains(variation)))
                    reason = $"MonsterProjectileAttack variation {variation} mover";
                // BoneSpear and IcicleBurstTriggered start at the target's own position at release (0x141b6b2f0).
                if (!spell && variation is 15 or 38) target = SkillCatalog.MonsterShotTarget.AtRelease;
                break;
            }
            case "monster_throw_equipped_weapon":
                id = "Metadata/Projectiles/PerandusThrownWeapon";
                break;
            case "skeleton_projectile_cold" or "skeleton_projectile_fire" or "skeleton_projectile_lightning":
            {
                // 0x141b329e0 / 0x141b32ab0 / 0x141b32b80: a fixed row, the player-skill one at skill_art_variation 1.
                string element = family["skeleton_projectile_".Length..];
                id = $"Metadata/Projectiles/SkeletonProjectile{char.ToUpperInvariant(element[0])}{element[1..]}"
                    + (Get("skill_art_variation") == 1 ? "PlayerSkill" : "");
                break;
            }
            case "melee":
                // A ranged melee attack (0x141ac9e70): a bow's default_ranged art by arrow_projectile_variation, else
                // the wand's bolt. Only a monster that holds a bow or a wand shoots with it.
                if (variety is not { RangedWeapon: true }) return null;
                arrows = HoldsBow(variety);
                id = arrows ? Game.Art.SkillProjectile(effect, 0, Get) : "Metadata/Projectiles/WandProjectile";
                break;
            case "split_arrow":
                // 0x141ab0700: without a bow the wand's bolt; with one, art slot 1 when it forks after a distance.
                arrows = BowShot(variety);
                id = arrows ? Game.Art.SkillProjectile(effect, Get("projectiles_fork_after_traveling_X_units_distance") != 0 ? 1 : 0, Get)
                    : "Metadata/Projectiles/WandProjectile";
                break;
            case "shaper_projectile":
                // Its own accelerating mover (0x1419f3620) from a row speed of 5: untraced, so no flight to simulate.
                return null;
            default:
                // Only a projectile skill (ActiveSkillTypes Projectile) fires one: a beam or a slam with art of its own does not.
                if (!catalog.IsProjectile(effect)) return null;
                id = Game.Art.SkillProjectile(effect, 0, Get) ?? catalog.ProjectileFor(effect)?.Id;
                // A bow skill's arrows (Lightning Arrow, Burning Arrow): its arrow modifiers apply.
                arrows = catalog.IsAttack(effect) && catalog.WeaponRestrictions(effect).Contains(Game.BowClassRow) && BowShot(variety);
                if (!StraightFamilies.Contains(family)) reason = $"{family} action not reproduced";
                break;
        }
        if (id is null || catalog.Projectile(id) is not { Speed: > 0 } row2) return null;
        if (reason is null)
        {
            if (Get("projectile_uses_contact_direction") != 0) reason = "projectile_uses_contact_direction";
            else if (Get("projectile_ballistic_angle_from_reference_event") != 0 || Get("projectile_ballistic_angle_from_target_distance") != 0)
                reason = "ballistic arc";
            else if (Get("projectile_spread_radius") != 0 || Get("projectile_minimum_range") != 0) reason = "scattered target";
            else if (Get("active_skill_projectile_speed_+%_variation_final") != 0) reason = "speed variation";
            else if (Get("support_parallel_projectile_number_of_points_per_side") != 0) reason = "parallel volley";
        }
        var shot = new SkillCatalog.MonsterProjectileInfo(id, row2.Speed, Lobbed: false, Math.Max(1, catalog.ProjectileRadius(id) ?? 1))
        {
            AnimationKey = animation ?? catalog.EffectAnimationKey(effect),
            ClientDraws = reason is null, ServerReason = reason, Arrows = arrows, Target = target,
            AimTranslates = aimTranslates, SkillStats = stats,
        };
        return shot with { Count = Count(shot, catalog.IsAttack(effect), family == "split_arrow") };
    }

    /// <summary>How many projectiles one release fires: <c>number_of_projectiles_override</c> when set, else the shared
    /// count (GatewayProjectileDefaults: the base and every additional projectile, a bow's additional arrows), plus Split
    /// Arrow's own additional arrows. The record and the server's flight both use this one number.</summary>
    public static int Count(SkillCatalog.MonsterProjectileInfo shot, bool attack, bool splitArrow = false)
    {
        int Get(string name) => Stats.Key(name) is uint key && shot.SkillStats is { } s ? s.GetValueOrDefault(key) : 0;
        if (Get("number_of_projectiles_override") is > 0 and var fixedCount) return fixedCount;
        int count = GatewayProjectileDefaults.Resolve(shot.Metadata, Get, 1, attack, shot.Arrows).Count;
        return Math.Max(1, count + (splitArrow ? Get("split_arrow_number_of_additional_arrows") : 0));
    }

    /// <summary>The record's count stats for a shot the client draws (none for any other): the totals the action's
    /// release reads (0x141ba8b00: virtual_total_number_of_arrows for a bow with skill_can_fire_arrows, the projectile
    /// total otherwise), a mortar's MonsterMortar row as skill_behaviour_variation, as GGG's Spitter records carry.</summary>
    public static IReadOnlyList<(uint, int)> CountStats(SkillCatalog.MonsterProjectileInfo? shot)
    {
        if (shot is not { ClientDraws: true }) return [];
        int n = Math.Max(1, shot.Count);
        // A mortar attack's record as GGG's Spitter's; any other shot with the four totals PR 983 checked live (the
        // Flame Sentinel's fireball fan), the arrow ones read only by a bow's arrows.
        return shot.Variation > 0
            ? [(StatKeys.SkillBehaviourVariation, shot.Variation), (StatKeys.TotalProjectilesToFire, n),
                (StatKeys.VirtualTotalProjectiles, n), (StatKeys.VirtualTotalArrows, n)]
            : [(StatKeys.TotalProjectilesToFire, n), (StatKeys.VirtualTotalProjectiles, n),
                (StatKeys.TotalArrowsToFire, n), (StatKeys.VirtualTotalArrows, n)];
    }

    /// <summary>The hidden flight of a shot the client draws: the shared straight-flight profile at the row's speed
    /// with the skill's speed stats, the client's own range, contact and volley (Game.ClientDrawnProfile, seeded by the
    /// monster), and the shot's one count.</summary>
    public static ProjectileProfile ClientDrawnProfile(ClientState conn, SkillCatalog.MonsterProjectileInfo shot, bool attack,
        ProjectileVector aim, ushort sequence, uint emitter)
    {
        int Get(string name) => Stats.Key(name) is uint key && shot.SkillStats is { } s ? s.GetValueOrDefault(key) : 0;
        double speed = ProjectileSpeed.CellsPerSecond(shot.Speed, Get, Get, Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        var profile = GatewayProjectileDefaults.Resolve(shot.Metadata, Get, speed, attack, shot.Arrows) with
        {
            Count = Math.Max(1, shot.Count), Radius = shot.CollisionRadius, DirectDamage = true, HideVisual = true,
        };
        return Game.ClientDrawnProfile(conn, profile, Get, sequence, aim, emitter: emitter);
    }

    /// <summary>Whether the variety holds a bow in its main hand (its Weapons, from MonsterVarieties
    /// MainHand_ItemClassesKey: the ItemClasses row Bow): the ranged melee attack's arrows, else a wand's bolt.</summary>
    public static bool HoldsBow(MonsterCatalog.Variety variety) =>
        variety.Weapons.Any(w => w.Slot == 0 && w.Type == Game.BowClassRow + 1);

    // A bow skill used without a known variety (an encounter's own move, the Labyrinth Lieutenant's) is taken to hold a bow.
    static bool BowShot(MonsterCatalog.Variety? variety) => variety is null || HoldsBow(variety);

}
