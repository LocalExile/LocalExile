using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>One spawned Blight reward chest (lane cyst/pustule/sack, Blightheart GigaChest, or Templar Ingenuity salvaged tower).</summary>
public sealed class BlightChestState
{
    public uint Id;
    public int Lane;
    public int X, Y;
    public string ChestType = "";
    public string RewardId = "";
    public bool Opened;
    public bool IsLucky;
    public bool IsGiga;
    public bool IsSalvagedTower;
    public int SalvagedRank;
    public int ReopenCount;
}

/// <summary>
/// Spawns end-of-lane Blight reward chests (<c>BlightChestTypes</c> / <c>BlightRewardTypes</c>), Blightheart <c>GigaChest</c>,
/// and Templar Ingenuity salvaged tower chests (<c>BlightTowerChestRank1..4</c>) on victory; bursts banked monster drops at
/// the pump (lootsplosion); handles Lucky chests, Bounty re-opening, Oil tier upgrades, Blooming Blight-ravaged map drops,
/// Oil Extractor drops, and Blight Unique / Stranglegasp drops.
/// </summary>
public static class BlightChests
{
    /// <summary>Base chests spawned per lane on victory (2 in regular maps/campaign, 3 in Blighted/Blight-ravaged maps).</summary>
    public const int ChestsPerLane = 2, BlightedChestsPerLane = 3;

    const int MaxReopensPerChest = 5;
    const string OilExtractorId = "Metadata/Items/Currency/CurrencyExtractOil";

    static readonly string[] BlightUniqueNames =
    [
        "Sporeguard",
        "The Stampede",
        "Breathstealer",
        "Cowl of the Cryophile",
        "Cowl of the Ceraunophile",
        "Cowl of the Thermophile",
    ];

    static readonly (string Id, int Weight)[] StandardRewards =
    [
        ("Blight", 18),
        ("Currency", 14),
        ("Generic", 12),
        ("Armour", 10),
        ("Weapon", 10),
        ("Trinkets", 10),
        ("Gems", 8),
        ("Maps", 8),
        ("Uniques", 6),
        ("DivinationCards", 6),
        ("Essences", 6),
        ("Fragments", 5),
        ("Scarabs", 5),
        ("Fossils", 5),
        ("Abyss", 4),
        ("Harbinger", 4),
        ("Breach", 4),
        ("Legion", 4),
    ];

    static readonly (string Id, int Weight)[] HardModeRewards =
    [
        ("HardModeBlight", 22),
        ("HardModeGeneric", 18),
        ("HardModeCurrency", 14),
        ("HardModeArmour", 12),
        ("HardModeWeapon", 12),
        ("HardModeTrinkets", 10),
        ("HardModeGems", 8),
        ("HardModeMaps", 8),
    ];

    static readonly (string Id, double Weight)[] RewardCurrency =
        [.. ItemDrops.Currency.Where(c => !c.Id.EndsWith("/CurrencyIdentification", StringComparison.Ordinal) && !c.Id.EndsWith("/CurrencyPortal", StringComparison.Ordinal))];

    /// <summary>Assigns a reward category Id per lane (guaranteeing at least one Blight oil/map lane, scaled by Atlas oil/blight reward weight stats).</summary>
    public static Dictionary<int, string> PickLaneRewards(int laneCount, bool hardMode, IReadOnlyDictionary<uint, int> areaStats, Random rng)
    {
        var result = new Dictionary<int, string>();
        if (laneCount <= 0) return result;
        var baseTable = hardMode ? HardModeRewards : StandardRewards;
        int blightBonusPct = areaStats.GetValueOrDefault(Stat.MapBlightEncounterBlightRewardTypeChancePlusPct)
            + areaStats.GetValueOrDefault(Stat.MapBlightEncounterOilRewardChancePlusPct);
        var weighted = baseTable.Select(x =>
            (x.Id, Weight: x.Id.EndsWith("Blight", StringComparison.Ordinal)
                ? Math.Max(1, x.Weight * (100 + Math.Max(0, blightBonusPct)) / 100)
                : x.Weight)).ToArray();
        int totalWeight = weighted.Sum(x => x.Weight);
        int guaranteedBlightLane = rng.Next(laneCount);
        for (int i = 0; i < laneCount; i++)
        {
            if (i == guaranteedBlightLane)
            {
                result[i] = hardMode ? "HardModeBlight" : "Blight";
                continue;
            }
            int roll = rng.Next(totalWeight);
            string picked = weighted[0].Id;
            foreach (var (id, w) in weighted)
            {
                if ((roll -= w) < 0) { picked = id; break; }
            }
            result[i] = picked;
        }
        return result;
    }

