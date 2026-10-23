using GameServer.Core.Protocol.Instance;

/// <summary>
/// The cosmetics picker: the vendor window (S→C 0x8c, 17-npcs.md §5) used as a browser instead of a shop. Its buttons
/// are the only place the client draws our own text, so every tab is one of the shop's town-portal effects
/// (<see cref="CosmeticCatalog.Portals"/>) and holds the single entry a click applies; the effect in use says " (worn)"
/// on its tab. Clicking an entry is C→S 0x88 (the vendor window's buy), which the Gateway takes as an apply instead.
/// <para>
/// Item names cannot be used: the tooltip draws only the base type's name, neither the Mods custom name nor the Base
/// text lines show (live 2026-10-21), and every entry is the same portal-scroll icon. The client's own Cosmetics page is
/// no option either — its entries come from GGG's web account API, which no message we own feeds
/// (Agents/docs/re/009-login-messages.md).
/// </para>
/// </summary>
sealed class CosmeticPicker
{
    /// <summary>Entries per tab: one, so a tab is one effect and its button reads the effect's name.</summary>
    public const int PerTab = 1;

    /// <summary>The picker's first tab inventory id, above the shops' (<see cref="NpcShops.FirstTabId"/>) and their sell
    /// offers' (FirstTabId + 0x10).</summary>
    public const uint FirstTabId = NpcShops.FirstTabId + 0x100;

    /// <summary>The tab id of a page (0 is the "no effect" page).</summary>
    public static uint TabId(int tab) => FirstTabId + (uint)tab;

    /// <summary>The page that turns the cosmetic off: the base portal effect.</summary>
    public const string OffName = "Off";

    /// <summary>One clickable entry: an effect (<paramref name="Effect"/>, null for the base portal), the portal
    /// variation it applies (0 = the base effect), and its cell.</summary>
    public sealed record Entry(uint ItemId, CosmeticCatalog.Cosmetic? Effect, ushort Variation, int Tab, byte X, byte Y)
    {
        /// <summary>The effect's name for the tab (without the " Portal Effect" every shop name ends with, and with the
        /// effect's number when several variations share a name).</summary>
        public string Name { get; init; } = "";
    }

    /// <summary>One tab: its id, its button's text, and its entries.</summary>
    public sealed record Page(int Tab, string Name, IReadOnlyList<Entry> Entries);

    readonly ItemCatalog.BaseType _type;
    readonly Dictionary<uint, Entry> _byItem;

    CosmeticPicker(ItemCatalog.BaseType type, List<Page> pages, Dictionary<uint, Entry> byItem, ushort worn)
    {
        _type = type;
        Pages = pages;
        _byItem = byItem;
        Worn = worn;
    }

    public IReadOnlyList<Page> Pages { get; }

    /// <summary>The portal variation in use (0 = the base effect).</summary>
    public ushort Worn { get; private set; }

    public int EntryCount => _byItem.Count;

    /// <summary>
    /// The picker for <paramref name="effects"/>: an "off" tab then one tab per effect in name order, each entry an item
    /// id from <paramref name="nextId"/>. <paramref name="worn"/> is the variation the character has now, so its tab opens
    /// marked.
    /// </summary>
    public static CosmeticPicker Build(IReadOnlyList<CosmeticCatalog.Cosmetic> effects, ItemCatalog.BaseType type,
        Func<uint> nextId, ushort worn)
    {
        var byItem = new Dictionary<uint, Entry>();
        var pages = new List<Page>();

        var off = new Entry(nextId(), null, 0, 0, 0, 0) { Name = OffName };
        byItem[off.ItemId] = off;
        pages.Add(new Page(0, OffName, [off]));

        var duplicates = effects.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sorted = effects.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Index).ToList();
        for (int at = 0, tab = 1; at < sorted.Count; at += PerTab, tab++)
        {
            var entries = new List<Entry>();
            for (int i = at; i < Math.Min(at + PerTab, sorted.Count); i++)
            {
                var effect = sorted[i];
                string name = Short(effect.Name);
                if (duplicates.Contains(effect.Name)) name += $" #{effect.Index}";
                var entry = new Entry(nextId(), effect, (ushort)effect.Index, tab, (byte)((i - at) % 12), (byte)((i - at) / 12))
                {
                    Name = name,
                };
                byItem[entry.ItemId] = entry;
                entries.Add(entry);
            }
            string label = entries.Count == 1 ? entries[0].Name : $"{entries[0].Name} – {entries[^1].Name}";
            pages.Add(new Page(tab, label, entries));
        }
        return new CosmeticPicker(type, pages, byItem, worn);
    }

    /// <summary>A shop name without the " Portal Effect" it ends with: the button strip is narrow.</summary>
    static string Short(string name) => name.EndsWith(" Portal Effect", StringComparison.OrdinalIgnoreCase)
        ? name[..^" Portal Effect".Length] : name;

    /// <summary>A tab's text, marked when it holds the effect in use.</summary>
    public string Label(Page page) => page.Entries.Any(e => e.Variation == Worn) ? $"{page.Name} (worn)" : page.Name;

    /// <summary>The entry an item id belongs to, or null (not a picker item).</summary>
    public Entry? Find(uint itemId) => _byItem.GetValueOrDefault(itemId);

    /// <summary>The entry that applies <paramref name="variation"/>, or null.</summary>
    public Entry? EntryOf(ushort variation) => _byItem.Values.FirstOrDefault(e => e.Variation == variation);

    /// <summary>The item a page shows for an entry: the portal scroll the vendor grid draws it as.</summary>
    public InventoryItem ItemFor(Entry entry) => new(entry.ItemId, entry.X, entry.Y, ItemCatalog.Create(_type));

    /// <summary>A page's items, as 0xc0 carries them.</summary>
    public IReadOnlyList<InventoryItem> Items(Page page) => [.. page.Entries.Select(ItemFor)];

    /// <summary>Applies an entry's variation; false when it was the one already in use (nothing to redraw).</summary>
    public bool Apply(uint itemId)
    {
        if (Find(itemId) is not { } entry || entry.Variation == Worn) return false;
        Worn = entry.Variation;
        return true;
    }
}
