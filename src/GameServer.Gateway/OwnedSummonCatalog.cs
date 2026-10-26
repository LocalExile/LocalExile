using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>Data for Raise Zombie and the Phrecia Raise Spiders grant. This does not create an actor, consume a corpse,
/// select an AI schedule or establish a wire owner. In particular, Flesh Puppets modifies a gem; it grants no skill.</summary>
sealed class OwnedSummonCatalog(string folder = "data")
{
    public enum Kind { Zombie, Spider }
    public enum ActorLevelSource { SkillStat, GrantedEffectLevelRequirement }
    public sealed record Grant(int PerLevelRow, int Effect, string EffectId, int Level);
    public sealed record GemVariant(int Index, int GemEffectsRow, int Effect, string EffectId);
    public sealed record Source(ushort Node, IReadOnlyDictionary<uint, int> Stats, Grant? GrantedSkill,
        uint? GemLevelStat, IReadOnlyList<GemVariant> ModifiedGems);
    public sealed record SkillLevel(int PerLevelRow, int Level, int ActorLevelRequirement, int CooldownMs);
    public sealed record MonsterSkill(int Effect, string EffectId, IReadOnlyList<SkillLevel> Levels,
        IReadOnlyDictionary<uint, int> LevelOneStats);
    public sealed record OwnerBuff(ushort Definition, string Id, IReadOnlyDictionary<uint, int> PerMinionStats,
        bool IsRecovery, bool ExtraU16, bool ShowCount, int StackMode);
    public sealed record Summon(Kind Type, int Effect, string EffectId, int SkillLevel, int Quality,
        byte ActorLevel, ActorLevelSource LevelSource, int AlternateGemIndex, MonsterCatalog.Variety Monster,
        string AiFile, IReadOnlyDictionary<uint, int> SkillStats, IReadOnlyDictionary<uint, int> ModStats,
        IReadOnlyDictionary<uint, int> TemplateStats, IReadOnlyList<MonsterSkill> DeclaredSkills,
        uint LimitInputStat, uint LimitStat, uint? CountStat, int BaseLimit, int? DurationMs, int CooldownMs,
        bool ManuallyCastable, bool RequiresCorpse, bool ConsumesNearbyCorpses, int? MinionsPerCorpse,
        bool DiesOnImpact, int TriggerChanceOnKill, OwnerBuff? Buff)
    {
        // No inference from a wolf's refresh policy, the unknown MinionType columns, or a client description.
        public bool HasKnownAtLimitPolicy => false;
        public bool HasKnownAiSchedule => false;
        public bool HasKnownWireOwner => false;
        public bool ActorLevelIsInferred => LevelSource == ActorLevelSource.GrantedEffectLevelRequirement;
        /// <summary>TemplateStats are pinned OT literals, not fields decoded by object-types.json. That manifest only
        /// supplies the component order. Keep this provenance separate from the loaded table/mod/skill dictionaries.</summary>
        public string TemplateStatsSource => Type == Kind.Spider ? "Metadata/Monsters/SummonedSpider/SummonedSpider.ot"
            : "Metadata/Monsters/RaisedZombies/RaisedZombieBase.ot";
    }

    const string SpiderVariety = "Metadata/Monsters/SummonedSpider/SummonedSpider";
    readonly SkillCatalog skills = new(folder);
    readonly StatCatalog stats = new(folder);
    readonly MonsterCatalog monsters = new(folder);
    readonly Lazy<(Metadata? Data, string? Error)> metadata = new(() => Load(folder));
    readonly ConcurrentDictionary<(int Effect, int Level, int Quality), Lazy<Summon?>> resolved = new();

    public string? Error => metadata.Value.Error;
    public Source? SourceForNode(ushort node) => metadata.Value.Data?.Sources.GetValueOrDefault(node);