    /// <summary>Picks an authentic BlightChestTypes Chests.Id matching a reward category (using BlightMushrunes /
    /// BlightInfectedMaps for Blight lanes, never BlightTowerChest which is reserved for Templar Ingenuity).</summary>
    public static string PickChestType(string rewardId, bool uberBlighted, Random rng)
    {
        string key = rewardId.Replace("HardMode", "", StringComparison.Ordinal);
        if (key == "Blight")
        {
            var blightChests = BlightTables.Chests.Where(c =>
                (c.Id.Contains("Mushrune", StringComparison.OrdinalIgnoreCase)
                    || c.Id.Contains("BlightInfectedMaps", StringComparison.OrdinalIgnoreCase))
                && !c.Id.Contains("BlightTowerChest", StringComparison.OrdinalIgnoreCase)).ToList();
            if (uberBlighted)
            {
                var ravaged = blightChests.Where(c => c.Id.Contains("BlightRavaged", StringComparison.OrdinalIgnoreCase)
                    || c.Id.Contains("Uber", StringComparison.OrdinalIgnoreCase)).ToList();
                if (ravaged.Count > 0) return ravaged[rng.Next(ravaged.Count)].Id;
            }
            if (blightChests.Count > 0) return blightChests[rng.Next(blightChests.Count)].Id;
            return BlightTables.BlightChestBaseType;
        }
        string prefix = key switch
        {
            "Currency" => "Metadata/Chests/BlightCurrency",
            "Uniques" => "Metadata/Chests/BlightUnique",
            "Gems" => "Metadata/Chests/BlightGems",
            "Maps" => "Metadata/Chests/BlightMaps",
            "Trinkets" => "Metadata/Chests/BlightTrinkets",
            "Armour" => "Metadata/Chests/BlightArmour",
            "Weapon" => "Metadata/Chests/BlightWeapon",
            "Abyss" => "Metadata/Chests/BlightAbyss",
            "Harbinger" => "Metadata/Chests/BlightHarbinger",
            "Breach" => "Metadata/Chests/BlightBreach",
            "Essences" or "Essences2" => "Metadata/Chests/BlightEssence",
            "Fragments" => "Metadata/Chests/BlightFragment",
            "Scarabs" => "Metadata/Chests/BlightScarab",
            "Fossils" => "Metadata/Chests/BlightDelve",
            "DivinationCards" => "Metadata/Chests/BlightDivination",
            "Legion" => "Metadata/Chests/BlightLegion",
            _ => "Metadata/Chests/BlightGeneric",
        };
        var matching = BlightTables.Chests.Where(c => c.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count > 0) return matching[rng.Next(matching.Count)].Id;
        var generic = BlightTables.Chests.Where(c => !c.Id.Contains("BlightTowerChest", StringComparison.OrdinalIgnoreCase)).ToList();
        if (generic.Count > 0) return generic[rng.Next(generic.Count)].Id;
        return BlightTables.BlightChestBaseType;
    }

    static QuestObjectTypes.Info? ResolveChestInfo(string chestType)
    {
        var info = QuestObjectTypes.Find(chestType) ?? QuestObjectTypes.Find(BlightTables.BlightChestBaseType);
        return info is not null && QuestObject.CanSerialize(info.Components) ? info : null;
    }

    static QuestObject BuildChestObject(BlightChestState chest, QuestObjectTypes.Info info) =>
        new(chest.X, chest.Y, info.Components, 0f, null, null,
            TransitionState: (byte)(chest.Opened ? Math.Max(2, info.States) : 1), Opened: chest.Opened);

    /// <summary>Computes chest count for one lane honoring Blighted/Blight-ravaged Map IIQ scaling (25% / 50%),
    /// Violet Oil / Atlas additional chest chance, and flat additional lane chests.</summary>
    public static int ComputeLaneChestCount(Defence d, IReadOnlyDictionary<uint, int> areaStats, Random rng)
    {
        int baseCount = d.Blighted || d.UberBlighted ? BlightedChestsPerLane : ChestsPerLane;
        int flatExtra = Math.Max(0, areaStats.GetValueOrDefault(Stat.MapBlightLaneAdditionalChests));
        int extraPct = Math.Max(0, areaStats.GetValueOrDefault(Stat.MapBlightLaneAdditionalChestChancePct));
        // Blighted Maps apply 25% of map IIQ to chest count; Blight-ravaged Maps apply 50%!
        if (d.Blighted || d.UberBlighted)
        {
            int mapIiq = Math.Max(0, areaStats.GetValueOrDefault(Stat.MapItemDropQuantityPlusPct));
            int scaledIiqPct = d.UberBlighted ? mapIiq / 2 : mapIiq / 4;
            extraPct += scaledIiqPct;
        }
        int total = baseCount + flatExtra + (extraPct / 100);
        if (extraPct % 100 > 0 && rng.Next(100) < (extraPct % 100)) total++;
        return Math.Max(1, total);
    }

