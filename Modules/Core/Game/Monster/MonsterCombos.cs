using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// General <c>EmptyAction*</c> combo resolver and runtime clip-event runner (PR #1618 S3).
/// <para>
/// Binary RE of Client/Game.exe 3.29.3c (<c>0x1434bbf28</c>, <c>0x1434b64b0</c>, <c>0x141e9a160</c>, <c>0x14219c1f0</c>)
/// proves clip-event → child-skill binding is 100% server-driven: the server starts the parent <c>EmptyAction*</c> via
/// <c>S→C 0x1ab</c> (occupying <c>Actor+0x1b0</c>) and fires bound child skills at clip-event timestamps via
/// <c>S→C 0x1ce</c> (<c>flags |= 0x80</c>, optional <c>0x2000</c> custom origin) for visual/geometry/beam/melee actions
/// or directly via authoritative <c>S→C 0x2bb</c> for pure projectiles/mortars/summons/ground effects.
/// </para>
/// </summary>
public static class MonsterCombos
{
    /// <summary>Base Actor skill id reserved for S3 triggered combo children (<c>0x4400+</c>), strictly above kit slots
    /// (<c>0x4000..0x43ff</c>) and stage skills (<c>0x0103</c>) so binary search in <c>Actor+0x6f0..0x6f8</c>
    /// (<c>0x14219c24d</c>) stays sorted and collision-free even with <c>BossKits.WithExtra</c>.</summary>
    public const ushort ChildSkillIdBase = 0x4400;

    /// <summary>World units per grid cell (250 / 23 ≈ 10.869565, matching client <c>0x1436ae440</c>).</summary>
    const double WorldUnitsPerCell = 250.0 / 23.0;

    /// <summary>Resolved Tier-2 combo table for one <see cref="MonsterCatalog.Variety"/>, precomputed once per catalog.</summary>
    public sealed record VarietyCombos(
        IReadOnlyDictionary<int, ResolvedComboParent> Parents,
        IReadOnlySet<int> BoundChildren)
    {
        public static readonly VarietyCombos Empty = new(
            new Dictionary<int, ResolvedComboParent>(),
            new HashSet<int>());

        public bool HasAny => Parents.Count > 0;
    }

    /// <summary>Pre-resolved parent <c>EmptyAction*</c> combo metadata (clip, control events, and ordered child triggers).</summary>
    public sealed record ResolvedComboParent(
        int ParentEffect,
        string ClipAnimation,
        int ClipLengthMs,
        IReadOnlyList<ResolvedComboEvent> Events,
        IReadOnlyList<MonsterComboControl> Controls);

    /// <summary>One deduplicated clip event bound to a child <c>GrantedEffects</c> row.</summary>
    public sealed record ResolvedComboEvent(
        string EventName,
        int Ms,
        double Fraction,
        int ChildEffect,
        MonsterComboDispatch Dispatch,
        ContactPoint? Point);

    static readonly RuleTable<SkillCatalog, ConcurrentDictionary<(int VarietyRow, int HookVersion), VarietyCombos>> Cache = new();

    static readonly HashSet<string> EngineControlTypes = new(StringComparer.Ordinal)
    {
        "StartFaceEventType", "EndFaceEventType",
        "MoveEventType", "MoveEndEventType",
        "InterruptSkillEventType", "DeathMoveEventType",
        "StartRotateEventType", "EndRotateEventType",
        "MeleeTrailEventType", "MeleeTrailEndEventType", "RulesetEventType",
    };

