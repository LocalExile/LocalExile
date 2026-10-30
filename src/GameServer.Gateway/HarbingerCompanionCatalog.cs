using System.Text.Json;
using System.Collections.Concurrent;

/// <summary>The three Phrecia Harbingers' source, current variety and buff data. This resolves metadata, not an AI
/// schedule: the server-only AIS files are absent from the client. Focus's channel condition is stated by its skill
/// description; cooldown suspension and the minion's own recovery modifier are preserved separately.</summary>
sealed class HarbingerCompanionCatalog(string folder = "data")
{
    public enum Kind { Arcane, Time, Focus }

    public sealed record BuffSkill(int Effect, string EffectId, int Level, int CastMs, int DurationMs, int CooldownMs,
        bool CooldownNotRecoverDuringActive, ushort Definition, string BuffId, int BindingRow,
        IReadOnlyDictionary<uint, int> SkillStats, IReadOnlyDictionary<uint, int> RecipientStats);

    public sealed record Companion(ushort Node, Kind Type, int SummonEffect, int SummonLevel, int AlternateMinion,
        uint LimitStat, int Limit, MonsterCatalog.Variety Monster, int WarpEffect, bool RequiresChannelling,
        IReadOnlyDictionary<uint, int> MinionStats, IReadOnlyList<BuffSkill> Buffs)
    {
        public uint? CountStat { get; init; }
    }

