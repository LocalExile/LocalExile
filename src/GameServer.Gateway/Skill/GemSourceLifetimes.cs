/// <summary>
/// One continuous lifetime of a physical gem source on this connection. A local lease can observe a shared marker owned
/// by the character's actual <see cref="PlayerInventories"/>. Every local access is made under
/// <see cref="ClientState.Monsters"/>. A retired lifetime is irrevocable; a later source with the same key gets a new one.
/// </summary>
sealed class GemSourceLifetimes
{
    /// <summary>The physical lifetimes shared by every connection using one loaded inventory. Socket mutations and owner
    /// replacement retire markers here; dictionary access uses only this short lock and never a connection's Monsters.</summary>
    internal sealed class SourceStore
    {
        readonly Dictionary<GemSkillIds.Gem, SourceMarker> _current = [];
        readonly Dictionary<(string Namespace, ushort Skill), ActivationOwner> _writers = [];
        readonly Lock _lock = new();
        bool _retired;

        internal readonly record struct ActivationOwner(SourceMarker Marker, Guid Owner);

        internal bool Retired => Volatile.Read(ref _retired);

        internal SourceMarker? Capture(GemSkillIds.Gem key)
        {
            lock (_lock)
            {
                if (_retired) return null;
                if (_current.GetValueOrDefault(key) is { Retired: false } current) return current;
                _current[key] = current = new(key, this);
                return current;
            }
        }

        internal void RetireSocket(uint host, int socket)
        {
            lock (_lock)
            {
                if (_retired) return;
                var retiring = _current.Where(pair =>
                    pair.Key.Source == GemSkillIds.SourceKind.SocketedGem
                    && pair.Key.Host == host && pair.Key.Socket == socket).Select(pair => pair.Value).ToArray();
                foreach (var marker in retiring)
                {
                    _current.Remove(marker.Key);
                    marker.Retire();
                }
                foreach (var entry in _writers.Where(pair => retiring.Contains(pair.Value.Marker)).ToArray())
                    _writers.Remove(entry.Key);
            }
        }

        /// <summary>Makes <paramref name="owner"/> the connection that owns this saved source's current activation.</summary>
        internal bool ClaimActivation(string saveNamespace, ushort skill, SourceMarker marker, Guid owner)
        {
            lock (_lock)
            {
                if (_retired || !ReferenceEquals(marker.Owner, this) || marker.Retired) return false;
                _writers[(saveNamespace, skill)] = new(marker, owner);
                return true;
            }
        }

        /// <summary>The current activation writer, without constructing an inventory or entering a Saves transaction.</summary>
        internal ActivationOwner? ActivationWriter(string saveNamespace, ushort skill)
        {
            lock (_lock)
            {
                if (_retired) return null;
                var key = (saveNamespace, skill);
                if (!_writers.TryGetValue(key, out var writer)) return null;
                if (!writer.Marker.Retired) return writer;
                _writers.Remove(key);
                return null;
            }
        }

        internal void RetireOwner()
        {
            lock (_lock)
            {
                if (_retired) return;
                Volatile.Write(ref _retired, true);
                var retiring = _current.Values.ToArray();
                foreach (var marker in retiring) marker.Retire();
                _current.Clear();
                _writers.Clear();
            }
        }
    }

    internal sealed class SourceMarker(GemSkillIds.Gem key, SourceStore owner)
    {
        internal GemSkillIds.Gem Key { get; } = key;
        internal SourceStore Owner { get; } = owner;
        bool _retired;
        internal bool Retired => Volatile.Read(ref _retired) || Owner.Retired;
        internal void Retire() => Volatile.Write(ref _retired, true);
    }

    internal sealed class SourceLifetime(GemSkillIds.Gem key, SourceMarker? parent = null, bool retired = false)
    {
        internal GemSkillIds.Gem Key { get; } = key;
        internal SourceMarker? Parent { get; } = parent;
        bool _retired = retired;
        internal bool Retired => Volatile.Read(ref _retired) || Parent is { Retired: true };

        internal void Retire() => Volatile.Write(ref _retired, true);
    }

    readonly Dictionary<GemSkillIds.Gem, SourceLifetime> _current = [];

    internal SourceLifetime Capture(GemSkillIds.Gem key)
    {
        if (_current.GetValueOrDefault(key) is not { Retired: false } current)
            _current[key] = current = new(key);
        return current;
    }

    internal SourceLifetime Capture(GemSkillIds.Gem key, SourceMarker parent)
    {
        if (parent.Key != key) throw new ArgumentException("the shared marker belongs to another gem source", nameof(parent));
        if (_current.GetValueOrDefault(key) is { Retired: false } current
            && ReferenceEquals(current.Parent, parent))
            return current;
        return _current[key] = new(key, parent);
    }

    internal SourceLifetime CaptureRetired(GemSkillIds.Gem key) => new(key, retired: true);

    internal SourceLifetime? Current(GemSkillIds.Gem key) => _current.GetValueOrDefault(key);

    internal bool IsCurrent(SourceLifetime lifetime) =>
        !lifetime.Retired && _current.GetValueOrDefault(lifetime.Key) is { } current
        && ReferenceEquals(current, lifetime);

    internal void Retire(GemSkillIds.Gem key)
    {
        if (_current.Remove(key, out var lifetime)) lifetime.Retire();
    }
}

sealed partial class ClientState
{
    /// <summary>The connection's current physical gem-source lifetimes, under <see cref="Monsters"/>.</summary>
    public readonly GemSourceLifetimes GemSourceLifetimes = new();
}
