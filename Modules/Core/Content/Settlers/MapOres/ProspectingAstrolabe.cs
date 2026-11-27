using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Settlers.MapOres;

/// <summary>
/// Handles the <c>Prospecting Astrolabe</c> (<c>Metadata/Items/Currency/AstrolabeSettlers</c>, BaseItemTypes 5392):
/// <list type="bullet">
/// <item>Applying a Prospecting Astrolabe to an Atlas map node (<c>C-&gt;S 0x0075 / 0x0076</c>) creates a Shaped Region
/// of up to 7 connected maps in that Atlas quadrant, persisted per character.</item>
/// <item>Opening any map inside an active Prospecting Shaped Region (or placing a Prospecting Astrolabe directly
/// in the Map Device) injects <c>prospecting_astrolabe_enabled_map</c>, <c>astrolabe_enabled_map</c>,
/// <c>map_spawn_settlers_ore</c>, and <c>+1</c> additional Ore Deposit.</item>
/// <item>Completing the maps in the Shaped Region collapses completed nodes and awards a Prospecting Vault bonus
/// (extra ore + Runegraft) when the region clears.</item>
/// </list>
/// </summary>
public sealed class ProspectingAstrolabe
{
    const string RegionSaveKey = "settlers.astrolabe.region";
    const int ShapedRegionSize = 7;

    sealed record AtlasEntry(int Row, string Id, ushort Hash16, int Region, string AreaId, IReadOnlyList<int> Connections, bool IsNormalMap, bool IsUniqueMap);
    sealed class SavedRegion
    {
        public int Region { get; set; }
        public List<string> RemainingAreas { get; set; } = [];
        public int CompletedCount { get; set; }
    }

    static Dictionary<ushort, AtlasEntry> _byHash = [];
    static Dictionary<int, AtlasEntry> _byRow = [];
    static Dictionary<string, AtlasEntry> _byAreaId = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, HashSet<string>> _areaBosses = new(StringComparer.OrdinalIgnoreCase);

    public static void LoadGameData()
    {
        var byHash = new Dictionary<ushort, AtlasEntry>();
        var byRow = new Dictionary<int, AtlasEntry>();
        var byArea = new Dictionary<string, AtlasEntry>(StringComparer.OrdinalIgnoreCase);
        var areaBosses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (ClientTables.Has("AtlasNode") && ClientTables.Has("WorldAreas"))
        {
            var areas = ClientTables.Table("WorldAreas");
            var varieties = ClientTables.Has("MonsterVarieties") ? ClientTables.Table("MonsterVarieties") : null;
            if (varieties is not null)
            {
                foreach (var areaRow in areas)
                {
                    if (areaRow.Text("Id") is not { Length: > 0 } aId) continue;
                    var bossRows = areaRow.Ints("Bosses_MonsterVarietiesKeys");
                    if (bossRows.Length == 0) continue;
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (int vRow in bossRows)
                        if (varieties.Row(vRow)?.Text("Id") is { Length: > 0 } vId)
                            set.Add(vId);
                    if (set.Count > 0) areaBosses[aId] = set;
                }
            }
            foreach (var row in ClientTables.Table("AtlasNode"))
            {
                if (row.BoolOrNull("NotOnAtlas") == true) continue;
                string id = row.Text("Id") ?? "";
                ushort hash = (ushort)(row.UIntOrNull("HASH16") ?? 0);
                if (hash == 0) continue;
                int region = row.IntOrNull("Region") ?? 0;
                int areaRow = row.IntOrNull("Area1") ?? -1;
                string areaId = areaRow >= 0 ? areas.Row(areaRow)?.Text("Id") ?? id : id;
                bool normal = row.BoolOrNull("IsNormalMap") == true;
                bool unique = row.BoolOrNull("IsUniqueMap") == true;
                var entry = new AtlasEntry(row.Index, id, hash, region, areaId, row.Ints("Connections"), normal, unique);
                byHash.TryAdd(hash, entry);
                byRow[row.Index] = entry;
                if (!string.IsNullOrEmpty(areaId)) byArea.TryAdd(areaId, entry);
            }
        }
        _byHash = byHash;
        _byRow = byRow;
        _byAreaId = byArea;
        _areaBosses = areaBosses;
    }

    public bool MapDeviceAccepts(Player? player, uint typeHash, string itemClass) =>
        Items.ByHash(typeHash)?.Id.Equals(MapOreTables.ProspectingAstrolabeId, StringComparison.Ordinal) == true;

