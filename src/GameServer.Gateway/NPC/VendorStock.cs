using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>Persistent generated offers; local generation assumptions are documented in protocol/17-npcs.md.</summary>
static class VendorStock
{
    public sealed record Cost(uint Currency, uint Amount);
    public sealed record Entry(Guid Token, int RefreshAt, CharacterSaves.SavedItem Item, bool Sold = false,
        List<Cost>? Price = null);
    public sealed record State(int Level, List<Entry> Entries, int OfferedTier = 0);
    public sealed record Window(List<NpcShops.ShopTab> Tabs, string Key, Dictionary<uint, Guid> Finite);

    public static string Key(string npc, string league) => "vendors.stock." + league + "." +
        (VendorCatalog.Default.Find(npc) is { Kind: "Kirac" } ? "Kirac" : npc);

    public static bool Consume(string character, string key, Guid token, AccountStash? account = null) => Saves.Atomically(() =>
    {
        bool shared = key.EndsWith(".Kirac",StringComparison.Ordinal) && account is not null;
        var json = shared ? account!.ModuleData(key) : CharacterSaves.ModuleData(character, key);
        if (json is null) return false;
        var state = JsonSerializer.Deserialize<State>(json)!;
        int i = state.Entries.FindIndex(e => e.Token == token && !e.Sold);
        if (i < 0) return false;
        state.Entries[i] = state.Entries[i] with { Sold = true };
        if (shared) account!.ChangeModuleData(key,_ => JsonSerializer.Serialize(state));
        else CharacterSaves.ChangeModuleData(character, key, _ => JsonSerializer.Serialize(state));
        return true;
    });