    /// <summary>Spawns victory reward chests (standard/Blighted lane chests, Blightheart GigaChest, or Templar Ingenuity
    /// salvaged tower chests) and bursts banked pump lootsplosion drops around the pump.</summary>
    public static async Task SpawnVictoryChests(Player player, Defence d,
        Func<Player, Func<Player, Task>, Task> shared,
        Func<Player, IReadOnlyList<Player>> membersOf)
    {
        if (d.Plan is not { } plan || plan.Lanes.Count == 0) return;
        var rng = AreaRng(player);
        var areaStats = BlightEncounter.AreaStats(player);
        bool giga = areaStats.GetValueOrDefault(Stat.MapGigaBlightEncounters) > 0;
        bool towersAreChests = areaStats.GetValueOrDefault(Stat.MapBlightTowersAreChests) > 0;
        int luckyBudget = Math.Max(0, areaStats.GetValueOrDefault(Stat.BlightedMapChestRewardLuckyCount));
        var created = new List<BlightChestState>();
        List<Item> bankedLoot;

        lock (d)
        {
            bankedLoot = [.. d.BankedPumpLoot];
            d.BankedPumpLoot.Clear();
            if (d.Chests.Count > 0) return;

            if (giga)
            {
                // Blight Scarab of the Blightheart: replaces standard lane chests with one giant Blighted Chest scaling with kills!
                var cell = player.Layout.Near((d.Cell.X + 6, d.Cell.Y + 6));
                var gigaChest = new BlightChestState
                {
                    Id = player.Instance.World.TakeObjectId(),
                    Lane = 0,
                    X = cell.X,
                    Y = cell.Y,
                    ChestType = PickChestType("Blight", d.UberBlighted, rng),
                    RewardId = Program.LeagueFor(player).HardMode ? "HardModeBlight" : "Blight",
                    IsLucky = luckyBudget > 0,
                    IsGiga = true,
                };
                d.Chests[gigaChest.Id] = gigaChest;
                created.Add(gigaChest);
            }
            else
            {
                int bonusBossChests = d.ExtraBossChests;
                for (int lane = 0; lane < plan.Lanes.Count; lane++)
                {
                    var end = plan.Lanes[lane].End;
                    string rewardId = d.LaneRewards.GetValueOrDefault(lane, "Generic");
                    int perLane = ComputeLaneChestCount(d, areaStats, rng);
                    if (bonusBossChests > 0 && d.BossLanes.Contains(lane))
                    {
                        perLane++;
                        bonusBossChests--;
                    }
                    for (int k = 0; k < perLane; k++)
                    {
                        double angle = k * (Math.Tau / Math.Max(1, perLane)) + lane * 0.7;
                        var cell = player.Layout.Near((end.X + (int)Math.Round(Math.Cos(angle) * 5), end.Y + (int)Math.Round(Math.Sin(angle) * 5)));
                        var chest = new BlightChestState
                        {
                            Id = player.Instance.World.TakeObjectId(),
                            Lane = lane,
                            X = cell.X,
                            Y = cell.Y,
                            ChestType = PickChestType(rewardId, d.UberBlighted, rng),
                            RewardId = rewardId,
                        };
                        d.Chests[chest.Id] = chest;
                        created.Add(chest);
                    }
                }
                // Distribute Lucky chest budget randomly across spawned lane chests (Azure/Crimson/Opalescent Oil + Epidemiology).
                if (luckyBudget > 0 && created.Count > 0)
                {
                    var indices = Enumerable.Range(0, created.Count).OrderBy(_ => rng.Next()).Take(Math.Min(luckyBudget, created.Count));
                    foreach (int idx in indices) created[idx].IsLucky = true;
                }
            }

            // Atlas Notable Templar Ingenuity (map_blight_towers_are_chests): built towers become Salvaged Tower Chests (Rank 1..4)!
            if (towersAreChests)
            {
                foreach (var spot in d.Spots.Values)
                {
                    if (spot.Row <= 0 || spot.Row >= BlightTables.Towers.Count) continue;
                    int rank = Math.Clamp(BlightTowers.TierNumberOf(BlightTables.Towers[spot.Row]), 1, 4);
                    var cell = player.Layout.Near((spot.X, spot.Y));
                    var salvaged = new BlightChestState
                    {
                        Id = player.Instance.World.TakeObjectId(),
                        Lane = -1,
                        X = cell.X,
                        Y = cell.Y,
                        ChestType = BlightTables.SalvagedTowerChests[rank - 1],
                        RewardId = "Blight",
                        IsSalvagedTower = true,
                        SalvagedRank = rank,
                    };
                    d.Chests[salvaged.Id] = salvaged;
                    created.Add(salvaged);
                }
            }
        }

        foreach (var chest in created)
            await ShowChest(player, chest, shared, membersOf);

        // Pump victory lootsplosion: burst banked monster drops around the Blight pump!
        if (bankedLoot.Count > 0 && GameEvents.AllowsLoot(player) && Environment.GetEnvironmentVariable("Game_DROPS") != "0")
            await DropItemsAround(player, d.Cell.X, d.Cell.Y, bankedLoot, "Blight pump lootsplosion", rng);
    }

