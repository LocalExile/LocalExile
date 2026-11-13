using System.Collections.Frozen;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Core;

/// <summary>
/// Skills the server grants: a GrantedEffects Id at a level becomes the GrantedEffectsPerLevel row and the
/// GrantedEffectStatSetsPerLevel row (of the effect's StatSet1 at that gem level) the Actor skill list refers to
/// (Agents/docs/protocol/11-skills.md). Reads the client tables (client.db, loaded on first use). Without the tables it
/// knows only the default attack, PlayerMelee at level 1 (GEPL 4474, GESSPL 4430 in 3.29.3b).
/// </summary>
public sealed class SkillCatalog(string folder = "data", bool hardMode = false)
{
    /// <summary>The ids the server gives its skills (+0x40). Basic actions use their ActionTypes HASH16 instead, so these
    /// must not collide with the type's basic actions (Character.ot: Move 0x2909, Interaction 0x0266 …). 0x0101 did: a
    /// Monster.ot basic action masks to it. 0x0102 is no ActionType's id in 3.29.3b or 3.29.3c.</summary>
    public const ushort FirstSkillId = 0x0102;

    private readonly Lazy<Tables?> _tables = new(() => Tables.Load(folder));
    private readonly StatCatalog _stats = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _castsOnDeath = new();

    public bool CastsOnDeath(int effect) => _castsOnDeath.GetOrAdd(effect, row =>
        _stats.Key("cast_on_death_%") is uint key && EffectStats(row, 1).Any(stat => stat.Item1 == key && stat.Item2 > 0));


    // Both sets of effects already have their own native rows and per-level stat sets. Only name-based lookups
    // require resolution; explicit row-based calls retain their exact identity (including monster effects).
    private readonly Lazy<Dictionary<int, int>> _modeEffects = new(() =>
    {
        var result = new Dictionary<int, int>();
        if (!hardMode || !ClientTables.Has("GemEffects")) return result;
        var doc = ClientTables.Table("GemEffects");
        foreach (var r in doc)
            foreach (string name in new[] { "GrantedEffect", "GrantedEffect2" })
                if (r.IntOrNull(name) is int regular && r.IntOrNull(name + "Hardmode") is int alternate
                    && regular >= 0 && alternate >= 0)
                    result.TryAdd(regular, alternate);
        return result;
    });

    /// <summary>Each GrantedEffects row's additional stat sets (StatSet2), when it has any.</summary>
    private readonly Lazy<Dictionary<int, int[]>> _extraSets = new(() =>
    {
        var result = new Dictionary<int, int[]>();
        if (!ClientTables.Has("GrantedEffects")) return result;
        var d = ClientTables.Table("GrantedEffects");
        int row = 0;
        foreach (var r in d)
        {
            if (r.Ints("StatSet2") is { Length: > 0 } sets)
                result[row] = sets;
            row++;
        }
        return result;
    });

    /// <summary>A skill with its effect's additional stat sets: each set's per-level row at this level (the closest one
    /// otherwise) and its stats (implicit 1s, constants, then the level's stats). Null when a set has no row at all: a
    /// record without it would make the client misread it.</summary>
    GrantedSkill? WithExtraSets(GrantedSkill skill, Tables t, int effectRow, int level)
    {
        // Both Find paths use catalog identity and raw stored uses, never effective cost or current souls.
        skill = skill with { VaalResourcesEnabled = VaalBudget(effectRow, level) is not null };
        if (!_extraSets.Value.TryGetValue(effectRow, out var sets)) return skill;
        var extra = new List<(uint, IReadOnlyList<(uint, int)>)>();
        foreach (int set in sets)
        {
            int row = PickLevel(t.StatSetsPerLevel, set, level);
            if (row < 0) return null;
            var stats = new Dictionary<uint, int>();
            if (set >= 0 && set < t.StatSetBase.Count)
            {
                foreach (int s in t.StatSetBase[set].Implicit) stats[(uint)s + 1] = 1;
                foreach (var (s, v) in t.StatSetBase[set].Constant) stats[(uint)s + 1] = v;
            }
            foreach (var (s, v) in t.StatSetLevelStats[row]) stats[(uint)s + 1] = stats.GetValueOrDefault((uint)s + 1) + v;
            extra.Add(((uint)row, [.. stats.Select(kv => (kv.Key, kv.Value))]));
        }
        return skill with { ExtraStatSets = extra };
    }

    /// <summary>Skill ids for the server's skills from <see cref="FirstSkillId"/> on, skipping every id an ActionType's
    /// HASH16 masks to (0x3fff): 0x0106, 0x010a and 0x0114 are basic actions in 3.29.3c, and a gem skill numbered after
    /// the default attack would replace one.</summary>
    public IEnumerable<ushort> SkillIds()
    {
        var taken = _actionIds.Value;
        for (int id = FirstSkillId; id <= 0x3fff; id++)
            if (!taken.Contains((ushort)id)) yield return (ushort)id;
    }

    /// <summary>Whether an id is one of the gem skills' (<see cref="SkillIds"/> after the default attack, the first 64), not a
    /// basic action such as Move.</summary>
    public bool IsGemSkill(ushort id) => id > FirstSkillId && id < FirstSkillId + 64 && !_actionIds.Value.Contains(id);

    private readonly Lazy<HashSet<ushort>> _actionIds = new(() =>
    {
        if (!ClientTables.Has("ActionTypes")) return [0x0101, 0x0106, 0x010a, 0x0114];
        var d = ClientTables.Table("ActionTypes");
        return [.. d.Select(r => (ushort)(r.Int("HASH16") & 0x3fff))];
    });

