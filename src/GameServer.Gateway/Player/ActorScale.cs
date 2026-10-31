/// <summary>Player Actor stat projection; native formula 0x1427ecc30, not a Positioned wire extension.</summary>
static class ActorScale
{
    public const uint Base = 720, Final = 1973, Capped = 1974, Limit = 1975, Permyriad = 10778;
    public static readonly string[] Inputs = ["base_actor_scale_+%", "capped_actor_scale_+%",
        "actor_scale_+%_limit", "metamorphosis_boss_actor_scale_+%_permyriad"];

    public static bool IsInput(uint key) => key is Base or Capped or Limit or Permyriad;
    public static bool IsInput(string name) => Inputs.Contains(name);
    public static int Saturate(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    // Saturate size inputs at each source dictionary boundary; unrelated aggregation keeps its existing semantics.
    public static int Add(uint key, int a, int b) => IsInput(key) ? Saturate((long)a + b) : unchecked(a + b);
    public static int Add(string name, int a, int b) => IsInput(name) ? Saturate((long)a + b) : unchecked(a + b);

    public static int Derive(int raw, int capped = 0, int permyriad = 0, int limit = 100)
    {
        // Preserve SSE order: float(permyriad)/100, then raw, then capped, then signed half and truncation.
        float value = permyriad / 100.0f;
        value += raw;
        value += Math.Clamp(capped, -50, 50);
        value += value < 0 ? -.5f : .5f;
        // Define the native conversion's out-of-int domain by saturation, before the native lower/upper bounds.
        int rounded = (int)Math.Clamp(Math.Truncate((double)value), int.MinValue, int.MaxValue);
        int result = Math.Max(-99, rounded);
        return limit == 0 ? result : Math.Min(result, limit);
    }

    public static void Project(Dictionary<uint, int> snapshot, IReadOnlyDictionary<uint, int> dev,
        IEnumerable<(uint Key, int Value)> cosmetics)
    {
        // Character.ot's baseline is an ordinary contribution, not a fallback for an absent source key.
        snapshot[Limit] = Add(Limit, 100, snapshot.GetValueOrDefault(Limit));
        foreach (var (key, value) in cosmetics)
            if (IsInput(key)) snapshot[key] = Add(key, snapshot.GetValueOrDefault(key), value);
        // An explicit zero replaces all ordinary inputs, including utility flasks and cosmetics.
        foreach (var (key, value) in dev)
            if (IsInput(key)) snapshot[key] = value;
        // Final dev replacements intentionally bypass formula clamps, like other final /set_stat replacements.
        snapshot[Final] = dev.TryGetValue(Final, out int final) ? final
            : Derive(snapshot.GetValueOrDefault(Base), snapshot.GetValueOrDefault(Capped),
                snapshot.GetValueOrDefault(Permyriad), snapshot[Limit]);
    }
}
