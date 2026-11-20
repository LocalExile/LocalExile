using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible Geodes (<c>Igneous Geode</c> -> <c>Primeval Remnant</c>, <c>Crystalline Geode</c> -> <c>Primordial Remnant</c>)
/// and Map Device opening rules for Forge of the Titans (<c>MapDeviceRecipes</c> 52/53).
/// </summary>
public static class CrucibleRemnants
{
    public const string OpenGeodeAction = "open_crucible_geode";

    /// <summary>Handles C→S 0x58 right-click on an Igneous Geode or Crystalline Geode.</summary>
    public static async Task<bool> TryOpenGeode(ClientState conn, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        bool uber;
        if (type.Id.Equals(CrucibleTables.IgneousGeodeId, StringComparison.OrdinalIgnoreCase)) uber = false;
        else if (type.Id.Equals(CrucibleTables.CrystallineGeodeId, StringComparison.OrdinalIgnoreCase)) uber = true;
        else return false;

        var catalog = ItemsFor(conn);
        var rolls = RollsFor(conn);
        string remnantId = uber ? CrucibleTables.PrimordialRemnantId : CrucibleTables.PrimevalRemnantId;
        if (catalog.Find(remnantId) is not { Supported: true } remnantBase)
        {
            await PlayerOf(conn).Tell($"Cannot open {type.Name}: {remnantId} is unavailable.");
            return true;
        }

        var items = ItemsOf(conn);
        // Reserve space before consuming the geode (unless the stack is size 1, in which case its own cell frees up).
        bool singleStack = item.Item.StackSize <= 1;
        var free = singleStack
            ? ((byte)item.X, (byte)item.Y)
            : items.FreeCell(PlayerInventories.MainInventory, remnantBase.Width, remnantBase.Height, ShapeOf);
        if (free is not var (tx, ty))
        {
            await PlayerOf(conn).Tell("Not enough inventory space to open the Geode.");
            return true;
        }

        var remnant = RollRemnant(remnantBase, rolls, uber, Random.Shared);
        byte targetRow = singleStack ? row : PlayerInventories.MainInventory;

        PlayerInventories.Consumption? consumed;
        InventoryItem? created;
        lock (conn.Monsters)
        {
            consumed = items.ConsumeOne(row, item.Id);
            if (consumed is null) return true;
            created = items.Put(targetRow, new InventoryItem(items.NewItemId(), tx, ty, remnant));
        }

        var (remote, stream, ciphers) = conn.Wire;
        if (targetRow == row)
        {
            var added = new List<InventoryItem>();
            if (consumed.Left is { } left) added.Add(left);
            added.Add(created);
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [item.Id], added),
                $"0x00bf inventory {row}: opened {type.Name} -> {remnantBase.Name}");
        }
        else
        {
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [item.Id],
                    consumed.Left is { } left ? [left] : []),
                $"0x00bf inventory {row}: consumed {type.Name}");
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(targetRow), items.NextRevision(targetRow), [], [created]),
                $"0x00bf inventory {targetRow}: created {remnantBase.Name}");
        }

        Console.WriteLine($"[crucible] {CharacterName(conn)} opened {type.Name} -> {remnantBase.Name} ({remnant.Mods.Count} mods)");
        return true;
    }

    /// <summary>
    /// Rolls a <c>Primeval Remnant</c> (ilvl 80, 2–3 prefixes + 2–3 suffixes) or <c>Primordial Remnant</c>
    /// (ilvl 84, 3–4 prefixes via <c>local_hidden_maximum_prefixes_allowed_+</c> + 2–3 suffixes) with its
    /// combining-forge implicit, identified and corrupted (matching 3.21 Geode behavior).
    /// </summary>
    public static Item RollRemnant(ItemCatalog.BaseType remnantBase, ItemRolls rolls, bool uber, Random random)
    {
        byte ilvl = (byte)(uber ? 84 : 80);
        var blank = ItemCatalog.Create(remnantBase) with
        {
            ItemLevel = ilvl,
            Identified = true,
            Rarity = ItemRarity.Rare,
        };
        var rolled = rolls.Roll(blank, ItemRarity.Rare, random);
        if (uber && rolls.AddAffix(rolled, 4, random) is { } extra)
            rolled = extra;
        return rolled with
        {
            ItemLevel = ilvl,
            Identified = true,
            Corrupted = true,
        };
    }

    /// <summary>
    /// <see cref="GameEvents.MapOpening"/> handler: when the Map Device opens Forge of the Titans via
    /// <c>MapDeviceRecipes</c> 52/53, propagate the Remnant's rolled Domain 33 mods onto <see cref="MapOpening.Opening"/>
    /// (since <see cref="MapRunCatalog.OfRecipe"/> only reads recipe-level mods).
    /// </summary>
    public static void OnMapOpening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening) return;
        if (!CrucibleTables.IsForgeArea(opening.Area.Id)) return;

        var remnantSlot = request.Slots.FirstOrDefault(s =>
            s.BaseId.Equals(CrucibleTables.PrimevalRemnantId, StringComparison.OrdinalIgnoreCase)
            || s.BaseId.Equals(CrucibleTables.PrimordialRemnantId, StringComparison.OrdinalIgnoreCase));
        if (remnantSlot.TypeHash == 0) return;

        var modCatalog = ItemsFor(request.Conn).Mods;
        var explicitMods = new List<IReadOnlyDictionary<string, int>>();
        var implicitStats = new Dictionary<string, int>(StringComparer.Ordinal);
        var sum = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var itemMod in remnantSlot.Mods)
        {
            if (modCatalog.ByHash(itemMod.Hash) is not { } mod) continue;
            var target = itemMod.Section == ModSection.Explicit ? new Dictionary<string, int>(StringComparer.Ordinal) : implicitStats;
            for (int i = 0; i < mod.Stats.Count && i < itemMod.Values.Count; i++)
            {
                string statId = mod.Stats[i].Id;
                int val = itemMod.Values[i];
                target[statId] = target.GetValueOrDefault(statId) + val;
                sum[statId] = sum.GetValueOrDefault(statId) + val;
            }
            if (itemMod.Section == ModSection.Explicit && target.Count > 0)
                explicitMods.Add(target);
        }

        // Preserve any existing mods already placed on opening.Mods (e.g., from Idols or other modules).
        foreach (var (k, v) in opening.Mods.Stats)
            if (!sum.ContainsKey(k)) sum[k] = v;

        bool uber = CrucibleTables.IsUberForgeArea(opening.Area.Id);
        int level = uber ? 84 : 80;
        var updatedMods = new MapMods(sum, remnantSlot.Quality)
        {
            ExplicitMods = explicitMods,
            Implicit = implicitStats,
            MapRarity = remnantSlot.Rarity,
            Corrupted = remnantSlot.Corrupted,
        };
        request.Opening = opening with
        {
            Area = opening.Area with { Level = level },
            Mods = updatedMods,
        };
        request.ReturnToSource = true;
    }

    /// <summary>Creates a geode drop for maps (Igneous in T1–T16, Crystalline chance in red/T14+ maps).</summary>
    public static Item? RollGeodeDrop(ItemCatalog catalog, int areaLevel, double dropRate, Random random)
    {
        if (areaLevel < 68 || dropRate <= 0) return null;
        // Base ~18% chance per map Crucible encounter completion (scales with dropRate setting).
        double chance = Math.Clamp(0.18 * dropRate, 0, 1);
        if (random.NextDouble() >= chance) return null;
        bool uber = areaLevel >= 81 && random.NextDouble() < 0.25;
        string id = uber ? CrucibleTables.CrystallineGeodeId : CrucibleTables.IgneousGeodeId;
        return catalog.Find(id) is { Supported: true } type ? ItemCatalog.Create(type) : null;
    }
}
