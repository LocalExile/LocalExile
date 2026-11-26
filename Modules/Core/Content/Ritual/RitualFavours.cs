using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// The Ritual Favour reward window: generates level- &amp; altar-themed reward pages, packs them into the 12×7 Manager-1
/// shop inventory (<see cref="RitualWire.ShopInventoryId"/>), reveals hidden slots as altars complete, handles Buy
/// (<c>0x0283</c>), Defer (<c>0x0284</c>, persisted per account/league in <c>AccountData</c> under <c>ritual.deferred</c>
/// and mirrored to Manager-0 row 57 <c>RitualSavedRewards1</c>), Reroll (<c>0x0168</c>), and Open (<c>0x0169</c> / Done altar click).
/// </summary>
public static class RitualFavours
{
    public const string DeferredDataKey = "ritual.deferred";
    const int GridWidth = 12, GridHeight = 7;
    static readonly JsonSerializerOptions Json = CharacterSaves.Opts;

    public sealed record DeferredEntry(
        Guid Key,
        CharacterSaves.SavedItem Item,
        int BuyCost,
        int OriginalCost,
        int CooldownMaps,
        int MissedCount);

    public sealed class Offer
    {
        public required uint ItemId;
        public required byte X;
        public required byte Y;
        public required byte Width;
        public required byte Height;
        public required Item RealItem;
        public required Item HiddenItem;
        public required ushort BuyCost;
        public required ushort OriginalCost;
        public byte Flag;
        public bool Revealed;
        public bool Purchased;
        public bool DeferredThisPage;
        public Guid? DeferredKey;
        public int RevealAtAltar;
    }

    // ---- AccountData deferred persistence (league-isolated via PersonalStashOf) ----------------------------------------

    public static List<DeferredEntry> LoadDeferred(Player player)
    {
        string? raw = PersonalStashOf(player).ModuleData(DeferredDataKey);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try { return JsonSerializer.Deserialize<List<DeferredEntry>>(raw, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void SaveDeferred(Player player, List<DeferredEntry> list)
    {
        string encoded = JsonSerializer.Serialize(list, Json);
        PersonalStashOf(player).ChangeModuleData(DeferredDataKey, _ => encoded);
    }

    /// <summary>Decrements cooldown counters for deferred items once when a new Ritual area is populated.</summary>
    public static void TickDeferredCooldowns(Player player)
    {
        var list = LoadDeferred(player);
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].CooldownMaps > 0)
            {
                list[i] = list[i] with { CooldownMaps = list[i].CooldownMaps - 1 };
                changed = true;
            }
        }
        if (changed) SaveDeferred(player, list);
    }

    // ---- Page generation & packing -------------------------------------------------------------------------------------

