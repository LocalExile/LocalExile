using System.Text.Json;

/// <summary>Executable passive attachments, separate from a node's five ordinary stat columns. In particular,
/// Phrecia grants skills through GEPL references and auras through BuffTemplates; neither is a display-only stat.</summary>
sealed class PassiveEffectCatalog(string folder = "data")
{
    public sealed record Skill(ushort Node, int Effect, int Level, string Name);
    public sealed record Buff(int Template, ushort Definition, string Id, int Radius, bool AffectsOwner,
        IReadOnlyList<(uint Key, int Value)> Stats, IReadOnlyList<int> Values);
    sealed record Attachments(Skill? Skill, IReadOnlyList<Buff> Buffs);
    readonly Lazy<IReadOnlyDictionary<ushort, Attachments>> entries = new(() => Load(folder));

    public IEnumerable<Skill> Skills(IEnumerable<ushort> nodes) => nodes.Distinct()
        .Select(n => entries.Value.GetValueOrDefault(n)?.Skill).OfType<Skill>();
    public IEnumerable<Buff> Buffs(IEnumerable<ushort> nodes) => nodes.Distinct()
        .SelectMany(n => entries.Value.GetValueOrDefault(n)?.Buffs ?? []).DistinctBy(b => b.Template);
    public Dictionary<uint, int> OwnerStats(IEnumerable<ushort> nodes)
    {
        var result = new Dictionary<uint, int>();
        foreach (var buff in Buffs(nodes).Where(b => b.AffectsOwner))
            foreach (var (key, value) in buff.Stats) result[key] = result.GetValueOrDefault(key) + value;
        return result;
    }

    static IReadOnlyDictionary<ushort, Attachments> Load(string folder)
    {
        string[] files = ["PassiveSkills", "GrantedEffectsPerLevel", "GrantedEffects", "ActiveSkills", "BuffTemplates", "BuffDefinitions"];
        if (files.Any(f => !File.Exists(Path.Combine(folder, "tables", f + ".json")))) return new Dictionary<ushort, Attachments>();
        var docs = files.Select(f => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables", f + ".json")))).ToArray();
        try
        {
            var result = new Dictionary<ushort, Attachments>();
            foreach (var row in docs[0].RootElement.EnumerateArray())
            {
                int id = row.GetProperty("PassiveSkillGraphId").GetInt32();
                if (id is <= 0 or > ushort.MaxValue) continue;
                Skill? skill = null;
                if (row.TryGetProperty("GrantedEffectsPerLevel", out var granted) && granted.ValueKind == JsonValueKind.Number)
                {
                    var perLevel = docs[1].RootElement[granted.GetInt32()];
                    int effect = perLevel.GetProperty("GrantedEffect").GetInt32();
                    var effectRow = docs[2].RootElement[effect];
                    string name = effectRow.GetProperty("Id").GetString() ?? "";
                    if (effectRow.TryGetProperty("ActiveSkill", out var active) && active.ValueKind == JsonValueKind.Number)
                        name = docs[3].RootElement[active.GetInt32()].GetProperty("DisplayedName").GetString() ?? name;
                    skill = new((ushort)id, effect, perLevel.GetProperty("Level").GetInt32(), name);
                }
                var buffs = new List<Buff>();
                foreach (var reference in row.GetProperty("PassiveSkillBuffs").EnumerateArray())
                {
                    int template = reference.GetInt32();
                    var b = docs[4].RootElement[template];
                    int definition = b.GetProperty("BuffDefinitionsKey").GetInt32();
                    var d = docs[5].RootElement[definition];
                    int[] values = [.. b.GetProperty("Buff_StatValues").EnumerateArray().Select(x => x.GetInt32())];
                    uint[] keys = [.. d.GetProperty("StatsKeys").EnumerateArray().Select(x => checked((uint)x.GetInt32() + 1))];
                    var stats = keys.Zip(values, (k, v) => (k, v)).ToList();
                    foreach (var flag in d.GetProperty("GrantedFlags").EnumerateArray())
                        stats.Add((checked((uint)flag.GetInt32() + 1), 1));
                    int radius = b.GetProperty("AuraRadius").GetInt32();
                    // Target lists in this client: 1 self, 2 allies, 3 enemies. An empty list/radius zero is a self buff.
                    bool owner = radius == 0 || b.GetProperty("__col4").EnumerateArray().Any(x => x.GetInt32() == 1);
                    // 0x1c1 transmits stat values only for IsRecovery rows. Ordinary passive buff stats are already
                    // present in the authoritative Stats component; raw values here would be read as the next packet.
                    int[] recovery = d.GetProperty("IsRecovery").GetBoolean()
                        ? keys.Select((_, i) => i < values.Length ? values[i] : 0).ToArray() : [];
                    buffs.Add(new(template, checked((ushort)definition), b.GetProperty("Id").GetString() ?? "", radius, owner, stats, recovery));
                }
                if (skill is not null || buffs.Count > 0) result[(ushort)id] = new(skill, buffs);
            }
            return result;
        }
        finally { foreach (var doc in docs) doc.Dispose(); }
    }
}
