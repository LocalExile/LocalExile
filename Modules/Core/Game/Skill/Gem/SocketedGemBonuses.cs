using GameServer.Core.Protocol.Instance;


/// <summary>Local level and quality bonuses belong to actual gems in the source item, not to the character
/// or to virtual supports granted by its mods. GemTags identifies the native gem families.</summary>
public static class SocketedGemBonuses
{
    public static (int Level, int Quality) For(Item host, int socket, Item gem, ItemCatalog items,
        SkillCatalog skills, IReadOnlyDictionary<uint, int> local, int playerLevel)
    {
        if (items.ByHash(gem.TypeHash) is not { } type || items.Gem(type) is not { Variants.Count: > 0 } info) return default;
        int variant = info.Variant(gem.GemVariant).Index;
        int Get(uint stat) => local.GetValueOrDefault(stat);
        int level = Get(Stat.LocalSocketedGemLevelPlus)
            + Get(info.IsSupport ? Stat.LocalSocketedSupportGemLevelPlus : Stat.LocalSocketedActiveSkillGemLevelPlus);
        int quality = Get(Stat.LocalSocketedGemQualityPlus)
            + Get(info.IsSupport ? Stat.LocalSocketedSupportGemQualityPlus : Stat.LocalSocketedActiveSkillGemQualityPlus);
        bool InFamily(uint stat) => skills.FamilyStatRaises(stat, type.Row, variant)
            || stat == Stat.LocalSocketedTrapAndMineGemLevelPlus
                && (skills.GemHasTag(type.Row, variant, "trap") || skills.GemHasTag(type.Row, variant, "mine"))
            || stat == Stat.LocalSocketedElementalGemLevelPlus
                && (skills.GemHasTag(type.Row, variant, "fire") || skills.GemHasTag(type.Row, variant, "cold")
                    || skills.GemHasTag(type.Row, variant, "lightning"));
        foreach (var (stat, value) in local)
            if (stat is not (Stat.LocalSocketedGemLevelPlus or Stat.LocalSocketedGemQualityPlus
                or Stat.LocalSocketedSupportGemLevelPlus or Stat.LocalSocketedSupportGemQualityPlus
                or Stat.LocalSocketedActiveSkillGemLevelPlus or Stat.LocalSocketedActiveSkillGemQualityPlus)
                && Stats.Name(stat).StartsWith("local_socketed_", StringComparison.Ordinal) && InFamily(stat))
            {
                if (Stats.Name(stat).EndsWith("_gem_level_+", StringComparison.Ordinal)) level += value;
                if (Stats.Name(stat).EndsWith("_gem_quality_+", StringComparison.Ordinal)) quality += value;
            }
        if (!skills.FamilyStatRaises(Stat.LocalSocketedVaalGemLevelPlus, type.Row, variant))
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

    public static void ApplySkillStats(SkillCatalog skills, int effect, IReadOnlyDictionary<uint, int> local,
        Dictionary<uint, int> own)
    {
        void Add(uint input, uint output, int sign = 1)
        {
            int value = local.GetValueOrDefault(input);
            if (value != 0)
                own[output] = ActorScale.Saturate(own.GetValueOrDefault(output) + (long)value * sign);
        }

        Add(Stat.LocalDisplaySocketedGemsChainXAdditionalTimes, Stat.NumberOfAdditionalChainsForProjectiles);
        Add(Stat.LocalDisplaySocketedSkillsFork, Stat.ProjectilesFork);
        // The old display key backs the shield's current generic reservation efficiency modifier.
        Add(Stat.LocalDisplaySocketedGemsHaveManaReservationPlusPct, Stat.BaseReservationEfficiencyPlusPct, sign: -1);

        uint damage;
        if (skills.IsAttack(effect))
        {
            Add(Stat.LocalDisplaySocketedAttacksAdditionalCriticalStrikeChance, Stat.AttackAdditionalCriticalStrikeChancePermyriad);
            Add(Stat.LocalDisplaySocketedAttacksCriticalStrikeMultiplierPlus, Stat.BaseCriticalStrikeMultiplierPlus);
            Add(Stat.LocalDisplaySocketedAttacksManaCostPlus, Stat.SkillManaCostPlus);
            damage = Stat.LocalDisplaySocketedAttackDamagePlusPctFinal;
        }
        else if (skills.IsSpell(effect))
        {
            Add(Stat.LocalDisplaySocketedSpellsAdditionalCriticalStrikeChance, Stat.SpellAdditionalCriticalStrikeChancePermyriad);
            Add(Stat.LocalDisplaySocketedSpellsCriticalStrikeMultiplierPlus, Stat.BaseCriticalStrikeMultiplierPlus);
            damage = Stat.LocalDisplaySocketedSpellDamagePlusPctFinal;
        }
        else return;

        if (local.GetValueOrDefault(damage) is int more and not 0)
        {
            double previous = 1 + own.GetValueOrDefault(Stat.ActiveSkillDamagePlusPctFinal) / 100d;
            own[Stat.ActiveSkillDamagePlusPctFinal] = (int)Math.Clamp(
                Math.Round(100 * (previous * (1 + more / 100d) - 1)), int.MinValue, int.MaxValue);
        }
    }
}
