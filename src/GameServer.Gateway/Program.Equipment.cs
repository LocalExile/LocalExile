using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static PlayerInventories ItemsOf(ClientState conn) => PlayerInventories.For(string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character, Items);

    // The flat armour and evasion of the equipped body armour, helmet, gloves and boots. Only the base type's ArmourTypes
    // row is used (its minimum roll, matching the item's default base percentile); local/global mods and quality are not
    // modelled. Shields are excluded for now. Inactive items (requirements not met) give none.
    static (int Armour, int Evasion, int EnergyShield) EquippedBaseDefences(ClientState conn)
    {
        var items = ItemsOf(conn);
        int armour = 0, evasion = 0, energyShield = 0;
        foreach (byte row in new[] { PlayerInventories.BodyArmour, PlayerInventories.Helmet, PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Offhand1 })
            if (items.Items(row) is [var equipped, ..] && !IsInactive(conn, equipped.Id)
                && Items.ByHash(equipped.Item.TypeHash) is { } type && Items.Defences(type) is { } d)
            {
                armour += d.ArmourMin;
                evasion += d.EvasionMin;
                var local = ItemModStats.Local(equipped.Item, Items.Mods);
                int flat = d.EnergyShieldMin + local.GetValueOrDefault("local_energy_shield")
                    + local.GetValueOrDefault("local_evasion_rating_and_energy_shield");
                int increase = local.GetValueOrDefault("local_energy_shield_+%") + local.GetValueOrDefault("local_armour_and_energy_shield_+%")
                    + local.GetValueOrDefault("local_evasion_and_energy_shield_+%") + local.GetValueOrDefault("local_armour_and_evasion_and_energy_shield_+%");
                // Armour quality is a separate multiplier since 3.25. Item's base percentile is currently zero (minimum).
                if (local.GetValueOrDefault("local_no_energy_shield") == 0)
                    energyShield += (int)(EnergyShield.Max(flat, increase) * (100L + equipped.Item.Quality) / 100);
            }
        return (armour, evasion, energyShield);
    }

    // The equipped main/off hands in both weapon sets. Rows 2/3 are Weapon1 and rows 14/15 Weapon2.
    // The local stats of the active main hand and off hand items (the first weapon set, else the second), for the panel's
    // hand rows (PanelStats).
    // The item in the main hand (row 2, the active set) and its base type, unless its requirements are not met.
    static (Item Item, ItemCatalog.BaseType? Type)? ActiveMainHand(ClientState conn) => ActiveHand(conn, PlayerInventories.Weapon1);

    // The item in the off hand (row 3, the active set). The weapon swap trades the hands' items with rows 14/15
    // (SwapWeaponSets), so Offhand2 always holds the inactive set's off hand, which is never swung.
    static (Item Item, ItemCatalog.BaseType? Type)? ActiveOffHand(ClientState conn) => ActiveHand(conn, PlayerInventories.Offhand1);

    static (Item Item, ItemCatalog.BaseType? Type)? ActiveHand(ClientState conn, byte row) =>
        ItemsOf(conn).Items(row).Where(i => !IsInactive(conn, i.Id))
            .Select(i => ((Item, ItemCatalog.BaseType?)?)(i.Item, Items.ByHash(i.Item.TypeHash))).FirstOrDefault();

    // The main hand's attack (2026-10-23): the weapon in the hand (row 2, the active set) by its WeaponTypes row, its
    // physical damage with the weapon's own local mods (local_physical_damage_+%, local_minimum / maximum_added_physical_damage)
    // and its attack time with local_attack_speed_+%; no weapon, the class's unarmed attack.
    static SkillCatalog.Unarmed WeaponFor(ClientState conn) => WeaponOf(conn, ActiveMainHand(conn));

    static SkillCatalog.Unarmed WeaponOf(ClientState conn, (Item Item, ItemCatalog.BaseType? Type)? mainHand) =>
        WeaponIn(conn, mainHand) ?? Skills.UnarmedFor(conn.ClassRow);

    // A hand's weapon attack (WeaponOf); null when the hand holds no weapon (empty, a shield, a quiver).
    static SkillCatalog.Unarmed? WeaponIn(ClientState conn, (Item Item, ItemCatalog.BaseType? Type)? hand)
    {
        var unarmed = Skills.UnarmedFor(conn.ClassRow);
        if (hand is not var (item, type) || type is null || Skills.WeaponBase(type.Row) is not var (ms, min, max, range)) return null;
        var local = ItemModStats.Local(item, Items.Mods);
        double scale = 1 + local.GetValueOrDefault("local_physical_damage_+%") / 100.0;
        int lo = (int)((min + local.GetValueOrDefault("local_minimum_added_physical_damage")) * scale);
        int hi = (int)((max + local.GetValueOrDefault("local_maximum_added_physical_damage")) * scale);
        int attackMs = (int)Math.Round(ms / (1 + local.GetValueOrDefault("local_attack_speed_+%") / 100.0));
        return new SkillCatalog.Unarmed(Math.Max(1, attackMs), Math.Max(0, lo), Math.Max(lo, hi), Math.Max(range, unarmed.Range));
    }

    // The off-hand weapon of a dual-wielded attack (the active set's Offhand1), with the same values WeaponIn gives the main
    // hand. Null when there is nothing to swing with there (empty, a shield, a quiver) or the main hand holds no weapon.
    static SkillCatalog.Unarmed? OffhandWeaponFor(ClientState conn) =>
        WeaponIn(conn, ActiveMainHand(conn)) is null ? null : WeaponIn(conn, ActiveOffHand(conn));

    static (Dictionary<string, int> Main, Dictionary<string, int> Off) HandLocalStats(ClientState conn)
    {
        var items = ItemsOf(conn);
        Dictionary<string, int> Local(byte row, byte swap) =>
            items.Items(row).Concat(items.Items(swap)).Where(i => !IsInactive(conn, i.Id)).Select(i => i.Item).FirstOrDefault() is { } item
                ? ItemModStats.Local(item, Items.Mods) : [];
        return (Local(2, 14), Local(3, 15));
    }

    // activeSet: only the hands' rows (the active weapon set), for what the player swings now (the attack animation);
    // otherwise both sets, as the passive conditions read them.
    static WeaponProfile WeaponProfileFor(ClientState conn, bool activeSet = false)
    {
        var items = ItemsOf(conn);
        // An inactive weapon (requirements not met) is not there at all: the class is unarmed, as the default attack's
        // unarmed fallback (WeaponFor) is.
        ItemCatalog.BaseType? At(byte row) => items.Items(row).Where(i => !IsInactive(conn, i.Id))
            .Select(i => Items.ByHash(i.Item.TypeHash)).FirstOrDefault(t => t is not null);
        static bool OneHandMelee(ItemCatalog.BaseType t) => t.Class is "Claw" or "Dagger" or "Rune Dagger"
            or "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre";
        static bool OneHand(ItemCatalog.BaseType t) => OneHandMelee(t) || t.Class == "Wand";
        static bool TwoHandMelee(ItemCatalog.BaseType t) => t.Class is "Two Hand Sword" or "Two Hand Axe"
            or "Two Hand Mace" or "Staff" or "Warstaff";
        static bool Pair(ItemCatalog.BaseType? main, ItemCatalog.BaseType? off) =>
            main is { } m && off is { } o && OneHand(m) && OneHand(o);
        var main1 = At(2);
        var off1 = At(3);
        var main2 = activeSet ? null : At(14);
        var off2 = activeSet ? null : At(15);
        var all = new[] { main1, off1, main2, off2 }.OfType<ItemCatalog.BaseType>().ToList();
        var dual = Pair(main1, off1) || Pair(main2, off2);
        var claws = (main1?.Class == "Claw" && off1?.Class == "Claw") || (main2?.Class == "Claw" && off2?.Class == "Claw");
        var unarmed = main1 is null && main2 is null;
        var mainItem = items.Items(2).Concat(activeSet ? [] : items.Items(14)).Where(i => !IsInactive(conn, i.Id)).Select(i => i.Item).FirstOrDefault();
        var local = mainItem is null ? [] : ItemModStats.Local(mainItem, Items.Mods);
        return new WeaponProfile(
            Shield: activeSet ? off1?.Class == "Shield" : HasShield(conn),
            DualWield: dual,
            Staff: all.Any(t => t.Class is "Staff" or "Warstaff"),
            Bow: all.Any(t => t.Class == "Bow"),
            DualClaws: claws,
            Unarmed: unarmed,
            OneHand: all.Any(OneHand),
            TwoHand: all.Any(t => TwoHandMelee(t) || t.Class == "Bow"),
            Melee: unarmed || all.Any(t => OneHandMelee(t) || TwoHandMelee(t)),
            OneHandMelee: all.Any(OneHandMelee),
            TwoHandMelee: all.Any(TwoHandMelee),
            Axe: all.Any(t => t.Class is "One Hand Axe" or "Two Hand Axe"),
            Sword: all.Any(t => t.Class is "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword"),
            Mace: all.Any(t => t.Class is "One Hand Mace" or "Two Hand Mace"),
            Claw: all.Any(t => t.Class == "Claw"),
            Dagger: all.Any(t => t.Class is "Dagger" or "Rune Dagger"),
            Wand: all.Any(t => t.Class == "Wand"),
            // The main hand's local mods: increased critical strike chance scales its base, attack speed and accuracy go
            // into PassiveBonus.
            Critical: (main1 ?? main2) is { } main ? Items.WeaponCritical(main) * (100 + local.GetValueOrDefault("local_critical_strike_chance_+%")) / 100 : 0,
            LocalAttackSpeedPct: local.GetValueOrDefault("local_attack_speed_+%"),
            LocalAccuracy: local.GetValueOrDefault("local_accuracy_rating"));
    }

    // The equipped off hand(s): Weapon1's off hand is row 3, Weapon2's is row 15.
    static bool HasShield(ClientState conn) => OffhandTypes(conn).Any(t => t.Class == "Shield");

    static int ShieldBaseBlock(ClientState conn) => OffhandTypes(conn).Where(t => t.Class == "Shield")
        .Select(Items.ShieldBlock).DefaultIfEmpty(0).Max();

    static IEnumerable<ItemCatalog.BaseType> OffhandTypes(ClientState conn)
    {
        var items = ItemsOf(conn);
        foreach (byte row in new byte[] { PlayerInventories.Offhand1, 15 })
            foreach (var item in items.Items(row))
                if (Items.ByHash(item.Item.TypeHash) is { } type) yield return type;
    }

    static PlayerInventories.Shape? ShapeOf(Item i) => Items.ByHash(i.TypeHash) is { } t
        ? new PlayerInventories.Shape(t.Width, t.Height, t.Class, t.MaxStack,
            t.BecomesWhenFull && Items.Row(t.FullStackRow) is { Supported: true } full ? () => ItemCatalog.Create(full) : null)
        : null;

    // The weapon type of an equipment row's item (its BaseItemTypes row), or null: the active (not inactive) item.
    static ItemCatalog.BaseType? HandType(ClientState conn, byte row) =>
        ItemsOf(conn).Items(row).Where(i => !IsInactive(conn, i.Id))
            .Select(i => Items.ByHash(i.Item.TypeHash)).FirstOrDefault();

    // The record's type is the item's ItemClasses row + 1: the client checks skills' weapon restrictions against it.
    static VisibleItem? LookOf(Item i) => Items.ByHash(i.TypeHash) is { } t
        ? new VisibleItem(0, t.VisualIdentity, t.Stance,
            t.ClassRow is >= 0 and < 255 ? (byte)(t.ClassRow + 1) : (byte)1) : null;

    // What the model wears: the equipped items, dressed in the worn cosmetics (CosmeticEffects). A skin replaces the item's
    // art in its entry's first reference and a weapon effect fills the second; attachments and character effects fill the
    // model slots no item uses, which go out empty when nothing is worn there so taking one off clears it.
    static IReadOnlyList<VisibleItem> Looks(ClientState conn, PlayerInventories items)
    {
        var looks = items.Equipped(LookOf).ToList();
        // A bow is held in the left hand, and its model slot is not the main hand's: BowSetup holds the record the live server
        // sends (the bow's art in slot 1, an arrow in slot 0 with the weapon type and stance, the quiver on the back in 7).
        if (HandType(conn, PlayerInventories.Weapon1) is { Class: "Bow" } bow)
        {
            int quiver = looks.FindIndex(l => l.Slot == 1 && l.VisualIdentity != 0);
            ushort quiverArt = quiver >= 0 ? looks[quiver].VisualIdentity : Items.Visual("DefaultQuiver") ?? 0x328b;
            looks.RemoveAll(l => l.Slot is 0 or 1 or 7);
            looks.AddRange(BowSetup.Entries(bow, Items.Visual("DefaultArrow") ?? 0xb2f6, quiverArt));
        }
        var cosmeticSlots = CosmeticEffects.CosmeticOnlySlots.ToDictionary(s => s, VisibleItem.Empty);
        foreach (var worn in WornCosmetics(conn))
        {
            if (MtxEffects.SlotOf(worn.Slot) is not { ModelSlot: byte model } slot || MtxEffects.Of(worn.MtxTypeHash) is not { } fx) continue;
            if (cosmeticSlots.ContainsKey(model))
            {
                if (fx.Visual != 0)
                    cosmeticSlots[model] = new VisibleItem(model, fx.Visual) { SkinVisual = fx.Visual, SkinHash = worn.MtxTypeHash };
                continue;
            }
            int at = looks.FindIndex(l => l.Slot == model);
            if (at < 0 || looks[at].VisualIdentity == 0) continue;   // nothing equipped there to dress
            if (slot.IsWeaponEffect && fx.WeaponEffect != 0)
                looks[at] = looks[at] with { EffectVisual = fx.WeaponEffect, EffectHash = worn.MtxTypeHash };
            else if (!slot.IsWeaponEffect && fx.Visual != 0)
                looks[at] = looks[at] with { SkinVisual = fx.Visual, SkinHash = worn.MtxTypeHash };
        }
        return [.. looks, .. cosmeticSlots.Values];
    }

    // The stats worn cosmetics give the character (footprints, apparitions, …), summed per stat. A pet is the stat the client
    // spawns it from: pet_slot1 for the first Pet slot (MicrotransactionSlot 18), pet_slot2 for the second (19).
    // Skill Gem Effect (slot 49) is left out: its skill_art_variation belongs to one skill's stats, not the character's.
    static IEnumerable<(uint Stat, int Value)> CosmeticStats(ClientState conn) =>
        WornCosmetics(conn).Where(w => w.Slot != 49).SelectMany(w => MtxEffects.Of(w.MtxTypeHash) is not { } fx ? [] :
                fx.Pet != 0 && Stats.Key(w.Slot == 19 ? "pet_slot2" : "pet_slot1") is uint pet ? [.. fx.Stats, (pet, fx.Pet)] : fx.Stats)
            .GroupBy(s => s.Stat, s => s.Value).Select(g => (g.Key, g.Sum()));

    // What the player wears changed (an item went into or out of a visible equipment row): 0x2ba Inventories for the model,
    // then the Actor skill list again, since the gems socketed in the equipment grant skills.
    static async Task SendEquipment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items)
    {
        var equipped = Looks(conn, items);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerEquipmentUpdate(PlayerId, equipped),
            $"0x02ba player equipment: {string.Join(", ", equipped.Select(e => $"slot {e.Slot} visual 0x{e.VisualIdentity:x4} stance {e.Stance}"))}");
        // The socketed gems may have changed: the Cosmetics tab's skill slots follow them.
        await SendWornCosmetics(remote, stream, ciphers, conn, "equipment changed");
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        lock (conn.Monsters) conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
        await SendSkills(remote, stream, ciphers, conn);
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)),
            "0x01b3 player stats (equipment)");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (equipment)");
    }

    // Dev commands (/mod, /rarity): changes the held item and replaces it on the cursor (0xbf removing and adding its id).
    static async Task ChangeHeldItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Func<Item, Item> change, string what)
    {
        var items = ItemsOf(conn);
        if (items.Items(PlayerInventories.Cursor) is not [var held, ..]) { await conn.Commands!.Reply("Hold an item first"); return; }
        if (!held.Item.Components.Contains("Mods")) { await conn.Commands!.Reply($"{Items.ByHash(held.Item.TypeHash)?.Name} has no Mods component"); return; }
        if (items.Change(PlayerInventories.Cursor, held.Id, change) is not { } changed) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [held.Id], [changed]), $"0x00bf cursor: item {held.Id} {what}");
        await conn.Commands!.Reply(what);
    }

    static ModSection? ParseSection(string text) => text.ToLowerInvariant() switch
    {
        "implicit" or "imp" => ModSection.Implicit,
        "explicit" or "exp" => ModSection.Explicit,
        "enchant" or "ench" => ModSection.Enchant,
        "scourge" or "s10" => ModSection.Scourge,
        "hidden" or "s11" => ModSection.Hidden,
        _ => null,
    };

    // Takes the held item off the cursor (0xbf); null when nothing is held.
    static async Task<InventoryItem?> TakeHeld(string remote, NetworkStream stream, ConnectionCiphers ciphers, PlayerInventories items, string why)
    {
        if (items.Items(PlayerInventories.Cursor) is not [var held, ..] || items.Remove(PlayerInventories.Cursor, held.Id) is null) return null;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [held.Id], []), $"0x00bf cursor: remove item {held.Id} ({why})");
        return held;
    }
}