    /// <summary>Handles <c>C-&gt;S 0x0076</c> when the stackable item is a Prospecting Astrolabe.</summary>
    public Task<bool> OnUseItemTypeOnNode(Player player, byte[] bytes)
    {
        if (bytes.Length < InstanceMapDevice.UseItemTypeOnAtlasNodeLength) return Task.FromResult(false);
        var req = InstanceMapDevice.ReadUseItemTypeOnAtlasNode(bytes);
        if (ItemsFor(player).ByHash(req.TypeHash)?.Id != MapOreTables.ProspectingAstrolabeId) return Task.FromResult(false);
        var (inventory, itemId) = LocateCurrencyStackByType(player, req.TypeHash, req.InventoryId);
        return itemId == 0
            ? Task.FromResult(true)
            : ApplyToAtlasNode(player, new InstanceMapDevice.UseItemOnAtlasNode(inventory, itemId, req.NodeHash16), req.TypeHash);
    }

    /// <summary>Handles <c>C-&gt;S 0x0075</c> when the targeted item is a Prospecting Astrolabe.</summary>
    public Task<bool> OnUseItemOnNode(Player player, byte[] bytes)
    {
        if (bytes.Length < InstanceMapDevice.UseItemOnAtlasNodeLength) return Task.FromResult(false);
        var req = InstanceMapDevice.ReadUseItemOnAtlasNode(bytes);
        var (_, used) = Locate(ItemsOf(player), StashOf(player), req.InventoryId, req.ItemId);
        if (used is null || ItemsFor(player).ByHash(used.Item.TypeHash)?.Id != MapOreTables.ProspectingAstrolabeId)
            return Task.FromResult(false);
        return ApplyToAtlasNode(player, req, used.Item.TypeHash);
    }

    async Task<bool> ApplyToAtlasNode(Player player, InstanceMapDevice.UseItemOnAtlasNode req, uint requiredType)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (!_byHash.TryGetValue(req.NodeHash16, out var target) || !target.IsNormalMap || target.IsUniqueMap)
        {
            await PlayerOf(player).Tell("Cannot apply Prospecting Astrolabe to this Atlas map.");
            return true;
        }

        var existing = LoadRegion(player);
        if (existing is { RemainingAreas.Count: > 0 } && existing.Region == target.Region)
        {
            await PlayerOf(player).Tell("This Atlas quadrant already has an active Prospecting Shaped Region.");
            return true;
        }

        var items = ItemsOf(player);
        var stash = StashOf(player);
        var cluster = BuildCluster(target, ShapedRegionSize);

        var inventoryUpdate = Saves.Atomically<byte[]?>(() =>
        {
            var (spot, used) = Locate(items, stash, req.InventoryId, req.ItemId);
            if (used is null || used.Item.StackSize == 0 || used.Item.TypeHash != requiredType) return null;
            if (spot.Tab is int stashTab)
            {
                if (!(player.StashOpen || player.GuildStashOpen) || stash.IsPublic(stashTab)) return null;
            }
            else if (req.InventoryId != PlayerInventories.InventoryId(spot.Row)
                || (spot.Row != PlayerInventories.MainInventory
                    && !(spot.Row == PlayerInventories.ExpandedInventory && ExpandedInventoryGranted(player)))) return null;

            InventoryItem? remaining = null;
            if (used.Item.StackSize == 1)
            {
                if ((spot.Tab is int tab ? stash.Take(tab, used.Id) : items.Remove(spot.Row, used.Id)) is null)
                    return null;
            }
            else
            {
                var reduced = used.Item with { StackSize = (ushort)(used.Item.StackSize - 1) };
                remaining = spot.Tab is int tab ? stash.Change(tab, used.Id, _ => reduced) : items.Change(spot.Row, used.Id, _ => reduced);
                if (remaining is null) return null;
            }

            SaveRegion(player, new SavedRegion { Region = target.Region, RemainingAreas = cluster, CompletedCount = 0 });
            uint revision = spot.Tab is int t ? stash.NextRevision(t) : items.NextRevision(spot.Row);
            return InstanceItems.InventoryChange(req.InventoryId, revision, [used.Id], remaining is null ? [] : [remaining]);
        });