    public static Window Open(string character, string league, string npc, int level, ItemCatalog items,
        SkillCatalog skills, IReadOnlyCollection<int> gems, Func<uint> nextId, LeagueRules rules,
        bool hardMode, List<string> notes, bool refreshKirac = false, Random? random = null, AccountStash? account = null)
    {
        random ??= Random.Shared;
        var profile = VendorCatalog.Default.Find(npc);
        if (profile is null) return new([], "", []);
        var price = VendorPrices.For(items, hardMode);
        string key = Key(npc, league);
        bool shared = profile.Kind == "Kirac" && account is not null;
        int offeredTier = shared ? KiracProgress.For(account).Tier : 1;
        int count = profile.Kind switch { "Weapons" => 60, "Magic" => 40, "Kirac" => 18, _ => 0 };
        var rolls = new Lazy<ItemRolls>(() => new(items));
        bool Eligible(ItemCatalog.BaseType t) => t.Supported && t.DropLevel > 0
            && t.Components.Contains("Mods") && !rules.ShopExcludedClasses.Contains(t.Class)
            && !t.Name.StartsWith('[') && !t.Id.Contains("Royale") && t.ModDomain == 1;
        var classes = profile.Kind == "Weapons"
            ? new[] { "OneHandSword", "TwoHandSword", "ThrustingOneHandSword", "OneHandAxe", "TwoHandAxe",
                "OneHandMace", "TwoHandMace", "Dagger", "RuneDagger", "Claw", "Bow", "Staff", "Warstaff",
                "Sceptre", "Shield", "BodyArmour", "Helmet", "Gloves", "Boots", "Quiver" }
            : new[] { "Wand", "Ring", "Amulet", "Belt", "LifeFlask", "ManaFlask", "HybridFlask", "UtilityFlask", "Tincture" };
        var pool = new Lazy<List<ItemCatalog.BaseType>>(() =>
        {
            var selected = items.All.Where(t => Eligible(t) && classes.Contains(t.Class)).ToList();
            // Flasks have their own ModDomain.
            if (profile.Kind == "Magic") selected.AddRange(items.All.Where(t => t.Supported && t.DropLevel > 0
                && !t.Id.Contains("Royale") && t.Components.Contains("Flask") && !rules.ShopExcludedClasses.Contains(t.Class)));
            return selected.DistinctBy(t => t.Row).ToList();
        });
        var candidatesByRarity = new Dictionary<ItemRarity,ItemCatalog.BaseType[][]>();
        Item? Generate(int slot)
        {
            if (profile.Kind == "Kirac" && slot == 17)
            {
                var scarab = items.Find("Metadata/Items/Scarabs/ScarabSettlers1");
                var chaos = items.Find("Chaos Orb");
                return scarab is { Supported:true } && chaos is not null
                    ? ItemCatalog.Create(scarab) with { Price = [(chaos.Hash,4)] } : null;
            }
            int roll = random.Next(100);
            var rarity = roll < 2 ? ItemRarity.Rare : roll < 32 ? ItemRarity.Magic : ItemRarity.Normal;
            ItemCatalog.BaseType? type;
            int itemLevel;
            if (profile.Kind == "Kirac")
            {
                int maxTier = offeredTier;
                int tier = slot < maxTier ? slot + 1 : random.Next(1, maxTier + 1);
                type = items.Find($"Metadata/Items/Maps/MapKeyTier{tier}");
                itemLevel = 82;
            }
            else
            {
                itemLevel = Math.Max(1, Math.Min(level, profile.TownLevel) + 2 - (int)rarity);
                if (!candidatesByRarity.TryGetValue(rarity,out var candidates))
                {
                    candidates = pool.Value.Where(t => t.DropLevel <= itemLevel
                        && price.Price(new Item(t.Hash,t.Components,t.VisualIdentity)
                            { ItemLevel=(byte)itemLevel, Rarity=rarity }) is not null)
                        .GroupBy(t => t.Class).OrderBy(g => g.Key,StringComparer.Ordinal)
                        .Select(g => { int top = g.Max(t => t.DropLevel); return g.Where(t => t.DropLevel >= top-15).ToArray(); })
                        .ToArray();
                    candidatesByRarity[rarity] = candidates;
                }
                if (candidates.Length == 0) return null;
                var bases = candidates[slot % candidates.Length];
                type = bases[random.Next(bases.Length)];
                if (type.Components.Contains("Flask") && rarity == ItemRarity.Rare) rarity = ItemRarity.Magic;
                itemLevel = Math.Max(1, Math.Min(level, profile.TownLevel) + 2 - (int)rarity);
            }
            if (type is not { Supported: true }) return null;
            var item = ItemCatalog.Create(type, random: random) with { ItemLevel = (byte)itemLevel,
                Quality = type.Components.Contains("Quality") && random.Next(10) == 0 ? (byte)random.Next(1,21) : (byte)0 };
            item = rolls.Value.Roll(item, rarity, random);
            int max = CurrencyEffects.MaximumSockets(item, type);
            if (max > 0)
            {
                var choices = new[] { (Count:1, Weight:50), (Count:2, Weight:120), (Count:3, Weight:100),
                    (Count:4, Weight:30), (Count:6, Weight:1) }.Where(c => c.Count <= max).ToArray();
                int draw = random.Next(choices.Sum(c => c.Weight)), sockets = 1;
                foreach (var c in choices) { draw -= c.Weight; if (draw < 0) { sockets = c.Count; break; } }
                var links = new List<byte> { 1 };
                for (int i = 1; i < sockets; i++) if (random.Next(2) == 0) links[^1]++; else links.Add(1);
                item = item with { Sockets = [.. Enumerable.Range(0, sockets).Select(_ => new ItemSocket(
                    SocketColours.Roll(items.Requirements(type), itemLevel, item.Quality, random)))], Links = links };
            }
            // Rare stock was unidentified in the capture; keep its rolled affixes in the saved server item.
            if (rarity == ItemRarity.Rare) item = item with { Identified = false };
            return price.Price(item);
        }

        var state = Saves.Atomically(() =>
        {
            var json = shared ? account!.ModuleData(key) : CharacterSaves.ModuleData(character, key);
            var old = json is null ? null : JsonSerializer.Deserialize<State>(json);
            refreshKirac |= profile.Kind == "Kirac" && old is not null && offeredTier > old.OfferedTier;
            var entries = old is null || refreshKirac ? new List<Entry>() : new List<Entry>(old.Entries);
            bool changed = old is null || refreshKirac;
            for (int i = 0; i < count; i++)
            {
                if (i < entries.Count && (profile.Kind == "Kirac" || entries[i].RefreshAt > level)) continue;
                if (Generate(i) is not { } item) { notes.Add($"No priced generated offer for {npc} slot {i}"); continue; }
                int next = i < entries.Count ? entries[i].RefreshAt + ((level - entries[i].RefreshAt) / 3 + 1) * 3
                    : level + i % 3 + 1;
                var entry = new Entry(Guid.NewGuid(), next, CharacterSaves.ToSaved(item, items.Mods),
                    Price:[.. item.Price.Select(p => new Cost(p.TypeHash,p.Amount))]);
                if (i < entries.Count) entries[i] = entry; else entries.Add(entry);
                changed = true;
            }
            var current = new State(Math.Max(level, old?.Level ?? level), entries,offeredTier);
            if (changed)
            {
                if (shared) account!.ChangeModuleData(key,_ => JsonSerializer.Serialize(current));
                else CharacterSaves.ChangeModuleData(character,key,_ => JsonSerializer.Serialize(current));
            }
            return current;
        });
        var goods = new List<(Item Item, Guid? Token)>();
        foreach (var entry in state.Entries.Where(e => !e.Sold))
            if (price.Price(CharacterSaves.FromSaved(entry.Item, items) with {
                Price = entry.Price is null ? [] : [.. entry.Price.Select(p => (p.Currency,p.Amount))] }) is { } item) goods.Add((item, entry.Token));
        void Supply(string name)
        {
            if (items.Find(name) is not { Supported: true } type || rules.ShopExcludedClasses.Contains(type.Class)) return;
            var item = ItemCatalog.Create(type);
            if (price.Price(item) is { } priced) goods.Add((priced, null));
        }
        if (profile.Kind == "Weapons") { Supply("Armourer's Scrap"); if (profile.Act >= 2) Supply("Blacksmith's Whetstone"); }
        if (profile.Kind == "Magic")
        {
            foreach (string name in profile.Act == 1 ? new[] { "Orb of Transmutation", "Orb of Alteration", "Orb of Augmentation", "Portal Scroll" }
                : profile.Act == 2 ? new[] { "Orb of Regret", "Orb of Scouring", "Portal Scroll", "Orb of Fusing", "Jeweller's Orb" }
                : new[] { "Orb of Alchemy", "Orb of Chance", "Orb of Regret", "Orb of Transmutation", "Orb of Alteration", "Orb of Scouring", "Orb of Augmentation", "Portal Scroll", "Orb of Fusing", "Jeweller's Orb" }) Supply(name);
        }
        if (profile.Kind == "Kirac") Supply("Orb of Unmaking");
        var tabs = new List<NpcShops.ShopTab>();
        var finite = new Dictionary<uint, Guid>();
        void Pack(string name, IEnumerable<(Item Item, Guid? Token)> source, bool columns = false)
        {
            var page = new List<InventoryItem>(); bool[,] occupied = new bool[12,11]; int number = 1;
            void Flush() { tabs.Add(new(number == 1 ? name : $"{name} {number}", page)); page = []; occupied = new bool[12,11]; number++; }
            foreach (var (item, token) in source)
            {
                var type = items.ByHash(item.TypeHash)!;
                (byte X, byte Y)? Free()
                {
                    for (byte outer = 0; outer < (columns ? 12 : 11); outer++)
                    for (byte inner = 0; inner < (columns ? 11 : 12); inner++)
                    {
                        byte x = columns ? outer : inner, y = columns ? inner : outer;
                        if (x + type.Width > 12 || y + type.Height > 11) continue;
                        if (!Enumerable.Range(x,type.Width).Any(xx => Enumerable.Range(y,type.Height).Any(yy => occupied[xx,yy]))) return (x,y);
                    }
                    return null;
                }
                var cell = Free(); if (cell is null) { Flush(); cell = Free(); }
                if (cell is not { } at) continue;
                uint id = nextId(); page.Add(new(id,at.X,at.Y,item)); if (token is Guid t) finite[id] = t;
                for (int x=at.X; x<at.X+type.Width; x++) for(int y=at.Y; y<at.Y+type.Height;y++) occupied[x,y]=true;
            }
            if (page.Count > 0) Flush();
        }
        if (profile.Kind == "Kirac")
        {
            int Order(Item item)
            {
                string id = items.ByHash(item.TypeHash)!.Id;
                if (id == "Metadata/Items/Scarabs/ScarabSettlers1") return 1;
                const string maps = "Metadata/Items/Maps/MapKeyTier";
                return id.StartsWith(maps, StringComparison.Ordinal) && int.TryParse(id[maps.Length..], out int tier)
                    ? tier + 1 : 0;
            }
            Pack("Goods", goods.OrderBy(g => Order(g.Item)), columns:true);
        }
        else
            Pack("Goods", goods.OrderBy(g => g.Token is null ? 1 : 0)
                .ThenByDescending(g => items.ByHash(g.Item.TypeHash)!.Width)
                .ThenByDescending(g => items.ByHash(g.Item.TypeHash)!.Height)
                .ThenBy(g => g.Token is null ? 0 : items.ByHash(g.Item.TypeHash)!.Row), columns:true);
        var remaining = gems.Select(items.Row).OfType<ItemCatalog.BaseType>().ToDictionary(t => t.Id);
        void GemSection(IEnumerable<IEnumerable<string>> groups)
        {
            var page = new List<InventoryItem>();
            byte x = 0, y = 0;
            void Flush()
            {
                if (page.Count > 0) tabs.Add(new("Gems",page));
                page = []; x = y = 0;
            }
            foreach (var group in groups)
            {
                bool placed = false;
                foreach (string id in group)
                {
                    if (!remaining.Remove(id,out var type) || items.Gem(type) is not { } gem) continue;
                    int gemLevel = profile.Act <= 4 && profile.Kind != "Gems" && !hardMode
                        ? Math.Min(gem.MaxLevel, skills.MaxLevelAt(gem.Variant(0).Effect, Math.Min(level,52))) : 1;
                    var item = ItemCatalog.Create(type) with { GemExperience = gem.ExperienceFor(gemLevel) };
                    if (price.Price(item) is not { } priced) { notes.Add($"No price for vendor gem {id}"); continue; }
                    if (y == 11) Flush();
                    page.Add(new(nextId(),x,y,priced)); placed = true;
                    if (++x == 12) { x = 0; y++; }
                }
                // Empty locked groups consume no space; the next available group begins on a fresh row.
                if (placed && x != 0) { x = 0; y++; }
            }
            Flush();
        }
        foreach (var section in VendorCatalog.Default.GemLayout(npc)) GemSection(section);
        // Uncaptured class-specific gems remain available; group them by level after the observed sections.
        var extra = remaining.Values.OrderBy(t => skills.PlayerLevelReq(items.Gem(t)!.Variant(0).Effect,1))
            .ThenBy(t => t.Row).GroupBy(t => skills.PlayerLevelReq(items.Gem(t)!.Variant(0).Effect,1))
            .Select(g => g.Select(t => t.Id).ToArray()).ToArray();
        GemSection(extra);
        if (tabs.Count == 0) tabs.Add(new("Goods", []));
        return new(tabs,key,finite);
    }
}
