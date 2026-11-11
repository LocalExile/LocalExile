using GameServer.Core.Protocol.Instance;

/// <summary>
/// The map tab's pages (#477). Live 2026-11-06, read from the client with a hardware breakpoint on its
/// <c>StashViewMapsNS::GenerateExpectedSubStashKeys</c> result: the client expects 191 sub-stash keys of the form
/// <c>key = (page &lt;&lt; 37) | (HASH32 of the map's base type &lt;&lt; 5) | group</c>.
/// <list type="bullet">
/// <item>group 0: the 20 map bases (the 16 tier bases and Nightmare, Vaal Temple, Valdo and Shaper Guardian maps), 6
/// pages each;</item>
/// <item>group 1: the 23 legendary maps, one page each, keyed by their visual identity;</item>
/// <item>groups 2 and 3: the four conquerors and the four elder guardians, 6 pages each, keyed 1 to 4.</item>
/// </list>
/// A page is a hidden child tab of the map tab with that key. The client drops a map on the tab itself, never on a page,
/// so <see cref="MapDestination"/> routes it to the first page of its own kind that has room: its base type, its visual
/// identity for a legendary map, or the guardian named by its map modifiers (#382).
/// </summary>
public sealed partial class AccountStash
{
    /// <summary>Pages the client expects per map base (its tier button's page strip).</summary>
    public const int MapPages = 6;

    /// <summary>The conqueror and elder-guardian groups, numbered 1 to 4 in the client's keys.</summary>
    public const int SpecialGroups = 4;

    /// <summary>The conqueror influence bits (group 2).</summary>
    const ItemInfluence Conquerors = ItemInfluence.Crusader | ItemInfluence.Redeemer | ItemInfluence.Hunter | ItemInfluence.Warlord;

    /// <summary>The key of a map tab page: page in bits 37+, the id in bits 5..36 (a base's HASH32 for group 0, the visual
    /// identity for group 1, the guardian number for groups 2 and 3), the group in the low bits.</summary>
    public static ulong MapPageKey(int page, uint typeHash, int group = 0) =>
        ((ulong)page << 37) | ((ulong)typeHash << 5) | (uint)group;

    /// <summary>The page number of a map page key.</summary>
    public static int MapPageOf(ulong key) => (int)(key >> 37);

    /// <summary>The id of a map page key (base HASH32, visual identity or guardian number).</summary>
    public static uint MapTypeHashOf(ulong key) => (uint)(key >> 5);

    /// <summary>The group of a map page key.</summary>
    public static int MapGroupOf(ulong key) => (int)(key & 0x1f);

    /// <summary>The conqueror an influence names: Baran 1, Veritania 2, Al-Hezmin 3, Drox 4, or 1 when only the elder's
    /// influence is set (an item carries no elder guardian identity; a map modifier does, see
    /// <see cref="StashRules.GuardianOf"/>).</summary>
    public static int MapPageIdFor(Item item) =>
        (item.Influences & ItemInfluence.Crusader) != 0 ? 1
        : (item.Influences & ItemInfluence.Redeemer) != 0 ? 2
        : (item.Influences & ItemInfluence.Hunter) != 0 ? 3
        : (item.Influences & ItemInfluence.Warlord) != 0 ? 4
        : 1;

    /// <summary>The map tab group and id an item belongs to: a legendary map by its visual identity (group 1), a guardian
    /// by the modifier that names it (groups 2 and 3, #382), the influence flags as the fallback, and the item's own base
    /// type for an ordinary map (group 0, id 0: the caller uses the type hash). Group -1 means a non-map.</summary>
    public (int Group, int Id) MapIdentity(Item item)
    {
        if (!item.Components.Contains("Map") && !item.Components.Contains("MapKey")) return (-1, 0);
        if (item.Rarity == ItemRarity.Unique && item.VisualIdentity != 0) return (1, item.VisualIdentity);
        if (_rules.GuardianOf(item) is { Group: > 0 } guardian) return guardian;
        if ((item.Influences & Conquerors) != 0) return (2, MapPageIdFor(item));
        if ((item.Influences & ItemInfluence.Elder) != 0) return (3, 1);
        return (0, 0);
    }