    /// <summary>Creates a chest object in the area (and blocks walking on it).</summary>
    public static async Task ShowChest(Player player, BlightChestState chest,
        Func<Player, Func<Player, Task>, Task> shared,
        Func<Player, IReadOnlyList<Player>> membersOf)
    {
        if (player.Area.Objects.Has(chest.Id)) return;
        if (ResolveChestInfo(chest.ChestType) is not { } info) return;
        byte[] blob = BuildChestObject(chest, info).Blob();
        uint hash = InstanceEntities.ObjectTypeHash(chest.ChestType);
        await shared(player, member => SendWorldCreate(member, chest.Id, () => InstanceEntities.CreateObject(chest.Id, hash, blob),
            $"0x02bb Blight chest 0x{chest.Id:x} ({chest.RewardId}{(chest.IsGiga ? ", Giga" : "")}{(chest.IsLucky ? ", Lucky" : "")}) at ({chest.X}, {chest.Y})",
            wake: true, awaitBirth: false));
        if (info.Blocking)
            foreach (var member in membersOf(player))
                member.SetObjectBlocker(chest.Id, chest.X, chest.Y, Math.Max(1, info.Size), info.ActorOrChest);
    }

    /// <summary>Handles clicking an unopened Blight reward chest: walks up, opens it, drops themed loot, and handles
    /// Bounty / Epidemiology re-closing (<c>map_blight_chest_close_after_opening_chance_%</c>).</summary>
    public static async Task<bool> UseChest(Player player, Run run, InstanceTransfer.UseSkillOnObject use, double requestedAt,
        Func<Player, Func<Player, Task>, Task> shared,
        Func<Player, IReadOnlyList<Player>> membersOf,
        Action<Player, Run> save)
    {
        Defence? owner = null;
        BlightChestState? chest = null;
        lock (run.Gate)
            foreach (var d in run.Defences)
                lock (d)
                    if (d.Chests.TryGetValue(use.TargetId, out var c)) { owner = d; chest = c; break; }
        if (owner is null || chest is null) return false;
        if (chest.Opened || player.Closed || player.LeavingArea || player.PlayerLife == 0 || InGracePeriod(player)) return true;

        var info = ResolveChestInfo(chest.ChestType);
        int reach = Math.Max(2, info?.Size ?? 2);
        if (!TryBeginObjectWalk(player, requestedAt, chest.X, chest.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, chest.X, chest.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: chest.Id), null,
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (chest.X, chest.Y), reach)) return true;

            var rng = AreaRng(player);
            var areaStats = BlightEncounter.AreaStats(player);
            int reopenChance = Math.Clamp(areaStats.GetValueOrDefault(Stat.MapBlightChestCloseAfterOpeningChancePct), 0, 80);