    /// <summary>Builds a fresh page of offers (deferred due items + freshly rolled rewards), packed into the 12×7 grid
    /// with strictly ascending <see cref="Offer.ItemId"/> values for client binary search.</summary>
    public static List<Offer> GeneratePage(Player player, RitualEncounter.State state, Random rng, bool isReroll)
    {
        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        var uniques = UniquesFor(player);
        var rune = state.Altars.Count > 0 ? state.Altars[0].Rune : null;
        string runeKind = rune?.Type ?? "";
        string runeId = rune?.Id ?? "";
        bool opulent = runeKind.Contains("Golden", StringComparison.OrdinalIgnoreCase) || runeId.Contains("Golden", StringComparison.OrdinalIgnoreCase);
        bool vaal = runeKind.Contains("Vaal", StringComparison.OrdinalIgnoreCase) || runeId.Contains("Vaal", StringComparison.OrdinalIgnoreCase);
        bool nameless = runeKind.Contains("Nameless", StringComparison.OrdinalIgnoreCase) || runeId.Contains("Nameless", StringComparison.OrdinalIgnoreCase);

        var candidates = new List<(Item Item, int BuyCost, int OrigCost, byte Flag, Guid? DeferredKey)>();

        // 1. Pull due deferred items (up to 6 per page, CooldownMaps == 0).
        if (state.Level >= RitualTables.MinDeferAreaLevel)
        {
            var deferred = LoadDeferred(player);
            int addedDeferred = 0;
            foreach (var entry in deferred)
            {
                if (entry.CooldownMaps > 0 || addedDeferred >= 6) continue;
                try
                {
                    var rebuilt = CharacterSaves.FromSaved(entry.Item, catalog);
                    byte flag = entry.MissedCount >= 2 ? RitualWire.FlagDeferredExpiring
                        : entry.MissedCount == 1 ? RitualWire.FlagDeferredWarning
                        : RitualWire.FlagDeferred;
                    candidates.Add((rebuilt, Math.Clamp(entry.BuyCost, 1, 65000), Math.Clamp(entry.OriginalCost, 1, 65000), flag, entry.Key));
                    addedDeferred++;
                }
                catch (Exception ex) when (ex is UnknownSavedItemException or InvalidDataException) { }
            }
        }

        // 2. Guarantee extra Itemised Corpses from Ritual Scarab of Corpses (ScarabRitual4).
        for (int c = 0; c < state.ExtraCorpses && RitualTables.ItemisedCorpses.Count > 0; c++)
        {
            string corpseId = RitualTables.ItemisedCorpses[rng.Next(RitualTables.ItemisedCorpses.Count)];
            if (catalog.Find(corpseId) is { Supported: true } ct)
            {
                var item = ItemCatalog.Create(ct) with { ItemLevel = (byte)Math.Clamp(state.Level, 1, 100) };
                int price = PriceWithModifiers(rng.Next(1800, 4200), state, isReroll, rng);
                candidates.Add((item, price, price, RitualWire.FlagNormal, null));
            }
        }

        // 3. Roll fresh items: base 18..24, scaled by map_ritual_reward_count_+% (ScarabRitual3 Abundance = +100%).
        int baseCount = rng.Next(18, 25);
        int targetCount = Math.Clamp(baseCount * Math.Max(50, 100 + state.RewardCountPct) / 100, 12, 48);
        int qualityBoost = state.VesselCount * 20; // +20% improved rewards per Blood-filled Vessel

        // Guarantee at least one Ritual Splinter stack in map areas (level >= 68).
        if (state.Level >= 68 && catalog.Find(RitualTables.RitualSplinterId) is { Supported: true } splinterType)
        {
            int stack = rng.Next(8, 26 + state.VesselCount * 5);
            stack = Math.Clamp(stack * Math.Max(100, 100 + state.SplinterStackPct) / 100, 1, Math.Max(1, (int)splinterType.MaxStack));
            var splinter = ItemCatalog.Create(splinterType) with { StackSize = (ushort)stack };
            int price = PriceWithModifiers(stack * rng.Next(45, 75), state, isReroll, rng);
            candidates.Add((splinter, price, price, RitualWire.FlagNormal, null));
        }

        // Nameless altar: guaranteed Omen and chance for An Audience With The King.
        if (nameless && state.Level >= 81)
        {
            if (rng.Next(100) < 35 + qualityBoost / 2 && catalog.Find(RitualTables.AudienceWithTheKingId) is { Supported: true } kingType)
            {
                int price = PriceWithModifiers(rng.Next(6500, 11500), state, isReroll, rng);
                candidates.Add((ItemCatalog.Create(kingType), price, price, RitualWire.FlagNormal, null));
            }
            if (RollOmen(catalog, rng) is { } omenItem)
            {
                int price = PriceWithModifiers(rng.Next(1600, 4800), state, isReroll, rng);
                candidates.Add((omenItem, price, price, RitualWire.FlagNormal, null));
            }
        }

        int level = Math.Clamp(state.Level, 1, 100);
        var equipBases = catalog.All.Where(b => b.Supported && b.DropLevel <= level && b.DropLevel >= Math.Max(1, level - 22)
            && (b.Components.Contains("Armour") || b.Components.Contains("Weapon")
                || b.Class is "Ring" or "Amulet" or "Belt" or "Jewel")).ToList();
        var scarabBases = level >= 68 ? catalog.All.Where(b => b.Supported && b.Class == "MapFragment"
            && b.Id.Contains("Scarab", StringComparison.Ordinal)).ToList() : [];
        var cardBases = catalog.All.Where(b => b.Supported && b.Class == "DivinationCard").ToList();

        int safety = targetCount * 4;
        while (candidates.Count < targetCount && safety-- > 0)
        {
            var rolled = RollRewardItem(catalog, rolls, uniques, equipBases, scarabBases, cardBases, state, opulent, vaal, nameless, qualityBoost, rng);
            if (rolled is not { } r) continue;
            int price = PriceWithModifiers(r.BasePrice, state, isReroll, rng);
            candidates.Add((r.Item, price, price, RitualWire.FlagNormal, null));
        }

        // 4. Pack into 12x7 grid using first-fit (largest items first for tight retail-like packing, then sort IDs ascending).
        var occupied = new bool[GridWidth, GridHeight];
        var placed = new List<(byte X, byte Y, byte W, byte H, Item Item, int BuyCost, int OrigCost, byte Flag, Guid? DeferredKey)>();
        // Deferred items get placement priority so they never get squeezed out of the grid.
        var ordered = candidates
            .OrderByDescending(c => c.DeferredKey is not null ? 1 : 0)
            .ThenByDescending(c => ItemArea(catalog, c.Item))
            .ToList();

        foreach (var c in ordered)
        {
            var (w, h) = ItemSize(catalog, c.Item);
            if (!TryFindSlot(occupied, w, h, out byte x, out byte y)) continue;
            MarkSlot(occupied, x, y, w, h);
            placed.Add((x, y, w, h, c.Item, c.BuyCost, c.OrigCost, c.Flag, c.DeferredKey));
        }

        // Assign strictly ascending ItemIds (1..N) and reveal thresholds across completed altars.
        int totalAltars = Math.Max(1, state.Altars.Count);
        var offers = new List<Offer>(placed.Count);
        uint nextId = 1;
        int nonDeferredIndex = 0;
        int nonDeferredTotal = placed.Count(p => p.DeferredKey is null);

        foreach (var p in placed)
        {
            string hiddenId = RitualTables.HiddenItemId(p.W, p.H);
            var hiddenType = catalog.Find(hiddenId) ?? catalog.Find("Metadata/Items/MicrotransactionCurrency/HiddenItem1x1Ritual");
            var hiddenItem = hiddenType is { Supported: true } ht ? ItemCatalog.Create(ht) : p.Item;

            int revealAt;
            if (state.AllRewardsRevealed || p.DeferredKey is not null || totalAltars <= 1)
                revealAt = 0;
            else
            {
                // ~50% revealed after 1st altar (or initially), remaining split evenly across altars 2..totalAltars.
                double frac = (double)nonDeferredIndex / Math.Max(1, nonDeferredTotal);
                revealAt = frac < 0.50 ? 1 : Math.Clamp(2 + (int)((frac - 0.50) / 0.50 * (totalAltars - 1)), 2, totalAltars);
                nonDeferredIndex++;
            }

            bool revealed = state.AllRewardsRevealed || p.DeferredKey is not null || revealAt <= state.Completed;
            offers.Add(new Offer
            {
                ItemId = nextId++,
                X = p.X,
                Y = p.Y,
                Width = p.W,
                Height = p.H,
                RealItem = p.Item,
                HiddenItem = hiddenItem,
                BuyCost = (ushort)Math.Clamp(p.BuyCost, 0, ushort.MaxValue - 1),
                OriginalCost = (ushort)Math.Clamp(p.OrigCost, 0, ushort.MaxValue - 1),
                Flag = p.Flag,
                Revealed = revealed,
                RevealAtAltar = revealAt,
                DeferredKey = p.DeferredKey,
            });
        }

        return offers;
    }

