using GameServer.Core;

namespace GameServer.Modules.Village;

/// <summary>
/// Mining and smelting: the town's two production lines, the ones the mine panel (S→C <c>0x7b</c> kind <c>0x57</c>) and
/// the blacksmith panel (<c>0x5a</c>) draw.
/// <para>
/// One line is one row of <c>VillageProduction</c>: an <c>Input</c> resource it consumes, an <c>Output</c> it makes, the
/// <c>BaseProductionTime</c> one unit takes, and the <c>Job</c> row of <c>VillageJobs</c> whose workers run it. The five
/// <c>Mining*</c> rows eat a <c>Pending*</c> row (0–4, the ore a map's deposits marked) and make the ore row (5–9); the
/// five <c>Smelting*</c> rows eat that ore and make the bar row (15–19).
/// </para>
/// <para>
/// The rate is the client's own (<c>0x141769b20</c>): <c>3600 × Σefficiency / BaseProductionTime</c> a line, where a
/// worker's efficiency is the <c>VillageJobSkillLevels</c> row its skill byte names — its second column
/// (<c>0x1424c2af0</c> reads <c>[row+4]</c>, and <c>0x1424c2e20</c> adds <c>100 ×</c> that per worker into the line's
/// ledger). The column holds 12, 18, 27, 37, 49 and 63 for job levels 0…5, which is the community's rank table scaled by
/// 18: 1×, 1.5×, 2.05×, 2.72× and 3.5×. The client draws the panel's "Production Rate" and its "Time" from that same
/// ledger, so the server has to use those numbers too or the counters it sends and the countdown it shows drift apart.
/// </para>
/// <para>
/// The server owns the counters: they are 21 resource counts and 15 production counts in the settlement record, which S→C
/// <c>0x2c9</c> kind <c>0x9b</c> subtag 4 carries. A panel whose counters were never sent shows zeros for ever, which is
/// what a town with no line running looks like.
/// </para>
/// </summary>
public static class VillageProduction
{
    /// <summary>One <c>VillageProduction</c> row: what a job turns into what, and how long one unit takes.</summary>
    /// <param name="Id">The row's name (<c>Mining1</c>, <c>Smelting5</c>).</param>
    /// <param name="JobRow">The <c>VillageJobs</c> row its workers hold (1–5 mining, 6–10 smelting).</param>
    /// <param name="Kind">The <c>VillageJobTypes</c> row of that job.</param>
    /// <param name="Input">The <c>VillageResources</c> row it consumes.</param>
    /// <param name="Output">The <c>VillageResources</c> row it makes.</param>
    /// <param name="Seconds">How long one unit takes one worker (<c>BaseProductionTime</c>).</param>
    public sealed record Line(int Row, string Id, int JobRow, int Kind, int Input, int Output, int Seconds);

    /// <summary>The <c>VillageJobTypes</c> rows this file runs (the client's own ids).</summary>
    public const int MiningKind = 1, SmeltingKind = 2;

    /// <summary>The stats the lines read, by their <c>Stats</c> row: the two speed percentages
    /// (<c>VillageJobTypes.JobSpeedStat</c>), the worker caps and ore ranks of <c>VillageBaseStats</c>, and the gold one
    /// mined ore is worth.</summary>
    public const int MiningSpeed = 20695, SmithingSpeed = 20696;
    public const int MaxMiners = 20713, MineRank = 20714, MaxSmiths = 20715, SmithRank = 20716;
    public const int GoldPerOre = 20872;

    /// <summary>How far one advance may jump: a town left alone for a day must not mint a day of ore at once.</summary>
    public const double MaxStep = 4 * 3600;

    /// <summary>The <c>VillageProduction</c> rows this file runs, by the row's own index. The state block's 15 production
    /// counters are one per table row, in row order, so a line is looked up by its row and never by its position in a
    /// filtered list: the farming rows (10–14) have no <c>VillageJobs</c> row and are left out, but they still own
    /// counters of their own.</summary>
    static readonly Line?[] ByRow = new Line?[15];
    static int[] JobKinds = [];
    static int[] JobRanks = [];
    static int[] Efficiency = [];
    static Dictionary<int, int> BaseStats = [];

