using System.Text.Json;
using GameServer.Modding;

/// <summary>
/// Unlocks the content a character only reaches after the campaign: the dialogue options and league rows whose
/// <c>NPCTalk</c> row requires quest flags (the PostGods/epilogue rows, the league NPCs' later options, the vendor
/// panels that appear once their quest is done).
///
/// The flag set is read from the tables, not listed here: every row of <c>data/tables/NPCTalk.json</c> requires the
/// flags of its <c>QuestFlag1</c> column (row indices into <c>data/tables/QuestFlags.json</c>) and is hidden by the
/// ones in <c>QuestFlag2</c>. A flag that some row requires and another hides is left out by default — granting it
/// would hide that second row — so the default set is "every gate the tables can express without closing another
/// door". <c>/unlock all</c> grants those too, plus <see cref="Quests.EndGameAvailable"/> earned (the epilogue, the
/// Pantheon, and Kitava's Act 10 resistance penalty with it), and <c>data/unlock-all.txt</c> makes the default set
/// automatic for every character that logs in.
///
/// Flags are set through the normal quest-flag path (<c>SetQuestFlags</c>), so they are saved with the character,
/// published to the client at once, and an NPC's options refresh the next time it is talked to.
/// </summary>
static partial class Game
{
    const string UnlockSwitch = "data/unlock-all.txt";
    static readonly object UnlockLock = new();
    static List<string>? UnlockSafe, UnlockEvery;
    // One application per connection: the flags are saved, so a second pass would only re-send the same set.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> Unlocked = new();

    /// <summary>The flags the character is missing to see a locked row: every NPCTalk requirement, but none that some
    /// other row hides (<c>QuestFlag2</c>).</summary>
    static List<string> UnlockFlags(bool everything)
    {
        lock (UnlockLock)
        {
            if (everything ? UnlockEvery is { } every : UnlockSafe is { } safe) return everything ? UnlockEvery! : UnlockSafe!;
            string folder = Path.Combine("data", "tables");
            List<string> names;
            using (var doc = GameTables.Open(Path.Combine(folder, "QuestFlags.json")))
                names = [.. doc.RootElement.EnumerateArray().Select(f => f.GetProperty("Id").GetString() ?? "")];
            var required = new HashSet<string>(StringComparer.Ordinal);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            using (var doc = GameTables.Open(Path.Combine(folder, "NPCTalk.json")))
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    foreach (string column in new[] { "QuestFlag1", "QuestFlag2" })
                    {
                        if (!row.TryGetProperty(column, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                        var target = column == "QuestFlag1" ? required : hidden;
                        foreach (var index in list.EnumerateArray())
                            if (index.ValueKind == JsonValueKind.Number && index.GetInt32() is int i && i >= 0 && i < names.Count
                                && names[i] != "NEVER_SET")
                                target.Add(names[i]);
                    }
                }
            UnlockSafe = [.. required.Except(hidden).Order(StringComparer.Ordinal)];
            UnlockEvery = [.. required.Union(hidden).Union(new[] { Quests.EndGameAvailable }).Order(StringComparer.Ordinal)];
            Console.WriteLine($"[gateway] unlock: {required.Count} flag(s) gate an NPCTalk row, {hidden.Count} hide one, "
                + $"{UnlockSafe.Count} are granted by default and {UnlockEvery.Count} by /unlock all");
            return everything ? UnlockEvery : UnlockSafe;
        }
    }

    /// <summary>Whether <c>data/unlock-all.txt</c> asks for the default set on every login (its content "all" for the
    /// full set, including the epilogue flag).</summary>
    static bool UnlockAutomatic(out bool everything)
    {
        everything = false;
        if (!File.Exists(UnlockSwitch)) return false;
        try
        {
            everything = File.ReadAllText(UnlockSwitch).Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // A half-written file is not a reason to skip the unlock: the default set is the safe one.
        }
        return true;
    }

    /// <summary>Grants the unlocked flags to the character (saved, and sent to the client at once).</summary>
    internal static async Task<int> Unlock(IPlayer player, bool everything)
    {
        var conn = Program.ConnOf(player);
        var (remote, stream, ciphers) = conn.Wire;
        List<string> flags = UnlockFlags(everything);
        int before = conn.QuestFlagsSnapshot().Count;
        await SetQuestFlags(remote, stream, ciphers, conn, flags);
        int after = conn.QuestFlagsSnapshot().Count;
        Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: unlock ({(everything ? "all" : "default")}) "
            + $"{flags.Count} flag(s), {after - before} newly set, {after} total");
        return after - before;
    }

    /// <summary>Dev tick: applies <c>data/unlock-all.txt</c> once per connection.</summary>
    internal static async Task UnlockTick(IPlayer player)
    {
        if (!UnlockAutomatic(out bool everything)) return;
        object conn = Program.ConnOf(player);
        if (Unlocked.TryGetValue(conn, out _)) return;
        Unlocked.Add(conn, conn);
        int set = await Unlock(player, everything);
        if (set > 0) await player.Tell($"Unlocked {set} post-campaign quest flag(s) ({(everything ? "all" : "default")}).");
    }
}