    static int PriceWithModifiers(int basePrice, RitualEncounter.State state, bool isReroll, Random rng)
    {
        // Scale down gently in low campaign areas so Act 2 rewards (~300-1400 tribute) feel affordable.
        double levelScale = state.Level < 68 ? 0.35 + 0.65 * (state.Level / 68.0) : 1.0;
        double price = basePrice * levelScale;
        // Arbitrary Tenets keystone (map_ritual_offered_rewards_have_additional_variable_cost): 0.10x .. 1.80x
        if (state.VariableCosts)
            price *= 0.10 + rng.NextDouble() * 1.70;
        // Atlas small passives: permyriad chance for rerolled rewards to cost 0 tribute.
        if (isReroll && state.FreeRerollRewardPermyriad > 0 && rng.Next(10000) < state.FreeRerollRewardPermyriad)
            return 0;
        return Math.Clamp((int)Math.Round(price), 100, 60000);
    }

    static readonly (string Id, int MinStack, int MaxStack, int UnitPrice, int MinLevel, int Weight)[] CurrencyPool =
    [
        ("Metadata/Items/Currency/CurrencyRerollRare", 1, 5, 320, 1, 180),                 // Chaos Orb
        ("Metadata/Items/Currency/CurrencyUpgradeToRare", 2, 8, 120, 1, 160),             // Orb of Alchemy
        ("Metadata/Items/Currency/CurrencyRerollSocketLinks", 3, 15, 75, 1, 150),         // Orb of Fusing
        ("Metadata/Items/Currency/CurrencyRerollSocketNumbers", 5, 20, 50, 1, 140),       // Jeweller's Orb
        ("Metadata/Items/Currency/CurrencyRerollSocketColours", 5, 20, 45, 1, 140),       // Chromatic Orb
        ("Metadata/Items/Currency/CurrencyRerollMagic", 5, 20, 45, 1, 140),               // Orb of Alteration
        ("Metadata/Items/Currency/CurrencyConvertToNormal", 2, 8, 110, 1, 110),           // Orb of Scouring
        ("Metadata/Items/Currency/CurrencyUpgradeMagicToRare", 1, 5, 200, 15, 90),        // Regal Orb
        ("Metadata/Items/Currency/CurrencyUpgradeRandomly", 3, 12, 60, 1, 100),           // Orb of Chance
        ("Metadata/Items/Currency/CurrencyCorrupt", 1, 5, 260, 20, 100),                  // Vaal Orb
        ("Metadata/Items/Currency/CurrencyPassiveRefund", 1, 4, 240, 20, 80),             // Orb of Regret
        ("Metadata/Items/Currency/CurrencyGemQuality", 1, 3, 380, 30, 65),                // Gemcutter's Prism
        ("Metadata/Items/Currency/CurrencyMapQuality", 3, 10, 110, 68, 90),               // Cartographer's Chisel
        ("Metadata/Items/Currency/CurrencyAddModToRare", 1, 2, 1600, 50, 25),             // Exalted Orb
        ("Metadata/Items/Currency/CurrencyModValues", 1, 1, 6800, 68, 8),                 // Divine Orb
        ("Metadata/Items/Currency/CurrencyRemoveMod", 1, 2, 1400, 55, 20),                // Orb of Annulment
        ("Metadata/Items/Currency/CurrencyDeck", 2, 10, 190, 40, 85),                     // Stacked Deck
        ("Metadata/Items/Currency/CurrencyRitualSplinter", 8, 25, 55, 68, 70),            // Ritual Splinter
        ("Metadata/Items/Currency/CurrencyRitualStone", 1, 1, 3600, 68, 16),              // Ritual Vessel
    ];

