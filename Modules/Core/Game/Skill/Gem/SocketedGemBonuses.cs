using GameServer.Core.Protocol.Instance;


/// <summary>Local level and quality bonuses belong to actual gems in the source item, not to the character
/// or to virtual supports granted by its mods. GemTags identifies the native gem families.</summary>
static class SocketedGemBonuses
{
    public static (int Level, int Quality) For(Item host, int socket, Item gem, ItemCatalog items,
        SkillCatalog skills, IReadOnlyDictionary<string, int> local, int playerLevel)
    {
        if (items.ByHash(gem.TypeHash) is not { } type || items.Gem(type) is not { } info) return default;
        int Get(string stat) => local.GetValueOrDefault(stat);
        int level = Get("local_socketed_gem_level_+")
            + Get(info.IsSupport ? "local_socketed_support_gem_level_+" : "local_socketed_active_skill_gem_level_+");
        int quality = Get("local_socketed_gem_quality_+")
            + Get(info.IsSupport ? "local_socketed_support_gem_quality_+" : "local_socketed_active_skill_gem_quality_+");
        foreach (var (stat, value) in local)
            if (stat is not ("local_socketed_gem_level_+" or "local_socketed_gem_quality_+"
                or "local_socketed_support_gem_level_+" or "local_socketed_support_gem_quality_+"
                or "local_socketed_active_skill_gem_level_+" or "local_socketed_active_skill_gem_quality_+")
                && stat.StartsWith("local_socketed_", StringComparison.Ordinal) && skills.FamilyStatRaises(stat, type.Row))
            {
                if (stat.EndsWith("_gem_level_+", StringComparison.Ordinal)) level += value;
                if (stat.EndsWith("_gem_quality_+", StringComparison.Ordinal)) quality += value;
            }
        if (!skills.FamilyStatRaises("local_socketed_vaal_gem_level_+", type.Row))
            level += Get("local_socketed_non_vaal_gem_level_+");
        int filled = host.Sockets.Count(s => s.Gem is not null);
        level += filled * Get("local_socketed_gem_level_+_per_filled_socket");
        if (filled == 1) level += Get("local_socketed_gem_level_+_if_only_socketed_gem");
        int divisor = Get("local_socketed_skill_gem_level_+1_per_x_player_levels");
        if (!info.IsSupport && divisor > 0) level += playerLevel / divisor;
        if (socket >= 0 && socket < host.Sockets.Count)
        {
            if (host.Sockets[socket].Colour == SocketColour.Red) level += Get("local_socketed_gems_in_red_sockets_get_level_+");
            if (host.Sockets[socket].Colour == SocketColour.Green) quality += Get("local_socketed_gems_in_green_sockets_get_quality_%");
        }
        return (level, quality);
    }
}
