using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

public static partial class Game
{
    static readonly (uint Level, string Effect, uint Flag, ushort BuffRow)[] MapHexes =
    [
        (Stat.MapPlayerHasLevelXVulnerability, "Vulnerability", Stat.YouAreCursedWithVulnerability, 79),
        (Stat.MapPlayerHasLevelXTemporalChains, "TemporalChains", Stat.YouAreCursedWithTemporalChains, 69),
        (Stat.MapPlayerHasLevelXElementalWeakness, "ElementalWeakness", Stat.YouAreCursedWithElementalWeakness, 70),
    ];

    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<(string Effect, int Level), (uint, int)[]>> MapHexRows = new();

    static bool HasMapHexes(MapMods? map) => map is not null && MapHexes.Any(hex => map.Get(hex.Level) > 0);

    static bool IsResistanceRecipientStat(uint key) => key is Stat.BaseResistAllElementsPct
        or Stat.BaseFireDamageResistancePct or Stat.BaseColdDamageResistancePct
        or Stat.BaseLightningDamageResistancePct or Stat.BaseChaosDamageResistancePct;

    static void AddMapHexStats(Player player, MapMods? map, StatMap stats,
        IReadOnlyList<AuraCatalog.Aura>? auras = null, int extraCurseEffect = 0,
        bool includeResistances = false, IReadOnlyDictionary<uint, int>? existingVulnerability = null)
    {
        if (map is null || !HasMapHexes(map)) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        double now = AreaTime(player);
        if (player.PlayerLife == 0 || PlayerCurseImmunityAt(player, now, Get) || player.FlaskGrounds.CurseImmune
            || WitchbaneImmunityActive(player, now)) return;
        bool HasAura(string effect) => (auras ??= AffectingAuras(player)).Any(a => AuraCatalog.Family(a.EffectId) == effect);

        bool unaffected = Get(Stat.UnaffectedByCurses) != 0 || Get(Stat.VirtualUnaffectedByCurses) != 0
            || Get(Stat.Hexproof) != 0 || Get(Stat.VirtualHexproof) != 0;
        if (Get(Stat.UnaffectedByCursesWhileAffectedByZealotry) != 0)
            unaffected |= HasAura("Zealotry");

        double effect = unaffected ? 0 : Math.Max(0, 1 + (Get(Stat.CurseEffectOnSelfPlusPct) + (long)extraCurseEffect) / 100.0)
            * Math.Max(0, 1 + Get(Stat.CurseEffectOnSelfPlusPctFinal) / 100.0);
        var catalog = SkillsFor(player);
        var rows = MapHexRows.GetValue(catalog, static _ => new());
        foreach (var (source, name, flag, buffRow) in MapHexes)
        {
            int level = map.Get(source);
            if (level <= 0) continue;
            stats[flag] = 1;
            if (name == "Vulnerability") stats[Stat.CursedByVulnerability] = 1;
            if (effect == 0
                || name == "Vulnerability" && (Get(Stat.VirtualUnaffectedByVulnerability) != 0
                    || Get(Stat.UnaffectedByVulnerabilityWhileAffectedByDetermination) != 0 && HasAura("Determination"))
                || name == "TemporalChains" && (Get(Stat.UnaffectedByTemporalChains) != 0
                    || Get(Stat.VirtualUnaffectedByTemporalChains) != 0
                    || Get(Stat.UnaffectedByTemporalChainsWhileAffectedByHaste) != 0 && HasAura("Haste"))) continue;

            var native = rows.GetOrAdd((name, level), static (hex, skills) =>
                [.. skills.EffectStats(skills.EffectRow(hex.Effect), hex.Level)], catalog);
            int againstPlayers = native.FirstOrDefault(row => row.Item1 == Stat.CurseEffectPlusPctFinalVsPlayers).Item2;
            double factor = effect * Math.Max(0, 1 + againstPlayers / 100.0);
            var debuffCurse = ActivePlayerCurseStats(player, buffRow);
            foreach (var (key, value) in native)
            {
                if (!CurseRecipientStats.Contains(key) || !includeResistances && IsResistanceRecipientStat(key)) continue;
                long contribution = (long)(value * factor);
                int existing = 0;
                bool hasExisting = false;
                if (debuffCurse is not null && debuffCurse.TryGetValue(key, out int debuffValue))
                {
                    existing = debuffValue;
                    hasExisting = true;
                }
                // Doedre's Temporal Chains pillar carries its slow in action_speed_-% on buff row 69.
                if (name == "TemporalChains" && key == Stat.TemporalChainsActionSpeedPlusPctFinal
                    && debuffCurse is not null && debuffCurse.TryGetValue(Stat.ActionSpeedMinusPct, out int pillarSlow) && pillarSlow > 0)
                {
                    existing = hasExisting ? Math.Min(existing, -pillarSlow) : -pillarSlow;
                    hasExisting = true;
                }
                if (name == "Vulnerability" && existingVulnerability is not null
                    && existingVulnerability.TryGetValue(key, out int selfValue))
                {
                    existing = hasExisting
                        ? (contribution >= 0 ? Math.Max(existing, selfValue) : Math.Min(existing, selfValue))
                        : selfValue;
                    hasExisting = true;
                }
                if (hasExisting)
                    contribution = contribution >= 0 ? Math.Max(0, contribution - existing) : Math.Min(0, contribution - existing);
                if (contribution != 0)
                    stats[key] = ActorScale.Saturate((long)Get(key) + contribution);
            }
        }
    }
}
