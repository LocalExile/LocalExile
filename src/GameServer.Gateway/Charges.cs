/// <summary>The three standard charges every character can hold (issue #237).</summary>
enum ChargeType { Endurance, Frenzy, Power }

/// <summary>
/// Endurance, Frenzy and Power charges: how many a character can hold, for how long, and how they are gained, refreshed,
/// expired and lost. The rules, from the Charge page of the wiki (Mark_GGG's base duration) and Path of Building's
/// CalcSetup/CalcPerform:
/// <list type="bullet">
/// <item>a charge lasts 10 seconds (<see cref="BaseDurationMs"/>), raised by the general and the type's own increased
/// charge duration (<see cref="LimitsOf"/>);</item>
/// <item>gaining a charge resets the duration of every charge of that type, also at the maximum, where no charge is
/// added; when the duration runs out all charges of the type expire together;</item>
/// <item>the maximum is <see cref="ChargePanelStats"/>' (3 from Character.ot plus the maximum-charge mods); minimum
/// charges are always held, and the maximum wins over the minimum (−2 maximum and +2 minimum leave 1);</item>
/// <item>death takes every charge (BuffDefinitions <c>CancelOnDeath</c> on <c>endurance_charge</c>,
/// <c>frenzy_charge</c>, <c>power_charge</c>).</item>
/// </list>
/// Times are the connection's game clock in seconds (it stops while the game is paused). The state is not thread-safe:
/// the Gateway keeps it under the connection's Monsters lock.
/// </summary>
static class Charges
{
    /// <summary>A charge's base duration: 10 seconds (wiki "Charge", Mark_GGG 2013; Path of Building CalcSetup's
    /// <c>ChargeDuration</c> BASE 10). No game table holds it.</summary>
    public const int BaseDurationMs = 10_000;

    public static readonly ChargeType[] Types = [ChargeType.Endurance, ChargeType.Frenzy, ChargeType.Power];

