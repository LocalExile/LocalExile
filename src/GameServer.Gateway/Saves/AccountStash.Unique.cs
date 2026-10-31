using GameServer.Core.Protocol.Instance;

sealed partial class AccountStash
{
    public int UniqueParent(int tab)
    {
        lock (_lock) return tab >= 0 && tab < _tabs.Count && _tabs[tab].Saved.UniqueCategory != 0 ? _tabs[tab].Saved.Parent : tab;
    }

    public IReadOnlyList<int> UniqueChildren(int parent)
    {
        lock (_lock) return Enumerable.Range(0, _tabs.Count).Where(i => _tabs[i].Saved.UniqueCategory != 0 && _tabs[i].Saved.Parent == parent).ToArray();
    }

    public int UniqueDestination(int tab, Item item)
    {
        lock (_lock)
        {
            if (tab < 0 || tab >= _tabs.Count || _tabs[tab].Saved.Type != StashRules.Unique) return tab;
            int parent = UniqueParent(tab);
            return _rules.Uniques.Find(item) is { } cell
                ? UniqueChildren(parent).FirstOrDefault(i => _tabs[i].Saved.UniqueCategory == cell.Category, -1) : -1;
        }
    }

    public bool Holds(int tab, Item item)
    {
        lock (_lock)
        {
            if (tab >= 0 && tab < _tabs.Count && _tabs[tab].Saved.UniqueCategory != 0)
                return _rules.Uniques.Find(item)?.Category == _tabs[tab].Saved.UniqueCategory;
            return Holds(tab, item.TypeHash);
        }
    }

    public sealed record UniqueFill(int Added, int Present, IReadOnlyList<string> Skipped);

    public UniqueFill FillUniques(int parent, UniqueCatalog catalog, Func<Item, PlayerInventories.Shape?> shapeOf)
    {
        return Saves.Atomically(() =>
        {
            lock (_lock)
            {
                if (parent < 0 || parent >= _tabs.Count || _tabs[parent].Saved.Type != StashRules.Unique
                    || _tabs[parent].Saved.UniqueCategory != 0) throw new ArgumentException("Select the Unique collection tab");
                if (_rules.Uniques.Categories.Count == 0) throw new InvalidOperationException("Unique collection layout data is unavailable");
                var children = UniqueChildren(parent).ToDictionary(i => _tabs[i].Saved.UniqueCategory);
                var changed = new HashSet<int>();
                foreach (var (category, name) in _rules.Uniques.Categories)
                    if (!children.ContainsKey(category))
                    {
                        int index = _tabs.Count;
                        if (index >= ushort.MaxValue) throw new InvalidOperationException("Too many stash tabs");
                        _tabs.Add(new(new SavedTab(name, StashRules.Unique, 0, 0, 0, [], UniqueCategory: category, Parent: (ushort)parent)));
                        children.Add(category, index); changed.Add(index);
                    }
                int added = 0, present = 0; var skipped = new List<string>();
                var considered = new HashSet<(ulong Category, byte X, byte Y)>();
                foreach (var entry in catalog.CollectionItems(Random.Shared))
                {
                    if (entry.Item is not { } item || _rules.Uniques.Find(item) is not { } cell
                        || shapeOf(item) is not { } shape)
                    { skipped.Add(entry.Name); continue; }
                    if (!considered.Add((cell.Category, cell.X, cell.Y))) continue;
                    int tab = children[cell.Category]; var t = _tabs[tab];
                    if (t.Items.Any(i => i.X == cell.X && i.Y == cell.Y)) { present++; continue; }
                    if (cell.X + shape.Width > t.Grid.W || cell.Y + shape.Height > t.Grid.H
                        || InTheWay(t, cell.X, cell.Y, shape, shapeOf).Count > 0)
                    { skipped.Add(entry.Name); continue; }
                    t.Items.Add(new InventoryItem(_nextItemId++, cell.X, cell.Y, item));
                    changed.Add(tab); added++;
                }
                if (changed.Count > 0) Persist([.. changed]);
                return new UniqueFill(added, present, skipped);
            }
        });
    }
}