    static (Item Item, int BasePrice)? RollRewardItem(
        ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques,
        IReadOnlyList<ItemCatalog.BaseType> equipBases,
        IReadOnlyList<ItemCatalog.BaseType> scarabBases,
        IReadOnlyList<ItemCatalog.BaseType> cardBases,
        RitualEncounter.State state,
        bool opulent, bool vaal, bool nameless, int qualityBoost, Random rng)
    {
        int roll = rng.Next(100);
        int level = Math.Clamp(state.Level, 1, 100);

        // Opulent altars heavily favour currency stacks.
        if (opulent && roll < 65) return RollCurrency(catalog, state, level, rng, bonusStack: true);

        // Vaal altars favour corrupted items & Vaal gems / Vaal Orbs.
        if (vaal && roll < 45)
        {
            var item = RollRareOrUnique(catalog, rolls, uniques, equipBases, level, qualityBoost, rng, forceUniqueChance: 25);
            if (item is not null)
                return (item.Value.Item with { Corrupted = true }, (int)(item.Value.BasePrice * 1.15));
        }

        // Standard + themed distribution:
        if (roll < 38) return RollCurrency(catalog, state, level, rng, bonusStack: false);
        if (roll < 50 && level >= 68 && RitualTables.RitualOmens.Count > 0)
        {
            if (RollOmen(catalog, rng) is { } omen) return (omen, rng.Next(1400, 4600));
        }
        if (roll < 60 && level >= 68 && RitualTables.RitualArmours.Count > 0)
        {
            string id = RitualTables.RitualArmours[rng.Next(RitualTables.RitualArmours.Count)];
            if (catalog.Find(id) is { Supported: true } rt)
            {
                var rare = rolls.Alchemy(ItemCatalog.Create(rt) with { ItemLevel = (byte)level }, rng) ?? ItemCatalog.Create(rt);
                return (rare, rng.Next(1600, 4200));
            }
        }
        if (roll < 68 && level >= 68 && RitualTables.ItemisedCorpses.Count > 0)
        {
            string id = RitualTables.ItemisedCorpses[rng.Next(RitualTables.ItemisedCorpses.Count)];
            if (catalog.Find(id) is { Supported: true } ct)
                return (ItemCatalog.Create(ct) with { ItemLevel = (byte)level }, rng.Next(1800, 4000));
        }
        if (roll < 78 && level >= 68 && nameless && rng.Next(100) < 12)
        {
            if (catalog.Find(RitualTables.AudienceWithTheKingId) is { Supported: true } king)
                return (ItemCatalog.Create(king), rng.Next(6500, 11000));
        }
        if (roll < 86)
            return RollRareOrUnique(catalog, rolls, uniques, equipBases, level, qualityBoost, rng, forceUniqueChance: 22 + qualityBoost / 3);
        if (roll < 94)
            return RollDivCardOrScarab(scarabBases, cardBases, level, rng);
        return RollCurrency(catalog, state, level, rng, bonusStack: false);
    }

    static (Item Item, int BasePrice)? RollCurrency(ItemCatalog catalog, RitualEncounter.State state, int level, Random rng, bool bonusStack)
    {
        var eligible = CurrencyPool.Where(c => c.MinLevel <= level).ToList();
        int total = eligible.Sum(c => c.Weight);
        if (total <= 0) return null;
        int pick = rng.Next(total);
        var chosen = eligible[0];
        foreach (var e in eligible)
            if ((pick -= e.Weight) < 0) { chosen = e; break; }
        if (catalog.Find(chosen.Id) is not { Supported: true } type) return null;
        int count = rng.Next(chosen.MinStack, chosen.MaxStack + 1);
        if (bonusStack) count = (int)Math.Ceiling(count * 1.5);
        if (chosen.Id == RitualTables.RitualSplinterId && state.SplinterStackPct > 0)
            count = count * (100 + state.SplinterStackPct) / 100;
        count = Math.Clamp(count, 1, Math.Max(1, (int)type.MaxStack));
        var item = ItemCatalog.Create(type) with { StackSize = (ushort)count };
        return (item, Math.Max(200, count * chosen.UnitPrice));
    }

    static Item? RollOmen(ItemCatalog catalog, Random rng)
    {
        if (RitualTables.RitualOmens.Count == 0) return null;
        string id = RitualTables.RitualOmens[rng.Next(RitualTables.RitualOmens.Count)];
        return catalog.Find(id) is { Supported: true } t ? ItemCatalog.Create(t) : null;
    }

    static (Item Item, int BasePrice)? RollRareOrUnique(ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques,
        IReadOnlyList<ItemCatalog.BaseType> equipBases, int level, int qualityBoost, Random rng, int forceUniqueChance)
    {
        if (rng.Next(100) < forceUniqueChance && uniques.All.Count > 0)
        {
            for (int tries = 0; tries < 10; tries++)
            {
                var def = uniques.All[rng.Next(uniques.All.Count)];
                if (catalog.Find(def.BaseTypeId) is not { Supported: true } bt || bt.DropLevel > level + 2) continue;
                if (uniques.Create(def, rng) is { } u)
                    return (u with { ItemLevel = (byte)level, Identified = true }, rng.Next(2200, 6200) + qualityBoost * 15);
            }
        }
        if (equipBases.Count == 0) return null;
        var chosen = equipBases[rng.Next(equipBases.Count)];
        var item = ItemCatalog.Create(chosen) with { ItemLevel = (byte)level };
        item = rolls.Alchemy(item, rng) ?? item;
        return (item, rng.Next(750, 2400));
    }

