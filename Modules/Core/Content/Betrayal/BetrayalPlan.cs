using GameServer.Core;
using static GameServer.Modules.Betrayal.BetrayalTables;

namespace GameServer.Modules.Betrayal;

/// <summary>
/// Whether an area has Jun's mission, and where its pieces stand: the roll over the map's master-mission stats and the
/// mission's formation, both pure in their inputs so the tests and the instance agree.
/// </summary>
public static class BetrayalPlan
{
    public const string GuardMelee1 = "Metadata/Monsters/LeagueBetrayal/BetrayalOriathBlackguardMelee1CartGuard";
    public const string GuardMelee2 = "Metadata/Monsters/LeagueBetrayal/BetrayalOriathBlackguardMelee2CartGuard";
    public const string GuardRanged = "Metadata/Monsters/LeagueBetrayal/BetrayalOriathBlackguardRangedCartGuard";

    /// <summary>The Transportation's three carts, in the frame of the first: along the line to the third, and across it
    /// (cells) [capture 1/1].</summary>
    public static readonly (double Along, double Across)[] CartOffsets = [(0.0, 0.0), (20.1, 0.5), (40.3, 0.0)];

    /// <summary>The Transportation's 11 cart guards in the same frame [capture 1/1: 4 Melee1, 4 Melee2, 3 Ranged].</summary>
    public static readonly (string Variety, double Along, double Across)[] GuardOffsets =
    [
        (GuardRanged, -39.7, -21.1), (GuardRanged, -30.6, 19.3), (GuardMelee2, -26.7, -20.5), (GuardMelee2, -20.6, 19.6),
        (GuardMelee1, -19.6, -20.6), (GuardMelee1, -10.5, 19.8), (GuardMelee1, -8.4, -21.2), (GuardRanged, 2.2, 22.5),
        (GuardMelee2, -0.9, -17.2), (GuardMelee2, 12.4, 21.7), (GuardMelee1, 9.3, -18.0),
    ];

    /// <summary>Where Jun stands in that frame (cells) [capture 1/1].</summary>
    public static readonly (double Along, double Across) JunAt = (-21.8, 45.6);

    /// <summary>The Intervention's six assassination portals by tier, in the order they were created [capture 6/6].</summary>
    public static readonly int[] SpawnerTiers = [1, 1, 2, 1, 2, 1];

    /// <summary>How far the assassination portals stand from the member, in cells [capture 6/6: 41 to 73].</summary>
    public const double SpawnerNear = 41, SpawnerFar = 73;

    /// <summary>The fewest guards that must find ground for a Transportation to stand there.</summary>
    public const int GuardsNeeded = 8;

    /// <summary>The instance's mission: the member, where it starts, Jun, and the Transportation's carts and guards or the
    /// Intervention's portals.</summary>
    public sealed record Plan(Member Member, (int X, int Y) Spot, double Angle, (int X, int Y) Jun, double JunRotation,
        double MemberRotation, IReadOnlyList<(int X, int Y)> Carts, IReadOnlyList<(string Variety, (int X, int Y) Cell)> Guards,
        IReadOnlyList<(int Tier, (int X, int Y) Cell)> Spawners)
    {
        public Job Job => Member.Job;
        /// <summary>Where the member comes out: the first cart, or the spot.</summary>
        public (int X, int Y) MemberCell => Carts.Count > 0 ? Carts[0] : Spot;
    }