    /// <summary>The node-granted skill only. Flesh Puppets intentionally returns null here.</summary>
    public Summon? ForNode(ushort node) => SourceForNode(node)?.GrantedSkill is { } grant
        ? ForEffect(grant.Effect, grant.Level) : null;

    /// <summary>Resolve an already effective gem level/quality. The caller applies gem modifiers without mutating items.</summary>
    public Summon? ForEffect(int effect, int level, int quality = 0) => resolved.GetOrAdd((effect, level, quality),
        key => new(() => Resolve(key.Effect, key.Level, key.Quality))).Value;

    public int GemLevelBonus(int effect, Func<string, int> owner) => SourceForNode(18817) is { GemLevelStat: uint key } source
        && source.ModifiedGems.Any(g => g.Effect == effect) ? owner(stats.Name(key)) : 0;

    /// <summary>Raw variety life, including the skill's explicit more-minion-life modifier and the spider OT's one-life
    /// override. Owner increased life is a separate multiplier, as in the existing wolf profile.</summary>
    public int? LifeWithoutOwner(Summon summon)
    {
        if (Get(summon.TemplateStats, "maximum_life_is_one") != 0) return 1;
        return monsters.BaseLife(summon.Monster, summon.ActorLevel) is int life
            ? Math.Max(1, (int)(life * Math.Max(0L, 100L + Get(summon.SkillStats, "active_skill_minion_life_+%_final")) / 100)) : null;
    }

    /// <summary>The variety's unmodified physical attack range, before its attack skill and minion/owner modifiers.</summary>
    public (int Min, int Max)? BaseDamage(Summon summon) => monsters.BaseDamage(summon.Monster, summon.ActorLevel);

    int Get(IReadOnlyDictionary<uint, int> values, string name) => stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;

