using GameServer.Core.Protocol.Instance;

/// <summary>
/// A flask's charges with its mods and the character's (Agents/docs/protocol/13-items.md §8b): how many a use takes and
/// how many it holds, and how flasks gain them. Path of Building Item.lua: charges used = floor(base × (1 + the flask's
/// local_charges_used_+%)), then the character's flask_charges_used_+% multiplies it; maximum = base +
/// local_extra_max_charges. Gains follow poewiki (Flask, "Flask charges"): kills by rarity, increased flask charges
/// gained and the flask's increased charge recovery multiplying, partial charges kept, crits with one shared cooldown.
/// </summary>
static class FlaskCharges
{
    /// <summary>Game_FLASK_CHARGES=full: the old shortcut for local play, every kill fills every flask and a flask put on
    /// the bar keeps its charges. Otherwise (default) the game's rules.</summary>
    public static bool FullShortcut => Environment.GetEnvironmentVariable("Game_FLASK_CHARGES") == "full";

    /// <summary>A flask put on the flask bar: in a hostile area (not a town or a hideout) it has no charges (poewiki: "If a
    /// player equips a flask within a hostile area, the flask has zero active charges by default"); Game_FLASK_CHARGES=full
    /// keeps them.</summary>
    public static Item WhenEquipped(Item flask, bool safeArea) =>
        FullShortcut || safeArea || flask.Charges == 0 ? flask : flask with { Charges = 0 };

    /// <summary>What a normal monster's kill gives (poewiki: "a normal monster grants 1 charge").</summary>
    public const double KillBase = 1;

    /// <summary>The charges a kill gives every flask: <see cref="KillBase"/> × (1 + the monster rarity mod's
    /// monster_slain_flask_charges_granted_+%: Mods.dat MonsterMagic6 +250, MonsterRare6 +500, MonsterUnique6 +1000),
    /// 1 / 3.5 / 6 / 11 as poewiki gives them. Acts 6 to 10 and maps give fewer (poewiki), by an amount the tables do not
    /// have: not applied.</summary>
    public static double KillCharges(MonsterRarity rarity, ModCatalog mods) =>
        KillBase * (100 + (rarity == MonsterRarity.Normal ? 0 : RarityIncrease(rarity, mods))) / 100.0;

    static int RarityIncrease(MonsterRarity rarity, ModCatalog mods) =>
        mods.ById($"Monster{rarity}6")?.Stats.FirstOrDefault(s => s.Id == "monster_slain_flask_charges_granted_+%")?.Min ?? 0;

    /// <summary>The native raw increased-charge source, including the current ordinary belt/passive modifiers, resolved
    /// into the virtual value the refill calculation reads. Call once on raw character sources, before final developer
    /// replacements: an explicitly set virtual zero must remain zero.</summary>
    public static List<(string Stat, int Value)> GainStats(Func<string, int> character) =>
        [("flask_charges_gained_+%", (int)Math.Clamp((long)character("flask_charges_gained_+%")
            + character("charges_gained_+%"), int.MinValue, int.MaxValue))];

    /// <summary>How much more a flask gains from any source: (1 + the character's flask_charges_gained_+%) × (1 + the
    /// flask's local_charges_added_+%, "increased Charge Recovery"); poewiki: these "stack multiplicatively".</summary>
    public static double GainMultiplier(IReadOnlyDictionary<string, int> local, Func<string, int> character) =>
        Math.Max(0, 100.0 + character("flask_charges_gained_+%")) / 100.0 * Math.Max(0, 100.0 + local.GetValueOrDefault("local_charges_added_+%")) / 100.0;

    /// <summary>The cooldown shared by every "gain a flask charge on critical strike" effect: 0.1 s, which the game's
    /// 0.033 s server frames make 0.132 s (poewiki); not affected by cooldown recovery.</summary>
    public const double CritCooldown = 0.132;

    /// <summary>The character's charges for dealing a critical strike: <paramref name="Chance"/>, "#% chance to gain a
    /// Flask Charge when you deal a Critical Strike" (gain_flask_chance_on_crit_%), and <paramref name="Sure"/>, "Gain #
    /// Flask Charges when you deal a Critical Strike" (recharge_flasks_on_crit, and
    /// recharge_flasks_on_crit_while_affected_by_precision while Precision runs). Not these: gain_flask_charge_when_crit_%
    /// / _amount are for taking a critical strike (monsters do not deal critical strikes here), and
    /// gain_flask_charge_on_crit_chance_%_while_at_maximum_frenzy_charges needs frenzy charges, which do not exist here.</summary>
    public static (int Chance, int Sure) CritSources(Func<string, int> character, bool precision) =>
        (character("gain_flask_chance_on_crit_%"),
         character("recharge_flasks_on_crit") + (precision ? character("recharge_flasks_on_crit_while_affected_by_precision") : 0));

