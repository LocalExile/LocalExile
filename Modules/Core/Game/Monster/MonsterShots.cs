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
        "melee", "split_arrow", "fireball", "lightning_arrow", "burning_arrow", "ice_shot", "frost_bolt",
    ];

    // Classes whose ctor sets byte +0x330 = 5, which the shared pierce check (0x141ba96a0) reads as pierce-all.
    static readonly HashSet<string> PierceAllFamilies =
    [
        "frost_bolt", "freezing_pulse", "breach_spell_barrage", "monster_snipe", "monster_fading_projectile_spell",
        "ball_lightning", "shavronne_ball_lightning",
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
        // The effect's skill_art_variation (GrantedEffects __col23), which the client's own skill-stat builder adds and
        // most of these actions pick their row by; the record carries it too (Monsters.MonsterProjectileSkillStats).
        if (catalog.ArtVariation(effect) is > 0 and var art && Stats.Key("skill_art_variation") is uint artKey && !stats.ContainsKey(artKey))
            stats[artKey] = art;
        // ... and its skill_behaviour_variation (its SkillID), which the record carries too (WithBehaviourVariation).
        if (catalog.BehaviourVariation(effect) is > 0 and var behaviour && !stats.ContainsKey(StatKeys.SkillBehaviourVariation))
            stats[StatKeys.SkillBehaviourVariation] = behaviour;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The radius percentage an area of the shot is sized by (0x1591, which the client derives for no network stat):
        // the skill's own area stats and the monster's (Essence breaths -60% and -80%, the Hailrake's +100%).
        if (SkillArea.RadiusPercent(Get, spell: !catalog.IsAttack(effect)) is not 0 and var areaPercent
            && Stats.Key(AreaPercent) is uint areaKey)
            stats[areaKey] = areaPercent;
        bool aimTranslates = Get("maintain_projectile_direction_when_using_contact_position") != 0;

        if (catalog.MonsterProjectile(effect) is { } mortar)
        {
            // The skill's count (SkillCatalog.MonsterProjectile) and the monster's own additional projectiles.
            mortar = mortar with
            {
                AimTranslates = aimTranslates, SkillStats = stats,
                Count = mortar.Count + (monsterStats?.GetValueOrDefault("number_of_additional_projectiles") ?? 0),
                DistanceOverride = Get("mortar_projectile_distance_override"),
                // The row's UseTargetLocation: a fanned volley keeps the aim's distance only with it (0x141bb9730).
                EndsAtTarget = catalog.ProjectileEndsAtTarget(mortar.Metadata),
            };
            // The attack form is the client's (its MonsterMortar row and count on the record, traced).
            if (mortar.ClientDraws) return mortar;
            // Where the server draws a spell mortar, its object lands as the client would place it: a nova in a ring, a
            // cone across its angle (the shared emitter's layouts), each at its landing distance (ProjectileHostile).
            if (Get("projectiles_nova") != 0) mortar = mortar with { SpreadRadians = 2 * Math.PI };
            else if (Get("mortar_cone_angle") is > 0 and var cone) mortar = mortar with { SpreadRadians = cone * Math.PI / 180 };
            return SpellMortar(catalog, effect, mortar, Get) ?? mortar with { ServerReason = "spell mortar row not named" };
        }

        string? family = catalog.ActiveSkillId(effect);
        if (family is null) return null;
        string? id = null, animation = null, reason = null, recipe = null;
        bool arrows = false, lobbed = false;
        int aimOffset = 0;
        var target = SkillCatalog.MonsterShotTarget.Ordered;
        int artVariation = Get("skill_art_variation"), mpv = Get("monster_projectile_variation");
        // The row an action names in its own code (+0x508): by a variation, else its default.
        static string Row(int key, string fallback, params (int Key, string Id)[] rows) =>
            "Metadata/Projectiles/" + (rows.FirstOrDefault(r => r.Key == key).Id ?? fallback);
        switch (family)
        {
            // Families whose class names its row in code (+0x508) and flies a plain straight shot the server reproduces.
            case "spark":
                // 0x141b35540: the spark_monster art by monster_projectile_variation where the skill has one, else the
                // spark art at skill_art_variation. Its legs are the client's seeded wander (SparkLegs, the player's).
                id = mpv > 0 ? Row(mpv, "BookTornado", (2, "FireTornado"), (3, "AfflictionFlamenado"), (4, "SanctumFlamenado"))
                    : Game.Art.SkillProjectile(effect, 0, Get);
                recipe = "Spark";
                break;
            case "ice_spear":
                // Art slot 0 (IceSpear). It changes form once along its flight (0x141a07b00 / 0x141a07db0).
                id = Game.Art.SkillProjectile(effect, 0, Get);
                recipe = "IceSpear";
                if (Get("projectile_random_angle_based_on_distance_to_target_location_%") != 0) reason = "distance-based angle jitter";
                else if (Get("skill_behaviour_variation") != 0) reason = "ice spear release variant";
                break;
            case "creeping_frost":
                // 0x141a33b90: art slot 2 for a volley of more than five with a behaviour variation, else the slot it names.
                {
                    int frostBehaviour = Get("skill_behaviour_variation");
                    int volley = Math.Max(1, Get("base_number_of_projectiles")) + Get("number_of_additional_projectiles");
                    id = Game.Art.SkillProjectile(effect, volley > 5 && frostBehaviour != 0 ? 2 : frostBehaviour, Get);
                    recipe = "CreepingFrost";
                    if (frostBehaviour != 0) reason = "creeping frost arc";
                }
                break;
            case "frost_bolt":
                id = Game.Art.SkillProjectile(effect, 0, Get);
                if (Get("frostbolt_projectile_acceleration") != 0) reason = "frost bolt acceleration";
                break;
            case "atziri_spear_throw": id = Row(0, "AtziriSpear"); break;                             // 0x141376e30
            case "stormflay_lightning_strike": id = Row(0, "StormflayLightningSlash"); break;         // 0x141c65aa0
            case "monster_snipe":                                                                       // 0x141b6c000
                id = Row(artVariation, "NightwaneSnipeArrow", (1, "HydraSnipeArrow"), (2, "StagBanditSnipe"), (3, "LunarisSnipeDash"),
                    (4, "ElderGuardianChaosSnipe"));
                break;
            case "monster_fading_projectile_spell":                                                     // 0x1413e6730
                id = Row(mpv, "SummonPhantasmFadingProjectile", (2, "BetrayalPhantasmProjectile"), (3, "AtlasExileCrusaderJudgeNova"));
                break;
            case "essence_drain":                                                                       // 0x141b32820
                id = Row(artVariation, "Siphon", (1, "RigwaldSiphon"), (2, "SiphonMicrotransaction1"), (3, "PaleKingSiphon"),
                    (4, "CelestialSiphon"), (5, "ScientistSiphon"), (6, "ProjectSiphon"), (7, "SunprismSiphon"));
                break;
            case "insect_spawner_spit":                                                                 // 0x141332700
                id = Row(artVariation, "InsectSpawnerSpit", (1, "InsectSpawnerSpitParasiteBoss"));
                break;
            case "monster_projectile_spell_ally" or "monster_projectile_spell_self":
                // The MonsterProjectileSpell row, as monster_projectile_spell; a self-cast's aim is moved by a seeded angle.
                if (catalog.MonsterProjectileRow(true, mpv) is not { } spellRow) return null;
                (id, animation) = (spellRow.Projectile, spellRow.AnimationKey);
                if (family == "monster_projectile_spell_self") reason = "self-cast aim";
                break;
            case "monster_puncture" or "elemental_hit_monster":
                // 0x141b70b00 / 0x141acb370: a real bow's arrow, any other ranged hand's wand bolt; a melee hand swings.
                if (variety is null || !(HoldsBow(variety) || HoldsWand(variety))) return null;
                arrows = HoldsBow(variety);
                if (family == "monster_puncture")
                    id = arrows ? Row(artVariation, "PunctureArrow", (1, "PunctureArrowStygian")) : Row(0, "WandProjectile");
                else
                {
                    id = Row(0, arrows ? "ElementalHitArrowCombined" : "ElementalHitWandCombined");
                    reason = "element roll";
                }
                break;
            case "barrage":
                // barrage_bow art with a real bow, barrage_wand otherwise; its arrows leave one after another.
                arrows = BowShot(variety);
                id = arrows ? Game.Art.SkillProjectile(effect, 0, Get) : Row(artVariation, "BarrageWand", (2, "BarrageWandCelestial"));
                reason = "sequential barrage";
                break;

            // Families the server cannot reproduce yet: the right row as the server's object, so the hit lands on arrival.
            case "demon_female_ranged_projectile":
                id = Row(mpv, "FemaleDemonRangedProjectile", (1, "FemaleDemonRangedProjectileBlue")); reason = "alternating hand offset"; break;
            case "miner_throw_fire": id = Row(0, "MinerLanternFire"); reason = "flight timed by its clip"; break;
            case "bloodline_daemon_nova": id = Row(0, "RampageProjectile"); reason = "nova of the pack's deaths"; break;
            case "synthesis_turret_charge_projectile": id = Row(0, "SynthesisGuardian2Turret"); reason = "turret charge"; break;
            case "decelerating_projectile":                                                             // 0x141ac9c90
                id = Row(artVariation, "DeceleratingProjectilesTriggerNova", (1, "DeceleratingProjectilesKitava"), (2, "ZanaRoseSpectralThrow"),
                    (3, "ToraDeceleratingArrow"), (4, "BetrayalBlackguardDeceleratingProjectile"), (5, "BetrayalRinDeceleratingProjectile"),
                    (6, "LegionTemplarDeceleratingShield"), (13, "AtlasExilesBlackguardDeceleratingProjectile"),
                    (14, "AfflictionMinionDeceleratingProjectile"));
                reason = "decelerating mover";
                break;
            case "shavronne_ball_lightning":                                                            // 0x141398f30
                id = Row(artVariation, "ShavronneBallLightning", (1, "HarbingerBallLightning")); reason = "ball lightning pulses"; break;
            case "storm_burst":                                                                         // 0x141c658f0
                id = Row(artVariation, "StormBurst", (2, "ShavronneStormBurst"), (1, "YugulChromeProjectile")); reason = "storm burst orbs"; break;
            case "water_vortex":                                                                        // 0x14133b070
                id = Row(artVariation, "WaterVortex", (1, "WaterVortexArtVariation"), (2, "PusWaterVortex"), (3, "WaterVortexRed"),
                    (4, "IncursionMapDeviceWaterVortex"));
                reason = "vortex pulses";
                break;
            case "freezing_pulse":
                id = Row(artVariation, "FreezingPulse", (1, "UndyingFreezingPulse"), (2, "FreezingPulseMTX")); reason = "pulse decay"; break;
            case "ice_elemental_spear":                                                                 // 0x141244f10
                id = Row(mpv, "IceElementalProjectile", (1, "PuiTotemProjectile"), (2, "IceElementalSwordProjectile"),
                    (3, "ElderGuardianObsidianGolemProjectile"));
                reason = "seeded target jitter";
                break;
            case "atlas_aoe_projectile": id = Row(0, "AtlastFrostBoltBlackHole"); reason = "vortex at the end"; break;
            case "breach_spell_barrage": id = Row(0, "BreachSpellBarrage"); reason = "seeded barrage angles"; break;
            case "spectral_throw" or "vaal_spectral_throw" or "vaal_thrown_weapon":
                // 0x141ab6480: the thrown weapon by skill_art_variation. Spectral Throw comes back (the player's recipe).
                id = Row(artVariation, "ThrownWeapon", (1, "SpectralThrowGold"), (2, "SpectralThrowEbony"), (3, "SpectralThrowBoomerang"),
                    (4, "SpectralThrowFairgraves"), (5, "TormentSpectralThrow"), (6, "NoEffectsSpectralThrow"), (7, "HandfingerFireThrownWeapon"),
                    (8, "RigwaldMoonbendersWing"), (9, "SpectralThrowEbonyMTXNew"), (10, "BreachChaosThrownWeapon"), (11, "KitavaThrownWeapon"),
                    (12, "TripleBladeThrownWeapon"), (13, "ElderGuardianHolySpectralThrowLeft"), (14, "ElderGuardianHolySpectralThrowRight"),
                    (15, "KitavaThrownWeaponMtx"));
                if (family == "spectral_throw") recipe = "SpectralThrow";
                reason = family == "spectral_throw" ? "thrown weapon's deceleration" : "thrown weapon spiral";
                break;
            case "ethereal_knives":                                                                     // 0x1419f2d50
                // Its trap and totem forms throw the trap or place the totem, which the server does not: its object flies.
                id = mpv != 0 ? Row(0, "MaligaroEtherealKnives") : Row(artVariation, "ShadowProjectile", (1, "ShadowProjectileStar"),
                    (2, "SpiderEtherealKnives"), (3, "EbonyEtherealKnvies"), (5, "EtherealKnivesSawBlade"));
                reason = Get("is_trap") != 0 || Get("is_totem") != 0 ? "totem, trap or mine form" : "seeded knife jitter";
                break;
            case "incinerate_old" or "incinerate_channel" or "figurehead_incinerate":                  // 0x141b834b0
                id = Row(artVariation, "Flamethrower1", (1, "FlamethrowerBlue1"), (2, "IncinerateHoly"), (3, "GhostProjectile"),
                    (4, family == "figurehead_incinerate" ? "AbyssLichFigureHeadIncinerate" : "IncinerateKitavaMinion"));
                reason = "flame stage jitter";
                break;
            case "totem_fire_spray" or "totem_fire_spray_triggerable":                                 // 0x141b84e00
                id = Row(artVariation, "TotemFireSpray", (1, "FlameBearerFireSpray"), (2, "SnakeFireSpray"), (3, "SnakeLightningSpray"),
                    (4, "FireElementalSpray"), (5, "GhostFlameTotemFireSpray"), (6, "FlameBearerFireSprayGold"));
                reason = "seeded spray offsets";
                break;

            // Lobbed families: the server's object lands on the aim and hits the area there.
            case "matriach_fire_mortar":
                id = Row(artVariation, "FireMortar", (1, "MatriachMortar"), (2, "MatriarchFireMortar1"), (3, "MatriarchFireMortar2"));
                (lobbed, reason) = (true, "mortar landing");
                break;
            case "kaom_volcano_mortar": (id, lobbed, reason) = (Row(0, "KaomMortarMolten"), true, "mortar landing"); break;
            case "lb_boss_spirit_mortar": (id, lobbed, reason) = (Row(0, "LbBossSpiritMortar"), true, "mortar landing"); break;
            case "monster_mortar_around_caster": (id, lobbed, reason) = (Row(0, "MortarLightning"), true, "mortar ring"); break;
            case "breach_mortar_in_line":                                                               // 0x141a15cf0
                if (mpv is < 0 or > 8) return null;
                id = Row(mpv, "BreachChaosMortar", (2, "KitavaDemonMortar"), (3, "YugulMortar"), (4, "Maligaro2Mortar"), (5, "FigureheadMortar"),
                    (6, "ElderProjectileDoom"), (7, "VilentaXMortar"), (8, "VilentaXMortarFire"));
                (lobbed, reason) = (true, "mortar landing");
                break;
            case "mortar_with_ground_effects":                                                          // 0x1419eae30
                if (mpv is < 0 or > 10) return null;
                id = Row(mpv, "MalachaiMortar", (2, "TarMortar"), (5, "NetMortar"), (6, "WebMortar"), (10, "WebMortar"), (7, "CausticMortar"),
                    (8, "BloodMortar"), (9, "ElderGuardianCausticMortar"));
                (lobbed, reason) = (true, "mortar landing");
                break;
            case "breach_bone_mortar":                                                                  // 0x141a17400
                if (mpv is < 0 or > 5) return null;
                id = Row(mpv, "BoneGoreMortar", (1, "FireMortar"), (2, "Maligaro2Mortar"), (3, "BoneMortar"), (5, "BoneMortar"),
                    (4, "ClockworkGolemLightningMortar"));
                (lobbed, reason) = (true, "mortar landing");
                break;
            case "rolling_magma":                                                                       // 0x141b81650
                id = Row(artVariation, "FireMortar", (1, "MagmaOrbConcoctionProjectile"), (3, "BloodMortar"), (4, "SplashMortar"),
                    (5, "MatriachMortar"), (6, "HarbingerFireMortar"), (7, "MatriarchFireMortar1"), (8, "MatriarchFireMortar2"),
                    (9, "FlameBileMortar"), (10, "IncursionNovasoulMagmaOrb"), (11, "KitavaCasterMortarProjectile"));
                (lobbed, recipe, reason) = (true, "RollingMagma", "magma bounces");
                break;

            // Not a flying projectile at the caster: a beam (the golem's arc), a line hazard, a totem or trap summon, a
            // retaliation, a rotating-orb rectangle, monsters a mortar brings. They keep their own handling.
            case "lightning_golem_arc" or "spider_god_riptide" or "lightning_clone_retaliation" or "rectangle_mortar_with_rotating_orbs"
                or "siege_ballista" or "summon_specific_monsters_from_mortar" or "shadow_projectiles_no_los":
                return null;

            case "monster_projectile_spell" or "monster_projectile_weapon":
            {
                int variation = Get("monster_projectile_variation");
                bool spell = family == "monster_projectile_spell";
                // An unknown variation throws in the client: no shot, and no count that would make it try.
                if (catalog.MonsterProjectileRow(spell, variation) is not { } row) return null;
                id = row.Projectile;
                animation = row.AnimationKey;
                // The movers (0x141a13830, 0x141b6b740) move the aim on along the shot by a column's cells; the other
                // columns are an end height and flags that do not change where the shot flies.
                aimOffset = spell ? row.Extra[2] : row.Extra[3];
                if (spell && artVariation == 20) reason = "MonsterProjectileSpell art 20 branch";
                if (!spell && WeaponMoverVariations.Contains(variation))
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
                if (variety is null || !Shoots(variety)) return null;
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
                // Art slot 0 (ShaperProjectile, speed 5); its mover (0x1419f6030, frame hook 0x1419f6180) speeds it up by
                // 50 cells/s² from the row's speed (AcceleratingLegs). Every one carries always_pierce.
                id = Game.Art.SkillProjectile(effect, 0, Get);
                recipe = "Accelerating";
                break;
            case "inca_minion_projectile":
                // 0x141a0ba30 (+0x508): IncaBossMinionProjectile at skill_art_variation 1, else IncaMinionProjectile; a
                // plain straight spell shot (its ActiveSkill lacks the Projectile type, so the default below would drop it).
                id = Row(artVariation, "IncaMinionProjectile", (1, "IncaBossMinionProjectile"));
                break;
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
        if (lobbed) target = SkillCatalog.MonsterShotTarget.AtRelease;
        if (reason is null)
        {
            // A totem's or a trap's form places the totem or throws the trap, which then fires (SpellTotems: is_totem
            // describes the placement); the server places neither, so its object flies from the monster.
            if (Get("is_totem") != 0 || Get("is_trap") != 0 || Get("is_mine") != 0) reason = "totem, trap or mine form";
            // A spiral fires its projectiles one by one over projectile_spiral_nova_time_ms, which the shot's one
            // volley does not.
            else if (Get("projectile_spiral_nova_time_ms") != 0) reason = "spiral nova";
            // The count getter (0x141ba61c0) splits the volley over the clip's contacts; the server releases it at one.
            else if (Get("distribute_projectiles_over_contact_points") != 0) reason = "projectiles over contact points";
            else if (Get("projectile_uses_contact_direction") != 0) reason = "projectile_uses_contact_direction";
            else if (Get("projectile_ballistic_angle_from_reference_event") != 0 || Get("projectile_ballistic_angle_from_target_distance") != 0)
                reason = "ballistic arc";
            // projectile_minimum_range moves no straight shot in these classes; only a mortar's landing.
            else if (Get("projectile_spread_radius") != 0) reason = "scattered target";
            else if (Get("active_skill_projectile_speed_+%_variation_final") != 0) reason = "speed variation";
            else if (Get("support_parallel_projectile_number_of_points_per_side") != 0) reason = "parallel volley";
        }
        var shot = new SkillCatalog.MonsterProjectileInfo(id, row2.Speed, Lobbed: lobbed, Math.Max(1, catalog.ProjectileRadius(id) ?? 1))
        {
            AnimationKey = animation ?? catalog.EffectAnimationKey(effect),
            ClientDraws = reason is null, ServerReason = reason, Arrows = arrows, Target = target,
            AimTranslates = aimTranslates, SkillStats = stats, EndsAtTarget = catalog.ProjectileEndsAtTarget(id), Recipe = recipe,
            AimOffset = aimOffset, PiercesAll = PierceAllFamilies.Contains(family) || Get("always_pierce") != 0,
        };
        // A spark with skill_behaviour_variation 2 fires one (0x141b363a0).
        if (family == "spark" && catalog.BehaviourVariation(effect) == 2) return shot with { Count = 1 };
        return shot with { Count = Count(shot, catalog.IsAttack(effect), family == "split_arrow") };
    }

    /// <summary>
    /// A spell mortar (<c>monster_mortar</c>, ActionTypes MonsterMortar, ctor 0x141a10750) as the client draws it: the same
    /// MonsterMortar row as the attack form, by <c>skill_behaviour_variation</c> (the effect's SkillID, which the record
    /// carries), so the client draws it from its count as it does the Sand Spitter's. Its mover (0x141a10d50) moves the
    /// launch <c>__col9</c> cells toward the target, pushes the landing out to the minimum range (plus <c>__col9</c>) from
    /// the actor, rescales it to <c>mortar_projectile_distance_override</c> from the launch, scatters it like the attack
    /// form, and lands it a fixed time after the release: its flight clip's contact (666 ms on most mortar rigs, from 166 ms
    /// to over a second on the rest) over a speed multiplier, at any distance; with
    /// <c>mortar_projectile_scale_animation_speed_instead_of_projectile_speed</c> it flies at its row's speed instead. A
    /// <c>__col5</c> row fires one. What the server does not reproduce keeps its object (a reason): forks, chains, a
    /// ground effect or a bounce on landing, and what the shared emitter (0x141bb13a0) does with the landings the
    /// server puts at one aim: a nova's ring (0x141bb33f0 reads projectiles_nova, 0x141bb9500 turns each target), a
    /// cone, swapped ends, the contact direction and a capped range. Null for no row (around_caster is its own action,
    /// MonsterShots' switch).
    /// </summary>
    static SkillCatalog.MonsterProjectileInfo? SpellMortar(SkillCatalog catalog, int effect, SkillCatalog.MonsterProjectileInfo mortar,
        Func<string, int> get)
    {
        if (catalog.ActiveSkillId(effect) != "monster_mortar" || catalog.BehaviourVariation(effect) is not (> 0 and var row)
            || catalog.MonsterMortarColumns(row) is not { } columns) return null;
        string? reason = get("monster_mortar_number_of_forks") != 0 ? "spell mortar forks"
            : get("number_of_chains") != 0 ? "spell mortar chains"
            : columns.Ground || columns.BounceScale ? "spell mortar ground effect or bounce"
            : get("projectiles_nova") != 0 ? "spell mortar nova"
            : get("mortar_cone_angle") != 0 ? "spell mortar cone"
            : get("projectile_swap_from_to_locations") != 0 ? "swapped ends"
            : get("projectile_uses_contact_direction") != 0 ? "projectile_uses_contact_direction"
            : get("projectile_maximum_range_override") != 0 ? "capped range"
            : get("projectiles_barrage") != 0 || get("support_parallel_projectile_number_of_points_per_side") != 0 ? "barrage or parallel volley"
            : get("distribute_projectiles_over_contact_points") != 0 ? "projectiles over contact points"
            : get("is_totem") != 0 || get("is_trap") != 0 || get("is_mine") != 0 ? "totem, trap or mine form" : null;
        // Its fixed flight is its rig's flight clip's contact (projectile-clips): without that clip the server cannot
        // time it (the Sanctum candles' views, an undefined clip).
        bool scaled = get("mortar_projectile_scale_animation_speed_instead_of_projectile_speed") != 0;
        int? contact = catalog.Clips(mortar.Metadata)?.Flight?.Contact;
        if (reason is null && !scaled && contact is not > 0) reason = "spell mortar flight clip not known";
        // A row of several flight clips draws one by the action's seed (0x141ba5d20), each with its own contact.
        if (reason is null && !scaled && FlightClips.Value.GetValueOrDefault(mortar.Metadata) > 1) reason = "seeded flight clip";
        if (reason is not null) return mortar with { ServerReason = reason };
        int speedPercent = columns.Speed + get("monster_mortar_additional_speed_+%")
            + (columns.SpeedModifier ? get("projectile_speed_+%") + get("base_projectile_speed_+%") : 0);
        int count = columns.Single ? 1 : mortar.Count;
        return mortar with
        {
            ClientDraws = true, ServerReason = null, Variation = row, Target = SkillCatalog.MonsterShotTarget.MortarScatter,
            Count = count, StartOffset = columns.StartOffset,
            // 0x141bab4e0: the spread grows by its per-projectile part for every projectile past the first.
            SpreadRadius = mortar.SpreadRadius + (count - 1) * get("projectile_spread_radius_per_additional_projectile"),
            FixedFlight = !scaled, ContactSeconds = (contact ?? 666) / 1000.0,
            MortarSpeed = (speedPercent, columns.SpeedVariation),
        };
    }

    // How many flight clips each Projectiles row names (LoopAnimationIds).
    static readonly Lazy<Dictionary<string, int>> FlightClips = new(() =>
    {
        var result = new Dictionary<string, int>();
        if (GameServer.Core.ClientTables.TableOrNull("Projectiles") is { } rows)
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].TextOrNull("Id") is { } id) result.TryAdd(id, rows[i].Texts("LoopAnimationIds").Length);
        return result;
    });

    /// <summary>The seconds a fixed-time spell mortar flies (0x141a10d50: its flight clip's contact over the multiplier
    /// max(0.01, 1 + (fixed + variation × (draw − 1)) / 100), with the draw in [0, 2] the action's first).</summary>
    public static double MortarFlightSeconds(SkillCatalog.MonsterProjectileInfo shot, float draw) =>
        shot.ContactSeconds / Math.Max(0.01, 1 + (shot.MortarSpeed.Fixed + shot.MortarSpeed.Variation * (draw - 1)) / 100.0);

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
        List<(uint, int)> stats = shot.Variation > 0
            ? [(StatKeys.SkillBehaviourVariation, shot.Variation), (StatKeys.TotalProjectilesToFire, n),
                (StatKeys.VirtualTotalProjectiles, n), (StatKeys.VirtualTotalArrows, n)]
            : [(StatKeys.TotalProjectilesToFire, n), (StatKeys.VirtualTotalProjectiles, n),
                (StatKeys.TotalArrowsToFire, n), (StatKeys.VirtualTotalArrows, n)];
        // A spark's life is the record's skill_effect_duration (a virtual stat the client derives nothing for: without it
        // a spark dies on its first tick).
        if (shot.Recipe == "Spark" && Stat(shot, "base_skill_effect_duration") is > 0 and var life && Stats.Key("skill_effect_duration") is uint lifeKey)
            stats.Add((lifeKey, life));
        // A burst's radius percentage (a creeping frost's, a spell mortar's landing), by which the client sizes the burst
        // it draws (0x141939aa0) as the server sizes its hit.
        if ((shot.Recipe == "CreepingFrost" || shot.Lobbed) && Stat(shot, AreaPercent) is not 0 and var area && Stats.Key(AreaPercent) is uint areaKey)
            stats.Add((areaKey, area));
        return stats;
    }

    const string AreaPercent = "total_skill_area_of_effect_radius_+%_including_final";

    /// <summary>A lobbed shot's area radius in cells: the shot's own, else its skill's base radius (the development
    /// fallback without one), by the skill's area percentage.</summary>
    public static double AreaRadius(SkillCatalog.MonsterProjectileInfo shot, double fallback) =>
        shot.AreaRadius > 0 ? shot.AreaRadius
            : (Stat(shot, "active_skill_base_area_of_effect_radius") is > 0 and var radius ? radius : fallback)
                * Math.Max(0, 1 + Stat(shot, AreaPercent) / 100.0);

    static int Stat(SkillCatalog.MonsterProjectileInfo shot, string name) =>
        Stats.Key(name) is uint key && shot.SkillStats is { } s ? s.GetValueOrDefault(key) : 0;

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
        if (shot.PiercesAll && Get("virtual_projectiles_cannot_pierce") == 0) profile = profile with { Pierce = -1 };
        profile = Game.ClientDrawnProfile(conn, profile, Get, sequence, aim, emitter: emitter);
        // A UseTargetLocation row's flight ends at the aimed cell (0x141ba99d0 keeps the aim); a column's aim offset
        // lengthens the flight by as much.
        profile = shot.EndsAtTarget ? profile with { Range = Math.Max(.001, aim.Length) }
            : shot.AimOffset != 0 ? profile with { Range = profile.Range + shot.AimOffset } : profile;
        if (shot.Recipe == "IceSpear")
        {
            // The first form pierces every target (byte +0x330 = 5); past 20.199 cells × (1 + its distance stat) it flies
            // on in its second form, faster by ice_spear_second_form_projectile_speed_+%_final and piercing by its stats.
            int pierce = Math.Max(Get("projectile_number_of_targets_to_pierce"), Get("projectile_base_number_of_targets_to_pierce"));
            profile = profile with
            {
                Pierce = Get("virtual_projectiles_cannot_pierce") != 0 ? 0 : -1,
                // shortcut: ice_spear_second_form_damage_+% is increased damage, applied here as a multiplier of the
                // monster's finished hit (its other increased damage is already folded into that hit).
                FormChange = new(20.199 * (100 + Get("ice_spear_distance_before_form_change_+%")) / 100.0,
                    1 + Get("ice_spear_second_form_projectile_speed_+%_final") / 100.0, pierce,
                    Math.Max(0, 1 + Get("ice_spear_second_form_damage_+%") / 100.0)),
            };
        }
        if (shot.Recipe == "CreepingFrost") profile = CreepingFrostBurst(profile, Get);
        if (shot.Recipe == "Accelerating")
        {
            // From the row's speed it gains 50 cells/s every second (0x1419f6180: s = v0·t + 25·t²) to its range.
            double range = profile.Range;
            profile = profile with { Legs = _ => new AcceleratingLegs(speed, 50, range), LegLeash = range };
        }
        if (shot.Recipe == "Spark")
        {
            // The client's wander (0x141b35a10 seeds [sequence, 2 + index, repeats, owner]): life from the record's
            // skill_effect_duration and spark_random_duration_%; it forgets its hits every 0.66 s and ends 150 cells
            // out, but every 0.1 s with no leash at skill_behaviour_variation 1, 2 or 4 (0x141b36170).
            double life = Math.Max(.05, Get("base_skill_effect_duration") / 1000.0);
            int behaviour = Get("skill_behaviour_variation");
            bool fast = behaviour is 1 or 2 or 4;
            profile = profile with
            {
                Legs = i => new SparkLegs(sequence, i, emitter, life, Get("spark_random_duration_%"), speed, 0),
                LegLeash = fast ? 0 : 150,
                HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Window, ProjectileRuntime.Time(fast ? 0.1 : 0.66)),
            };
        }
        return profile;
    }

    // A creeping frost's burst where it ends or meets the player (0x1413e0550): active_skill_base_area_of_effect_radius
    // (15) by the radius percentage the record carries. It meets the player as a Fireball does: the contact ends it and
    // the burst is the one hit (the burst passes over the actor the contact hit); the chilled ground it leaves is not
    // modelled.
    static ProjectileProfile CreepingFrostBurst(ProjectileProfile profile, Func<string, int> get) => profile with
    {
        DirectDamage = true,
        EndArea = new ProjectileAreaRecipe("creeping frost",
            Math.Max(1, get("active_skill_base_area_of_effect_radius") is > 0 and var r ? r : 15)
                * Math.Max(0, 1 + get(AreaPercent) / 100.0)),
        EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Enemy, ProjectileEndReason.Terrain, ProjectileEndReason.Distance },
    };

    /// <summary>The server's flight of a shot it draws itself by a player skill's recipe (Rolling Magma's bouncing lob
    /// and its landing explosions, Spectral Throw's piercing throw that comes back), at the shot's speed and count; the
    /// explosions of a monster's magma are 14 cells plus active_skill_base_radius_+ across its radius percentage (the
    /// client's 0x141b80880), and each bounce lands 2S + 3 cells on (0x141b80a30: S = round(10 × (1 + chaining range)
    /// × (1 + projectile speed)), 23 cells at base). Each bounce's own scatter is not modelled.</summary>
    public static ProjectileProfile RecipeProfile(SkillCatalog.MonsterProjectileInfo shot, double speed, double landing, bool attack)
    {
        int Get(string name) => Stats.Key(name) is uint key && shot.SkillStats is { } s ? s.GetValueOrDefault(key) : 0;
        var profile = GatewayProjectileSkillProfiles.Resolve(shot.Recipe!, shot.Metadata, Get, speed, attack, shot.Arrows) with
        {
            Count = Math.Max(1, shot.Count), Radius = shot.CollisionRadius, HideVisual = false,
        };
        if (shot.Recipe == "RollingMagma")
        {
            double hop = Math.Max(1, Math.Floor(10 * (1 + Get("chaining_range_+%") / 100.0) * MagmaSpeed(shot) + .5));
            return profile with
            {
                Range = landing,
                GroundBounceDistance = 2 * hop + 3,
                EndArea = new ProjectileAreaRecipe("monster magma",
                    (14 + Get("active_skill_base_radius_+")) * Math.Max(0, 1 + Get(AreaPercent) / 100.0)),
            };
        }
        if (shot.Recipe == "CreepingFrost") return CreepingFrostBurst(profile, Get);
        if (shot.Recipe == "SpectralThrow")
            return profile with
            {
                Legs = _ => new SpectralThrowLegs(speed), LegLeash = SpectralThrowLegs.Leash(speed), ReturnOvershoot = 0,
                ReturnSpeedMultiplier = profile.ReturnSpeedMultiplier * SpectralThrowLegs.ReturnSpeedMultiplier,
            };
        return profile;
    }

    static double MagmaSpeed(SkillCatalog.MonsterProjectileInfo shot) =>
        1 + (Stat(shot, "projectile_speed_+%") + Stat(shot, "base_projectile_speed_+%")) / 100.0;

    /// <summary>A monster magma orb's first landing, in cells from the caster (0x141b80fa0): its aim's, pushed out to 9 and
    /// held within 50 cells, both by the shot's projectile speed.</summary>
    public static double MagmaLanding(SkillCatalog.MonsterProjectileInfo shot, double cells) =>
        Math.Clamp(cells, Math.Floor(9 * MagmaSpeed(shot) + .5), Math.Max(1, Math.Floor(50 * MagmaSpeed(shot) + .5)));

    /// <summary>Whether the variety holds a bow in its main hand (its Weapons, from MonsterVarieties
    /// MainHand_ItemClassesKey: the ItemClasses row Bow): the ranged melee attack's arrows, else a wand's bolt.</summary>
    public static bool HoldsBow(MonsterCatalog.Variety variety) =>
        variety.Weapons.Any(w => w.Slot == 0 && w.Type == Game.BowClassRow + 1);

    /// <summary>Whether the variety shoots its ranged attacks: a bow or wand by its art or its main hand's class.</summary>
    public static bool Shoots(MonsterCatalog.Variety variety) => variety.RangedWeapon || HoldsBow(variety) || HoldsWand(variety);

    /// <summary>Whether the variety holds a wand in its main hand (ItemClasses row Wand).</summary>
    public static bool HoldsWand(MonsterCatalog.Variety variety) =>
        variety.Weapons.Any(w => w.Slot == 0 && w.Type == WandClassRow.Value + 1);

    static readonly Lazy<int> WandClassRow = new(() =>
        GameServer.Core.ClientTables.TableOrNull("ItemClasses") is { } classes
            ? Enumerable.Range(0, classes.Count).FirstOrDefault(i => classes[i].TextOrNull("Id") == "Wand", -2) : -2);

    // A bow skill used without a known variety (an encounter's own move, the Labyrinth Lieutenant's) is taken to hold a bow.
    static bool BowShot(MonsterCatalog.Variety? variety) => variety is null || HoldsBow(variety);

}
