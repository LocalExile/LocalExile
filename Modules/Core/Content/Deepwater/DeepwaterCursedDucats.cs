using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Cursed Ducats (Allflame / Deepwater): the 6 quest-item ducats (<c>DeepwaterCursedDucats</c>,
/// <c>BaseItemTypes#5431–5436</c>) socketed in inventory <c>115</c> (<c>DeepwaterCursedDucats1</c>, 3 × 1) and stored in
/// inventory <c>116</c> (<c>DeepwaterCursedDucatsStorage1</c>, 6 × 1 at fixed <c>x = Id</c>), their <c>C→S 0x0115</c>
/// quick-move, <c>LQF 1899–1904</c> / <c>QF 3629–3634</c> sync, area/combat/drop scaling for both Charts and Voyages,
/// and drop + immediate unlock (<c>LQF 1905–1909</c>) for the 5 non-Velka Cursed Ducats.</summary>
public static class DeepwaterCursedDucats
{
    /// <summary>Inventories rows: <c>DeepwaterCursedDucats1</c> (3 × 1 active sockets) and
    /// <c>DeepwaterCursedDucatsStorage1</c> (6 × 1 fixed storage, <c>x = DeepwaterCursedDucats.Id</c>).</summary>
    public const byte ActiveRow = 115, StorageRow = 116;

    /// <summary>Slot counts of inventories <see cref="ActiveRow"/> and <see cref="StorageRow"/>.</summary>
    public const int ActiveSlots = 3, StorageSlots = 6;

    /// <summary>Module-defined monster stat for <c>map_monster_difficulty_tankiness_+%</c> (+50/100/150% toughness),
    /// handed over to <see cref="MonsterModEffects.HandOverMore"/> in <c>DeepwaterModule.Load</c>.</summary>
    public const string CursedMoreLife = "deepwater_cursed_ducat_toughness_life_+%_final";

    // Exact Stats.dat Ids (client.db verified).
    public const string EnabledStat = "map_cursed_ducat_enabled";
    public const string LeagueRarityStat = "map_monster_from_league_item_rarity_+%_permyriad";
    public const string LeagueQuantityStat = "map_monster_from_league_item_quantity_+%_permyriad";
    public const string TankinessStat = "map_monster_difficulty_tankiness_+%";
    public const string SpeedStat = "map_monster_attack_cast_and_movement_speed_+%";
    public const string DamageStat = "map_monsters_damage_+%";
    public const string RandomElementStat = "map_monsters_physical_damage_%_to_add_as_random_element";
    public const string CritChanceStat = "map_monsters_critical_strike_chance_+%";
    public const string CritMultiplierStat = "map_monsters_critical_strike_multiplier_+";
    public const string PenetrationStat = "map_monsters_penetrate_elemental_resistances_%";

    /// <summary>LQF 1899–1901 (<c>DeepwaterTotalDucatsSocketed1..3</c>): distinct Cursed Ducats socketed in row 115.</summary>
    public static readonly int[] TotalSocketedLeagueFlags = [1899, 1900, 1901];

    /// <summary>LQF 1902–1904 (<c>DeepwaterSocketedDucat1..3</c>): individual active sockets x = 0..2 occupied in row 115.</summary>
    public static readonly int[] SlotSocketedLeagueFlags = [1902, 1903, 1904];

    // Drop source metadata ids for the 5 non-Velka Cursed Ducats (Tsoatha's Gift #2 / LQF 1886 is Velka in Phase 7).
    /// <summary>Offering of the Drowned interactable (Id 3: Ducat of the Undead Sea, LQF 1907).</summary>
    public const string DrownedOfferingObject = "Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterCursedDucatDrop";
    /// <summary>Filthscrabble miniboss (Id 0: Ducat of the Grasping Deep, LQF 1905).</summary>
    public const string FilthscrabbleVariety = "Metadata/Monsters/DeepwaterLeague/OctopusParasiteGiant";
    /// <summary>Rare Infested Horrors (Id 1: Ducat of the Foul Kin, LQF 1906).</summary>
    public const string InfestedHorrorVariety = "Metadata/Monsters/DeepwaterLeague/MantaRayParasite";
    /// <summary>Captainsbane miniboss (Id 4: Ducat of the Eucarid Isle, LQF 1908).</summary>
    public const string CaptainsbaneVariety = "Metadata/Monsters/DeepwaterLeague/DeepwaterCrabMiniboss";
    /// <summary>Rare Hungry Maws (Id 5: Ducat of the Fallen Stars, LQF 1909; excludes Spectre).</summary>
    public const string HungryMawPrefix = "Metadata/Monsters/DeepwaterLeague/GiantStarfish";

    const string DeepwaterMonsterPrefix = "Metadata/Monsters/DeepwaterLeague/";

    // G: world.db system_settings keys for monster Cursed Ducat drop chances (0..100%).
    public const string MinibossDropChanceSetting = "deepwater_cursed_ducat_miniboss_chance";
    public const string RareDropChanceSetting = "deepwater_cursed_ducat_rare_chance";

    // Fallback unlock LQFs by DeepwaterCursedDucats.Id (0..5), matching QuestRewards#1349..1354.__col13.
    static readonly int[] DefaultUnlockFlags = [1905, 1906, 1886, 1907, 1908, 1909];

    static readonly string[] RandomElementTargets =
    [
        "physical_damage_%_to_add_as_fire",
        "physical_damage_%_to_add_as_cold",
        "physical_damage_%_to_add_as_lightning",
    ];

    static readonly Dictionary<string, int> EmptyStats = new(StringComparer.Ordinal);

    /// <summary>One Cursed Ducat definition loaded from <c>DeepwaterCursedDucats</c>, joined with
    /// <c>BaseItemTypes</c>, <c>QuestItems</c>/<c>QuestFlags</c>, and <c>QuestRewards</c> (offer 139).</summary>
    public sealed record DucatInfo(
        int Id,
        int BaseRow,
        string ItemId,
        string Name,
        uint TypeHash,
        int? ImplicitModRow,
        string? QuestFlag,
        int UnlockLeagueFlag);

    /// <summary>One row of <c>DeepwaterStatsPerCursedDucat</c> (<c>Count</c> 1..3), with client stat keys (<c>Stats._row + 1</c>).</summary>
    public sealed record CountScaling(int Count, IReadOnlyList<(uint Key, int Value)> Stats);

    /// <summary>Precomputed per-run Cursed Ducat effects (computed once at <c>StartRun</c>, zero allocations per kill/spawn).</summary>
    public readonly record struct RunEffects(
        double QuantityFactor,
        double RarityFactor,
        IReadOnlyDictionary<string, int> MonsterStats,
        int Tankiness,
        int Speed,
        int Damage,
        int ExtraElementPercent,
        string? ExtraElementStat,
        int CritChancePercent,
        int CritMultiplierBonus,
        int ElementalPenetration)
    {
        public static readonly RunEffects None = new(1.0, 1.0, EmptyStats, 0, 0, 0, 0, null, 0, 0, 0);

        public bool Active => QuantityFactor != 1.0 || RarityFactor != 1.0 || MonsterStats.Count > 0
            || Damage != 0 || CritChancePercent != 0 || CritMultiplierBonus != 0 || ElementalPenetration != 0;
    }

    sealed record Catalog(
        DucatInfo[] All,
        Dictionary<uint, DucatInfo> ByHash,
        Dictionary<int, DucatInfo> ById,
        Dictionary<int, CountScaling> PerCount);

    static readonly Lazy<Catalog> Data = new(LoadCatalog);

    static Catalog LoadCatalog()
    {
        var cursedTable = ClientTables.TableOrEmpty("DeepwaterCursedDucats");
        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        var questFlags = ClientTables.TableOrEmpty("QuestFlags");

        var cursedBaseRows = new HashSet<int>();
        foreach (var r in cursedTable)
            if (r.IntOrNull("BaseItemType") is int b) cursedBaseRows.Add(b);

        var questFlagByBaseRow = new Dictionary<int, string>();
        foreach (var r in ClientTables.TableOrEmpty("QuestItems"))
        {
            if (r.IntOrNull("Item") is not int itemRow || !cursedBaseRows.Contains(itemRow)) continue;
            if (r.IntOrNull("TriggeredQuestFlag") is int qfRow && qfRow >= 0 && qfRow < questFlags.Count
                && questFlags[qfRow].Text("Id") is { Length: > 0 } qfId)
                questFlagByBaseRow[itemRow] = qfId;
        }

        var unlockByBaseRow = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("QuestRewards"))
        {
            if (r.IntOrNull("Reward") is not int itemRow || !cursedBaseRows.Contains(itemRow)) continue;
            if (r.Has("__col13") && r.IntOrNull("__col13") is int lf && lf > 0 && lf <= Quests.League.Count)
                unlockByBaseRow.TryAdd(itemRow, lf);
        }

        var list = new List<DucatInfo>(cursedTable.Count);
        foreach (var r in cursedTable)
        {
            int id = r.IntOrNull("Id") ?? r.Index;
            if (r.IntOrNull("BaseItemType") is not int baseRow || baseRow < 0 || baseRow >= bases.Count) continue;
            var baseType = bases[baseRow];
            if (baseType.Text("Id") is not { Length: > 0 } itemId) continue;
            string name = baseType.Text("Name") ?? itemId;
            int[] implicits = baseType.Has("Implicit_ModsKeys") ? baseType.Ints("Implicit_ModsKeys") : [];
            int? implicitMod = implicits.Length > 0 ? implicits[0] : null;
            string? qf = questFlagByBaseRow.GetValueOrDefault(baseRow)
                ?? (id is >= 0 and < StorageSlots ? $"HaveDeepwaterQuestDucat{id + 1}" : null);
            int unlockLqf = unlockByBaseRow.TryGetValue(baseRow, out int lf)
                ? lf
                : (id is >= 0 && id < DefaultUnlockFlags.Length ? DefaultUnlockFlags[id] : 0);
            list.Add(new DucatInfo(id, baseRow, itemId, name, InstanceEntities.ObjectTypeHash(itemId), implicitMod, qf, unlockLqf));
        }

        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        var all = list.ToArray();
        var byHash = new Dictionary<uint, DucatInfo>(all.Length);
        var byId = new Dictionary<int, DucatInfo>(all.Length);
        foreach (var info in all)
        {
            byHash[info.TypeHash] = info;
            byId[info.Id] = info;
        }

        var perCount = new Dictionary<int, CountScaling>();
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterStatsPerCursedDucat"))
        {
            if (r.IntOrNull("Count") is not int count || count <= 0) continue;
            int[] stats = r.IsNull("Stats") ? [] : r.Ints("Stats");
            int[] values = r.IsNull("StatValues") ? [] : r.Ints("StatValues");
            var pairs = new (uint Key, int Value)[stats.Length];
            for (int i = 0; i < stats.Length; i++)
                pairs[i] = ((uint)stats[i] + 1, i < values.Length ? values[i] : 0);
            perCount[count] = new CountScaling(count, pairs);
        }

        return new Catalog(all, byHash, byId, perCount);
    }

    /// <summary>All 6 Cursed Ducat definitions from <c>DeepwaterCursedDucats</c>, ordered by <c>Id</c> (0..5).</summary>
    public static IReadOnlyList<DucatInfo> All => Data.Value.All;

    public static DucatInfo? ByHash(uint typeHash) => Data.Value.ByHash.GetValueOrDefault(typeHash);

    public static DucatInfo? ById(int id) => Data.Value.ById.GetValueOrDefault(id);

    public static bool IsCursedDucat(uint typeHash) => Data.Value.ByHash.ContainsKey(typeHash);

    public static bool IsCursedDucat(Item item) => IsCursedDucat(item.TypeHash);

    /// <summary><c>DeepwaterStatsPerCursedDucat</c> row for <paramref name="count"/> distinct socketed Cursed Ducats (1..3).</summary>
    public static CountScaling? StatsForCount(int count) => Data.Value.PerCount.GetValueOrDefault(count);

    // ---- Inventories 115 / 116 validation & C→S 0x0115 quick-move ----

    /// <summary><c>GameEvents.InventoryRefusal</c> for rows <c>115</c> and <c>116</c>: only Cursed Ducats are accepted.</summary>
    public static string? Refusal(ClientState conn, byte row, Item item) => row is ActiveRow or StorageRow
        ? (IsCursedDucat(item.TypeHash) ? null : "only a Cursed Ducat goes into the Cursed Ducat slots")
        : null;

    /// <summary><c>GameEvents.InventoryPlace</c> for rows <c>115</c> and <c>116</c>: enforces fixed slot
    /// <c>x = DeepwaterCursedDucats.Id</c>, <c>y = 0</c> in storage (<c>116</c>, client rule <c>0x141d65f80</c>),
    /// bounds <c>0..2</c> and no duplicate Cursed Ducat base types in active sockets (<c>115</c>). Returns <c>true</c>
    /// when refused (handled, nothing changes), <c>false</c> to let the Game module place/swap normally.</summary>
    public static bool RefusedPlace(IModuleContext module, ClientState conn, InstanceItems.PlaceItem place)
    {
        bool isStorage = place.InventoryId == PlayerInventories.InventoryId(StorageRow);
        bool isActive = !isStorage && place.InventoryId == PlayerInventories.InventoryId(ActiveRow);
        if (!isStorage && !isActive) return false;

        var items = ItemsOf(conn);
        if (items.Items(PlayerInventories.Cursor).FirstOrDefault() is not { } held) return false;
        if (ByHash(held.Item.TypeHash) is not { } info)
        {
            module.Log($"{PlayerOf(conn).Character} <= 0x002a place item {held.Id} in Cursed Ducats {(isStorage ? "storage" : "sockets")} at ({place.X}, {place.Y}): refused (not a Cursed Ducat)");
            return true;
        }
        if (isStorage)
        {
            if (info.Id >= 0 && place.X == (uint)info.Id && place.Y == 0) return false;
            module.Log($"{PlayerOf(conn).Character} <= 0x002a place item {held.Id} ({info.Name}) in Cursed Ducats storage at ({place.X}, {place.Y}): refused (belongs in fixed slot ({info.Id}, 0))");
            return true;
        }
        if (place.X >= ActiveSlots || place.Y != 0)
        {
            module.Log($"{PlayerOf(conn).Character} <= 0x002a place item {held.Id} ({info.Name}) in Cursed Ducats sockets at ({place.X}, {place.Y}): refused (out of bounds)");
            return true;
        }
        if (items.Items(ActiveRow).Any(i => i.X != place.X && i.Item.TypeHash == held.Item.TypeHash))
        {
            module.Log($"{PlayerOf(conn).Character} <= 0x002a place item {held.Id} ({info.Name}) in Cursed Ducats sockets at ({place.X}, {place.Y}): refused (duplicate Cursed Ducat)");
            return true;
        }
        return false;
    }

    /// <summary><c>C→S 0x0115</c> (<see cref="InstanceItems.SocketStorageMoveId"/>), Ctrl + click on a Cursed Ducat with
    /// <c>DeepwaterVoyageWindow</c> open:
    /// <list type="bullet">
    /// <item><c>(FromExpanded=true, Into=true)</c>: clicked in <c>MainInventory (0)</c> → move to <c>116</c> fixed slot
    /// <c>(Id, 0)</c> if empty, else first free slot of <c>115</c> if not duplicate;</item>
    /// <item><c>(FromExpanded=false, Into=true)</c>: clicked in <c>115</c> (unsocket) → move to <c>116</c> fixed slot
    /// <c>(Id, 0)</c> if empty, else first free cell in <c>MainInventory (0)</c>;</item>
    /// <item><c>(FromExpanded=false, Into=false)</c>: clicked in <c>116</c> (socket) → move to first free slot of
    /// <c>115</c> if not duplicate, else first free cell in <c>MainInventory (0)</c>.</item>
    /// </list>
    /// Syncs <c>LQF 1899–1904</c> and <c>QF 3629–3634</c> after moving.</summary>
    public static async Task QuickMove(IModuleContext module, ClientState conn, InstanceItems.LeagueWindowMove move)
    {
        var player = PlayerOf(conn);
        var items = ItemsOf(conn);
        string what = $"0x0115 Ctrl + click Cursed Ducat {move.ItemId} ({(move.FromExpanded ? 1 : 0)}, {(move.Into ? 1 : 0)})";

        byte fromRow;
        if (move.FromExpanded && move.Into) fromRow = PlayerInventories.MainInventory;
        else if (!move.FromExpanded && move.Into) fromRow = ActiveRow;
        else if (!move.FromExpanded && !move.Into) fromRow = StorageRow;
        else
        {
            module.Log($"{player.Character} <= {what}: refused (unexpected flags)");
            return;
        }

        var it = items.Items(fromRow).FirstOrDefault(i => i.Id == move.ItemId);
        if (it is null)
        {
            module.Log($"{player.Character} <= {what}: refused (not in row {fromRow})");
            return;
        }
        if (ByHash(it.Item.TypeHash) is not { } info)
        {
            module.Log($"{player.Character} <= {what}: refused (not a Cursed Ducat)");
            return;
        }
        if (fromRow == PlayerInventories.MainInventory && DeepwaterDredge.Dredged(it.Item))
        {
            module.Log($"{player.Character} <= {what}: refused (Dredge item)");
            return;
        }

        var shape = ShapeOf(it.Item) ?? new(1, 1, "");
        byte toRow = 0;
        InventoryItem? placed = null;
        string? refusal = null;

        Saves.Atomically(() =>
        {
            (byte Row, byte X, byte Y)? target = fromRow switch
            {
                // G: MainInventory → prefer fixed storage slot (116), else active socket (115).
                PlayerInventories.MainInventory => FreeStorageSlot(items, info) ?? FreeActiveSlot(items, info),
                // Active (115) → prefer fixed storage slot (116), else MainInventory (0).
                ActiveRow => FreeStorageSlot(items, info) ?? FreeMainSlot(items, shape),
                // Storage (116) → prefer active socket (115), else MainInventory (0).
                StorageRow => FreeActiveSlot(items, info) ?? FreeMainSlot(items, shape),
                _ => null,
            };
            if (target is not { } dest)
            {
                refusal = "no valid destination slot";
                return;
            }
            if (items.Remove(fromRow, it.Id) is not { } taken)
            {
                refusal = "item disappeared";
                return;
            }
            toRow = dest.Row;
            placed = items.Put(toRow, taken with { X = dest.X, Y = dest.Y });
        });

        if (placed is null)
        {
            module.Log($"{player.Character} <= {what}: refused ({refusal ?? "no room"})");
            return;
        }

        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(fromRow), items.NextRevision(fromRow), [placed.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(fromRow)}: remove Cursed Ducat {placed.Id}");
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(toRow), items.NextRevision(toRow), [], [placed]),
            $"0x00bf inventory {PlayerInventories.InventoryId(toRow)}: add Cursed Ducat {placed.Id} ({info.Name}) at ({placed.X}, {placed.Y})");
        module.Log($"{player.Character} <= {what}: moved {info.Name} from row {fromRow} to row {toRow} ({placed.X}, {placed.Y})");
        await SyncFlags(conn);
    }

    static (byte Row, byte X, byte Y)? FreeStorageSlot(PlayerInventories items, DucatInfo info)
    {
        if (info.Id is < 0 or >= StorageSlots) return null;
        return items.Items(StorageRow).Any(s => s.X == info.Id && s.Y == 0) ? null : (StorageRow, (byte)info.Id, (byte)0);
    }

    static (byte Row, byte X, byte Y)? FreeActiveSlot(PlayerInventories items, DucatInfo info)
    {
        var active = items.Items(ActiveRow);
        if (active.Any(s => s.Item.TypeHash == info.TypeHash)) return null;
        for (byte x = 0; x < ActiveSlots; x++)
            if (!active.Any(s => s.X == x && s.Y == 0)) return (ActiveRow, x, (byte)0);
        return null;
    }

    static (byte Row, byte X, byte Y)? FreeMainSlot(PlayerInventories items, PlayerInventories.Shape shape) =>
        items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is { } cell
            ? (PlayerInventories.MainInventory, cell.X, cell.Y) : null;

    // ---- LQF 1899–1904 & QF 3629–3634 sync ----

    /// <summary>Syncs <c>LQF 1899–1901</c> (<c>DeepwaterTotalDucatsSocketed1..3</c>), <c>LQF 1902–1904</c>
    /// (<c>DeepwaterSocketedDucat1..3</c>), and <c>QF 3629–3634</c> (<c>HaveDeepwaterQuestDucat1..6</c>) to the player's
    /// current inventories. Sends nothing when all flags already match.</summary>
    public static async Task SyncFlags(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea) return;
        var all = All;
        if (all.Count == 0) return;

        var items = ItemsOf(conn);
        var active = items.Items(ActiveRow);
        uint distinctMask = 0;
        int distinctCount = 0;
        int slotMask = 0;
        for (int i = 0; i < active.Count; i++)
        {
            var entry = active[i];
            if (ByHash(entry.Item.TypeHash) is not { } info) continue;
            if (info.Id is >= 0 and < 32)
            {
                uint bit = 1u << info.Id;
                if ((distinctMask & bit) == 0) { distinctMask |= bit; distinctCount++; }
            }
            if (entry.X < ActiveSlots) slotMask |= 1 << entry.X;
        }
        if (distinctCount > ActiveSlots) distinctCount = ActiveSlots;

        List<int>? lqfSet = null, lqfClear = null;
        lock (conn.Monsters)
        {
            for (int i = 0; i < TotalSocketedLeagueFlags.Length; i++)
            {
                int flag = TotalSocketedLeagueFlags[i];
                bool want = distinctCount >= i + 1;
                bool has = conn.LeagueQuestFlags.Contains(flag);
                if (want && !has) (lqfSet ??= []).Add(flag);
                else if (!want && has) (lqfClear ??= []).Add(flag);
            }
            for (int i = 0; i < SlotSocketedLeagueFlags.Length; i++)
            {
                int flag = SlotSocketedLeagueFlags[i];
                bool want = (slotMask & (1 << i)) != 0;
                bool has = conn.LeagueQuestFlags.Contains(flag);
                if (want && !has) (lqfSet ??= []).Add(flag);
                else if (!want && has) (lqfClear ??= []).Add(flag);
            }
        }

        uint carriedMask = 0;
        foreach (var (_, carried) in QuestItems.Carried(items))
        {
            if (ByHash(carried.Item.TypeHash) is { Id: >= 0 and < 32 } info)
                carriedMask |= 1u << info.Id;
        }

        List<string>? qfSet = null, qfClear = null;
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            if (info.QuestFlag is not { Length: > 0 } qf || info.Id is < 0 or >= 32) continue;
            bool want = (carriedMask & (1u << info.Id)) != 0;
            bool has = conn.HasQuestFlag(qf);
            if (want && !has) (qfSet ??= []).Add(qf);
            else if (!want && has) (qfClear ??= []).Add(qf);
        }

        if (lqfSet is null && lqfClear is null && qfSet is null && qfClear is null) return;
        var (remote, stream, ciphers) = conn.Wire;
        if (lqfSet is not null) await SetLeagueQuestFlags(remote, stream, ciphers, conn, lqfSet, on: true);
        if (lqfClear is not null) await SetLeagueQuestFlags(remote, stream, ciphers, conn, lqfClear, on: false);
        if (qfSet is not null) await SetQuestFlags(remote, stream, ciphers, conn, qfSet, on: true);
        if (qfClear is not null) await SetQuestFlags(remote, stream, ciphers, conn, qfClear, on: false);
    }

    /// <summary><c>GameEvents.MessagesHandled</c>: syncs Cursed Ducat flags only when an item-moving, reward-claim,
    /// pickup, or command opcode was handled (0-alloc no-op on ordinary ticks/combat).</summary>
    public static Task OnMessagesHandled(ClientState conn, IReadOnlyList<ushort> ids)
    {
        for (int i = 0; i < ids.Count; i++)
            if (MayChangeCursedDucats(ids[i])) return SyncFlags(conn);
        return Task.CompletedTask;
    }

    static bool MayChangeCursedDucats(ushort id) => id is
        InstanceItems.PickUpItemId or
        InstanceItems.PlaceItemId or
        InstanceItems.DropItemId or
        InstanceItems.ItemActionId or
        InstanceItems.SocketStorageMoveId or
        InstanceItems.CommandId or
        InstanceNpcs.BuyId or
        InstanceNpcs.CtrlBuyId or
        InstanceTransfer.UseSkillOnObjectId or
        InstanceChat.ChatId
        || QuestItems.MovesItems(id);

    // ---- Area stats injection (both Charts and Voyages) ----

    /// <summary>Injects stats from all distinct Cursed Ducats currently socketed in inventory <c>115</c> into
    /// <paramref name="global"/> (both Charts and Voyages): each Ducat's implicit downside mod stats
    /// (<c>Mods#40212–40217</c>), <c>map_cursed_ducat_enabled</c> clamped to <c>1</c>, and the matching
    /// <c>DeepwaterStatsPerCursedDucat</c> row (+40/80/120% IIR, +20/40/60% IIQ, +50/100/150% toughness). Returns the
    /// number of distinct socketed Cursed Ducats applied (0..3).</summary>
    public static int InjectPlanStats(ClientState conn, Dictionary<uint, int> global)
    {
        var active = ItemsOf(conn).Items(ActiveRow);
        if (active.Count == 0) return 0;
        var items = new List<Item>(Math.Min(active.Count, ActiveSlots));
        for (int i = 0; i < active.Count; i++) items.Add(active[i].Item);
        return InjectStats(items, ItemsFor(conn).Mods, global);
    }

    /// <summary>Pure stat-injection core for <see cref="InjectPlanStats"/> (also callable directly in tests).</summary>
    public static int InjectStats(IReadOnlyList<Item> socketed, ModCatalog mods, Dictionary<uint, int> global)
    {
        if (socketed.Count == 0) return 0;
        uint seenMask = 0;
        int count = 0;

        for (int i = 0; i < socketed.Count && count < ActiveSlots; i++)
        {
            var item = socketed[i];
            if (ByHash(item.TypeHash) is not { } info || info.Id is < 0 or >= 32) continue;
            uint bit = 1u << info.Id;
            if ((seenMask & bit) != 0) continue; // match client 0x141fbf340 deduplication by BaseItemTypes
            seenMask |= bit;
            count++;

            if (item.Mods.Count > 0)
            {
                for (int m = 0; m < item.Mods.Count; m++)
                {
                    var itemMod = item.Mods[m];
                    if (mods.ByHash(itemMod.Hash) is not { } def) continue;
                    int statCount = Math.Min(def.Stats.Count, itemMod.Values.Count);
                    for (int s = 0; s < statCount; s++)
                    {
                        int value = itemMod.Values[s];
                        if (value != 0 && Program.Stats.Key(def.Stats[s].Id) is uint key)
                            global[key] = global.GetValueOrDefault(key) + value;
                    }
                }
            }
            else if (info.ImplicitModRow is int modRow && mods.Row(modRow) is { } fallbackMod)
            {
                for (int s = 0; s < fallbackMod.Stats.Count; s++)
                {
                    int value = fallbackMod.RangeAt(s).Min;
                    if (value != 0 && Program.Stats.Key(fallbackMod.Stats[s].Id) is uint key)
                        global[key] = global.GetValueOrDefault(key) + value;
                }
            }
        }

        if (count == 0) return 0;

        if (Program.Stats.Key(EnabledStat) is uint enabledKey)
            global[enabledKey] = 1;

        if (StatsForCount(count) is { } scaling)
        {
            for (int i = 0; i < scaling.Stats.Count; i++)
            {
                var (key, value) = scaling.Stats[i];
                if (value != 0) global[key] = global.GetValueOrDefault(key) + value;
            }
        }

        return count;
    }

    // ---- Precomputed combat & loot effects (0-alloc per kill / per spawn) ----

    /// <summary>Computes the Cursed Ducat IIQ and IIR drop multipliers from a run's global named stats
    /// (<c>1.0 + permyriad / 10000.0</c>, clamped ≥ 0): <c>(1.0, 1.0)</c> when no Cursed Ducats are active.</summary>
    public static (double Quantity, double Rarity) DropFactors(IReadOnlyDictionary<string, int> globalNamed)
    {
        int qty = globalNamed.GetValueOrDefault(LeagueQuantityStat);
        int rar = globalNamed.GetValueOrDefault(LeagueRarityStat);
        return (Math.Max(0.0, 1.0 + qty / 10000.0), Math.Max(0.0, 1.0 + rar / 10000.0));
    }

    /// <summary>Builds the Cursed Ducat monster stats to merge into <c>DeepwaterMonsters.ModsFor</c> (called once at
    /// <c>StartRun</c>). Maps <c>map_monster_difficulty_tankiness_+%</c> → <see cref="CursedMoreLife"/>,
    /// <c>map_monster_attack_cast_and_movement_speed_+%</c> → <c>attack_speed_+%</c> / <c>base_cast_speed_+%</c> /
    /// <c>base_movement_velocity_+%</c>, and <c>map_monsters_physical_damage_%_to_add_as_random_element</c> → a random
    /// element chosen once per run. (<c>map_monsters_damage_+%</c> is omitted here because
    /// <see cref="MapMods.MonsterEffectStats"/> already applies it via <c>MapModsOf</c>.)</summary>
    public static IReadOnlyDictionary<string, int> MonsterStats(IReadOnlyDictionary<string, int> globalNamed, Random? random = null) =>
        EffectsFor(globalNamed, random).MonsterStats;

    /// <summary>Precomputes all Cursed Ducat combat and drop effects once from a plan's global named stats.</summary>
    public static RunEffects EffectsFor(IReadOnlyDictionary<string, int> globalNamed, Random? random = null)
    {
        if (globalNamed.Count == 0) return RunEffects.None;

        var (qty, rar) = DropFactors(globalNamed);
        int tankiness = Math.Max(0, globalNamed.GetValueOrDefault(TankinessStat));
        int speed = globalNamed.GetValueOrDefault(SpeedStat);
        int damage = globalNamed.GetValueOrDefault(DamageStat);
        int extraElem = Math.Max(0, globalNamed.GetValueOrDefault(RandomElementStat));
        int critChance = Math.Max(0, globalNamed.GetValueOrDefault(CritChanceStat));
        int critMult = Math.Max(0, globalNamed.GetValueOrDefault(CritMultiplierStat));
        int pen = Math.Max(0, globalNamed.GetValueOrDefault(PenetrationStat));

        string? elemStat = null;
        Dictionary<string, int>? monsterStats = null;
        if (tankiness > 0)
            (monsterStats ??= new(StringComparer.Ordinal))[CursedMoreLife] = tankiness;
        if (speed != 0)
        {
            monsterStats ??= new(StringComparer.Ordinal);
            monsterStats["attack_speed_+%"] = speed;
            monsterStats["base_cast_speed_+%"] = speed;
            monsterStats["base_movement_velocity_+%"] = speed;
        }
        if (extraElem > 0)
        {
            // G: roll the random element once per run so every monster in the run shares the same elemental gain.
            elemStat = RandomElementTargets[(random ?? Random.Shared).Next(RandomElementTargets.Length)];
            (monsterStats ??= new(StringComparer.Ordinal))[elemStat] = extraElem;
        }

        if (qty == 1.0 && rar == 1.0 && monsterStats is null && damage == 0 && critChance == 0 && critMult == 0 && pen == 0)
            return RunEffects.None;

        return new RunEffects(qty, rar, monsterStats ?? EmptyStats, tankiness, speed, damage, extraElem, elemStat, critChance, critMult, pen);
    }

    /// <summary>Precomputes all Cursed Ducat combat and drop effects directly from client-keyed global stats
    /// (<c>DeepwaterPlan.Global</c>).</summary>
    public static RunEffects EffectsFor(IReadOnlyDictionary<uint, int> global, Random? random = null)
    {
        if (global.Count == 0) return RunEffects.None;
        var named = new Dictionary<string, int>(global.Count, StringComparer.Ordinal);
        foreach (var (key, value) in global)
            if (value != 0 && Program.Stats.Name(key) is { Length: > 0 } name)
                named[name] = named.GetValueOrDefault(name) + value;
        return EffectsFor(named, random);
    }

    /// <summary>Merges precomputed <paramref name="cursedStats"/> onto a cell's monster stats (0-alloc when either
    /// side is empty).</summary>
    public static IReadOnlyDictionary<string, int>? MergeMonsterStats(
        IReadOnlyDictionary<string, int>? cellStats, IReadOnlyDictionary<string, int>? cursedStats)
    {
        if (cursedStats is null || cursedStats.Count == 0) return cellStats;
        if (cellStats is null || cellStats.Count == 0) return cursedStats;
        var merged = new Dictionary<string, int>(cellStats, StringComparer.Ordinal);
        foreach (var (id, value) in cursedStats)
            if (value != 0) merged[id] = merged.GetValueOrDefault(id) + value;
        return merged;
    }

    /// <summary>Per-kill hot-path helper: applies precomputed Cursed Ducat IIQ/IIR multipliers to <paramref name="roll"/>
    /// (zero allocations).</summary>
    public static void ApplyDropFactors(DropRoll roll, double quantityFactor, double rarityFactor)
    {
        if (quantityFactor != 1.0) roll.Quantity *= quantityFactor;
        if (rarityFactor != 1.0) roll.Rarity *= rarityFactor;
    }

    // ---- Drop logic for the 5 non-Velka Cursed Ducats + immediate LQF 1905–1909 unlock ----

    /// <summary>Whether this connection's league has already unlocked the given Cursed Ducat flag.</summary>
    public static bool HasUnlocked(ClientState conn, int unlockLeagueFlag)
    {
        if (unlockLeagueFlag <= 0) return false;
        lock (conn.Monsters) return conn.LeagueQuestFlags.Contains(unlockLeagueFlag);
    }

    /// <summary>Returns the <c>DeepwaterCursedDucats.Id</c> dropped by this monster variety and rarity, or <c>-1</c> if
    /// none (0-alloc hot-path filter):
    /// <list type="bullet">
    /// <item>Id 0 (Grasping Deep, LQF 1905): Filthscrabble (<c>OctopusParasiteGiant</c>)</item>
    /// <item>Id 1 (Foul Kin, LQF 1906): Rare/Unique Infested Horrors (<c>MantaRayParasite</c>)</item>
    /// <item>Id 4 (Eucarid Isle, LQF 1908): Captainsbane (<c>DeepwaterCrabMiniboss</c>)</item>
    /// <item>Id 5 (Fallen Stars, LQF 1909): Rare/Unique Hungry Maws (<c>GiantStarfish*</c>, excluding Spectre)</item>
    /// </list></summary>
    public static int MonsterDropDucatId(string varietyId, MonsterRarity rarity)
    {
        if (!varietyId.StartsWith(DeepwaterMonsterPrefix, StringComparison.Ordinal)) return -1;
        if (varietyId.Equals(FilthscrabbleVariety, StringComparison.Ordinal)) return 0;
        if (varietyId.Equals(CaptainsbaneVariety, StringComparison.Ordinal)) return 4;
        if (rarity < MonsterRarity.Rare) return -1;
        if (varietyId.Equals(InfestedHorrorVariety, StringComparison.Ordinal)) return 1;
        if (varietyId.StartsWith(HungryMawPrefix, StringComparison.Ordinal)
            && !varietyId.EndsWith("Spectre", StringComparison.Ordinal)) return 5;
        return -1;
    }

    /// <summary>Per-kill <c>GameEvents.Drops</c> hook inside an Allflame Chart or Voyage: drops the monster's Cursed
    /// Ducat if the player has not yet unlocked its LQF (<c>1905, 1906, 1908, 1909</c>) and immediately commits + sends
    /// that unlock LQF so Valerie's reclaim offer (<c>#139</c>) works even if the player fails extraction. Zero
    /// allocations on every non-matching or already-unlocked kill.</summary>
    public static void Drops(IModuleContext module, DropRoll roll)
    {
        int id = MonsterDropDucatId(roll.Monster.VarietyId, roll.Monster.Rarity);
        if (id < 0) return;
        if (ById(id) is not { UnlockLeagueFlag: > 0 } info) return;

        var conn = roll.Conn;
        if (HasUnlocked(conn, info.UnlockLeagueFlag)) return;

        // G: minibosses (Filthscrabble / Captainsbane) default to 100%, rare pack monsters (Infested Horror / Hungry Maw) default to 25%.
        int chance = id is 0 or 4
            ? ChanceSetting(MinibossDropChanceSetting, 100)
            : ChanceSetting(RareDropChanceSetting, 25);
        if (chance <= 0 || (chance < 100 && Random.Shared.Next(100) >= chance)) return;

        bool unlocked = false;
        lock (conn.Monsters)
        {
            if (!HardcoreProgressBlocked(conn))
                unlocked = conn.LeagueQuestFlags.Add(info.UnlockLeagueFlag);
        }
        if (!unlocked) return;

        PersonalStashOf(conn).ChangeLeagueQuestFlags([info.UnlockLeagueFlag], true);
        roll.Items.Add(info.ItemId);
        module.Log($"{CharacterName(conn)}: {roll.Monster.Name} drops {info.Name} (unlocked LQF {info.UnlockLeagueFlag})");

        if (!conn.Closed && !conn.LeavingArea && conn.PlayerSent)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    List<int> all;
                    lock (conn.Monsters) all = [.. conn.LeagueQuestFlags.Order()];
                    if (!conn.Closed && !conn.LeavingArea && conn.PlayerSent)
                        await global::Game.SendWorldRecord(PlayerOf(conn), PlayerId,
                            InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(all)),
                            $"0x02bc player league quest flags: {string.Join(", ", all)}");
                }
                catch (Exception ex)
                {
                    module.Log($"{CharacterName(conn)}: Cursed Ducat unlock LQF {info.UnlockLeagueFlag} send failed: {ex.Message}");
                }
            });
        }
    }

    /// <summary>Offering of the Drowned (<see cref="DrownedOfferingObject"/>): if the player has not yet unlocked
    /// <c>Ducat of the Undead Sea</c> (Id 3, <c>BaseItemTypes#5434</c>, <c>LQF 1907</c>), unlocks <c>LQF 1907</c>
    /// immediately and returns the Cursed Ducat drop; returns <c>null</c> once already unlocked so the caller can fall
    /// back to a random crafting Ducat.</summary>
    public static async Task<ItemDrops.Drop?> DropFromOffering(IModuleContext module, ClientState conn, int level)
    {
        if (ById(3) is not { UnlockLeagueFlag: > 0 } info) return null;
        if (HasUnlocked(conn, info.UnlockLeagueFlag)) return null;
        lock (conn.Monsters) if (HardcoreProgressBlocked(conn)) return null;

        if (ItemsFor(conn).Find(info.ItemId) is not { Supported: true } type)
        {
            module.Log($"{CharacterName(conn)}: {info.ItemId} is not supported; Offering of the Drowned cannot drop {info.Name}");
            return null;
        }

        var (remote, stream, ciphers) = conn.Wire;
        await SetLeagueQuestFlags(remote, stream, ciphers, conn, [info.UnlockLeagueFlag], on: true);
        module.Log($"{CharacterName(conn)}: Offering of the Drowned drops {info.Name} (unlocked LQF {info.UnlockLeagueFlag})");
        return new ItemDrops.Drop(ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) }, type);
    }

    static int ChanceSetting(string key, int fallback) =>
        SystemSettings.Default.Values.TryGetValue(key, out var v) && double.IsFinite(v)
            ? (int)Math.Clamp(v, 0, 100) : fallback;
}