    Summon? Resolve(int effect, int level, int quality)
    {
        if (metadata.Value.Data is not { } data || !data.Effects.TryGetValue(effect, out var info)) return null;
        bool zombie = data.Sources.GetValueOrDefault((ushort)18817)?.ModifiedGems.Any(g => g.Effect == effect) == true;
        bool spider = info.Id == "RaiseSpiders" && data.Sources.GetValueOrDefault((ushort)22622)?.GrantedSkill?.Effect == effect;
        if (!zombie && !spider) return null;
        var own = skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Own(string name) => Get(own, name);
        if (PickLevel(info.Levels, level) is not { } perLevel) return null;
        int actorLevel = Own("base_display_minion_actor_level");
        var levelSource = ActorLevelSource.SkillStat;
        if (actorLevel <= 0)
        {
            // PoB's pinned CalcActiveSkill.lua 717-719 uses this fallback when no explicit minion level exists.
            // Fang's level-1 skill requires 75; Phrecia grants level 20, whose requirement is 84, not 75.
            actorLevel = perLevel.ActorLevelRequirement;
            levelSource = ActorLevelSource.GrantedEffectLevelRequirement;
        }
        if (actorLevel is < 1 or > 100) return null;
        string? varietyId;
        if (zombie)
            varietyId = data.DisplayVarieties.GetValueOrDefault(Own("display_minion_monster_type"));
        else varietyId = SpiderVariety; // No display-minion foreign key exists on RaiseSpiders.
        if (varietyId is null || !data.Varieties.TryGetValue(varietyId, out var variety)
            || monsters.Find(varietyId) is not { } monster || monster.Id != varietyId || !monster.Supported) return null;
        string typeId = zombie ? "ZombieMinion" : "SpiderMinion";
        string limitInput = zombie ? "base_number_of_zombies_allowed" : "number_of_spider_minions_allowed";
        if (!data.MinionTypes.TryGetValue(typeId, out var minionType) || stats.Key(limitInput) is not uint input
            || own.GetValueOrDefault(input) <= 0) return null;
        bool infinite = Own("infinite_minion_duration") != 0;
        int duration = Own("base_skill_effect_duration");
        if (!infinite && duration <= 0) return null;
        var declared = variety.Effects.Select(e => data.Effects.TryGetValue(e, out var skill)
            ? new MonsterSkill(e, skill.Id, skill.Levels,
                skills.EffectStats(e, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2)) : null).OfType<MonsterSkill>().ToArray();
        if (declared.Length != variety.Effects.Length) return null;
        OwnerBuff? buff = null;
        if (spider)
        {
            if (data.SpiderBuff is not { } definition || definition.IsRecovery || definition.ExtraU16
                || definition.Stats.Length != 2 || stats.Key("attack_speed_+%") is not uint speed
                || stats.Key("base_poison_damage_+%") is not uint poison
                || !definition.Stats.SequenceEqual(new[] { speed, poison })) return null;
            buff = new(definition.Row, definition.Id, new Dictionary<uint, int>
            {
                [speed] = Own("summoned_spider_grants_attack_speed_+%"),
                [poison] = Own("summoned_spider_grants_poison_damage_+%"),
            }, definition.IsRecovery, definition.ExtraU16, definition.ShowCount, definition.StackMode);
        }
        var template = new Dictionary<uint, int>();
        void Template(string name, int value) { if (stats.Key(name) is uint key) template[key] = value; }
        // These are literal stats in the pinned object's OT, distinct from the variety's Mods foreign references.
        // They are not guessed PetAi ownership fields. The OT paths are stated in the tests and integration contract.
        Template("item_drop_slots", 0);
        if (zombie) Template("monster_no_drops_or_experience", 1);
        else
        {
            Template("untargetable_by_monster_ai", 1); Template("is_spider", 1);
            Template("base_cannot_be_damaged", 1); Template("cannot_be_chained_from", 1);
            Template("maximum_life_is_one", 1);
        }
        // ActiveSkills[RaiseSpiders].Description explicitly says "Raise two spectral spiders from each nearby corpse".
        // Two is a pinned source rule, not the node's display chance (200), a damage multiplier, or a guessed AI timer.
        return new(zombie ? Kind.Zombie : Kind.Spider, effect, info.Id, perLevel.Level, quality, (byte)actorLevel,
            levelSource, Own("active_skill_minion_from_alternate_gem_index"), monster, variety.AiFile, own,
            variety.Mods, template, declared, input, minionType.Limit, minionType.Count, own[input],
            infinite ? null : duration, perLevel.CooldownMs, info.Manual,
            zombie && Own("raise_zombie_does_not_use_corpses") == 0, spider, spider ? 2 : null,
            zombie && info.Id == "RaiseZombieAltY", Own("chance_to_cast_on_kill_%_target_self"), buff);
    }

    static SkillLevel? PickLevel(IReadOnlyList<SkillLevel> levels, int level) =>
        levels.LastOrDefault(l => l.Level <= level) ?? levels.FirstOrDefault();

    sealed record Effect(string Id, bool Manual, SkillLevel[] Levels);
    sealed record Variety(string AiFile, int[] Effects, IReadOnlyDictionary<uint, int> Mods);
    sealed record MinionType(uint Limit, uint? Count);
    sealed record Buff(ushort Row, string Id, uint[] Stats, bool IsRecovery, bool ExtraU16, bool ShowCount, int StackMode);
    sealed record Metadata(Dictionary<ushort, Source> Sources, Dictionary<int, Effect> Effects,
        Dictionary<int, string> DisplayVarieties, Dictionary<string, Variety> Varieties,
        Dictionary<string, MinionType> MinionTypes, Buff? SpiderBuff);