    /// <summary>
    /// A gem skill's own stats at a level (client keys = Stats.dat row + 1): the effect's stat set (GrantedEffectStatSets
    /// ImplicitStats = 1, ConstantStats with their values) and its per-level row (GESSPL FloatStats with
    /// BaseResolvedValues, e.g. Fireball's spell_minimum/maximum_base_fire_damage 9–14 at level 1, and AdditionalStats),
    /// then the cast: base_spell_cast_time_ms (GrantedEffects CastTime), hundred_times_casts_per_second, and the mana cost
    /// (GEPL CostAmounts of CostTypes 0 = mana). Then the quality stats: GrantedEffectQualityStats per 1% quality in
    /// thousandths, times the gem's quality, rounded toward zero (the wiki: rounded down;
    /// the 16 negative values are unverified). No modifiers yet: the base values are the final ones.
    /// </summary>
    public IReadOnlyList<(uint, int)> EffectStats(int effectRow, int level, int quality = 0)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.Effects.Count) return [];
        var e = t.Effects[effectRow];
        var stats = new Dictionary<uint, int>();
        var constant = new HashSet<uint>();
        if (e.StatSet >= 0 && e.StatSet < t.StatSetBase.Count)
        {
            var (implicits, constants) = t.StatSetBase[e.StatSet];
            foreach (int s in implicits) stats[(uint)s + 1] = 1;
            foreach (var (s, v) in constants) { stats[(uint)s + 1] = v; constant.Add((uint)s + 1); }
        }
        int gesspl = PickLevel(t.StatSetsPerLevel, e.StatSet, level);
        // A per-level stat that is also a constant adds to it (Righteous Fire: radius 18 + 0 at level 1, + 5 at level 20).
        if (gesspl >= 0) foreach (var (s, v) in t.StatSetLevelStats[gesspl]) stats[(uint)s + 1] = (constant.Contains((uint)s + 1) ? stats[(uint)s + 1] : 0) + v;
        int gepl = PickLevel(t.PerLevel, effectRow, level);
        if (e.CastTime > 0)
        {
            Put("base_spell_cast_time_ms", e.CastTime);
            // what the client reads when it starts the cast (traced live 2026-10-18; 0 = an instant cast)
            Put("virtual_base_spell_cast_time_ms", e.CastTime);
            Put("hundred_times_casts_per_second", (int)Math.Round(100_000.0 / e.CastTime));
        }
        if (Value(stats, "volley_additional_projectiles_fire_parallel_x_dist", "greater_volley_additional_projectiles_fire_parallel_x_dist") is int parallelDist and > 0)
            Put("virtual_additional_projectiles_fire_parallel_x_dist", parallelDist);
        if (gepl >= 0 && t.RageCost[gepl] > 0) { Put("base_rage_cost", t.RageCost[gepl]); Put("total_base_rage_cost", t.RageCost[gepl]); }
        if (gepl >= 0 && t.ManaCost[gepl] > 0) { Put("base_mana_cost", t.ManaCost[gepl]); Put("mana_cost", t.ManaCost[gepl]); }
        if (gepl >= 0 && t.LifeCost[gepl] > 0) { Put("base_life_cost", t.LifeCost[gepl]); Put("life_cost", t.LifeCost[gepl]); }
        // A cost over time (the link gems' "Mana per second"): CostTypes 7 ManaPerMinute.
        if (gepl >= 0 && t.ManaPerMinute[gepl] > 0) { Put("base_mana_cost_per_minute", t.ManaPerMinute[gepl]); Put("mana_cost_per_minute", t.ManaPerMinute[gepl]); }
        // A share of maximum mana over time (Bladefall of Trarthus: 300 per minute, "5% Mana per second"): CostTypes 9
        // ManaPercentPerMinute. Its final mana_cost_per_minute takes the caster's maximum mana (Skills.cs publishes it).
        if (gepl >= 0 && t.ManaPercentPerMinute[gepl] > 0) Put("base_mana_cost_%_per_minute", t.ManaPercentPerMinute[gepl]);
        // A skill with a cooldown (GEPL Cooldown, e.g. Haste's 1200 ms): the client keeps the cooldown only when the skill
        // has uses (virtual_total_cooldown_count, GEPL StoredUses) and a duration in its record (GrantedSkill.CooldownMs).
        // An instant skill (ActiveSkillType Instant: Haste, Righteous Fire) is one by its stat: the client reads
        // skill_is_instant (0x2064), not the skill type. Without it the skill's action waited for a cast that never came,
        // stayed the player's current action and held back every other skill until a move (Frida, live 2026-10-21).
        if (IsInstant(effectRow))
        {
            Put("base_skill_is_instant", 1);
            Put("skill_is_instant", 1);
        }
        if (IsAura(effectRow)) stats[OwnAuraRestriction.BaseAura] = 1;
        if (_curseEffects.Value.Contains(effectRow)) stats[OwnAuraRestriction.Curse] = 1;
        if (OwnAuraRestriction.IsAura(stats)) stats[OwnAuraRestriction.Aura] = 1;
        // CharacterPanelStats gates spell rows on this final flag and hides the weapon rows while it is set.
        // A non-attack is not necessarily a spell (warcries, for example); use the actual ActiveSkillType.
        Put("casting_spell", IsSpell(effectRow) && !IsAttack(effectRow) ? 1 : 0);
        // The Nova type's flag. The client casts a Nova at the cursor only when this, casting_spell and the caster's
        // nova_spells_cast_at_target_location are all set (FUN_14289d210); otherwise it stays around the caster.
        if (IsNova(effectRow)) Put("skill_is_nova_skill", 1);
        // ActiveSkillType Brand's FlagStat is skill_is_brand_skill. It belongs to this selected skill, not the actor.
        Put("skill_is_brand_skill", IsBrand(effectRow) ? 1 : 0);
        if (gepl >= 0 && t.Cooldowns[gepl] is { Ms: > 0, Uses: > 0 } cooldown)
        {
            Put("base_cooldown_count", cooldown.Uses);
            Put("virtual_total_cooldown_count", cooldown.Uses);
        }
        if (quality > 0 && t.Quality.TryGetValue(effectRow, out var perQuality))
            foreach (var (s, permille) in perQuality) stats[(uint)s + 1] = stats.GetValueOrDefault((uint)s + 1) + permille * quality / 1000;
        return [.. stats.Select(kv => (kv.Key, kv.Value))];

        void Put(string name, int value) { if (_stats.Key(name) is uint k) stats[k] = value; }
    }

    /// <summary>
    /// A skill's base hit damage by type, from its per-level stats (<c>spell_minimum_base_fire_damage</c> and so on).
    /// The packet keeps the types separate so conversion and mitigation can be applied per type. Added damage and
    /// increased/more modifiers are applied by the caller; this is the skill's own base range.
    /// </summary>
    public DamagePacket HitDamage(int effectRow, int level, int quality = 0) => HitDamageOf(effectRow, level, quality, "spell");

    /// <summary>A secondary-damage skill's own base hit (Bear Trap's <c>secondary_minimum/maximum_base_physical_damage</c>,
    /// Herald of Ice's shatter): neither an attack nor a spell, so no added attack/spell damage joins it.</summary>
    public DamagePacket SecondaryHitDamage(int effectRow, int level, int quality = 0) => HitDamageOf(effectRow, level, quality, "secondary");

    /// <summary>A charge-spending skill's base hit per removable charge of <paramref name="type"/> (Discharge's
    /// <c>spell_minimum_base_lightning_damage_per_removable_power_charge</c> and so on); empty for other skills.</summary>
    public DamagePacket HitDamagePerCharge(int effectRow, int level, ChargeType type, int quality = 0) =>
        HitDamageOf(effectRow, level, quality, "spell", $"_per_removable_{Charges.Name(type)}_charge");

    private DamagePacket HitDamageOf(int effectRow, int level, int quality, string prefix, string suffix = "")
    {
        var stats = EffectStats(effectRow, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var result = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type switch
            {
                DamageType.Physical => "physical",
                DamageType.Lightning => "lightning",
                DamageType.Cold => "cold",
                DamageType.Fire => "fire",
                DamageType.Chaos => "chaos",
                _ => throw new ArgumentOutOfRangeException(),
            };
            int min = prefix == "spell" ? Value(stats, $"spell_minimum_base_{slug}_damage{suffix}", $"minimum_base_{slug}_damage{suffix}")
                : Value(stats, $"{prefix}_minimum_base_{slug}_damage{suffix}");
            int max = prefix == "spell" ? Value(stats, $"spell_maximum_base_{slug}_damage{suffix}", $"maximum_base_{slug}_damage{suffix}")
                : Value(stats, $"{prefix}_maximum_base_{slug}_damage{suffix}");
            if (min > 0 || max > 0) result = result.Add(type, new DamageRange(min, Math.Max(min, max)));
        }
        return result;
    }

    private int Value(Dictionary<uint, int> stats, params string[] names)
    {
        foreach (string name in names)
            if (_stats.Key(name) is uint key && stats.TryGetValue(key, out int value)) return value;
        return 0;
    }

    /// <summary>How far a gem's stored experience may level it (GrantedEffectStatSetsPerLevel PlayerLevelReq): its level
    /// stalls at the first one the character's level does not meet, as the game's does. The client derives the level from
    /// the experience alone (it does not stall), so the server stops the experience below that level's threshold
    /// (Program.GainGemExperience). 1 when not even the first level's requirement is met (the character cannot use the
    /// gem), 40 when the skill has none.</summary>
    public int MaxLevelAt(int effectRow, int charLevel)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 40;
        int statSet = t.StatSets[effectRow];
        int max = 0;
        foreach (var (level, req) in t.LevelReqsOf(statSet))
            if (req <= charLevel && level > max) max = level;
        return max > 0 ? max : 1;
    }

    /// <summary>The level of a monster's effect for the monster's level: the highest whose PlayerLevelReq it meets, else its
    /// lowest (Izaro's Leap Slam: levels 1, 2, 3 at 33, 68, 75; his Spikes: 30 levels, 22-33 damage at 32).</summary>
    public int MonsterEffectLevel(int effectRow, int monsterLevel)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 1;
        var levels = t.LevelReqsOf(t.StatSets[effectRow]);
        int met = levels.Where(l => l.PlayerLevelReq <= monsterLevel).Select(l => l.GemLevel).DefaultIfEmpty(0).Max();
        return met > 0 ? met : levels.Select(l => l.GemLevel).DefaultIfEmpty(1).Min();
    }

    /// <summary>The character level a gem needs at this level (GrantedEffectStatSetsPerLevel PlayerLevelReq): the
    /// highest requirement of its levels at or below it, 0 when the skill has none.</summary>
    public int PlayerLevelReq(int effectRow, int level)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 0;
        int statSet = t.StatSets[effectRow], req = 0;
        foreach (var (lvl, r) in t.LevelReqsOf(statSet))
            if (lvl <= level && r > req) req = r;
        return req;
    }

    /// <summary>
    /// A skill's resolved damage effectiveness in permyriad. The exported value is an offset from 100%:
    /// Kinetic Blast's 4000 means 14000 (140%), and Cyclone's -1840 means 8160 (81.6%). Missing data returns 0
    /// for the callers' identity fallback. A spell's own base damage is already final (see SpellDamage).
    /// </summary>
    public int DamageEffectiveness(int effectRow, int level)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 0;
        int row = PickLevel(t.StatSetsPerLevel, t.StatSets[effectRow], level);
        return row >= 0 && row < t.Effectiveness.Count ? Math.Max(0, t.Effectiveness[row]) : 0;
    }

    /// <summary>The inherent weapon attack-rate multiplier at the selected GEPL level, in percent.
    /// It multiplies weapon rate separately from player increased speed; absent data keeps ordinary weapon timing.</summary>
    public int AttackSpeedMultiplier(int effectRow, int level) =>
        effectRow >= 0 && level > 0 && _tables.Value is { } t
            && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.AttackSpeedMultipliers[row] : 0;

    /// <summary>The gem's own base attack time at a GEPL level (GrantedEffectsPerLevel AttackTime, ms; Shield Charge
    /// 500), or 0 where the skill uses its weapon's.</summary>
    public int AttackTimeMs(int effectRow, int level) =>
        effectRow >= 0 && level > 0 && _tables.Value is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0
            && row < _attackTimes.Value.Count ? _attackTimes.Value[row] : 0;

    // GrantedEffectsPerLevel AttackTime by row, in the order Tables.PerLevel reads them.
    private readonly Lazy<List<int>> _attackTimes = new(() =>
    {
        var times = new List<int>();
        if (!ClientTables.Has("GrantedEffectsPerLevel")) return times;
        var doc = ClientTables.Table("GrantedEffectsPerLevel");
        foreach (var r in doc)
            times.Add(r.IntOrNull("AttackTime") is int ms ? Math.Max(0, ms) : 0);
        return times;
    });

    /// <summary>The effect's primary stat set's base spell critical chance at this gem level, in permyriad.
    /// Null means missing/invalid data, distinct from a known zero (a spell with no inherent critical chance).</summary>
    public int? SpellCritChance(int effectRow, int level)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count || level < 1) return null;
        int set = t.StatSets[effectRow];
        if (set < 0) return null;
        int row = PickLevel(t.StatSetsPerLevel, set, level);
        return row >= 0 ? t.SpellCritChances[row] : null;
    }

    /// <summary>A gem's requirement for one attribute at a level (0x142476710): the level requirement of that level
    /// (PlayerLevelReq) through the client's curve (3 x requirement + 11), the gem's percentage share (SkillGems
    /// Strength/Dexterity/IntelligenceRequirementPercent) raised to the power 0.9, and 0.5 for a support or 0.7 for a
    /// skill, truncated. Below 14 the client hides it and the requirement is 0. Anchors from the client's tooltips:
    /// 100% at requirement level 70 is 155 (Haste, Fireball), 75% is 119 (Clarity), 60%/40% are 98/68 (Anger), and a
    /// support at 8 is 18 (Added Fire Damage Support).</summary>
    public static int GemAttributeRequirement(int percent, int playerLevelReq, bool support)
    {
        if (percent <= 0) return 0;
        int requirement = (int)((3 * playerLevelReq + 11) * MathF.Pow(percent / 100f, 0.9f) * (support ? 0.5f : 0.7f) + 0.5f);
        return requirement < 14 ? 0 : requirement;
    }

    /// <summary>The ActionTypes Id of a basic action's skill id (HASH16 &amp; 0x3fff: Move 0x2909), or null.</summary>
    public string? ActionName(ushort skillId) => _actions.Value.FirstOrDefault(a => a.Id == skillId).Name;

    /// <summary>The full ActionTypes HASH16 for a GrantedEffects row, used with its server skill id by skill buffs.</summary>
    public ushort? ActionHash(int effectRow) => _effectActions.Value.TryGetValue(effectRow, out ushort hash) ? hash : null;

    /// <summary>
    /// A GrantedEffects row's SkillID, 0 for none: the value the client's own skill-stat builder adds as
    /// <c>skill_behaviour_variation</c> (0x1921; 0x141eefbe3 loads the row's +0xbe). An action takes its data row from it:
    /// a mortar its MonsterMortar row, a geometry channel its GeometryAttack row (by Id) and that row's GeometryChannel.
    /// A record sent over the network does not run that builder, so a record without it reads Id 0, whose GeometryAttack
    /// row has no GeometryChannel: the King in the Mists' GSVoodooKingRitual (SkillID 1116) then crashed the client in
    /// GeometryChannelBase::StartChannelling (0xc0000005 at 0x1419e37bf, the null row's +0x28).
    /// </summary>
    public int BehaviourVariation(int effectRow) => _skillIds.Value.GetValueOrDefault(effectRow);

    /// <summary>The effect's art (GrantedEffects <c>__col23</c>), the number the client takes as <c>skill_art_variation</c>:
    /// SkillArtVariations' Variants of its skill (Izaro's Leap Slam 5 LabyrinthBoss, his Upheaval 5 Ornaments). Sent only on
    /// the records that ask for it: without it the Upheaval drew Glacial Cascade's ice (live, Izaro).</summary>
    public int ArtVariation(int effectRow) => _artVariations.Value.GetValueOrDefault(effectRow);

    private readonly Lazy<Dictionary<int, int>> _artVariations = new(() =>
    {
        var result = new Dictionary<int, int>();
        if (!ClientTables.Has("GrantedEffects")) return result;
        int row = 0;
        foreach (var effect in ClientTables.Table("GrantedEffects"))
        {
            int index = row++;
            if (effect.IntOrNull("__col23") is int value && value > 0) result[index] = value;
        }
        return result;
    });

    private readonly Lazy<Dictionary<int, int>> _skillIds = new(() =>
    {
        var result = new Dictionary<int, int>();
        if (!ClientTables.Has("GrantedEffects")) return result;
        var effects = ClientTables.Table("GrantedEffects");
        int row = 0;
        foreach (var effect in effects)
        {
            int index = row++;
            if (effect.IntOrNull("SkillID") is int value && value > 0)
                result[index] = value;
        }
        return result;
    });

    private readonly Lazy<Dictionary<int, ushort>> _effectActions = new(() =>
    {
        if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills") || !ClientTables.Has("ActionTypes")) return [];
        var effects = ClientTables.Table("GrantedEffects");
        var active = ClientTables.Table("ActiveSkills");
        var actions = ClientTables.Table("ActionTypes");
        var result = new Dictionary<int, ushort>();
        int row = 0;
        foreach (var effect in effects)
        {
            int index = row++;
            if (effect.IntOrNull("ActiveSkill") is not int a
                || a < 0 || a >= active.Count) continue;
            var entry = active[a];
            if (entry.IntOrNull("ActionType") is not int t
                || t < 0 || t >= actions.Count) continue;
            result[index] = (ushort)actions[t].Int("HASH16");
        }
        return result;
    });

    /// <summary>A basic action's skill id by ActionTypes Id ("Move" → 0x2909), or null.</summary>
    public ushort? ActionSkillId(string name) => _actions.Value.FirstOrDefault(a => a.Name == name) is { Name: not null } a ? a.Id : null;

    private readonly Lazy<List<(ushort Id, string Name)>> _actions = new(() =>
    {
        if (!ClientTables.Has("ActionTypes")) return [(0x2909, "Move")];
        var d = ClientTables.Table("ActionTypes");
        return [.. d.Select(r => ((ushort)(r.Int("HASH16") & 0x3fff), r.Text("Id") ?? ""))];
    });

    /// <summary>A GrantedEffects row's Id, or null.</summary>
    public string? EffectId(int effectRow) =>
        _tables.Value?.EffectIds.GetValueOrDefault(effectRow);

    /// <summary>Whether a GrantedEffects row is a support, from its actual row (not its translated name).</summary>
    public bool IsSupportEffect(int effectRow) => _supportEffects.Value.Contains(effectRow);

    private readonly Lazy<HashSet<int>> _supportEffects = new(() =>
    {
        var result = new HashSet<int>();
        if (ClientTables.TableOrNull("GrantedEffects") is not { } d) return result;
        int row = 0;
        foreach (var r in d)
        {
            if (r.Bool("IsSupport"))
                result.Add(row);
            row++;
        }
        return result;
    });

    /// <summary>
    /// Whether the effect's ActiveSkills row is manually cast. A trigger-only row has no bar use; callers must
    /// not turn one into a manually cast skill merely because it has an active-skill record.
    /// </summary>
    public bool IsManuallyCasted(int effectRow) => _manuallyCastEffects.Value.Contains(effectRow);

    private readonly Lazy<HashSet<int>> _manuallyCastEffects = new(() =>
    {
        var result = new HashSet<int>();
        if (ClientTables.TableOrNull("GrantedEffects") is not { } effects
            || ClientTables.TableOrNull("ActiveSkills") is not { } active) return result;
        int row = 0;
        foreach (var effect in effects)
        {
            if (effect.IntOrNull("ActiveSkill") is int index and >= 0 && index < active.Count
                && active[index].Bool("IsManuallyCasted")) result.Add(row);
            row++;
        }
        return result;
    });

    /// <summary>A GrantedEffectsPerLevel row's GrantedEffects row and level, or null.</summary>
    public (int Effect, int Level)? EffectsPerLevel(int row) =>
        _tables.Value is { } t && row >= 0 && row < t.PerLevel.Count ? t.PerLevel[row] : null;

    /// <summary>A GrantedEffects row by its Id, or -1.</summary>
    public int EffectRow(string effectId) =>
        _tables.Value is { } t && t.EffectRows.TryGetValue(effectId, out var effect)
            ? _modeEffects.Value.GetValueOrDefault(effect.Row, effect.Row) : -1;

    /// <summary>
    /// The resources a skill reserves at a level: GrantedEffectsPerLevel's mana and life flat/percent fields
    /// (permyriad: 5000 = 50%). Null when the row reserves neither resource.
    /// </summary>
    public ReservationCost? Reservation(int effectRow, int level)
    {
        if (_tables.Value is not { } t) return null;
        int row = PickLevel(t.PerLevel, effectRow, level);
        if (row < 0 || row >= t.Reservations.Count) return null;
        var r = t.Reservations[row];
        return r.ManaFlat == 0 && r.ManaPercent == 0 && r.LifeFlat == 0 && r.LifePercent == 0
            ? null : new(r.ManaFlat, r.ManaPercent, r.LifeFlat, r.LifePercent);
    }

    public readonly record struct ReservationCost(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)
    {
        // The legacy one-pool contract exposes Mana when present, otherwise Life. Raw-pool callers use the
        // Mana*/Life* fields so they never have to infer the resource from a zero value.
        public bool IsLife => ManaFlat == 0 && ManaPercent == 0;
        public int Flat => IsLife ? LifeFlat : ManaFlat;
        public int Percent => IsLife ? LifePercent : ManaPercent;
    }

    public bool LifeReservation(int effectRow, int level) => _tables.Value is { } t
        && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0
        && (t.Reservations[row].LifeFlat != 0 || t.Reservations[row].LifePercent != 0);

    /// <summary>The Vaal souls a use of a skill needs at a level (GrantedEffectsPerLevel VaalSouls; 0 for skills that are
    /// not Vaal skills).</summary>
    public int VaalSouls(int effectRow, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? Math.Max(0, t.VaalSouls[row]) : 0;

    public readonly record struct VaalDefinition(uint ActiveSkillsRow, int BaseCost, int StoredUses);

    /// <summary>Null for non-Vaal skills or missing/invalid catalog references. Never substitutes GE/GEPL identity.</summary>
    public VaalDefinition? VaalBudget(int effectRow, int level)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.ActiveSkills.Count
            || t.ActiveSkills[effectRow] is not int active || active < 0) return null;
        int row = PickLevel(t.PerLevel, effectRow, level);
        return row >= 0 && t.VaalSouls[row] >= 0 && t.VaalStoredUses[row] > 0
            ? new((uint)active, t.VaalSouls[row], t.VaalStoredUses[row]) : null;
    }

    public int SoulGainPreventionMs(int effect, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effect, level) is int row and >= 0 ? t.SoulPrevention[row] : 0;

    public int BaseRageCost(int effect, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effect, level) is int row and >= 0 ? t.RageCost[row] : 0;

    /// <summary>The resources a GrantedEffects Id reserves at a level, or null.</summary>
    public ReservationCost? Reservation(string effectId, int level) =>
        EffectRow(effectId) is int row and >= 0 ? Reservation(row, level) : null;

    /// <summary>A GrantedEffects row's CastTime in ms (0 when unknown).</summary>
    public int CastMs(int effectRow) =>
        _tables.Value is { } t && effectRow >= 0 && effectRow < t.Effects.Count ? t.Effects[effectRow].CastTime : 0;

    /// <summary>A GrantedEffects row's cooldown at a level in ms (GEPL Cooldown: Haste 1200, Righteous Fire 300), 0 when
    /// it has none.</summary>
    public int CooldownMs(int effectRow, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effectRow, level) is int gepl and >= 0 ? t.Cooldowns[gepl].Ms : 0;

    public int CooldownUses(int effectRow, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.Cooldowns[row].Uses : 0;

    /// <summary>A GrantedEffects row's GEPL CooldownBypassType at a level (<see cref="CooldownBypass"/>), 0 when none.</summary>
    public int CooldownBypassType(int effectRow, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.Cooldowns[row].Bypass : 0;

    public int CooldownGroup(int effectRow, int level) =>
        _tables.Value is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.Cooldowns[row].Group : 36;

    public bool CooldownPausesWhileActive(int effectRow) =>
        _tables.Value is { } t && effectRow >= 0 && effectRow < t.Effects.Count && t.Effects[effectRow].CooldownPauses;

    /// <summary>Whether a GrantedEffects row's skill is instant: its ActiveSkills row has the ActiveSkillType Instant (Haste,
    /// Righteous Fire). An instant skill is used while moving, without an action of its own.</summary>
    public bool IsInstant(int effectRow) => _instantEffects.Value.Contains(effectRow);

    /// <summary>A movement skill (ActiveSkillType Movement: Leap Slam, Whirling Blades, Flame Dash …): the player ends
    /// where it was aimed.</summary>
    public bool IsMovement(int effectRow) => _movementEffects.Value.Contains(effectRow);
    public bool IsChannelled(int effectRow) => _skillTypes.Channel.Value.Contains(effectRow);
    public bool IsTravel(int effectRow) => _skillTypes.Travel.Value.Contains(effectRow);
    public bool IsGuard(int effectRow) => _skillTypes.Guard.Value.Contains(effectRow);
    /// <summary>A skill that needs a shield (ActiveSkillType RequiresShield: Shield Charge, Shield Crush …).</summary>
    public bool RequiresShield(int effectRow) => _skillTypes.RequiresShield.Value.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's skill is an attack (ActiveSkillType Attack: the default attack, Holy
    /// Strike, Flicker Strike …): it swings with the weapon's attack time and its use is paced like the default
    /// attack's, unlike a spell, which the client paces by its cast time.</summary>
    public bool IsAttack(int effectRow) => _attackEffects.Value.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's skill classifies Melee (the ActiveSkillType). Decides whether an attack
    /// gem's hit takes melee_damage_+% (the default attack does by its weapon profile instead).</summary>
    public bool IsMelee(int effectRow) => _meleeEffects.Value.Contains(effectRow);

    public bool IsSpell(int effectRow) => _spellEffects.Value.Contains(effectRow);
    public bool IsProjectile(int effectRow) => _projectileEffects.Value.Contains(effectRow);
    // Generic monster Melee also carries Projectile. Actual melee classification takes precedence.
    public bool IsRangedAttack(int effectRow) => IsAttack(effectRow) && IsProjectile(effectRow) && !IsMelee(effectRow);

    /// <summary>Classify the gem's base-item row from GemTags/SkillGems metadata, not its display name.</summary>
    public bool FamilyStatRaises(string stat, int gemRow) =>
        _stats.Key(stat) is uint key && _families.Value.Raises((int)key - 1, gemRow);

    public bool IsExceptional(int gemRow) => _families.Value.IsExceptional(gemRow);

    readonly Lazy<SkillGemFamilies> _families = new(() => new(folder));

    /// <summary>The ItemClasses rows a GrantedEffects row's skill can be used with (its ActiveSkill's
    /// WeaponRestriction_ItemClassesKeys: Burning Arrow 13, Bow); empty for a skill any hands can use. The client greys
    /// out a skill when neither hand fits (11-skills.md §10a).</summary>
    public IReadOnlySet<int> WeaponRestrictions(int effectRow) =>
        _weaponRestrictions.Value.TryGetValue(effectRow, out var rows) ? rows : FrozenSet<int>.Empty;

    private readonly Lazy<Dictionary<int, IReadOnlySet<int>>> _weaponRestrictions = new(() =>
    {
        if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills")) return [];
        var active = ClientTables.Table("ActiveSkills");
        var restricted = active.Select(r =>
            r.Ints("WeaponRestriction_ItemClassesKeys").ToFrozenSet()).ToList();
        var effects = ClientTables.Table("GrantedEffects");
        var result = new Dictionary<int, IReadOnlySet<int>>();
        foreach (var (r, i) in effects.Select((r, i) => (r, i)))
            if (r.IntOrNull("ActiveSkill") is int row && row >= 0 && row < restricted.Count && restricted[row].Count > 0)
                result[i] = restricted[row];
        return result;
    });

    /// <summary>Whether a GrantedEffects row's ActiveSkill has the Warcry type.</summary>
    public bool IsWarcry(int effectRow) => _warcryEffects.Value.Contains(effectRow);

    /// <summary>A Strike skill: ActiveSkillType MeleeSingleTarget, whose FlagStat is <c>skill_is_strike_skill</c>, the
    /// stat the client's own strike virtuals test (<c>0x14289ced0</c>, <c>0x1428b7e90</c>). The default attack is one
    /// (PoB's Default Attack has MeleeSingleTarget too).</summary>
    public bool IsStrike(int effectRow) => _strikeEffects.Value.Contains(effectRow);

    /// <summary>ActiveSkillType Vaal (FlagStat <c>skill_is_vaal_skill</c>).</summary>
    public bool IsVaal(int effectRow) => _vaalEffects.Value.Contains(effectRow);

    /// <summary>A Nova skill (ActiveSkillType Nova: Ice Nova, Discharge, Shock Nova, but also Shockwave Totem and
    /// Stormblast Mine).</summary>
    public bool IsNova(int effectRow) => _novaEffects.Value.Contains(effectRow);

    /// <summary>A self-targeted skill (ActiveSkillTargetTypes 4 or 10: Ice Nova, Discharge, Vaal Cold Snap), whose use
    /// the client centres on the caster (FUN_1419439e0). Totems, mines and traps are left out: their effect goes off at
    /// the object they place.</summary>
    public bool IsSelfTargeted(int effectRow) => _selfTargetedEffects.Value.Contains(effectRow);

    /// <summary>Aura metadata, including only supports already accepted by GemSupports.Resolve.</summary>
    public bool IsAura(int effectRow, IEnumerable<int>? acceptedSupports = null) =>
        _auraEffects.Value.Contains(effectRow)
        || acceptedSupports?.Any(row => _addedAuraEffects.Value.Contains(row)) == true;
    /// <summary>The native Brand skill type, independently of display name or gem tags.</summary>
    public bool IsBrand(int effectRow) => _brandEffects.Value.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's ActiveSkill has an ActiveSkillType by its data Id (Trap, Mine, Totem,
    /// Channel, ...): the named helpers cover the common ones, this reads any type a caller names.</summary>
    public bool HasType(int effectRow, string typeId) => _skillTypes.HasType(effectRow, typeId);

    /// <summary>The ActiveSkillType Ids a support adds to the skill it supports (Trap Support adds Trap, Ballista Totem
    /// Support adds Totem): the run uses these to keep mechanics it cannot place out of the support pool.</summary>
    public IReadOnlyList<string> AddedTypes(int effectRow) => _skillTypes.AddedTypes(effectRow);

    // The effects of one ActiveSkillType each: ActiveSkillType, ActiveSkills and GrantedEffects are parsed once for all
    // of them (each set used to parse the three tables again: ~100 MB of JSON per catalogue at the first spawn).
    private readonly SkillTypeSets _skillTypes = new(folder);
    private Lazy<HashSet<int>> _instantEffects => _skillTypes.Instant;
    private Lazy<HashSet<int>> _movementEffects => _skillTypes.Movement;
    private Lazy<HashSet<int>> _attackEffects => _skillTypes.Attack;
    private Lazy<HashSet<int>> _meleeEffects => _skillTypes.Melee;
    private Lazy<HashSet<int>> _spellEffects => _skillTypes.Spell;
    private Lazy<HashSet<int>> _projectileEffects => _skillTypes.Projectile;
    private Lazy<HashSet<int>> _warcryEffects => _skillTypes.Warcry;
    private Lazy<HashSet<int>> _strikeEffects => _skillTypes.Strike;
    private Lazy<HashSet<int>> _vaalEffects => _skillTypes.Vaal;
    private Lazy<HashSet<int>> _novaEffects => _skillTypes.Nova;
    private Lazy<HashSet<int>> _selfTargetedEffects => _skillTypes.SelfTargeted;
    private Lazy<HashSet<int>> _auraEffects => _skillTypes.Aura;
    private Lazy<HashSet<int>> _curseEffects => _skillTypes.Curse;
    private Lazy<HashSet<int>> _addedAuraEffects => _skillTypes.AddedAura;
    private Lazy<HashSet<int>> _brandEffects => _skillTypes.Brand;

    /// <summary>The GrantedEffects rows whose active skill has an ActiveSkillType (or, <c>added</c>, that add the type to
    /// the skill they support), from one read of the three tables. Each set is computed on first use.</summary>
    sealed class SkillTypeSets
    {
        sealed record Rows(string?[] TypeIds, int[][] ActiveSkillTypes, int?[] EffectSkills, int[][] AddedTypes);

        readonly Lazy<Rows?> _rows;

        public SkillTypeSets(string folder)
        {
            _rows = new(() => Load(folder));
            Instant = Of("Instant"); Movement = Of("Movement"); Attack = Of("Attack"); Melee = Of("Melee");
            Spell = Of("Spell"); Projectile = Of("Projectile"); Warcry = Of("Warcry"); Aura = Of("Aura");
            Curse = Of("AppliesCurse"); AddedAura = Of("Aura", added: true); Brand = Of("Brand");
            Travel = Of("Travel"); Guard = Of("Guard"); Channel = Of("Channel");
            Strike = Of("MeleeSingleTarget"); Vaal = Of("Vaal"); Nova = Of("Nova"); RequiresShield = Of("RequiresShield");
            SelfTargeted = new(() =>
            {
                var self = EffectsOfTargetType(folder, 4, 10);
                foreach (string placed in new[] { "SummonsTotem", "RemoteMined", "Trapped" }) self.ExceptWith(Effects(_rows.Value, placed, false));
                return self;
            });
        }

        public Lazy<HashSet<int>> Instant { get; }
        public Lazy<HashSet<int>> Movement { get; }
        public Lazy<HashSet<int>> Attack { get; }
        public Lazy<HashSet<int>> Melee { get; }
        public Lazy<HashSet<int>> Spell { get; }
        public Lazy<HashSet<int>> Projectile { get; }
        public Lazy<HashSet<int>> Warcry { get; }
        public Lazy<HashSet<int>> Aura { get; }
        public Lazy<HashSet<int>> Curse { get; }
        public Lazy<HashSet<int>> AddedAura { get; }
        public Lazy<HashSet<int>> Brand { get; }
        public Lazy<HashSet<int>> Travel { get; }
        public Lazy<HashSet<int>> Channel { get; }
        public Lazy<HashSet<int>> Guard { get; }
        public Lazy<HashSet<int>> Strike { get; }
        public Lazy<HashSet<int>> Vaal { get; }
        public Lazy<HashSet<int>> Nova { get; }
        public Lazy<HashSet<int>> RequiresShield { get; }
        public Lazy<HashSet<int>> SelfTargeted { get; }

        Lazy<HashSet<int>> Of(string typeId, bool added = false) => new(() => Effects(_rows.Value, typeId, added));

        // Any named type a caller asks for (Trap, Mine, Totem, ...): resolved once and reused, like the fixed sets.
        readonly Dictionary<string, Lazy<HashSet<int>>> _byId = new(StringComparer.Ordinal);

        public bool HasType(int effectRow, string typeId)
        {
            lock (_byId)
            {
                if (!_byId.TryGetValue(typeId, out var effects)) _byId[typeId] = effects = Of(typeId);
                return effects.Value.Contains(effectRow);
            }
        }

        public IReadOnlyList<string> AddedTypes(int effectRow)
        {
            var rows = _rows.Value;
            if (rows is null || effectRow < 0 || effectRow >= rows.AddedTypes.Length) return [];
            return [.. rows.AddedTypes[effectRow]
                .Select(i => i >= 0 && i < rows.TypeIds.Length ? rows.TypeIds[i] ?? "" : "")
                .Where(id => id.Length > 0)];
        }

        static HashSet<int> Effects(Rows? rows, string typeId, bool added)
        {
            if (rows is null) return [];
            int type = Array.IndexOf(rows.TypeIds, typeId);
            if (type < 0) return [];
            if (added)
                return [.. Enumerable.Range(0, rows.AddedTypes.Length).Where(i => rows.AddedTypes[i].Contains(type))];
            var skills = Enumerable.Range(0, rows.ActiveSkillTypes.Length).Where(i => rows.ActiveSkillTypes[i].Contains(type)).ToHashSet();
            return [.. Enumerable.Range(0, rows.EffectSkills.Length).Where(i => rows.EffectSkills[i] is int skill && skills.Contains(skill))];
        }

        static Rows? Load(string folder)
        {
            if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills") || !ClientTables.Has("ActiveSkillType")) return null;
            static int[] Ints(ClientRow row, string name) => row.Ints(name);
            string?[] typeIds;
            var types = ClientTables.Table("ActiveSkillType");
            typeIds = [.. types.Select(r => r.Text("Id"))];
            int[][] activeTypes;
            var active = ClientTables.Table("ActiveSkills");
            activeTypes = [.. active.Select(r => Ints(r, "ActiveSkillTypes"))];
            var effects = ClientTables.Table("GrantedEffects");
            return new(typeIds, activeTypes,
                [.. effects.Select(r =>
                    r.IntOrNull("ActiveSkill"))],
                [.. effects.Select(r => Ints(r, "AddedActiveSkillTypes"))]);
        }
    }

    /// <summary>The GrantedEffects rows whose ActiveSkill has one of these ActiveSkillTargetTypes.</summary>
    static HashSet<int> EffectsOfTargetType(string folder, params int[] targetTypes)
    {
        if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills")) return [];
        var active = ClientTables.Table("ActiveSkills");
        var skills = active.Select((r, i) => (r, i))
            .Where(a => a.r.Ints("ActiveSkillTargetTypes").Any(targetTypes.Contains))
            .Select(a => a.i).ToHashSet();
        var effects = ClientTables.Table("GrantedEffects");
        return [.. effects.Select((r, i) => (r, i))
            .Where(e => e.r.IntOrNull("ActiveSkill") is int a && skills.Contains(a))
            .Select(e => e.i)];
    }

    /// <summary>A weapon base's WeaponTypes row by its BaseItemTypes row: attack time (ms), physical damage, range.</summary>
    public (int AttackMs, int Min, int Max, int Range)? WeaponBase(int baseRow) => _weaponTypes.Value.TryGetValue(baseRow, out var w) ? w : null;

    private readonly Lazy<Dictionary<int, (int, int, int, int)>> _weaponTypes = new(() =>
    {
        var result = new Dictionary<int, (int, int, int, int)>();
        if (!ClientTables.Has("WeaponTypes")) return result;
        var d = ClientTables.Table("WeaponTypes");
        foreach (var r in d)
            result.TryAdd(r.Int("BaseItemTypesKey"), (r.Int("Speed"), r.Int("DamageMin"),
                r.Int("DamageMax"), r.Int("RangeMax")));
        return result;
    });

    /// <summary>A projectile type for an effect: the Projectiles.dat row whose Id is Metadata/Projectiles/(GrantedEffects
    /// Id), e.g. Fireball → Metadata/Projectiles/Fireball at speed 1040, or null. The real link between a skill and its
    /// projectile is not known yet; the Gateway matches the Id, and <see cref="ProjectileFallbacks"/> covers the gems
    /// whose row is not named after their effect. Kinetic Blast (ClusterBurst) fires the wand attack's own projectile
    /// (3.29 has no ClusterBurst row); its explosion is a separate impact art (Program.Combat).</summary>
    public (string Id, int Speed)? ProjectileFor(int effectRow)
    {
        if (_tables.Value is not { } t) return null;
        string? effect = t.EffectIds.GetValueOrDefault(effectRow);
        if (effect is null) return null;
        foreach (string id in ProjectileCandidates(effect))
            if (_projectiles.Value.TryGetValue(id, out int speed)) return (id, speed);
        return null;
    }

    /// <summary>The Projectiles.dat rows to try for an effect, best first: its own row, then the row of the effect it is a
    /// variant of. 3.29 files the Royale and transfigured duplicates under the base skill's name plus a suffix
    /// (<c>LightningArrowRoyale</c>, <c>FireballAltX</c>), and those rows are the base skill's, so the Lightning Arrow gem
    /// — which grants <c>LightningArrowRoyale</c> — fires nothing at all until this second step is taken.</summary>
    static IEnumerable<string> ProjectileCandidates(string effect)
    {
        yield return ProjectileFallbacks.GetValueOrDefault(effect, "Metadata/Projectiles/" + effect);
        string baseEffect = effect;
        foreach (string suffix in VariantSuffixes)
            if (baseEffect.EndsWith(suffix, StringComparison.Ordinal)) { baseEffect = baseEffect[..^suffix.Length]; break; }
        if (baseEffect.Length != 0 && baseEffect != effect)
            yield return ProjectileFallbacks.GetValueOrDefault(baseEffect, "Metadata/Projectiles/" + baseEffect);
    }

    /// <summary>The suffixes GrantedEffects Ids carry on a duplicate of a base effect (3.29: <c>LightningArrowRoyale</c>,
    /// <c>FireballAltX</c>, <c>CobraLashAltY</c>). Ordered longest-first so that no shorter suffix hides a longer one.</summary>
    static readonly string[] VariantSuffixes = ["Royale", "AltX", "AltY", "AltZ"];

    // Granted effects whose Projectiles.dat row is not named after them, by effect Id. Kinetic Blast's effect is
    // ClusterBurst (the skill data names no row); it uses the wand attack's projectile at the same 3260 speed. Tornado
    // Shot's arrow is TornadoShotArrow (2173); without it every use was refused while the client had stopped to shoot,
    // and the server walked on (live 2026-10-29). Spark's transfigured and Vaal versions fire Spark's own row.
    // Elemental Hit fires ElementalHitArrowFire/Cold/Lightning by the element it picked, a choice the server does not model
    // yet, so both the gem and its transfigured (Spectrum) version take the combined arrow's art.
    // Rolling Magma (once Magma Orb) creates MagmaOrbShort (Frida 2026-11-01, trace-projectile-create.js); without the row every
    // cast was refused while the client drew its orb. Winter Orb's channelled orb fires FrostFuryMortar (its Projectiles row,
    // one per volley); the effect's own name has no row, so the channel was refused while the client played it.
    // Spectral Throw's is ThrownWeapon; without it every use was refused (live 2026-11-02).
    // Kinetic Bolt (ZigZagProjectile) creates ZigZagWandProjectile (3.29.3c RE); its transfigured/public variants share
    // the same ActionType and primary projectile row.
    // Essence Drain keeps its old name's row, Siphon (Metadata/Effects/Spells/siphon), as do its transfigured versions.
    // Splitting Steel's is ImpactingSteelProjectile: the skill's own row name is gone from 3.29 Projectiles.dat, whose only
    // steel-family rows are this one, ImpactingSteelProjectileSecondary (its split shards), ShatteringSteelProjectile
    // (Shattering Steel, still a separate effect and gem) and the Lancing Steel ones. The skill's own art still carries the
    // name: ActiveSkills splitting_steel (+ its two transfigured variants) draws ImpactingSteelSkill.dds, and the gem's
    // ItemVisualIdentity SkillGemSplittingSteel is Art/2DItems/Gems/ImpactingSteelGem.dds (the archive has no
    // SplittingSteel art at all). Without it every use was refused (live 2026-11-09).
    static readonly Dictionary<string, string> ProjectileFallbacks = new()
    {
        ["KineticBlast"] = "Metadata/Projectiles/WandProjectile",
        ["KineticBlastAltX"] = "Metadata/Projectiles/WandProjectile",
        ["KineticBlastAltY"] = "Metadata/Projectiles/WandProjectile",
        ["KineticBolt"] = "Metadata/Projectiles/ZigZagWandProjectile",
        ["KineticBoltAltX"] = "Metadata/Projectiles/ZigZagWandProjectile",
        ["KineticBoltAltY"] = "Metadata/Projectiles/ZigZagWandProjectile",
        ["EtherealKnives"] = "Metadata/Projectiles/EtherealKnivesProjectile",
        ["EtherealKnivesAltX"] = "Metadata/Projectiles/EtherealKnivesProjectile",
        ["EtherealKnivesAltY"] = "Metadata/Projectiles/EtherealKnivesProjectile",
        ["IceShot"] = "Metadata/Projectiles/IceArrow",
        ["RampageDaemonStunNova"] = "Metadata/Projectiles/RampageProjectile",
        ["RampageDaemonBlindNova"] = "Metadata/Projectiles/RampageProjectile",
        ["RampageDaemonSuperRepeatNova"] = "Metadata/Projectiles/RampageProjectile",
        ["TornadoShot"] = "Metadata/Projectiles/TornadoShotArrow",
        ["TornadoShotAltX"] = "Metadata/Projectiles/TornadoShotArrow",
        ["TornadoShotAltY"] = "Metadata/Projectiles/TornadoShotArrow",
        ["SparkAltX"] = "Metadata/Projectiles/Spark",
        ["SparkAltY"] = "Metadata/Projectiles/Spark",
        ["VaalFireball"] = "Metadata/Projectiles/Fireball",
        ["VaalSpark"] = "Metadata/Projectiles/Spark",
        ["Twister"] = "Metadata/Projectiles/SandTornadoSpark",
        ["ElementalHit"] = "Metadata/Projectiles/ElementalHitArrowCombined",
        ["ElementalHitAltX"] = "Metadata/Projectiles/ElementalHitArrowCombined",
        ["RollingMagma"] = "Metadata/Projectiles/MagmaOrbShort",
        ["WinterOrb"] = "Metadata/Projectiles/FrostFuryMortar",
        ["SpectralThrow"] = "Metadata/Projectiles/ThrownWeapon",
        ["LightningStrike"] = "Metadata/Projectiles/LightningSlash",
        ["EssenceDrain"] = "Metadata/Projectiles/Siphon",
        ["EssenceDrainAltX"] = "Metadata/Projectiles/Siphon",
        ["EssenceDrainAltY"] = "Metadata/Projectiles/Siphon",
        ["SplitArrow"] = "Metadata/Projectiles/SplitArrowDefault",
        ["SplitArrowAltX"] = "Metadata/Projectiles/SplitArrowDefault",
        ["SplitArrowAltY"] = "Metadata/Projectiles/SplitArrowDefault",
        ["VaalSplitArrow"] = "Metadata/Projectiles/VaalSplitArrowDefault",
        ["SplittingSteel"] = "Metadata/Projectiles/ImpactingSteelProjectile",
        ["SplittingSteelAltX"] = "Metadata/Projectiles/ImpactingSteelProjectile",
        ["SplittingSteelAltY"] = "Metadata/Projectiles/ImpactingSteelProjectile",
    };

    /// <summary>A Projectiles.dat row by its Id (<c>Metadata/Projectiles/DefaultArrow</c> for a bow's ordinary attack).</summary>
    public (string Id, int Speed)? Projectile(string id) =>
        _projectiles.Value.TryGetValue(id, out int speed) ? (id, speed) : null;

    /// <summary>A Projectiles.dat row's max(CollisionRadius, CollisionCandidateRadius): the reach of the client's object
    /// contact for a projectile it draws itself (0x141e35a50 → 0x141e4b140, 11-skills.md "Hit").</summary>
    public int? ProjectileContactCells(string id) => _projectileContacts.Value.TryGetValue(id, out int cells) ? cells : null;

    private readonly Lazy<Dictionary<string, int>> _projectileContacts = new(() =>
    {
        var result = new Dictionary<string, int>();
        if (ClientTables.TableOrNull("Projectiles") is not { } d) return result;
        static int Cells(ClientRow r, string key) => r.IntOrNull(key) ?? 0;
        foreach (var r in d)
            if (r.Text("Id") is { } id)
                result.TryAdd(id, Math.Max(Cells(r, "CollisionRadius"), Cells(r, "CollisionCandidateRadius")));
        return result;
    });

    private readonly Lazy<Dictionary<string, int>> _projectiles = new(() =>
    {
        var result = new Dictionary<string, int>();
        if (!ClientTables.Has("Projectiles")) return result;
        var d = ClientTables.Table("Projectiles");
        foreach (var r in d)
            if (r.Text("Id") is { } id)
                result.TryAdd(id, r.IntOrNull("ProjectileSpeed") is int v ? v : 0);
        return result;
    });

    /// <summary>A Projectiles.dat row's first AOFiles entry: the art the client plays for that projectile object
    /// (SplitArrowDefault's split_arrow.ao, which Arrow Nova attaches to its rain rig).</summary>
    public string? ProjectileAo(string id) =>
        _projectileAos.Value.TryGetValue(id, out var ao) ? ao : null;

    private readonly Lazy<Dictionary<string, string>> _projectileAos = new(() =>
    {
        var result = new Dictionary<string, string>();
        if (!ClientTables.Has("Projectiles")) return result;
        var d = ClientTables.Table("Projectiles");
        foreach (var r in d)
            if (r.Text("Id") is { } id && r.Texts("AOFiles") is [var ao, ..])
                result.TryAdd(id, ao);
        return result;
    });

    /// <summary>One clip of a projectile type's rig: its name, index in the rig's animation list (what 0x1b2 plays), length
    /// and whether it plays once (client.db's projectile_clips, from scripts/re/projectile_clips.py).</summary>
    public sealed record ProjectileClip(string Name, uint Index, int Ms, bool Once);

    /// <summary>What the client needs to show a server-created object of a projectile type: the .ao's default animation,
    /// if it declares one (the wand bolt's <c>projectile_01</c>: it flies by itself), the flight clip (Projectiles.dat
    /// LoopAnimationIds, the Sand Spitter mortar's <c>proj</c>: the object is invisible until it plays) and the impact
    /// clip (ImpactAnimationIds, <c>end</c>).</summary>
    public sealed record ProjectileClips(string? DefaultAnimation, ProjectileClip? Flight, ProjectileClip? Impact);

    public ProjectileClips? Clips(string projectileId) =>
        _projectileClips.Value.TryGetValue(projectileId, out var clips) ? clips : null;

    private readonly Lazy<Dictionary<string, ProjectileClips>> _projectileClips = new(() =>
    {
        var result = new Dictionary<string, ProjectileClips>();
        foreach (var r in ClientTables.TableOrEmpty("projectile_clips"))
        {
            ProjectileClip? Clip(string key) =>
                r.TextOrNull(key + "_name") is { } name && r.LongOrNull(key + "_index") is { } index && r.IntOrNull(key + "_ms") is { } length
                    ? new(name, (uint)index, length, r.BoolOrNull(key + "_once") ?? false) : null;
            result[r.Text("type")!] = new(r.TextOrNull("default"), Clip("flight"), Clip("impact"));
        }
        return result;
    });

    /// <summary>A monster skill's projectile (Projectiles.dat Id, speed in world units per second, whether it is lobbed at
    /// the aim, collision radius in cells). Speed is the row's ProjectileSpeed; <c>Lobbed</c> flights land where they were
    /// aimed and damage an area there.</summary>
    public sealed record MonsterProjectileInfo(string Metadata, int Speed, bool Lobbed, double CollisionRadius)
    {
        /// <summary>How many projectiles one release creates (VoodooKingBugRend's six BugSoulrends; a mortar's from its
        /// skill, MonsterProjectile; one otherwise).</summary>
        public int Count { get; init; } = 1;
        /// <summary>The Animation.dat Id of the clips the client plays for the skill (MonsterMortar.Animation: the Sand
        /// Spitter mortar's <c>SpellProjectile</c>, row 20), the key of the variety's ACT pool; null when the row names none.</summary>
        public string? AnimationKey { get; init; }
        /// <summary>The MonsterMortar row the client draws for a <c>monster_mortar_attack</c> skill (its GrantedEffects
        /// SkillID: 183 for the Sand Spitter's, 55 for a Cannibal's ThrowFire); 0 for any other projectile, for the spell
        /// forms (<c>monster_mortar</c>, not traced) and for the skills whose SkillID is row 0.</summary>
        public int Variation { get; init; }
        /// <summary>The skill's <c>projectile_minimum_range</c> in grid cells (0x141b6c630; the Spitter's 50).</summary>
        public int MinimumRange { get; init; }
        public int SpreadRadius { get; init; }
        /// <summary>The spread of a multi-projectile release: a fan by default, a full turn for a ring.</summary>
        public double SpreadRadians { get; init; } = Math.PI / 6;
        /// <summary>A lobbed shot's terminal area radius in cells (monster rows carry none; 10 is the development
        /// fallback, and a boss skill may name its own).</summary>
        public double AreaRadius { get; init; }
    }

    /// <summary>One clip of a monster's attack pool (client.db's monster_contact_clips, from scripts/re/monster_contacts.py): its name,
    /// its index in the rig's animation list, its length, and its contact keyframe's time and position when it has one
    /// (the Sand Spitter's attack1: 375 of 1000 ms, 45.9 units behind the model and 71.6 up).</summary>
    public sealed record MonsterClipContact(string Animation, uint Index, int LengthMs, int? ContactMs, ContactPoint? Point)
    {
        /// <summary>The contact as a fraction of the clip, clamped to it: an .ao event time is independent of the rig's
        /// clip length, and a release past the attack's end would miss its busy period.</summary>
        public double? ContactFraction => ContactMs is int ms && LengthMs > 0 ? Math.Clamp((double)ms / LengthMs, 0, 1) : null;
    }

    /// <summary>The clip the client plays for a monster's action: the variety's ACT pool for the skill's animation key
    /// (<c>SpellProjectile</c> for the Spitter's mortar), chosen as the client chooses it, with the native selector seeded
    /// by the action's sequence (AnimationVariantSelector). Null without data for the variety or key.</summary>
    public MonsterClipContact? MonsterContact(string varietyId, string animationKey, ushort sequence)
    {
        if (!_monsterContacts.Value.TryGetValue(varietyId, out var pools) || !pools.TryGetValue(animationKey, out var pool)
            || pool.Length == 0) return null;
        // Equal weights for every clip; the pool's size comes from a data file, so only a small one lives on the stack.
        Span<uint> weights = pool.Length <= 32 ? stackalloc uint[pool.Length] : new uint[pool.Length];
        weights.Fill(1);
        return pool[AnimationVariantSelector.Select(sequence, weights)];
    }

    private readonly Lazy<Dictionary<string, Dictionary<string, MonsterClipContact[]>>> _monsterContacts = new(() =>
    {
        var result = new Dictionary<string, Dictionary<string, MonsterClipContact[]>>(StringComparer.Ordinal);
        if (ClientTables.TableOrNull("monster_contacts") is not { } varieties) return result;
        var clipsByVariety = ClientTables.Table("monster_contact_clips").By("monster");
        foreach (var variety in varieties)
        {
            var byKey = clipsByVariety[variety.Index]
                .Where(c => c.TextOrNull("animation") is not null && !c.IsNull("index") && c.IntOrNull("length") is > 0)
                .GroupBy(c => c.Text("pool")!, StringComparer.Ordinal)
                .ToDictionary(pool => pool.Key, pool => pool.Select(c => new MonsterClipContact(c.Text("animation")!, c.UInt("index"),
                    c.Int("length"), c.IntOrNull("contact"), AnimationContacts.Point(c, "position"))).ToArray(), StringComparer.Ordinal);
            if (byKey.Count > 0) result[variety.Text("variety")!] = byKey;
        }
        return result;
    });

    /// <summary>The projectile of a monster's granted effect: for a mortar skill (ActiveSkills Id <c>monster_mortar*</c>)
    /// the GrantedEffects row's SkillID is the MonsterMortar row that names it (every Sand Spitter variant: SkillID 183 →
    /// MonsterMortar 183 → <c>Metadata/Projectiles/SandspitterMortarProjectile</c>, 1170). Weapon projectile attacks
    /// (<c>monster_projectile_weapon</c>) use <c>monster_projectile_variation</c> through MonsterProjectileAttack (the
    /// summoned Snake: variation 1 → Projectile 31 → <c>Metadata/Projectiles/Snake</c>). Other weapon/snipe effects
    /// without that mapping keep their instant contact. The count is the skill's:
    /// <c>number_of_projectiles_override</c> when set, else <c>base_number_of_projectiles</c> (1 without) plus
    /// <c>number_of_additional_projectiles</c> (the side-area Spitter's 3, the Elder Guardian's strafe 5).</summary>
    public MonsterProjectileInfo? MonsterProjectile(int effectRow)
    {
        if (!MonsterProjectiles.TryGetValue(effectRow, out var projectile)) return null;
        // Constants of the skill's stat set, so level 1 reads them.
        var stats = EffectStats(effectRow, 1);
        int Get(string name) => _stats.Key(name) is uint key ? stats.FirstOrDefault(s => s.Item1 == key).Item2 : 0;
        int count = Get("number_of_projectiles_override") is > 0 and var fixedCount ? fixedCount
            : Math.Max(1, Get("base_number_of_projectiles")) + Get("number_of_additional_projectiles");
        return projectile with
        {
            MinimumRange = Get("projectile_minimum_range"), SpreadRadius = Get("projectile_spread_radius"),
            Count = Math.Max(1, count),
            // MonsterMortar names the action/data table, not its trajectory. The attack's +0x558 callback
            // (141b6c630 -> 141baf9f0) selects a straight path unless a ballistic stat requests an arc.
            Lobbed = projectile.Variation == 0 || Get("projectile_ballistic_angle_from_reference_event") != 0
                || Get("projectile_ballistic_angle_from_target_distance") != 0,
        };
    }

    /// <summary>What a monster's summoning skill brings: its stat set's <c>alternate_minion</c> (a SummonedSpecificMonsters
    /// Id, whose MonsterVarietiesKey is the monster), <c>number_of_monsters_to_summon</c>, <c>summon_specific_monsters_in_front_offset</c>
    /// (world units ahead of the summoner) and <c>summoned_monsters_no_drops_or_experience</c>, with the effect's cast time
    /// and cooldown (seconds). The Labyrinth's Immolator (<c>SummonLabyrinthFireSkeleton</c>): one Ember Claimant 114
    /// units ahead, a 2.17 s cast, 3 s cooldown. Null for any other skill.</summary>
    public sealed record MonsterSummonInfo(int Variety, int Count, double Offset, bool NoRewards, double CastSeconds, double CooldownSeconds);

    public MonsterSummonInfo? MonsterSummon(int effectRow, int level)
    {
        var stats = EffectStats(effectRow, level);
        int Get(string name) => _stats.Key(name) is uint key ? stats.FirstOrDefault(s => s.Item1 == key).Item2 : 0;
        if (Get("alternate_minion") is not (> 0 and var minion) || !_summonedMonsters.Value.TryGetValue(minion, out int variety)) return null;
        return new(variety, Math.Max(1, Get("number_of_monsters_to_summon")), Get("summon_specific_monsters_in_front_offset"),
            Get("summoned_monsters_no_drops_or_experience") != 0, CastMs(effectRow) / 1000.0, CooldownMs(effectRow, level) / 1000.0);
    }

    // SummonedSpecificMonsters: Id → MonsterVarietiesKey.
    private readonly Lazy<Dictionary<int, int>> _summonedMonsters = new(() =>
    {
        var result = new Dictionary<int, int>();
        if (!ClientTables.Has("SummonedSpecificMonsters")) return result;
        var doc = ClientTables.Table("SummonedSpecificMonsters");
        foreach (var r in doc)
            if (r.IntOrNull("Id") is int id && r.IntOrNull("MonsterVarietiesKey") is int v)
                result.TryAdd(id, v);
        return result;
    });

    private Lazy<Dictionary<int, MonsterProjectileInfo>>? _monsterProjectiles;

    private Dictionary<int, MonsterProjectileInfo> MonsterProjectiles =>
        (_monsterProjectiles ??= new Lazy<Dictionary<int, MonsterProjectileInfo>>(BuildMonsterProjectiles)).Value;

    private Dictionary<int, MonsterProjectileInfo> BuildMonsterProjectiles()
    {
        var result = new Dictionary<int, MonsterProjectileInfo>();
        if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills")
            || !ClientTables.Has("Projectiles")) return result;
        var projectiles = ClientTables.Table("Projectiles");
        var rows = projectiles.Select(r => (
            Id: r.TextOrNull("Id") ?? "",
            Speed: (r.IntOrNull("ProjectileSpeed") ?? 0),
            Radius: (r.IntOrNull("CollisionRadius") ?? 1))).ToList();
        // Animation.dat Ids: a mortar row's Animation names the ACT key of the clips the client plays for the skill.
        var animationIds = new List<string>();
        if (ClientTables.Has("Animation"))
        {
            var animations = ClientTables.Table("Animation");
            foreach (var r in animations)
                animationIds.Add(r.TextOrNull("Id") ?? "");
        }
        var mortarRows = new Dictionary<int, (int Projectile, string? AnimationKey)>();
        if (ClientTables.TableOrNull("MonsterMortar") is { } mortars)
            foreach (var r in mortars)
                if (r.IntOrNull("Id") is int id && r.IntOrNull("Projectile") is int p)
                {
                    string? key = r.IntOrNull("Animation") is int a
                        && a >= 0 && a < animationIds.Count && animationIds[a].Length > 0
                        ? animationIds[a] : null;
                    mortarRows.TryAdd(id, (p, key));
                }
        var attackRows = new Dictionary<int, int>();
        if (ClientTables.TableOrNull("MonsterProjectileAttack") is { } attacks)
            foreach (var r in attacks)
                if (r.IntOrNull("Id") is int id && r.IntOrNull("Projectile") is int p)
                    attackRows.TryAdd(id, p);
        var active = ClientTables.Table("ActiveSkills");
        var mortarSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") is { } name
                && name.StartsWith("monster_mortar", StringComparison.Ordinal))
            .ToDictionary(a => a.i, a => a.r.Text("Id") == "monster_mortar_attack");
        var equipmentProjectileSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") == "monster_projectile_weapon")
            .Select(a => a.i).ToHashSet();
        var effects = ClientTables.Table("GrantedEffects");
        uint? variationKey = _stats.Key("monster_projectile_variation");
        int row = -1;
        foreach (var e in effects)
        {
            row++;
            if (e.IntOrNull("ActiveSkill") is not int activeRow) continue;
            int projectileRow;
            string? animationKey;
            bool lobbed;
            int variation;
            if (mortarSkills.TryGetValue(activeRow, out bool attackForm))
            {
                if (e.IntOrNull("SkillID") is not int skill
                    || !mortarRows.TryGetValue(skill, out var mortar)) continue;
                (projectileRow, animationKey, lobbed) = (mortar.Projectile, mortar.AnimationKey, true);
                variation = attackForm ? skill : 0;
            }
            else if (equipmentProjectileSkills.Contains(activeRow) && variationKey is uint key)
            {
                int? configuredVariation = EffectStats(row, 1).Where(s => s.Item1 == key)
                    .Select(s => (int?)s.Item2).FirstOrDefault();
                if (configuredVariation is not int variationRow || !attackRows.TryGetValue(variationRow, out projectileRow))
                    continue;
                // The weapon projectile's ACT pool is the existing SpellProjectile contact for these varieties.
                animationKey = "SpellProjectile";
                lobbed = false;
                variation = variationRow;
            }
            else continue;
            if (projectileRow < 0 || projectileRow >= rows.Count) continue;
            var p = rows[projectileRow];
            if (p.Id.Length == 0 || p.Speed <= 0) continue;
            result[row] = new(p.Id, p.Speed, lobbed, Math.Max(1, p.Radius))
                { AnimationKey = animationKey, Variation = variation };
        }
        return result;
    }

    // The highest level at or below `level` among rows with this key, else the lowest; -1 when none.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<(int Key, int Level)>, Dictionary<int, int[]>> RowsByKey = new();

    private static int PickLevel(List<(int Key, int Level)> rows, int key, int level)
    {
        int best = -1, bestLevel = int.MinValue, low = -1, lowLevel = int.MaxValue;
        // The per-level tables are loaded once and never change: index their rows by key instead of scanning them all
        // (the combat paths pick levels many times per stat calculation). Same rows, same order, same answer.
        var index = RowsByKey.GetValue(rows, r =>
        {
            var byKey = new Dictionary<int, int[]>();
            foreach (var g in r.Select((row, i) => (row.Key, i)).GroupBy(x => x.Key)) byKey[g.Key] = [.. g.Select(x => x.i)];
            return byKey;
        });
        if (!index.TryGetValue(key, out var indices)) return -1;
        foreach (int i in indices)
        {
            int l = rows[i].Level;
            if (l <= level && l > bestLevel) (best, bestLevel) = (i, l);
            if (l < lowLevel) (low, lowLevel) = (i, l);
        }
        return best >= 0 ? best : low;
    }

    /// <summary>A GrantedEffects row's cast time in ms (CastTime; 0 unknown).</summary>
    public int CastTimeMs(int effectRow) =>
        _tables.Value is { } t && effectRow >= 0 && effectRow < t.Effects.Count ? t.Effects[effectRow].CastTime : 0;

    public GrantedSkill? Find(ushort id, string effect, int level, IReadOnlyList<(uint, int)>? stats = null)
    {
        if (_tables.Value is not { } t)
            return effect == "PlayerMelee" && level == 1 ? new GrantedSkill(id, 4474, 4430, stats) : null;
        int effectRow = EffectRow(effect);
        if (effectRow < 0 || effectRow >= t.StatSets.Count) return null;
        int gepl = t.PerLevel.FindIndex(r => r.Effect == effectRow && r.Level == level);
        int gesspl = t.StatSetsPerLevel.FindIndex(r => r.StatSet == t.StatSets[effectRow] && r.Level == level);
        return gepl < 0 || gesspl < 0 ? null : WithExtraSets(new GrantedSkill(id, (uint)gepl, (uint)gesspl, stats, (uint)t.Cooldowns[gepl].Ms)
            { CooldownBypassType = (byte)t.Cooldowns[gepl].Bypass }, t, effectRow, level);
    }

    /// <summary>
    /// A GrantedEffects row at a level, for monsters: their effects have per-level rows only for some levels (Hungry
    /// Corpse's Melee only level 1), so the highest GEPL level at or below <paramref name="level"/> (else the lowest), and
    /// the effect's StatSet1 at that level the same way. Null without the tables or rows.
    /// </summary>
    public GrantedSkill? FindByRow(ushort id, int effectRow, int level, IReadOnlyList<(uint, int)>? stats = null)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count || t.StatSets[effectRow] < 0) return null;
        int statSet = t.StatSets[effectRow];
        int gepl = PickLevel(t.PerLevel, effectRow, level), gesspl = PickLevel(t.StatSetsPerLevel, statSet, level);
        return gepl < 0 || gesspl < 0 ? null : WithExtraSets(new GrantedSkill(id, (uint)gepl, (uint)gesspl, stats, (uint)t.Cooldowns[gepl].Ms)
            { CooldownBypassType = (byte)t.Cooldowns[gepl].Bypass }, t, effectRow, level);
    }

    /// <summary>The player's default skills: the default attack, with the class's unarmed weapon stats.</summary>
    public IReadOnlyList<GrantedSkill> PlayerDefaults(Unarmed weapon) => PlayerDefaults(weapon.Stats());

    /// <summary>The player's default skills with these stats.</summary>
    public IReadOnlyList<GrantedSkill> PlayerDefaults(IReadOnlyList<(uint, int)> stats)
    {
        string effect = Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee";
        return Find(FirstSkillId, effect, 1, stats) is { } s ? [s] : [];
    }

    /// <summary>A class's unarmed "weapon" (Characters.dat WeaponSpeed, MinDamage, MaxDamage, MaxAttackDistance).</summary>
    public sealed record Unarmed(int AttackMs, int MinDamage, int MaxDamage, int Range)
    {
        /// <summary>The equipped item's local typed ranges. Class unarmed records retain their physical fallback.</summary>
        public DamagePacket? LocalDamage { get; init; }
        /// <summary>Raw local modifiers captured with this weapon, including penetration and impale chance.</summary>
        public IReadOnlyDictionary<string, int>? LocalSources { get; init; }
        public string? ItemClass { get; init; }
        public static readonly Unarmed Witch = new(833, 2, 5, 8);

        /// <summary>The weapon's own added elemental/chaos damage (its local added damage mods, ItemWeapons), added to
        /// every hit it makes; empty for unarmed and for weapons without such mods.</summary>
        public DamagePacket LocalAdded { get; init; } = DamagePacket.Empty;
        public bool WieldingWand { get; init; }
        /// <summary>An ordinary bow: its attacks also take the items' "with Bows" added damage stats.</summary>
        public bool Bow { get; init; }
        public bool Melee { get; init; } = true;

        /// <summary>Unarmed attacks alternate hands, i.e. count as dual wielding, and the client adds dual wielding's 10%
        /// attack speed to the skill's main_hand_attack_speed_+% (live 2026-10-17: the skill read 10 that the server never
        /// sent, and the swings came every ~757 ms = 833 / 1.1).</summary>
        public const int DualWieldAttackSpeedPct = 10;

        /// <summary>
        /// The same attack's off-hand stats (off_hand_… instead of main_hand_…), for the weapon in the other hand of a
        /// dual-wielded attack. A dual-wielded attack swings the off hand too, and the client sets that strike's weapon
        /// up from these stats; it derives none of them (11-skills.md §4), so without them the off-hand weapon has no
        /// attack time, damage or reach at all. <paramref name="mainHandLocalAttackSpeedPct"/> is the main weapon's own
        /// local_attack_speed_+%, which the final main-hand value carries (<see cref="OffHandAttackSpeedPct"/>).
        /// </summary>
        public IReadOnlyList<(uint, int)> OffStats(IReadOnlyDictionary<uint, int>? player, DamagePacket? totalHit = null,
            int mainHandLocalAttackSpeedPct = 0)
        {
            var physical = (totalHit ?? HitDamage(player))[DamageType.Physical];
            var (min, max) = ((int)Math.Floor(physical.Min), (int)Math.Floor(physical.Max));
            return
            [
                (StatKeys.OffHandMinPhysical, min), (StatKeys.OffHandMaxPhysical, max),
                (StatKeys.OffHandMinTotal, totalHit is null ? min : (int)Math.Floor(totalHit.MinTotal)),
                (StatKeys.OffHandMaxTotal, totalHit is null ? max : (int)Math.Floor(totalHit.MaxTotal)),
                (StatKeys.OffHandAttackDurationMs, AttackMs), (StatKeys.OffHandTotalAttackDurationMs, AttackMs),
                (StatKeys.OffHandAttackSpeedPct, OffHandAttackSpeedPct(IncreasedAttackSpeed(player), mainHandLocalAttackSpeedPct)),
                (StatKeys.OffHandBaseMaxAttackDistance, Range), (StatKeys.OffHandMaxAttackDistance, Range),
            ];
        }

        /// <summary>The off hand's attack_speed_+%: the final main-hand value with the main weapon's local attack speed
        /// taken back out. CharacterStats multiplies that local value in like a more, but it belongs to the main weapon
        /// only; the off-hand weapon's own local attack speed is already in its <see cref="AttackMs"/>.</summary>
        public static int OffHandAttackSpeedPct(int mainHandPct, int mainHandLocalPct) =>
            Math.Max(-99, (int)Math.Round(((1 + mainHandPct / 100.0) / Math.Max(0.01, 1 + mainHandLocalPct / 100.0) - 1) * 100,
                MidpointRounding.AwayFromZero));

        /// <summary>The final main_hand_attack_speed_+% (passives and dual wielding, from CharacterStats), or the raw
        /// stats' sum when no final value was sent.</summary>
        public int IncreasedAttackSpeed(IReadOnlyDictionary<uint, int>? player)
        {
            // The server's stats carry the final value (passives, weapon conditions, dual wielding's 10% more).
            // The fallback is for callers that only have the raw stats.
            if (player?.TryGetValue(StatKeys.MainHandAttackSpeedPct, out int final) == true) return final;
            int increased = DualWieldAttackSpeedPct;
            foreach (uint k in StatKeys.AttackSpeedInputs)
                increased += player?.GetValueOrDefault(k) ?? 0;
            return increased;
        }

        /// <summary>The attack's total increased physical damage: the player's damage, attack, physical, melee
        /// and melee physical damage stats.</summary>
        public long IncreasedDamage(IReadOnlyDictionary<uint, int>? player)
        {
            long increased = 0;
            foreach (uint k in (uint[])[StatKeys.DamagePct, StatKeys.AttackDamagePct, StatKeys.PhysicalDamagePct])
                increased += player?.GetValueOrDefault(k) ?? 0;
            if (Melee) increased += (long)(player?.GetValueOrDefault(StatKeys.MeleeDamagePct) ?? 0)
                + (player?.GetValueOrDefault(StatKeys.MeleePhysicalDamagePct) ?? 0);
            return increased;
        }

        /// <summary>Local typed weapon damage (or class physical damage), with the actual attack's increase buckets.</summary>
        public DamagePacket HitDamage(IReadOnlyDictionary<uint, int>? player, bool melee = true, bool projectile = false)
        {
            // Arcane Might's wand-only grant follows the weapon held (WieldingWand); every local component takes the
            // transfer once, with the weapon's own melee context.
            int Get(string name) => Program.Stats.Key(name) is uint key ? player?.GetValueOrDefault(key) ?? 0 : 0;
            return (LocalDamage ?? DamagePacket.Of(DamageType.Physical, new(MinDamage, MaxDamage)).Add(LocalAdded)).Select((type, range) =>
                DamageStats.IncreaseAttackRange(range, DamageStats.AttackIncrease(Get, type, melee, projectile: projectile, wieldingWand: WieldingWand)));
        }

        /// <summary>The hit's damage range: Characters.dat MinDamage..MaxDamage × (1 + increased / 100), rounded down,
        /// at least 0.</summary>
        public (int Min, int Max) Damage(IReadOnlyDictionary<uint, int>? player)
        {
            var damage = HitDamage(player)[DamageType.Physical];
            return ((int)Math.Floor(damage.Min), (int)Math.Floor(damage.Max));
        }

        /// <summary>The attack time: base / (1 + increased / 100), as the client computes the swing from the skill's
        /// main_hand_attack_speed_+% (the final value already includes more multipliers), at least one server tick
        /// (<see cref="Program.TickSeconds"/>: at most 30 attacks per second).</summary>
        public double AttackSeconds(IReadOnlyDictionary<uint, int>? player) =>
            Math.Max(Program.TickSeconds, AttackMs / 1000.0 / Math.Max(0.01, 1 + IncreasedAttackSpeed(player) / 100.0));

        /// <summary>
        /// The skill's final main-hand stats (client keys = Stats.dat row + 1). The server computes them, as the real one
        /// does: the client derives nothing from attack_speed_+% (live 2026-10-17: a skill holding attack_speed_+% 2000
        /// still swung at ~757 ms and the character panel kept showing 1.2). The swing follows main_hand_attack_speed_+%
        /// (the client puts only dual wielding's 10 there when it is not sent), the panel's "Attacks per Second" is
        /// hundred_times_attacks_per_second / 100.
        /// </summary>
        public IReadOnlyList<(uint, int)> Stats(IReadOnlyDictionary<uint, int>? player = null, DamagePacket? totalHit = null,
            double damageFactor = 1)
        {
            var physical = (totalHit ?? HitDamage(player))[DamageType.Physical];
            var (min, max) = ((int)Math.Floor(physical.Min), (int)Math.Floor(physical.Max));
            double attacksPerSecond = 1 / AttackSeconds(player);
            var display = SpellDamage.Display((totalHit?.MinTotal ?? min, totalHit?.MaxTotal ?? max), attacksPerSecond, damageFactor);
            return
            [
                (StatKeys.MainHandMinPhysical, min), (StatKeys.MainHandMaxPhysical, max),
                (StatKeys.MainHandMinTotal, totalHit is null ? min : (int)Math.Floor(totalHit.MinTotal)),
                (StatKeys.MainHandMaxTotal, totalHit is null ? max : (int)Math.Floor(totalHit.MaxTotal)),
                (StatKeys.MainHandAttackDurationMs, AttackMs), (StatKeys.MainHandTotalAttackDurationMs, AttackMs),
                (StatKeys.MainHandAttackSpeedPct, IncreasedAttackSpeed(player)),
                (StatKeys.SkillUsesMainHand, 1),
                (StatKeys.HundredTimesAttacksPerSecond, (int)Math.Round(100 * attacksPerSecond)),
                // The character panel's DPS and Damage per Use rows read these; the client derives neither.
                (StatKeys.HundredTimesDamagePerSecond, display.PerSecond),
                (StatKeys.DisplayHundredTimesDamagePerSkillUse, display.PerUse),
                (StatKeys.MainHandBaseMaxAttackDistance, Range), (StatKeys.MainHandMaxAttackDistance, Range),
            ];
        }
    }

    /// <summary>A class from Characters.dat: IntegerId (what the Player component's class byte holds: Marauder 1, Witch 3),
    /// the base attributes, and the player object's type (the row's Id, e.g. Metadata/Characters/StrInt/StrInt for the
    /// Templar) and model (AOFile).</summary>
    public sealed record ClassInfo(int IntegerId, int Strength, int Dexterity, int Intelligence,
        string ObjectType = "Metadata/Characters/Int/Int", string AnimatedObject = "Metadata/Characters/Int/Int.ao",
        int BaseMaxLife = 38, int BaseMaxMana = 34)
    {
        public static readonly ClassInfo Witch = new(3, 14, 14, 32);
    }

    /// <summary>The Witch's Characters.dat row, used when a class is unknown.</summary>
    public const int WitchRow = 1;

    /// <summary>The Characters.dat row of a class by IntegerId (CharacterClass): Marauder 1 → row 0, Witch 3 → row 1,
    /// Templar 5 → row 6.</summary>
    public int RowOfClass(int integerId)
    {
        if (!ClientTables.Has("Characters")) return WitchRow;
        var d = ClientTables.Table("Characters");
        int i = 0;
        foreach (var r in d)
        {
            if (r.Int("IntegerId") == integerId) return i;
            i++;
        }
        return WitchRow;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ClassInfo> _classes = new();

    public ClassInfo ClassFor(int characterRow) => _classes.GetOrAdd(characterRow, row =>
    {
        if (!ClientTables.Has("Characters")) return ClassInfo.Witch;
        var d = ClientTables.Table("Characters");
        if (row < 0 || row >= d.Count) return ClassInfo.Witch;
        var r = d[row];
        return new(r.Int("IntegerId"), r.Int("BaseStrength"),
            r.Int("BaseDexterity"), r.Int("BaseIntelligence"),
            r.Text("Id")!, r.Text("AOFile")!,
            r.Int("BaseMaxLife"), r.Int("BaseMaxMana"));
    });

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Unarmed> _unarmed = new();

    public Unarmed UnarmedFor(int characterRow) => _unarmed.GetOrAdd(characterRow, LoadUnarmed);

    private Unarmed LoadUnarmed(int characterRow)
    {
        if (!ClientTables.Has("Characters")) return Unarmed.Witch;
        var d = ClientTables.Table("Characters");
        if (characterRow < 0 || characterRow >= d.Count) return Unarmed.Witch;
        var r = d[characterRow];
        return new Unarmed(r.Int("WeaponSpeed"), r.Int("MinDamage"),
            r.Int("MaxDamage"), r.Int("MaxAttackDistance"));
    }

    // StatSets: each GrantedEffects row's StatSet1 (-1 when none). Effects: per GrantedEffects row its StatSet1 and CastTime.
    // StatSetBase: per GrantedEffectStatSets row its ImplicitStats and (ConstantStats, ConstantStatsValues).
    // StatSetLevelStats: per GESSPL row its (FloatStats, BaseResolvedValues) and (AdditionalStats, AdditionalStatsValues).
    // Effectiveness: resolved permyriad, 10000 + the GESSPL DamageEffectiveness offset; 0 for missing data.
    // ManaCost: per GEPL row the CostAmounts entry of CostTypes 0 (mana), else 0; ManaPerMinute the same of CostTypes 7.
    // Reservations: per GEPL row the Mana/LifeReservationFlat and -Percent (permyriad), parallel to PerLevel.
    // Cooldowns: per GEPL row its Cooldown (ms), StoredUses, CooldownBypassType and Group, parallel to PerLevel. VaalSouls: per GEPL row its VaalSouls.
    // Quality: per GrantedEffects row its (quality stat, permille).
    private sealed record Tables(Dictionary<string, (int Row, int StatSet)> EffectRows, List<int> StatSets,
        List<(int Effect, int Level)> PerLevel, List<(int StatSet, int Level)> StatSetsPerLevel,
        List<(int StatSet, int CastTime, bool CooldownPauses)> Effects, List<(int[] Implicit, (int, int)[] Constant)> StatSetBase,
        List<(int, int)[]> StatSetLevelStats, List<(int StatSet, int GemLevel, int PlayerLevelReq)> GemLevelReqs, List<int> ManaCost, List<int> LifeCost,
        List<(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)> Reservations, List<(int Ms, int Uses, int Bypass, int Group)> Cooldowns,
        List<int> VaalSouls, List<int> VaalStoredUses, List<int?> ActiveSkills, List<int> SoulPrevention, List<int> RageCost,
        Dictionary<int, (int, int)[]> Quality, List<int> Effectiveness, List<int?> SpellCritChances, List<int> AttackSpeedMultipliers,
        List<int> ManaPerMinute, List<int> ManaPercentPerMinute)
    {
        public FrozenDictionary<int, string> EffectIds { get; } = EffectRows
            .GroupBy(p => p.Value.Row).ToFrozenDictionary(g => g.Key, g => g.First().Key);

        // GemLevelReqs by stat set: every gem's requirement check (twice per linked gem per skill build) scanned the whole
        // table before.
        Dictionary<int, (int GemLevel, int PlayerLevelReq)[]>? _levelReqsBySet;
        public (int GemLevel, int PlayerLevelReq)[] LevelReqsOf(int statSet) =>
            LazyInitializer.EnsureInitialized(ref _levelReqsBySet, () => GemLevelReqs.GroupBy(r => r.StatSet)
                .ToDictionary(g => g.Key, g => g.Select(r => (r.GemLevel, r.PlayerLevelReq)).ToArray()))
                .GetValueOrDefault(statSet, []);

        public static Tables? Load(string folder)
        {
            if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("GrantedEffectsPerLevel")
                || !ClientTables.Has("GrantedEffectStatSetsPerLevel")) return null;
            var g = ClientTables.Table("GrantedEffects");
            var p = ClientTables.Table("GrantedEffectsPerLevel");
            var s = ClientTables.Table("GrantedEffectStatSetsPerLevel");
            static int Int(ClientRow r, string name) => r.IntOrNull(name) ?? -1;
            static int[] Ints(ClientRow r, string name) => [.. r.Reals(name).Select(x => (int)Math.Round(x))];
            static (int, int)[] Pairs(int[] keys, int[] values) => [.. keys.Zip(values).Where(kv => kv.First >= 0)];
            var effects = new Dictionary<string, (int, int)>();
            var statSets = new List<int>();
            var effectInfo = new List<(int, int, bool)>();
            int activeCount = 0;
            if (ClientTables.Has("ActiveSkills"))
            {
                activeCount = ClientTables.Table("ActiveSkills").Count;
            }
            var activeSkills = new List<int?>();
            int row = 0;
            foreach (var r in g)
            {
                string? id = r.Text("Id");
                int statSet = Int(r, "StatSet1");
                int activeRow = Int(r, "ActiveSkill");
                activeSkills.Add(activeRow >= 0 && activeRow < activeCount ? activeRow : null);
                if (id is not null) effects.TryAdd(id, (row, statSet));
                statSets.Add(statSet);
                effectInfo.Add((statSet, Math.Max(0, Int(r, "CastTime")),
                    r.BoolOrNull("CooldownNotRecoverDuringActive") == true));
                row++;
            }
            var baseStats = new List<(int[], (int, int)[])>();
            if (ClientTables.Has("GrantedEffectStatSets"))
            {
                foreach (var r in ClientTables.Table("GrantedEffectStatSets"))
                    baseStats.Add((Ints(r, "ImplicitStats"), Pairs(Ints(r, "ConstantStats"), Ints(r, "ConstantStatsValues"))));
            }
            var perLevel = new List<(int, int)>();
            var mana = new List<int>();
            var life = new List<int>();
            var manaPerMinute = new List<int>();
            var manaPercentPerMinute = new List<int>();
            var reservations = new List<(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)>();
            var cooldowns = new List<(int, int, int, int)>();
            var vaalSouls = new List<int>();
            var vaalStoredUses = new List<int>();
            var soulPrevention = new List<int>();
            var rageCost = new List<int>();
            var attackSpeedMultipliers = new List<int>();
            foreach (var r in p)
            {
                perLevel.Add((Int(r, "GrantedEffect"), Int(r, "Level")));
                int[] types = Ints(r, "CostTypes"), amounts = Ints(r, "CostAmounts");
                int i = Array.IndexOf(types, 0);
                mana.Add(i >= 0 && i < amounts.Length ? amounts[i] : 0);
                int lifeIndex = Array.IndexOf(types, 1);
                life.Add(lifeIndex >= 0 && lifeIndex < amounts.Length ? amounts[lifeIndex] : 0);
                int m = Array.IndexOf(types, 7);
                manaPerMinute.Add(m >= 0 && m < amounts.Length ? amounts[m] : 0);
                int mp = Array.IndexOf(types, 9);
                manaPercentPerMinute.Add(mp >= 0 && mp < amounts.Length ? amounts[mp] : 0);
                reservations.Add((Math.Max(0, Int(r, "ManaReservationFlat")), Math.Max(0, Int(r, "ManaReservationPercent")),
                    Math.Max(0, Int(r, "LifeReservationFlat")), Math.Max(0, Int(r, "LifeReservationPercent"))));
                cooldowns.Add((Math.Max(0, Int(r, "Cooldown")), Math.Max(0, Int(r, "StoredUses")), Math.Max(0, Int(r, "CooldownBypassType")), r.Has("CooldownGroup") ? Int(r, "CooldownGroup") : 36));
                vaalSouls.Add(Int(r, "VaalSouls"));
                vaalStoredUses.Add(Int(r, "VaalStoredUses"));
                soulPrevention.Add(Math.Max(0, Int(r, "SoulGainPreventionDuration")));
                int rageIndex = Array.IndexOf(types, 3);
                rageCost.Add(rageIndex >= 0 && rageIndex < amounts.Length ? Math.Max(0, amounts[rageIndex]) : 0);
                attackSpeedMultipliers.Add(r.IntOrNull("AttackSpeedMultiplier") ?? 0);
            }
            var quality = new Dictionary<int, (int, int)[]>();
            if (ClientTables.Has("GrantedEffectQualityStats"))
            {
                foreach (var r in ClientTables.Table("GrantedEffectQualityStats"))
                    quality.TryAdd(Int(r, "GrantedEffectsKey"), Pairs(Ints(r, "StatsKeys"), Ints(r, "StatsValuesPermille")));
            }
            var setLevels = new List<(int, int)>();
            var effectiveness = new List<int>();
            var spellCritChances = new List<int?>();
            var levelStats = new List<(int, int)[]>();
            var levelReqs = new List<(int, int, int)>();
            foreach (var r in s)
            {
                setLevels.Add((Int(r, "StatSet"), Int(r, "GemLevel")));
                // PoB Export/Scripts/skills.lua: raw / 10000 + 1. Zero is 100%, negatives are real reductions.
                effectiveness.Add(r.IntOrNull("DamageEffectiveness") is int offset
                    ? (int)Math.Clamp(10_000L + offset, 0, int.MaxValue) : 0);
                int crit = Int(r, "SpellCritChance");
                spellCritChances.Add(crit >= 0 ? crit : null);
                levelStats.Add([.. Pairs(Ints(r, "FloatStats"), Ints(r, "BaseResolvedValues")),
                    .. Pairs(Ints(r, "AdditionalStats"), Ints(r, "AdditionalStatsValues"))]);
                if (Int(r, "PlayerLevelReq") is >= 0 and var req) levelReqs.Add((Int(r, "StatSet"), Int(r, "GemLevel"), req));
            }
            return new Tables(effects, statSets, perLevel, setLevels, effectInfo, baseStats, levelStats, levelReqs, mana, life, reservations, cooldowns,
                vaalSouls, vaalStoredUses, activeSkills, soulPrevention, rageCost, quality, effectiveness, spellCritChances, attackSpeedMultipliers, manaPerMinute,
                manaPercentPerMinute);
        }
    }
}
