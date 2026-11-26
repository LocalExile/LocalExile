using System.Collections.Concurrent;
using GameServer.Core;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// Cached multi-skill kits, on-death skill specs, Discharge charge scaling, and kin-death charge triggers for
/// ordinary Normal/Magic/Rare monsters (and shared helpers for uniques/bosses). Templates are cached per
/// <c>(Variety.Row, Level)</c> inside a <see cref="RuleTable{TKey, TValue}"/> so hot-path monster spawning stays O(kit)
/// without re-reading client tables or allocating skill graphs, and clears automatically on module reload.
/// </summary>
public static class MonsterKits
{
    const int MaxOrdinaryKitSkills = 5;
    const double KinDeathChargeRadiusCells = 60.0;

    /// <summary>Cached un-modded template for one <c>(Variety.Row, Level)</c>.</summary>
    public sealed record CachedKit(
        IReadOnlyList<MonsterKitSkill> BaseKit,
        IReadOnlyList<DeathSkillSpec> DeathSkills,
        IReadOnlyDictionary<uint, int> AuraStats,
        IReadOnlyList<ushort> AuraRows,
        bool HasMeleeFallback);

    /// <summary>Pre-resolved on-death skill specification (Porcupine Goliath spine nova, death explosions, chilled/caustic ground, on-death curses).</summary>
    public sealed record DeathSkillSpec(
        int Effect,
        MonsterAttack BaseAttack,
        SkillCatalog.MonsterProjectileInfo? Projectile,
        double AreaRadius,
        MonsterGroundSpec? Ground,
        MonsterSkillUse? CurseUse,
        bool IsNovaProjectile,
        int ChancePct = 100);

    static readonly RuleTable<SkillCatalog, ConcurrentDictionary<(int VarietyRow, byte Level), CachedKit>> Cache = new();

    static readonly string[] ExcludedOrdinaryTokens =
    [
        "Spectre", "WhileSpectred", "EmptyAction", "Wait", "Taunt", "ChangeToStance",
        "ClaimSoldier", "ExecuteGeal", "SuicideExplosion", "Teleport", "Daemon",
        "MemoryGame", "Bond", "Mark", "Souls", "Emerge", "Retreat", "Burrow",
        "Impact", "Trigger", "NoEffects", "InAoE", "OutAoE", "Flee",
        "SpiderGodActivateLaser", "MapBoss", "Beacon",
    ];

    static bool IsGenericMelee(string? effectId) =>
        effectId is "Melee" or "MeleeAtAnimationSpeed" or "MeleeAtAnimationSpeedUnique" or "MeleeAtAnimationSpeedFireUnique";

    static bool IsExcludedOrdinaryEffect(SkillCatalog catalog, int effect, string? id)
    {
        if (effect < 0 || string.IsNullOrEmpty(id)) return true;
        if (catalog.CastsOnDeath(effect) || BossKits.IsDamageTakenTrigger(catalog, effect) || catalog.IsAura(effect))
            return true;
        if (catalog.HasType(effect, "Triggered") || catalog.HasType(effect, "RemoteMined"))
            return true;
        if (id.StartsWith("GS", StringComparison.Ordinal) || id.StartsWith("GT", StringComparison.Ordinal)
            || id.StartsWith("EAS", StringComparison.Ordinal) || id.StartsWith("GA", StringComparison.Ordinal))
            return true;
        for (int i = 0; i < ExcludedOrdinaryTokens.Length; i++)
            if (id.Contains(ExcludedOrdinaryTokens[i], StringComparison.Ordinal)) return true;
        // Skip skills flagged skill_unusable (they are script/death-only).
        if (catalog.EffectStats(effect, 1).Any(s => s.Item1 == Stat.SkillUnusable && s.Item2 != 0))
            return true;
        return false;
    }

