using GameServer.Core.Protocol.Instance;


/// <summary>Local level and quality bonuses belong to actual gems in the source item, not to the character
/// or to virtual supports granted by its mods. GemTags identifies the native gem families.</summary>
public static class SocketedGemBonuses
{
    public static (int Level, int Quality) For(Item host, int socket, Item gem, ItemCatalog items,
        SkillCatalog skills, IReadOnlyDictionary<uint, int> local, int playerLevel)
    {
        if (items.ByHash(gem.TypeHash) is not { } type || items.Gem(type) is not { } info) return default;
        int Get(uint stat) => local.GetValueOrDefault(stat);
        int level = Get(Stat.LocalSocketedGemLevelPlus)
            + Get(info.IsSupport ? Stat.LocalSocketedSupportGemLevelPlus : Stat.LocalSocketedActiveSkillGemLevelPlus);
        int quality = Get(Stat.LocalSocketedGemQualityPlus)
            + Get(info.IsSupport ? Stat.LocalSocketedSupportGemQualityPlus : Stat.LocalSocketedActiveSkillGemQualityPlus);
        bool InFamily(uint stat) => skills.FamilyStatRaises(stat, type.Row)
            || stat == Stat.LocalSocketedElementalGemLevelPlus
                && (skills.GemHasTag(type.Row, "fire") || skills.GemHasTag(type.Row, "cold") || skills.GemHasTag(type.Row, "lightning"));
        foreach (var (stat, value) in local)
            if (stat is not (Stat.LocalSocketedGemLevelPlus or Stat.LocalSocketedGemQualityPlus
                or Stat.LocalSocketedSupportGemLevelPlus or Stat.LocalSocketedSupportGemQualityPlus
                or Stat.LocalSocketedActiveSkillGemLevelPlus or Stat.LocalSocketedActiveSkillGemQualityPlus)
                && Stats.Name(stat).StartsWith("local_socketed_", StringComparison.Ordinal) && InFamily(stat))
            {
                if (Stats.Name(stat).EndsWith("_gem_level_+", StringComparison.Ordinal)) level += value;
                if (Stats.Name(stat).EndsWith("_gem_quality_+", StringComparison.Ordinal)) quality += value;
            }
        if (!skills.FamilyStatRaises(Stat.LocalSocketedVaalGemLevelPlus, type.Row))
            level += Get(Stat.LocalSocketedNonVaalGemLevelPlus);
        int filled = host.Sockets.Count(s => s.Gem is not null);
        level += filled * Get(Stat.LocalSocketedGemLevelPlusPerFilledSocket);
        if (filled == 1) level += Get(Stat.LocalSocketedGemLevelPlusIfOnlySocketedGem);
        int divisor = Get(Stat.LocalSocketedSkillGemLevelPlus1PerXPlayerLevels);
        if (!info.IsSupport && divisor > 0) level += playerLevel / divisor;
        if (socket >= 0 && socket < host.Sockets.Count)
        {
            if (host.Sockets[socket].Colour == SocketColour.Red) level += Get(Stat.LocalSocketedGemsInRedSocketsGetLevelPlus);
            if (host.Sockets[socket].Colour == SocketColour.Green) quality += Get(Stat.LocalSocketedGemsInGreenSocketsGetQualityPct);
        }
        return (level, quality);
    }
}
