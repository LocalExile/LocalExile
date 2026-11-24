using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// Currency used on a box (C→S 0x74, <see cref="InstanceItems.UseItemOnObject"/>): the orbs that change an item's rarity
/// and explicit mods change a closed, uncorrupted box's the same way; one leaves its stack only when the box changed, and
/// the box's rarity and mods are updated in place (ObjectMagicProperties' S→C 0x2bc reader), as when it opens. The orb may
/// sit in a character inventory or in the open stash / currency tab — the client sends the tab's inventory id either way —
/// and is consumed from whichever source holds it (character <c>ConsumeOne</c>, stash <c>Pay</c> + <c>SendStashPayment</c>).
/// </summary>
public static partial class MapStrongboxes
{
    /// <summary>An Orb of Chance's chance (percent) to make a normal box unique: OUR value, no source gives it.</summary>
    public const int ChanceUnique = 5;

    const string ScrollOfWisdom = "Metadata/Items/Currency/CurrencyIdentification", EngineersOrb = "Metadata/Items/Currency/CurrencyStrongboxQuality";

    /// <summary>GameEvents.Packet(0x74): true when the target is one of this instance's boxes (handled, or refused with
    /// the reason logged); false for any other object.</summary>
    public static Task<bool> UseItemOn(Player player, byte[] message) =>
        message.Length < InstanceItems.UseItemOnObjectLength
            ? Task.FromResult(false)
            : UseItemOnCore(player, InstanceItems.ReadUseItemOnObject(message), "0x0074");

