using System.Text.Json;

namespace GameServer.Modules.Village;

/// <summary>
/// The town panel's workers: the rows the client keeps for the people of the town (S→C <c>0x2c9</c>, kind <c>0x9b</c>,
/// subtag <c>5</c>).
/// <para>
/// A row is the worker's name as two <c>Words.dat</c> keys, the job it holds and its skill level:
/// <c>u32 name key 1 | u32 name key 2 | u16 | u8 count | count × (u8 job type, u8 skill level) | u8 VillageJobs row</c>.
/// The client's reader is <c>0x1417737d0</c>: the last byte names the <c>VillageJobs</c> row, and the client's job resolver
/// (<c>0x142252050</c>) turns that row into a <c>VillageJobTypes</c> row index, which has to be one of the pairs the row
/// carries — a name that resolves to a job the row does not list leaves the client reading a job that is not there and it
/// walks into a NULL. The whole payload is <c>12 + 2 × count</c> bytes; one byte short and the client reads past the end
/// of the message and stops itself with an <c>int3</c>.
/// </para>
/// </summary>
public static class VillageWorkers
{
    /// <summary>The words a worker's name is made of (the client's <c>Words.dat</c>, wordlist 1).</summary>
    const string WordsFile = "data/tables/Words.json";

    /// <summary>The jobs a worker can hold (the client's <c>VillageJobs.dat</c>).</summary>
    const string JobsFile = "data/tables/VillageJobs.json";

    static readonly Lazy<(uint[] Words, byte[] JobTypes)> Tables = new(Load);

    static (uint[] Words, byte[] JobTypes) Load()
    {
        var words = new List<uint>();
        foreach (var row in Rows(WordsFile))
        {
            // the name words come from wordlist 1, the one the town panel's names live in
            if (row.TryGetValue("Wordlist", out var list) && list.ValueKind == JsonValueKind.Number && list.GetInt32() != 1)
                continue;
            if (row.TryGetValue("HASH32", out var hash) && hash.ValueKind == JsonValueKind.Number)
                words.Add(hash.GetUInt32());
        }

        var types = new List<byte>();
        foreach (var row in Rows(JobsFile))
            types.Add(row.TryGetValue("JobType", out var type) && type.ValueKind == JsonValueKind.Number
                ? (byte)type.GetInt32() : (byte)0);

        return (words.ToArray(), types.ToArray());
    }

    static IEnumerable<Dictionary<string, JsonElement>> Rows(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(File.ReadAllText(file)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The job type the client resolves a <c>VillageJobs</c> row to (the <c>VillageJobTypes</c> row index).</summary>
    /// <param name="jobRow">The <c>VillageJobs</c> row the worker's name points at.</param>
    public static byte JobType(int jobRow)
    {
        var (_, types) = Tables.Value;
        return jobRow >= 0 && jobRow < types.Length ? types[jobRow] : (byte)0;
    }

    /// <summary>One worker's row.</summary>
    /// <param name="jobRow">The <c>VillageJobs</c> row the worker holds (1 is "Mining Crimson Iron").</param>
    /// <param name="worker">The worker's number in the town; it picks the two words of the name.</param>
    /// <param name="skill">The worker's skill level (a <c>VillageJobSkillLevels</c> row, 0 is the first).</param>
    public static byte[] Row(int jobRow, int worker, byte skill = 0)
    {
        var payload = Core(jobRow, worker, skill).ToList();
        payload.Add((byte)jobRow);                       // the VillageJobs row the name resolves through
        return payload.ToArray();
    }

    /// <summary>One recruit's row: the same row the workers have, but the job reference is the client's u32 (the recruit
    /// list's reader <c>0x141773300</c> reads the row and then a u32 into the element's <c>+0xa8</c>). That u32 is the
    /// price the card shows ("Cost: …") and the button weighs against the treasury's gold, so the server writes the
    /// hiring price there.</summary>
    /// <param name="jobRow">The <c>VillageJobs</c> row the recruit would take (1 is "Mining Crimson Iron").</param>
    /// <param name="worker">The recruit's number; it picks the two words of the name.</param>
    /// <param name="skill">The recruit's skill level (a <c>VillageJobSkillLevels</c> row, 0 is the first).</param>
    /// <param name="price">What hiring this recruit costs.</param>
    public static byte[] Recruit(int jobRow, int worker, byte skill = 0, uint price = 0)
    {
        var payload = Core(jobRow, worker, skill).ToList();
        payload.AddRange(BitConverter.GetBytes(price)[..4]);
        return payload.ToArray();
    }

    /// <summary>The row the client reads with <c>0x1424c7ca0</c>: two name keys, the wage, and the (job type, skill level)
    /// pairs. The job reference that follows it is read by whoever reads the row.</summary>
    static List<byte> Core(int jobRow, int worker, byte skill)
    {
        var (words, _) = Tables.Value;
        uint name1 = words.Length > 0 ? words[(worker * 2) % words.Length] : 0;
        uint name2 = words.Length > 1 ? words[(worker * 2 + 1) % words.Length] : 0;

        var payload = new List<byte>(13);
        payload.AddRange(BitConverter.GetBytes(name1)[..4]);
        payload.AddRange(BitConverter.GetBytes(name2)[..4]);
        // the wage the town pays this worker every hour; the panel shows it as it is and adds the rows up
        payload.AddRange(BitConverter.GetBytes(VillageWages.Wage(JobType(jobRow), skill))[..2]);
        payload.Add(1);                                  // one (job type, skill level) pair
        payload.Add(JobType(jobRow));
        payload.Add(skill);
        return payload;
    }
}
