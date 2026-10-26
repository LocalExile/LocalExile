using System.Collections.Concurrent;

/// <summary>Transient character-owned summons. A connection lease prevents the old area's AI from changing the new
/// area's roster. Area travel keeps the original deadline; logout and owner death remove the summons.</summary>
sealed class SummonRoster
{
    static readonly ConcurrentDictionary<string, SummonRoster> Characters = new(StringComparer.OrdinalIgnoreCase);
    public static SummonRoster For(string character) => Characters.GetOrAdd(character, _ => new());
    public sealed class Member(long id, DateTimeOffset expires)
    {
        public long Id { get; } = id;
        public DateTimeOffset Expires { get; internal set; } = expires;
        public uint? Life { get; internal set; }
        public bool RefreshLife { get; internal set; }
    }
    readonly object _gate = new();
    readonly List<Member> _members = [];
    Guid _owner;
    long _nextId;
    DateTimeOffset? _pausedAt;
    DateTimeOffset _nextTrigger = DateTimeOffset.MinValue;

    public void Attach(Guid owner)
    {
        lock (_gate) _owner = owner;
    }

    public void Pause(Guid owner, DateTimeOffset now, bool pause)
    {
        lock (_gate) if (_owner == owner) Advance(now, pause);
    }

    void Advance(DateTimeOffset now, bool paused)
    {
        if (paused)
        {
            _pausedAt ??= now;
            return;
        }
        if (_pausedAt is { } start)
        {
            var elapsed = now - start;
            foreach (var member in _members) member.Expires += elapsed;
            if (_nextTrigger != DateTimeOffset.MinValue) _nextTrigger += elapsed;
            _pausedAt = null;
        }
        _members.RemoveAll(m => m.Life == 0 || m.Expires <= now);
    }

    public Member[] Read(Guid owner, DateTimeOffset now, bool paused, bool enabled, int limit)
    {
        lock (_gate)
        {
            if (_owner != owner) return [];
            Advance(now, paused);
            if (!enabled) _members.Clear();
            if (_members.Count > limit) _members.RemoveRange(Math.Max(0, limit), _members.Count - Math.Max(0, limit));
            return [.. _members];
        }
    }

    public Member? Trigger(Guid owner, DateTimeOffset now, bool paused, int limit, int durationMs, int cooldownMs)
    {
        lock (_gate)
        {
            if (_owner != owner || paused || limit <= 0 || durationMs <= 0) return null;
            Advance(now, false);
            if (now < _nextTrigger) return null;
            _nextTrigger = now.AddMilliseconds(Math.Max(0, cooldownMs));
            if (_members.Count < limit)
            {
                var created = new Member(++_nextId, now.AddMilliseconds(durationMs));
                _members.Add(created);
                return created;
            }
            // The skill's own description refreshes an existing wolf at the limit. Refresh the one expiring first.
            var refresh = _members.MinBy(m => m.Expires)!;
            refresh.Expires = now.AddMilliseconds(durationMs);
            refresh.Life = null;
            refresh.RefreshLife = true;
            return refresh;
        }
    }

    public bool SetLife(Guid owner, long id, uint life)
    {
        lock (_gate)
        {
            if (_owner != owner || _members.FirstOrDefault(m => m.Id == id) is not { } member) return false;
            member.Life = life;
            return true;
        }
    }

    public bool TakeRefresh(Guid owner, long id)
    {
        lock (_gate)
        {
            if (_owner != owner || _members.FirstOrDefault(m => m.Id == id) is not { RefreshLife: true } member) return false;
            member.RefreshLife = false;
            return true;
        }
    }

    public void Release(Guid owner, bool transfer)
    {
        lock (_gate)
        {
            if (_owner != owner) return;
            if (!transfer) { _members.Clear(); _nextTrigger = DateTimeOffset.MinValue; }
            _owner = Guid.Empty;
        }
    }

    public void Clear(Guid owner)
    {
        lock (_gate)
            if (_owner == owner) { _members.Clear(); _nextTrigger = DateTimeOffset.MinValue; }
    }
}