    static readonly HashSet<string> NonCombatExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "face", "footstep", "remove_collidability", "add_collidability",
        "unhighlightable", "highlightable", "stunnable", "no_stuns", "nostun",
        "resume", "unblock", "block", "allow_cancel", "cancel",
        "add_arrow", "remove_arrow", "show_weapon", "hide_weapon",
        "anim_leave", "anim_hit", "leave_ground", "hit_ground",
        "submerge", "emerge", "finish_emerge", "disappear", "appear", "reappear",
        "phase", "im_outta_here", "facetarget", "face_target", "stop_track",
        "start_track", "flee_target", "teleport_to_ritual", "projectile_locator",
        "rootfx", "spawnbird", "birdepk", "hideboss", "dissolve", "delete",
        "starttimer", "spend_mana", "start_face", "end_face", "move",
        "end", "end_breath", "end_beam", "end_ruckus", "stop", "done",
        "untargetable", "targetable", "invulnerable", "vulnerable",
        "invincible", "mortal", "hidden", "unhidden", "hide", "unhide",
        "reset_facing", "lock_facing", "unlock_facing",
        "camera_shake", "shake", "sound", "audio", "vo", "dialogue",
        "idle", "pause", "unpause", "loop", "reset",
        "roar", "indicate", "telegraph", "warn", "warning", "buff", "enrage",
        "shout", "scream", "yell", "windup", "recover", "recovery", "charge",
        "identify", "addepk", "removeepk", "addfx", "removefx",
        "start_tracking", "stop_tracking", "tele_out", "tele_in",
    };

    static readonly string[] NonCombatPrefixes =
    [
        "add_", "remove_", "fade_cast_", "show_", "hide_", "play_", "stop_",
        "enable_", "disable_", "set_state_", "state_", "sound_", "audio_",
        "vfx_", "fx_", "epk_", "light_", "camera_", "anim_", "weapon_",
        "start_face", "end_face", "end_", "finish_", "clear_", "reset_",
        "toggle_", "attach_", "detach_", "spawn_fx", "trail_",
        "glow_", "unglow_", "check_", "pre_", "post_", "warn_",
        "start_track", "stop_track", "move_",
    ];

    static readonly string[] NonCombatSuffixes =
    [
        "_epk", "_fx", "_vfx", "_sfx", "_sound", "_audio", "_light",
        "_particle", "_decal", "_trail", "_camera", "_shake", "_end",
        "_visible", "_invisible", "_hidden", "_unhidden", "_arrow",
        "_face", "_tracking", "epk",
    ];

    static readonly HashSet<string> UtilityParentTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "emerge", "submerge", "burrow", "unburrow", "retreat", "flee",
        "morph", "transform", "transition", "phase", "leave", "intro", "outro",
        "taunt", "roar", "idle", "wait", "stasis", "freeze", "unfreeze",
        "sleep", "wake", "rise", "stand", "sit", "kneel", "death", "die",
        "dodge", "move", "walk", "run", "teleport", "blink", "vanish",
        "reappear", "disappear", "enrage", "buff", "stance", "activate",
        "deactivate", "open", "close", "channel", "breatheend", "end180",
        "end270", "end360", "armblock", "block", "shield", "guard", "heal",
        "summonportal", "portal",
    };

    static readonly HashSet<string> UtilityHardTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "buff", "enrage", "taunt", "heal", "roar", "stasis", "emerge", "submerge",
        "morph", "leave", "flee", "retreat", "burrow", "frenzy", "armour", "armor",
        "shield", "aura",
    };

    static readonly HashSet<string> CombatOverrideTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "slam", "swipe", "slash", "combo", "shot", "shoot", "proj", "projectile",
        "beam", "blast", "wave", "nova", "cascade", "storm", "barrage", "snipe",
        "mortar", "reave", "screech", "stomp", "whirl", "spin", "spinning",
        "death", "lance", "spear", "bomb", "vomit", "breath", "breathe", "cleave",
        "fireball", "bolt", "spike", "spikes", "strike", "smash", "crush", "summon",
        "totem", "effigy", "hex", "vine", "vines", "wall", "orb", "orbs", "ruckus",
        "circle", "impact", "explode", "explosion", "quake", "earthquake", "boulder",
    };

    static readonly HashSet<string> TriggeredActionTypes = new(StringComparer.Ordinal)
    {
        "GeometryAttack", "GeometrySpell", "GeometryTrigger",
        "EffectDrivenSpell", "EffectDrivenAttack",
        "TriggerBeam", "MultiTriggerBeam", "ExecuteGEAL",
        "ExpandingPulse", "FireStorm", "DelayedSkillTrigger",
        "MonsterSlam", "ArchitectComboStomp", "Melee",
    };

    static readonly HashSet<string> ProjectileActionTypes = new(StringComparer.Ordinal)
    {
        "MonsterProjectileSpell", "MonsterProjectileWeapon",
        "MonsterMortar", "MonsterMortarAttack", "Fireball", "LunarisGlaiveProjectile",
        "GeometryProjectiles", "GeometryProjectilesAttack",
        "DeceleratingProjectile", "MonsterFadingProjectileSpell",
        "MonsterSnipe", "MonsterCleave", "MonsterShockNova",
        "MonsterFlameblast", "MonsterSpark", "MonsterIceSpear",
        "MonsterLightningWarp", "MonsterColdSnap", "MonsterFrostWall",
    };

    static readonly HashSet<string> ServerSpawnActionTypes = new(StringComparer.Ordinal)
    {
        "SummonSpecificMonsters", "CastGroundEffect", "SpawnObject", "MoveDaemon",
    };

    static readonly HashSet<string> Strict2AActionTypes = new(StringComparer.Ordinal)
    {
        "GeometryAttack", "GeometrySpell", "GeometryTrigger",
        "EffectDrivenSpell", "EffectDrivenAttack",
        "TriggerBeam", "MultiTriggerBeam", "ExecuteGEAL",
        "GeometryProjectiles", "GeometryProjectilesAttack",
        "DelayedSkillTrigger", "ExpandingPulse", "ArchitectComboStomp",
    };

    static readonly HashSet<string> BeamActionTypes = new(StringComparer.Ordinal)
    {
        "TriggerBeam", "MultiTriggerBeam", "GeometrySpell", "EffectDrivenSpell",
    };

    static readonly HashSet<string> GeoActionTypes = new(StringComparer.Ordinal)
    {
        "GeometryAttack", "GeometrySpell", "GeometryTrigger",
        "EffectDrivenSpell", "EffectDrivenAttack", "MonsterSlam", "ExpandingPulse", "FireStorm",
    };

    static readonly string[] ExcludedChildSubstrings =
    [
        "EmptyAction", "Spectre", "WhileSpectred", "ChangeToStance",
        "ClaimSoldier", "SuicideExplosion", "MemoryGame", "Bond", "Mark",
        "Souls", "SpiderGodActivateLaser", "WalkEmerge", "Emerge", "Retreat",
        "Burrow", "Flee", "Taunt", "Wait", "DoNothing", "Death",
    ];

    static readonly HashSet<string> GenericMeleeIds = new(StringComparer.Ordinal)
    {
        "Melee", "MeleeAtAnimationSpeed", "MeleeAtAnimationSpeedUnique",
        "MeleeAtAnimationSpeedFireUnique", "MeleeAtAnimationSpeedColdUnique",
        "MeleeAtAnimationSpeedLightningUnique", "MeleeAtAnimationSpeedChaosUnique",
    };

    static readonly HashSet<string> GenericStopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "empty", "action", "spell", "attack", "loop", "monster", "boss", "map",
        "unique", "rare", "magic", "normal", "uber", "gauntlet", "standalone",
        "geometry", "trigger", "triggered", "projectile", "projectiles", "weapon",
        "mortar", "summon", "specific", "monsters", "spawn", "object", "cast",
        "ground", "effect", "driven", "geal", "execute", "daemon", "move", "melee",
        "general", "sergeant", "captain", "commander", "champion", "elite", "guard",
        "caster", "warrior", "archer", "mage", "minion", "totem", "clone", "copy",
        "var", "v", "new", "old", "test", "alt", "base", "main", "sub", "hit",
        "damage", "dmg", "skill", "ability", "combo", "multi", "single", "small",
        "medium", "large", "big", "fast", "slow", "short", "long", "epic", "super",
        "hard", "easy", "high", "low", "far", "near", "close", "range", "ranged",
        "physical", "fire", "cold", "lightning", "chaos", "ele", "elemental",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "01", "02", "03", "04",
    };

    static readonly Regex StripPrefixRegex = new(
        @"^(?:EmptyActionSpell[2-5]?|EmptyActionAttack[2-5]?|EmptyActionLoop|EmptyAction|" +
        @"EASL|EAAL|EAS[2-5]?|EAA[2-5]?|GEAL|MPS|MPW|SSM|GS|GA|GT|EDS|EDA|GP|GPA|TB|MT|" +
        @"GeometryTrigger|GeometryAttack|GeometrySpell|GeometryProjectiles(?:Attack)?|" +
        @"EffectDriven(?:Spell|Attack)|MonsterProjectile(?:Spell|Weapon)|MonsterMortar|" +
        @"SummonSpecificMonsters|TriggerBeam|CastGroundEffect|SpawnObject|MoveDaemon|" +
        @"Monster|Boss|MapBoss|Standalone|Uber|Gauntlet|Atlas|League|Legion|Harvest|Heist|" +
        @"Azmeri|Abyss|Sanctum|Expedition|Ultimatum|Betrayal|Delve|Synthesis|Faridun|Kalguur|" +
        @"Blight|Ritual|Crucible|Affliction|Necropolis|Settlers|Incursion|Bestiary|Breach)+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex OrdEventRegex = new(@"^([a-z_]+?)(\d+)$", RegexOptions.Compiled);
    static readonly Regex TrailingNumberRegex = new(@"^(.*?)(\d+)$", RegexOptions.Compiled);

    /// <summary>Maps a child skill's <c>ActionTypes.Id</c> to its binary-verified S3 wire dispatch kind.</summary>
    public static MonsterComboDispatch DispatchFor(string? actionType)
    {
        if (string.IsNullOrEmpty(actionType)) return MonsterComboDispatch.TriggeredAction;
        if (ProjectileActionTypes.Contains(actionType)) return MonsterComboDispatch.Projectile;
        if (ServerSpawnActionTypes.Contains(actionType)) return MonsterComboDispatch.ServerSpawn;
        return MonsterComboDispatch.TriggeredAction;
    }

    /// <summary>Returns the precomputed Tier-2 combo bindings for <paramref name="variety"/> (cached per catalog).</summary>
    public static VarietyCombos For(SkillCatalog catalog, MonsterCatalog.Variety? variety)
    {
        if (variety is null || variety.GrantedEffects is not { Count: > 0 }) return VarietyCombos.Empty;
        if (IsBossControllerVariety(variety)) return VarietyCombos.Empty;
        var map = Cache.GetValue(catalog, static _ => new ConcurrentDictionary<(int, int), VarietyCombos>());
        return map.GetOrAdd((variety.Row, MonsterKitHooks.ComboProviderVersion), _ => ResolveVariety(catalog, variety));
    }

    /// <summary>True when <paramref name="parentEffect"/> is an <c>EmptyAction*</c> parent with at least one bound
    /// clip-event child trigger on <paramref name="variety"/>.</summary>
    public static bool HasCombo(SkillCatalog catalog, MonsterCatalog.Variety? variety, int parentEffect) =>
        SkillCatalog.IsEmptyAction(parentEffect) && For(catalog, variety).Parents.ContainsKey(parentEffect);

    /// <summary>True when <paramref name="effect"/> is bound as a child of an active S3 combo parent on
    /// <paramref name="variety"/> and must stay out of top-level AI selection (while staying registered in <c>0x2bb</c>
    /// if dispatched via <c>0x1ce</c>). Never excludes standalone <c>Animation = NULL</c> skills like <c>Melee</c> or
    /// <c>MonsterFireball</c>.</summary>
    public static bool IsComboChild(SkillCatalog catalog, MonsterCatalog.Variety? variety, int effect) =>
        For(catalog, variety).BoundChildren.Contains(effect);

    /// <summary>Deduplicates raw <c>.ao</c>/<c>.amd</c> clip events by <c>(name, ms, roundedPos)</c>, removing exact
    /// duplicate tracks (e.g., LegionTemplarGeneral <c>projectile_shield_throw_01</c> 62 → 31, Hyrri <c>leap_attack_01</c>
    /// 30 → 10) while preserving simultaneous multi-position launches at the same millisecond.</summary>
    public static List<SkillCatalog.ClipNamedEvent> DedupeEvents(IReadOnlyList<SkillCatalog.ClipNamedEvent> rawEvents)
    {
        var result = new List<SkillCatalog.ClipNamedEvent>(rawEvents.Count);
        var seen = new HashSet<(string Name, int Ms, int Px, int Py, int Pz, bool HasPos)>();
        for (int i = 0; i < rawEvents.Count; i++)
        {
            var ev = rawEvents[i];
            string name = NormalizeEventName(ev.Name);
            bool hasPos = ev.Point is not null;
            int px = hasPos ? (int)Math.Round(ev.Point!.Value.X * 100f) : 0;
            int py = hasPos ? (int)Math.Round(ev.Point!.Value.Y * 100f) : 0;
            int pz = hasPos ? (int)Math.Round(ev.Point!.Value.Z * 100f) : 0;
            if (seen.Add((name, ev.Ms, px, py, pz, hasPos)))
                result.Add(ev);
        }
        return result;
    }

    // ---- Tier-2 Parent-Event -> Child-GE Resolution ------------------------------------------------------------------

    sealed record CandidateChild(int Effect, string Id, string ActionType, bool IsNullAnim, HashSet<string> Stem, HashSet<string> AllTokens);

    sealed record ParentClipInfo(
        int Effect,
        string Id,
        string ActionType,
        string ClipAnimation,
        int ClipLengthMs,
        List<SkillCatalog.ClipNamedEvent> CombatEvents,
        List<MonsterComboControl> Controls,
        bool IsUtility);

    static VarietyCombos ResolveVariety(SkillCatalog catalog, MonsterCatalog.Variety variety)
    {
        var effects = variety.GrantedEffects;
        if (effects is not { Count: > 0 }) return VarietyCombos.Empty;

        var parentEffects = new List<(int Effect, string Id, string ActionType)>();
        var allCandidates = new List<CandidateChild>();
        var seen = new HashSet<int>();
        var varTokens = VarietyIdentityTokens(variety.Id, variety.Name);

        foreach (int effect in effects)
        {
            if (effect < 0 || !seen.Add(effect)) continue;
            string? id = catalog.EffectId(effect);
            string? at = SkillCatalog.ActionTypeId(effect);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(at)) continue;

            if (at.StartsWith("EmptyAction", StringComparison.Ordinal))
            {
                parentEffects.Add((effect, id, at));
                continue;
            }

            if (catalog.CastsOnDeath(effect) || BossKits.IsDamageTakenTrigger(catalog, effect) || catalog.IsAura(effect))
                continue;
            if (ContainsExcludedChildSubstring(id)) continue;
            if (!TriggeredActionTypes.Contains(at) && !ProjectileActionTypes.Contains(at) && !ServerSpawnActionTypes.Contains(at))
                continue;
            bool isNullAnim = SkillCatalog.EffectAnimationName(effect) is null;
            if (at == "Melee" && (!isNullAnim || GenericMeleeIds.Contains(id)))
                continue;
            bool isStrictPayload = Strict2AActionTypes.Contains(at);
            if (!isNullAnim && !isStrictPayload)
                continue;

            allCandidates.Add(new(effect, id, at, isNullAnim, StemTokens(id, varTokens), new HashSet<string>(SplitTokens(id), StringComparer.OrdinalIgnoreCase)));
        }

        if (parentEffects.Count == 0 || (allCandidates.Count == 0 && !MonsterKitHooks.HasComboProviders))
            return VarietyCombos.Empty;

        // Prefer Animation=NULL candidates (matching client ExecuteTriggered +0x1e8=1 design); fall back to strict payload ActionTypes.
        var candidates = allCandidates.Where(static c => c.IsNullAnim).ToList();
        if (candidates.Count == 0) candidates = allCandidates;

        // Resolve S2 clip + deduped events for each EmptyAction* parent.
        var parentInfos = new List<ParentClipInfo>(parentEffects.Count);
        foreach (var (effect, id, at) in parentEffects)
        {
            string? animKey = SkillCatalog.EffectAnimationName(effect) ?? SkillCatalog.EmptyActionFallbackAnimation(effect);
            if (string.IsNullOrEmpty(animKey)) continue;
            var pool = catalog.MonsterContactPool(variety.Id, animKey);
            if (pool is not { Length: > 0 }) continue;
            var clip = pool[0];
            if (clip.LengthMs <= 0 || clip.Events.Count == 0) continue;

            var deduped = DedupeEvents(clip.Events);
            var combat = new List<SkillCatalog.ClipNamedEvent>();
            var controls = new List<MonsterComboControl>();
            for (int i = 0; i < deduped.Count; i++)
            {
                var ev = deduped[i];
                if (TryParseControlEvent(ev, out var ctrl))
                    controls.Add(ctrl);
                else if (!IsNonCombatEvent(ev))
                    combat.Add(ev);
            }
            bool utility = IsUtilityParent(id, clip.Animation);
            parentInfos.Add(new(effect, id, at, clip.Animation, clip.LengthMs, combat, controls, utility));
        }

        int activeParentCount = 0;
        for (int i = 0; i < parentInfos.Count; i++)
            if (!parentInfos[i].IsUtility && parentInfos[i].CombatEvents.Count > 0)
                activeParentCount++;

        if (activeParentCount == 0) return VarietyCombos.Empty;

        var resolvedParents = new Dictionary<int, ResolvedComboParent>();
        var boundChildren = new HashSet<int>();

        foreach (var pi in parentInfos)
        {
            if (pi.IsUtility || pi.CombatEvents.Count == 0) continue;

            // Group combat events by normalized name, preserving chronological order.
            var groups = new Dictionary<string, List<SkillCatalog.ClipNamedEvent>>(StringComparer.Ordinal);
            foreach (var ev in pi.CombatEvents)
            {
                string en = NormalizeEventName(ev.Name);
                if (!groups.TryGetValue(en, out var list)) groups[en] = list = [];
                list.Add(ev);
            }

            // Tier-1 module overrides via MonsterKitHooks.AddComboBindings (zero-cost when empty).
            if (MonsterKitHooks.HasComboProviders)
            {
                var hookBindings = MonsterKitHooks.ExtraComboBindings(variety, pi.Effect);
                if (hookBindings.Count > 0)
                {
                    var byEvent = new Dictionary<string, List<MonsterKitHooks.ComboEventBinding>>(StringComparer.Ordinal);
                    for (int h = 0; h < hookBindings.Count; h++)
                    {
                        string en = NormalizeEventName(hookBindings[h].EventName);
                        if (!byEvent.TryGetValue(en, out var bList)) byEvent[en] = bList = [];
                        bList.Add(hookBindings[h]);
                    }
                    var hookOcc = new Dictionary<string, int>(StringComparer.Ordinal);
                    var hookEvents = new List<ResolvedComboEvent>();
                    foreach (var ev in pi.CombatEvents)
                    {
                        string en = NormalizeEventName(ev.Name);
                        if (!byEvent.TryGetValue(en, out var bList)) continue;
                        int occ = hookOcc.GetValueOrDefault(en);
                        hookOcc[en] = occ + 1;
                        var b = bList.Count == 1 ? bList[0] : bList[Math.Min(occ, bList.Count - 1)];
                        double frac = pi.ClipLengthMs > 0 ? Math.Clamp((double)ev.Ms / pi.ClipLengthMs, 0.0, 1.0) : 0.5;
                        var dispatch = b.DispatchOverride ?? DispatchFor(SkillCatalog.ActionTypeId(b.ChildEffect));
                        hookEvents.Add(new(en, ev.Ms, frac, b.ChildEffect, dispatch, ev.Point));
                        boundChildren.Add(b.ChildEffect);
                    }
                    if (hookEvents.Count > 0)
                    {
                        hookEvents.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
                        resolvedParents[pi.Effect] = new(pi.Effect, pi.ClipAnimation, pi.ClipLengthMs, hookEvents, pi.Controls);
                        continue;
                    }
                }
            }
            if (candidates.Count == 0) continue;

            var pStem = StemTokens(pi.Id, varTokens);
            var clipToks = new HashSet<string>(
                SplitTokens(pi.ClipAnimation).Where(t => t.Length >= 3 && !GenericStopTokens.Contains(t) && !varTokens.Contains(t)),
                StringComparer.OrdinalIgnoreCase);

            // EventName -> ordered list of children (count == 1 means same child for every occurrence; count == evList.Count means ordinal 1..N)
            var bindings = new Dictionary<string, List<CandidateChild>>(StringComparer.Ordinal);

            bool isSummonParent = pi.Id.Contains("summon", StringComparison.OrdinalIgnoreCase);
            if (activeParentCount == 1 && candidates.Count == 1
                && Strict2AActionTypes.Contains(candidates[0].ActionType)
                && !(isSummonParent && !ServerSpawnActionTypes.Contains(candidates[0].ActionType)))
            {
                // Rule 2a: Unique 1:1 strict payload child. Prefer custom event names over generic "contact".
                bool hasCustom = groups.Keys.Any(static k => k != "contact");
                foreach (string en in groups.Keys)
                {
                    if (hasCustom && en == "contact") continue;
                    bindings[en] = [candidates[0]];
                }
            }
            else
            {
                int projCandCount = 0;
                for (int i = 0; i < candidates.Count; i++)
                    if (ProjectileActionTypes.Contains(candidates[i].ActionType)
                        && candidates[i].ActionType is not ("MonsterMortar" or "MonsterMortarAttack"))
                        projCandCount++;

                foreach (var (en, evList) in groups)
                {
                    var evToks = new HashSet<string>(
                        SplitTokens(en).Where(static t => t.Length >= 2 && t is not ("trigger" or "proj" or "projs" or "effect" or "event" or "contact")),
                        StringComparer.OrdinalIgnoreCase);
                    var ordMatch = OrdEventRegex.Match(en);

                    var scored = new List<(int AdjScore, int RawScore, CandidateChild Child)>();
                    foreach (var c in candidates)
                    {
                        int score = 0;
                        int sharedParent = CountIntersection(pStem, c.Stem);
                        int sharedClip = CountIntersection(clipToks, c.Stem);
                        int sharedEv = CountIntersection(evToks, c.AllTokens);
                        score += 4 * sharedParent + 2 * sharedClip + 5 * sharedEv;

                        if (ordMatch.Success)
                        {
                            string baseTok = ordMatch.Groups[1].Value.Trim('_');
                            string numTok = ordMatch.Groups[2].Value;
                            if (c.AllTokens.Contains(baseTok) && c.AllTokens.Contains(numTok))
                                score += 6;
                        }

                        int aff = EventActionAffinity(en, c.ActionType);
                        if (aff > 0)
                        {
                            if (score > 0 || activeParentCount == 1)
                                score += aff;
                            else if (projCandCount == 1
                                && ProjectileActionTypes.Contains(c.ActionType)
                                && c.ActionType is not ("MonsterMortar" or "MonsterMortarAttack")
                                && en is "trigger_proj" or "trigger_projs" or "proj_left" or "proj_right" or "left_proj" or "right_proj")
                                score += 4;
                        }

                        if (score > 0)
                        {
                            int unmatchedChild = 0;
                            foreach (string t in c.Stem)
                                if (!pStem.Contains(t) && !clipToks.Contains(t) && !evToks.Contains(t))
                                    unmatchedChild++;
                            scored.Add((score * 10 - unmatchedChild, score, c));
                        }
                    }

                    if (scored.Count == 0) continue;
                    scored.Sort(static (a, b) =>
                    {
                        int cmp = b.AdjScore.CompareTo(a.AdjScore);
                        return cmp != 0 ? cmp : a.Child.Effect.CompareTo(b.Child.Effect);
                    });

                    int topAdj = scored[0].AdjScore;
                    int topRaw = scored[0].RawScore;

                    // Rule 2c: Ordinal series match (e.g. 4x "reave" -> DaressoReaveGeometryAttack1..4)
                    if (evList.Count >= 2 && topRaw >= 4)
                    {
                        var byBase = new Dictionary<string, List<(int Num, CandidateChild Child)>>(StringComparer.Ordinal);
                        for (int i = 0; i < scored.Count; i++)
                        {
                            if (scored[i].RawScore < topRaw - 1) continue;
                            var mNum = TrailingNumberRegex.Match(scored[i].Child.Id);
                            if (!mNum.Success || !int.TryParse(mNum.Groups[2].Value, out int num)) continue;
                            string baseName = mNum.Groups[1].Value;
                            if (!byBase.TryGetValue(baseName, out var list)) byBase[baseName] = list = [];
                            list.Add((num, scored[i].Child));
                        }
                        bool ordinalMatched = false;
                        foreach (var items in byBase.Values)
                        {
                            if (items.Count != evList.Count) continue;
                            items.Sort(static (a, b) => a.Num.CompareTo(b.Num));
                            bool seqOk = true;
                            for (int k = 0; k < items.Count; k++)
                                if (items[k].Num != k + 1) { seqOk = false; break; }
                            if (seqOk)
                            {
                                bindings[en] = [.. items.Select(static x => x.Child)];
                                ordinalMatched = true;
                                break;
                            }
                        }
                        if (ordinalMatched) continue;
                    }

                    // Rule 2b: Confident unique winner (raw >= 4 and strictly ahead of runner-up after specificity tie-break)
                    int runnerUpAdj = scored.Count > 1 ? scored[1].AdjScore : int.MinValue;
                    if (topRaw >= 4 && (topAdj > runnerUpAdj || scored.Count == 1))
                        bindings[en] = [scored[0].Child];
                }

                // Shadow "contact" suppression: if custom event(s) bound child(ren) and "contact" bound the exact subset,
                // drop "contact" so co-located events never double-fire (e.g., Daresso Reave 4+4 -> 4, Brutus MultiSlam).
                if (bindings.Count > 1 && bindings.TryGetValue("contact", out var contactList))
                {
                    var customSet = new HashSet<int>();
                    foreach (var (en, list) in bindings)
                        if (en != "contact")
                            for (int i = 0; i < list.Count; i++) customSet.Add(list[i].Effect);
                    if (contactList.All(c => customSet.Contains(c.Effect)))
                        bindings.Remove("contact");
                }
            }

            if (bindings.Count == 0) continue;

            // Flatten into chronologically sorted ResolvedComboEvent list.
            var occCounter = new Dictionary<string, int>(StringComparer.Ordinal);
            var resolvedEvents = new List<ResolvedComboEvent>();
            foreach (var ev in pi.CombatEvents)
            {
                string en = NormalizeEventName(ev.Name);
                if (!bindings.TryGetValue(en, out var childList)) continue;
                int occ = occCounter.GetValueOrDefault(en);
                occCounter[en] = occ + 1;
                var child = childList.Count == 1 ? childList[0] : childList[Math.Min(occ, childList.Count - 1)];
                double frac = pi.ClipLengthMs > 0 ? Math.Clamp((double)ev.Ms / pi.ClipLengthMs, 0.0, 1.0) : 0.5;
                resolvedEvents.Add(new(en, ev.Ms, frac, child.Effect, DispatchFor(child.ActionType), ev.Point));
                boundChildren.Add(child.Effect);
            }

            if (resolvedEvents.Count > 0)
            {
                resolvedEvents.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
                resolvedParents[pi.Effect] = new(pi.Effect, pi.ClipAnimation, pi.ClipLengthMs, resolvedEvents, pi.Controls);
            }
        }

        return resolvedParents.Count > 0 ? new(resolvedParents, boundChildren) : VarietyCombos.Empty;
    }

    // ---- Kit & Actor Skill Construction ------------------------------------------------------------------------------

    /// <summary>Builds a playable parent <c>EmptyAction*</c> <see cref="MonsterAttack"/> with pre-resolved
    /// <see cref="MonsterAttack.ComboSteps"/> and <see cref="MonsterAttack.ComboControls"/>, allocating child skill ids
    /// starting at <see cref="ChildSkillIdBase"/> (<c>0x4400+</c>). Returns null if no executable child steps resolve.</summary>
    public static MonsterAttack? BuildParentAttack(
        Player player,
        MonsterCatalog.Variety variety,
        byte level,
        int parentEffect,
        ushort parentSkillId,
        Dictionary<int, ushort> childSkillIds)
    {
        var catalog = SkillsFor(player);
        var combos = For(catalog, variety);
        if (!combos.Parents.TryGetValue(parentEffect, out var parent)) return null;

        var childCache = new Dictionary<int, MonsterAttack?>();
        var steps = new List<MonsterComboStep>(parent.Events.Count);
        double maxReach = 0;

        foreach (var ev in parent.Events)
        {
            if (!childCache.TryGetValue(ev.ChildEffect, out var childAttack))
            {
                ushort childSkillId;
                if (!childSkillIds.TryGetValue(ev.ChildEffect, out childSkillId))
                {
                    childSkillId = (ushort)(ChildSkillIdBase + childSkillIds.Count);
                    childSkillIds[ev.ChildEffect] = childSkillId;
                }
                childAttack = BuildChildAttack(player, variety, level, ev.ChildEffect, childSkillId, ev.Dispatch);
                childCache[ev.ChildEffect] = childAttack;
            }
            if (childAttack is null) continue;
            if (childAttack.Range > maxReach) maxReach = childAttack.Range;
            steps.Add(new(ev.EventName, ev.Ms, ev.Fraction, ev.ChildEffect, childAttack, ev.Dispatch, ev.Point));
        }

        if (steps.Count == 0) return null;

        bool isSpell = SkillCatalog.ActionTypeId(parentEffect)?.StartsWith("EmptyActionSpell", StringComparison.Ordinal) == true;
        double seconds = Math.Clamp(parent.ClipLengthMs / 1000.0, 0.35, 12.0);
        double reach = maxReach > 0 ? Math.Min(maxReach, Math.Max(variety.AggroRange, 45)) : Math.Min(variety.AggroRange, 32);
        if (reach < 12) reach = Math.Min(Math.Max(variety.AttackRange, 18), Math.Max(variety.AggroRange, 22));

        // Carry representative damage on the parent record (first damaging step) only for UI/inspection; actual damage
        // lands per-step from each step.ChildAttack during RunComboAsync.
        var repDamage = steps.FirstOrDefault(static s => !s.ChildAttack.Damage.IsZero)?.ChildAttack.Damage ?? DamagePacket.Empty;

        return new MonsterAttack(parentSkillId, seconds, reach, repDamage)
        {
            Effect = parentEffect,
            Spell = isSpell,
            Ranged = reach > variety.AttackRange + 4,
            ComboSteps = steps,
            ComboControls = parent.Controls,
        };
    }

    static MonsterAttack? BuildChildAttack(
        Player player,
        MonsterCatalog.Variety variety,
        byte level,
        int childEffect,
        ushort childSkillId,
        MonsterComboDispatch dispatch)
    {
        var catalog = SkillsFor(player);
        var raw = BossKits.AttackFor(player, variety, level, childEffect, childSkillId) with { Effect = childEffect };

        if (dispatch == MonsterComboDispatch.ServerSpawn)
        {
            if (raw.Summon is not null) return raw;
            if (BossKits.Shaped(player, variety, level, childEffect, raw) is { } shapedSpawn && BossKits.Acts(shapedSpawn))
                return shapedSpawn;
            return raw.Ground is not null || !raw.Damage.IsZero ? raw : null;
        }

        if (dispatch == MonsterComboDispatch.Projectile)
        {
            var shaped = BossKits.Shaped(player, variety, level, childEffect, raw);
            if (shaped is { Projectile: not null }) return shaped;
            if (raw.Projectile is not null) return raw;
            if (BossKits.ArtShot(catalog, childEffect, level) is { } shot)
                return raw with { Projectile = shot, ReleaseEffect = childEffect };
            // Fallback: if a projectile child has damage but no resolved Projectile row, still fire as area/ranged hit.
            return !raw.Damage.IsZero ? raw with { AreaRadius = Math.Max(raw.AreaRadius, 14) } : null;
        }

        // TriggeredAction (GeometryAttack, GeometrySpell, GeometryTrigger, EffectDriven*, TriggerBeam, ExecuteGEAL, Melee)
        var shapedTrig = BossKits.Shaped(player, variety, level, childEffect, raw);
        if (shapedTrig is not null)
        {
            // Ensure geometry/effect-driven attacks have a sensible AoE radius when tables omit an explicit radius stat.
            if (shapedTrig.AreaRadius <= 0 && shapedTrig.Projectile is null && shapedTrig.Summon is null && !shapedTrig.Damage.IsZero)
            {
                double fallbackRadius = BossKits.AreaRadius(catalog, childEffect, level) ?? 16.0;
                shapedTrig = shapedTrig with { AreaRadius = fallbackRadius };
            }
            return shapedTrig;
        }
        // Unshaped GeometryAttack/GeometrySpell/TriggerBeam with damage: assign table radius or 16-cell default so it hits cleanly.
        if (!raw.Damage.IsZero)
        {
            double radius = BossKits.AreaRadius(catalog, childEffect, level) ?? 16.0;
            return raw with { AreaRadius = radius };
        }
        return raw;
    }

    /// <summary>Scales a combo parent's duration and every child step's <see cref="MonsterComboStep.ChildAttack"/>
    /// using <paramref name="scaleChild"/> (for <see cref="MonsterModEffects"/> in <c>BossKits</c> / <c>MonsterKits</c>).</summary>
    public static MonsterAttack ScaleComboAttack(
        MonsterAttack parent,
        MonsterModEffects mods,
        Func<MonsterAttack, MonsterAttack> scaleChild)
    {
        if (parent.ComboSteps is not { Count: > 0 } steps) return parent;
        var scaled = new MonsterComboStep[steps.Count];
        for (int i = 0; i < steps.Count; i++)
            scaled[i] = steps[i] with { ChildAttack = scaleChild(steps[i].ChildAttack) };
        var repDamage = scaled.FirstOrDefault(static s => !s.ChildAttack.Damage.IsZero)?.ChildAttack.Damage ?? DamagePacket.Empty;
        return parent with
        {
            Damage = repDamage,
            Seconds = Math.Max(0.25, mods.AttackSeconds(parent.Seconds, parent.Spell)),
            ComboSteps = scaled,
        };
    }

    /// <summary>Returns <paramref name="skills"/> with all <see cref="MonsterComboDispatch.TriggeredAction"/> child
    /// skills (<c>0x4400+</c>) appended when <paramref name="kit"/> contains S3 combo parents, keeping
    /// <paramref name="skills"/> unchanged (zero allocations) when no combo children are needed.</summary>
    public static IReadOnlyList<GrantedSkill> WithChildSkills(
        Player player,
        MonsterCatalog.Variety variety,
        byte level,
        IReadOnlyList<MonsterKitSkill> kit,
        IReadOnlyList<GrantedSkill> skills)
    {
        if (kit.Count == 0) return skills;
        List<GrantedSkill>? copy = null;
        SortedDictionary<ushort, (int Effect, MonsterAttack Attack)>? needed = null;
        for (int i = 0; i < kit.Count; i++)
        {
            if (kit[i].Attack.ComboSteps is not { Count: > 0 } steps) continue;
            for (int j = 0; j < steps.Count; j++)
            {
                var step = steps[j];
                if (step.Dispatch != MonsterComboDispatch.TriggeredAction) continue;
                (needed ??= []).TryAdd(step.ChildAttack.SkillId, (step.ChildEffect, step.ChildAttack));
            }
        }
        if (needed is null) return skills;
        copy = [.. skills];
        AppendChildSkills(player, variety, level, kit, copy);
        return copy;
    }

    /// <summary>Enforces the binary-verified <c>0x2bb</c> invariant: every <see cref="MonsterComboDispatch.TriggeredAction"/>
    /// child referenced by any entry in <paramref name="kit"/> MUST have a <see cref="GrantedSkill"/> registered in the
    /// monster's <c>Actor</c> skill list (<c>Actor+0x6f0..0x6f8</c>), sorted by ascending <c>SkillId</c>, so
    /// <c>ClientActor::ExecuteTriggeredAction</c> (<c>0x14219c1f0</c>) finds it when <c>S→C 0x1ce</c> arrives.</summary>
    public static void AppendChildSkills(
        Player player,
        MonsterCatalog.Variety variety,
        byte level,
        IReadOnlyList<MonsterKitSkill> kit,
        List<GrantedSkill> skills)
    {
        SortedDictionary<ushort, (int Effect, MonsterAttack Attack)>? needed = null;
        for (int i = 0; i < kit.Count; i++)
        {
            if (kit[i].Attack.ComboSteps is not { Count: > 0 } steps) continue;
            for (int j = 0; j < steps.Count; j++)
            {
                var step = steps[j];
                if (step.Dispatch != MonsterComboDispatch.TriggeredAction) continue;
                (needed ??= []).TryAdd(step.ChildAttack.SkillId, (step.ChildEffect, step.ChildAttack));
            }
        }
        if (needed is null) return;

        var catalog = SkillsFor(player);
        var existingIds = new HashSet<ushort>(skills.Select(static s => s.Id));
        foreach (var (skillId, (childEffect, childAttack)) in needed)
        {
            if (!existingIds.Add(skillId)) continue;
            int effectLevel = catalog.MaxLevelAt(childEffect, level);
            var stats = MonsterActionSkillStats(player, childEffect, effectLevel, childAttack);
            if (catalog.FindByRow(skillId, childEffect, effectLevel, stats) is { } granted)
                skills.Add(granted);
        }
    }

    // ---- Runtime Combo Execution (AreaTime clock, zero Task.Delay) ---------------------------------------------------

    /// <summary>Executes an <c>EmptyAction*</c> parent combo after <c>S→C 0x1ab</c> has started the parent clip on the
    /// client. Schedules facing/motion control events and child triggers strictly on <c>AreaTime</c> via
    /// <c>WaitMonsterActionTime</c>, aborting immediately if <c>MonsterActionCurrent</c> turns false (death, stun, freeze,
    /// pause, cancel, or action generation bump).</summary>
    public static async Task RunComboAsync(
        Player player,
        uint id,
        SpawnedMonster m,
        MonsterAttack parent,
        int action,
        ushort seq,
        double startedAt,
        double seconds,
        (int X, int Y) initialOrigin,
        (int X, int Y) initialAim)
    {
        var steps = parent.ComboSteps;
        if (steps is not { Count: > 0 }) return;
        var controls = parent.ComboControls ?? [];

        double clipLenMs = parent.Seconds > 0 ? parent.Seconds * 1000.0 : 1000.0;
        double speedScale = seconds > 0.01 ? (clipLenMs / 1000.0) / seconds : 1.0;

        // Build unified chronological timeline of controls + child steps.
        var timeline = new List<(double At, bool IsControl, int Index)>(steps.Count + controls.Count);
        for (int i = 0; i < controls.Count; i++)
        {
            double frac = Math.Clamp(controls[i].Ms / clipLenMs, 0.0, 1.0);
            timeline.Add((startedAt + seconds * frac, true, i));
        }
        for (int i = 0; i < steps.Count; i++)
            timeline.Add((startedAt + seconds * steps[i].Fraction, false, i));
        timeline.Sort(static (a, b) =>
        {
            int cmp = a.At.CompareTo(b.At);
            // Process controls before child steps at identical timestamps (so facing/move updates apply first).
            if (cmp != 0) return cmp;
            return b.IsControl.CompareTo(a.IsControl);
        });

        bool trackingFace = false;
        var aim = initialAim;
        double facingAngle = Math.Atan2(initialAim.Y - initialOrigin.Y, initialAim.X - initialOrigin.X);

        for (int t = 0; t < timeline.Count; t++)
        {
            var (at, isControl, idx) = timeline[t];
            if (!await WaitMonsterActionTime(player, id, m, action, at)) return;
            if (!MonsterActionCurrent(player, id, m, action)) return;

            double now = AreaTime(player);
            var monsterPos = m.Walk.Position(now);
            var playerPos = player.Walk.Position(now);

            if (isControl)
            {
                var ctrl = controls[idx];
                switch (ctrl.Kind)
                {
                    case "StartFaceEventType":
                        trackingFace = true;
                        aim = playerPos;
                        facingAngle = Math.Atan2(aim.Y - monsterPos.Y, aim.X - monsterPos.X);
                        break;
                    case "EndFaceEventType":
                        trackingFace = false;
                        break;
                    case "face":
                        aim = playerPos;
                        facingAngle = Math.Atan2(aim.Y - monsterPos.Y, aim.X - monsterPos.X);
                        break;
                    case "MoveEventType":
                        ApplyRootMotionMove(player, m, ctrl, monsterPos, facingAngle, speedScale, now);
                        break;
                }
                continue;
            }

            if (trackingFace)
            {
                aim = playerPos;
                facingAngle = Math.Atan2(aim.Y - monsterPos.Y, aim.X - monsterPos.X);
            }

            var step = steps[idx];
            var eventOrigin = ComputeEventOrigin(monsterPos, step.Point, facingAngle);
            var stepAim = trackingFace ? playerPos : aim;

            await FireComboStepAsync(player, id, m, parent.Effect ?? -1, action, seq, step, monsterPos, eventOrigin, stepAim);
        }
    }

    static void ApplyRootMotionMove(
        Player player,
        SpawnedMonster m,
        MonsterComboControl ctrl,
        (int X, int Y) from,
        double facingAngle,
        double speedScale,
        double now)
    {
        if (ctrl.Point is not { } delta) return;
        double durSec = ctrl.EndMs is int endMs && endMs > ctrl.Ms
            ? Math.Max(0.05, (endMs - ctrl.Ms) / 1000.0 / Math.Max(0.1, speedScale))
            : 0.25;
        var target = ComputeEventOrigin(from, delta, facingAngle);
        if (target == from) return;

        // Walk along the segment and clamp to the last walkable cell when Terrain-clamped (matching client MoveEventType).
        int steps = Math.Max(1, Math.Max(Math.Abs(target.X - from.X), Math.Abs(target.Y - from.Y)));
        var dest = from;
        for (int s = 1; s <= steps; s++)
        {
            int cx = from.X + (int)Math.Round((target.X - from.X) * (s / (double)steps));
            int cy = from.Y + (int)Math.Round((target.Y - from.Y) * (s / (double)steps));
            var cell = (cx, cy);
            if (ctrl.TerrainClamped && (player.Barrier.Contains(cell) || (cell != from && !m.Walk.Floor(cx, cy))))
                break;
            dest = cell;
        }
        if (dest != from)
            m.Walk.Slide(dest.X, dest.Y, durSec, now);
    }

    static (int X, int Y) ComputeEventOrigin((int X, int Y) monsterPos, ContactPoint? point, double facingAngle)
    {
        if (point is not { } p) return monsterPos;
        // Local rig coordinates in .ao: -Y is forward along facing, X is lateral.
        double forwardCells = -p.Y / WorldUnitsPerCell;
        double rightCells = p.X / WorldUnitsPerCell;
        if (Math.Abs(forwardCells) < 0.25 && Math.Abs(rightCells) < 0.25) return monsterPos;
        double cos = Math.Cos(facingAngle), sin = Math.Sin(facingAngle);
        double dx = forwardCells * cos - rightCells * sin;
        double dy = forwardCells * sin + rightCells * cos;
        return (monsterPos.X + (int)Math.Round(dx), monsterPos.Y + (int)Math.Round(dy));
    }

    static async Task FireComboStepAsync(
        Player player,
        uint id,
        SpawnedMonster m,
        int parentEffect,
        int action,
        ushort seq,
        MonsterComboStep step,
        (int X, int Y) monsterPos,
        (int X, int Y) eventOrigin,
        (int X, int Y) stepAim)
    {
        if (MonsterKitHooks.HasClipObservers)
            MonsterKitHooks.NotifyClipEvent(player, id, m, parentEffect, step.EventName, AreaTime(player));

        var child = step.ChildAttack;
        (int X, int Y)? customOrigin = eventOrigin != monsterPos ? eventOrigin : null;

        switch (step.Dispatch)
        {
            case MonsterComboDispatch.TriggeredAction:
            {
                // S->C 0x1ce (flags |= 0x80, optional 0x2000 origin): runs child ExecuteTriggered (+0x268) on client
                // WITHOUT interrupting the playing parent EmptyAction* on Actor+0x1b0!
                if (!await SendMonsterAction(player, id, m, action, () =>
                    InstanceProtocol.InstantAction(id, child.SkillId, stepAim.X, stepAim.Y, customOrigin,
                        targetObjectId: player.Id, sequence: seq, triggered: true),
                    ServerTrace.Combat || ServerTrace.Ai
                        ? $"0x01ce monster 0x{id:x} ({m.Label}) combo event '{step.EventName}' -> child 0x{child.SkillId:x4}"
                        : null)) return;

                var hitCenter = child.AreaAtSelf ? eventOrigin : (customOrigin ?? stepAim);
                if (child.AreaRadius > 0)
                    await MonsterAreaHit(player, id, m, child, action, hitCenter, child.AreaRadius * m.Effects.AreaScaleFactor, seq);
                else if (child.Projectile is { } trigProj)
                    LaunchMonsterProjectile(player, id, m, child, trigProj, action, orderedAim: stepAim, sequence: seq, releasedFrom: customOrigin);
                else if (!child.Damage.IsZero)
                    await MonsterContact(player, id, m, action, seq, child);
                else if (child.Ground is not null)
                    await LeaveSkillGround(player, id, child, hitCenter);
                break;
            }

            case MonsterComboDispatch.Projectile:
            {
                // Authoritative S->C 0x2bb projectile spawn without duplicate 0x1ce.
                if (child.Projectile is { } proj)
                    LaunchMonsterProjectile(player, id, m, child, proj, action, orderedAim: stepAim, sequence: seq, releasedFrom: customOrigin);
                else if (child.AreaRadius > 0)
                    await MonsterAreaHit(player, id, m, child, action, customOrigin ?? stepAim, child.AreaRadius * m.Effects.AreaScaleFactor, seq);
                break;
            }

            case MonsterComboDispatch.ServerSpawn:
            {
                if (child.Summon is { } summon)
                    await MonsterSummon(player, id, m, summon);
                else if (child.Ground is not null)
                    await LeaveSkillGround(player, id, child, customOrigin ?? stepAim);
                break;
            }
        }
    }

    // ---- Parsing & Tokenization Helpers ------------------------------------------------------------------------------

    static string NormalizeEventName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int hash = name.IndexOf('#');
        string trimmed = (hash >= 0 ? name[..hash] : name).Trim();
        return trimmed.ToLowerInvariant();
    }

    static bool TryParseControlEvent(SkillCatalog.ClipNamedEvent ev, out MonsterComboControl control)
    {
        control = default!;
        string name = NormalizeEventName(ev.Name);
        if (!string.IsNullOrEmpty(ev.ArgsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.ArgsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() is { } typeStr
                    && EngineControlTypes.Contains(typeStr))
                {
                    int? endMs = root.TryGetProperty("end_ms", out var endProp) && endProp.TryGetInt32(out int e) ? e : null;
                    bool terrain = root.TryGetProperty("data", out var dataProp)
                        && dataProp.GetString()?.Contains("Terrain", StringComparison.OrdinalIgnoreCase) == true;
                    control = new(typeStr, ev.Ms, endMs, ev.Point, terrain);
                    return true;
                }
            }
            catch (JsonException) { }
        }
        if (name is "face" or "start_face" or "end_face" or "move")
        {
            string kind = name switch
            {
                "start_face" => "StartFaceEventType",
                "end_face" => "EndFaceEventType",
                "move" => "MoveEventType",
                _ => "face",
            };
            control = new(kind, ev.Ms, null, ev.Point, TerrainClamped: true);
            return true;
        }
        return false;
    }

    static bool IsNonCombatEvent(SkillCatalog.ClipNamedEvent ev)
    {
        string name = NormalizeEventName(ev.Name);
        if (name.Length == 0 || NonCombatExact.Contains(name)) return true;
        for (int i = 0; i < NonCombatPrefixes.Length; i++)
            if (name.StartsWith(NonCombatPrefixes[i], StringComparison.Ordinal)) return true;
        for (int i = 0; i < NonCombatSuffixes.Length; i++)
            if (name.EndsWith(NonCombatSuffixes[i], StringComparison.Ordinal)) return true;
        return false;
    }

    static bool IsUtilityParent(string geId, string? clipName)
    {
        if (!string.IsNullOrEmpty(clipName))
        {
            string cl = clipName.ToLowerInvariant();
            if (cl.StartsWith("move_", StringComparison.Ordinal) || cl.StartsWith("dodge_", StringComparison.Ordinal)
                || cl.StartsWith("strafe", StringComparison.Ordinal) || cl.StartsWith("morph_", StringComparison.Ordinal)
                || cl.StartsWith("leave_", StringComparison.Ordinal) || cl.StartsWith("emerge", StringComparison.Ordinal)
                || cl.StartsWith("submerge", StringComparison.Ordinal) || cl.StartsWith("idle", StringComparison.Ordinal)
                || cl.StartsWith("taunt", StringComparison.Ordinal) || cl.StartsWith("death", StringComparison.Ordinal)
                || cl.StartsWith("flinch", StringComparison.Ordinal) || cl.StartsWith("run", StringComparison.Ordinal)
                || cl.StartsWith("walk", StringComparison.Ordinal) || cl.StartsWith("burrow", StringComparison.Ordinal))
                return true;
            if (cl.Contains("_end_", StringComparison.Ordinal)
                && (cl.Contains("breathe", StringComparison.Ordinal) || cl.Contains("channel", StringComparison.Ordinal) || cl.Contains("beam", StringComparison.Ordinal)))
                return true;
        }

        string stripped = StripPrefixRegex.Replace(geId, "");
        var toks = SplitTokens(stripped).Where(static t => !GenericStopTokens.Contains(t)).ToList();
        if (toks.Count == 0) return false;
        if (toks.All(static t => UtilityParentTokens.Contains(t))) return true;
        bool hasHardUtility = toks.Any(static t => UtilityHardTokens.Contains(t));
        bool hasCombatOverride = toks.Any(static t => CombatOverrideTokens.Contains(t));
        return hasHardUtility && !hasCombatOverride;
    }

    static bool ContainsExcludedChildSubstring(string id)
    {
        for (int i = 0; i < ExcludedChildSubstrings.Length; i++)
            if (id.Contains(ExcludedChildSubstrings[i], StringComparison.Ordinal))
                return true;
        return false;
    }

    static int EventActionAffinity(string evName, string childAt)
    {
        if (evName.Contains("proj", StringComparison.Ordinal) || evName.Contains("shot", StringComparison.Ordinal)
            || evName.Contains("shoot", StringComparison.Ordinal) || evName.Contains("arrow", StringComparison.Ordinal)
            || evName.Contains("bolt", StringComparison.Ordinal) || evName.Contains("spear", StringComparison.Ordinal)
            || evName.Contains("mortar", StringComparison.Ordinal) || evName.Contains("spit", StringComparison.Ordinal)
            || evName.Contains("vomit", StringComparison.Ordinal) || evName.Contains("throw", StringComparison.Ordinal)
            || evName.Contains("orb", StringComparison.Ordinal) || evName.Contains("ball", StringComparison.Ordinal)
            || evName.Contains("shard", StringComparison.Ordinal))
            return ProjectileActionTypes.Contains(childAt) ? 3 : 0;

        if (evName.Contains("beam", StringComparison.Ordinal) || evName.Contains("laser", StringComparison.Ordinal)
            || evName.Contains("ray", StringComparison.Ordinal))
            return BeamActionTypes.Contains(childAt) ? 3 : 0;

        if (evName.Contains("minion", StringComparison.Ordinal) || evName.Contains("summon", StringComparison.Ordinal)
            || evName.Contains("spawn", StringComparison.Ordinal) || evName.Contains("totem", StringComparison.Ordinal)
            || evName.Contains("effigy", StringComparison.Ordinal) || evName.Contains("worm", StringComparison.Ordinal)
            || evName.Contains("daemon", StringComparison.Ordinal) || evName.Contains("clone", StringComparison.Ordinal))
            return ServerSpawnActionTypes.Contains(childAt) ? 3 : 0;

        if (evName.Contains("slam", StringComparison.Ordinal) || evName.Contains("swipe", StringComparison.Ordinal)
            || evName.Contains("left", StringComparison.Ordinal) || evName.Contains("right", StringComparison.Ordinal)
            || evName.Contains("slash", StringComparison.Ordinal) || evName.Contains("cleave", StringComparison.Ordinal)
            || evName.Contains("reave", StringComparison.Ordinal) || evName.Contains("wave", StringComparison.Ordinal)
            || evName.Contains("nova", StringComparison.Ordinal) || evName.Contains("impact", StringComparison.Ordinal)
            || evName.Contains("dash", StringComparison.Ordinal) || evName.Contains("screech", StringComparison.Ordinal)
            || evName.Contains("wall", StringComparison.Ordinal) || evName.Contains("circle", StringComparison.Ordinal)
            || evName.Contains("blast", StringComparison.Ordinal) || evName.Contains("explode", StringComparison.Ordinal)
            || evName.Contains("stomp", StringComparison.Ordinal) || evName.Contains("crush", StringComparison.Ordinal)
            || evName.Contains("smash", StringComparison.Ordinal) || evName.Contains("cone", StringComparison.Ordinal))
            return GeoActionTypes.Contains(childAt) ? 2 : 0;

        return 0;
    }

    static HashSet<string> VarietyIdentityTokens(string? mvId, string? mvName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(mvId))
        {
            var parts = mvId.Split('/');
            int start = Math.Max(0, parts.Length - 3);
            for (int i = start; i < parts.Length; i++)
                foreach (string t in SplitTokens(parts[i]))
                    if (t.Length >= 2) set.Add(t);
        }
        if (!string.IsNullOrEmpty(mvName))
            foreach (string t in SplitTokens(mvName))
                if (t.Length >= 2) set.Add(t);
        return set;
    }

    static HashSet<string> StemTokens(string geId, HashSet<string> varTokens)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string stripped = StripPrefixRegex.Replace(geId, "");
        foreach (string t in SplitTokens(stripped))
            if (t.Length >= 2 && !GenericStopTokens.Contains(t) && !varTokens.Contains(t)) set.Add(t);
        foreach (string t in SplitTokens(geId))
            if (t.Length >= 2 && !GenericStopTokens.Contains(t) && !varTokens.Contains(t)) set.Add(t);
        return set;
    }

    static List<string> SplitTokens(string? s)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(s)) return tokens;
        int start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (start >= 0) { tokens.Add(s[start..i].ToLowerInvariant()); start = -1; }
                continue;
            }
            if (start < 0) { start = i; continue; }
            char prev = s[i - 1];
            bool boundary = (char.IsLower(prev) && char.IsUpper(c))
                || (char.IsLetter(prev) && char.IsDigit(c))
                || (char.IsDigit(prev) && char.IsLetter(c))
                || (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]));
            if (boundary)
            {
                tokens.Add(s[start..i].ToLowerInvariant());
                start = i;
            }
        }
        if (start >= 0) tokens.Add(s[start..].ToLowerInvariant());
        return tokens;
    }

    static int CountIntersection(HashSet<string> a, HashSet<string> b)
    {
        int n = 0;
        foreach (string s in a)
            if (b.Contains(s)) n++;
        return n;
    }
}
