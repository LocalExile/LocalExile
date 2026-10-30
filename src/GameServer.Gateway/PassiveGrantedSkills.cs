using System.Text.Json;

/// <summary>
/// Skills that allocated passives grant or trigger ("Trigger level 20 Summon Spirit of Kahuturoa Skill when ..."):
/// PassiveSkills.dat GrantedEffectsPerLevel by PassiveSkillGraphId, GrantedEffectsPerLevelHardmode in Ruthless. The row
/// fixes the skill and its level. A passive-granted skill is not a gem: it has no supports, no quality, no gem-level
/// bonuses and no requirements. Ordinary and ascendancy nodes both count. Game data: data/tables/PassiveSkills.json,
/// loaded on first use.
/// </summary>
sealed class PassiveGrantedSkills(SkillCatalog skills, string folder = "data", bool ruthless = false)
{
    /// <summary>One granted skill: the node, its GrantedEffectsPerLevel row, the row's GrantedEffects row (and Id) and
    /// level.</summary>
    public readonly record struct Grant(ushort Node, int EffectsPerLevelRow, int Effect, string EffectId, int Level);

    readonly Lazy<Dictionary<ushort, Grant>> _grants = new(() => Load(skills, folder, ruthless));

    /// <summary>The skills these nodes grant, in node order, each node once.</summary>
    public IReadOnlyList<Grant> For(IEnumerable<ushort> nodes) =>
        [.. nodes.Distinct().Where(_grants.Value.ContainsKey).Select(n => _grants.Value[n])];

    static Dictionary<ushort, Grant> Load(SkillCatalog skills, string folder, bool ruthless)
    {
        var grants = new Dictionary<ushort, Grant>();
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        if (!File.Exists(path)) return grants;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
        {
            int graphId = r.GetProperty("PassiveSkillGraphId").GetInt32();
            // Ruthless uses its own column, as its gems use their HardMode effects.
            if (graphId is <= 0 or > ushort.MaxValue
                || !r.TryGetProperty(ruthless ? "GrantedEffectsPerLevelHardmode" : "GrantedEffectsPerLevel", out var v)
                || v.ValueKind != JsonValueKind.Number || skills.EffectsPerLevel(v.GetInt32()) is not { } level
                || skills.EffectId(level.Effect) is not { } effectId) continue;
            grants.TryAdd((ushort)graphId, new((ushort)graphId, v.GetInt32(), level.Effect, effectId, level.Level));
        }
        return grants;
    }
}

partial class Program
{
    static readonly Lazy<PassiveGrantedSkills> GrantedSkills = new(() => NewGrantedSkills(false));
    static readonly Lazy<PassiveGrantedSkills> RuthlessGrantedSkills = new(() => NewGrantedSkills(true));
    static PassiveGrantedSkills NewGrantedSkills(bool ruthless) => new(ruthless ? RuthlessSkills.Value : Skills, ruthless: ruthless);

    /// <summary>The skills the player's allocated passives grant whose node carries this trigger stat
    /// (<c>has_trigger_on_crit_by_unique_enemy</c> and the like; the level row's AdditionalFlags name the same trigger).
    /// Call under the Monsters lock.</summary>
    static IEnumerable<PassiveGrantedSkills.Grant> TriggeredGrantsLocked(ClientState conn, string trigger)
    {
        if (Stats.Key(trigger) is not uint key) return [];
        var passives = PassivesFor(conn);
        return (conn.Ruthless ? RuthlessGrantedSkills : GrantedSkills).Value.For([.. conn.Passives, .. conn.MainAscendancy.Allocated])
            .Where(g => passives.Find(g.Node)?.Stats.Any(s => s.Key == key && s.Value != 0) == true);
    }
}
