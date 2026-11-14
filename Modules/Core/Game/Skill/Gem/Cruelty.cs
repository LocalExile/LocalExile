// Cruelty Support (poedb): "Hits from Supported Skills grant Cruelty", "Cruelty has a Base Duration of 4 seconds",
// "(Cruelty makes all Skills Supported by Cruelty Support deal up to 40% more Damage over Time, based on the amount of
// Damage in the Hit which grants it)". The buff's effect (wiki Cruelty, the GGG explanation it cites) is
//
//     E = (1 + M) * (D / L)^0.2
//
// where D is the hit's damage, L the enemy's maximum life and M the sum of increased Cruelty effect
// (cruelty_effect_+%, the gem's own per-level and quality value); E is a percentage capped at 40 and is unaffected by
// modifiers to its effect (Mark_GGG, "Cruelty buff damage cap"). Each hit grants its own instance and only the
// strongest running one applies, as for ignite, shock or Exposure: BuffDefinitions 1815 (cruelty) shares its stacking
// class (__col7 = 13) with exactly those strongest-instance buffs. A weaker hit therefore neither lowers nor extends a
// stronger running value; it takes over when the stronger one ends.
public static partial class Game
{
    public const string CrueltyDotStat = "cruelty_damage_over_time_+%_final";
    public const string CrueltySupportedStat = "supported_by_cruelty";
    public const int CrueltyMaxPct = 40, CrueltyDurationMs = 4000;

    /// <summary>The more Damage over Time a hit grants Cruelty for: the wiki's formula, rounded and capped at 40%
    /// (<paramref name="damage"/> is limited to the life the target could lose, so overkill and culling strikes do not
    /// inflate it). 0 for a hit with no damage.</summary>
    public static int CrueltyPctFor(double damage, long maximumLife, int effectPct)
    {
        if (!(damage > 0) || maximumLife <= 0) return 0;
        double pct = 100 * Math.Max(0, 1 + effectPct / 100.0) * Math.Pow(damage / maximumLife, 0.2);
        return Math.Clamp((int)Math.Floor(pct + .5), 0, CrueltyMaxPct);
    }

    /// <summary>A Cruelty-supported skill's hit on a living monster (under the Monsters lock): an instance of this
    /// hit's strength for the full duration (a hit of a running strength refreshes it). Minions cannot gain Cruelty, so
    /// a totem's or a monster's hit never calls this. <paramref name="skill"/> is the supported skill's stats.</summary>
    public static void CrueltyOnHitLocked(ClientState conn, ushort skillId, double damage, uint lifeBefore,
        uint maximumLife, IReadOnlyDictionary<uint, int> skill, double now)
    {
        int Get(string name) => Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        if (Get(CrueltySupportedStat) == 0) return;
        int pct = CrueltyPctFor(Math.Min(damage, lifeBefore), maximumLife, Get("cruelty_effect_+%"));
        if (pct <= 0) return;
        var state = conn.SupportGems;
        double durationMs = Get("support_base_cruelty_duration_ms");
        if (durationMs <= 0) durationMs = CrueltyDurationMs;
        double until = now + durationMs / 1000.0 * Math.Max(0, 1 + Get("cruelty_duration_+%") / 100.0);
        var (before, _) = state.CrueltyActiveAt(now);
        state.CrueltyInstances[pct] = Math.Max(until, state.CrueltyInstances.GetValueOrDefault(pct));
        // An ended instance, or a weaker one that ends no later than a stronger one, can never apply again.
        foreach (var (strength, ends) in state.CrueltyInstances.ToList())
            if (ends <= now || state.CrueltyInstances.Any(i => i.Key > strength && i.Value >= ends))
                state.CrueltyInstances.Remove(strength);
        if (state.CrueltyActiveAt(now).Pct != before) state.PendingStats = true;
    }

    /// <summary>The running Cruelty value every Cruelty-supported skill gets: the strongest running instance any of
    /// them granted, as the one buff on the character shows it (BuffDefinitions 1815: "Skills supported by Cruelty
    /// Support deal more damage over time"); 0 when none is running. The caller checks the skill's own support.</summary>
    public static int CrueltyActivePct(ClientState conn, ushort skillId) =>
        conn.SupportGems.CrueltyActiveAt(conn.Clock.Elapsed.TotalSeconds).Pct;
}
