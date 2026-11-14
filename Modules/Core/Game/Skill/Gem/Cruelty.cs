// Cruelty Support (poedb): "Hits from Supported Skills grant Cruelty", "Cruelty has a Base Duration of 4 seconds",
// "(Cruelty makes all Skills Supported by Cruelty Support deal up to 40% more Damage over Time, based on the amount of
// Damage in the Hit which grants it)". The buff's effect (wiki Cruelty, the GGG explanation it cites) is
//
//     E = (1 + M) * (D / L)^0.2
//
// where D is the hit's damage, L the enemy's maximum life and M the sum of increased Cruelty effect
// (cruelty_effect_+%, the gem's own per-level and quality value); E is a percentage capped at 40 and is unaffected by
// modifiers to its effect (Mark_GGG, "Cruelty buff damage cap"). PoB's default model is the maximum value for a
// Cruelty-supported main skill (CalcPerform.lua: multipliers["Cruelty"] = 40), so a hit never downgrades a stronger
// running buff; it only refreshes its duration.
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

    /// <summary>A Cruelty-supported skill's hit on a living monster (under the Monsters lock): the stronger of the one
    /// running buff and this hit's value, with the buff's duration refreshed, whichever supported skill granted the
    /// running value. Minions cannot gain Cruelty, so a totem's or a monster's hit never calls this.
    /// <paramref name="skill"/> is the supported skill's stats.</summary>
    public static void CrueltyOnHitLocked(ClientState conn, ushort skillId, double damage, uint lifeBefore,
        uint maximumLife, IReadOnlyDictionary<uint, int> skill, double now)
    {
        int Get(string name) => Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        if (Get(CrueltySupportedStat) == 0) return;
        int pct = CrueltyPctFor(Math.Min(damage, lifeBefore), maximumLife, Get("cruelty_effect_+%"));
        if (pct <= 0) return;
        var state = conn.SupportGems;
        // Cruelty is one buff on the character: the running value and deadline are the strongest and latest any
        // supported skill holds, and this hit refreshes that one buff rather than only its own skill's entry, so a
        // weaker hit from a second skill never lets a stronger running value lapse.
        int running = 0;
        double runningUntil = 0;
        foreach (var (other, otherUntil) in state.CrueltyUntil)
        {
            if (otherUntil <= now) continue;
            running = Math.Max(running, state.CrueltyPct.GetValueOrDefault(other));
            runningUntil = Math.Max(runningUntil, otherUntil);
        }
        int value = Math.Max(running, pct);
        double durationMs = Get("support_base_cruelty_duration_ms");
        if (durationMs <= 0) durationMs = CrueltyDurationMs;
        double until = Math.Max(runningUntil, now + durationMs / 1000.0 * Math.Max(0, 1 + Get("cruelty_duration_+%") / 100.0));
        if (running == 0 || value != running) state.PendingStats = true;
        state.CrueltyPct.Clear();
        state.CrueltyUntil.Clear();
        state.CrueltyPct[skillId] = value;
        state.CrueltyUntil[skillId] = until;
    }

    /// <summary>The running Cruelty value every Cruelty-supported skill gets: the strongest one any of them granted, as
    /// the one buff on the character shows it (BuffDefinitions 1815: "Skills supported by Cruelty Support deal more
    /// damage over time"); 0 when none is running. The caller checks the skill's own support.</summary>
    public static int CrueltyActivePct(ClientState conn, ushort skillId)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        int best = 0;
        foreach (var skill in conn.SupportGems.CrueltyUntil.Keys)
            best = Math.Max(best, conn.SupportGems.CrueltyActiveAt(skill, now));
        return best;
    }
}
