
static partial class Game
{
    // A support resolution is synchronous under Monsters. Its level/quality lookups share raw sources;
    // the scope ends before any later hit, charge expiry, equipment change or aura reconciliation.
    sealed class GemSourceScope(ClientState conn) : IDisposable
    {
        readonly GemSourceScope? outer = GemSources.Value;
        public readonly ClientState Conn = conn;
        public Dictionary<uint, int>? Sources;
        public void Enter() => GemSources.Value = this;
        public void Dispose() => GemSources.Value = outer;
    }
    static readonly AsyncLocal<GemSourceScope?> GemSources = new();
    // Read raw sources rather than CharacterPlayerStats: reservation/pool calculations can already be resolving
    // this skill. A native developer replacement wins over the aggregate source, without feeding a derived level back.
    internal static int EffectiveGemLevel(ClientState conn, GameServer.Core.Protocol.Instance.Item gem, int supportedBonus)
    {
        int intrinsic = ItemsFor(conn).GemLevel(gem);
        var type = ItemsFor(conn).ByHash(gem.TypeHash);
        if (type is null || ItemsFor(conn).Gem(type) is not { } info)
            return SkillGemLevel.Resolve(intrinsic, supportedBonus, []);
        string[] stats = info.IsSupport
            ? SkillsFor(conn).IsExceptional(type.Row) ? [] : [SkillGemLevel.NonExceptionalSupports]
            : [SkillGemLevel.AllSkillGems, .. SkillGemLevel.FamilyStats.Where(stat => SkillsFor(conn).FamilyStatRaises(stat, type.Row))];
        return SkillGemLevel.Resolve(intrinsic, supportedBonus, stats.Select(stat => RawGemStat(conn, stat)));
    }

    // +% quality of all skill gems: active gems only, like the level stat.
    internal static int GemQualityBonus(ClientState conn, GameServer.Core.Protocol.Instance.Item gem) =>
        ItemsFor(conn).ByHash(gem.TypeHash) is { } type && ItemsFor(conn).Gem(type) is { IsSupport: false }
            ? RawGemStat(conn, SkillGemLevel.AllSkillGemQuality) : 0;

    internal static int RawGemStat(ClientState conn, string stat)
    {
        if (Stats.Key(stat) is not uint key) return 0;
        var scope = GemSources.Value;
        var sources = scope is not null && ReferenceEquals(scope.Conn, conn)
            ? scope.Sources ??= CharacterStatSources(conn).Stats : CharacterStatSources(conn).Stats;
        lock (conn.Monsters) return conn.PlayerStats.GetValueOrDefault(key, sources.GetValueOrDefault(key));
    }
}