        if (inventoryUpdate is null) return true;
        await Send(player, inventoryUpdate, $"0x00bf consume Prospecting Astrolabe on {target.Id}");
        await PlayerOf(player).Tell($"Prospecting Shaped Region created across {cluster.Count} connected Atlas maps (guaranteed Ore Deposits)!");
        return true;
    }

    /// <summary>
    /// <see cref="GameEvents.MapOpening"/> hook: enables Prospecting Astrolabe stats when opening a map in the active
    /// Shaped Region or when a Prospecting Astrolabe is placed directly into the Map Device.
    /// </summary>
    public void OnMapOpening(MapOpening request)
    {
        if (request.Opening is not { } opening || request.Campaign) return;

        bool directSlot = request.Slots.Any(s => !s.IsMap && s.BaseId.Equals(MapOreTables.ProspectingAstrolabeId, StringComparison.Ordinal));
        var region = LoadRegion(request.Player);
        bool inShapedRegion = region is { RemainingAreas.Count: > 0 }
            && region.RemainingAreas.Contains(opening.Area.Id, StringComparer.OrdinalIgnoreCase);

        if (!directSlot && !inShapedRegion) return;

        var mods = opening.Mods
            .WithFragmentStat(Stat.ProspectingAstrolabeEnabledMap, 1)
            .WithFragmentStat(Stat.AstrolabeEnabledMap, 1)
            .WithFragmentStat(Stat.MapSpawnSettlersOre, 1)
            .WithFragmentStat(Stat.MapContainsXAdditionalVillageOres, 1);

        request.Opening = opening with { Mods = mods };
        request.HandledContent.Add(MapScarabContent.KalguuranOre);
    }

    /// <summary>
    /// Tracks Shaped Region completion when the area's final map boss dies inside a Prospecting Astrolabe map
    /// (verified against <c>WorldAreas.Bosses_MonsterVarietiesKeys</c>, ignoring Rogue Exiles / ore guardians).
    /// </summary>
    public async Task OnMonsterKilled(MonsterKill kill)
    {
        if (kill.Monster.Rarity != MonsterRarity.Unique) return;
        var player = kill.Player;
        if (!MapDevices.IsMapArea(player.Layout.Hash)) return;
        if (!_areaBosses.TryGetValue(player.Layout.Id, out var bosses) || !bosses.Contains(kill.Monster.VarietyId))
            return;

        // Multi-boss maps complete only when the final living map boss is slain.
        foreach (var remaining in player.Area.Monsters.Values.Concat(player.Area.DormantMonsters.Values))
        {
            if (!remaining.Hostile || remaining.Dead || remaining.Life <= 0) continue;
            if (kill.Monster.ObjectId != 0 && remaining.ObjectId == kill.Monster.ObjectId) continue;
            if (remaining.Variety is { Id.Length: > 0 } v && bosses.Contains(v.Id))
                return;
        }

        var region = LoadRegion(player);
        if (region is null || region.RemainingAreas.Count == 0) return;

        int idx = region.RemainingAreas.FindIndex(a => a.Equals(player.Layout.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        region.RemainingAreas.RemoveAt(idx);
        region.CompletedCount++;
        SaveRegion(player, region);

        if (region.RemainingAreas.Count == 0)
        {
            int level = Math.Clamp(MapRunOf(player)?.Area.Level is > 0 and int ml ? ml : 83, 68, 100);
            long bonusOre = Math.Max(250L, (long)Math.Round(MapOreTables.BaseOreAmount(level) * 2.0 * VillageResourceHooks.OreFoundMultiplier));
            var oreKind = (MapOreSlotKind)AreaRng(player).Next(5);
            VillageResourceHooks.GrantResource(player, MapOreTables.PendingResourceOf(oreKind), bonusOre, "prospecting-astrolabe:vault");
            await PlayerOf(player).Tell($"Prospecting Shaped Region complete! Vault bonus: {bonusOre:N0} {MapOreTables.DisplayName(oreKind)} marked for Kingsmarch!");
        }
        else
        {
            await PlayerOf(player).Tell($"Prospecting Shaped Region progress: {region.RemainingAreas.Count} map(s) remaining.");
        }
    }

    static List<string> BuildCluster(AtlasEntry start, int maxSize)
    {
        var result = new List<string> { start.AreaId };
        var visited = new HashSet<int> { start.Row };
        var queue = new Queue<AtlasEntry>();
        queue.Enqueue(start);

        while (queue.Count > 0 && result.Count < maxSize)
        {
            var cur = queue.Dequeue();
            foreach (int nextRow in cur.Connections)
            {
                if (result.Count >= maxSize) break;
                if (!visited.Add(nextRow) || !_byRow.TryGetValue(nextRow, out var next)) continue;
                if (!next.IsNormalMap || next.IsUniqueMap || next.Region != start.Region) continue;
                if (!result.Contains(next.AreaId, StringComparer.OrdinalIgnoreCase))
                    result.Add(next.AreaId);
                queue.Enqueue(next);
            }
        }
        return result;
    }

    static SavedRegion? LoadRegion(Player player)
    {
        string? raw = CharacterSaves.ModuleData(CharacterName(player), RegionSaveKey);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<SavedRegion>(raw); }
        catch (JsonException) { return null; }
    }

    static void SaveRegion(Player player, SavedRegion region)
    {
        string serialized = region.RemainingAreas.Count == 0 ? "" : JsonSerializer.Serialize(region);
        CharacterSaves.ChangeModuleData(CharacterName(player), RegionSaveKey, _ => serialized);
    }
}
