using System.Collections.Frozen;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Core;

/// <summary>
/// Skills the server grants: a GrantedEffects Id at a level becomes the GrantedEffectsPerLevel row and the
/// GrantedEffectStatSetsPerLevel row (of the effect's StatSet1 at that gem level) the Actor skill list refers to
/// (Agents/docs/protocol/11-skills.md). Reads the client tables (client.db, in memory). Without the tables it
/// knows only the default attack, PlayerMelee at level 1 (GEPL 4474, GESSPL 4430 in 3.29.3b).
/// </summary>
public sealed class SkillCatalog
{
    readonly string folder;
    readonly bool hardMode;

    public SkillCatalog(string folder = "data", bool hardMode = false)
    {
        this.folder = folder;
        this.hardMode = hardMode;
        _tables = Tables.Load(folder);
        _skillTypes = new(folder);
        _modeEffects = LoadModeEffects();
        _extraSets = LoadExtraSets();
        _actionIds = LoadActionIds();
        _attackTimes = LoadAttackTimes();
        _artVariations = LoadArtVariations();
        _skillIds = LoadSkillIds();
        _effectActions = LoadEffectActions();
        _actions = LoadActions();
        _families = new(folder);
        _weaponRestrictions = LoadWeaponRestrictions();
        _weaponTypes = LoadWeaponTypes();
        _skillArt = LoadSkillArt();
        _activeSkillOfEffect = LoadActiveSkillOfEffect();
        _projectileContacts = LoadProjectileContacts();
        _projectiles = LoadProjectiles();
        _projectileAos = LoadProjectileAos();
        _projectileClips = LoadProjectileClips();
        _monsterContacts = LoadMonsterContacts();
        _monsterMeleeClocks = LoadMonsterMeleeClocks();
        _summonedMonsters = LoadSummonedMonsters();
        _monsterVarietyRowsById = LoadMonsterVarietyRowsById();
        _noBorrowSkills = LoadNoBorrowSkills();
        _monsterProjectiles = BuildMonsterProjectiles(false);
        _weaponMonsterProjectiles = BuildMonsterProjectiles(true);
    }

    /// <summary>The ids the server gives its skills (+0x40). Basic actions use their ActionTypes HASH16 instead, so these
    /// must not collide with the type's basic actions (Character.ot: Move 0x2909, Interaction 0x0266 …). 0x0101 did: a
    /// Monster.ot basic action masks to it. 0x0102 is no ActionType's id in 3.29.3b or 3.29.3c.</summary>
    public const ushort FirstSkillId = 0x0102;

    private readonly Tables? _tables;
    private readonly StatCatalog _stats = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _castsOnDeath = new();

    public bool CastsOnDeath(int effect) => _castsOnDeath.GetOrAdd(effect, row =>
        EffectStats(row, 1).Any(stat => (stat.Item1 == Stat.CastOnDeathPct || stat.Item1 == Stat.AttackOnDeathPct) && stat.Item2 > 0));


    // Both sets of effects already have their own native rows and per-level stat sets. Only name-based lookups
    // require resolution; explicit row-based calls retain their exact identity (including monster effects).
    private readonly Dictionary<int, int> _modeEffects;

