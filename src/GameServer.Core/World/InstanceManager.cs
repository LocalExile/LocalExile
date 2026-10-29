using System.Collections.Concurrent;

namespace GameServer.Core.World;

/// <summary>One running area instance. <see cref="Seed"/> drives the client's local level generation.</summary>
public sealed class AreaInstance(uint id, string areaId, uint seed, int monsterLevel, string league = "STANDARD")
{
    private readonly ConcurrentDictionary<Guid, byte> _players = new();
    public uint Id { get; } = id;
    public string AreaId { get; } = areaId;
    public uint Seed { get; } = seed;
    public int MonsterLevel { get; } = monsterLevel;
    public string League { get; } = league;
    public IReadOnlyCollection<Guid> Players => _players.Keys.ToArray();
    internal void Add(Guid c) => _players[c] = 0;
    internal void Remove(Guid c) => _players.TryRemove(c, out _);
}

/// <summary>Creates/finds instances and moves characters between them (zone transitions).</summary>
public sealed class InstanceManager(Func<uint>? seedSource = null)
{
    private readonly ConcurrentDictionary<uint, AreaInstance> _instances = new();
    private readonly ConcurrentDictionary<Guid, uint> _where = new();
    private readonly Func<uint> _seed = seedSource ?? (() => (uint)Random.Shared.NextInt64(1, uint.MaxValue));
    private readonly object _gate = new();
    private uint _next = 1;

    /// <summary>Towns are shared between players; other areas get a fresh private instance per entry.</summary>
    public AreaInstance Enter(Guid characterId, string areaId, int monsterLevel = 1, string? league = null)
    {
        lock (_gate)
        {
            Leave(characterId);
            string scope = string.IsNullOrWhiteSpace(league) ? "STANDARD" : league.Trim().ToUpperInvariant();
            AreaInstance? inst = IsShared(areaId)
                ? _instances.Values.FirstOrDefault(i => i.AreaId == areaId && i.League == scope)
                : null;
            if (inst is null)
            {
                inst = new AreaInstance(_next++, areaId, IsShared(areaId) ? 1u : _seed(), monsterLevel, scope);
                _instances[inst.Id] = inst;
            }
            inst.Add(characterId);
            _where[characterId] = inst.Id;
            return inst;
        }
    }

    public void Leave(Guid characterId)
    {
        lock (_gate)
        {
            if (!_where.TryRemove(characterId, out var id) || !_instances.TryGetValue(id, out var inst)) return;
            inst.Remove(characterId);
            if (inst.Players.Count == 0 && !IsShared(inst.AreaId)) _instances.TryRemove(id, out _);
        }
    }

    public AreaInstance? Current(Guid characterId) => _where.TryGetValue(characterId, out var id) && _instances.TryGetValue(id, out var i) ? i : null;
    public int Count => _instances.Count;
    private static bool IsShared(string areaId) => areaId.EndsWith("_town", StringComparison.Ordinal);
}