    /// <summary>The rows' count: one counter each, the farming ones included.</summary>
    public const int Counters = 15;

    /// <summary>Reads the client's tables. The village module calls this when it loads, with the rest of the game data.</summary>
    public static void LoadGameData()
    {
        JobKinds = [.. ClientTables.TableOrEmpty("VillageJobs").Select(row => (byte)(row.IntOrNull("JobType") ?? 0))];
        JobRanks = [.. ClientTables.TableOrEmpty("VillageJobs").Select(row => row.IntOrNull("StatValue") ?? 0)];
        // The efficiency a job level gives: the client reads the skill-level row's second column (0x1424c2af0, the column
        // the table calls PlayerLevelRequired), so the server reads the same one. Its values 18 to 63 are the community's
        // rank curve 1x / 1.5x / 2.05x / 2.72x / 3.5x, 18 being the unit.
        var levels = ClientTables.TableOrEmpty("VillageJobSkillLevels")
            .Where(row => row.IntOrNull("JobLevel") is not null).ToList();
        var efficiency = new int[levels.Count > 0 ? levels.Max(row => row.IntOrNull("JobLevel")!.Value) + 1 : 0];
        foreach (var row in levels)
            efficiency[row.IntOrNull("JobLevel")!.Value] = row.IntOrNull("PlayerLevelRequired") ?? 0;
        Efficiency = efficiency;
        BaseStats = [];
        foreach (var row in ClientTables.TableOrEmpty("VillageBaseStats"))
            if (row.IntOrNull("Stat") is { } stat && row.IntOrNull("StatValue") is { } value) BaseStats[stat] = value;
        Array.Clear(ByRow);
        // The row number is the row's place in the table, not a column: client.db keeps a table's rows in their own order
        // (Mining5 first, then Mining4…, the reversed `_row` the table dump shows), the same order the client's own row
        // array has, and the state block's production counters are one per row in that order.
        int rowIndex = 0;
        foreach (var row in ClientTables.TableOrEmpty("VillageProduction"))
        {
            int index = rowIndex++;
            if (index >= ByRow.Length) break;
            if (row.TextOrNull("Id") is not { } id
                || !(id.StartsWith("Mining", StringComparison.Ordinal) || id.StartsWith("Smelting", StringComparison.Ordinal)))
                continue;
            int job = row.IntOrNull("Job") ?? -1;
            ByRow[index] = new Line(index, id, job, KindOfJob(job), row.IntOrNull("Input") ?? -1,
                row.IntOrNull("Output") ?? -1, Math.Max(1, row.IntOrNull("BaseProductionTime") ?? 1));
        }
    }

    /// <summary>The <c>VillageJobTypes</c> row a <c>VillageJobs</c> row belongs to (1 mining, 2 smelting).</summary>
    public static int KindOfJob(int jobRow) => jobRow >= 0 && jobRow < JobKinds.Length ? JobKinds[jobRow] : 0;

    /// <summary>The ore rank a job row works, its own <c>StatValue</c> (1–5): the rank
    /// <c>village_can_mine_ores_up_to_rank</c> / <c>village_can_smith_ores_up_to_rank</c> has to reach.</summary>
    public static int RankOfJob(int jobRow) => jobRow >= 0 && jobRow < JobRanks.Length ? JobRanks[jobRow] : 0;

    /// <summary>The efficiency a skill byte gives: the <c>VillageJobSkillLevels</c> row whose <c>JobLevel</c> is that byte,
    /// and within it the second column the client reads (18 for level 1, 63 for level 5 — the same curve the community
    /// table states as 1×, 1.5×, 2.05×, 2.72× and 3.5×). A level the table does not have works at the lowest level's rate.</summary>
    public static int EfficiencyOf(byte skill) =>
        skill < Efficiency.Length && Efficiency[skill] > 0
            ? Efficiency[skill]
            : Efficiency.Length > 0 && Efficiency[0] > 0 ? Efficiency[0] : 18;