            uint oldId = chest.Id;
            BlightChestState nextState;
            bool reopened;
            lock (owner)
            {
                if (!owner.Chests.TryGetValue(oldId, out var live) || live.Opened) return true;
                reopened = reopenChance > 0 && live.ReopenCount < MaxReopensPerChest && rng.Next(100) < reopenChance;
                owner.Chests.Remove(oldId);
                nextState = new BlightChestState
                {
                    Id = player.Instance.World.TakeObjectId(),
                    Lane = live.Lane,
                    X = live.X,
                    Y = live.Y,
                    ChestType = live.ChestType,
                    RewardId = live.RewardId,
                    Opened = !reopened,
                    IsLucky = live.IsLucky,
                    IsGiga = live.IsGiga,
                    IsSalvagedTower = live.IsSalvagedTower,
                    SalvagedRank = live.SalvagedRank,
                    ReopenCount = reopened ? live.ReopenCount + 1 : live.ReopenCount,
                };
                owner.Chests[nextState.Id] = nextState;
            }
            foreach (var member in membersOf(player)) member.RemoveBlocker(oldId);
            await shared(player, member => SendWorldRemoval(member, oldId, $"Blight chest 0x{oldId:x} opened{(reopened ? " (re-closed by Bounty/Epidemiology)" : "")}"));
            await ShowChest(player, nextState, shared, membersOf);
            await DropChestLoot(player, owner, nextState);
            save(player, run);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>Rolls items for an opened Blight chest (supports Lucky double-rolls, GigaChest kill scaling,
    /// Salvaged Tower ranks, Oil Tier Upgrades, Blooming Blight-ravaged maps, Oil Extractors, and Blight Uniques).</summary>
    public static List<Item> RollChestItems(Player player, string rewardId, int level, Random rng,
        bool lucky = false, bool uberBlighted = false, bool giga = false, int gigaKills = 0, int salvagedRank = 0)
    {
        var areaStats = BlightEncounter.AreaStats(player);
        int rollsCount = 1 + Math.Max(0, areaStats.GetValueOrDefault(Stat.MapBlightChestsRepeatDropsCount));
        if (giga)
        {
            int bonus = areaStats.GetValueOrDefault(Stat.MapBlightGigaChestAdditionalRewards);
            rollsCount = Math.Clamp(3 + gigaKills / 15 + Math.Max(0, bonus), 3, 18);
        }
        else if (salvagedRank > 0)
        {
            rollsCount = Math.Clamp(salvagedRank, 1, 4);
        }

        var total = new List<Item>();
        for (int r = 0; r < rollsCount; r++)
        {
            var first = RollSingleChestPass(player, rewardId, level, areaStats, uberBlighted, rng);
            if (lucky)
            {
                var second = RollSingleChestPass(player, rewardId, level, areaStats, uberBlighted, rng);
                total.AddRange(ScoreLoot(second, player) > ScoreLoot(first, player) ? second : first);
            }
            else total.AddRange(first);
        }

        // Duplicate reward chance (map_blight_chest_duplicate_rewards_chance_permillage).
        int dupPerm = areaStats.GetValueOrDefault(Stat.MapBlightChestDuplicateRewardsChancePermillage);
        if (dupPerm > 0 && rng.Next(1000) < dupPerm)
            total.AddRange(total.Select(i => i with { }));

        return total;
    }

    static int ScoreLoot(IReadOnlyList<Item> items, Player player)
    {
        var catalog = ItemsFor(player);
        int score = items.Count * 10;
        foreach (var item in items)
        {
            if (item.Rarity == ItemRarity.Unique) score += 200;
            else if (item.Rarity == ItemRarity.Rare) score += 25;
            if (catalog.ByHash(item.TypeHash) is { } bt)
            {
                if (BlightTables.OilByItemId.TryGetValue(bt.Id, out var oil)) score += oil.Tier * oil.Tier * 15;
                if (item.Mods.Any(m => m.Hash == BlightTables.UberInfectedMapModHash)) score += 500;
                else if (item.Mods.Any(m => m.Hash == BlightTables.InfectedMapModHash)) score += 250;
            }
        }
        return score;
    }

    static List<Item> RollSingleChestPass(Player player, string rewardId, int level,
        IReadOnlyDictionary<uint, int> areaStats, bool uberBlighted, Random rng)
    {
        var items = new List<Item>();
        var catalog = ItemsFor(player);
        var drops = DropsFor(player);
        var rolls = RollsFor(player);
        var league = Program.LeagueFor(player);

        string kind = rewardId.Replace("HardMode", "", StringComparison.Ordinal);
        int extraDropChance = areaStats.GetValueOrDefault(Stat.MapBlightChestPctChanceForAdditionalDrop);
        int baseCount = 3 + (extraDropChance > 0 && rng.Next(100) < extraDropChance ? 1 : 0);

        switch (kind)
        {
            case "Blight":
            {
                // Oils from BlightCraftingItems weighted by tier, area level, Blight-ravaged bonus, and Atlas Oil tier-up chance.
                int oilBonusPct = areaStats.GetValueOrDefault(Stat.MapBlightChestOilChancePlusPct);
                int tierUpChance = areaStats.GetValueOrDefault(Stat.MapBlightOilsChanceToDropATierHigherPct);
                int oilRolls = rng.Next(1, 4) + (uberBlighted ? 2 : 0) + (oilBonusPct > 0 && rng.Next(100) < oilBonusPct ? 1 : 0);
                for (int i = 0; i < oilRolls; i++)
                    if (RollOil(catalog, level, rng, tierUpChance, uberBlighted) is { } oil) items.Add(oil);

                // Oil Extractor chance (map_blight_oil_chests_drop_oil_extractor_chance_% + small natural chance in maps).
                int extractorChance = (level >= 68 ? 4 : 0) + areaStats.GetValueOrDefault(Stat.MapBlightOilChestsDropOilExtractorChancePct);
                if (extractorChance > 0 && rng.Next(100) < extractorChance && catalog.Find(OilExtractorId) is { Supported: true } extBt)
                    items.Add(ItemCatalog.Create(extBt) with { ItemLevel = (byte)Math.Clamp(level, 1, 100), StackSize = 1 });

                // Chance for a Blighted Map (or Blight-ravaged Map if Blooming + T14+, or inside Blighted/Ravaged maps!).
                int blightedMapChance = 20 + areaStats.GetValueOrDefault(Stat.MapBlightChestBlightedMapChancePlusPct) / 5
                    + areaStats.GetValueOrDefault(Stat.MapBlightEncounterBlightedMapRewardChancePlusPct) / 5;
                if (level >= 68 && rng.Next(100) < Math.Clamp(blightedMapChance, 5, 95)
                    && RollBlightedMap(player, level, areaStats, uberBlighted, rng) is { } bmap)
                    items.Add(bmap);

                // Blight Uniques (and Stranglegasp exclusively in Blight-ravaged maps!).
                if (level >= 68 && RollBlightUnique(player, level, uberBlighted, rng) is { } unique)
                    items.Add(unique);
                break;
            }
            case "Currency":
            {
                var candidates = RewardCurrency
                    .Select(c => (Type: catalog.Find(c.Id), c.Weight))
                    .Where(x => x.Type is { Supported: true } bt && bt.DropLevel <= level)
                    .ToList();
                double total = candidates.Sum(x => x.Weight);
                for (int i = 0; i < baseCount && candidates.Count > 0; i++)
                {
                    double pick = rng.NextDouble() * total;
                    var chosen = candidates[0];
                    foreach (var c in candidates) { chosen = c; if ((pick -= c.Weight) < 0) break; }
                    int stack = chosen.Weight >= 50 ? rng.Next(1, 3) : 1;
                    items.Add(ItemCatalog.Create(chosen.Type!) with { ItemLevel = (byte)level, StackSize = (ushort)Math.Min(chosen.Type!.MaxStack, stack) });
                }
                break;
            }
            case "Uniques":
            {
                var uniques = UniquesFor(player);
                var eligible = uniques.All
                    .Where(u => drops.IsDropEligibleUnique(u, level) && catalog.Find(u.BaseTypeId) is { } bt && ItemDrops.Droppable(bt) && bt.DropLevel <= level)
                    .ToList();
                for (int i = 0; i < Math.Max(1, baseCount - 1) && eligible.Count > 0; i++)
                    if (drops.PickUnique(eligible, rng) is { } u && uniques.Create(u, rng) is { } item)
                        items.Add(item with { ItemLevel = (byte)level, Identified = false });
                break;
            }
            case "Maps":
            {
                int tier = level >= 68 ? Math.Clamp(level - 67, 1, 16) : 0;
                if (tier > 0)
                {
                    for (int i = 0; i < baseCount; i++)
                        if (RollMapDropForKill(player, level, MonsterRarity.Rare, tier, rng, 20) is { } map)
                            items.Add(map.Item);
                }
                if (items.Count == 0) items.AddRange(drops.Container(level, baseCount, 100, rng).Select(d => d.Item));
                break;
            }
            case "Scarabs":
            {
                var mapMods = MapModsOf(player);
                for (int i = 0; i < baseCount; i++)
                    if (drops.Scarab(rng, level, mapMods) is { } scarab) items.Add(scarab.Item with { ItemLevel = (byte)level });
                if (items.Count == 0) items.AddRange(drops.Container(level, baseCount, 100, rng).Select(d => d.Item));
                break;
            }
            case "DivinationCards":
            {
                for (int i = 0; i < baseCount; i++)
                    if (drops.Card(rng, level) is { } card) items.Add(card.Item);
                if (items.Count == 0) items.AddRange(drops.Container(level, baseCount, 100, rng).Select(d => d.Item));
                break;
            }
            case "Trinkets":
            case "Armour":
            case "Weapon":
            {
                Func<ItemCatalog.BaseType, bool> accepts = kind switch
                {
                    "Trinkets" => t => t.Class is "Ring" or "Amulet" or "Belt",
                    "Armour" => t => t.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield",
                    _ => t => ItemDrops.EquipmentClasses.Contains(t.Class) && (t.Components.Contains("Weapon") || t.Class == "Quiver"),
                };
                foreach (var drop in drops.Container(level, baseCount, 300, rng, accepts))
                {
                    var item = drop.Item;
                    if (!league.HardMode && item.Rarity == ItemRarity.Normal && drop.Type.Components.Contains("Mods"))
                        item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
                    items.Add(item);
                }
                break;
            }
            case "Essences":
            case "Essences2":
            case "Fossils":
            case "Gems":
            case "Breach":
            case "Abyss":
            case "Harbinger":
            case "Fragments":
            case "Legion":
            {
                var pool = catalog.All.Where(t => t.Supported && t.DropLevel >= 0 && t.DropLevel <= level && !t.Components.Contains("Quest") && MatchesPool(kind, t)).ToList();
                for (int i = 0; i < baseCount && pool.Count > 0; i++)
                {
                    var bt = pool[rng.Next(pool.Count)];
                    items.Add(ItemCatalog.Create(bt) with { ItemLevel = (byte)level });
                }
                if (items.Count == 0) items.AddRange(drops.Container(level, baseCount, 100, rng).Select(d => d.Item));
                break;
            }
            default:
                items.AddRange(drops.Container(level, baseCount, 100, rng).Select(d => d.Item));
                break;
        }
        return items;
    }

    static bool MatchesPool(string kind, ItemCatalog.BaseType t) => kind switch
    {
        "Essences" or "Essences2" => t.Id.Contains("/Currency/CurrencyEssence", StringComparison.Ordinal) && !t.Name.StartsWith("Deafening ", StringComparison.Ordinal),
        "Fossils" => t.Id.Contains("/CurrencyDelveCrafting", StringComparison.Ordinal),
        "Gems" => t.Id.StartsWith("Metadata/Items/Gems/SkillGem", StringComparison.Ordinal),
        "Breach" => t.Id.Contains("/Currency/CurrencyBreach", StringComparison.Ordinal) && t.Id.EndsWith("Shard", StringComparison.Ordinal),
        "Abyss" => t.Id.EndsWith("/BeltAbyss", StringComparison.Ordinal) || t.Id.Contains("/JewelAbyss", StringComparison.Ordinal),
        "Harbinger" => t.Id.Contains("/Currency", StringComparison.Ordinal) && t.Id.EndsWith("Shard", StringComparison.Ordinal),
        "Fragments" => t.Id.Contains("/CurrencyVaalFragment1_", StringComparison.Ordinal) || t.Id.EndsWith("/CurrencyOfferingToTheGoddess", StringComparison.Ordinal),
        "Legion" => t.Id.Contains("/CurrencyLegion", StringComparison.Ordinal) && t.Id.EndsWith("Shard", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Rolls a tier-weighted Oil from BlightCraftingItems eligible for the area level (Clear..Golden, plus
    /// Tainted/Reflective/Prismatic in Blight-ravaged maps), honoring <c>map_blight_oils_chance_to_drop_a_tier_higher_%</c>.</summary>
    public static Item? RollOil(ItemCatalog catalog, int level, Random rng, int tierUpgradeChancePct = 0, bool uberBlighted = false)
    {
        // In Blight-ravaged maps, small chance to roll special oils (Tainted/Reflective/Prismatic).
        if (uberBlighted && rng.Next(100) < 6)
        {
            var specials = BlightTables.Oils.Where(o => o.UseType is 1 or 2 || o.Id.Contains("Prismatic", StringComparison.OrdinalIgnoreCase)).ToList();
            if (specials.Count > 0 && catalog.Find(specials[rng.Next(specials.Count)].Id) is { Supported: true } sBt)
                return ItemCatalog.Create(sBt) with { ItemLevel = (byte)Math.Clamp(level, 1, 100), StackSize = 1 };
        }

        int maxTier = level switch
        {
            < 15 => 3,
            < 35 => 5,
            < 55 => 7,
            < 68 => 9,
            < 75 => 11,
            < 80 => 12,
            _ => 13,
        };
        var eligible = new List<(BlightTables.OilItem Oil, ItemCatalog.BaseType Type, int Weight)>();
        int total = 0;
        foreach (var oil in BlightTables.Oils)
        {
            if (oil.Tier <= 0 || oil.Tier > maxTier || oil.UseType != 0) continue;
            if (catalog.Find(oil.Id) is not { Supported: true } bt) continue;
            int shift = uberBlighted ? Math.Clamp(11 - oil.Tier, 0, 10) : Math.Clamp(13 - oil.Tier, 0, 12);
            int weight = Math.Max(1, 1 << shift);
            eligible.Add((oil, bt, weight));
            total += weight;
        }
        if (eligible.Count == 0) return null;
        int roll = rng.Next(total);
        var picked = eligible[0];
        foreach (var entry in eligible)
            if ((roll -= entry.Weight) < 0) { picked = entry; break; }

        // Oil tier upgrade (map_blight_oils_chance_to_drop_a_tier_higher_%): upgrade to Tier + 1 up to Golden (13).
        var finalBt = picked.Type;
        if (tierUpgradeChancePct > 0 && picked.Oil.Tier < 13 && rng.Next(100) < tierUpgradeChancePct)
        {
            var higher = BlightTables.Oils.FirstOrDefault(o => o.UseType == 0 && o.Tier == picked.Oil.Tier + 1);
            if (higher is not null && catalog.Find(higher.Id) is { Supported: true } upBt) finalBt = upBt;
        }
        return ItemCatalog.Create(finalBt) with { ItemLevel = (byte)Math.Clamp(level, 1, 100), StackSize = 1 };
    }

    /// <summary>Creates a Blighted or Blight-ravaged map from an existing map item (adding InfectedMap or UberInfectedMap__).</summary>
    public static Item MakeBlightedMap(Item mapItem, bool uber, bool randomAnoints, Random rng)
    {
        ushort modHash = uber && BlightTables.UberInfectedMapModHash != 0
            ? BlightTables.UberInfectedMapModHash
            : BlightTables.InfectedMapModHash;
        if (modHash == 0) return mapItem;
        if (mapItem.Mods.Any(m => m.Hash == BlightTables.InfectedMapModHash || m.Hash == BlightTables.UberInfectedMapModHash))
            return mapItem;
        int[] values = uber ? [9, 20, 200, 85, 1, 1, 1] : [1, 1, 1];
        var mods = new List<ItemMod> { new(ModSection.Implicit, modHash, values) };
        if (randomAnoints)
        {
            var mapRecipes = BlightTables.Recipes.Where(r => r.CraftingType == 2 && r.ModHash != 0).ToList();
            if (mapRecipes.Count > 0)
            {
                int count = rng.Next(1, 4);
                for (int i = 0; i < count; i++)
                {
                    var rec = mapRecipes[rng.Next(mapRecipes.Count)];
                    mods.Add(new ItemMod(ModSection.Enchant, rec.ModHash, [1, 1, 1]));
                }
            }
        }
        mods.AddRange(mapItem.Mods);
        return mapItem with { Mods = mods };
    }

    /// <summary>Rolls a Blighted Map (or Blight-ravaged Map if Scarab of Blooming + T14+, or inside a Blighted/Ravaged T14+ map).</summary>
    public static Item? RollBlightedMap(Player player, int level, IReadOnlyDictionary<uint, int> areaStats, bool fromUber, Random rng)
    {
        if (BlightTables.InfectedMapModHash == 0) return null;
        int tier = Math.Clamp(level - 67, 1, 16);
        var catalog = ItemsFor(player);
        var mapType = catalog.Find($"Metadata/Items/Maps/MapKeyTier{tier}")
            ?? catalog.All.FirstOrDefault(t => t.Supported && CurrencyEffects.IsMap(t));
        if (mapType is null || !mapType.Supported) return null;
        var baseMap = ItemCatalog.Create(mapType) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) };
        bool blooming = areaStats.GetValueOrDefault(Stat.MapBlightMapsDropAsUberBlightMaps) > 0;
        bool dropUber = tier >= 14 && (blooming || (fromUber && rng.Next(100) < 30));
        bool randomAnoints = areaStats.GetValueOrDefault(Stat.MapBlightedMapsDropWithRandomAnoints) > 0;
        return MakeBlightedMap(baseMap, dropUber, randomAnoints, rng);
    }

    static Item? RollBlightUnique(Player player, int level, bool uberBlighted, Random rng)
    {
        var uniques = UniquesFor(player);
        // Stranglegasp is Blight-ravaged exclusive!
        if (uberBlighted && rng.Next(100) < 5)
        {
            var strangle = uniques.All.FirstOrDefault(u => u.Name.Equals("Stranglegasp", StringComparison.OrdinalIgnoreCase));
            if (strangle is not null && uniques.Create(strangle, rng) is { } sItem)
                return sItem with { ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = false };
        }
        if (rng.Next(100) < (uberBlighted ? 12 : 4))
        {
            string pick = BlightUniqueNames[rng.Next(BlightUniqueNames.Length)];
            var u = uniques.All.FirstOrDefault(x => x.Name.Equals(pick, StringComparison.OrdinalIgnoreCase));
            if (u is not null && uniques.Create(u, rng) is { } uItem)
                return uItem with { ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = false };
        }
        return null;
    }

    static async Task DropChestLoot(Player player, Defence d, BlightChestState chest)
    {
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rng = AreaRng(player);
        int level = Math.Clamp((int)d.Level, 1, 100);
        int kills;
        bool uber;
        lock (d) { kills = d.KilledCount; uber = d.UberBlighted; }
        var items = RollChestItems(player, chest.RewardId, level, rng,
            lucky: chest.IsLucky, uberBlighted: uber, giga: chest.IsGiga, gigaKills: kills, salvagedRank: chest.SalvagedRank);
        await DropItemsAround(player, chest.X, chest.Y, items, $"Blight chest 0x{chest.Id:x} ({chest.RewardId})", rng);
    }

    static async Task DropItemsAround(Player player, int x, int y, IReadOnlyList<Item> items, string label, Random rng)
    {
        for (int i = 0; i < items.Count; i++)
        {
            double angle = i * 2.399963229728653 + rng.NextDouble();
            int dist = 3 + (i % 5);
            var at = player.Layout.Near((x + (int)Math.Round(Math.Cos(angle) * dist), y + (int)Math.Round(Math.Sin(angle) * dist)));
            var ground = new WorldItemObject(at.X, at.Y, items[i]);
            uint id = player.Instance.Ground.Add(ground, player);
            string name = ItemsFor(player).ByHash(items[i].TypeHash)?.Name ?? "item";
            await SendGroundItem(player, id, ground, $"{label} drops {name} x{items[i].StackSize}", justDropped: true);
        }
    }
}
