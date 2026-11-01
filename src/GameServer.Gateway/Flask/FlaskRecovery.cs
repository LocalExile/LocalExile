using GameServer.Core.Protocol.Instance;

/// <summary>
/// One use of a life or mana flask with its quality, its mods and the character's flask stats (Agents/docs/protocol/
/// 13-items.md §8): what it recovers instantly and over how long, as Path of Building's Item.lua and CalcPerform
/// calcFlaskRecovery and poewiki (Flask, "Recovery from flasks") compute it.
/// </summary>
static class FlaskRecovery
{
    /// <summary>A use: life and mana recovered over <paramref name="Seconds"/> and instantly, what it takes from the other
    /// pool on use, the mana that comes all at once at the end instead (0: none), whether the effect stays on a full pool, the extra
    /// life a life flask's suffix recovers over 10 s when used below full life, and whether it does not queue (Enduring:
    /// "Effect is not removed when Unreserved Mana is Filled / Effect does not Queue").</summary>
    public sealed record Use(double Life, double Mana, double Seconds, double InstantLife, double InstantMana,
        double RemovesLife, double RemovesMana, double ManaAtEnd, bool LifeStaysAtFull, bool ManaStaysAtFull, double ExtraLife,
        bool NoQueue = false, double? LifeSeconds = null, double? ManaSeconds = null);

    /// <summary>The extra life's duration: "Recover an additional #% of Life over 10 seconds if used while not on Full
    /// Life" (local_flask_additional_x%_life_recovery_per_second_over_10_seconds_if_not_on_full_life).</summary>
    public const double ExtraLifeSeconds = 10;

    /// <summary>A use's instant part and what it takes from the other pool, applied to <paramref name="life"/> and
    /// <paramref name="mana"/> (life never below 1, mana up to <paramref name="manaCap"/>): the new pools, or null when the
    /// player is dead (life 0) — a flask must not bring a dead player back to 1 life.</summary>
    public static (uint Life, uint Mana)? Instant(Use r, uint life, uint mana, uint maxLife, int manaCap) =>
        life == 0 ? null
            : ((uint)Math.Clamp(Math.Floor(life + r.InstantLife - r.RemovesLife), 1, maxLife),
               (uint)Math.Clamp(Math.Floor(mana + r.InstantMana - r.RemovesMana), 0, Math.Max(0, manaCap)));

    /// <summary>Low life is half of maximum life or less (Path of Building Data.lua LowPoolThreshold 0.5).</summary>
    public static bool LowLife(uint life, uint maxLife) => life * 2 <= maxLife;

