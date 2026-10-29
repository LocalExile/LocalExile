using System.Text.Json;

/// <summary>
/// Skills that allocated passives grant ("Grants Level 30 Determination Skill"): PassiveSkills.dat
/// GrantedEffectsPerLevel by PassiveSkillGraphId, GrantedEffectsPerLevelHardmode in Ruthless. The row fixes the skill
/// and its level. A passive-granted skill is not a gem: it has no supports, no quality, no gem-level bonuses and no
/// requirements. Ordinary and ascendancy nodes both count. Game data: data/tables/PassiveSkills.json, loaded on first use.
/// </summary>
sealed class PassiveGrantedSkills(SkillCatalog skills, string folder = "data", bool ruthless = false)
{
    /// <summary>One granted skill: the node, its GrantedEffectsPerLevel row, the row's GrantedEffects row (and Id) and
    /// level.</summary>
    public readonly record struct Grant(ushort Node, int EffectsPerLevelRow, int Effect, string EffectId, int Level)
    {
        /// <summary>The host GemSkillIds keys the skill by (socket 0), so its skill id stays stable like a socketed
        /// gem's.</summary>
        public uint Host => HostBase | Node;
    }

    /// <summary>Synthetic hosts: item ids count up from 1 and never reach this range.</summary>
    public const uint HostBase = 0xFFFF0000;

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
            // Ruthless uses its own column (Determination's points at DeterminationHardMode), as its gems use their
            // HardMode effects; HasHardmodeAlternate only marks the stat columns.
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

    // The skills the player's allocated passives grant, as virtual gem sources after the socketed gems
    // (GemSkillEffectsAt, SupportedSkillAt): same id rules, the granted level, quality 0, no supports.
    // shortcut: only self auras (AuraCatalog) have an executor; a granted triggered, minion or other skill stays out of
    // the skill list until its mechanic exists (the client would offer a skill that does nothing).
    static IReadOnlyList<PassiveGrantedSkills.Grant> GrantedSkillsOf(ClientState conn)
    {
        ushort[] nodes;
        lock (conn.Monsters) nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        return [.. (conn.Ruthless ? RuthlessGrantedSkills : GrantedSkills).Value.For(nodes)
            .Where(g => AuraCatalog.Supported(g.EffectId))];
    }

    // A granted skill resolved like a gem: its own stats at the granted level and the aura markers SupportedSkillAt
    // adds. No EffectiveGemLevel or GemQualityBonus: those raise gems.
    static SupportedGem GrantedSupportedSkill(ClientState conn, PassiveGrantedSkills.Grant grant)
    {
        var own = SkillsFor(conn).EffectStats(grant.Effect, grant.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        if (SkillsFor(conn).IsAura(grant.Effect)) own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(grant.Effect, grant.Level, 0, [], own, Granted: true);
    }
}
