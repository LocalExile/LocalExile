using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>One spawned Blight reward chest at a lane's end (cyst, pustule or sack).</summary>
public sealed class BlightChestState
{
    public uint Id;
    public int Lane;
    public int X, Y;
    public string ChestType = "";
    public string RewardId = "";
    public bool Opened;
}

/// <summary>
/// Spawns end-of-lane Blight reward chests (<c>BlightChestTypes</c> / <c>BlightRewardTypes</c>) when a defence is won,
/// handles opening them on click, and rolls category-matched loot including Blight Oils (<c>BlightCraftingItems</c>) and
/// Blighted Maps (<c>InfectedMap</c>).
/// </summary>
public static class BlightChests
{
    /// <summary>Chests spawned per lane on victory (2 in regular maps/campaign, 3 in Blighted maps).</summary>
    public const int ChestsPerLane = 2, BlightedChestsPerLane = 3;

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

    /// <summary>Assigns a reward category Id per lane (guaranteeing at least one Blight oil/map lane).</summary>
    public static Dictionary<int, string> PickLaneRewards(int laneCount, bool hardMode, Random rng)
    {
        var result = new Dictionary<int, string>();
        if (laneCount <= 0) return result;
        var table = hardMode ? HardModeRewards : StandardRewards;
        int totalWeight = table.Sum(x => x.Weight);
        int guaranteedBlightLane = rng.Next(laneCount);
        for (int i = 0; i < laneCount; i++)
        {
            if (i == guaranteedBlightLane)
            {
                result[i] = hardMode ? "HardModeBlight" : "Blight";
                continue;
            }
            int roll = rng.Next(totalWeight);
            string picked = table[0].Id;
            foreach (var (id, w) in table)
            {
                if ((roll -= w) < 0) { picked = id; break; }
            }
            result[i] = picked;
        }
        return result;
    }

    /// <summary>Picks a BlightChestTypes Chests.Id matching a reward category.</summary>
    public static string PickChestType(string rewardId, Random rng)
    {
        string key = rewardId.Replace("HardMode", "", StringComparison.Ordinal);
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
            "Blight" => "Metadata/Chests/BlightTowerChest",
            _ => "Metadata/Chests/BlightGeneric",
        };
        var matching = BlightTables.Chests.Where(c => c.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count > 0) return matching[rng.Next(matching.Count)].Id;
        if (BlightTables.Chests.Count > 0) return BlightTables.Chests[rng.Next(BlightTables.Chests.Count)].Id;
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