    sealed record Source(ushort Node, Kind Type, string Effect, int AlternateMinion, string Variety, string MinionType,
        string[] BuffEffects);
    static readonly Source[] Sources =
    [
        new(20105, Kind.Arcane, "SummonHarbingerOfTheArcaneAscendancy", 12,
            "Metadata/Monsters/Harbinger/HarbingerMinion13", "HarbingerAscendancyArcane",
            ["HarbingerArcaneSurgeUber", "HarbingerArcaneSurgeUber2"]),
        new(60867, Kind.Time, "SummonHarbingerOfTimeAscendancy", 13,
            "Metadata/Monsters/Harbinger/HarbingerMinion14", "HarbingerAscendancyTime", ["HarbingerActionSpeedAuraUber"]),
        new(18859, Kind.Focus, "SummonHarbingerOfFocusAscendancy", 14,
            "Metadata/Monsters/Harbinger/HarbingerMinion15_", "HarbingerAscendancyFocus", ["HarbingerChannelBuffUber"]),
    ];
    static readonly IReadOnlyDictionary<string, string> BuffIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["HarbingerArcaneSurgeUber"] = "arcane_surge",
        ["HarbingerArcaneSurgeUber2"] = "harbinger_lightning_buff_uber",
        ["HarbingerActionSpeedAuraUber"] = "harbinger_action_speed_aura_uber",
        ["HarbingerChannelBuffUber"] = "harbinger_channel_buff_uber",
    };

    readonly SkillCatalog _skills = new(folder);
    readonly MonsterCatalog _monsters = new(folder);
    readonly StatCatalog _stats = new(folder);
    readonly Lazy<(Metadata? Data, string? Error)> _metadata = new(() => Load(folder));
    readonly ConcurrentDictionary<ushort, Lazy<Companion?>> resolved = new();

    public string? Error => _metadata.Value.Error;

    public IEnumerable<Companion> ForNodes(IEnumerable<ushort> nodes) => nodes.Distinct().Select(ForNode).OfType<Companion>();

    public IEnumerable<Companion> All => ForNodes(Sources.Select(s => s.Node));
    public Companion? ForNode(ushort node) => resolved.GetOrAdd(node, id => new(() => ResolveNode(id))).Value;

    Companion? ResolveNode(ushort node)
    {
        if (Sources.FirstOrDefault(s => s.Node == node) is not { } source || _metadata.Value.Data is not { } data
            || !data.Grants.TryGetValue(node, out var grant) || _skills.EffectId(grant.Effect) != source.Effect)
            return null;
        var summon = _skills.EffectStats(grant.Effect, grant.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        if (_stats.Key("alternate_minion") is not uint alternate || summon.GetValueOrDefault(alternate) != source.AlternateMinion
            || !data.LimitStats.TryGetValue(source.MinionType, out uint limitStat) || summon.GetValueOrDefault(limitStat) <= 0)
            return null;
        var monster = _monsters.Find(source.Variety);
        if (monster is null || monster.Id != source.Variety || !monster.Supported
            || !data.MinionStats.TryGetValue(source.Variety, out var minionStats)) return null;
        int warp = _skills.EffectRow("HarbingerWarp");
        if (warp < 0 || monster.GrantedEffects is not { } granted || !granted.Contains(warp)
            || _skills.FindByRow(SkillCatalog.FirstSkillId, warp, 1) is null) return null;
        var buffs = new List<BuffSkill>();
        foreach (string effectId in source.BuffEffects)
        {
            int effect = _skills.EffectRow(effectId);
            // Monster buff effects have their own level-1 rows, independent of the player's summon skill level.
            const int level = 1;
            if (effect < 0 || !granted.Contains(effect) || !data.Effects.TryGetValue(effect, out var info)
                || !data.Bindings.TryGetValue(info.SkillId, out var binding) || binding.Definition.Id != BuffIds[effectId]) return null;
            var own = _skills.EffectStats(effect, level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            int duration = _stats.Key("base_skill_effect_duration") is uint durationKey ? own.GetValueOrDefault(durationKey) : 0;
            int cooldown = _skills.CooldownMs(effect, level);
            if (duration <= 0 || cooldown <= 0 || _skills.FindByRow(SkillCatalog.FirstSkillId, effect, level) is null)
                return null;
            if (binding.Definition.Category != 1 || binding.Definition.IsRecovery || binding.Definition.ExtraU16
                || binding.FixedValues.Length + binding.SourceStats.Length > binding.Definition.Stats.Length) return null;
            // AddBuffToTargetVarieties supplies a constant prefix, then source-stat values; BuffDefinitions supplies
            // the recipient keys. Example: Focus's [1,1,1] precedes -20 final damage taken; Arcane's support_* inputs
            // become arcane_surge_cast_speed and mana_regeneration_rate, not the input stat names.
            var recipient = new Dictionary<uint, int>();
            for (int i = 0; i < binding.Definition.Stats.Length; i++)
            {
                int sourceIndex = i - binding.FixedValues.Length;
                recipient[binding.Definition.Stats[i]] = i < binding.FixedValues.Length ? binding.FixedValues[i]
                    : sourceIndex < binding.SourceStats.Length ? own.GetValueOrDefault(binding.SourceStats[sourceIndex]) : 0;
            }
            buffs.Add(new(effect, effectId, level, _skills.CastMs(effect), duration, cooldown, info.StopsWhileActive,
                binding.Definition.Row, binding.Definition.Id, binding.Row, own, recipient));
        }
        return new(node, source.Type, grant.Effect, grant.Level, source.AlternateMinion, limitStat, summon[limitStat],
            monster, warp, source.Type == Kind.Focus, minionStats, buffs)
            { CountStat = data.CountStats.TryGetValue(source.MinionType, out uint countStat) ? countStat : null };
    }

    sealed record Definition(ushort Row, string Id, uint[] Stats, bool IsRecovery, bool ExtraU16, int Category);
    sealed record Binding(int Row, Definition Definition, int[] FixedValues, uint[] SourceStats);
    sealed record Metadata(Dictionary<ushort, (int Effect, int Level)> Grants,
        Dictionary<int, (int SkillId, bool StopsWhileActive)> Effects, Dictionary<int, Binding> Bindings,
        Dictionary<string, uint> LimitStats, Dictionary<string, uint> CountStats,
        Dictionary<string, IReadOnlyDictionary<uint, int>> MinionStats);

    static (Metadata? Data, string? Error) Load(string folder)
    {
        string[] files = ["PassiveSkills", "GrantedEffects", "GrantedEffectsPerLevel", "GrantedEffectStatSets",
            "GrantedEffectStatSetsPerLevel", "Stats", "AddBuffToTargetVarieties", "BuffDefinitions", "MinionType",
            "MonsterVarieties", "Mods"];
        string? missing = files.Select(f => Path.Combine(folder, "tables", f + ".json")).Append(Path.Combine(folder, "object-types.json"))
            .FirstOrDefault(f => !File.Exists(f));
        if (missing is not null) return (null, $"Harbinger companion data missing: {missing}");
        var docs = new Dictionary<string, JsonDocument>();
        try
        {
            foreach (string file in files) docs[file] = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables", file + ".json")));
            var perLevel = docs["GrantedEffectsPerLevel"].RootElement;
            var grants = new Dictionary<ushort, (int, int)>();
            foreach (var row in docs["PassiveSkills"].RootElement.EnumerateArray())
                if (Int(row, "PassiveSkillGraphId") is > 0 and <= ushort.MaxValue and var node && Sources.Any(s => s.Node == node)
                    && Int(row, "GrantedEffectsPerLevel") is >= 0 and var index && index < perLevel.GetArrayLength())
                {
                    var grant = perLevel[index];
                    grants[(ushort)node] = (Int(grant, "GrantedEffect"), Int(grant, "Level"));
                }
            var effects = docs["GrantedEffects"].RootElement.EnumerateArray().Select((r, i) => (r, i))
                .ToDictionary(x => x.i, x => (Int(x.r, "SkillID"), Bool(x.r, "CooldownNotRecoverDuringActive")));
            var definitions = docs["BuffDefinitions"].RootElement.EnumerateArray().Select((r, i) => new Definition(
                checked((ushort)i), r.GetProperty("Id").GetString() ?? "", Keys(r, "StatsKeys"), Bool(r, "IsRecovery"), Bool(r, "__col44"), Int(r, "BuffCategory"))).ToArray();
            var bindings = new Dictionary<int, Binding>();
            int bindingRow = 0;
            foreach (var row in docs["AddBuffToTargetVarieties"].RootElement.EnumerateArray())
            {
                int id = Int(row, "__col5"), definition = Int(row, "BuffDefinitions");
                if (id >= 0 && definition >= 0 && definition < definitions.Length
                    && !bindings.TryAdd(id, new(bindingRow, definitions[definition], Ints(row, "__col1"), Keys(row, "StatsKeys"))))
                    return (null, $"Duplicate AddBuffToTargetVarieties key: {id}");
                bindingRow++;
            }
            var limits = new Dictionary<string, uint>(StringComparer.Ordinal);
            var counts = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var row in docs["MinionType"].RootElement.EnumerateArray())
                if (row.GetProperty("Id").GetString() is { } id && Int(row, "LimitStat") is >= 0 and var stat)
                {
                    limits[id] = checked((uint)stat + 1);
                    if (Int(row, "ActiveCountStat") is >= 0 and var count) counts[id] = checked((uint)count + 1);
                }
            var minionStats = new Dictionary<string, IReadOnlyDictionary<uint, int>>(StringComparer.Ordinal);
            var mods = docs["Mods"].RootElement;
            foreach (var row in docs["MonsterVarieties"].RootElement.EnumerateArray())
            {
                string id = row.GetProperty("Id").GetString() ?? "";
                if (!Sources.Any(s => s.Variety == id)) continue;
                var values = new Dictionary<uint, int>();
                foreach (int index in Ints(row, "ModsKeys"))
                {
                    if (index < 0 || index >= mods.GetArrayLength()) return (null, $"Invalid Harbinger mod reference: {index}");
                    var mod = mods[index];
                    for (int i = 1; i <= 8; i++)
                        if (Int(mod, $"StatsKey{i}") is >= 0 and var key)
                        {
                            int min = Int(mod, $"Stat{i}Min"), max = Int(mod, $"Stat{i}Max");
                            if (min != max) return (null, $"Variable Harbinger mod cannot be treated as fixed: {index}");
                            uint clientKey = checked((uint)key + 1);
                            values[clientKey] = values.GetValueOrDefault(clientKey) + min;
                        }
                }
                minionStats[id] = values;
            }
            return (new(grants, effects, bindings, limits, counts, minionStats), null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or OverflowException)
        {
            return (null, $"Invalid Harbinger companion data: {e.Message}");
        }
        finally { foreach (var doc in docs.Values) doc.Dispose(); }
    }

    static int Int(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
    static bool Bool(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static int[] Ints(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
        ? [.. v.EnumerateArray().Select(x => x.GetInt32())] : [];
    static uint[] Keys(JsonElement row, string name) => [.. Ints(row, name).Select(x => checked((uint)x + 1))];
}