    /// <summary>A stat of the settlement: the client's <c>VillageBaseStats</c> value plus what the town has bought.</summary>
    public static int Stat(VillageModule.Settlement settlement, int stat) =>
        BaseStats.GetValueOrDefault(stat) + settlement.Stats.GetValueOrDefault(stat);

    /// <summary>How many of the settlement's workers hold this job row.</summary>
    public static int WorkersOn(VillageModule.Settlement settlement, int jobRow)
    {
        int workers = 0;
        foreach (var pair in settlement.Assignments) if (pair.Value == jobRow) workers++;
        return workers;
    }

    /// <summary>The efficiency of everyone on a job row, summed as the client sums it.</summary>
    static int Crew(VillageModule.Settlement settlement, int jobRow)
    {
        int crew = 0;
        foreach (var pair in settlement.Assignments)
            if (pair.Value == jobRow) crew += EfficiencyOf(settlement.Ranks.GetValueOrDefault(pair.Key));
        return crew;
    }

    /// <summary>How many of the settlement's workers hold a job of this type (1 mining, 2 smelting): the number the job
    /// type's <c>MaxWorkersStat</c> caps.</summary>
    public static int CrewOf(VillageModule.Settlement settlement, int kind)
    {
        int crew = 0;
        foreach (var pair in settlement.Assignments) if (KindOfJob(pair.Value) == kind) crew++;
        return crew;
    }

    /// <summary>The counter of a <c>VillageResources</c> row (0 for "Ore Marked" 0–4, the ore 5–9, the bars 15–19).</summary>
    public static uint Resource(VillageModule.Settlement settlement, int row) =>
        row < 0 ? 0 : settlement.Resources.GetValueOrDefault(row);

    /// <summary>What one line makes in an hour at the settlement's present crew — the panel's "Production Rate".</summary>
    public static double RatePerHour(VillageModule.Settlement settlement, in Line line)
    {
        int crew = Crew(settlement, line.JobRow);
        if (crew <= 0) return 0;
        return 3600.0 * crew / line.Seconds;
    }

    /// <summary>Whether the job row is unlocked: its rank has to be within what the town can mine or smith.</summary>
    public static bool Unlocked(VillageModule.Settlement settlement, int jobRow) => KindOfJob(jobRow) switch
    {
        MiningKind => RankOfJob(jobRow) <= Stat(settlement, MineRank),
        SmeltingKind => RankOfJob(jobRow) <= Stat(settlement, SmithRank),
        _ => true,
    };

    /// <summary>What a line's counter is on the wire: the work done on the unit in progress, in hundredths of one unit's
    /// own time (<c>0x1424c5b80</c> subtracts it from <c>units × 100 × BaseProductionTime</c> and divides by the crew).</summary>
    public static uint Counter(VillageModule.Settlement settlement, int index)
    {
        if (index < 0 || index >= ByRow.Length || ByRow[index] is not { } line) return 0;
        double progress = Math.Clamp(settlement.Progress[index], 0, 0.999);
        return (uint)Math.Floor(100.0 * line.Seconds * progress);
    }

    /// <summary>What one advance did.</summary>
    /// <param name="Units">Units made, all lines together.</param>
    /// <param name="Gold">Gold the newly mined ore is worth (<c>village_gold_per_ore_mined</c> a unit).</param>
    /// <param name="Progressed">A line moved, so its counter changed.</param>
    public readonly record struct Moved(int Units, uint Gold, bool Progressed);