    /// <summary>Spawns end-of-lane reward chests when the defence succeeds.</summary>
    public static async Task SpawnVictoryChests(Player player, Defence d,
        Func<Player, Func<Player, Task>, Task> shared,
        Func<Player, IReadOnlyList<Player>> membersOf)
    {
        if (d.Plan is not { } plan || plan.Lanes.Count == 0) return;
        var rng = AreaRng(player);
        int perLane = d.Blighted ? BlightedChestsPerLane : ChestsPerLane;
        var created = new List<BlightChestState>();
        lock (d)
        {
            if (d.Chests.Count > 0) return;
            for (int lane = 0; lane < plan.Lanes.Count; lane++)
            {
                var end = plan.Lanes[lane].End;
                string rewardId = d.LaneRewards.GetValueOrDefault(lane, "Generic");
                for (int k = 0; k < perLane; k++)
                {
                    double angle = k * (Math.Tau / perLane) + lane * 0.7;
                    var cell = player.Layout.Near((end.X + (int)Math.Round(Math.Cos(angle) * 5), end.Y + (int)Math.Round(Math.Sin(angle) * 5)));
                    var chest = new BlightChestState
                    {
                        Id = player.Instance.World.TakeObjectId(),
                        Lane = lane,
                        X = cell.X,
                        Y = cell.Y,
                        ChestType = PickChestType(rewardId, rng),
                        RewardId = rewardId,
                    };
                    d.Chests[chest.Id] = chest;
                    created.Add(chest);
                }
            }
        }
        foreach (var chest in created)
            await ShowChest(player, chest, shared, membersOf);
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
            $"0x02bb Blight chest 0x{chest.Id:x} ({chest.RewardId}) at ({chest.X}, {chest.Y})", wake: true, awaitBirth: false));
        if (info.Blocking)
            foreach (var member in membersOf(player))
                member.SetObjectBlocker(chest.Id, chest.X, chest.Y, Math.Max(1, info.Size), info.ActorOrChest);
    }

    /// <summary>Handles clicking an unopened Blight reward chest: walks up, opens it and drops themed loot.</summary>
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

            uint oldId = chest.Id;
            BlightChestState opened;
            lock (owner)
            {
                if (!owner.Chests.TryGetValue(oldId, out var live) || live.Opened) return true;
                owner.Chests.Remove(oldId);
                opened = new BlightChestState
                {
                    Id = player.Instance.World.TakeObjectId(),
                    Lane = live.Lane,
                    X = live.X,
                    Y = live.Y,
                    ChestType = live.ChestType,
                    RewardId = live.RewardId,
                    Opened = true,
                };
                owner.Chests[opened.Id] = opened;
            }
            foreach (var member in membersOf(player)) member.RemoveBlocker(oldId);
            await shared(player, member => SendWorldRemoval(member, oldId, $"Blight chest 0x{oldId:x} opened"));
            await ShowChest(player, opened, shared, membersOf);
            await DropChestLoot(player, owner, opened);
            save(player, run);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>Rolls items for an opened Blight chest (exposed internally for unit tests).</summary>
    public static List<Item> RollChestItems(Player player, string rewardId, int level, Random rng)
    {
        var items = new List<Item>();
        var catalog = ItemsFor(player);
        var drops = DropsFor(player);
        var rolls = RollsFor(player);
        var league = Program.LeagueFor(player);
        var areaStats = AreaLeagueStats(player);

        string kind = rewardId.Replace("HardMode", "", StringComparison.Ordinal);
        int extraDropChance = areaStats.GetValueOrDefault(Stat.MapBlightChestPctChanceForAdditionalDrop);
        int baseCount = 3 + (extraDropChance > 0 && rng.Next(100) < extraDropChance ? 1 : 0);

        switch (kind)
        {
            case "Blight":
            {
                // Oils from BlightCraftingItems weighted by tier & area level.
                int oilBonusPct = areaStats.GetValueOrDefault(Stat.MapBlightChestOilChancePlusPct);
                int oilRolls = rng.Next(1, 4) + (oilBonusPct > 0 && rng.Next(100) < oilBonusPct ? 1 : 0);
                for (int i = 0; i < oilRolls; i++)
                    if (RollOil(catalog, level, rng) is { } oil) items.Add(oil);

                // Chance for a Blighted Map in maps (area level >= 68).
                int blightedMapChance = 20 + areaStats.GetValueOrDefault(Stat.MapBlightChestBlightedMapChancePlusPct) / 5;
                if (level >= 68 && rng.Next(100) < Math.Clamp(blightedMapChance, 5, 95)
                    && RollBlightedMap(player, level, rng) is { } bmap)
                    items.Add(bmap);
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

    /// <summary>Rolls a tier-weighted Oil from BlightCraftingItems eligible for the area level (Clear..Golden).</summary>
    public static Item? RollOil(ItemCatalog catalog, int level, Random rng)
    {
        // Standard oils have Tier 1..13 (Clear=1 .. Golden=13) and UseType 0; higher tiers unlock progressively with area level.
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
        var eligible = new List<(ItemCatalog.BaseType Type, int Weight)>();
        int total = 0;
        foreach (var oil in BlightTables.Oils)
        {
            if (oil.Tier <= 0 || oil.Tier > maxTier || oil.UseType != 0) continue;
            if (catalog.Find(oil.Id) is not { Supported: true } bt) continue;
            // Higher tiers are exponentially rarer (Clear=4096 down to Golden=1).
            int weight = Math.Max(1, 1 << Math.Clamp(13 - oil.Tier, 0, 12));
            eligible.Add((bt, weight));
            total += weight;
        }
        if (eligible.Count == 0) return null;
        int roll = rng.Next(total);
        var picked = eligible[0].Type;
        foreach (var (bt, w) in eligible)
            if ((roll -= w) < 0) { picked = bt; break; }
        return ItemCatalog.Create(picked) with { ItemLevel = (byte)Math.Clamp(level, 1, 100), StackSize = 1 };
    }

    /// <summary>Rolls a Blighted Map (regular map carrying the InfectedMap implicit mod).</summary>
    static Item? RollBlightedMap(Player player, int level, Random rng)
    {
        if (BlightTables.InfectedMapModHash == 0) return null;
        int tier = Math.Clamp(level - 67, 1, 16);
        if (RollMapDropForKill(player, level, MonsterRarity.Normal, tier, rng, 20) is not { } map) return null;
        var infectedMod = new ItemMod(ModSection.Implicit, BlightTables.InfectedMapModHash, [1, 1, 1]);
        var existing = map.Item.Mods;
        return map.Item with { Mods = [infectedMod, .. existing] };
    }

    static async Task DropChestLoot(Player player, Defence d, BlightChestState chest)
    {
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rng = AreaRng(player);
        int level = Math.Clamp((int)d.Level, 1, 100);
        var items = RollChestItems(player, chest.RewardId, level, rng);
        for (int i = 0; i < items.Count; i++)
        {
            double angle = i * 2.399963229728653 + rng.NextDouble();
            var at = player.Layout.Near((chest.X + (int)Math.Round(Math.Cos(angle) * 4), chest.Y + (int)Math.Round(Math.Sin(angle) * 4)));
            var ground = new WorldItemObject(at.X, at.Y, items[i]);
            uint id = player.Instance.Ground.Add(ground, player);
            string name = ItemsFor(player).ByHash(items[i].TypeHash)?.Name ?? "item";
            await SendGroundItem(player, id, ground, $"Blight chest 0x{chest.Id:x} ({chest.RewardId}) drops {name} x{items[i].StackSize}", justDropped: true);
        }
    }
}