    /// <summary>Whether a page takes the item: its own base type for an ordinary page, its visual identity for a
    /// legendary map's page, and its own guardian for a conqueror's or elder's page. The same classification as
    /// <see cref="MapDestination"/> uses, so a request addressed straight at a child cannot smuggle a map into the wrong
    /// page.</summary>
    public bool MapPageTakes(ulong key, Item item)
    {
        lock (_lock)
        {
            int group = MapGroupOf(key);
            var (itemGroup, id) = MapIdentity(item);
            if (itemGroup != group) return false;
            return group == 0
                ? _rules.MapTier(item.TypeHash) is not null && MapTypeHashOf(key) == item.TypeHash
                : MapTypeHashOf(key) == (uint)id;
        }
    }

    /// <summary>Every page the client expects: six per map base (group 0), one per legendary map (group 1) and six per
    /// conqueror and elder-guardian group (groups 2 and 3), 191 in all. Existing children are left alone; returns how many
    /// were added.</summary>
    public int EnsureMapChildren()
    {
        lock (_lock)
        {
            int parent = -1;
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i].Saved.Type == StashRules.Map && _tabs[i].Saved.UniqueCategory == 0
                    && _tabs[i].Saved.Parent == ushort.MaxValue) { parent = i; break; }
            if (parent < 0) return 0;
            var have = MapChildren(parent).Select(i => _tabs[i].Saved.UniqueCategory).ToHashSet();
            int added = 0;
            void Add(ulong key, int page, string name)
            {
                if (!have.Add(key)) return;
                _tabs.Add(new(new SavedTab(name, StashRules.Map, 0x50, 0x82, 0xb4, [],
                    Position: (ushort)(page + 1), UniqueCategory: key, Parent: (ushort)parent)));
                added++;
            }
            foreach (uint hash in _rules.MapBases)
                for (int page = 0; page < MapPages; page++) Add(MapPageKey(page, hash), page, $"{page + 1}");
            foreach (uint hash in _rules.LegendaryMaps) Add(MapPageKey(0, hash, 1), 0, "1");
            for (int id = 1; id <= SpecialGroups; id++)
                for (int page = 0; page < MapPages; page++)
                {
                    Add(MapPageKey(page, (uint)id, 2), page, $"{page + 1}");
                    Add(MapPageKey(page, (uint)id, 3), page, $"{page + 1}");
                }
            if (added > 0) Persist([.. Enumerable.Range(_tabs.Count - added, added)]);
            return added;
        }
    }

    /// <summary>The hidden key of a tab: a map page key for a map page, the unique category for a unique collection,
    /// 0 for an ordinary tab. Tests and the page routing read it.</summary>
    public ulong PageKeyOf(int tab) { lock (_lock) return tab >= 0 && tab < _tabs.Count ? _tabs[tab].Saved.UniqueCategory : 0; }

    /// <summary>The hidden children of a map tab: its pages, bound by key rather than by order.</summary>
    public IReadOnlyList<int> MapChildren(int parent)
    {
        lock (_lock) return Enumerable.Range(0, _tabs.Count)
            .Where(i => _tabs[i].Saved.Type == StashRules.Map && _tabs[i].Saved.UniqueCategory != 0 && _tabs[i].Saved.Parent == parent)
            .ToArray();
    }

    /// <summary>Where a map dropped on the map tab goes: the first page of its own kind with room, in page order, found
    /// inside the caller's transaction (the client sends the drop for the tab it shows, never for the page; the same
    /// selection serves the cursor path, #605/#382). The tab itself for a non-map or a kind with no page.</summary>
    public int MapDestination(int tab, Item item, Func<Item, PlayerInventories.Shape?> shapeOf)
    {
        lock (_lock)
        {
            if (tab < 0 || tab >= _tabs.Count || _tabs[tab].Saved.Type != StashRules.Map) return tab;
            // A page addressed directly validates itself (Holds/Place use MapPageTakes).
            if (_tabs[tab].Saved.UniqueCategory != 0) return tab;
            var (group, id) = MapIdentity(item);
            if (group < 0 || (group == 0 && _rules.MapTier(item.TypeHash) is null)) return tab;
            uint keyId = group == 0 ? item.TypeHash : (uint)id;
            var pages = MapChildren(tab).ToDictionary(i => _tabs[i].Saved.UniqueCategory, i => i);
            int first = -1;
            for (int page = 0; page < MapPages; page++)
            {
                if (!pages.TryGetValue(MapPageKey(page, keyId, group), out int child)) continue;
                if (first < 0) first = child;
                if (CellFor(child, item, shapeOf) is not null) return child;
            }
            // Every page of that kind is full: answer the first one so the caller reports the tab as full.
            return first >= 0 ? first : tab;
        }
    }
}
