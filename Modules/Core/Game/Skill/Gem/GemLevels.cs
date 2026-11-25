
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
        if (type is null || ItemsFor(player).Gem(type) is not { Variants.Count: > 0 } info)
            return SkillGemLevel.Resolve(intrinsic, supportedBonus, []);
        var skills = SkillsFor(player);
        int variant = info.Variant(gem.GemVariant).Index;
        long bonus = (long)intrinsic + supportedBonus;
        if (info.IsSupport)
        {
            if (!skills.IsExceptional(type.Row, variant)) bonus += RawGemStat(player, SkillGemLevel.NonExceptionalSupports);
            if (skills.GemHasTag(type.Row, variant, "critical")) bonus += RawGemStat(player, Stat.CriticalSupportGemLevelPlus);
        }
        else
        {
            bonus += RawGemStat(player, SkillGemLevel.AllSkillGems);
            foreach (uint stat in SkillGemLevel.FamilyStats)
                if (skills.FamilyStatRaises(stat, type.Row, variant)) bonus += RawGemStat(player, stat);
            foreach (var (stat, tag) in SkillGemLevel.TaggedActiveLevelStats)
                if (skills.GemHasTag(type.Row, variant, tag)) bonus += RawGemStat(player, stat);
            if (AttributeStat(info.Colour) is { } attribute) bonus += RawGemStat(player, attribute);
        }
        return (int)Math.Clamp(bonus, 1, int.MaxValue);
    }

    // The attribute gem-level stat for a SkillGems GemColour (Active gems only: support gems have their own).
    static uint? AttributeStat(int colour) => SkillGemLevel.AttributeStat(colour);

    // Global skill gem quality applies to active gems with the relevant native gem tag.
    public static int GemQualityBonus(Player player, GameServer.Core.Protocol.Instance.Item gem)
    {
        var items = ItemsFor(player);
        if (items.ByHash(gem.TypeHash) is not { } type || items.Gem(type) is not { IsSupport: false, Variants.Count: > 0 } info) return 0;
        int quality = RawGemStat(player, SkillGemLevel.AllSkillGemQuality);
        int variant = info.Variant(gem.GemVariant).Index;
        return !SkillsFor(player).FamilyStatRaises(Stat.LocalSocketedMinionGemLevelPlus, type.Row, variant) ? quality
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