    Dictionary<int, int> LoadModeEffects()
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
    }

    /// <summary>Each GrantedEffects row's additional stat sets (StatSet2), when it has any.</summary>
    private readonly Dictionary<int, int[]> _extraSets;

    Dictionary<int, int[]> LoadExtraSets()
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
    }

    /// <summary>A skill with its effect's additional stat sets: each set's per-level row at this level (the closest one
    /// otherwise) and its stats (implicit 1s, constants, then the level's stats). Null when a set has no row at all: a
    /// record without it would make the client misread it.</summary>
    GrantedSkill? WithExtraSets(GrantedSkill skill, Tables t, int effectRow, int level)
    {
        // Both Find paths use catalog identity and raw stored uses, never effective cost or current souls.
        skill = skill with { VaalResourcesEnabled = VaalBudget(effectRow, level) is not null };
        if (!_extraSets.TryGetValue(effectRow, out var sets)) return skill;
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
        var taken = _actionIds;
        for (int id = FirstSkillId; id <= 0x3fff; id++)
            if (!taken.Contains((ushort)id)) yield return (ushort)id;
    }

    /// <summary>Whether an id is one of the gem skills' (<see cref="SkillIds"/> after the default attack, the first 64), not a
    /// basic action such as Move.</summary>
    public bool IsGemSkill(ushort id) => id > FirstSkillId && id < FirstSkillId + 64 && !_actionIds.Contains(id);

    private readonly HashSet<ushort> _actionIds;

    HashSet<ushort> LoadActionIds()
    {
        if (!ClientTables.Has("ActionTypes")) return [0x0101, 0x0106, 0x010a, 0x0114];
        var d = ClientTables.Table("ActionTypes");
        return [.. d.Select(r => (ushort)(r.Int("HASH16") & 0x3fff))];
    }

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
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.Effects.Count) return [];
        var e = t.Effects[effectRow];
        var stats = new Dictionary<uint, int>();
        var resolved = new HashSet<uint>();
        if (e.StatSet >= 0 && e.StatSet < t.StatSetBase.Count)
        {
            var (implicits, constants) = t.StatSetBase[e.StatSet];
            foreach (int s in implicits) stats[(uint)s + 1] = 1;
            foreach (var (s, v) in constants) { stats[(uint)s + 1] = v; resolved.Add((uint)s + 1); }
        }
        int gesspl = PickLevel(t.StatSetsPerLevel, e.StatSet, level);
        // Per-level entries add to constants and to earlier per-level entries. Vortex of Projection has a resolved
        // cold damage rate and a negative additional entry for that same stat. Implicit flags are still replaced.
        if (gesspl >= 0)
            foreach (var (s, v) in t.StatSetLevelStats[gesspl])
            {
                uint key = (uint)s + 1;
                stats[key] = (resolved.Add(key) ? 0 : stats[key]) + v;
            }
        int gepl = PickLevel(t.PerLevel, effectRow, level);
        if (e.CastTime > 0)
        {
            Put(Stat.BaseSpellCastTimeMs, e.CastTime);
            // what the client reads when it starts the cast (traced live 2026-10-18; 0 = an instant cast)
            Put(Stat.VirtualBaseSpellCastTimeMs, e.CastTime);
            Put(Stat.HundredTimesCastsPerSecond, (int)Math.Round(100_000.0 / e.CastTime));
        }
        if (Value(stats, Stat.VolleyAdditionalProjectilesFireParallelXDist, Stat.GreaterVolleyAdditionalProjectilesFireParallelXDist) is int parallelDist and > 0)
            Put(Stat.VirtualAdditionalProjectilesFireParallelXDist, parallelDist);
        if (gepl >= 0 && t.RageCost[gepl] > 0) { Put(Stat.BaseRageCost, t.RageCost[gepl]); Put(Stat.TotalBaseRageCost, t.RageCost[gepl]); }
        if (gepl >= 0 && t.ManaCost[gepl] > 0) { Put(Stat.BaseManaCost, t.ManaCost[gepl]); Put(Stat.ManaCost, t.ManaCost[gepl]); }
        // Manabond uses CostTypes 4, a percent of maximum Mana, with the same support and skill cost modifiers.
        if (gepl >= 0 && t.ManaPercentCost[gepl] > 0) Put(Stat.BaseManaCostPct, t.ManaPercentCost[gepl]);
        if (gepl >= 0 && t.LifeCost[gepl] > 0) { Put(Stat.BaseLifeCost, t.LifeCost[gepl]); Put(Stat.LifeCost, t.LifeCost[gepl]); }
        // An Energy Shield cost (CostTypes 2: Energy Blade, Wild Strike of Extremes).
        if (gepl >= 0 && t.EsCost[gepl] > 0) { Put(Stat.BaseEsCost, t.EsCost[gepl]); Put(Stat.EsCost, t.EsCost[gepl]); }
        // A cost over time (the link gems' "Mana per second"): CostTypes 7 ManaPerMinute.
        if (gepl >= 0 && t.ManaPerMinute[gepl] > 0) { Put(Stat.BaseManaCostPerMinute, t.ManaPerMinute[gepl]); Put(Stat.ManaCostPerMinute, t.ManaPerMinute[gepl]); }
        // A share of maximum mana over time (Bladefall of Trarthus: 300 per minute, "5% Mana per second"): CostTypes 9
        // ManaPercentPerMinute. Its final mana_cost_per_minute takes the caster's maximum mana (Skills.cs publishes it).
        if (gepl >= 0 && t.ManaPercentPerMinute[gepl] > 0) Put(Stat.BaseManaCostPctPerMinute, t.ManaPercentPerMinute[gepl]);
        // A skill with a cooldown (GEPL Cooldown, e.g. Haste's 1200 ms): the client keeps the cooldown only when the skill
        // has uses (virtual_total_cooldown_count, GEPL StoredUses) and a duration in its record (GrantedSkill.CooldownMs).
        // An instant skill (ActiveSkillType Instant: Haste, Righteous Fire) is one by its stat: the client reads
        // skill_is_instant (0x2064), not the skill type. Without it the skill's action waited for a cast that never came,
        // stayed the player's current action and held back every other skill until a move (Frida, live 2026-10-21).
        if (IsInstant(effectRow))
        {
            Put(Stat.BaseSkillIsInstant, 1);
            Put(Stat.SkillIsInstant, 1);
        }
        if (IsAura(effectRow)) stats[OwnAuraRestriction.BaseAura] = 1;
        if (_curseEffects.Contains(effectRow)) stats[OwnAuraRestriction.Curse] = 1;
        if (OwnAuraRestriction.IsAura(stats)) stats[OwnAuraRestriction.Aura] = 1;
        // CharacterPanelStats gates spell rows on this final flag and hides the weapon rows while it is set.
        // A non-attack is not necessarily a spell (warcries, for example); use the actual ActiveSkillType.
        Put(Stat.CastingSpell, IsSpell(effectRow) && !IsAttack(effectRow) ? 1 : 0);
        // The Nova type's flag. The client casts a Nova at the cursor only when this, casting_spell and the caster's
        // nova_spells_cast_at_target_location are all set (FUN_14289d210); otherwise it stays around the caster.
        if (IsNova(effectRow)) Put(Stat.SkillIsNovaSkill, 1);
        // ActiveSkillType Brand's FlagStat is skill_is_brand_skill. It belongs to this selected skill, not the actor.
        Put(Stat.SkillIsBrandSkill, IsBrand(effectRow) ? 1 : 0);
        if (gepl >= 0 && t.Cooldowns[gepl] is { Ms: > 0, Uses: > 0 } cooldown)
        {
            Put(Stat.BaseCooldownCount, cooldown.Uses);
            Put(Stat.VirtualTotalCooldownCount, cooldown.Uses);
        }
        if (quality > 0 && t.Quality.TryGetValue(effectRow, out var perQuality))
            foreach (var (s, permille) in perQuality) stats[(uint)s + 1] = stats.GetValueOrDefault((uint)s + 1) + permille * quality / 1000;
        return [.. stats.Select(kv => (kv.Key, kv.Value))];

        void Put(uint name, int value) { if (name is var k) stats[k] = value; }
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
            int min = prefix == "spell" ? Value(stats, Stat.Of($"spell_minimum_base_{slug}_damage{suffix}"), Stat.Of($"minimum_base_{slug}_damage{suffix}"))
                : Value(stats, Stat.Of($"{prefix}_minimum_base_{slug}_damage{suffix}"));
            int max = prefix == "spell" ? Value(stats, Stat.Of($"spell_maximum_base_{slug}_damage{suffix}"), Stat.Of($"maximum_base_{slug}_damage{suffix}"))
                : Value(stats, Stat.Of($"{prefix}_maximum_base_{slug}_damage{suffix}"));
            if (min > 0 || max > 0) result = result.Add(type, new DamageRange(min, Math.Max(min, max)));
        }
        return result;
    }

    private static int Value(Dictionary<uint, int> stats, params uint[] keys)
    {
        foreach (uint key in keys)
            if (stats.TryGetValue(key, out int value)) return value;
        return 0;
    }

    /// <summary>How far a gem's stored experience may level it (GrantedEffectStatSetsPerLevel PlayerLevelReq): its level
    /// stalls at the first one the character's level does not meet, as the game's does. The client derives the level from
    /// the experience alone (it does not stall), so the server stops the experience below that level's threshold
    /// (Program.GainGemExperience). 1 when not even the first level's requirement is met (the character cannot use the
    /// gem), 40 when the skill has none.</summary>
    public int MaxLevelAt(int effectRow, int charLevel)
    {
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 40;
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
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 1;
        var levels = t.LevelReqsOf(t.StatSets[effectRow]);
        int met = levels.Where(l => l.PlayerLevelReq <= monsterLevel).Select(l => l.GemLevel).DefaultIfEmpty(0).Max();
        return met > 0 ? met : levels.Select(l => l.GemLevel).DefaultIfEmpty(1).Min();
    }

    /// <summary>The character level a gem needs at this level (GrantedEffectStatSetsPerLevel PlayerLevelReq): the
    /// highest requirement of its levels at or below it, 0 when the skill has none.</summary>
    public int PlayerLevelReq(int effectRow, int level)
    {
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 0;
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
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return 0;
        int row = PickLevel(t.StatSetsPerLevel, t.StatSets[effectRow], level);
        return row >= 0 && row < t.Effectiveness.Count ? Math.Max(0, t.Effectiveness[row]) : 0;
    }

    /// <summary>The two independent GESSPL coefficients in permyriad. Missing metadata is identity;
    /// an explicit -10000 offset resolves to zero, not the missing-data fallback.</summary>
    public (int Base, int Added) DamageCoefficients(int effectRow, int level)
    {
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count) return (10000, 10000);
        int row = PickLevel(t.StatSetsPerLevel, t.StatSets[effectRow], level);
        return row >= 0 && row < t.DamageCoefficients.Count ? t.DamageCoefficients[row] : (10000, 10000);
    }

    /// <summary>The inherent weapon attack-rate multiplier at the selected GEPL level, in percent.
    /// It multiplies weapon rate separately from player increased speed; absent data keeps ordinary weapon timing.</summary>
    public int AttackSpeedMultiplier(int effectRow, int level) =>
        effectRow >= 0 && level > 0 && _tables is { } t
            && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.AttackSpeedMultipliers[row] : 0;

    /// <summary>The gem's own base attack time at a GEPL level (GrantedEffectsPerLevel AttackTime, ms; Shield Charge
    /// 500), or 0 where the skill uses its weapon's.</summary>
    public int AttackTimeMs(int effectRow, int level) =>
        effectRow >= 0 && level > 0 && _tables is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0
            && row < _attackTimes.Count ? _attackTimes[row] : 0;

    // GrantedEffectsPerLevel AttackTime by row, in the order Tables.PerLevel reads them.
    private readonly List<int> _attackTimes;

    List<int> LoadAttackTimes()
    {
        var times = new List<int>();
        if (!ClientTables.Has("GrantedEffectsPerLevel")) return times;
        var doc = ClientTables.Table("GrantedEffectsPerLevel");
        foreach (var r in doc)
            times.Add(r.IntOrNull("AttackTime") is int ms ? Math.Max(0, ms) : 0);
        return times;
    }

    /// <summary>The effect's primary stat set's base spell critical chance at this gem level, in permyriad.
    /// Null means missing/invalid data, distinct from a known zero (a spell with no inherent critical chance).</summary>
    public int? SpellCritChance(int effectRow, int level) => BaseCriticalChances(effectRow, level).Spell;

    /// <summary>The effect's own attack critical chance in permyriad, used by attacks that swing a shield.</summary>
    public int? AttackCritChance(int effectRow, int level) => BaseCriticalChances(effectRow, level).Attack;

    (int? Spell, int? Attack) BaseCriticalChances(int effectRow, int level)
    {
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count || level < 1) return default;
        int set = t.StatSets[effectRow];
        if (set < 0) return default;
        int row = PickLevel(t.StatSetsPerLevel, set, level);
        return row >= 0 ? t.CriticalChances[row] : default;
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
    public string? ActionName(ushort skillId)
    {
        foreach (var action in _actions)
            if (action.Id == skillId) return action.Name;
        return null;
    }

    /// <summary>The full ActionTypes HASH16 for a GrantedEffects row, used with its server skill id by skill buffs.</summary>
    public ushort? ActionHash(int effectRow) => _effectActions.TryGetValue(effectRow, out ushort hash) ? hash : null;

    /// <summary>The ActionTypes Id of a GrantedEffects row's action class (ActiveSkills.ActionType), or null.</summary>
    public static string? ActionTypeId(int effectRow)
    {
        if (ClientTables.TableOrNull("GrantedEffects")?.Row(effectRow)?.IntOrNull("ActiveSkill") is not int skill
            || ClientTables.TableOrNull("ActiveSkills")?.Row(skill)?.IntOrNull("ActionType") is not int action) return null;
        return ClientTables.TableOrNull("ActionTypes")?.Row(action)?.Text("Id");
    }

    /// <summary>
    /// A GrantedEffects row's SkillID, 0 for none: the value the client's own skill-stat builder adds as
    /// <c>skill_behaviour_variation</c> (0x1921; 0x141eefbe3 loads the row's +0xbe). An action takes its data row from it:
    /// a mortar its MonsterMortar row, a geometry channel its GeometryAttack row (by Id) and that row's GeometryChannel.
    /// A record sent over the network does not run that builder, so a record without it reads Id 0, whose GeometryAttack
    /// row has no GeometryChannel: the King in the Mists' GSVoodooKingRitual (SkillID 1116) then crashed the client in
    /// GeometryChannelBase::StartChannelling (0xc0000005 at 0x1419e37bf, the null row's +0x28).
    /// </summary>
    public int BehaviourVariation(int effectRow) => _skillIds.GetValueOrDefault(effectRow);

    /// <summary>The effect's art (GrantedEffects <c>__col23</c>), the number the client takes as <c>skill_art_variation</c>:
    /// SkillArtVariations' Variants of its skill (Izaro's Leap Slam 5 LabyrinthBoss, his Upheaval 5 Ornaments). Sent only on
    /// the records that ask for it: without it the Upheaval drew Glacial Cascade's ice (live, Izaro).</summary>
    public int ArtVariation(int effectRow) => _artVariations.GetValueOrDefault(effectRow);

    private readonly Dictionary<int, int> _artVariations;

    Dictionary<int, int> LoadArtVariations()
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
    }

    private readonly Dictionary<int, int> _skillIds;

    Dictionary<int, int> LoadSkillIds()
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
    }

    private readonly Dictionary<int, ushort> _effectActions;

    Dictionary<int, ushort> LoadEffectActions()
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
    }

    /// <summary>A basic action's skill id by ActionTypes Id ("Move" → 0x2909), or null.</summary>
    public ushort? ActionSkillId(string name) => _actions.FirstOrDefault(a => a.Name == name) is { Name: not null } a ? a.Id : null;

    private readonly List<(ushort Id, string Name)> _actions;

    List<(ushort Id, string Name)> LoadActions()
    {
        if (!ClientTables.Has("ActionTypes")) return [(0x2909, "Move")];
        var d = ClientTables.Table("ActionTypes");
        return [.. d.Select(r => ((ushort)(r.Int("HASH16") & 0x3fff), r.Text("Id") ?? ""))];
    }

    /// <summary>A GrantedEffects row's Id, or null.</summary>
    public string? EffectId(int effectRow) =>
        _tables?.EffectIds.GetValueOrDefault(effectRow);

    /// <summary>Whether a GrantedEffects row is a support, from its actual row (not its translated name).</summary>
    public bool IsSupportEffect(int effectRow) => _supportEffects.Contains(effectRow);

    private readonly HashSet<int> _supportEffects = LoadSupportEffects();

    static HashSet<int> LoadSupportEffects()
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
    }

    /// <summary>
    /// Whether the effect's ActiveSkills row is manually cast. A trigger-only row has no bar use; callers must
    /// not turn one into a manually cast skill merely because it has an active-skill record.
    /// </summary>
    public bool IsManuallyCasted(int effectRow) => _manuallyCastEffects.Contains(effectRow);

    private readonly HashSet<int> _manuallyCastEffects = LoadManuallyCastEffects();

    static HashSet<int> LoadManuallyCastEffects()
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
    }

    /// <summary>A GrantedEffectsPerLevel row's GrantedEffects row and level, or null.</summary>
    public (int Effect, int Level)? EffectsPerLevel(int row) =>
        _tables is { } t && row >= 0 && row < t.PerLevel.Count ? t.PerLevel[row] : null;

    /// <summary>A GrantedEffects row by its Id, or -1.</summary>
    public int EffectRow(string effectId) =>
        _tables is { } t && t.EffectRows.TryGetValue(effectId, out var effect)
            ? _modeEffects.GetValueOrDefault(effect.Row, effect.Row) : -1;

    /// <summary>
    /// The resources a skill reserves at a level: GrantedEffectsPerLevel's mana and life flat/percent fields
    /// (permyriad: 5000 = 50%). Null when the row reserves neither resource.
    /// </summary>
    public ReservationCost? Reservation(int effectRow, int level)
    {
        if (_tables is not { } t) return null;
        int row = PickLevel(t.PerLevel, effectRow, level);
        if (row < 0 || row >= t.Reservations.Count) return null;
        var r = t.Reservations[row];
        return r.ManaFlat == 0 && r.ManaPercent == 0 && r.LifeFlat == 0 && r.LifePercent == 0
            ? null : new(r.ManaFlat, r.ManaPercent, r.LifeFlat, r.LifePercent);
    }

    public readonly record struct ReservationCost(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)
    {
        public bool FixedMana { get; init; }
        // The legacy one-pool contract exposes Mana when present, otherwise Life. Raw-pool callers use the
        // Mana*/Life* fields so they never have to infer the resource from a zero value.
        public bool IsLife => ManaFlat == 0 && ManaPercent == 0;
        public int Flat => IsLife ? LifeFlat : ManaFlat;
        public int Percent => IsLife ? LifePercent : ManaPercent;
    }

    public bool LifeReservation(int effectRow, int level) => _tables is { } t
        && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0
        && (t.Reservations[row].LifeFlat != 0 || t.Reservations[row].LifePercent != 0);

    /// <summary>The Vaal souls a use of a skill needs at a level (GrantedEffectsPerLevel VaalSouls; 0 for skills that are
    /// not Vaal skills).</summary>
    public int VaalSouls(int effectRow, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? Math.Max(0, t.VaalSouls[row]) : 0;

    public readonly record struct VaalDefinition(uint ActiveSkillsRow, int BaseCost, int StoredUses);

    /// <summary>Null for non-Vaal skills or missing/invalid catalog references. Never substitutes GE/GEPL identity.</summary>
    public VaalDefinition? VaalBudget(int effectRow, int level)
    {
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.ActiveSkills.Count
            || t.ActiveSkills[effectRow] is not int active || active < 0) return null;
        int row = PickLevel(t.PerLevel, effectRow, level);
        return row >= 0 && t.VaalSouls[row] >= 0 && t.VaalStoredUses[row] > 0
            ? new((uint)active, t.VaalSouls[row], t.VaalStoredUses[row]) : null;
    }

    public int SoulGainPreventionMs(int effect, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effect, level) is int row and >= 0 ? t.SoulPrevention[row] : 0;

    public int BaseRageCost(int effect, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effect, level) is int row and >= 0 ? t.RageCost[row] : 0;

    /// <summary>The resources a GrantedEffects Id reserves at a level, or null.</summary>
    public ReservationCost? Reservation(string effectId, int level) =>
        EffectRow(effectId) is int row and >= 0 ? Reservation(row, level) : null;

    /// <summary>A GrantedEffects row's CastTime in ms (0 when unknown).</summary>
    public int CastMs(int effectRow) =>
        _tables is { } t && effectRow >= 0 && effectRow < t.Effects.Count ? t.Effects[effectRow].CastTime : 0;

    /// <summary>A GrantedEffects row's cooldown at a level in ms (GEPL Cooldown: Haste 1200, Righteous Fire 300), 0 when
    /// it has none.</summary>
    public int CooldownMs(int effectRow, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effectRow, level) is int gepl and >= 0 ? t.Cooldowns[gepl].Ms : 0;

    public int CooldownUses(int effectRow, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.Cooldowns[row].Uses : 0;

    /// <summary>A GrantedEffects row's GEPL CooldownBypassType at a level (<see cref="CooldownBypass"/>), 0 when none.</summary>
    public int CooldownBypassType(int effectRow, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.Cooldowns[row].Bypass : 0;

    public int CooldownGroup(int effectRow, int level) =>
        _tables is { } t && PickLevel(t.PerLevel, effectRow, level) is int row and >= 0 ? t.Cooldowns[row].Group : 36;

    public bool CooldownPausesWhileActive(int effectRow) =>
        _tables is { } t && effectRow >= 0 && effectRow < t.Effects.Count && t.Effects[effectRow].CooldownPauses;

    /// <summary>Whether a GrantedEffects row's skill is instant: its ActiveSkills row has the ActiveSkillType Instant (Haste,
    /// Righteous Fire). An instant skill is used while moving, without an action of its own.</summary>
    public bool IsInstant(int effectRow) => _instantEffects.Contains(effectRow);

    /// <summary>A movement skill (ActiveSkillType Movement: Leap Slam, Whirling Blades, Flame Dash …): the player ends
    /// where it was aimed.</summary>
    public bool IsMovement(int effectRow) => _movementEffects.Contains(effectRow);
    public bool IsChannelled(int effectRow) => _skillTypes.Channel.Contains(effectRow);
    public bool IsTravel(int effectRow) => _skillTypes.Travel.Contains(effectRow);
    public bool IsGuard(int effectRow) => _skillTypes.Guard.Contains(effectRow);
    const int ShieldItemClass = 26; // ItemClasses row, also the sole weapon restriction on Spectral Shield Throw.

    /// <summary>A shield requirement from the skill type or an exclusive shield item-class restriction.</summary>
    public bool RequiresShield(int effectRow) => _skillTypes.RequiresShield.Contains(effectRow)
        || _weaponRestrictions.TryGetValue(effectRow, out var rows) && rows.Count == 1 && rows.Contains(ShieldItemClass);

    /// <summary>Whether a GrantedEffects row's skill is an attack (ActiveSkillType Attack: the default attack, Holy
    /// Strike, Flicker Strike …): it swings with the weapon's attack time and its use is paced like the default
    /// attack's, unlike a spell, which the client paces by its cast time.</summary>
    public bool IsAttack(int effectRow) => _attackEffects.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's skill classifies Melee (the ActiveSkillType). Decides whether an attack
    /// gem's hit takes melee_damage_+% (the default attack does by its weapon profile instead).</summary>
    public bool IsMelee(int effectRow) => _meleeEffects.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's ActiveSkill has the Area type (slams, sweeps, novas, area spells).</summary>
    public bool IsArea(int effectRow) => _skillTypes.HasType(effectRow, "Area");

    public bool IsSpell(int effectRow) => _spellEffects.Contains(effectRow);
    public bool IsProjectile(int effectRow) => _projectileEffects.Contains(effectRow);
    // Generic monster Melee also carries Projectile. Actual melee classification takes precedence.
    public bool IsRangedAttack(int effectRow) => IsAttack(effectRow) && IsProjectile(effectRow) && !IsMelee(effectRow);

    /// <summary>Classify the gem's base-item row from GemTags/SkillGems metadata, not its display name.</summary>
    public bool FamilyStatRaises(uint stat, int gemRow) =>
        stat is var key && _families.Raises((int)key - 1, gemRow);

    public bool FamilyStatRaises(uint stat, int gemRow, int variantIndex) =>
        _families.Raises((int)stat - 1, gemRow, variantIndex);

    public bool IsExceptional(int gemRow) => _families.IsExceptional(gemRow);

    public bool IsExceptional(int gemRow, int variantIndex) => _families.IsExceptional(gemRow, variantIndex);

    public bool GemHasTag(int gemRow, int variantIndex, string tag) => _families.HasTag(gemRow, variantIndex, tag);

    readonly SkillGemFamilies _families;

    /// <summary>The ItemClasses rows a GrantedEffects row's skill can be used with (its ActiveSkill's
    /// WeaponRestriction_ItemClassesKeys: Burning Arrow 13, Bow); empty for a skill any hands can use. The client greys
    /// out a skill when neither hand fits (11-skills.md §10a).</summary>
    public IReadOnlySet<int> WeaponRestrictions(int effectRow) =>
        _weaponRestrictions.TryGetValue(effectRow, out var rows) ? rows : FrozenSet<int>.Empty;

    private readonly Dictionary<int, IReadOnlySet<int>> _weaponRestrictions;

    Dictionary<int, IReadOnlySet<int>> LoadWeaponRestrictions()
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
    }

    /// <summary>Whether a GrantedEffects row's ActiveSkill has the Warcry type.</summary>
    public bool IsWarcry(int effectRow) => _warcryEffects.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's ActiveSkill has the Retaliation type (row 120).</summary>
    public bool IsRetaliation(int effectRow) => HasType(effectRow, "Retaliation");

    /// <summary>A Strike skill: ActiveSkillType MeleeSingleTarget, whose FlagStat is <c>skill_is_strike_skill</c>, the
    /// stat the client's own strike virtuals test (<c>0x14289ced0</c>, <c>0x1428b7e90</c>). The default attack is one
    /// (PoB's Default Attack has MeleeSingleTarget too).</summary>
    public bool IsStrike(int effectRow) => _strikeEffects.Contains(effectRow);

    /// <summary>ActiveSkillType Vaal (FlagStat <c>skill_is_vaal_skill</c>).</summary>
    public bool IsVaal(int effectRow) => _vaalEffects.Contains(effectRow);

    /// <summary>A Nova skill (ActiveSkillType Nova: Ice Nova, Discharge, Shock Nova, but also Shockwave Totem and
    /// Stormblast Mine).</summary>
    public bool IsNova(int effectRow) => _novaEffects.Contains(effectRow);

    /// <summary>A self-targeted skill (ActiveSkillTargetTypes 4 or 10: Ice Nova, Discharge, Vaal Cold Snap), whose use
    /// the client centres on the caster (FUN_1419439e0). Totems, mines and traps are left out: their effect goes off at
    /// the object they place.</summary>
    public bool IsSelfTargeted(int effectRow) => _selfTargetedEffects.Contains(effectRow);

    /// <summary>Aura metadata, including only supports already accepted by GemSupports.Resolve.</summary>
    public bool IsAura(int effectRow, IEnumerable<int>? acceptedSupports = null) =>
        _auraEffects.Contains(effectRow)
        || acceptedSupports?.Any(row => _addedAuraEffects.Contains(row)) == true;
    /// <summary>The native Brand skill type, independently of display name or gem tags.</summary>
    public bool IsBrand(int effectRow) => _brandEffects.Contains(effectRow);

    /// <summary>Whether a GrantedEffects row's ActiveSkill has an ActiveSkillType by its data Id (Trap, Mine, Totem,
    /// Channel, ...): the named helpers cover the common ones, this reads any type a caller names.</summary>
    public bool HasType(int effectRow, string typeId) => _skillTypes.HasType(effectRow, typeId);

    /// <summary>The ActiveSkillType Ids a support adds to the skill it supports (Trap Support adds Trap, Ballista Totem
    /// Support adds Totem): the run uses these to keep mechanics it cannot place out of the support pool.</summary>
    public IReadOnlyList<string> AddedTypes(int effectRow) => _skillTypes.AddedTypes(effectRow);

    // The effects of one ActiveSkillType each: ActiveSkillType, ActiveSkills and GrantedEffects are parsed once for all
    // of them (each set used to parse the three tables again: ~100 MB of JSON per catalogue at the first spawn).
    private readonly SkillTypeSets _skillTypes;
    private HashSet<int> _instantEffects => _skillTypes.Instant;
    private HashSet<int> _movementEffects => _skillTypes.Movement;
    private HashSet<int> _attackEffects => _skillTypes.Attack;
    private HashSet<int> _meleeEffects => _skillTypes.Melee;
    private HashSet<int> _spellEffects => _skillTypes.Spell;
    private HashSet<int> _projectileEffects => _skillTypes.Projectile;
    private HashSet<int> _warcryEffects => _skillTypes.Warcry;
    private HashSet<int> _strikeEffects => _skillTypes.Strike;
    private HashSet<int> _vaalEffects => _skillTypes.Vaal;
    private HashSet<int> _novaEffects => _skillTypes.Nova;
    private HashSet<int> _selfTargetedEffects => _skillTypes.SelfTargeted;
    private HashSet<int> _auraEffects => _skillTypes.Aura;
    private HashSet<int> _curseEffects => _skillTypes.Curse;
    private HashSet<int> _addedAuraEffects => _skillTypes.AddedAura;
    private HashSet<int> _brandEffects => _skillTypes.Brand;

    /// <summary>The GrantedEffects rows of each ActiveSkillType (and, added, of the supports that add the type to the skill
    /// they support), built in one pass over the three tables when the catalog is made.</summary>
    sealed class SkillTypeSets
    {
        readonly Dictionary<string, int> _typeIndex = new(StringComparer.Ordinal);
        readonly HashSet<int>[] _byType = [];
        readonly HashSet<int>[] _addedByType = [];
        readonly string[][] _addedTypes = [];
        static readonly HashSet<int> None = [];

        public SkillTypeSets(string folder)
        {
            if (ClientTables.Has("GrantedEffects") && ClientTables.Has("ActiveSkills") && ClientTables.Has("ActiveSkillType"))
            {
                var typeIds = ClientTables.Table("ActiveSkillType").Select(r => r.TextOrNull("Id")).ToArray();
                for (int i = 0; i < typeIds.Length; i++)
                    if (typeIds[i] is { } id) _typeIndex.TryAdd(id, i);
                _byType = [.. typeIds.Select(_ => new HashSet<int>())];
                _addedByType = [.. typeIds.Select(_ => new HashSet<int>())];
                var activeTypes = ClientTables.Table("ActiveSkills").Select(r => r.Ints("ActiveSkillTypes")).ToArray();
                var effects = ClientTables.Table("GrantedEffects");
                _addedTypes = new string[effects.Count][];
                int row = 0;
                foreach (var effect in effects)
                {
                    if (effect.IntOrNull("ActiveSkill") is int skill && skill >= 0 && skill < activeTypes.Length)
                        foreach (int type in activeTypes[skill])
                            if (type >= 0 && type < _byType.Length) _byType[type].Add(row);
                    var added = effect.Ints("AddedActiveSkillTypes");
                    foreach (int type in added)
                        if (type >= 0 && type < _addedByType.Length) _addedByType[type].Add(row);
                    _addedTypes[row] = [.. added.Select(i => i >= 0 && i < typeIds.Length ? typeIds[i] ?? "" : "").Where(id => id.Length > 0)];
                    row++;
                }
            }
            Instant = Of("Instant"); Movement = Of("Movement"); Attack = Of("Attack"); Melee = Of("Melee");
            Spell = Of("Spell"); Projectile = Of("Projectile"); Warcry = Of("Warcry"); Aura = Of("Aura");
            Curse = Of("AppliesCurse"); AddedAura = Of("Aura", added: true); Brand = Of("Brand");
            Travel = Of("Travel"); Guard = Of("Guard"); Channel = Of("Channel");
            Strike = Of("MeleeSingleTarget"); Vaal = Of("Vaal"); Nova = Of("Nova"); RequiresShield = Of("RequiresShield");
            SelfTargeted = EffectsOfTargetType(folder, 4, 10);
            foreach (string placed in new[] { "SummonsTotem", "RemoteMined", "Trapped" }) SelfTargeted.ExceptWith(Of(placed));
        }

        public HashSet<int> Instant { get; }
        public HashSet<int> Movement { get; }
        public HashSet<int> Attack { get; }
        public HashSet<int> Melee { get; }
        public HashSet<int> Spell { get; }
        public HashSet<int> Projectile { get; }
        public HashSet<int> Warcry { get; }
        public HashSet<int> Aura { get; }
        public HashSet<int> Curse { get; }
        public HashSet<int> AddedAura { get; }
        public HashSet<int> Brand { get; }
        public HashSet<int> Travel { get; }
        public HashSet<int> Channel { get; }
        public HashSet<int> Guard { get; }
        public HashSet<int> Strike { get; }
        public HashSet<int> Vaal { get; }
        public HashSet<int> Nova { get; }
        public HashSet<int> RequiresShield { get; }
        public HashSet<int> SelfTargeted { get; }

        HashSet<int> Of(string typeId, bool added = false) =>
            _typeIndex.TryGetValue(typeId, out int type) ? (added ? _addedByType : _byType)[type] : None;

        public bool HasType(int effectRow, string typeId) => Of(typeId).Contains(effectRow);

        public IReadOnlyList<string> AddedTypes(int effectRow) =>
            effectRow >= 0 && effectRow < _addedTypes.Length ? _addedTypes[effectRow] : [];
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
    public (int AttackMs, int Min, int Max, int Range)? WeaponBase(int baseRow) => _weaponTypes.TryGetValue(baseRow, out var w) ? w : null;

    private readonly Dictionary<int, (int, int, int, int)> _weaponTypes;

    Dictionary<int, (int, int, int, int)> LoadWeaponTypes()
    {
        var result = new Dictionary<int, (int, int, int, int)>();
        if (!ClientTables.Has("WeaponTypes")) return result;
        var d = ClientTables.Table("WeaponTypes");
        foreach (var r in d)
            result.TryAdd(r.Int("BaseItemTypesKey"), (r.Int("Speed"), r.Int("DamageMin"),
                r.Int("DamageMax"), r.Int("RangeMax")));
        return result;
    }

    /// <summary>A projectile type for an effect: the Projectiles.dat row whose Id is Metadata/Projectiles/(GrantedEffects
    /// Id), e.g. Fireball → Metadata/Projectiles/Fireball at speed 1040, or null. The real link between a skill and its
    /// projectile is not known yet; the Gateway matches the Id, and <see cref="ProjectileFallbacks"/> covers the gems
    /// whose row is not named after their effect. Kinetic Blast (ClusterBurst) fires the wand attack's own projectile
    /// (3.29 has no ClusterBurst row); its explosion is a separate impact art (Program.Combat).</summary>
    /// <summary>The projectile the client flies for an effect, as its resolver picks it (RE 012 resource-selection §1–3:
    /// 0x1419417c0 → 0x141f67770): the SkillArtVariations rows associated with the GrantedEffects row, then those
    /// associated with its ActiveSkills row, each in row order; the first whose ProjectileArt has the slot and whose
    /// ProjectilesArtVariations Id has a row at the variant gives that row's Projectiles (the first, when there are several:
    /// none of the rows this is used for has more). The variant is the effect's <c>skill_art_variation</c> (<see cref="ArtVariation"/>), 0 without one (SynthesisColdRend 10506 art 0, LegionVaalSoulrend 10507 art 1). Null when no
    /// candidate has one: the client then flies nothing from this path.</summary>
    public (string Id, int Speed, int CollisionRadius)? SkillArtProjectile(int effectRow, int slot = 0, int variant = -1)
    {
        if (variant < 0) variant = ArtVariation(effectRow);
        var art = _skillArt;
        IEnumerable<int> candidates = art.ByEffect.GetValueOrDefault(effectRow, []);
        if (_activeSkillOfEffect.GetValueOrDefault(effectRow, -1) is >= 0 and var active)
            candidates = candidates.Concat(art.ByActive.GetValueOrDefault(active, []));
        foreach (int row in candidates)
        {
            var slots = art.ProjectileArt[row];
            if (slot >= slots.Count || !art.ArtIds.TryGetValue(slots[slot], out string? id)
                || !art.Directory.TryGetValue((id, variant), out var projectiles) || projectiles.Count == 0) continue;
            return projectiles[0];
        }
        return null;
    }

    /// <summary>The Projectiles row an effect's action class creates when its art names none: the row of another effect on
    /// the same ActiveSkills row, the action the client runs for both (<see cref="ProjectileFor"/>: Ethereal Knives'
    /// EtherealKnivesProjectile, Spectral Throw's ThrownWeapon, Essence Drain's Siphon, all live-verified for the player's
    /// gem). A monster's ExileEtherealKnives shares ethereal_knives with the gem's EtherealKnives. Its own row first, then
    /// the others' in table order; null when none of them names one, except on a skill in <see cref="_noBorrowSkills"/> (monster_projectile_weapon: the effect's own row only; its projectile is its variation row, <see cref="VariationProjectile"/>) (a bow's arrow comes from the equipped item:
    /// projectiles/bow-wand-and-burning-arrow.md, its priority still open).</summary>
    public (string Id, int Speed, int CollisionRadius)? SharedProjectile(int effectRow)
    {
        // monster_projectile_weapon is shared by unrelated monsters: another effect's
        // row is not this one's projectile. Its own row, then its variation row (VariationProjectile), only.
        bool generic = _activeSkillOfEffect.TryGetValue(effectRow, out int activeRow) && _noBorrowSkills.Contains(activeRow);
        foreach (int row in generic ? [effectRow] : SameActionEffects(effectRow))
            if (ProjectileFor(row) is { } found)
            {
                int radius = ClientTables.TableOrEmpty("Projectiles").FirstOrDefault(p => p.Text("Id") == found.Id).IntOrNull("CollisionRadius") ?? 1;
                return (found.Id, found.Speed, radius);
            }
        return null;
    }

    /// <summary>ActiveSkills whose projectile comes per effect from a variation table (monster_projectile_weapon), so
    /// <see cref="SharedProjectile"/> never borrows from another effect on them.</summary>
    private readonly HashSet<int> _noBorrowSkills;

    HashSet<int> LoadNoBorrowSkills() => ClientTables.TableOrEmpty("ActiveSkills").Select((r, i) => (r, i))
        .Where(a => a.r.TextOrNull("Id") == "monster_projectile_weapon").Select(a => a.i).ToHashSet();

    /// <summary>The projectile the client's own path gives a weapon-attack effect: its <c>monster_projectile_variation</c>
    /// names a MonsterProjectileAttack row, which names the Projectiles row (Roc Sentinel's dash 76 LegionMarakethRangedDash,
    /// Lioneye's snipe 78 LegionLioneyeSnipeMedium). Resolved when the catalog loads. Null for an effect without a variation.
    /// The server still publishes the object itself (Variation 0), as for a spell projectile.</summary>
    public (string Id, int Speed, int CollisionRadius)? VariationProjectile(int effectRow) =>
        _weaponMonsterProjectiles.TryGetValue(effectRow, out var p) && !_monsterProjectiles.ContainsKey(effectRow)
            ? (p.Metadata, p.Speed, (int)Math.Max(1, p.CollisionRadius)) : null;

    /// <summary>The effect itself, then every other GrantedEffects row on its ActiveSkills row in table order: the effects
    /// whose action the client runs alike (a monster's ExileStormCall and the gem's StormCall are both storm_call). What
    /// the server knows of one by its Id (a projectile row, a base radius, a skill family) holds for the others.</summary>
    public IEnumerable<int> SameActionEffects(int effectRow)
    {
        yield return effectRow;
        if (_activeSkillOfEffect.GetValueOrDefault(effectRow, -1) is not (>= 0 and var active)) yield break;
        foreach (var (row, skill) in _activeSkillOfEffect.OrderBy(p => p.Key))
            if (skill == active && row != effectRow) yield return row;
    }

    sealed record SkillArt(Dictionary<int, List<int>> ByEffect, Dictionary<int, List<int>> ByActive, List<IReadOnlyList<int>> ProjectileArt,
        Dictionary<int, string> ArtIds, Dictionary<(string Id, int Variant), List<(string Id, int Speed, int CollisionRadius)>> Directory);

    private readonly SkillArt _skillArt;

    SkillArt LoadSkillArt()
    {
        Dictionary<int, List<int>> byEffect = [], byActive = [];
        List<IReadOnlyList<int>> projectileArt = [];
        Dictionary<int, string> artIds = [];
        Dictionary<(string, int), List<(string, int, int)>> directory = [];
        if (!ClientTables.Has("SkillArtVariations") || !ClientTables.Has("ProjectilesArtVariations") || !ClientTables.Has("Projectiles"))
            return new(byEffect, byActive, projectileArt, artIds, directory);
        int index = 0;
        foreach (var r in ClientTables.Table("SkillArtVariations"))
        {
            projectileArt.Add([.. r.Ints("ProjectileArt")]);
            foreach (int e in r.Ints("GrantedEffect")) { if (!byEffect.TryGetValue(e, out var l)) byEffect[e] = l = []; l.Add(index); }
            foreach (int a in r.Ints("ActiveSkill")) { if (!byActive.TryGetValue(a, out var l)) byActive[a] = l = []; l.Add(index); }
            index++;
        }
        var projectiles = ClientTables.Table("Projectiles").Select(p => (p.Text("Id") ?? "", p.IntOrNull("ProjectileSpeed") ?? 0,
            p.IntOrNull("CollisionRadius") ?? 1)).ToList();
        index = 0;
        foreach (var r in ClientTables.Table("ProjectilesArtVariations"))
        {
            string id = r.Text("Id") ?? "";
            artIds[index++] = id;
            // The directory's exact (Id, variant) key; on a duplicate the first row wins (0x14222a250).
            directory.TryAdd((id, r.IntOrNull("Variant") ?? 0),
                [.. r.Ints("Projectiles").Where(p => p >= 0 && p < projectiles.Count).Select(p => projectiles[p])]);
        }
        return new(byEffect, byActive, projectileArt, artIds, directory);
    }

    private readonly Dictionary<int, int> _activeSkillOfEffect;

    Dictionary<int, int> LoadActiveSkillOfEffect()
    {
        Dictionary<int, int> result = [];
        if (!ClientTables.Has("GrantedEffects")) return result;
        int row = 0;
        foreach (var e in ClientTables.Table("GrantedEffects"))
        {
            if (e.IntOrNull("ActiveSkill") is int a) result[row] = a;
            row++;
        }
        return result;
    }

    public (string Id, int Speed)? ProjectileFor(int effectRow)
    {
        if (_tables is not { } t) return null;
        string? effect = t.EffectIds.GetValueOrDefault(effectRow);
        if (effect is null) return null;
        foreach (string id in ProjectileCandidates(effect))
            if (_projectiles.TryGetValue(id, out int speed)) return (id, speed);
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
    // Molten Strike fires Metadata/Projectiles/MoltenStrikeChunk (Projectiles row 107; Speed 750, 633 ms flight clip).
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
        ["Snipe"] = "Metadata/Projectiles/ChannelledSnipeDefaultProjectile",
        ["SpectralThrow"] = "Metadata/Projectiles/ThrownWeapon",
        // Venom Gyre throws the same claw the Spectral Throw row draws: the client tables have no
        // Metadata/Projectiles/VenomGyre row at all, and the gem grants VenomGyre (GrantedEffects row 10850)
        // and VaalVenomGyre (row 12333); both take the ThrownWeapon row (row 94, speed 1000). Without these
        // every press was refused as having no projectile. The transfigured VenomGyreAltX/AltY/Royale resolve
        // through the base effect's entry via ProjectileCandidates, so they need no entry of their own.
        ["VenomGyre"] = "Metadata/Projectiles/ThrownWeapon",
        ["VaalVenomGyre"] = "Metadata/Projectiles/ThrownWeapon",
        ["LightningStrike"] = "Metadata/Projectiles/LightningSlash",
        // Unearth fires its old name's row, BoneLance: the BoneLance action class loads Metadata/Projectiles/BoneLance
        // (3.29.3c FUN_1413e64b0, by skill_art_variation).
        ["Unearth"] = "Metadata/Projectiles/BoneLance",
        ["EssenceDrain"] = "Metadata/Projectiles/Siphon",
        ["EssenceDrainAltX"] = "Metadata/Projectiles/Siphon",
        ["EssenceDrainAltY"] = "Metadata/Projectiles/Siphon",
        ["SplitArrow"] = "Metadata/Projectiles/SplitArrowDefault",
        ["SplitArrowAltX"] = "Metadata/Projectiles/SplitArrowDefault",
        ["SplitArrowAltY"] = "Metadata/Projectiles/SplitArrowDefault",
        ["VaalSplitArrow"] = "Metadata/Projectiles/VaalSplitArrowDefault",
        ["LancingSteel"] = "Metadata/Projectiles/LancingSteelNewProjectile",
        ["ShatteringSteel"] = "Metadata/Projectiles/ShatteringSteelProjectile",
        ["SplittingSteel"] = "Metadata/Projectiles/ImpactingSteelProjectile",
        ["SplittingSteelAltX"] = "Metadata/Projectiles/ImpactingSteelProjectile",
        ["SplittingSteelAltY"] = "Metadata/Projectiles/ImpactingSteelProjectile",
        ["MoltenStrike"] = "Metadata/Projectiles/MoltenStrikeChunk",
        ["MoltenStrikeAltX"] = "Metadata/Projectiles/MoltenStrikeChunk",
        ["MoltenStrikeAltY"] = "Metadata/Projectiles/MoltenStrikeChunk",
        ["VaalMoltenStrike"] = "Metadata/Projectiles/MoltenStrikeChunk",
    };

    /// <summary>A Projectiles.dat row by its Id (<c>Metadata/Projectiles/DefaultArrow</c> for a bow's ordinary attack).</summary>
    public (string Id, int Speed)? Projectile(string id) =>
        _projectiles.TryGetValue(id, out int speed) ? (id, speed) : null;

    /// <summary>A Projectiles.dat row's max(CollisionRadius, CollisionCandidateRadius): the reach of the client's object
    /// contact for a projectile it draws itself (0x141e35a50 → 0x141e4b140, 11-skills.md "Hit").</summary>
    public int? ProjectileContactCells(string id) => _projectileContacts.TryGetValue(id, out int cells) ? cells : null;

    private readonly Dictionary<string, int> _projectileContacts;

    Dictionary<string, int> LoadProjectileContacts()
    {
        var result = new Dictionary<string, int>();
        if (ClientTables.TableOrNull("Projectiles") is not { } d) return result;
        static int Cells(ClientRow r, string key) => r.IntOrNull(key) ?? 0;
        foreach (var r in d)
            if (r.Text("Id") is { } id)
                result.TryAdd(id, Math.Max(Cells(r, "CollisionRadius"), Cells(r, "CollisionCandidateRadius")));
        return result;
    }

    private readonly Dictionary<string, int> _projectiles;

    Dictionary<string, int> LoadProjectiles()
    {
        var result = new Dictionary<string, int>();
        if (!ClientTables.Has("Projectiles")) return result;
        var d = ClientTables.Table("Projectiles");
        foreach (var r in d)
            if (r.Text("Id") is { } id)
                result.TryAdd(id, r.IntOrNull("ProjectileSpeed") is int v ? v : 0);
        return result;
    }

    /// <summary>A Projectiles.dat row's first AOFiles entry: the art the client plays for that projectile object
    /// (SplitArrowDefault's split_arrow.ao, which Arrow Nova attaches to its rain rig).</summary>
    public string? ProjectileAo(string id) =>
        _projectileAos.TryGetValue(id, out var ao) ? ao : null;

    private readonly Dictionary<string, string> _projectileAos;

    Dictionary<string, string> LoadProjectileAos()
    {
        var result = new Dictionary<string, string>();
        if (!ClientTables.Has("Projectiles")) return result;
        var d = ClientTables.Table("Projectiles");
        foreach (var r in d)
            if (r.Text("Id") is { } id && r.Texts("AOFiles") is [var ao, ..])
                result.TryAdd(id, ao);
        return result;
    }

    /// <summary>One clip of a projectile type's rig: its name, index in the rig's animation list (what 0x1b2 plays), length
    /// and whether it plays once (client.db's projectile_clips, from scripts/re/projectile_clips.py); a flight clip also its
    /// first "contact" in ms (its length without one), by which the client times a spell mortar's landing.</summary>
    public sealed record ProjectileClip(string Name, uint Index, int Ms, bool Once, int? Contact = null)
    {
        /// <summary>The flight's candidate clips (a group, several names) land at different contacts.</summary>
        public bool Varies { get; init; }
    }

    /// <summary>What the client needs to show a server-created object of a projectile type: the .ao's default animation,
    /// if it declares one (the wand bolt's <c>projectile_01</c>: it flies by itself), the flight clip (Projectiles.dat
    /// LoopAnimationIds, the Sand Spitter mortar's <c>proj</c>: the object is invisible until it plays) and the impact
    /// clip (ImpactAnimationIds, <c>end</c>).</summary>
    public sealed record ProjectileClips(string? DefaultAnimation, ProjectileClip? Flight, ProjectileClip? Impact);

    public ProjectileClips? Clips(string projectileId) =>
        _projectileClips.TryGetValue(projectileId, out var clips) ? clips : null;

    private readonly Dictionary<string, ProjectileClips> _projectileClips;

    Dictionary<string, ProjectileClips> LoadProjectileClips()
    {
        var result = new Dictionary<string, ProjectileClips>();
        foreach (var r in ClientTables.TableOrEmpty("projectile_clips"))
        {
            ProjectileClip? Clip(string key) =>
                r.TextOrNull(key + "_name") is { } name && r.LongOrNull(key + "_index") is { } index && r.IntOrNull(key + "_ms") is { } length
                    ? new(name, (uint)index, length, r.BoolOrNull(key + "_once") ?? false, r.IntOrNull(key + "_contact"))
                        { Varies = r.BoolOrNull(key + "_varies") ?? false } : null;
            result[r.Text("type")!] = new(r.TextOrNull("default"), Clip("flight"), Clip("impact"));
        }
        return result;
    }

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
        /// <summary>Spiral nova timing and arc metadata (<c>projectile_spiral_nova_*</c>) from the skill's stat set.</summary>
        public int SpiralTimeMs { get; init; }
        public int SpiralAngle { get; init; }
        public int SpiralStartAngle { get; init; }
    }

    /// <summary>One clip of a monster's attack pool (client.db's monster_contact_clips, from scripts/re/monster_contacts.py): its name,
    /// its index in the rig's animation list, its length, and its contact keyframe's time and position when it has one
    /// (the Sand Spitter's attack1: 375 of 1000 ms, 45.9 units behind the model and 71.6 up).</summary>
    public sealed record MonsterClipContact(string Animation, uint Index, int LengthMs, int? ContactMs, ContactPoint? Point)
    {
        /// <summary>Every contact of a clip with several (monster_contact_clip_frames: the Iguana's five spines), in time
        /// order; null for a clip with one or none.</summary>
        public IReadOnlyList<(int Ms, ContactPoint? Point)>? Contacts { get; init; }

        /// <summary>The merged AO/rig clock was extracted completely. False for legacy data or an unsupported span;
        /// true with null slow bounds means the clip has no slow span.</summary>
        public bool ClockMetadataAvailable { get; init; }
        public int? SlowBeginMs { get; init; }
        public int? SlowEndMs { get; init; }
        public double? BudgetSeconds { get; init; }

        /// <summary>The contact as a fraction of the clip, clamped to it: an .ao event time is independent of the rig's
        /// clip length, and a release past the attack's end would miss its busy period.</summary>
        public double? ContactFraction => ContactMs is int ms && LengthMs > 0 ? Math.Clamp((double)ms / LengthMs, 0, 1) : null;

        /// <summary>Every contact frame of the clip in order (ms): a multi-strike skill's later strikes land on the later
        /// ones (Double Strike's doublestrike_bow: 166 and 633 of 1000). Empty when the data names none.</summary>
        public IReadOnlyList<int> ContactsMs { get; init; } = [];

        /// <summary>The clip's contact at <paramref name="index"/> as a fraction of the clip; null past its last.</summary>
        public double? ContactFractionAt(int index) =>
            index < ContactsMs.Count && LengthMs > 0 ? Math.Clamp((double)ContactsMs[index] / LengthMs, 0, 1) : null;
    }

    /// <summary>The clip the client plays for a monster's action: the variety's ACT pool for the skill's animation key
    /// (<c>SpellProjectile</c> for the Spitter's mortar), chosen as the client chooses it, with the native selector seeded
    /// by the action's sequence (AnimationVariantSelector). Null without data for the variety or key.</summary>
    public MonsterClipContact? MonsterContact(string varietyId, string animationKey, ushort sequence)
    {
        if (!_monsterContacts.TryGetValue(varietyId, out var pools) || !pools.TryGetValue(animationKey, out var pool)
            || pool.Length == 0) return null;
        // Equal weights for every clip; the pool's size comes from a data file, so only a small one lives on the stack.
        Span<uint> weights = pool.Length <= 32 ? stackalloc uint[pool.Length] : new uint[pool.Length];
        weights.Fill(1);
        return pool[AnimationVariantSelector.Select(sequence, weights)];
    }

    /// <summary>A variety's whole pool for an animation key (every clip the selector may pick), or null.</summary>
    public MonsterClipContact[]? MonsterContactPool(string varietyId, string animationKey) =>
        _monsterContacts.TryGetValue(varietyId, out var pools) && pools.TryGetValue(animationKey, out var pool) ? pool : null;

    private readonly Dictionary<string, Dictionary<string, MonsterClipContact[]>> _monsterContacts;

    /// <summary>The ordinary melee clip clock from the variety's actual AO, independent of the legacy projectile
    /// and boss contact corpus. Null when that separate metadata has not been extracted.</summary>
    public MonsterClipContact? MeleeClock(string varietyId, ushort sequence)
    {
        if (!_monsterMeleeClocks.TryGetValue(varietyId, out var pool) || pool.Length == 0) return null;
        Span<uint> weights = pool.Length <= 32 ? stackalloc uint[pool.Length] : new uint[pool.Length];
        weights.Fill(1);
        return pool[AnimationVariantSelector.Select(sequence, weights)];
    }

    /// <summary>Nothing left to build: the constructor reads the melee clocks and the actions. Kept for the game
    /// module's load.</summary>
    public void WarmMonsterMeleeClocks() { }

    /// <summary>Nothing left to build: the constructor reads every table this catalog answers from. Kept for the game
    /// module's startup warm-up.</summary>
    public void Warm() { }

    private readonly Dictionary<string, MonsterClipContact[]> _monsterMeleeClocks;

    static Dictionary<string, MonsterClipContact[]> LoadMonsterMeleeClocks()
    {
        var result = new Dictionary<string, MonsterClipContact[]>(StringComparer.Ordinal);
        if (ClientTables.TableOrNull("monster_melee_clocks") is not { } clocks
            || ClientTables.TableOrNull("monster_contacts") is not { } varieties) return result;
        var byVariety = clocks.By("monster");
        foreach (var variety in varieties)
        {
            var pool = byVariety[variety.Index].OrderBy(c => c.Int("ord"))
                .Select(c => new MonsterClipContact(c.Text("animation")!, c.UInt("index"), c.Int("length"), c.IntOrNull("contact"), null)
                {
                    ClockMetadataAvailable = c.BoolOrNull("clock") == true,
                    SlowBeginMs = c.IntOrNull("slow_begin"), SlowEndMs = c.IntOrNull("slow_end"),
                    BudgetSeconds = c.RealOrNull("budget"),
                }).ToArray();
            if (pool.Length > 0) result[variety.Text("variety")!] = pool;
        }
        return result;
    }

    Dictionary<string, Dictionary<string, MonsterClipContact[]>> LoadMonsterContacts()
    {
        var result = new Dictionary<string, Dictionary<string, MonsterClipContact[]>>(StringComparer.Ordinal);
        if (ClientTables.TableOrNull("monster_contacts") is not { } varieties) return result;
        var clipsByVariety = ClientTables.Table("monster_contact_clips").By("monster");
        var framesByClip = ClientTables.TableOrNull("monster_contact_clip_frames")?.By("clip");
        IReadOnlyList<(int, ContactPoint?)>? Frames(int clip) => framesByClip is null ? null
            : framesByClip[clip].OrderBy(f => f.Int("ord")).Select(f => (f.Int("ms"), AnimationContacts.Point(f, "position"))).ToArray() is { Length: > 1 } all
                ? all : null;
        foreach (var variety in varieties)
        {
            var byKey = clipsByVariety[variety.Index]
                .Where(c => c.TextOrNull("animation") is not null && !c.IsNull("index") && c.IntOrNull("length") is > 0)
                .GroupBy(c => c.Text("pool")!, StringComparer.Ordinal)
                .ToDictionary(pool => pool.Key, pool => pool.Select(c => new MonsterClipContact(c.Text("animation")!, c.UInt("index"),
                    c.Int("length"), c.IntOrNull("contact"), AnimationContacts.Point(c, "position")) { Contacts = Frames(c.Index) }).ToArray(),
                    StringComparer.Ordinal);
            if (byKey.Count > 0) result[variety.Text("variety")!] = byKey;
        }
        return result;
    }

    /// <summary>The projectile of a monster's granted effect: for a mortar skill (ActiveSkills Id <c>monster_mortar*</c>)
    /// the GrantedEffects row's SkillID is the MonsterMortar row that names it (every Sand Spitter variant: SkillID 183 →
    /// MonsterMortar 183 → <c>Metadata/Projectiles/SandspitterMortarProjectile</c>, 1170). Weapon projectile attacks
    /// (<c>monster_projectile_weapon</c>) use <c>monster_projectile_variation</c> through MonsterProjectileAttack (the
    /// summoned Snake: variation 1 → Projectile 31 → <c>Metadata/Projectiles/Snake</c>). Other weapon/snipe effects
    /// without that mapping keep their instant contact. The count is the skill's:
    /// <c>number_of_projectiles_override</c> when set, else <c>base_number_of_projectiles</c> (1 without) plus
    /// <c>number_of_additional_projectiles</c> (the side-area Spitter's 3, the Elder Guardian's strafe 5).</summary>
    public MonsterProjectileInfo? MonsterProjectile(int effectRow) => MonsterProjectile(effectRow, includeWeaponAttacks: false);

    /// <summary>Also reads weapon projectile metadata when the caller explicitly enables those attacks.</summary>
    public MonsterProjectileInfo? MonsterProjectile(int effectRow, bool includeWeaponAttacks)
    {
        if (MonsterProjectileOverrides.TryGetValue(effectRow, out var overridden)) return overridden;
        var projectiles = includeWeaponAttacks ? _weaponMonsterProjectiles : _monsterProjectiles;
        if (!projectiles.TryGetValue(effectRow, out var projectile)) return null;
        // Constants of the skill's stat set, so level 1 reads them.
        var stats = EffectStats(effectRow, 1);
        int Get(uint key) => stats.FirstOrDefault(s => s.Item1 == key).Item2;
        int count = Get(Stat.NumberOfProjectilesOverride) is > 0 and var fixedCount ? fixedCount
            : Math.Max(1, Get(Stat.BaseNumberOfProjectiles)) + Get(Stat.NumberOfAdditionalProjectiles);
        return projectile with
        {
            MinimumRange = Get(Stat.ProjectileMinimumRange), SpreadRadius = Get(Stat.ProjectileSpreadRadius),
            Count = Math.Max(1, count),
            SpiralTimeMs = Get(Stat.ProjectileSpiralNovaTimeMs),
            SpiralAngle = Get(Stat.ProjectileSpiralNovaAngle),
            SpiralStartAngle = Get(Stat.ProjectileSpiralNovaStartingAngleOffset),
            // MonsterMortar names the action/data table, not its trajectory. The attack's +0x558 callback
            // (141b6c630 -> 141baf9f0) selects a straight path unless a ballistic stat requests an arc.
            // Straight spell projectiles (MonsterProjectileSpell) start with Lobbed false and stay straight.
            Lobbed = projectile.Lobbed && (projectile.Variation == 0
                || Get(Stat.ProjectileBallisticAngleFromReferenceEvent) != 0
                || Get(Stat.ProjectileBallisticAngleFromTargetDistance) != 0),
        };
    }

    /// <summary>Module-provided monster-projectile art for skills the client tables give no MonsterProjectile row (an
    /// essence daemon's Ball Lightning / Fireball): effect row -> projectile. Populated once at startup by a module; empty
    /// for a build that provides none. Consulted before the tables, so no boss or skill class has to name an essence.</summary>
    public static readonly Dictionary<int, MonsterProjectileInfo> MonsterProjectileOverrides = [];

    /// <summary>What a monster's summoning skill brings: its stat set's <c>alternate_minion</c> (a SummonedSpecificMonsters
    /// Id, whose MonsterVarietiesKey is the monster), <c>number_of_monsters_to_summon</c>, <c>summon_specific_monsters_in_front_offset</c>
    /// (world units ahead of the summoner) and <c>summoned_monsters_no_drops_or_experience</c>, with the effect's cast time
    /// and cooldown (seconds). The Labyrinth's Immolator (<c>SummonLabyrinthFireSkeleton</c>): one Ember Claimant 114
    /// units ahead, a 2.17 s cast, 3 s cooldown. Null for any other skill.</summary>
    public sealed record MonsterSummonInfo(int Variety, int Count, double Offset, bool NoRewards, double CastSeconds, double CooldownSeconds)
    {
        /// <summary>How many of them the summoner keeps (the skill's limit stat); using it past that retires the oldest.
        /// 0 for a skill that names none.</summary>
        public int Limit { get; init; }
        /// <summary>Seconds each lives (the skill's duration); infinity for one that stays.</summary>
        public double Lifetime { get; init; } = double.PositiveInfinity;
        /// <summary>Brought where the skill aims (the player), as the player's own minion skills place theirs, rather
        /// than <see cref="Offset"/> ahead of the summoner.</summary>
        public bool AtTarget { get; init; }
        /// <summary>The GrantedEffects row the summoned body acts with (a totem's totemified skill); -1 for a minion that
        /// fights with its own.</summary>
        public int Effect { get; init; } = -1;
        public bool DiesWithParent { get; init; }
        public bool DiesWhenParentDeleted { get; init; }
    }

    public MonsterSummonInfo? MonsterSummon(int effectRow, int level)
    {
        var stats = EffectStats(effectRow, level);
        int Get(uint key) => stats.FirstOrDefault(s => s.Item1 == key).Item2;
        string? effectId = EffectId(effectRow);
        // Monster Raging Spirit uses alternate_minion as an art variant (1 = ghostflame for Fairgraves), NOT a
        // SummonedSpecificMonsters Id (where 1 is Adolescent Rhoa). Merveil's statue/daughter/spawn skills likewise
        // omit alternate_minion and name their summons by skill type.
        if (effectId is "SummonRagingSpiritFairgraves" or "MonsterSummonRagingSpirit"
            or "MerveilSummonStatue" or "MerveilSummonDaughters" or "MerveilSummonSpawn")
        {
            string targetId = effectId switch
            {
                "SummonRagingSpiritFairgraves" => "Metadata/Monsters/MonsterSummonedSkull/MonsterSummonedSkullGhostly",
                "MonsterSummonRagingSpirit" => "Metadata/Monsters/MonsterSummonedSkull/MonsterSummonedSkull",
                "MerveilSummonStatue" => "Metadata/Monsters/Statue/GoldStatueFemaleSwordMerveil",
                "MerveilSummonDaughters" => "Metadata/Monsters/Seawitch/SeaWitchMerveilSummonFire",
                _ => "Metadata/Monsters/Seawitch/SeaWitchSpawnExploding",
            };
            if (!_monsterVarietyRowsById.TryGetValue(targetId, out int specialVariety)) return null;
            int rawCount = Get(Stat.NumberOfMonstersToSummon);
            int count = effectId == "MerveilSummonSpawn" ? Math.Clamp(rawCount > 0 ? rawCount : 3, 1, 4)
                : Math.Max(1, rawCount > 0 ? rawCount : 2);
            return new(specialVariety, count, Get(Stat.SummonSpecificMonstersInFrontOffset),
                NoRewards: true, CastMs(effectRow) / 1000.0, CooldownMs(effectRow, level) / 1000.0);
        }
        // Only the SummonSpecificMonsters action classes (SummonSpecificMonsters, ...CannotDie, ...FromMortars, ...InFront)
        // read alternate_minion as a SummonedSpecificMonsters Id: Sawbones' 400 is SkeletonMeleeSawbones, Ryslatha's 227
        // RyslathaEgg, Izaro's 63 IzaroSummonSkeletonCasterFire. Every other class that carries the stat holds a small
        // variant index there (Raise Zombie, Summon Raging Spirit and the golems' monster versions all 1, the Harbingers
        // 1 to 14), which names no SummonedSpecificMonsters row of theirs: Id 1 is a Rhoa.
        if (ActionTypeId(effectRow)?.StartsWith("SummonSpecificMonsters", StringComparison.Ordinal) != true) return null;
        if (Get(Stat.AlternateMinion) is not (> 0 and var minion) || !_summonedMonsters.TryGetValue(minion, out int variety)) return null;
        return new(variety, Math.Max(1, Get(Stat.NumberOfMonstersToSummon)), Get(Stat.SummonSpecificMonstersInFrontOffset),
            Get(Stat.SummonedMonstersNoDropsOrExperience) != 0, CastMs(effectRow) / 1000.0, CooldownMs(effectRow, level) / 1000.0)
        {
            DiesWithParent = Get(Stat.MinionDiesWhenParentDies) != 0,
            DiesWhenParentDeleted = Get(Stat.MinionDiesWhenParentDeleted) != 0,
        };
    }

    /// <summary>A storm skill's cast, from its own stats: Firestorm's <c>base_skill_effect_duration</c> and
    /// <c>fire_storm_fireball_delay_ms</c>, the drop area's <c>active_skill_base_area_of_effect_radius</c> scaled by
    /// <c>firestorm_base_area_of_effect_+%</c>, each fall's explosion radius (<c>ImpactRadius</c>, scaled by
    /// <c>firestorm_explosion_area_of_effect_+%</c> when present), and the fall's own hit, the skill's fire damage. Fire Fury's
    /// (<c>FemaleCannibalBossFireStorm</c>, stat set 1474) is 3000 ms / 100 ms: 30 falls 0.1 s apart, each in a
    /// 5-cell drop area.</summary>
    public sealed record StormInfo(int DurationMs, int DelayMs, double Radius, DamagePacket Damage)
    {
        /// <summary>Each individual fall's explosion radius in cells. Kept separate from <see cref="Radius"/> (the drop
        /// scatter zone) so large-spread storms like Gravicius's (+600% storm area) do not inflate each fireball's blast
        /// to the whole arena, and zero-spread storms (-100% storm area) keep their base blast radius.</summary>
        public double ImpactRadius { get; init; }
        /// <summary>The falls one cast brings: the duration over the delay (30 for Fire Fury's 3000 ms / 100 ms).</summary>
        public int Falls => Math.Clamp(DurationMs / DelayMs, 1, MaxFalls);
        public double IntervalSeconds => DelayMs / 1000.0;
        /// <summary>The most falls one cast schedules: a guard on a mis-typed row, above the longest fire storm the
        /// tables ship (FireWitchFirestorm and GladiatorBossFirestorm, 15000/125 and 12000/100, are 120).</summary>
        public const int MaxFalls = 128;
    }

    /// <summary>A downpour spell's falls for a monster at <paramref name="level"/>, or null for every other skill. The
    /// gate is ActionTypes <c>FireStorm</c> with <c>fire_storm_fireball_delay_ms</c>, a duration, a base radius and fire
    /// damage: the ice and chaos storms (an Icestorm's cold, a Chaosstorm's chaos, all sharing ActiveSkills row 206 with
    /// Firestorm) and non-FireStorm cascades drop nothing extra here.</summary>
    public StormInfo? MonsterStorm(int effectRow, int level)
    {
        if (ActionTypeId(effectRow) != "FireStorm") return null;
        int effectLevel = MonsterEffectLevel(effectRow, level);
        var stats = EffectStats(effectRow, effectLevel);
        int Get(uint key) => stats.FirstOrDefault(s => s.Item1 == key).Item2;
        int delay = Get(Stat.FireStormFireballDelayMs), duration = Get(Stat.BaseSkillEffectDuration);
        if (delay <= 0 || duration <= 0) return null;
        int baseRadius = Get(Stat.ActiveSkillBaseAreaOfEffectRadius);
        if (baseRadius <= 0) return null;
        var damage = HitDamage(effectRow, effectLevel);
        if (damage[DamageType.Fire].Max <= 0) return null;
        // SkillArea.Radius's rule, over the storm's own base area stat: the radius times the square root of its area.
        double area = Math.Max(0, 1 + Get(Stat.FirestormBaseAreaOfEffectPlusPct) / 100.0);
        double dropRadius = baseRadius * Math.Sqrt(area);
        int explosionPct = Get(Stat.FirestormExplosionAreaOfEffectPlusPct);
        double impactRadius = explosionPct != 0
            ? baseRadius * Math.Sqrt(Math.Max(0, 1 + explosionPct / 100.0))
            : dropRadius > 0 ? Math.Min(baseRadius, dropRadius) : baseRadius;
        return new(duration, delay, dropRadius, damage) { ImpactRadius = impactRadius };
    }

    /// <summary>Measured launch frames for monster projectile skills: frames of 33 ms from the start of the action,
    /// measured on official captures, counted from the start of the action 0x1ab. The key is
    /// the skill's GrantedEffects Id; the value is its frame and how many capture events
    /// the frame rests on. The table is embedded here: the Gateway reads no generated JSON data file (only
    /// data/game-files.zip comes from data/ itself), so a file-based load would need a client.db regeneration
    /// before the first use, and a generated-table migration can replace this table when that is scheduled.</summary>
    static readonly FrozenDictionary<string, (int Frames, int Samples)> MonsterLaunchFrameRows =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["EinharSnipe"] = (18, 24),
            ["FireballIncursionChaos"] = (10, 9),
            ["FireballIncusionFire"] = (16, 18),
            ["FireballIncusionLightning"] = (6, 24),
            ["HeraldOfAgonyMinionTailSpike"] = (5, 298),
            ["IncaMinionProjectile"] = (12, 19),
            ["IncursionAlvaProjectileFast"] = (26, 4),
            ["IncursionIncaMinionProjectile"] = (14, 16),
            ["IncursionWraithProjectile"] = (14, 3),
            ["MonsterLesserMultiFireball"] = (11, 12),
            ["MonsterMultiFireball"] = (11, 14),
            ["MonsterSpark"] = (14, 18),
            ["MonsterSplitArrow"] = (0, 10),
            ["MonsterSplitFireball"] = (11, 16),
            ["ScavengerThrow"] = (11, 14),
            ["SkeletonSpark"] = (1, 5),
            ["SnakeProjectileSlow"] = (15, 17),
        }.ToFrozenDictionary();

    /// <summary>A monster skill's measured launch frame, or null for every other skill, which keeps its clip contact.
    /// Read once when the attack is built (Monsters), never per hit.</summary>
    public int? LaunchFrames(string? effectsId) =>
        effectsId is { Length: > 0 } id && MonsterLaunchFrameRows.TryGetValue(id, out var row) ? row.Frames : null;
    private readonly Dictionary<string, int> _monsterVarietyRowsById;

    static Dictionary<string, int> LoadMonsterVarietyRowsById()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!ClientTables.Has("MonsterVarieties")) return result;
        int row = 0;
        foreach (var r in ClientTables.Table("MonsterVarieties"))
        {
            if (r.TextOrNull("Id") is { Length: > 0 } id) result.TryAdd(id, row);
            row++;
        }
        return result;
    }

    // SummonedSpecificMonsters: Id → MonsterVarietiesKey.
    private readonly Dictionary<int, int> _summonedMonsters;

    Dictionary<int, int> LoadSummonedMonsters()
    {
        var result = new Dictionary<int, int>();
        if (!ClientTables.Has("SummonedSpecificMonsters")) return result;
        var doc = ClientTables.Table("SummonedSpecificMonsters");
        foreach (var r in doc)
            if (r.IntOrNull("Id") is int id && r.IntOrNull("MonsterVarietiesKey") is int v)
                result.TryAdd(id, v);
        return result;
    }

    private readonly Dictionary<int, MonsterProjectileInfo> _monsterProjectiles, _weaponMonsterProjectiles;

    Dictionary<int, MonsterProjectileInfo> BuildMonsterProjectiles(bool includeWeaponAttacks)
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
        var spellRows = new Dictionary<int, (int Projectile, string? AnimationKey)>();
        if (ClientTables.TableOrNull("MonsterProjectileSpell") is { } spells)
            foreach (var r in spells)
                if (r.IntOrNull("Id") is int id && r.IntOrNull("Projectile") is int p)
                {
                    string? key = r.IntOrNull("Animation") is int a
                        && a >= 0 && a < animationIds.Count && animationIds[a].Length > 0
                        ? animationIds[a] : null;
                    spellRows.TryAdd(id, (p, key));
                }
        var active = ClientTables.Table("ActiveSkills");
        var mortarSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") is { } name
                && name.StartsWith("monster_mortar", StringComparison.Ordinal))
            .ToDictionary(a => a.i, a => a.r.Text("Id") == "monster_mortar_attack");
        var spellProjectileSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") == "monster_projectile_spell")
            .Select(a => a.i).ToHashSet();
        var rollingMagmaSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") == "rolling_magma")
            .Select(a => a.i).ToHashSet();
        int magmaOrbRow = rows.FindIndex(p => p.Id == "Metadata/Projectiles/MagmaOrbShort");
        var merveilIceSpearSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") == "merveil_ice_spear")
            .Select(a => a.i).ToHashSet();
        int iceSpearRow = rows.FindIndex(p => p.Id == "Metadata/Projectiles/IceSpear");
        var spectralThrowSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") == "spectral_throw")
            .Select(a => a.i).ToHashSet();
        int fairgravesThrowRow = rows.FindIndex(p => p.Id == "Metadata/Projectiles/SpectralThrowFairgraves");
        var equipmentProjectileSkills = active.Select((r, i) => (r, i))
            .Where(a => a.r.TextOrNull("Id") == "monster_projectile_weapon")
            .Select(a => a.i).ToHashSet();
        var effects = ClientTables.Table("GrantedEffects");
        uint? variationKey = Stat.MonsterProjectileVariation;
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
            else if (spellProjectileSkills.Contains(activeRow))
            {
                // MonsterProjectileSpell rows index by monster_projectile_variation, defaulting to 0 (e.g. HeiTiki).
                // Keep Variation 0 so the server publishes the projectile object instead of expecting a client mortar.
                int variationRow = variationKey is uint key
                    ? EffectStats(row, 1).Where(s => s.Item1 == key).Select(s => (int?)s.Item2).FirstOrDefault() ?? 0
                    : 0;
                if (!spellRows.TryGetValue(variationRow, out var spell)) continue;
                (projectileRow, animationKey, lobbed, variation) = (spell.Projectile, spell.AnimationKey ?? "SpellProjectile", false, 0);
            }
            else if (rollingMagmaSkills.Contains(activeRow) && magmaOrbRow >= 0)
            {
                // Rolling Magma (Flame Mother): Frida-verified MagmaOrbShort lobbed projectile, server-published.
                (projectileRow, animationKey, lobbed, variation) = (magmaOrbRow, "SpellProjectile", true, 0);
            }
            else if (merveilIceSpearSkills.Contains(activeRow) && iceSpearRow >= 0)
            {
                (projectileRow, animationKey, lobbed, variation) = (iceSpearRow, "SpellProjectile", false, 0);
            }
            else if (spectralThrowSkills.Contains(activeRow) && fairgravesThrowRow >= 0
                && e.TextOrNull("Id") == "FairgravesThrownWeapon")
            {
                (projectileRow, animationKey, lobbed, variation) = (fairgravesThrowRow, "Attack", false, 0);
            }
            else if (includeWeaponAttacks && equipmentProjectileSkills.Contains(activeRow) && variationKey is uint key)
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
        _tables is { } t && effectRow >= 0 && effectRow < t.Effects.Count ? t.Effects[effectRow].CastTime : 0;

    public GrantedSkill? Find(ushort id, string effect, int level, IReadOnlyList<(uint, int)>? stats = null)
    {
        if (_tables is not { } t)
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
        if (_tables is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count || t.StatSets[effectRow] < 0) return null;
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
        /// <summary>Raw item-local and weapon-scoped modifiers captured with this weapon, including penetration and impale chance.</summary>
        public IReadOnlyDictionary<uint, int>? LocalSources { get; init; }
        public string? ItemClass { get; init; }
        public static readonly Unarmed Witch = new(833, 2, 5, 8);

        /// <summary>The weapon's own added elemental/chaos damage (its local added damage mods, ItemWeapons), added to
        /// every hit it makes; empty for unarmed and for weapons without such mods.</summary>
        public DamagePacket LocalAdded { get; init; } = DamagePacket.Empty;
        public bool OffHand { get; init; }
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
                (Stat.OffHandMinimumPhysicalDamage, min), (Stat.OffHandMaximumPhysicalDamage, max),
                (Stat.OffHandMinimumTotalDamage, totalHit is null ? min : (int)Math.Floor(totalHit.MinTotal)),
                (Stat.OffHandMaximumTotalDamage, totalHit is null ? max : (int)Math.Floor(totalHit.MaxTotal)),
                (Stat.OffHandBaseWeaponAttackDurationMs, AttackMs), (Stat.OffHandTotalBaseWeaponAttackDurationMs, AttackMs),
                (Stat.OffHandAttackSpeedPlusPct, OffHandAttackSpeedPct(IncreasedAttackSpeed(player), mainHandLocalAttackSpeedPct)),
                (Stat.OffHandBaseMaximumAttackDistance, Range), (Stat.OffHandMaximumAttackDistance, Range),
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
            if (player?.TryGetValue(Stat.MainHandAttackSpeedPlusPct, out int final) == true) return final;
            int increased = DualWieldAttackSpeedPct;
            foreach (uint k in Stat.AttackSpeedInputs)
                increased += player?.GetValueOrDefault(k) ?? 0;
            return increased;
        }

        /// <summary>The attack's total increased physical damage: the player's damage, attack, physical, melee
        /// and melee physical damage stats.</summary>
        public long IncreasedDamage(IReadOnlyDictionary<uint, int>? player)
        {
            long increased = 0;
            foreach (uint k in (uint[])[Stat.DamagePlusPct, Stat.AttackDamagePlusPct, Stat.PhysicalDamagePlusPct])
                increased += player?.GetValueOrDefault(k) ?? 0;
            if (Melee) increased += (long)(player?.GetValueOrDefault(Stat.MeleeDamagePlusPct) ?? 0)
                + (player?.GetValueOrDefault(Stat.MeleePhysicalDamagePlusPct) ?? 0);
            return increased;
        }

        /// <summary>Local typed weapon damage (or class physical damage), with the actual attack's increase buckets.</summary>
        public DamagePacket HitDamage(IReadOnlyDictionary<uint, int>? player, bool melee = true, bool projectile = false)
        {
            // Arcane Might's wand-only grant follows the weapon held (WieldingWand); every local component takes the
            // transfer once, with the weapon's own melee context.
            int Get(uint key) => player?.GetValueOrDefault(key) ?? 0;
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
                (Stat.MainHandMinimumPhysicalDamage, min), (Stat.MainHandMaximumPhysicalDamage, max),
                (Stat.MainHandMinimumTotalDamage, totalHit is null ? min : (int)Math.Floor(totalHit.MinTotal)),
                (Stat.MainHandMaximumTotalDamage, totalHit is null ? max : (int)Math.Floor(totalHit.MaxTotal)),
                (Stat.MainHandBaseWeaponAttackDurationMs, AttackMs), (Stat.MainHandTotalBaseWeaponAttackDurationMs, AttackMs),
                (Stat.MainHandAttackSpeedPlusPct, IncreasedAttackSpeed(player)),
                (Stat.SkillUsesMainHand, 1),
                (Stat.HundredTimesAttacksPerSecond, (int)Math.Round(100 * attacksPerSecond)),
                // The character panel's DPS and Damage per Use rows read these; the client derives neither.
                (Stat.HundredTimesDamagePerSecond, display.PerSecond),
                (Stat.DisplayHundredTimesDamagePerSkillUse, display.PerUse),
                (Stat.MainHandBaseMaximumAttackDistance, Range), (Stat.MainHandMaximumAttackDistance, Range),
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
    // ManaCost: per GEPL row the CostAmounts entry of CostTypes 0 (mana), else 0; LifeCost of CostTypes 1, EsCost of
    // CostTypes 2, ManaPercentCost of CostTypes 4, ManaPerMinute of CostTypes 7, ManaPercentPerMinute of CostTypes 9.
    // Reservations: per GEPL row the Mana/LifeReservationFlat and -Percent (permyriad), parallel to PerLevel.
    // Cooldowns: per GEPL row its Cooldown (ms), StoredUses, CooldownBypassType and Group, parallel to PerLevel. VaalSouls: per GEPL row its VaalSouls.
    // Quality: per GrantedEffects row its (quality stat, permille).
    private sealed record Tables(Dictionary<string, (int Row, int StatSet)> EffectRows, List<int> StatSets,
        List<(int Effect, int Level)> PerLevel, List<(int StatSet, int Level)> StatSetsPerLevel,
        List<(int StatSet, int CastTime, bool CooldownPauses)> Effects, List<(int[] Implicit, (int, int)[] Constant)> StatSetBase,
        List<(int, int)[]> StatSetLevelStats, List<(int StatSet, int GemLevel, int PlayerLevelReq)> GemLevelReqs, List<int> ManaCost, List<int> LifeCost,
        List<(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)> Reservations, List<(int Ms, int Uses, int Bypass, int Group)> Cooldowns,
        List<int> VaalSouls, List<int> VaalStoredUses, List<int?> ActiveSkills, List<int> SoulPrevention, List<int> RageCost,
        Dictionary<int, (int, int)[]> Quality, List<int> Effectiveness, List<(int? Spell, int? Attack)> CriticalChances, List<int> AttackSpeedMultipliers,
        List<int> ManaPerMinute, List<int> ManaPercentPerMinute, List<(int Base, int Added)> DamageCoefficients,
        List<int> EsCost, List<int> ManaPercentCost)
    {
        public FrozenDictionary<int, string> EffectIds { get; } = EffectRows
            .GroupBy(p => p.Value.Row).ToFrozenDictionary(g => g.Key, g => g.First().Key);

        // GemLevelReqs by stat set: every gem's requirement check (twice per linked gem per skill build) scanned the whole
        // table before.
        readonly Dictionary<int, (int GemLevel, int PlayerLevelReq)[]> _levelReqsBySet = GemLevelReqs.GroupBy(r => r.StatSet)
            .ToDictionary(g => g.Key, g => g.Select(r => (r.GemLevel, r.PlayerLevelReq)).ToArray());
        public (int GemLevel, int PlayerLevelReq)[] LevelReqsOf(int statSet) => _levelReqsBySet.GetValueOrDefault(statSet, []);

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
            var es = new List<int>();
            var manaPercent = new List<int>();
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
                int esIndex = Array.IndexOf(types, 2);
                es.Add(esIndex >= 0 && esIndex < amounts.Length ? amounts[esIndex] : 0);
                int percentIndex = Array.IndexOf(types, 4);
                manaPercent.Add(percentIndex >= 0 && percentIndex < amounts.Length ? amounts[percentIndex] : 0);
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
            var damageCoefficients = new List<(int Base, int Added)>();
            var criticalChances = new List<(int?, int?)>();
            var levelStats = new List<(int, int)[]>();
            var levelReqs = new List<(int, int, int)>();
            foreach (var r in s)
            {
                setLevels.Add((Int(r, "StatSet"), Int(r, "GemLevel")));
                // PoB Export/Scripts/skills.lua: raw / 10000 + 1. Zero is 100%, negatives are real reductions.
                effectiveness.Add(r.IntOrNull("DamageEffectiveness") is int offset
                    ? (int)Math.Clamp(10_000L + offset, 0, int.MaxValue) : 0);
                damageCoefficients.Add((Coefficient(r.IntOrNull("BaseMultiplier")), Coefficient(r.IntOrNull("DamageEffectiveness"))));
                int spellCrit = Int(r, "SpellCritChance"), attackCrit = Int(r, "AttackCritChance");
                criticalChances.Add((spellCrit >= 0 ? spellCrit : null, attackCrit >= 0 ? attackCrit : null));
                levelStats.Add([.. Pairs(Ints(r, "FloatStats"), Ints(r, "BaseResolvedValues")),
                    .. Pairs(Ints(r, "AdditionalStats"), Ints(r, "AdditionalStatsValues"))]);
                if (Int(r, "PlayerLevelReq") is >= 0 and var req) levelReqs.Add((Int(r, "StatSet"), Int(r, "GemLevel"), req));
            }
            return new Tables(effects, statSets, perLevel, setLevels, effectInfo, baseStats, levelStats, levelReqs, mana, life, reservations, cooldowns,
                vaalSouls, vaalStoredUses, activeSkills, soulPrevention, rageCost, quality, effectiveness, criticalChances, attackSpeedMultipliers, manaPerMinute,
                manaPercentPerMinute, damageCoefficients, es, manaPercent);

            static int Coefficient(int? offset) => offset is int value
                ? (int)Math.Clamp(10_000L + value, 0, int.MaxValue) : 10_000;
        }
    }
}
