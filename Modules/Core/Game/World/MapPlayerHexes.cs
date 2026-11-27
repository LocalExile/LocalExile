using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

public static partial class Game
{
    static readonly (uint Level, string Effect, uint Flag)[] MapHexes =
    [
        (Stat.MapPlayerHasLevelXVulnerability, "Vulnerability", Stat.YouAreCursedWithVulnerability),
        (Stat.MapPlayerHasLevelXTemporalChains, "TemporalChains", Stat.YouAreCursedWithTemporalChains),
        (Stat.MapPlayerHasLevelXElementalWeakness, "ElementalWeakness", Stat.YouAreCursedWithElementalWeakness),
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
        foreach (var (source, name, flag) in MapHexes)
        {
            int level = map.Get(source);
            if (level <= 0) continue;
            stats[flag] = 1;
            if (name == "Vulnerability") stats[Stat.CursedByVulnerability] = 1;
            if (effect == 0 || name == "Vulnerability" && (Get(Stat.VirtualUnaffectedByVulnerability) != 0
                || Get(Stat.UnaffectedByVulnerabilityWhileAffectedByDetermination) != 0
                    && HasAura("Determination"))) continue;

            var native = rows.GetOrAdd((name, level), static (hex, skills) =>
                [.. skills.EffectStats(skills.EffectRow(hex.Effect), hex.Level)], catalog);
            int againstPlayers = native.FirstOrDefault(row => row.Item1 == Stat.CurseEffectPlusPctFinalVsPlayers).Item2;
            double factor = effect * Math.Max(0, 1 + againstPlayers / 100.0);
            foreach (var (key, value) in native)
            {
                if (!CurseRecipientStats.Contains(key) || !includeResistances && IsResistanceRecipientStat(key)) continue;
                long contribution = (long)(value * factor);
                if (name == "Vulnerability" && existingVulnerability is not null
                    && existingVulnerability.TryGetValue(key, out int existing))
                    contribution = contribution >= 0 ? Math.Max(0, contribution - existing) : Math.Min(0, contribution - existing);
                if (contribution != 0)
                    stats[key] = ActorScale.Saturate((long)Get(key) + contribution);
            }
        }
    }
}