    static (Item Item, int BasePrice)? RollDivCardOrScarab(
        IReadOnlyList<ItemCatalog.BaseType> scarabBases,
        IReadOnlyList<ItemCatalog.BaseType> cardBases, int level, Random rng)
    {
        bool pickScarab = level >= 68 && scarabBases.Count > 0 && rng.Next(2) == 0;
        var pool = pickScarab ? scarabBases : cardBases;
        if (pool.Count == 0) return null;
        var chosen = pool[rng.Next(pool.Count)];
        return (ItemCatalog.Create(chosen), pickScarab ? rng.Next(900, 2800) : rng.Next(800, 3200));
    }

    static (byte W, byte H) ItemSize(ItemCatalog catalog, Item item)
    {
        var shape = catalog.ByHash(item.TypeHash);
        return ((byte)Math.Clamp((int)(shape?.Width ?? 1), 1, GridWidth), (byte)Math.Clamp((int)(shape?.Height ?? 1), 1, GridHeight));
    }

    static int ItemArea(ItemCatalog catalog, Item item)
    {
        var (w, h) = ItemSize(catalog, item);
        return w * h;
    }

    static bool TryFindSlot(bool[,] occupied, byte w, byte h, out byte x, out byte y)
    {
        for (int cx = 0; cx <= GridWidth - w; cx++)
            for (int cy = 0; cy <= GridHeight - h; cy++)
            {
                bool ok = true;
                for (int dx = 0; dx < w && ok; dx++)
                    for (int dy = 0; dy < h && ok; dy++)
                        if (occupied[cx + dx, cy + dy]) ok = false;
                if (ok) { x = (byte)cx; y = (byte)cy; return true; }
            }
        x = y = 0;
        return false;
    }

    static void MarkSlot(bool[,] occupied, byte x, byte y, byte w, byte h)
    {
        for (int dx = 0; dx < w; dx++)
            for (int dy = 0; dy < h; dy++)
                occupied[x + dx, y + dy] = true;
    }

    // ---- Wire serialization helpers ------------------------------------------------------------------------------------

    /// <summary>Builds the 0x0166 per-item metadata rows (sorted ascending by ItemId) for the current page.</summary>
    public static List<(uint Id, ushort BuyCost, ushort OriginalCost, byte Flag)> BuildRows(RitualEncounter.State state)
    {
        var rows = new List<(uint, ushort, ushort, byte)>(state.Offers.Count);
        foreach (var offer in state.Offers)
        {
            if (offer.Purchased) continue;
            if (!offer.Revealed)
                rows.Add((offer.ItemId, ushort.MaxValue, ushort.MaxValue, RitualWire.FlagNormal));
            else
            {
                ushort orig = offer.DeferredThisPage ? ushort.MaxValue : offer.OriginalCost;
                rows.Add((offer.ItemId, offer.BuyCost, orig, offer.Flag));
            }
        }
        rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return rows;
    }

    /// <summary>Builds the Manager-1 inventory items (revealed items show real Item; unrevealed show HiddenItem*Ritual).</summary>
    public static List<InventoryItem> BuildInventoryItems(RitualEncounter.State state)
    {
        var items = new List<InventoryItem>(state.Offers.Count);
        foreach (var offer in state.Offers)
        {
            if (offer.Purchased) continue;
            items.Add(new InventoryItem(offer.ItemId, offer.X, offer.Y, offer.Revealed ? offer.RealItem : offer.HiddenItem));
        }
        return items;
    }

    /// <summary>Sends Manager-1 shop inventory (<c>0x00c1</c> if previously sent + <c>0x00c0</c>), <c>0x0166</c>, and <c>0x0167</c> to one client.</summary>
    public static async Task SendShopState(Player player, RitualEncounter.State state, bool openWindow)
    {
        var items = BuildInventoryItems(state);
        if (!state.ShopSentTo.Add(player.Id))
            await Send(player, InstanceInventories.RemoveInventory(RitualWire.ShopInventoryId, NpcShops.Manager), null);
        await Send(player, InstanceInventories.AddInventory(RitualWire.ShopInventoryId, RitualWire.ShopSlot,
            NpcShops.Manager, items, flags: 0x60),
            $"0x00c0 Ritual favours inventory 0x{RitualWire.ShopInventoryId:x} ({items.Count} item(s))");
        await Send(player, RitualWire.Favours(state.Tribute, state.RerollsUsed, BuildRows(state), state.Altars.Count, state.Completed),
            $"0x0166 Ritual favours ({state.Completed}/{state.Altars.Count} done, rerolls {state.RerollsUsed})");
        await Send(player, RitualWire.Tribute(state.Tribute), $"0x0167 Ritual tribute {state.Tribute}");
        if (openWindow)
            await Send(player, RitualWire.OpenFavoursWindow(RitualWire.ShopInventoryId),
                $"0x007b open Ritual favours window (inv 0x{RitualWire.ShopInventoryId:x})");
    }

    sealed class SavedRowSync
    {
        public uint Revision;
        public List<uint> SentIds = [];
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, SavedRowSync> SavedRowStates = new();

    public static void Clear() => SavedRowStates.Clear();

