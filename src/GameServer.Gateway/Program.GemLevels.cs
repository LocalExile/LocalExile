partial class Program
{
    // Read raw sources rather than CharacterPlayerStats: reservation/pool calculations can already be resolving
    // this skill. A native developer replacement wins over the aggregate source, without feeding a derived level back.
    static int EffectiveGemLevel(ClientState conn, GameServer.Core.Protocol.Instance.Item gem, int supportedBonus)
    {
        int intrinsic = Items.GemLevel(gem);
        var type = Items.ByHash(gem.TypeHash);
        if (type is null || Items.Gem(type) is not { IsSupport: false })
            return SkillGemLevel.Resolve(intrinsic, supportedBonus, []);
        string[] families = [.. SkillGemLevel.FamilyStats.Where(stat => Skills.FamilyStatRaises(stat, type.Row))];
        if (families.Length == 0) return SkillGemLevel.Resolve(intrinsic, supportedBonus, []);
        var sources = CharacterStatSources(conn).Stats;
        lock (conn.Monsters)
            return SkillGemLevel.Resolve(intrinsic, supportedBonus, families.Select(stat =>
                Stats.Key(stat) is uint key ? conn.PlayerStats.GetValueOrDefault(key, sources.GetValueOrDefault(key)) : 0));
    }
}