    static readonly byte[] CurrencyRows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory];

    /// <summary>GameEvents.Packet(0x79): Shift-click / repeat currency use by type hash on a strongbox.</summary>
    public static Task<bool> UseItemTypeOn(Player player, byte[] message)
    {
        if (message.Length < InstanceItems.UseItemTypeOnObjectLength) return Task.FromResult(false);
        var req = InstanceItems.ReadUseItemTypeOnObject(message);
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area)
            || area.Boxes?.FirstOrDefault(b => b.Id == req.TargetObjectId) is null)
            return Task.FromResult(false);
        var items = ItemsOf(player);
        foreach (byte row in CurrencyRows)
            if (items.Items(row).FirstOrDefault(i => i.Item.TypeHash == req.TypeHash && items.RowOf(i.Id) == row) is { } stack)
                return UseItemOnCore(player, new(PlayerInventories.InventoryId(row), stack.Id, req.TargetObjectId), "0x0079");
        if (OpenStash(player) is { } stash)
            for (int tab = 0; tab < stash.Count; tab++)
                if (stash.IsShownTab(tab) && !stash.IsPublic(tab)
                    && stash.Inventory(tab).Items.FirstOrDefault(i => i.Item.TypeHash == req.TypeHash) is { } stashStack)
                    return UseItemOnCore(player, new(stash.InventoryOf(tab), stashStack.Id, req.TargetObjectId), "0x0079");
        Console.WriteLine($"[map-content] {player.Connection.Remote} <= 0x0079 type 0x{req.TypeHash:x8} on strongbox 0x{req.TargetObjectId:x}: none left in inventory or stash");
        return Task.FromResult(true);
    }

    static AccountStash? OpenStash(Player player) =>
        player.StashOpen || (player.GuildStashOpen && !player.Character.Ssf) ? StashOf(player) : null;

    static async Task<bool> UseItemOnCore(Player player, InstanceItems.UseItemOnObject use, string opcode)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area)
            || area.Boxes?.FirstOrDefault(b => b.Id == use.TargetObjectId) is not { } target) return false;
        string Log(string text) => $"[map-content] {player.Connection.Remote} <= {opcode} item {use.ItemId} (inventory {use.InventoryId}) on strongbox 0x{use.TargetObjectId:x}: {text}";
        var items = ItemsOf(player);
        // The orb may sit in a character inventory or in the open stash / currency tab: the client sends 0x74 with that
        // inventory's id either way (live: a currency-tab orb clicked on a box). Resolve the source before the gate.
        byte row = 0; bool fromChar = false; InventoryItem? used = null;
        if (items.RowOf(use.ItemId) is byte r && PlayerInventories.InventoryId(r) == use.InventoryId
            && items.Items(r).FirstOrDefault(i => i.Id == use.ItemId) is { } ci) { row = r; fromChar = true; used = ci; }
        else if (PlayerInventories.InventoryId(PlayerInventories.Cursor) == use.InventoryId
            && items.Items(PlayerInventories.Cursor).FirstOrDefault(i => i.Id == use.ItemId) is { } hi)
        { row = PlayerInventories.Cursor; fromChar = true; used = hi; }
        AccountStash? stash = null; int stashTab = -1;
        if (used is null && OpenStash(player) is { } s && s.TabOf(use.InventoryId) is int t
            && !s.IsPublic(t)
            && s.Inventory(t).Items.FirstOrDefault(i => i.Id == use.ItemId) is { } si) { stash = s; stashTab = t; used = si; }
        if (used is null)
        {
            Console.WriteLine(Log("no such item in the character's inventories or the open stash"));
            return true;
        }
        var currency = ItemsFor(player).ByHash(used.Item.TypeHash);
        await area.Gate.WaitAsync();
        try
        {
            var box = area.Boxes![target.Index];
            // A box is used from nearby: an open, living player within the object's interaction reach of it (the client
            // enforces its own; this refuses a use that cannot be a click on the box).
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (box.X, box.Y), 10)) return true;
            if (StrongboxCatalog.Find(box.Type) is not { } type) return true;
            var (changed, refusal) = Change(ItemsFor(player).Mods, type, box, currency?.Id, AreaLevel(player), AreaMods(player), AreaRng(player));
            if (changed is null)
            {
                Console.WriteLine(Log($"{currency?.Name ?? $"0x{used.Item.TypeHash:x8}"} refused: {refusal}"));
                return true;
            }
            // Consume one orb from whichever source holds it; only then commit the box.
            Func<Task> notify;
            if (fromChar)
            {
                if (items.ConsumeOne(row, used.Id) is not { } taken) { Console.WriteLine(Log("the item is gone")); return true; }
                uint inventory = PlayerInventories.InventoryId(row);
                notify = () => Send(player, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [used.Id], taken.Left is { } left ? [left] : []),
                    $"0x00bf inventory {inventory}: {currency!.Name} used" + (taken.Left is { } l ? $", {l.Item.StackSize} left" : ", stack gone"));
            }
            else
            {
                // One orb from the clicked stack, not the smallest stack of its type anywhere in the stash: the client
                // named (tab, item). SendStashPayment notifies the tab it changed.
                if (PayOne(stash!, stashTab, used) is not { } paid) { Console.WriteLine(Log("the item is gone")); return true; }
                notify = () => SendStashPayment(player, stash!, paid);
            }
            area.Boxes[box.Index] = changed;
            Keep(player, area);
            await notify();
            Console.WriteLine(Log($"{currency!.Name}: {Label(player, box)} → {Label(player, changed)}"));
            // ObjectMagicProperties has a 0x2bc reader: update the box's rarity and mods in place, where the client's
            // nameplate and selection stay put (a new object dropped the label). A unique roll and a change of the Chest
            // flag byte (the trap mods' warning bit, which has no 0x2bc reader) are create-only: words and model come
            // with the object. Both go out once for the area, not once per member.
            bool recreate = changed.Unique != box.Unique || ChestFlags(player, type, box) != ChestFlags(player, type, changed);
            if (recreate)
                foreach (var member in Members(player)) await Present(member, changed, true);
            else
            {
                await UpdateBoxMods(player, type, changed);
                area.Shown[changed.Id] = changed;
                // The closed glow is the rarity's: an in-place update keeps the box object, so its effect is worn again
                // for the new rarity (one glow for the area, not one per member).
                await Wear(player, changed);
            }
        }
        finally { area.Gate.Release(); }
        return true;
    }

    /// <summary>One unit of the stack the client named by (tab, item): <see cref="AccountStash.Shrink"/> for a stack that
    /// keeps some, <see cref="AccountStash.Take"/> for the last one. Null when the stack is gone. Shared with the essence
    /// monolith's Vaal Orb.</summary>
    internal static AccountStash.Payment? PayOne(AccountStash stash, int tab, InventoryItem item)
    {
        var tabs = new Dictionary<int, (List<uint> Removed, List<InventoryItem> Changed)>();
        if (item.Item.StackSize > 1)
        {
            if (stash.Shrink(tab, item.Id, 1) is not { } left) return null;
            tabs[tab] = ([], [left]);
        }
        else
        {
            if (stash.Take(tab, item.Id) is not { } taken) return null;
            tabs[tab] = ([taken.Id], []);
        }
        return new(tabs);
    }

    /// <summary>The box's ObjectMagicProperties by S→C 0x2bc: the same bytes a created box's component carries
    /// (<see cref="QuestObject"/>), so the client rerolls the nameplate, rarity colour and mod lines without a new object.</summary>
    static async Task UpdateBoxMods(Player player, StrongboxCatalog.Definition type, Box box)
    {
        int index = type.Components.ToList().IndexOf("ObjectMagicProperties");
        if (index < 0) return;
        var mods = box.Mods.Length > 0 ? [.. type.Mods, .. box.Explicits] : type.Mods;
        var w = new BlobWriter();
        if (mods.Count == 0 && box.Rarity == 0) w.U16(0).U8(0).U8(0);
        else { MagicProperties.Write(w, box.Rarity, true, mods); w.U8(0); }
        await SendWorldRecord(player, box.Id, InstanceEntities.ComponentUpdate(box.Id, (byte)index, w.ToArray()),
            $"strongbox 0x{box.Id:x} mods updated in place: {Label(player, box)}");
    }

    /// <summary>What the currency (a BaseItemTypes id) makes of the box, or null with the reason: only a closed,
    /// uncorrupted box of a type that rolls mods changes.</summary>
    internal static (Box? Box, string? Refusal) Change(ModCatalog catalog, StrongboxCatalog.Definition type, Box box, string? currency,
        int level, MapMods stat, Random rng)
    {
        if (box.Phase != 0) return (null, "the box is open");
        if (box.Corrupted) return (null, "the box is corrupted");
        if (!type.Rollable) return (null, "the box cannot be modified");
        // OUR choice: no source says what currency does to a unique box; it keeps its fixed mods.
        if (box.Unique is not null) return (null, "a unique box takes no currency");
        var mods = box.Explicits;
        (Box?, string?) With(byte rarity, IReadOnlyList<ItemMod> rolled) => (box with { Rarity = rarity, Mods = Kept(rolled) }, null);
        static (Box?, string?) Fail(string why) => (null, why);
        switch (currency)
        {
            case CurrencyEffects.OrbOfTransmutation:
                return box.Rarity != 0 ? Fail("not a normal box") : With(1, StrongboxMods.Roll(catalog, type, 1, level, stat, rng));
            case CurrencyEffects.OrbOfAlchemy:
                return box.Rarity != 0 ? Fail("not a normal box") : With(2, StrongboxMods.Roll(catalog, type, 2, level, stat, rng));
            case CurrencyEffects.OrbOfAlteration:
                return box.Rarity != 1 ? Fail("not a magic box") : With(1, StrongboxMods.Roll(catalog, type, 1, level, stat, rng));
            case CurrencyEffects.ChaosOrb:
                return box.Rarity != 2 ? Fail("not a rare box") : With(2, StrongboxMods.Roll(catalog, type, 2, level, stat, rng));
            case CurrencyEffects.OrbOfAugmentation:
            {
                if (box.Rarity != 1) return Fail("not a magic box");
                var more = StrongboxMods.Add(catalog, type, mods, 1, 1, level, stat, rng);
                return more.Count > mods.Count ? With(1, more) : Fail("no room for another mod");
            }
            case CurrencyEffects.RegalOrb:
                return box.Rarity != 1 ? Fail("not a magic box") : With(2, StrongboxMods.Add(catalog, type, mods, 2, 1, level, stat, rng));
            case CurrencyEffects.ExaltedOrb:
            {
                if (box.Rarity != 2) return Fail("not a rare box");
                var more = StrongboxMods.Add(catalog, type, mods, 2, 1, level, stat, rng);
                return more.Count > mods.Count ? With(2, more) : Fail("no room for another mod");
            }
            case CurrencyEffects.OrbOfAnnulment:
            {
                if (mods.Count == 0) return Fail("the box has no mod to remove");
                int removed = rng.Next(mods.Count);
                return With(box.Rarity, [.. mods.Where((_, i) => i != removed)]);
            }
            case CurrencyEffects.DivineOrb:
                return mods.Count == 0 ? Fail("the box has no mod to reroll") : With(box.Rarity, StrongboxMods.Reroll(catalog, mods, stat, rng));
            // A box's implicits have fixed values (every Chests mod's range is one value): nothing to reroll.
            case CurrencyEffects.BlessedOrb: return Fail("the box's implicit has no range");
            case CurrencyEffects.OrbOfScouring:
                return box.Rarity == 0 ? Fail("already a normal box") : With(0, []);
            // A Vaal Orb corrupts the box: no currency changes it again, its rarity and mods stay, and its contents drop
            // corrupted (StrongboxLoot).
            case CurrencyEffects.VaalOrb:
                return (box with { Corrupted = true }, null);
            // Orb of Chance on a normal box, as on an item: a unique strongbox of its base type (poewiki, Strongbox: "it is
            // possible to craft a unique strongbox using an Orb of Chance on a normal strongbox with the corresponding base
            // type") at OUR 5% when one exists for the area level, else its affixes as ItemRolls.Chance rolls an item's
            // (rare at 30%, else magic).
            case CurrencyEffects.OrbOfChance:
            {
                if (box.Rarity != 0) return Fail("not a normal box");
                var uniques = StrongboxCatalog.Uniques.Where(u => u.ChestRow == type.Row && level >= u.MinLevel).ToArray();
                if (uniques.Length > 0 && rng.Next(100) < ChanceUnique)
                {
                    long pick = rng.NextInt64(uniques.Sum(u => (long)u.Weight));
                    var unique = uniques.FirstOrDefault(u => (pick -= u.Weight) < 0) ?? uniques[^1];
                    return (box with
                    {
                        Unique = unique.Id, Rarity = 3,
                        Mods = Kept(unique.ModRows.Select(r => catalog.Row(r)?.Roll(ModSection.Explicit, rng)).OfType<ItemMod>()),
                    }, null);
                }
                if (rng.Next(100) < 30) return With(2, StrongboxMods.Roll(catalog, type, 2, level, stat, rng));
                var magic = StrongboxMods.Add(catalog, type, [], 1, rng.Next(2, 5), level, stat, rng);
                return magic.Count > 0 ? With(1, magic) : Fail("no mod can roll on the box");
            }
            // Boxes are sent identified (the map mod that makes them unidentified is not consumed): nothing to identify.
            case ScrollOfWisdom: return Fail("the box is identified");
            // CurrencyItems: "This item is no longer usable." / "Formerly improved the quality of a Strongbox".
            case EngineersOrb: return Fail("Engineer's Orb is no longer usable (CurrencyItems Directions)");
            default: return Fail("this currency does nothing to a strongbox");
        }
    }
}