    /// <summary>
    /// Runs the lines for <paramref name="seconds"/> of game time: mining first, because the ore it makes is what the
    /// smelters eat. A line needs a worker (its job row's crew) and a unit of its input; the unit in progress waits where
    /// it is when the input runs out instead of being lost.
    /// </summary>
    public static Moved Advance(VillageModule.Settlement settlement, double seconds)
    {
        if (seconds <= 0) return default;
        if (seconds > MaxStep) seconds = MaxStep;
        int units = 0;
        uint gold = 0;
        bool progressed = false;
        for (int pass = 0; pass < 2; pass++)
        {
            int kind = pass == 0 ? MiningKind : SmeltingKind;
            for (int i = 0; i < ByRow.Length; i++)
            {
                if (ByRow[i] is not { } line) continue;
                if (line.Kind != kind || !Unlocked(settlement, line.JobRow)) continue;
                double rate = RatePerHour(settlement, line);
                if (rate <= 0) continue;
                if (Fallback(settlement, line) is null && settlement.Progress[i] < 1) continue;
                double next = settlement.Progress[i] + rate / 3600.0 * seconds;
                for (int guard = 0; guard < 4096 && next >= 1; guard++)
                {
                    // The ore the crew is actually working: its own, and when that has nothing marked, another one the
                    // town holds. The wiki's own wording is "If no marked ore deposits that are designated to a Miner are
                    // available, they will automatically mine another available ore type" (the smelters' line reads the
                    // same way); the client's ledger models the same thing through its priority ore (0x1424c3fb0 writes
                    // the priority rank's count at ledger +0x30 and marks it chosen at +0x34).
                    if (Fallback(settlement, line) is not (int input, int output)) { next = 1; break; }
                    settlement.Resources[input] = Resource(settlement, input) - 1;
                    settlement.Resources[output] = Resource(settlement, output) + 1;
                    units++;
                    if (kind == MiningKind) gold += (uint)Math.Max(0, Stat(settlement, GoldPerOre));
                    next -= 1;
                }
                if (next != settlement.Progress[i]) progressed = true;
                settlement.Progress[i] = Math.Min(next, 1);
            }
        }
        return new Moved(units, gold, progressed);
    }

    /// <summary>The rows a line may work right now: its own input and output when the town holds a unit of it, and
    /// otherwise the richest ore it could work instead (the highest rank with units marked), which is the "another
    /// available ore type" the wiki describes. Null when the town holds nothing this kind can eat.</summary>
    /// <param name="settlement">The settlement whose counters are read.</param>
    /// <param name="line">The production row whose crew is working.</param>
    static (int Input, int Output)? Fallback(VillageModule.Settlement settlement, in Line line)
    {
        if (Resource(settlement, line.Input) > 0) return (line.Input, line.Output);
        // Mining eats 0–4 (Pending*) and makes 5–9; smelting eats 5–9 and makes 15–19. Both are +5 and +10 off the
        // ore's rank, so the fallback keeps the rank and only changes which ore the crew is on.
        for (int rank = 5; rank >= 1; rank--)
        {
            int input = line.Kind == MiningKind ? rank - 1 : rank + 4;
            if (Resource(settlement, input) == 0) continue;
            return (input, line.Kind == MiningKind ? rank + 4 : rank + 14);
        }
        return null;
    }

    /// <summary>Adds marked ore to the town (what a map's ore deposit does when it is claimed): the mining rows eat it.
    /// </summary>
    /// <param name="settlement">The settlement the ore belongs to.</param>
    /// <param name="rank">The ore's rank, 1–5, which is the <c>VillageResources</c> row of its <c>Pending*</c> count.</param>
    /// <param name="amount">How many units were claimed.</param>
    /// <returns>The row's new count.</returns>
    public static uint AddPendingOre(VillageModule.Settlement settlement, int rank, int amount)
    {
        int row = Math.Clamp(rank - 1, 0, 4);
        uint next = Resource(settlement, row) + (uint)Math.Max(0, amount);
        settlement.Resources[row] = next;
        return next;
    }
}