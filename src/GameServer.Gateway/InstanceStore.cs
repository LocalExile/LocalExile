using GameServer.Core.Protocol.Instance;

/// <summary>
/// What the Gateway keeps per live area instance across connections: its ground items (issue #5) and monsters (#12). An instance is known by
/// Auth's id and creation time (Auth's ids restart at 1 when it restarts). Kept in memory, so it survives client crashes,
/// logouts and disconnects; it is wiped, like Auth's instance, once no connection has been in it for the lifetime
/// (<c>InstanceRegistry.Lifetime</c>). Agents/docs/protocol/13-items.md §5.
/// </summary>
sealed class InstanceStore(TimeProvider clock, TimeSpan lifetime)
{
    readonly Dictionary<(ulong Id, long Created), InstanceState> _instances = [];

    /// <summary>A connection enters the instance: its state, a fresh one for a new or expired instance.</summary>
    public InstanceState Enter(ulong id, long created)
    {
        lock (_instances)
        {
            var now = clock.GetUtcNow();
            foreach (var (key, _) in _instances.Where(e => e.Value.EmptySince is { } t && now - t >= lifetime).ToList())
                _instances.Remove(key);
            if (!_instances.TryGetValue((id, created), out var state)) _instances[(id, created)] = state = new InstanceState();
            state.Connections++;
            state.EmptySince = null;
            return state;
        }
    }

    /// <summary>A connection left the instance; the lifetime starts when the last one leaves.</summary>
    public void Leave(InstanceState state)
    {
        lock (_instances)
            if (--state.Connections == 0) state.EmptySince = clock.GetUtcNow();
    }
}

sealed class InstanceState
{
    public InstanceGround Ground { get; } = new();
    /// <summary>The living monsters as the last connection left them (issue #12), or null before the first connection
    /// placed the area's packs.</summary>
    public volatile List<SavedMonster>? Monsters;
    internal int Connections;
    internal DateTimeOffset? EmptySince;
    readonly Dictionary<string, (uint Id, Item Weapon)> _startWeapons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts a character's start weapon (<see cref="PlayerInventories.StartWeapon"/>) on this instance's ground
    /// once: its ground id, or null when this weapon was put here before (it is still on the ground, or was picked up).
    /// A new character of a deleted one's name has a weapon of its own, so it gets it here too.</summary>
    public uint? PlaceStartWeapon(string character, WorldItemObject weapon)
    {
        lock (_startWeapons)
        {
            if (_startWeapons.TryGetValue(character, out var placed) && ReferenceEquals(placed.Weapon, weapon.Item)) return null;
            uint id = Ground.Add(weapon);
            _startWeapons[character] = (id, weapon.Item);
            return id;
        }
    }

    /// <summary>Whether ground item <paramref name="id"/> is the start weapon put here for <paramref name="character"/>.</summary>
    public bool IsStartWeapon(string character, uint id)
    {
        lock (_startWeapons) return _startWeapons.TryGetValue(character, out var placed) && placed.Id == id;
    }

    public uint? PlaceTutorialItem(string key, WorldItemObject item)
    {
        lock (_startWeapons)
        {
            if (_startWeapons.TryGetValue(key, out var placed) && Ground.Get(placed.Id) is not null) return null;
            uint id = Ground.Add(item);
            _startWeapons[key] = (id, item.Item);
            return id;
        }
    }
}

/// <summary>An instance's items on the ground. Their object ids come from their own range, so they stay the same for every
/// connection and never meet the ids a connection gives its monsters and effects (0x3000 up).</summary>
sealed class InstanceGround
{
    public const uint FirstItemId = 0x100000;
    readonly Dictionary<uint, WorldItemObject> _items = [];
    uint _nextId = FirstItemId;

    public void Restore(uint id, WorldItemObject item)
    {
        lock (_items) _items[id] = item;
    }

    public uint Add(WorldItemObject item)
    {
        lock (_items)
        {
            uint id = _nextId++;
            _items[id] = item;
            return id;
        }
    }

    public WorldItemObject? Get(uint id)
    {
        lock (_items) return _items.GetValueOrDefault(id);
    }

    /// <summary>Removes the item: only one caller gets it, so a pickup can never duplicate it.</summary>
    public WorldItemObject? Take(uint id)
    {
        lock (_items) return _items.Remove(id, out var item) ? item : null;
    }

    public IReadOnlyList<(uint Id, WorldItemObject Item)> All()
    {
        lock (_items) return [.. _items.Select(e => (e.Key, e.Value))];
    }

    /// <summary>The items of <paramref name="which"/> within <paramref name="range"/> cells (centre to centre) of a cell.</summary>
    public IReadOnlyList<(uint Id, WorldItemObject Item)> Within(int x, int y, double range, Func<WorldItemObject, bool> which)
    {
        lock (_items)
            return [.. _items.Where(e => which(e.Value) && Math.Sqrt((double)(e.Value.GridX - x) * (e.Value.GridX - x)
                + (double)(e.Value.GridY - y) * (e.Value.GridY - y)) <= range).Select(e => (e.Key, e.Value))];
    }
}

/// <summary>A living monster of an instance, as it was when the last connection left: re-created there with this life on
/// the next entry, idle until the player comes in range. Dead monsters are not kept, so they stay dead.</summary>
// shortcut: a /spawn_mob name is not kept (packs have none), add it when named monsters matter.
sealed record SavedMonster(int Variety, byte Level, MonsterRarity Rarity, bool Hostile, int X, int Y, uint Life)
{
    public static List<SavedMonster> Save(IEnumerable<SpawnedMonster> monsters, double now) =>
        [.. monsters.Where(m => !m.Dead && m.Variety is not null).Select(m =>
        {
            var (x, y) = m.Walk.Position(now);
            return new SavedMonster(m.Variety!.Row, m.Level, m.Rarity, m.Hostile, x, y, m.Life);
        })];
}
