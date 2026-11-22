using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>
/// Currency used on a box (C→S 0x74, <see cref="InstanceItems.UseItemOnObject"/>): the orbs that change an item's rarity
/// and explicit mods change a closed, uncorrupted box's the same way; one leaves its stack only when the box changed, and
/// the box is created again (same id) with its new rarity and mods, as when it opens.
/// </summary>
public static partial class MapStrongboxes
{
    /// <summary>An Orb of Chance's chance (percent) to make a normal box unique: OUR value, no source gives it.</summary>
    public const int ChanceUnique = 5;

    const string ScrollOfWisdom = "Metadata/Items/Currency/CurrencyIdentification", EngineersOrb = "Metadata/Items/Currency/CurrencyStrongboxQuality";

    /// <summary>GameEvents.Packet(0x74): true when the target is one of this instance's boxes (handled, or refused with
    /// the reason logged); false for any other object.</summary>
    public static async Task<bool> UseItemOn(ClientState conn, byte[] message)
    {
        if (message.Length < InstanceItems.UseItemOnObjectLength) return false;
        var use = InstanceItems.ReadUseItemOnObject(message);
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area)
            || area.Boxes?.FirstOrDefault(b => b.Id == use.TargetObjectId) is not { } target) return false;
        var (remote, stream, ciphers) = conn.Wire;
        string Log(string text) => $"[map-content] {remote} <= 0x0074 item {use.ItemId} (inventory {use.InventoryId}) on strongbox 0x{use.TargetObjectId:x}: {text}";
        var items = ItemsOf(conn);
        if (items.RowOf(use.ItemId) is not byte row || PlayerInventories.InventoryId(row) != use.InventoryId
            || items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } used)
        {
            Console.WriteLine(Log("no such item in the character's inventories (currency from the stash is not handled)"));
            return true;
        }
        var currency = ItemsFor(conn).ByHash(used.Item.TypeHash);
        await area.Gate.WaitAsync();
        try
        {
            var box = area.Boxes![target.Index];
            if (StrongboxCatalog.Find(box.Type) is not { } type) return true;
            var (changed, refusal) = Change(ItemsFor(conn).Mods, type, box, currency?.Id, AreaLevel(conn), AreaMods(conn), Random.Shared);
            if (changed is null)
            {
                Console.WriteLine(Log($"{currency?.Name ?? $"0x{used.Item.TypeHash:x8}"} refused: {refusal}"));
                return true;
            }
            if (items.ConsumeOne(row, used.Id) is not { } taken)
            {
                Console.WriteLine(Log("the item is gone"));
                return true;
            }
            area.Boxes[box.Index] = changed;
            Keep(conn, area);
uint inventory = PlayerInventories.InventoryId(row);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [used.Id], taken.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: {currency!.Name} used" + (taken.Left is { } l ? $", {l.Item.StackSize} left" : ", stack gone"));
            Console.WriteLine(Log($"{currency.Name}: {Label(conn, box)} → {Label(conn, changed)}"));
            // ObjectMagicProperties has a 0x2bc reader: update the box's rarity and mods in place, where the client's
            // nameplate and selection stay put (a new object dropped the label). Only a change to the Chest flag byte — the
            // trap mods' warning bit, which has no 0x2bc reader — needs a new object.
            foreach (var member in Members(conn))
                if (ChestFlags(conn, type, box) != ChestFlags(conn, type, changed)) await Present(member, changed, true);
                else await UpdateBoxMods(member, type, changed);
        }
        finally { area.Gate.Release(); }
        return true;
    }

    /// <summary>The box's ObjectMagicProperties by S→C 0x2bc: the same bytes a created box's component carries
    /// (<see cref="QuestObject"/>), so the client rerolls the nameplate, rarity colour and mod lines without a new object.</summary>
    static async Task UpdateBoxMods(ClientState conn, StrongboxCatalog.Definition type, Box box)
    {
        int index = type.Components.ToList().IndexOf("ObjectMagicProperties");
        if (index < 0) return;
        var mods = box.Mods.Length > 0 ? [.. type.Mods, .. box.Explicits] : type.Mods;
        var w = new BlobWriter();
        if (mods.Count == 0 && box.Rarity == 0) w.U16(0).U8(0).U8(0);
        else { MagicProperties.Write(w, box.Rarity, true, mods); w.U8(0); }
        var (remote, stream, ciphers) = conn.Wire;
        await SendWorldRecord(remote, stream, ciphers, conn, box.Id, InstanceEntities.ComponentUpdate(box.Id, (byte)index, w.ToArray()),
            $"strongbox 0x{box.Id:x} mods updated in place: {Label(conn, box)}");
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
