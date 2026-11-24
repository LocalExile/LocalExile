
public static partial class Game
{
    // A support resolution is synchronous in the area's frame. Its level/quality lookups share raw sources;
    // the scope ends before any later hit, charge expiry, equipment change or aura reconciliation.
    sealed class GemSourceScope(Player player) : IDisposable
    {
        readonly GemSourceScope? outer = GemSources.Value;
        public readonly Player Player = player;
        public Dictionary<uint, int>? Sources;
        public void Enter() => GemSources.Value = this;
        public void Dispose() => GemSources.Value = outer;
    }
    static readonly AsyncLocal<GemSourceScope?> GemSources = new();
    // Read raw sources rather than CharacterPlayerStats: reservation/pool calculations can already be resolving
    // this skill. A native developer replacement wins over the aggregate source, without feeding a derived level back.
    public static int EffectiveGemLevel(Player player, GameServer.Core.Protocol.Instance.Item gem, int supportedBonus)
    {
        int intrinsic = ItemsFor(player).GemLevel(gem);
        var type = ItemsFor(player).ByHash(gem.TypeHash);
        if (type is null || ItemsFor(player).Gem(type) is not { } info)
            return SkillGemLevel.Resolve(intrinsic, supportedBonus, []);
        var skills = SkillsFor(player);
        uint[] stats = info.IsSupport
            ? [.. skills.IsExceptional(type.Row) ? Array.Empty<uint>() : [SkillGemLevel.NonExceptionalSupports],
                .. skills.GemHasTag(type.Row, "critical") ? [Stat.CriticalSupportGemLevelPlus] : Array.Empty<uint>()]
            : [SkillGemLevel.AllSkillGems, .. SkillGemLevel.FamilyStats.Where(stat => skills.FamilyStatRaises(stat, type.Row)),
                .. SkillGemLevel.TaggedActiveLevelStats.Where(entry => skills.GemHasTag(type.Row, entry.Tag)).Select(entry => entry.Stat),
                .. AttributeStat(info.Colour) is { } attribute ? [attribute] : Array.Empty<uint>()];
        return SkillGemLevel.Resolve(intrinsic, supportedBonus, stats.Select(stat => RawGemStat(player, stat)));
    }

    // The attribute gem-level stat for a SkillGems GemColour (Active gems only: support gems have their own).
    static uint? AttributeStat(int colour) => SkillGemLevel.AttributeStat(colour);

    // Global skill gem quality applies to active gems with the relevant native gem tag.
    public static int GemQualityBonus(Player player, GameServer.Core.Protocol.Instance.Item gem)
    {
        var items = ItemsFor(player);
        if (items.ByHash(gem.TypeHash) is not { } type || items.Gem(type) is not { IsSupport: false }) return 0;
        int quality = RawGemStat(player, SkillGemLevel.AllSkillGemQuality);
        return !SkillsFor(player).FamilyStatRaises(Stat.LocalSocketedMinionGemLevelPlus, type.Row) ? quality
            : ActorScale.Saturate((long)quality + RawGemStat(player, Stat.MinionSkillGemQualityPlus));
    }

    public static int RawGemStat(Player player, uint stat)
    {
        if (!(stat is var key)) return 0;
        var scope = GemSources.Value;
        var sources = scope is not null && ReferenceEquals(scope.Player, player)
            ? scope.Sources ??= CharacterStatSources(player).Stats : CharacterStatSources(player).Stats;
        return sources.GetValueOrDefault(key);
    }
}