    /// <summary>The use of <paramref name="type"/> (Flasks.dat) as <paramref name="flask"/>, with its local mods
    /// (<paramref name="local"/>, by Stats.dat Id) and the character's stats (<paramref name="character"/>).</summary>
    /// <remarks>
    /// Amount: base × (1 + quality) × (1 + increased amount recovered) × (1 + the character's increased life / mana
    /// recovery from flasks) × (1 + increased flask effect duration, which lengthens the recovery at the same rate) ×
    /// (increased flask effect + the low-life more multiplier − 1: the low-life part is not scaled by flask effect).
    /// Instant part: the partial-instant mods (50%), all of it
    /// with "Instant Recovery" or, on low life, "Instant Recovery when on Low Life". Time: base × (1 + increased duration)
    /// / (1 + increased recovery rate), the character's flask recovery rate dividing it again.
    /// </remarks>
    public static Use For(Flasks.Flask type, Item flask, IReadOnlyDictionary<string, int> local, Func<string, int> character,
        uint life, uint maxLife, int playerLevel = 1)
    {
        int L(string name) => local.GetValueOrDefault(name);
        bool lowLife = LowLife(life, maxLife);
        double quality = 1 + flask.Quality / 100.0;
        double lowLifeMore = lowLife ? 1 + L("local_flask_amount_to_recover_+%_when_on_low_life") / 100.0 : 1;
        int instant = Math.Min(100, L("local_flask_recovery_amount_%_to_recover_instantly")
            + (L("local_flask_recovers_instantly") > 0 ? 100 : 0)
            + (lowLife && L("local_flask_recover_instantly_when_on_low_life") > 0 ? 100 : 0));
        double durationInc = PanelStats.FlaskDurationIncrease(character, playerLevel);
        double effect = FlaskEffectScaling.Multiplier(character, playerLevel, L("local_flask_effect_+%"));
        // The low-life more multiplier is not scaled by flask effect (Path of Building CalcPerform: "More life recovery
        // while on low life is not affected by flask effect (verified ingame)"): × (effect + more − 1), not × more × effect.
        double Amount(int baseAmount, string localSpecific, string characterSpecific) =>
            baseAmount * quality * (100 + L("local_flask_amount_to_recover_+%") + L(localSpecific)) / 100.0
            * (100 + character(characterSpecific) + character("flask_life_and_mana_to_recover_+%")) / 100.0 * (effect + lowLifeMore - 1);
        double life0 = Amount(type.LifePerUse, "local_flask_life_to_recover_+%", "flask_life_to_recover_+%");
        double mana0 = Amount(type.ManaPerUse, "local_flask_mana_to_recover_+%", "flask_mana_to_recover_+%");
        double commonRate = (100.0 + L("local_flask_recovery_speed_+%")) / 100.0
            * (100.0 + character("flask_recovery_speed_+%")) / 100.0;
        // Pinned PoB CalcPerform calculates each resource independently: generic rate × that pool's rate.
        // A hybrid's faster Mana recovery must not accelerate its Life recovery or shorten its remaining Life effect.
        double Seconds(string typedRate) => instant >= 100 ? 0
            : type.Seconds * (100.0 + L("local_flask_duration_+%")) / 100.0 * (100.0 + durationInc) / 100.0
                / Math.Max(0.01, commonRate * (100.0 + character(typedRate)) / 100.0);
        double lifeSeconds = type.LifePerUse > 0 ? Seconds("flask_life_recovery_rate_+%") : 0;
        double manaSeconds = type.ManaPerUse > 0 ? Seconds("flask_mana_recovery_rate_+%") : 0;
        double seconds = Math.Max(lifeSeconds, manaSeconds);
        // Increased flask effect duration recovers for longer at the same rate: more in all (not for the instant part).
        double overTime = 1 - instant / 100.0, longer = (100 + durationInc) / 100.0;
        double lifeOverTime = life0 * overTime * longer, manaOverTime = mana0 * overTime * longer;
        double lifeTotal = lifeOverTime + life0 * instant / 100.0, manaTotal = manaOverTime + mana0 * instant / 100.0;
        bool manaAtEnd = L("local_flask_mana_recovery_occurs_instantly_at_end_of_flask_effect") > 0 && manaSeconds > 0;
        return new Use(
            Life: lifeOverTime, Mana: manaAtEnd ? 0 : manaOverTime, Seconds: seconds,
            InstantLife: life0 * instant / 100.0, InstantMana: mana0 * instant / 100.0,
            RemovesLife: manaTotal * L("local_flask_removes_%_of_mana_recovery_from_life_on_use") / 100.0,
            RemovesMana: lifeTotal * L("local_flask_removes_%_of_life_recovery_from_mana_on_use") / 100.0,
            ManaAtEnd: manaAtEnd ? manaOverTime : 0,
            LifeStaysAtFull: character("life_flask_effects_are_not_removed_at_full_life") > 0,
            ManaStaysAtFull: L("local_flask_effect_not_removed_at_full_mana") > 0 || character("mana_flask_effects_are_not_removed_at_full_mana") > 0,
            // "Recover an additional #% of Flask's Life Recovery Amount over 10 seconds if used while not on Full Life".
            ExtraLife: life < maxLife ? lifeTotal * L("local_flask_additional_x%_life_recovery_per_second_over_10_seconds_if_not_on_full_life") / 100.0 : 0,
            // "Effect does not Queue": Enduring, and the character's "Life / Mana Flask Effects are not removed when … is
            // Filled / … do not Queue" on that kind of flask.
            NoQueue: L("local_flask_effect_not_removed_at_full_mana") > 0
                || type.LifePerUse > 0 && character("life_flask_effects_are_not_removed_at_full_life") > 0
                || type.ManaPerUse > 0 && character("mana_flask_effects_are_not_removed_at_full_mana") > 0,
            LifeSeconds: lifeSeconds, ManaSeconds: manaSeconds);
    }
}