    /// <summary>Syncs Manager-0 row 57 (<c>RitualSavedRewards1</c>, invId 58) in place via <c>0x00bf</c> so the client's
    /// <c>50 - inv57.ItemCount</c> deferral cap indicator matches the persisted <c>AccountData</c> deferred list without
    /// replacing the row's initial registration.</summary>
    public static async Task SyncSavedRewardsInventory(Player player)
    {
        var deferred = LoadDeferred(player);
        var sync = SavedRowStates.GetOrCreateValue(player);
        if (deferred.Count == 0 && sync.SentIds.Count == 0) return;

        var catalog = ItemsFor(player);
        var placeholderType = catalog.Find("Metadata/Items/MicrotransactionCurrency/HiddenItem1x1Ritual");
        var list = new List<InventoryItem>(Math.Min(deferred.Count, RitualTables.MaxDeferredItems));
        byte x = 0, y = 0;
        uint idBase = (sync.Revision + 1) * 100u;
        for (int i = 0; i < deferred.Count && i < RitualTables.MaxDeferredItems; i++)
        {
            Item item;
            try { item = CharacterSaves.FromSaved(deferred[i].Item, catalog); }
            catch { if (placeholderType is not { Supported: true }) continue; item = ItemCatalog.Create(placeholderType); }
            list.Add(new InventoryItem(idBase + (uint)i + 1, x, y, item));
            if (++x >= 100) { x = 0; y++; }
        }
        var removed = sync.SentIds;
        sync.SentIds = [.. list.Select(i => i.Id)];
        uint rev = ++sync.Revision;
        await Send(player, InstanceItems.InventoryChange(RitualWire.SavedRewardsInventoryId, rev, removed, list, manager: 0),
            $"0x00bf RitualSavedRewards1 ({list.Count}/{RitualTables.MaxDeferredItems} deferred)");
    }

    /// <summary>Reveals any newly unlocked offers when an altar finishes, updating every client's Manager-1 shop inventory.</summary>
    public static async Task RevealForProgress(Player player, RitualEncounter.State state, Func<Player, IEnumerable<Player>> clients)
    {
        var newlyRevealed = new List<Offer>();
        foreach (var offer in state.Offers)
        {
            if (offer.Purchased || offer.Revealed) continue;
            if (state.AllRewardsRevealed || offer.RevealAtAltar <= state.Completed)
            {
                offer.Revealed = true;
                newlyRevealed.Add(offer);
            }
        }
        if (newlyRevealed.Count == 0) return;
        uint rev = ++state.ShopRevision;
        var removedIds = newlyRevealed.Select(o => o.ItemId).ToList();
        var addedItems = newlyRevealed.Select(o => new InventoryItem(o.ItemId, o.X, o.Y, o.RealItem)).ToList();
        var change = InstanceItems.InventoryChange(RitualWire.ShopInventoryId, rev, removedIds, addedItems, NpcShops.Manager);
        foreach (var peer in clients(player))
            await Send(peer, change.ToArray(), $"0x00bf Ritual favours reveal {newlyRevealed.Count} item(s)");
    }

    // ---- C→S Packet Handlers -------------------------------------------------------------------------------------------

    /// <summary>C→S 0x0169: player opened the Ritual favour window (V hotkey or bottom-right button).</summary>
    public static async Task<bool> HandleOpen(Player player, RitualEncounter encounter)
    {
        if (encounter.Of(player) is not { Altars.Count: > 0 } state) return false;
        await state.Gate.WaitAsync();
        try
        {
            EnsureOffers(player, state);
            await SyncSavedRewardsInventory(player);
            await SendShopState(player, state, openWindow: true);
            if (!ClientHasQuestFlag(player, "RitualRewardsSeen"))
                await SetQuestFlags(player, ["RitualRewardsSeen"]);
        }
        finally { state.Gate.Release(); }
        return true;
    }