    // ---- the roll ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the area has Jun's mission, as the other master missions roll (MapSulphite's Niko). CoreLeagues Betrayal is a
    /// row the league chances never roll (+0xa0 set, so <see cref="LeagueChances.Count"/> gives none); its row names the
    /// stats instead [data: DisableStat map_disable_missions, LeagueChanceStat map_master_mission_jun_%_chance,
    /// BaseLeagueChanceStat map_master_mission_jun_chance_+%, ForcedSpawnStat map_betrayal_league], and the area's
    /// WorldAreaLeagueChances row its base chance (2% on the Atlas maps, 0 on Ruthless's and on boss and unique maps). An area
    /// with no normal base chance is never eligible. <c>Game_LEAGUE_CHANCES=Betrayal=N</c> is a test's chance on eligible areas.
    /// </summary>
    public static bool Rolled(string areaId, IReadOnlyDictionary<uint, int> stats, bool isMap, Random rng, bool hardMode = false, int? forced = null)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if (LeagueRow < 0
            || S(Stat.MapDisableMissions) != 0
            || LeagueRow < 31 && (S(Stat.MapDisableLeagues) & (1 << LeagueRow)) != 0
            || S(Stat.AtlasKeystoneNoExtraContent) != 0
            || S(Stat.MapNukeEverything) != 0
            || S(Stat.MapAreaUnderlevelled) != 0)
            return false;
        int normalChance = LeagueChances.Current.ChanceOf(areaId, LeagueRow, alternate: false).Chance;
        if (normalChance <= 0) return false;
        long chance;
        if (forced is int test) chance = test;
        else
        {
            if (!isMap) return false;
            if (S(Stat.MapBetrayalLeague) > 0) return true;
            int baseChance = S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0
                ? 0
                : (hardMode ? LeagueChances.Current.ChanceOf(areaId, LeagueRow, alternate: true).Chance : normalChance);
            chance = baseChance * Math.Max(0L, 100L + S(Stat.MapLeagueBaseChancePlusPctFinal)) / 100
                + S(Stat.MapLeagueContentAdditionalChancePct)
                + S(Stat.MapMasterMissionJunPctChance);
            chance = Math.Max(0L, chance) * Math.Max(0L, 100L + S(Stat.MapMasterMissionJunChancePlusPct)) / 100;
        }
        chance = Math.Clamp(chance, 0L, 100L);
        return chance >= 1 && (chance >= 100 || rng.Next(100) < chance);
    }

    /// <summary>The test chance <c>Game_LEAGUE_CHANCES</c> gives the Betrayal row, as <see cref="LeagueChances"/> reads it for
    /// the rows it rolls.</summary>
    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Betrayal", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    public static Member? Pick(IReadOnlyDictionary<int, BetrayalRanks.Target> board, Random rng)
    {
        var eligible = Candidates.Where(m => BetrayalRanks.Of(board, m).Turns == 0
            && Monsters.Find(m.Variety.ToString()) is { Supported: true }).ToArray();
        if (eligible.Length == 0) return null;
        var member = eligible[rng.Next(eligible.Length)];
        int rank = BetrayalRanks.Of(board, member).Rank;
        // Unassigned targets can join either supported mission. Board relationships and recruitment choices remain separate.
        var job = member.Job == Job.Unassigned || rank == 0 ? rng.Next(2) == 0 ? Job.Transportation : Job.Intervention : member.Job;
        return member with { Rank = rank, Job = job };
    }

    // ---- the formation -------------------------------------------------------------------------------------------

    /// <summary>origin + along · u + across · v, with u = (cos a, sin a) and v = u turned a quarter.</summary>
    static (int X, int Y) Frame((int X, int Y) origin, double angle, double along, double across) =>
        ((int)Math.Round(origin.X + along * Math.Cos(angle) - across * Math.Sin(angle)),
         (int)Math.Round(origin.Y + along * Math.Sin(angle) + across * Math.Cos(angle)));

    /// <summary>A cart faces down the line, away from the first cart.</summary>
    public static double CartRotation(double angle) => Math.Atan2(-Math.Cos(angle), Math.Sin(angle)) % Math.Tau;

    /// <summary>The nearest floor cell to <paramref name="cell"/> within a square of <paramref name="r"/>, or null.</summary>
    static (int X, int Y)? Snap(Func<int, int, bool> floor, (int X, int Y) cell, int r)
    {
        if (floor(cell.X, cell.Y)) return cell;
        (int X, int Y)? best = null;
        int bestDistance = int.MaxValue;
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int d = dx * dx + dy * dy;
                if (d < bestDistance && floor(cell.X + dx, cell.Y + dy)) (best, bestDistance) = ((cell.X + dx, cell.Y + dy), d);
            }
        return best;
    }

    /// <summary>
    /// The mission of <paramref name="member"/> at <paramref name="spot"/>, or null when it does not fit there: Jun needs
    /// ground within 6 cells of his place; a Transportation needs its three carts on ground and at least
    /// <see cref="GuardsNeeded"/> of its guards within 4 cells of theirs; an Intervention's portals stand on ground around
    /// the member. <paramref name="floor"/> is the area's walk map (false outside it). The draws are <paramref name="rng"/>'s,
    /// a few angles before giving up.
    /// </summary>
    public static Plan? Place(Member member, (int X, int Y) spot, Func<int, int, bool> floor, Random rng)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            double angle = rng.NextDouble() * Math.Tau;
            if (Snap(floor, Frame(spot, angle, JunAt.Along, JunAt.Across), 6) is not { } jun) continue;
            List<(int X, int Y)> carts = [];
            List<(string Variety, (int X, int Y) Cell)> guards = [];
            List<(int Tier, (int X, int Y) Cell)> spawners = [];
            if (member.Job == Job.Transportation)
            {
                carts = [.. CartOffsets.Select(o => Frame(spot, angle, o.Along, o.Across))];
                if (!carts.All(c => floor(c.X, c.Y))) continue;
                foreach (var (variety, along, across) in GuardOffsets)
                    if (Snap(floor, Frame(spot, angle, along, across), 4) is { } cell) guards.Add((variety, cell));
                if (guards.Count < GuardsNeeded) continue;
            }
            else
                foreach (int tier in SpawnerTiers)
                    for (int tries = 0; tries < 24; tries++)
                    {
                        double a = rng.NextDouble() * Math.Tau, r = SpawnerNear + rng.NextDouble() * (SpawnerFar - SpawnerNear);
                        var cell = ((int)Math.Round(spot.X + r * Math.Cos(a)), (int)Math.Round(spot.Y + r * Math.Sin(a)));
                        if (!floor(cell.Item1, cell.Item2)) continue;
                        spawners.Add((tier, cell));
                        break;
                    }
            double junRotation = rng.NextDouble() * Math.Tau;
            double memberRotation = member.Job == Job.Transportation ? CartRotation(angle) : rng.NextDouble() * Math.Tau;
            return new Plan(member, spot, angle, jun, junRotation, memberRotation, carts, guards, spawners);
        }
        return null;
    }
}
