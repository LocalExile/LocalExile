using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>Buffs a passive puts on the player: PassiveSkills.PassiveSkillBuffs → BuffTemplates → BuffDefinitions, read
/// like the Antiquarian's Adrenaline (the template's Buff_StatValues for the definition's GrantedStats, in order; Stats
/// row + 1 is the client key). The stats join the allocated passives' sum (CharacterStatSources), and the buff itself
/// is published on the player (S→C 0x1c1, removed with 0x1c2) for its icon and visual.
/// Only the self buffs in <see cref="SelfBuffTemplates"/> are applied. Templates with an AuraRadius are enemy or ally
/// auras, and the other radius-0 templates carry their own timing or condition (Trickster's alternating damage taken,
/// Occultist's wither pulse, Chieftain's while stationary); none of those is applied here.</summary>
static class PassiveBuffAuras
{
    internal sealed record Buff(string Template, ushort Definition, IReadOnlyList<(uint Key, int Value)> Stats);

    // Permanent, unconditional and on the player itself (AuraRadius 0, CancelOnDeath false).
    static readonly HashSet<string> SelfBuffTemplates =
    [
        "CustomAscendancyIllegalFisherman", // Surfcaster Ghost of the Deep: BuffDefinitions 2550 Torment Touched
    ];

    static readonly Lazy<IReadOnlyDictionary<string, Buff[]>> byPassive = new(() => Load("data"));

    /// <summary>The self buffs of these nodes, one per template however many nodes grant it.</summary>
    internal static IReadOnlyList<Buff> For(PassiveTree tree, IEnumerable<ushort> nodes) =>
        [.. nodes.SelectMany(n => tree.Find(n) is { } node && byPassive.Value.TryGetValue(node.Id, out var buffs) ? buffs : [])
            .DistinctBy(b => b.Template)];

    internal static Dictionary<uint, int> WithSelfBuffs(Dictionary<uint, int> stats, PassiveTree tree, IEnumerable<ushort> nodes)
    {
        foreach (var buff in For(tree, nodes))
            foreach (var (key, value) in buff.Stats)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>The 0x1c1/0x1c2 that bring the player's published passive buffs to the allocated ones, or null. Run at the
    /// send gate; a new area's connection starts with none, so area entry publishes them again.</summary>
    internal static byte[]? Presentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || !conn.PlayerSent) return null;
            var wanted = For(Program.PassivesFor(conn), [.. conn.Passives, .. conn.MainAscendancy.Allocated])
                .Select(b => b.Definition).ToHashSet();
            var bytes = new List<byte>();
            foreach (var (definition, instance) in conn.PassiveBuffs.Where(b => !wanted.Contains(b.Key)).ToArray())
            {
                bytes.AddRange(InstanceEntities.RemoveBuff(Program.PlayerId, instance));
                conn.PassiveBuffs.Remove(definition);
            }
            foreach (ushort definition in wanted.Where(d => !conn.PassiveBuffs.ContainsKey(d)))
                bytes.AddRange(InstanceEntities.AddBuff(Program.PlayerId, conn.PassiveBuffs[definition] = conn.NextBuff++,
                    definition, Program.PlayerId));
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }

    static IReadOnlyDictionary<string, Buff[]> Load(string folder)
    {
        string Table(string name) => Path.Combine(folder, "tables", $"{name}.json");
        var result = new Dictionary<string, Buff[]>();
        if (new[] { "PassiveSkills", "BuffTemplates", "BuffDefinitions" }.Any(t => !File.Exists(Table(t)))) return result;
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkills")));
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Table("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Table("BuffDefinitions")));
        Buff? Template(int row)
        {
            if (row < 0 || row >= templates.RootElement.GetArrayLength()) return null;
            var t = templates.RootElement[row];
            if (t.GetProperty("Id").GetString() is not { } id || !SelfBuffTemplates.Contains(id)
                || t.GetProperty("AuraRadius").GetInt32() != 0
                || t.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } key) return null;
            int definition = key.GetInt32();
            if (definition < 0 || definition > ushort.MaxValue || definition >= definitions.RootElement.GetArrayLength()) return null;
            var granted = definitions.RootElement[definition].GetProperty("GrantedStats").EnumerateArray().Select(s => s.GetInt32()).ToList();
            var values = t.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32()).ToList();
            // A template whose values do not line up with its definition's stats is left out rather than misapplied.
            if (granted.Count != values.Count) return null;
            return new(id, (ushort)definition, [.. granted.Zip(values, (stat, value) => ((uint)(stat + 1), value))]);
        }
        foreach (var row in passives.RootElement.EnumerateArray())
            if (row.TryGetProperty("PassiveSkillBuffs", out var buffs) && buffs.ValueKind == JsonValueKind.Array
                && buffs.EnumerateArray().Select(b => Template(b.GetInt32())).OfType<Buff>().ToArray() is { Length: > 0 } self
                && row.GetProperty("Id").GetString() is { } passive)
                result.TryAdd(passive, self);
        return result;
    }
}