    /// <summary>C→S 0x0283: player bought/claimed a revealed Ritual favour item (normal click or Ctrl+click).</summary>
    public static async Task<bool> HandleBuy(Player player, byte[] message, RitualEncounter encounter)
    {
        if (!RitualWire.TryReadBuyFavour(message, out var buy)) return false;
        if (encounter.Of(player) is not { Altars.Count: > 0 } state) return false;
        await state.Gate.WaitAsync();
        try
        {
            if (!encounter.IsOwner(player, state))
            {
                await Send(player, InstanceChat.ChatLine("Only the instance owner can purchase Favours."), "0x0017 Ritual not owner");
                return true;
            }
            EnsureOffers(player, state);
            var offer = state.Offers.FirstOrDefault(o => o.ItemId == buy.ItemId);
            if (offer is null || offer.Purchased || !offer.Revealed) return true;
            if (state.Tribute < offer.BuyCost)
            {
                await Send(player, InstanceChat.ChatLine("You do not have enough Tribute to purchase this item."), "0x0017 Ritual insufficient tribute");
                return true;
            }

            var items = ItemsOf(player);
            bool expandedAllowed = ExpandedInventoryGranted(player);
            bool explicitExpanded = buy.DestInventoryRow == PlayerInventories.ExpandedInventory;
            byte destRow = explicitExpanded && expandedAllowed
                ? PlayerInventories.ExpandedInventory : PlayerInventories.MainInventory;
            List<uint> invRemoved = [];
            List<InventoryItem> invAdded = [];
            byte placedRow = destRow;

            bool placed = false;
            if ((!explicitExpanded || expandedAllowed)
                && buy.DestX >= 0 && buy.DestY >= 0 && buy.DestX <= byte.MaxValue && buy.DestY <= byte.MaxValue
                && destRow < InstanceInventories.PlayerDefaults.Length)
            {
                // Explicit cell placement from dragging/clicking into inventory.
                var slotDef = InstanceInventories.PlayerDefaults[destRow];
                var shape = ShapeOf(offer.RealItem);
                int w = shape?.Width ?? 1, h = shape?.Height ?? 1;
                int tx = buy.DestX, ty = buy.DestY;
                if (tx + w <= slotDef.Width && ty + h <= slotDef.Height && !OverlapsExisting(items.Items(destRow), tx, ty, w, h))
                {
                    var added = items.Add(destRow, (byte)tx, (byte)ty, offer.RealItem);
                    invAdded.Add(added);
                    placed = true;
                }
            }
            if (!placed)
            {
                // Ctrl+click (-1, -1) or fallback auto-place (with stacking); ExpandedInventory is only eligible when granted.
                byte[] tryRows = expandedAllowed
                    ? (destRow == PlayerInventories.ExpandedInventory
                        ? [PlayerInventories.ExpandedInventory, PlayerInventories.MainInventory]
                        : [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory])
                    : [PlayerInventories.MainInventory];
                foreach (byte row in tryRows)
                {
                    if (!items.Fits(row, offer.RealItem, ShapeOf)) continue;
                    var stacked = items.AddStacked(row, offer.RealItem, ShapeOf);
                    invRemoved.AddRange(stacked.Changed.Select(i => i.Id));
                    invAdded.AddRange(stacked.Changed);
                    invAdded.AddRange(stacked.Added);
                    placedRow = row;
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                await Send(player, InstanceItems.InventoryFull(), "0x0117 Ritual buy inventory full");
                return true;
            }

            state.Tribute -= offer.BuyCost;
            offer.Purchased = true;
            if (offer.DeferredKey is Guid dkey)
            {
                var deferred = LoadDeferred(player);
                if (deferred.RemoveAll(d => d.Key == dkey) > 0)
                {
                    SaveDeferred(player, deferred);
                    await SyncSavedRewardsInventory(player);
                }
            }

            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(placedRow),
                items.NextRevision(placedRow), invRemoved, invAdded),
                $"0x00bf Ritual bought item 0x{offer.ItemId:x} for {offer.BuyCost} tribute -> row {placedRow}");
            uint rev = ++state.ShopRevision;
            var removeShop = InstanceItems.InventoryChange(RitualWire.ShopInventoryId, rev, [offer.ItemId], [], NpcShops.Manager);
            var favoursMsg = RitualWire.Favours(state.Tribute, state.RerollsUsed, BuildRows(state), state.Altars.Count, state.Completed);
            var tributeMsg = RitualWire.Tribute(state.Tribute);
            foreach (var peer in encounter.InstanceClients(player))
            {
                await Send(peer, removeShop.ToArray(), $"0x00bf Ritual shop remove 0x{offer.ItemId:x}");
                await Send(peer, favoursMsg.ToArray(), $"0x0166 Ritual favours after buy");
                await Send(peer, tributeMsg.ToArray(), $"0x0167 Ritual tribute {state.Tribute}");
            }
        }
        finally { state.Gate.Release(); }
        return true;
    }

