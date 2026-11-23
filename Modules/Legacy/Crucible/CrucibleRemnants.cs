using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible Geodes (<c>Igneous Geode</c> -> <c>Primeval Remnant</c>, <c>Crystalline Geode</c> -> <c>Primordial Remnant</c>)
/// and Map Device opening rules for Forge of the Titans (<c>MapDeviceRecipes</c> 52/53).
/// </summary>
public static class CrucibleRemnants
{
    /// <summary>Handles C→S 0x58 right-click on an Igneous Geode or Crystalline Geode.</summary>
    public static async Task<bool> TryOpenGeode(Player player, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        bool uber;
        if (type.Id.Equals(CrucibleTables.IgneousGeodeId, StringComparison.OrdinalIgnoreCase)) uber = false;
        else if (type.Id.Equals(CrucibleTables.CrystallineGeodeId, StringComparison.OrdinalIgnoreCase)) uber = true;
        else return false;

        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        string remnantId = uber ? CrucibleTables.PrimordialRemnantId : CrucibleTables.PrimevalRemnantId;
        if (catalog.Find(remnantId) is not { Supported: true } remnantBase)
        {
            await PlayerOf(player).Tell($"Cannot open {type.Name}: {remnantId} is unavailable.");
            return true;
        }

        var items = ItemsOf(player);
        // Reserve space before consuming the geode (unless the stack is size 1, in which case its own cell frees up).
        bool singleStack = item.Item.StackSize <= 1;
        var free = singleStack
            ? ((byte)item.X, (byte)item.Y)
            : items.FreeCell(PlayerInventories.MainInventory, remnantBase.Width, remnantBase.Height, ShapeOf);
        if (free is not var (tx, ty))
        {
            await PlayerOf(player).Tell("Not enough inventory space to open the Geode.");
            return true;
        }

        var remnant = RollRemnant(remnantBase, rolls, uber, AreaRng(player));
        byte targetRow = singleStack ? row : PlayerInventories.MainInventory;

        PlayerInventories.Consumption? consumed;
        InventoryItem? created;
        // One save: a failed write never takes the geode without giving the Remnant.
        (consumed, created) = Saves.Atomically(() => items.ConsumeOne(row, item.Id) is { } taken
            ? (taken, items.Put(targetRow, new InventoryItem(items.NewItemId(), tx, ty, remnant)))
            : default((PlayerInventories.Consumption?, InventoryItem?)));
        if (consumed is null || created is null) return true;

        if (targetRow == row)
        {
            var added = new List<InventoryItem>();
            if (consumed.Left is { } left) added.Add(left);
            added.Add(created);
            await Send(player,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [item.Id], added),
                $"0x00bf inventory {row}: opened {type.Name} -> {remnantBase.Name}");
        }
        else
        {
            await Send(player,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [item.Id],
                    consumed.Left is { } left ? [left] : []),
                $"0x00bf inventory {row}: consumed {type.Name}");
            await Send(player,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(targetRow), items.NextRevision(targetRow), [], [created]),
                $"0x00bf inventory {targetRow}: created {remnantBase.Name}");
        }

        Console.WriteLine($"[crucible] {CharacterName(player)} opened {type.Name} -> {remnantBase.Name} ({remnant.Mods.Count} mods)");
        return true;
    }

    /// <summary>
    /// Rolls a rare <c>Primeval Remnant</c> with six mods ("half of their mods are upsides and half are downsides",
    /// GGG's Crucible video, 2:38; three and three, Q&amp;A) or <c>Primordial Remnant</c> (one more prefix: its implicit
    /// has <c>local_hidden_maximum_prefixes_allowed_+</c> 1) at its base's <c>DropLevel</c>, 80 and 84.
    /// </summary>
    public static Item RollRemnant(ItemCatalog.BaseType remnantBase, ItemRolls rolls, bool uber, Random random)
    {
        byte ilvl = (byte)remnantBase.DropLevel;
        var blank = ItemCatalog.Create(remnantBase) with
        {
            ItemLevel = ilvl,
            Identified = true,
            Rarity = ItemRarity.Rare,
        };
        var rolled = rolls.Roll(blank, ItemRarity.Rare, random, (6, 3));
        if (uber && rolls.AddAffix(rolled, (4, 3), random) is { } extra)
            rolled = extra;
        return rolled with { ItemLevel = ilvl, Identified = true };
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

        var modCatalog = ItemsFor(request.Player).Mods;
        var explicitMods = new List<IReadOnlyDictionary<uint, int>>(opening.Mods.ExplicitMods);
        var implicitStats = new Dictionary<uint, int>(opening.Mods.Implicit);
        var sum = new Dictionary<uint, int>(opening.Mods.Stats);

        foreach (var itemMod in remnantSlot.Mods)
        {
            if (modCatalog.ByHash(itemMod.Hash) is not { } mod) continue;
            var target = itemMod.Section == ModSection.Explicit ? new Dictionary<uint, int>() : implicitStats;
            for (int i = 0; i < mod.Stats.Count && i < itemMod.Values.Count; i++)
            {
                uint statId = mod.Stats[i].Id;
                int val = itemMod.Values[i];
                target[statId] = target.GetValueOrDefault(statId) + val;
                sum[statId] = sum.GetValueOrDefault(statId) + val;
            }
            if (itemMod.Section == ModSection.Explicit && target.Count > 0)
                explicitMods.Add(target);
        }

        var updatedMods = new MapMods(sum, remnantSlot.Quality)
        {
            ExplicitMods = explicitMods,
            Implicit = implicitStats,
            Fragments = opening.Mods.Fragments,
            FragmentCount = opening.Mods.FragmentCount,
            ScarabCount = opening.Mods.ScarabCount,
            ScarabKinds = opening.Mods.ScarabKinds,
            MapRarity = remnantSlot.Rarity,
            Corrupted = remnantSlot.Corrupted,
        };
        request.Opening = opening with { Mods = updatedMods };
        request.ReturnToSource = true;
    }

    // Our own numbers: GGG never published the geodes' drop rate or levels.
    const double GeodeChance = 0.18;
    const int GeodeAreaLevel = 68, CrystallineAreaLevel = 81;

    /// <summary>The geode of a completed Forge in a map, one roll per encounter: Crystalline only from a full channel.</summary>
    public static Item? RollGeodeDrop(ItemCatalog catalog, int areaLevel, bool fullCharge, double dropRate, Random random)
    {
        if (areaLevel < GeodeAreaLevel || dropRate <= 0) return null;
        if (random.NextDouble() >= Math.Clamp(GeodeChance * dropRate, 0, 1)) return null;
        bool uber = fullCharge && areaLevel >= CrystallineAreaLevel;
        string id = uber ? CrucibleTables.CrystallineGeodeId : CrucibleTables.IgneousGeodeId;
        return catalog.Find(id) is { Supported: true } type ? ItemCatalog.Create(type) : null;
    }
}