    /// <summary>The type's word in stat ids: <c>endurance</c>, <c>frenzy</c>, <c>power</c>.</summary>
    public static string Name(ChargeType type) => type switch
    {
        ChargeType.Endurance => "endurance",
        ChargeType.Frenzy => "frenzy",
        ChargeType.Power => "power",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ChargeType? Parse(string text) => text.ToLowerInvariant() switch
    {
        "endurance" or "e" or "str" or "brutal" => ChargeType.Endurance,
        "frenzy" or "f" or "dex" or "affliction" => ChargeType.Frenzy,
        "power" or "p" or "int" or "absorption" => ChargeType.Power,
        _ => null,
    };

    /// <summary>The special charge a type's charges become under its conversion flag: <c>brutal</c> for Endurance,
    /// <c>affliction</c> for Frenzy, <c>absorption</c> for Power (the three Domain 1 unique mods
    /// <c>Gain*ChargesInsteadOf*Unique__1</c>).</summary>
    public static string SpecialName(ChargeType type) => type switch
    {
        ChargeType.Endurance => "brutal",
        ChargeType.Frenzy => "affliction",
        ChargeType.Power => "absorption",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>"Gain Brutal Charges instead of Endurance Charges": <c>gain_brutal_charges_instead_of_endurance_charges</c>
    /// and the Frenzy and Power ones. While the flag is held, every charge of the type the character gains or holds is
    /// one of the special type (pinned PoB CalcPerform: the special count takes the standard count, which becomes 0), with
    /// the special type's effects and HUD buff instead of the standard ones; the held state, limits and gains are the
    /// standard type's.</summary>
    public static string ConversionFlag(ChargeType type) => $"gain_{SpecialName(type)}_charges_instead_of_{Name(type)}_charges";

    /// <summary>"Modifiers to Minimum Endurance Charges instead apply to Brutal Charges":
    /// <c>modifiers_to_minimum_endurance_charges_instead_apply_to_brutal_charges</c> and the other two. The minimum then
    /// shows on the special type's row.</summary>
    public static string MinimumConversionFlag(ChargeType type) => $"modifiers_to_minimum_{Name(type)}_charges_instead_apply_to_{SpecialName(type)}_charges";

    /// <summary>Whether the type's charges are the special type's now (<paramref name="get"/>: the character's stats).</summary>
    public static bool Converted(ChargeType type, Func<string, int> get) => get(ConversionFlag(type)) != 0;

    /// <summary>The word for the charges the character holds of a type: the special name under the conversion flag.</summary>
    public static string Label(ChargeType type, bool converted) => converted ? SpecialName(type) : Name(type);

    /// <summary>What a type allows now: its maximum, its minimum (already capped by the maximum) and how long a gained
    /// charge lasts, in milliseconds.</summary>
    public readonly record struct Limits(int Maximum, int Minimum, int DurationMs);

    /// <summary>
    /// The type's limits from the character's stats (<paramref name="get"/>: by Stats.dat Id, 0 when absent).
    /// Maximum: <see cref="ChargePanelStats.Compute"/>. Minimum: <c>base_minimum_*_charges</c> and
    /// <c>minimum_frenzy_power_endurance_charges</c>, at most the maximum. Duration: 10 s × (1 + increased / 100) ×
    /// (1 + more / 100), increased being <c>charge_duration_+%</c>, <c>base_charge_duration_+%</c> and the type's own
    /// stat (<c>endurance_charge_duration_+%</c>, <c>base_frenzy_charge_duration_+%</c>, <c>power_charge_duration_+%</c>:
    /// the stats the panel's duration rows sum, PanelStats) plus <c>frenzy_charge_duration_+%_per_frenzy_charge</c> ×
    /// the frenzy charges held after the gain (<paramref name="held"/>); more is <c>power_charge_duration_+%_final</c>.
    /// Path of Building floors the result to whole seconds for its display; the game counts milliseconds, so this
    /// floors to milliseconds.
    /// </summary>
    public static Limits LimitsOf(ChargeType type, Func<string, int> get, int held = 0)
    {
        string name = Name(type);
        int maximum = ChargePanelStats.Compute(get).First(r => r.Stat == $"virtual_maximum_{name}_charges").Value;
        int minimum = Math.Clamp(get($"base_minimum_{name}_charges") + get("minimum_frenzy_power_endurance_charges"), 0, maximum);
        int increased = get("charge_duration_+%") + get("base_charge_duration_+%") + type switch
        {
            ChargeType.Endurance => get("endurance_charge_duration_+%"),
            ChargeType.Frenzy => get("base_frenzy_charge_duration_+%") + get("frenzy_charge_duration_+%_per_frenzy_charge") * held,
            _ => get("power_charge_duration_+%"),
        };
        int more = type == ChargeType.Power ? get("power_charge_duration_+%_final") : 0;
        double ms = BaseDurationMs * Math.Max(0, 100 + increased) / 100.0 * Math.Max(0, 100 + more) / 100.0;
        return new(maximum, minimum, (int)Math.Floor(ms));
    }

    /// <summary>One type's charges as an area change hands them on: how many were held and how much of their duration was
    /// left (seconds).</summary>
    public readonly record struct Carried(ChargeType Type, int Held, double Remaining);

    /// <summary>
    /// The charges a character holds: per type the number gained (on top of nothing: the minimum is added by
    /// <see cref="Effective"/>, not stored) and when they expire. Gaining refreshes the expiry of the whole type;
    /// expiring removes every gained charge of the type at once.
    /// </summary>
    public sealed class State
    {
        private readonly int[] held = new int[3];
        private readonly double[] expiresAt = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];

        /// <summary>The charges gained and not expired yet (without the minimum).</summary>
        public int Held(ChargeType type) => held[(int)type];

        /// <summary>When the type's charges expire (game clock seconds); −∞ when none are held.</summary>
        public double ExpiresAt(ChargeType type) => expiresAt[(int)type];

        /// <summary>The seconds left before the type expires, 0 when none are held.</summary>
        public double Remaining(ChargeType type, double now) => held[(int)type] > 0 ? Math.Max(0, expiresAt[(int)type] - now) : 0;

        /// <summary>The charges the character has: the gained ones, at least the minimum, at most the maximum
        /// (Path of Building: max(charges, min(maximum, minimum))).</summary>
        public int Effective(ChargeType type, Limits limits) =>
            Math.Clamp(Math.Max(held[(int)type], limits.Minimum), 0, Math.Max(0, limits.Maximum));

        /// <summary>
        /// Gains <paramref name="count"/> charges of a type at <paramref name="now"/>: added on top of the charges the
        /// character has (the minimum ones count), up to the maximum, and the duration of all of them starts again, at the
        /// maximum too. With a maximum of 0 nothing is gained. <paramref name="toMaximum"/>: "you instead gain up to
        /// maximum" charges. Returns the effective count before and after.
        /// </summary>
        public (int Before, int After) Gain(ChargeType type, int count, double now, Limits limits, bool toMaximum = false)
        {
            int before = Effective(type, limits);
            if (count <= 0 || limits.Maximum <= 0) return (before, before);
            int i = (int)type;
            held[i] = toMaximum ? limits.Maximum : Math.Min(limits.Maximum, before + count);
            expiresAt[i] = now + limits.DurationMs / 1000.0;
            return (before, Effective(type, limits));
        }

        /// <summary>
        /// Loses <paramref name="count"/> charges of a type (Replica Fragility, Replica Pacifism and Replica
        /// Powerlessness take one each second): the gained ones go first, never below the minimum the character keeps.
        /// The duration of what is left stays as it was. Returns the effective count before and after.
        /// </summary>
        public (int Before, int After) Lose(ChargeType type, int count, Limits limits)
        {
            int before = Effective(type, limits);
            if (count <= 0) return (before, before);
            int i = (int)type;
            held[i] = Math.Clamp(before - count, 0, Math.Max(0, limits.Maximum));
            if (held[i] == 0) expiresAt[i] = double.NegativeInfinity;
            return (before, Effective(type, limits));
        }

        /// <summary>Sets a type's charges directly (the /charges command): <paramref name="count"/> held, lasting their
        /// full duration from now. 0 removes them.</summary>
        public void Set(ChargeType type, int count, double now, Limits limits)
        {
            int i = (int)type;
            held[i] = Math.Clamp(count, 0, Math.Max(0, limits.Maximum));
            expiresAt[i] = held[i] > 0 ? now + limits.DurationMs / 1000.0 : double.NegativeInfinity;
        }

        /// <summary>The types whose duration ran out by <paramref name="now"/>: all their gained charges go at once (the
        /// minimum stays).</summary>
        public List<ChargeType> Expire(double now)
        {
            var expired = new List<ChargeType>();
            foreach (var type in Charges.Types)
            {
                int i = (int)type;
                if (held[i] > 0 && now >= expiresAt[i])
                {
                    held[i] = 0;
                    expiresAt[i] = double.NegativeInfinity;
                    expired.Add(type);
                }
            }
            return expired;
        }

        /// <summary>Keeps a type within a maximum that went down (an item taken off): the charges above it are lost.</summary>
        public bool Clamp(ChargeType type, Limits limits)
        {
            int i = (int)type;
            int cap = Math.Max(0, limits.Maximum);
            if (held[i] <= cap) return false;
            held[i] = cap;
            if (cap == 0) expiresAt[i] = double.NegativeInfinity;
            return true;
        }

        /// <summary>Every gained charge goes (death). Returns whether any was held.</summary>
        public bool Clear()
        {
            bool any = held.Any(h => h > 0);
            Array.Clear(held);
            Array.Fill(expiresAt, double.NegativeInfinity);
            return any;
        }

        /// <summary>What an area change carries on: each held type with the duration it has left.</summary>
        public List<Carried> Carry(double now) =>
            [.. Charges.Types.Where(t => held[(int)t] > 0 && expiresAt[(int)t] > now).Select(t => new Carried(t, held[(int)t], expiresAt[(int)t] - now))];

        /// <summary>The charges carried from the last area, from <paramref name="now"/> on the new area's clock.</summary>
        public void Restore(IEnumerable<Carried> carried, double now)
        {
            foreach (var c in carried)
                if (c.Held > 0 && c.Remaining > 0)
                {
                    held[(int)c.Type] = c.Held;
                    expiresAt[(int)c.Type] = now + c.Remaining;
                }
        }
    }
}