    /// <summary>C→S 0x0284: player deferred a revealed Ritual favour item (15% fee, 10% future discount, persisted in AccountData).</summary>
    public static async Task<bool> HandleDefer(Player player, byte[] message, RitualEncounter encounter)
    {
        if (!RitualWire.TryReadDeferFavour(message, out uint itemId)) return false;
        if (encounter.Of(player) is not { Altars.Count: > 0 } state) return false;
        await state.Gate.WaitAsync();
        try
        {
            if (!encounter.IsOwner(player, state))
            {
                await Send(player, InstanceChat.ChatLine("Only the instance owner can defer Favours."), "0x0017 Ritual defer not owner");
                return true;
            }
            EnsureOffers(player, state);
            var offer = state.Offers.FirstOrDefault(o => o.ItemId == itemId);
            if (offer is null || offer.Purchased || !offer.Revealed || offer.DeferredThisPage) return true;
            if (!RitualTables.CanDefer(state.Level, offer.BuyCost, offer.OriginalCost)) return true;

            int fee = RitualTables.DeferCost(offer.OriginalCost, state.DeferCostPct);
            if (state.Tribute < fee)
            {
                await Send(player, InstanceChat.ChatLine("You do not have enough Tribute to defer this item."), "0x0017 Ritual defer insufficient tribute");
                return true;
            }

            var deferred = LoadDeferred(player);
            if (offer.DeferredKey is null && deferred.Count >= RitualTables.MaxDeferredItems)
            {
                await Send(player, InstanceChat.ChatLine("Deferring Failed: You have reached the maximum amount of deferred items"),
                    "0x0017 Ritual defer cap reached");
                return true;
            }

            int discount = RitualTables.DeferDiscount(offer.OriginalCost);
            int nextBuy = Math.Max(1, offer.BuyCost - discount);
            int baseCooldown = offer.OriginalCost >= 5000 ? 3 : offer.OriginalCost >= 2000 ? 2 : 1;
            int cooldown = Math.Max(1, (int)Math.Round(baseCooldown * 100.0 / Math.Max(100, 100 + state.DeferSoonerPct)));

            Guid key = offer.DeferredKey ?? Guid.NewGuid();
            var savedItem = CharacterSaves.ToSaved(offer.RealItem, ItemsFor(player).Mods);
            var updated = new DeferredEntry(key, savedItem, nextBuy, offer.OriginalCost, cooldown, MissedCount: 0);
            int idx = deferred.FindIndex(d => d.Key == key);
            if (idx >= 0) deferred[idx] = updated;
            else deferred.Add(updated);
            SaveDeferred(player, deferred);

            state.Tribute -= fee;
            offer.DeferredKey = key;
            offer.DeferredThisPage = true;
            offer.BuyCost = (ushort)Math.Clamp(nextBuy, 1, ushort.MaxValue - 1);
            offer.Flag = RitualWire.FlagDeferred;

            await SyncSavedRewardsInventory(player);
            var favoursMsg = RitualWire.Favours(state.Tribute, state.RerollsUsed, BuildRows(state), state.Altars.Count, state.Completed);
            var tributeMsg = RitualWire.Tribute(state.Tribute);
            foreach (var peer in encounter.InstanceClients(player))
            {
                await Send(peer, favoursMsg.ToArray(), $"0x0166 Ritual favours after defer 0x{itemId:x}");
                await Send(peer, tributeMsg.ToArray(), $"0x0167 Ritual tribute {state.Tribute}");
            }
        }
        finally { state.Gate.Release(); }
        return true;
    }

    /// <summary>C→S 0x0168: player rerolled Ritual favours.</summary>
    public static async Task<bool> HandleReroll(Player player, RitualEncounter encounter)
    {
        if (encounter.Of(player) is not { Altars.Count: > 0 } state) return false;
        await state.Gate.WaitAsync();
        try
        {
            if (!encounter.IsOwner(player, state))
            {
                await Send(player, InstanceChat.ChatLine("Only the instance owner can reroll Favours."), "0x0017 Ritual reroll not owner");
                return true;
            }
            int maxRerolls = RitualTables.MaxRerolls(state.AdditionalRerolls, state.CannotReroll);
            if (state.RerollsUsed >= maxRerolls)
            {
                await Send(player, InstanceChat.ChatLine("You cannot reroll Favours again in this area."), "0x0017 Ritual max rerolls");
                return true;
            }
            int cost = RitualTables.RerollCost(state.Level, state.RerollsUsed, state.FreeRerolls, state.RerollCostPctFinal);
            if (state.Tribute < cost)
            {
                await Send(player, InstanceChat.ChatLine("You do not have enough Tribute to reroll Favours."), "0x0017 Ritual reroll insufficient tribute");
                return true;
            }

            // Process un-deferred deferred items from the outgoing page: increase cost by 10% (capped at original) or expire if red warning.
            bool deferredChanged = false;
            var deferred = LoadDeferred(player);
            foreach (var offer in state.Offers)
            {
                if (offer.DeferredKey is not Guid dkey || offer.Purchased || offer.DeferredThisPage) continue;
                int idx = deferred.FindIndex(d => d.Key == dkey);
                if (idx < 0) continue;
                var entry = deferred[idx];
                if (entry.MissedCount >= 2)
                {
                    deferred.RemoveAt(idx);
                }
                else
                {
                    int penalty = RitualTables.DeferDiscount(entry.OriginalCost);
                    int raised = Math.Min(entry.OriginalCost, entry.BuyCost + penalty);
                    deferred[idx] = entry with { BuyCost = raised, MissedCount = entry.MissedCount + 1, CooldownMaps = 1 };
                }
                deferredChanged = true;
            }
            if (deferredChanged)
            {
                SaveDeferred(player, deferred);
                await SyncSavedRewardsInventory(player);
            }

            state.Tribute -= cost;
            state.RerollsUsed++;
            var rng = new Random(HashCode.Combine(state.Seed, state.RerollsUsed, 0x5245524F));
            state.Offers = GeneratePage(player, state, rng, isReroll: true);
            foreach (var peer in encounter.InstanceClients(player))
                await SendShopState(peer, state, openWindow: ReferenceEquals(peer, player));
        }
        finally { state.Gate.Release(); }
        return true;
    }

    public static void EnsureOffers(Player player, RitualEncounter.State state)
    {
        if (state.Offers.Count > 0) return;
        var rng = new Random(HashCode.Combine(state.Seed, 0x4641564F));
        state.Offers = GeneratePage(player, state, rng, isReroll: false);
    }

    static bool OverlapsExisting(IReadOnlyList<InventoryItem> existing, int x, int y, int w, int h)
    {
        foreach (var it in existing)
        {
            var s = ShapeOf(it.Item);
            int ew = s?.Width ?? 1, eh = s?.Height ?? 1;
            if (x < it.X + ew && x + w > it.X && y < it.Y + eh && y + h > it.Y) return true;
        }
        return false;
    }
}