    /// <summary>Returns the cached template for <paramref name="variety"/> at <paramref name="level"/> (built once per
    /// catalog load and cleared automatically on module reload).</summary>
    public static CachedKit TemplateFor(Player player, MonsterCatalog.Variety variety, byte level)
    {
        var catalog = SkillsFor(player);
        var map = Cache.GetValue(catalog, static _ => new ConcurrentDictionary<(int, byte), CachedKit>());
        return map.GetOrAdd((variety.Row, level), _ => BuildTemplate(player, catalog, variety, level));
    }

    /// <summary>Builds a modded multi-skill kit for an ordinary hostile monster, or empty if the variety only has a
    /// single plain attack matching <paramref name="defaultEffect"/> (which stays on the fast single-attack path).</summary>
    public static (IReadOnlyList<MonsterKitSkill> Kit, IReadOnlyDictionary<uint, int> AuraStats, IReadOnlyList<ushort> AuraRows)
        ForOrdinary(Player player, MonsterCatalog.Variety variety, byte level, int defaultEffect, MonsterModEffects mods,
            double mapBossDamage = 1.0, bool applyAuraStats = true)
    {
        var template = TemplateFor(player, variety, level);
        if (template.BaseKit.Count == 0 || (template.BaseKit.Count == 1 && template.BaseKit[0].Effect == defaultEffect))
            return ([], template.AuraStats, template.AuraRows);
        var effectiveMods = applyAuraStats && template.AuraStats.Count > 0 ? mods.Combine(template.AuraStats) : mods;
        return (ApplyMods(template.BaseKit, effectiveMods, level, mapBossDamage), template.AuraStats, template.AuraRows);
    }

    /// <summary>Instantiates a cached kit with a live monster's <see cref="MonsterModEffects"/> (speed, damage,
    /// additional projectiles, AoE scaling) in O(kit.Count) without touching client tables.</summary>
    static IReadOnlyList<MonsterKitSkill> ApplyMods(IReadOnlyList<MonsterKitSkill> baseKit, MonsterModEffects mods,
        byte level, double mapBossDamage)
    {
        var result = new MonsterKitSkill[baseKit.Count];
        int extraProj = mods.AdditionalProjectiles;
        double aoeScale = mods.AreaScaleFactor;
        for (int i = 0; i < baseKit.Count; i++)
        {
            var entry = baseKit[i];
            var atk = entry.Attack;
            var damage = atk.Damage;
            if (!damage.IsZero)
            {
                if (mapBossDamage != 1.0) damage = damage.Scale(mapBossDamage);
                damage = mods.Hit(damage, level);
            }
            double seconds = atk.Summon is not null ? atk.Seconds : mods.AttackSeconds(atk.Seconds, atk.Spell);
            var proj = atk.Projectile;
            if (proj is not null && (extraProj > 0 || aoeScale != 1.0))
            {
                int nextCount = Math.Max(1, proj.Count) + extraProj;
                double nextSpread = nextCount > 1 && proj.SpreadRadians == 0 ? 0.45 : proj.SpreadRadians;
                proj = proj with
                {
                    Count = nextCount,
                    SpreadRadians = nextSpread,
                    AreaRadius = proj.AreaRadius > 0 ? proj.AreaRadius * aoeScale : proj.AreaRadius,
                };
            }
            result[i] = entry with
            {
                Attack = atk with
                {
                    Damage = damage,
                    Seconds = Math.Max(0.25, seconds),
                    Projectile = proj,
                }
            };
        }
        return result;
    }

