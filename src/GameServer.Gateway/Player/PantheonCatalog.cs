using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The Pantheon's gods (Agents/docs/game/campaign/pantheon.md): PantheonPanelLayout rows (one per panel slot: major or
/// minor, the quest flag that unlocks the god, the god's power and three upgrades as stats) and PantheonSouls (the
/// upgrades' quest flags: a captured soul handed to Sin sets <c>&lt;God&gt;Upgrade&lt;N&gt;</c>). Rows 4, 5, 7 and 12 are disabled
/// placeholders (QuestFlag NEVER_SET). The data the Pantheon module (Modules/Core/Player/Pantheon) chooses and applies the
/// gods by; the choice itself is the character's (ClientState and the save: PantheonMajor, PantheonMinor).
/// </summary>
public sealed class PantheonCatalog
{
    /// <summary>A panel row: its Id (TheBrineKing …), whether it is a major god, the flag that unlocks it, the god's name
    /// (GodName1), its power (Effect1: Stats keys = row + 1, values) and its upgrades (Effect2–4, each with the PantheonSouls
    /// flag that unlocks it, in the souls' order).</summary>
    public sealed record God(int Row, string Id, string Name, bool Major, string? UnlockFlag, bool Disabled,
        IReadOnlyList<(uint Key, int Value)> Power, IReadOnlyList<(string? Flag, IReadOnlyList<(uint Key, int Value)> Stats)> Upgrades)
    {
        /// <summary>What C→S 0x1ee and the Player bytes carry: the row + 1.</summary>
        public byte Wire => (byte)(Row + 1);
    }

    public IReadOnlyList<God> Gods { get; }

    PantheonCatalog(IReadOnlyList<God> gods) => Gods = gods;

    public static readonly Lazy<PantheonCatalog> Default = new(() => Load());

    public God? ByWire(byte wire) => wire >= 1 && wire <= Gods.Count ? Gods[wire - 1] : null;
    public God? ById(string? id) => id is null ? null : Gods.FirstOrDefault(g => g.Id == id);

    /// <summary>What the Player component's byte says for a chosen god (its Id; null = none): the row + 1, 0 for none or a
    /// row the table no longer has.</summary>
    public byte Wire(string? id) => ById(id)?.Wire ?? 0;

    /// <summary>Whether a character with these flags may choose the god: an enabled row whose unlock flag it has.</summary>
    public static bool Unlocked(God god, Func<string, bool> has) => !god.Disabled && god.UnlockFlag is { } flag && has(flag);

    /// <summary>The stats a chosen god grants: its power, and with <paramref name="endgame"/> each upgrade whose soul flag
    /// is set (the client applies upgrades only after An End to Hunger: ClientStrings PantheonUpgradeUnavailableTillEndgame,
    /// so only once EndGameAvailable is earned).</summary>
    public static IEnumerable<(uint Key, int Value)> Stats(God god, Func<string, bool> has, bool endgame) =>
        god.Power.Concat(endgame ? god.Upgrades.Where(u => u.Flag is { } f && has(f)).SelectMany(u => u.Stats) : []);

    /// <summary>What the character's chosen gods grant (the Pantheon module's stat sources): each chosen god that is
    /// unlocked (<paramref name="has"/>: the flags the client has), its upgrades only once the character earned
    /// EndGameAvailable. Every character is given that flag at login for the Atlas (Quests.EveryCharacter; until earned
    /// only in a hideout and the maps, Quests.EndgameOnly): it counts once a quest set it
    /// (<see cref="ClientState.EarnedQuestFlag"/>, as Kitava's Act 10 resistance penalty).</summary>
    public List<(uint Key, int Value)> ChosenStats(ClientState conn, Func<string, bool> has)
    {
        bool endgame = conn.EarnedQuestFlag(Quests.EndGameAvailable);
        string? major, minor;
        lock (conn.Monsters) (major, minor) = (conn.PantheonMajor, conn.PantheonMinor);
        return [.. new[] { ById(major), ById(minor) }.OfType<God>().Where(g => Unlocked(g, has)).SelectMany(g => Stats(g, has, endgame))];
    }

    public static PantheonCatalog Load(string folder = "data")
    {
        if (!ClientTables.Has("PantheonPanelLayout") || !ClientTables.Has("QuestFlags")) return new([]);
        try
        {
            List<string> flags;
            flags = [.. ClientTables.Table("QuestFlags").Select(f => f.Text("Id") ?? "")];
            string? Flag(int? v) => v is int i && i >= 0 && i < flags.Count
                && flags[i] != "NEVER_SET" ? flags[i] : null;
            var upgradeFlags = new Dictionary<int, List<string?>>();
            if (ClientTables.Has("PantheonSouls"))
                foreach (var s in ClientTables.Table("PantheonSouls"))
                    if (s.IntOrNull("PanelLayout") is int p)
                    {
                        if (!upgradeFlags.TryGetValue(p, out var list)) upgradeFlags[p] = list = [];
                        list.Add(Flag(s.IntOrNull("QuestFlagUpgrade")));
                    }
            var gods = new List<God>();
            foreach (var r in ClientTables.Table("PantheonPanelLayout"))
            {
                IReadOnlyList<(uint, int)> Effect(int n) =>
                    [.. r.Ints($"Effect{n}_StatsKeys").Zip(r.Ints($"Effect{n}_Values"), (k, v) => ((uint)k + 1, v))];
                int row = gods.Count;
                var upgrades = Enumerable.Range(2, 3).Select((n, i) => (upgradeFlags.GetValueOrDefault(row)?.ElementAtOrDefault(i), Effect(n)))
                    .Where(u => u.Item2.Count > 0).ToList();
                gods.Add(new God(row, r.Text("Id") ?? "", r.TextOrNull("GodName1") ?? "",
                    r.BoolOrNull("IsMajorGod") == true, Flag(r.IntOrNull("QuestFlag")),
                    r.BoolOrNull("IsDisabled") == true, Effect(1), upgrades));
            }
            return new(gods);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] pantheon: {ex.Message}");
            return new([]);
        }
    }
}
