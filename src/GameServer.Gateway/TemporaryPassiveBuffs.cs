using System.Collections.Concurrent;

/// <summary>Short-lived combat buffs share an absolute expiry across area connections; they are not permanent save data.</summary>
sealed class TemporaryPassiveBuffs
{
    public sealed record Effect(PassiveEffectCatalog.Buff Buff, DateTimeOffset ExpiresAt);
    static readonly ConcurrentDictionary<string, TemporaryPassiveBuffs> characters = new(StringComparer.OrdinalIgnoreCase);
    public static TemporaryPassiveBuffs For(string character) => characters.GetOrAdd(character, _ => new());
    readonly object gate = new();
    readonly Dictionary<int, Effect> effects = [];

    public IReadOnlyList<Effect> Active(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var (id, effect) in effects.ToArray()) if (effect.ExpiresAt <= now) effects.Remove(id);
            return effects.Values.ToArray();
        }
    }

    public bool Grant(PassiveEffectCatalog.Buff buff, TimeSpan duration, DateTimeOffset now)
    {
        if (duration <= TimeSpan.Zero) return false;
        lock (gate)
        {
            // Adrenaline cannot be gained/refreshed while already active.
            if (effects.TryGetValue(buff.Template, out var current) && current.ExpiresAt > now) return false;
            effects[buff.Template] = new(buff, now + duration);
            return true;
        }
    }

    public void Clear() { lock (gate) effects.Clear(); }
}
