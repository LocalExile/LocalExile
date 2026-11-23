using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

// Unified monster kill-reward & NoRewards funnel shared across melee/AoE/DoT (Combat.cs), projectiles
// (ProjectileReactions.cs), minions/accepted hits (MinionOutcomes.cs), and party kill shares (SharedKills.cs).
public static partial class Game
{
    /// <summary>Whether a monster gives no experience, loot, gold, or kill flask charges (either instance-flagged
    /// <see cref="SpawnedMonster.NoRewards"/> or species mod <see cref="BossMods.NoRewards"/>).</summary>
    public static bool MonsterGivesNoRewards(SpawnedMonster m) =>
        m.NoRewards || BossMods.NoRewards(m.Variety);

    /// <summary>Whether a monster drops no items or gold (includes <see cref="BossMods.NoDrops"/> and the tutorial zombie).</summary>
    public static bool MonsterGivesNoItems(SpawnedMonster m) =>
        m.NoRewards || BossMods.GivesNoItems(m.Variety) || m.Variety?.Id == TutorialObjects.Zombie;

    /// <summary>Computes solo/party-scaled player XP and gem XP for a kill, uniformly honouring
    /// <see cref="MonsterGivesNoRewards"/> and species XP bonuses (<c>MonsterActBoss*</c> / <c>MonsterQuestBoss*</c>).</summary>
    public static (uint Xp, uint GemXp) ComputeKillExperience(Player player, SpawnedMonster m,
        MonsterCatalog.Variety variety, MonsterInfo modMonster, double partyShare)
    {
        if (MonsterGivesNoRewards(m) || Monsters.BaseExperience(variety, m.Level) is not int baseXp)
            return (0, 0);
        int speciesXpBonus = m.Effects.SpeciesExperienceBonusPct(m.Rarity);
        int xpMultiplier = speciesXpBonus > 0
            ? (int)Math.Round(variety.ExperienceMultiplier * (1 + speciesXpBonus / 100.0))
            : variety.ExperienceMultiplier;
        return KillExperience(PlayerOf(player), modMonster, baseXp, xpMultiplier, m.Rarity,
            player.Pools.Level, m.Level, partyShare);
    }

    /// <summary>Call BEFORE committing kill quest flags: returns true if this kill will newly grant at least one
    /// quest kill flag for <paramref name="player"/> (ensuring first-kill boss bonuses fire once per boss per
    /// character and never on farm re-kills).</summary>
    public static bool HasUnsetKillQuestFlag(Player player, string varietyId)
    {
        var flags = QuestFlagsOnKill(player, varietyId);
        for (int i = 0; i < flags.Count; i++)
            if (!player.Character.HasQuestFlag(flags[i])) return true;
        return false;
    }

    /// <summary>Whether this kill is Act 1 Twilight Strand Hillock (eligible for curated starter drop).</summary>
    public static bool IsTwilightStrandHillock(string areaId, string varietyId) =>
        areaId == TutorialObjects.Strand
        && varietyId is "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal" or "Metadata/Monsters/ZombieBoss/ZombieBossHillock";

    // Final killable Act Boss varieties whose second form does not repeat the Form 1 MonsterActBoss* mod row.
    static readonly HashSet<string> FinalActBossVarieties = new(StringComparer.Ordinal)
    {
        "Metadata/Monsters/Seawitch/BossMerveil2",
        "Metadata/Monsters/IncaShadowBoss/BossIncaShadow",
        "Metadata/Monsters/Dominusdemon/Dominusdemon",
        "Metadata/Monsters/Malachai/MalachaiBoss2",
        "Metadata/Monsters/BrineKing/BrineKing",
        "Metadata/Monsters/VaalSpiderGod/Arakaali",
        "Metadata/Monsters/LunarisSolaris/Solaris",
        "Metadata/Monsters/LunarisSolaris/Lunaris",
        "Metadata/Monsters/UnholyTrio/UnholyTrio",
        "Metadata/Monsters/KitavaBoss/KitavaFinal",
    };

    /// <summary>Classifies a unique campaign boss for first-kill bonus loot (<see cref="ItemDrops.RollBossFirstKillBonus"/>).
    /// Returns null for non-bosses, Twilight Strand Hillock (uses <see cref="ItemDrops.RollHillockStarterDrop"/>), and
    /// map bosses.</summary>
    public static bool? ClassifyFirstKillCampaignBoss(Player player, MonsterCatalog.Variety variety)
    {
        if (MapDevices.IsMapArea(player.Layout.Hash)) return null;
        if (IsTwilightStrandHillock(player.Layout.Id, variety.Id)) return null;
        if (BossMods.IsActBoss(variety) || FinalActBossVarieties.Contains(variety.Id)) return true;
        if (BossMods.IsQuestBoss(variety)) return false;
        if (BossKits.IsCampaignBoss(variety)) return false;
        return null;
    }

