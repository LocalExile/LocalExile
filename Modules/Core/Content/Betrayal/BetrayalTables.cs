using GameServer.Core;

namespace GameServer.Modules.Betrayal;

/// <summary>
/// The client tables a Betrayal mission reads, loaded once with the module (<see cref="LoadGameData"/>) and looked up by Id,
/// never by a row number.
/// </summary>
public static class BetrayalTables
{
    /// <summary>A BetrayalJobs row a mission in a map can be: the two a server places with objects and monsters alone.
    /// BaseDefence (1, Fortification) and LabDefence (4, Research) need the level generator's own fort and laboratory.</summary>
    public enum Job { Unassigned = 0, Transportation = 2, Intervention = 3 }

    /// <summary>A Syndicate member who leads a mission: the BetrayalTargets row (the board and the MakeChoice name it by
    /// row), its Id, its division (BetrayalJobsKey) and its monster (MonsterVarietiesKey).</summary>
    public sealed record Member(int Row, string Id, Job Job, ushort Variety)
    {
        public int Rank { get; init; }
    }

    static int leagueRow = -1;
    static byte? assassinationRules;
    static IReadOnlyList<Member> members = [], targets = [], candidates = [];

    /// <summary>The CoreLeagues row of Betrayal, looked up by its Id (a bit of MapDisableLeagues).</summary>
    public static int LeagueRow => leagueRow;

    /// <summary>The Rulesets row JobAssassinationRules, the Intervention's (the 0x1f and the 0x2c9 name it).</summary>
    public static byte? AssassinationRules => assassinationRules;

    /// <summary>The members who lead a mission: the BetrayalTargets rows whose division is Transportation or Intervention
    /// and that have a monster, in table order. The others are in a division a map mission here does not place
    /// (Fortification, Research), in none (Haku, Hillock, Cameria, Riker) or not on the board at all (Catarina and the
    /// rows without a division).</summary>
    public static IReadOnlyList<Member> Members => members;
    public static IReadOnlyList<Member> Targets => targets;
    public static IReadOnlyList<Member> Candidates => candidates;

    /// <summary>Reads the module's tables; called from the module's <c>Load</c>.</summary>
    public static void LoadGameData()
    {
        leagueRow = RowOf("CoreLeagues", "Betrayal");
        assassinationRules = RowOf("Rulesets", "JobAssassinationRules") is int rules and >= 0 and <= byte.MaxValue ? (byte)rules : null;
        targets = [.. ClientTables.TableOrEmpty("BetrayalTargets")
            .Where(r => r.Index > 0 && r.IntOrNull("MonsterVarietiesKey") is >= 0 and <= ushort.MaxValue)
            .Select(r => new Member(r.Index, r.TextOrNull("Id") ?? "", (Job)(r.IntOrNull("BetrayalJobsKey") ?? 0),
                (ushort)r.Int("MonsterVarietiesKey")) { Rank = r.IntOrNull("BetrayalRanksKey") ?? 0 })];
        members = [.. targets.Where(m => m.Job is Job.Transportation or Job.Intervention)];
        candidates = [.. targets.Where(m => m.Job is Job.Unassigned or Job.Transportation or Job.Intervention)];
    }

    /// <summary>The row of <paramref name="table"/> whose Id is <paramref name="id"/>, or −1.</summary>
    static int RowOf(string table, string id) =>
        ClientTables.TableOrEmpty(table).Where(r => r.TextOrNull("Id") == id).Select(r => r.Index).DefaultIfEmpty(-1).First();
}