    /// <summary>What a critical strike the player deals gives the flasks (<paramref name="flasks"/>: each flask's id, its
    /// own "#% chance to gain a Flask Charge when you deal a Critical Strike" and whether it is full): one charge to each
    /// flask whose chance rolls; the character's <see cref="CritSources"/> give the first flask that is not full one
    /// charge on their chance and their sure charges (poewiki: "This effect grants one charge to one flask"). Nothing while
    /// the shared cooldown runs (<see cref="Carry.TryCrit"/>, which starts it only when some source exists).
    /// <paramref name="roll"/>: a number below its argument.</summary>
    public static Dictionary<uint, double> OnCrit(IReadOnlyList<(uint Id, int Chance, bool Full)> flasks, (int Chance, int Sure) character,
        Carry carry, double now, Func<int, int> roll)
    {
        var gains = new Dictionary<uint, double>();
        if (character.Chance <= 0 && character.Sure <= 0 && !flasks.Any(f => f.Chance > 0)) return gains;
        if (!carry.TryCrit(now)) return gains;
        foreach (var f in flasks)
            if (f.Chance > 0 && roll(100) < f.Chance) gains[f.Id] = 1;
        int charges = (character.Chance > 0 && roll(100) < character.Chance ? 1 : 0) + Math.Max(0, character.Sure);
        if (charges > 0 && flasks.Where(f => !f.Full).Select(f => (uint?)f.Id).FirstOrDefault() is uint id)
            gains[id] = gains.GetValueOrDefault(id) + charges;
        return gains;
    }

    /// <summary>What a flask gains every 3 seconds from the character's stats: flask_charges_recovered_per_3_seconds (every
    /// flask), utility_flask_charges_recovered_per_3_seconds, life_flask_charges_recovered_per_3_seconds (and
    /// life_flask_charges_recovered_per_3_seconds_on_low_life on low life), mana_flasks_gain_X_charges_every_3_seconds.</summary>
    public static int PerThreeSeconds(Func<string, int> character, bool life, bool mana, bool utility, bool lowLife) =>
        character("flask_charges_recovered_per_3_seconds")
        + (utility ? character("utility_flask_charges_recovered_per_3_seconds") : 0)
        + (life ? character("life_flask_charges_recovered_per_3_seconds") + (lowLife ? character("life_flask_charges_recovered_per_3_seconds_on_low_life") : 0) : 0)
        + (mana ? character("mana_flasks_gain_X_charges_every_3_seconds") : 0);

    /// <summary>The partial charges of one connection's flasks (poewiki: "tracked with at least two decimals") and when a
    /// crit last gave a charge.</summary>
    public sealed class Carry
    {
        readonly Dictionary<uint, double> _partial = [];
        readonly Lock _lock = new();
        double _lastCrit = double.NegativeInfinity;

        /// <summary>Adds <paramref name="charges"/> to the flask's partial charges: the whole charges to give it now (the
        /// rest, to two decimals, is kept for the next gain).</summary>
        public int Take(uint flask, double charges)
        {
            lock (_lock)
            {
                double total = Math.Round(_partial.GetValueOrDefault(flask) + charges, 2);
                double whole = Math.Floor(total);
                _partial[flask] = Math.Round(total - whole, 2);
                return (int)Math.Min(whole, int.MaxValue);
            }
        }

        /// <summary>The partial charges a flask holds.</summary>
        public double Partial(uint flask)
        {
            lock (_lock) return _partial.GetValueOrDefault(flask);
        }

        /// <summary>Whether the shared cooldown is over at <paramref name="now"/> (a cheap look before the character's stats
        /// are worked out; <see cref="TryCrit"/> starts it).</summary>
        public bool CritReady(double now)
        {
            lock (_lock) return now - _lastCrit >= CritCooldown - 1e-9;
        }

        /// <summary>Whether a crit at <paramref name="now"/> may give a charge (the shared cooldown is over); if so, the
        /// cooldown starts.</summary>
        public bool TryCrit(double now)
        {
            lock (_lock)
            {
                if (now - _lastCrit < CritCooldown - 1e-9) return false;   // clock seconds are doubles
                _lastCrit = now;
                return true;
            }
        }
    }

    /// <summary>The flask's charges per use and maximum with its local mods (<paramref name="local"/>, by Stats.dat Id)
    /// and the character's stats (<paramref name="character"/>; null: none, e.g. for refills, which only need the
    /// maximum).</summary>
    public static Flasks.Flask Of(Flasks.Flask type, IReadOnlyDictionary<string, int> local, Func<string, int>? character = null)
    {
        int perUse = (int)Math.Floor(type.ChargesPerUse * (100 + local.GetValueOrDefault("local_charges_used_+%")) / 100.0);
        perUse = (int)Math.Floor(perUse * (100 + (character?.Invoke("flask_charges_used_+%") ?? 0)) / 100.0);
        return type with
        {
            ChargesPerUse = Math.Max(0, perUse),
            MaxCharges = Math.Max(1, type.MaxCharges + local.GetValueOrDefault("local_extra_max_charges")),
        };
    }
}
