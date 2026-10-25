using System.Collections.Concurrent;

/// <summary>Current Rage, its shared on-hit cooldown and the mana-spending accumulator. Transient across saves.</summary>
sealed class RageState
{
    public readonly record struct Value(int Current, int Maximum, int Effective);
    static readonly ConcurrentDictionary<string, RageState> characters = new(StringComparer.OrdinalIgnoreCase);
    public static RageState For(string character) => characters.GetOrAdd(character, _ => new());
    readonly object gate = new();
    decimal current;
    uint manaRemainder;
    DateTimeOffset? advancedAt;
    DateTimeOffset activityAt = DateTimeOffset.MinValue, hitGainAt = DateTimeOffset.MinValue;

    public Value Read(DateTimeOffset now, Func<string, int> get, bool paused = false)
    {
        lock (gate)
        {
            int maximum = (int)Math.Clamp(30L + get("maximum_rage"), 0, int.MaxValue);
            if (get("gain_x_rage_per_200_mana_spent") <= 0) manaRemainder = 0;
            if (get("maximum_rage_is_halved") != 0) maximum /= 2;
            if (advancedAt is { } previous && now > previous)
            {
                if (paused)
                {
                    if (activityAt != DateTimeOffset.MinValue) activityAt += now - previous;
                    if (hitGainAt != DateTimeOffset.MinValue) hitGainAt += now - previous;
                }
                else if (get("no_inherent_rage_loss") == 0 && current > 0)
                {
                    var lossStarts = activityAt.AddMilliseconds(Math.Max(0L, 2000L + get("rage_loss_delay_ms")));
                    var from = previous > lossStarts ? previous : lossStarts;
                    if (now > from)
                        current -= (decimal)(now - from).TotalSeconds * 10
                            * Math.Max(0m, 100m + get("rage_loss_delay_recovery_rate_+%")) / 100;
                }
            }
            if (advancedAt is null || now > advancedAt) advancedAt = now;
            current = Math.Clamp(current, 0, maximum);
            int whole = (int)decimal.Ceiling(current);
            int effective = (int)Math.Clamp(decimal.Truncate(whole * Math.Max(0m, 100m + get("rage_effect_+%")) / 100), 0, int.MaxValue);
            return new(whole, maximum, effective);
        }
    }

    public void SpendMana(uint amount, DateTimeOffset now, Func<string, int> get)
    {
        lock (gate)
        {
            Read(now, get);
            int per200 = get("gain_x_rage_per_200_mana_spent");
            if (per200 <= 0) { manaRemainder = 0; return; }
            ulong total = (ulong)manaRemainder + amount;
            manaRemainder = (uint)(total % 200);
            Gain((decimal)(total / 200) * per200, now, get);
        }
    }

    public void Hit(DateTimeOffset now, Func<string, int> get, bool attack, bool melee, bool critical, int roll)
    {
        long amount = get("gain_x_rage_on_hit") + (attack ? (long)get("gain_x_rage_on_attack_hit") : 0)
            + (melee ? (long)get("gain_x_rage_on_melee_hit") : 0)
            + (attack && critical ? (long)get("gain_x_rage_on_attack_crit") : 0)
            + (melee && critical ? (long)get("gain_x_rage_on_melee_crit") : 0);
        lock (gate) GainFromHit(amount, now, get, roll);
    }

    public void EnemyHit(DateTimeOffset now, Func<string, int> get, int roll)
    {
        lock (gate)
        {
            Read(now, get);
            activityAt = now;
            GainFromHit(get("gain_x_rage_when_hit"), now, get, roll);
        }
    }

    void GainFromHit(long amount, DateTimeOffset now, Func<string, int> get, int roll)
    {
        Read(now, get);
        if (amount <= 0 || (now - hitGainAt).TotalSeconds < .5) return;
        hitGainAt = now;
        int maximum = Read(now, get).Maximum;
        Gain(roll < Math.Clamp(get("gain_max_rage_on_rage_gain_from_hit_%_chance"), 0, 100) ? maximum : amount, now, get);
    }

    void Gain(decimal amount, DateTimeOffset now, Func<string, int> get)
    {
        if (amount <= 0) return;
        current = Math.Min(Read(now, get).Maximum, current + amount);
        activityAt = now;
    }

    public void Clear()
    {
        lock (gate)
        {
            current = 0; manaRemainder = 0; advancedAt = null;
            activityAt = hitGainAt = DateTimeOffset.MinValue;
        }
    }
}