    static CachedKit BuildTemplate(Player player, SkillCatalog catalog, MonsterCatalog.Variety variety, byte level)
    {
        var effects = variety.GrantedEffects;
        if (effects is not { Count: > 0 })
            return new([], [], EmptyStats, [], false);

        var (auraStats, auraRows) = MonsterSkills.Auras(catalog, variety, level);
        var deathSkills = BuildDeathSkills(player, catalog, variety, level, effects);

        // Single-effect varieties stay on the ordinary single-attack path (zero extra kit overhead).
        if (effects.Count == 1)
            return new([], deathSkills, auraStats, auraRows, false);

        // Classify usable effects into priority buckets:
        //   0 = primary iconic caster/ranged/trap/summon/totem offensive skill (beats generic Melee!)
        //   1 = generic melee fallback (Melee / MeleeAtAnimationSpeed)
        //   2 = signature specials: movement attacks (Shield Charge, Leap Slam, Flicker Strike, Whirling Blades),
        //       curses, rally/buffs (MassFrenzy, MassPower, Enduring Cry, Molten Shell, Lightning Thorns), Discharge
        var primaries = new List<(int Priority, MonsterKitSkill Skill)>();
        MonsterKitSkill? meleeFallback = null;
        var seen = new HashSet<int>();

        foreach (int effect in effects)
        {
            if (!seen.Add(effect)) continue;
            string? id = catalog.EffectId(effect);
            if (IsExcludedOrdinaryEffect(catalog, effect, id)) continue;

            // 1. Utility / Movement / Curse / Self-buff / Rally skills (MonsterSkills.UseOf)
            if (MonsterSkills.UseOf(catalog, effect, level) is { } use)
            {
                // Skip non-rally, non-curse, non-travel self-buffs that have no stats and no buff icon (e.g. empty script spells).
                if (use.Shape == MonsterSkillShape.Self && use.RallyCharge is null && use.BuffRow == 0 && use.Stats.Count == 0)
                    continue;
                // Skip corpse skills that require raising minions unless they are damaging corpse explosions.
                if (use.Shape == MonsterSkillShape.Corpse) continue;

                int effectLevel = catalog.MaxLevelAt(effect, level);
                double cooldown = catalog.CooldownMs(effect, effectLevel) / 1000d;
                var built = BossKits.AttackFor(player, variety, level, effect, 0x4000) with { Effect = effect };

                // Ensure signature travel attacks (Shield Charge, Leap Slam, Flicker Strike, Whirling Blades) have
                // proper range gating and cooldowns so packs engage dynamically without spamming movement every swing.
                double reach = use.Shape == MonsterSkillShape.Travel ? MonsterSkills.TravelReach
                    : use.Shape == MonsterSkillShape.Curse
                        ? Math.Max(built.Range, Math.Min(variety.AggroRange, 45))
                        : Math.Max(variety.AggroRange, built.Range);
                double minRange = use.Shape == MonsterSkillShape.Travel ? MonsterSkills.TravelMinRange : use.MinRange;
                if (use.Shape == MonsterSkillShape.Travel)
                {
                    if (BossKits.AreaRadius(catalog, effect, level) is > 0 and var areaR)
                        built = built with { AreaRadius = areaR };
                    else if (id is not null && id.Contains("LeapSlam", StringComparison.Ordinal))
                        built = built with { AreaRadius = 15 };
                    cooldown = Math.Max(cooldown, 5.0);
                }
                else if (use.RallyCharge is not null)
                {
                    cooldown = Math.Max(cooldown, 8.0);
                }
                else if (use.Shape == MonsterSkillShape.Curse)
                {
                    cooldown = Math.Max(cooldown, 8.0);
                }
                else
                {
                    cooldown = Math.Max(cooldown, use.Seconds + 3.0);
                }

                var attack = built with { Use = use with { MinRange = minRange }, Range = reach };
                int prio = use.RallyCharge is not null ? 2 : use.Shape == MonsterSkillShape.Curse ? 3 : 4;
                primaries.Add((prio, new MonsterKitSkill(effect, attack, cooldown)));
                continue;
            }

            // 2. Direct offensive / summon / trap / totem skills
            if (MonsterSkills.IsCorpseSkill(effect)) continue;
            if (!(catalog.IsAttack(effect) || catalog.IsSpell(effect) || catalog.IsProjectile(effect))) continue;

            int lvl = catalog.MaxLevelAt(effect, level);
            double cd = catalog.CooldownMs(effect, lvl) / 1000d;
            var rawAttack = BossKits.AttackFor(player, variety, level, effect, 0x4000) with { Effect = effect };

            // Generic melee fallback: keep separately so iconic spells/ranged/summons win slot 0.
            if (IsGenericMelee(id))
            {
                // Bow/wand varieties whose melee row is actually their ranged weapon attack keep Ranged = true.
                if (variety.RangedWeapon && catalog.IsAttack(effect))
                    rawAttack = rawAttack with { Ranged = true, Range = Math.Max(variety.AttackRange, 35) };
                meleeFallback ??= new MonsterKitSkill(effect, rawAttack, 0);
                continue;
            }

            // Shape the skill using BossKits.Shaped (handles traps, totems, summons, projectiles, areas, chains).
            MonsterAttack? shaped;
            if (rawAttack.Summon is { Effect: >= 0 } totem)
            {
                if (BossKits.BodyAttack(player, Monsters.Find(totem.Variety.ToString()), level, totem.Effect, 0x4000) is null)
                    continue;
                shaped = rawAttack;
                cd = Math.Max(cd, Math.Max(totem.CooldownSeconds, 8.0));
            }
            else if (rawAttack.Summon is not null)
            {
                shaped = rawAttack;
                cd = Math.Max(cd, Math.Max(rawAttack.Summon.CooldownSeconds, 6.0));
            }
            else
            {
                shaped = BossKits.Shaped(player, variety, level, effect, rawAttack);
                if (shaped is null && rawAttack.Spell && !rawAttack.Damage.IsZero && !catalog.HasType(effect, "ProjectilesFromUser"))
                {
                    // Non-projectile monster spells without an explicit Area tag (e.g. SeaWitchScreech) land as an area hit.
                    shaped = rawAttack with { AreaRadius = 16, Range = Math.Min(variety.AggroRange, 28) };
                }
            }
            if (shaped is null || !BossKits.Acts(shaped)) continue;

            // Cap unmodified ranged spell reach to on-screen distance (consistent with Monsters.MonsterSpellReach).
            bool selfBurn = SkillCatalog.ActionTypeId(effect) is "RighteousFire" or "InvisibleFire";
            if (selfBurn && shaped.AreaRadius > 0)
                shaped = shaped with { AreaAtSelf = true, Range = shaped.AreaRadius, Ranged = false };
            else if (shaped.Spell && shaped.Ranged && shaped.Summon is null && shaped.Range > 45)
                shaped = shaped with { Range = Math.Min(shaped.Range, shaped.Projectile is not null ? 45 : 32) };

            if (shaped.IsDischarge)
            {
                cd = Math.Max(cd, 4.0);
                primaries.Add((2, new MonsterKitSkill(effect, shaped, cd)));
            }
            else if (selfBurn)
            {
                // Self-centered burning pulse (e.g. WickerManRighteousFire): only fires inside its own radius on a
                // cooldown so Wicker Men close in and swing WickerManMoltenStrike rather than spamming AoE nukes.
                cd = Math.Max(cd, 2.5);
                primaries.Add((2, new MonsterKitSkill(effect, shaped, cd)));
            }
            else if (shaped.Summon is not null || shaped.Trap is not null)
            {
                if (shaped.Trap is not null) cd = Math.Max(cd, 4.0);
                primaries.Add((1, new MonsterKitSkill(effect, shaped, cd)));
            }
            else if (shaped.Ranged || shaped.Spell || shaped.Projectile is not null || shaped.AreaRadius > 0)
            {
                // Iconic zero-cooldown or short-cooldown caster/ranged primary skill (Spark, Arc, Flamebearer,
                // Fireball, Split Arrow, Burning Arrow, Caustic Arrow, Delayed Blast, Shock Nova, etc.)!
                primaries.Add((0, new MonsterKitSkill(effect, shaped, Math.Max(0, cd))));
            }
            else
            {
                // Non-generic melee attack (Cleave, Ground Slam, Puncture, Molten Strike, etc.)
                primaries.Add((1, new MonsterKitSkill(effect, shaped, Math.Max(0, cd))));
            }
        }

        if (primaries.Count == 0)
            return new([], deathSkills, auraStats, auraRows, meleeFallback is not null);

        primaries.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));

        // Assemble final kit with sequential 0x4000+i SkillIds.
        // If the monster has an iconic ranged/caster primary (Priority == 0 and no cooldown, or Trap/Summon),
        // put it FIRST (ahead of generic MeleeAtAnimationSpeed) so casters/archers lead with their signature skill!
        // If the monster is a melee + movement/special archetype (e.g. Rhoa = Melee + ShieldCharge, Goatman = Melee +
        // LeapSlam, Grappler = Melee + FlickerStrike + Discharge), put Melee at slot 0 and specials after it!
        bool hasPrimaryOffensive = primaries[0].Priority == 0;
        var ordered = new List<MonsterKitSkill>(Math.Min(MaxOrdinaryKitSkills, primaries.Count + (meleeFallback is not null ? 1 : 0)));

        if (!hasPrimaryOffensive && meleeFallback is not null)
            ordered.Add(meleeFallback);

        foreach (var (_, skill) in primaries)
        {
            if (ordered.Count >= MaxOrdinaryKitSkills - (hasPrimaryOffensive && meleeFallback is not null ? 1 : 0))
                break;
            ordered.Add(skill);
        }

        if (hasPrimaryOffensive && meleeFallback is not null && ordered.Count < MaxOrdinaryKitSkills)
            ordered.Add(meleeFallback);

        // Only use a multi-skill kit if we actually have >= 2 distinct skills OR if our single chosen iconic skill
        // differs from what single-attack MavenNucleus.CombatEffect would pick (e.g. Voidbearer / Blackguard Mage).
        var finalKit = new MonsterKitSkill[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            ushort sid = (ushort)(0x4000 + i);
            finalKit[i] = ordered[i] with { Attack = ordered[i].Attack with { SkillId = sid } };
        }

        return new(finalKit, deathSkills, auraStats, auraRows, meleeFallback is not null);
    }

    static readonly IReadOnlyDictionary<uint, int> EmptyStats = new Dictionary<uint, int>();

    // ---- CastsOnDeath resolution & execution -------------------------------------------------------------------------

    static IReadOnlyList<DeathSkillSpec> BuildDeathSkills(Player player, SkillCatalog catalog, MonsterCatalog.Variety variety,
        byte level, IReadOnlyList<int> effects)
    {
        List<DeathSkillSpec>? list = null;
        foreach (int effect in effects)
        {
            if (effect < 0 || !catalog.CastsOnDeath(effect)) continue;
            string? id = catalog.EffectId(effect);
            if (id is null || id.Contains("Beacon", StringComparison.Ordinal)
                || id.Contains("Support", StringComparison.Ordinal))
                continue;
            // Skip summon-on-death skills handled by specialized controllers unless they are simple damaging/ground/curse skills.
            if (catalog.HasType(effect, "Minion") || id.Contains("Summon", StringComparison.Ordinal))
                continue;

            var attack = BossKits.AttackFor(player, variety, level, effect, SkillCatalog.FirstSkillId) with { Effect = effect };
            var stats = GolemData.Skill(catalog, effect, level).Stats;
            int Get(uint key) => stats.GetValueOrDefault(key);
            int chancePct = Math.Clamp(Math.Max(Get(Stat.CastOnDeathPct), Get(Stat.AttackOnDeathPct)), 1, 100);

            // Check for on-death curse (e.g. MonsterConductivityOnDeath)
            if (catalog.HasType(effect, "Hex") && MonsterSkills.UseOf(catalog, effect, level) is { } curseUse)
            {
                (list ??= []).Add(new DeathSkillSpec(effect, attack, null, 28, null, curseUse, false, chancePct));
                continue;
            }

            // Check for projectile death nova / shot (e.g. SpikerSpineDeathNova, MonsterIceSpearDeathNova)
            bool isNova = stats.GetValueOrDefault(Stat.ProjectilesNova) != 0 || id.Contains("Nova", StringComparison.Ordinal);
            SkillCatalog.MonsterProjectileInfo? proj = catalog.MonsterProjectile(effect, includeWeaponAttacks: true)
                ?? BossKits.ArtShot(catalog, effect, level);
            if (proj is not null)
            {
                int count = Get(Stat.NumberOfProjectilesOverride) is > 0 and var fixedCount ? fixedCount
                    : Math.Max(1, Get(Stat.BaseNumberOfProjectiles)) + Get(Stat.NumberOfAdditionalProjectiles);
                proj = proj with
                {
                    Count = Math.Clamp(Math.Max(proj.Count, count), 1, 16),
                    SpreadRadians = isNova ? Math.Tau : (proj.SpreadRadians > 0 ? proj.SpreadRadians : Math.PI / 4),
                    Variation = 0, // Detached server-created projectiles must be drawn by the server (HideVisual = false).
                };
                if (attack.Damage.IsZero)
                {
                    var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
                    int more = Get(Stat.ActiveSkillDamagePlusPctFinal);
                    attack = attack with { Damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(Math.Max(0.25, 1 + more / 100.0)) };
                }
                attack = attack with { Ranged = true, Projectile = proj, Range = Math.Max(attack.Range, 45) };
                (list ??= []).Add(new DeathSkillSpec(effect, attack, proj, 0, null, null, isNova, chancePct));
                continue;
            }

            // Check for ground effect on death (e.g. MonsterCastGroundEffectChilled, caustic clouds)
            MonsterGroundSpec? ground = null;
            var shaped = BossKits.Shaped(player, variety, level, effect, attack);
            if (shaped?.Ground is { } g) ground = g;
            else if (Get(Stat.GroundIceArtVariation) is > 0 and var iceVar && GroundKindOf("ice", iceVar) is { } iceKind)
            {
                double dur = Math.Max(2.0, Get(Stat.BaseSkillEffectDuration) / 1000.0);
                ground = new("ice", iceVar, new Dictionary<uint, int> { [Stat.BaseMovementVelocityPlusPct] = -15 },
                    Math.Max(12, iceKind.ArtCells), dur);
            }
            else if (Get(Stat.GroundCausticArtVariation) is > 0 and var causticVar && GroundKindOf("caustic", causticVar) is { } causticKind)
            {
                double dur = Math.Max(2.0, Get(Stat.BaseSkillEffectDuration) / 1000.0);
                int dps = Get(Stat.BaseChaosDamageToDealPerMinute);
                var gStats = dps > 0 ? new Dictionary<uint, int> { [Stat.BaseChaosDamageToDealPerMinute] = dps } : new Dictionary<uint, int>();
                ground = new("caustic", causticVar, gStats, Math.Max(12, causticKind.ArtCells), dur);
            }

            double radius = shaped?.AreaRadius is > 0 and var sr ? sr
                : BossKits.AreaRadius(catalog, effect, level) ?? (ground?.Radius ?? 16);
            if (attack.Damage.IsZero && ground is null) continue;
            attack = attack with { AreaRadius = radius, AreaAtSelf = true, Ground = ground, Secondary = id.Contains("Explode", StringComparison.Ordinal) };
            (list ??= []).Add(new DeathSkillSpec(effect, attack, null, radius, ground, null, false, chancePct));
        }
        return list is { Count: > 0 } ? list : [];
    }

    /// <summary>Executes all <c>CastsOnDeath</c> skills carried by <paramref name="m"/> (Porcupine Goliath spine nova,
    /// death explosions, chilled/caustic ground, on-death curses) once on death, safely guarded against retired/invalid
    /// states and running on the area clock.</summary>
    public static async Task ExecuteDeathSkills(Player player, uint id, SpawnedMonster m, (int X, int Y) at)
    {
        if (m.DeathSkillsTriggered) return;
        m.DeathSkillsTriggered = true;
        if (!CurrentGameWork || player.Closed || player.LeavingArea
            || !m.Hostile || m.CreatedAsCorpse || m.CorpseConsumed || m.Variety is not { } variety)
            return;
        // Skip CastsOnDeath on monsters flagged NoRewards when they are retired/boss-phase escapes without a real death.
        if (BossMods.NoRewards(variety)) return;

        var template = TemplateFor(player, variety, m.Level);
        if (template.DeathSkills.Count == 0) return;
        var instance = player.Instance;
        bool Still() => CurrentGameWork && !player.Closed && !player.LeavingArea && ReferenceEquals(player.Instance, instance);

        bool anyLivingPlayer = false;
        foreach (var peer in player.Frames.Players())
            if (!peer.Closed && !peer.LeavingArea && peer.PlayerLife > 0 && !InGracePeriod(peer))
            { anyLivingPlayer = true; break; }

        var rng = AreaRng(player);
        foreach (var spec in template.DeathSkills)
        {
            if (!Still()) return;
            if (spec.Ground is null && !anyLivingPlayer) continue;
            if (spec.ChancePct < 100 && rng.Next(100) >= spec.ChancePct) continue;
            var attack = spec.BaseAttack;
            if (!attack.Damage.IsZero)
                attack = attack with { Damage = m.Mods.Effects.Hit(attack.Damage, m.Level) };
            ushort seq = m.NextSequence();

            if (spec.CurseUse is { } curse)
            {
                double curseRadius = spec.AreaRadius * m.Effects.AreaScaleFactor;
                var curseStats = MonsterSkills.CursePlayerStats(curse.Stats);
                double now = AreaTime(player);
                foreach (var peer in player.Frames.Players())
                {
                    if (peer.Closed || peer.LeavingArea || peer.PlayerLife == 0 || InGracePeriod(peer)) continue;
                    if (Dist(at, peer.Walk.Position(now)) <= curseRadius)
                        await AddPlayerDebuff(peer, curse.BuffRow, id, curseStats, curse.Seconds);
                }
                continue;
            }

            if (spec.Projectile is { } proj)
            {
                if (m.Mods.Effects.AdditionalProjectiles > 0)
                    proj = proj with { Count = Math.Min(16, proj.Count + m.Mods.Effects.AdditionalProjectiles) };
                LaunchDetachedMonsterProjectile(player, id, m, attack with { Projectile = proj }, proj, at, Still, seq);
                continue;
            }

            if (spec.AreaRadius > 0 && (!attack.Damage.IsZero || spec.Ground is not null))
            {
                double scaledRadius = spec.AreaRadius * m.Effects.AreaScaleFactor;
                await MonsterDetachedAreaHit(player, id, m, attack, at, scaledRadius, seq, Still);
            }
        }
    }

    // ---- Monster Discharge live charge scaling -----------------------------------------------------------------------

    /// <summary>Computes a monster's live <c>MonsterDischarge</c> damage from its active Endurance (Fire), Frenzy (Cold)
    /// and Power (Lightning) charges, consuming all charges from <paramref name="m"/>. Returns null if the monster holds
    /// no charges.</summary>
    public static MonsterAttack? PrepareDischargeContactLocked(Player player, uint id, SpawnedMonster m, MonsterAttack a)
    {
        var (endurance, frenzy, power) = MonsterChargesOf(m);
        if (endurance + frenzy + power <= 0) return null;
        var catalog = SkillsFor(player);
        var damage = DamagePacket.Empty;
        if (a.Effect is int effect)
        {
            var raw = GolemData.Skill(catalog, effect, m.Level).Stats;
            int Get(uint key) => raw.GetValueOrDefault(key);
            if (endurance > 0)
            {
                int fMin = Get(Stat.SpellMinimumBaseFireDamagePerRemovableEnduranceCharge);
                int fMax = Get(Stat.SpellMaximumBaseFireDamagePerRemovableEnduranceCharge);
                if (fMax > 0) damage = damage.Add(DamageType.Fire, new(fMin * endurance, Math.Max(fMin, fMax) * endurance));
            }
            if (frenzy > 0)
            {
                int cMin = Get(Stat.SpellMinimumBaseColdDamagePerRemovableFrenzyCharge);
                int cMax = Get(Stat.SpellMaximumBaseColdDamagePerRemovableFrenzyCharge);
                if (cMax > 0) damage = damage.Add(DamageType.Cold, new(cMin * frenzy, Math.Max(cMin, cMax) * frenzy));
            }
            if (power > 0)
            {
                int lMin = Get(Stat.SpellMinimumBaseLightningDamagePerRemovablePowerCharge);
                int lMax = Get(Stat.SpellMaximumBaseLightningDamagePerRemovablePowerCharge);
                if (lMax > 0) damage = damage.Add(DamageType.Lightning, new(lMin * power, Math.Max(lMin, lMax) * power));
            }
        }
        if (damage.IsZero) damage = a.Damage.Scale(endurance + frenzy + power);
        else damage = m.Mods.Effects.Hit(damage, m.Level);
        ClearMonsterChargesLocked(player.Area, id, m);
        return a with { Damage = damage };
    }

    // ---- On-Kin-Death Frenzy & Power Charge Grants -------------------------------------------------------------------

    /// <summary>When a hostile monster dies, nearby living kin carrying <c>monster_gain_frenzy_charge_on_kin_death</c>
    /// (Dread Primates / Chieftain apes / Maternal Rhex) or <c>monster_gain_power_charge_on_kin_death</c> (Undying
    /// Grapplers) gain a Frenzy or Power charge via <see cref="Game.GrantMonsterChargeLocked"/>.</summary>
    public static void GrantKinDeathChargesLocked(GameArea area, uint deadId, SpawnedMonster dead, double now)
    {
        if (dead.KinDeathChargesGranted || !dead.Hostile || dead.Variety is not { } deadVariety) return;
        dead.KinDeathChargesGranted = true;
        var origin = dead.Walk.Position(now);
        double r2 = KinDeathChargeRadiusCells * KinDeathChargeRadiusCells;
        int deadPack = dead.PackId;
        int deadType = deadVariety.MonsterType;
        string deadFolder = FolderOf(deadVariety.Id);

        foreach (var (allyId, ally) in area.MonstersNear(origin, (int)KinDeathChargeRadiusCells + 4))
        {
            if (allyId == deadId || !ally.Hostile || ally.Dead || ally.Life == 0 || ally.Variety is not { } allyVariety)
                continue;
            int frenzyOnKin = ally.Effects[Stat.MonsterGainFrenzyChargeOnKinDeath];
            int powerOnKin = ally.Effects[Stat.MonsterGainPowerChargeOnKinDeath];
            if (frenzyOnKin <= 0 && powerOnKin <= 0) continue;

            // Kin match: same pack, same MonsterType, or same variety folder (e.g. Monkeys / Grappler).
            bool sameKin = (deadPack > 0 && ally.PackId == deadPack)
                || (deadType >= 0 && allyVariety.MonsterType == deadType)
                || (deadFolder.Length > 0 && FolderOf(allyVariety.Id).Equals(deadFolder, StringComparison.OrdinalIgnoreCase));
            if (!sameKin) continue;

            var at = ally.Walk.Position(now);
            double dx = at.X - origin.X, dy = at.Y - origin.Y;
            if (dx * dx + dy * dy > r2) continue;

            if (frenzyOnKin > 0) GrantMonsterChargeLocked(area, allyId, ally, ChargeType.Frenzy, now);
            if (powerOnKin > 0) GrantMonsterChargeLocked(area, allyId, ally, ChargeType.Power, now);
        }
    }

    static string FolderOf(string id)
    {
        int slash = id.LastIndexOf('/');
        return slash > 0 ? id[..slash] : "";
    }
}