    /// <summary>Unified monster loot roll across Combat, Projectiles, and Minions: wires area-scoped campaign Divination
    /// Cards, overlevel currency/card penalty, curved IIR/IIQ gold scaling, Twilight Strand Hillock starter drops,
    /// first-kill Act/Quest boss bonuses, King in the Mists / Encounter Jewels / Sirus / Party Currency / Map drops,
    /// and mod hooks.</summary>
    public static IReadOnlyList<ItemDrops.Drop> RollKillLoot(Player player, SpawnedMonster m, (int X, int Y) at,
        Random rng, bool firstBossKill = false)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return [];
        if (MonsterGivesNoItems(m)) return [];
        if (!GameEvents.AllowsLoot(player)) return [];

        var balance = LeagueFor(player).Rules;
        var modded = GameEvents.RunDrops(player, ModMonster(m, at));
        int iir = IncreasedItemRarity(player, m);
        double iiq = ItemQuantityFactor(player, m);
        int areaLevel = ServerLevels.AreaLevel(player.Layout.Id) is > 0 and var al ? al : m.Level;
        List<ItemDrops.Drop> drops = [];

        // The King in the Mists always drops one of four uniques, plus its support gems and card.
        if (KingInMists.IsBoss3(m.Variety))
            drops.AddRange(DropsFor(player).KingInMists(m.Level, rng));

        if (modded is not { Cancel: true })
        {
            if (m.Rarity == MonsterRarity.Unique && m.Variety is { } boss)
            {
                drops.AddRange(DropsFor(player).EncounterJewels(boss.Id, m.Level, rng, DropDataProvider.Default.Current));
                if (IsTwilightStrandHillock(player.Layout.Id, boss.Id))
                {
                    drops.AddRange(DropsFor(player).RollHillockStarterDrop(player.Character.ClassRow, rng));
                }
                else if (firstBossKill && ClassifyFirstKillCampaignBoss(player, boss) is bool actBoss)
                {
                    drops.AddRange(DropsFor(player).RollBossFirstKillBonus(m.Level, rng, actBoss: actBoss,
                        increasedRarity: iir, rarityMultiplier: balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1)));
                }
            }

            int mapTier = MapRunOf(player)?.Area.Tier ?? 0;
            var mapMods = MapModsOf(player);
            drops.AddRange(DropsFor(player).Roll(
                m.Level, m.Rarity, rng, iir,
                balance.ItemDropMultiplier * (modded?.Quantity ?? 1) * iiq,
                balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1),
                mapTier, mapMods, MapScarabQuantity(mapMods),
                areaId: player.Layout.Id,
                characterLevel: player.Pools.Level,
                areaLevel: areaLevel));
            drops.AddRange(PartyCurrency(player, drops, rng));
            if (RollMapDropForKill(player, m.Level, m.Rarity, mapTier, rng, modded?.MapRelated ?? 1) is { } mapDrop)
                drops.Add(mapDrop);
            if (DropsFor(player).Gold(
                m.Level, m.Rarity, rng,
                balance.GoldMultiplier * SystemSettings.Default.GoldDropMultiplier * (modded?.Gold ?? 1),
                increasedRarity: iir,
                quantityFactor: iiq) is { } gold)
                drops.Add(gold);
            drops.AddRange(ExtraDropsFromMap(player, m.Level, m.Rarity, rng));
            drops.AddRange(Sirus.Rewards(player, m));
        }

        if (modded is { Stacks.Count: > 0 }) drops.AddRange(ModStacks(player, modded));
        drops = ModdedDrops(player, modded, drops, m.Level, m.Rarity, iir, balance.EquipmentRarityMultiplier, rng);
        drops.AddRange(MadeDrops(player, modded));
        modded?.ApplyTransforms(drops);
        if (MapBeyond.CorruptsDrops(player, m.Variety?.Id)) CorruptDrops(drops);
        return ItemDrops.Stacked(drops);
    }

    /// <summary>Scatters a dropped item 3 to 6 cells around <paramref name="at"/>, falling back to <paramref name="at"/>
    /// if the scattered cell is unwalkable.</summary>
    public static (int X, int Y) ScatterDropPosition(Player player, (int X, int Y) at, Random rng)
    {
        double angle = rng.NextDouble() * Math.Tau, radius = 3 + rng.NextDouble() * 3;
        int x = at.X + (int)Math.Round(Math.Cos(angle) * radius);
        int y = at.Y + (int)Math.Round(Math.Sin(angle) * radius);
        if (player.Layout.Walkable is { } walks && !walks(x, y)) return at;
        return (x, y);
    }
}