    static (Metadata? Data, string? Error) Load(string folder)
    {
        string[] files = ["PassiveSkills", "Stats", "SkillGems", "GemEffects", "GrantedEffects", "GrantedEffectsPerLevel",
            "GrantedEffectStatSets", "GrantedEffectStatSetsPerLevel", "ActiveSkills", "DisplayMinionMonsterType",
            "MonsterVarieties", "MinionType", "Mods", "BuffDefinitions", "MonsterTypes", "DefaultMonsterStats"];
        string? missing = files.Select(f => Path.Combine(folder, "tables", f + ".json"))
            .Append(Path.Combine(folder, "object-types.json")).FirstOrDefault(f => !File.Exists(f));
        if (missing is not null) return (null, $"Owned summon data missing: {missing}");
        var docs = new Dictionary<string, JsonDocument>();
        try
        {
            foreach (string file in files) docs[file] = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables", file + ".json")));
            JsonElement Table(string name) => docs[name].RootElement;
            int statCount = Table("Stats").GetArrayLength();
            uint Key(int row)
            {
                if (row < 0 || row >= statCount) throw new InvalidDataException($"Invalid summon stat row {row}");
                return checked((uint)row + 1);
            }
            JsonElement Ref(JsonElement rows, int index)
            {
                if (index < 0 || index >= rows.GetArrayLength()) throw new InvalidDataException($"Invalid summon foreign row {index}");
                return rows[index];
            }
            var levelRows = Table("GrantedEffectsPerLevel").EnumerateArray().Select((r, i) =>
                (Effect: Int(r, "GrantedEffect"), Value: new SkillLevel(i, Int(r, "Level"), Int(r, "PlayerLevelReq"), Math.Max(0, Int(r, "Cooldown"))))).ToArray();
            var levelsByEffect = levelRows.GroupBy(l => l.Effect)
                .ToDictionary(g => g.Key, g => g.Select(l => l.Value).OrderBy(l => l.Level).ToArray());
            var effects = Table("GrantedEffects").EnumerateArray().Select((r, i) =>
                (i, Value: new Effect(Text(r, "Id"), Int(r, "ActiveSkill") is >= 0 and var active
                    && Bool(Ref(Table("ActiveSkills"), active), "IsManuallyCasted"),
                    levelsByEffect.GetValueOrDefault(i) ?? [])))
                .ToDictionary(e => e.i, e => e.Value);
            var sources = new Dictionary<ushort, Source>();
            foreach (var row in Table("PassiveSkills").EnumerateArray())
            {
                int node = Int(row, "PassiveSkillGraphId");
                if (node is not (18817 or 22622)) continue;
                var values = new Dictionary<uint, int>();
                foreach (var (key, index) in Ints(row, "Stats").Select((key, index) => (key, index)))
                    values[Key(key)] = Int(row, $"Stat{index + 1}Value");
                Grant? grant = null;
                if (Int(row, "GrantedEffectsPerLevel") is >= 0 and var grantRow)
                {
                    var granted = Ref(Table("GrantedEffectsPerLevel"), grantRow);
                    int effect = Int(granted, "GrantedEffect");
                    if (!effects.TryGetValue(effect, out var info)) throw new InvalidDataException("Invalid summon grant effect");
                    grant = new(grantRow, effect, info.Id, Int(granted, "Level"));
                }
                uint? gemLevelStat = null;
                var variants = new List<GemVariant>();
                if (node == 18817)
                    foreach (var gem in Table("SkillGems").EnumerateArray())
                    {
                        int levelStat = Int(gem, "MinionGlobalSkillLevelStat");
                        if (levelStat < 0 || !values.ContainsKey(Key(levelStat))) continue;
                        if (gemLevelStat is not null) throw new InvalidDataException("Ambiguous Flesh Puppets gem mapping");
                        gemLevelStat = Key(levelStat);
                        foreach (var (gemRow, index) in Ints(gem, "GemVariants").Select((value, index) => (value, index)))
                        {
                            int effect = Int(Ref(Table("GemEffects"), gemRow), "GrantedEffect");
                            if (!effects.TryGetValue(effect, out var info)) throw new InvalidDataException("Invalid Zombie gem effect");
                            variants.Add(new(index, gemRow, effect, info.Id));
                        }
                    }
                if (node == 18817 && (grant is not null || gemLevelStat is null || variants.Count == 0)
                    || node == 22622 && grant?.EffectId != "RaiseSpiders")
                    throw new InvalidDataException("Unexpected owned summon passive source");
                sources.Add((ushort)node, new((ushort)node, values, grant, gemLevelStat, variants));
            }
            var varieties = new Dictionary<string, Variety>(StringComparer.Ordinal);
            var monsterRows = Table("MonsterVarieties");
            var display = new Dictionary<int, string>();
            foreach (var row in Table("DisplayMinionMonsterType").EnumerateArray())
                display.Add(Int(row, "Id"), Text(Ref(monsterRows, Int(row, "MonsterVarietiesKey")), "Id"));
            var wanted = new HashSet<string>(StringComparer.Ordinal) { SpiderVariety };
            int displayStat = Table("Stats").EnumerateArray().Select((r, i) => (r, i))
                .Where(x => Text(x.r, "Id") == "display_minion_monster_type").Select(x => x.i).DefaultIfEmpty(-1).Single();
            if (sources.GetValueOrDefault((ushort)18817) is { } zombieSource)
                foreach (var variant in zombieSource.ModifiedGems)
                {
                    var effect = Ref(Table("GrantedEffects"), variant.Effect);
                    var set = Ref(Table("GrantedEffectStatSets"), Int(effect, "StatSet1"));
                    foreach (var pair in Ints(set, "ConstantStats").Zip(Ints(set, "ConstantStatsValues")))
                        if (pair.First == displayStat && display.TryGetValue(pair.Second, out string? current)) wanted.Add(current);
                }
            foreach (var row in monsterRows.EnumerateArray())
            {
                string id = Text(row, "Id");
                if (!wanted.Contains(id)) continue;
                var values = new Dictionary<uint, int>();
                foreach (int modRow in Ints(row, "ModsKeys"))
                {
                    var mod = Ref(Table("Mods"), modRow);
                    for (int i = 1; i <= 8; i++)
                        if (Int(mod, $"StatsKey{i}") is >= 0 and var key)
                        {
                            int min = Int(mod, $"Stat{i}Min"), max = Int(mod, $"Stat{i}Max");
                            if (min != max) throw new InvalidDataException("Variable summon intrinsic mod is not a fixed stat");
                            values[Key(key)] = values.GetValueOrDefault(Key(key)) + min;
                        }
                }
                varieties.Add(id, new(Text(row, "AISFile"), Ints(row, "GrantedEffectsKeys"), values));
            }
            var types = new Dictionary<string, MinionType>(StringComparer.Ordinal);
            foreach (var row in Table("MinionType").EnumerateArray())
                if (Text(row, "Id") is "ZombieMinion" or "SpiderMinion" && Int(row, "LimitStat") is >= 0 and var limit)
                    types.Add(Text(row, "Id"), new(Key(limit), Int(row, "ActiveCountStat") is >= 0 and var count ? Key(count) : null));
            Buff? buff = null;
            foreach (var (row, index) in Table("BuffDefinitions").EnumerateArray().Select((row, index) => (row, index)))
                if (Text(row, "Id") == "summoned_spider_buff")
                    buff = new(checked((ushort)index), Text(row, "Id"), [.. Ints(row, "StatsKeys").Select(Key)],
                        Bool(row, "IsRecovery"), Bool(row, "__col44"), Bool(row, "ShowCount"), Int(row, "UIStackMode"));
            return (new(sources, effects, display, varieties, types, buff), null);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ArgumentException or OverflowException)
        { return (null, $"Owned summon data invalid: {e.Message}"); }
        finally { foreach (var document in docs.Values) document.Dispose(); }
    }

    static int Int(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;
    static string Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    static bool Bool(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    static int[] Ints(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? [.. value.EnumerateArray().Select(v => v.GetInt32())] : [];
}
