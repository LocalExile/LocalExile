using System.Text.Json;
using GameServer.Core;

/// <summary>
/// Player character voice-line events from <c>CharacterAudioEvents</c> and <c>CharacterEventTextAudio</c> in
/// <c>client.db</c>.
/// <para>
/// In the 3.29.3c client there is no dedicated S→C voice-line message: server-driven events (261 of 291 rows) bind
/// their <c>Event</c> column to a <c>QuestFlags</c> row and fire automatically in <c>CharacterAudioController</c>
/// (<c>0x14010bd30</c>, vtable <c>0x143027020</c> +0x10) whenever an S→C <c>0x2bc</c> <c>PlayerQuestFlagsUpdate</c>
/// transitions that flag from 0 to 1 relative to the <c>0x2bb</c> creation baseline (<c>0x140108c80</c>).
/// <c>LevelUp&lt;N&gt;</c> rows (10 rows, null <c>Event</c>) fire automatically on <c>PlayerExperienceUpdate</c>
/// level-up (<c>0x140d5b2f0</c>), and the remaining rows with null <c>Event</c> (<c>NoMana</c>, <c>Cooldown</c>,
/// <c>InvFull</c>, <c>KillRare</c>, <c>KillRogueExile</c>, <c>SkillTown</c>, <c>QuestItem</c>, <c>LowSTR/INT/DEX</c>,
/// <c>Flask*</c>, <c>EventJack*</c>) are triggered locally by the client via <c>0x14010c960</c>.
/// </para>
/// </summary>
public sealed class CharacterAudioCatalog
{
    /// <summary>
    /// One row of <c>CharacterAudioEvents.dat</c> (0x52 bytes in 3.29.3c):
    /// +0x00 Id, +0x08 Event (<c>QuestFlags</c> row), +0x18 <paramref name="DelayMs"/> (<c>__col2</c>, queued at
    /// <c>0x14010ccbc</c>), +0x1c <paramref name="CooldownMs"/> (<c>__col3</c>, enforced at <c>0x14010cc2e</c> /
    /// <c>0x140145e6b</c>), +0x20 Goddess keys, +0x30 Jack the Axe keys, +0x40 <paramref name="SkipIfBusy"/>
    /// (<c>__col6</c>), +0x41 <paramref name="AllowInterrupt"/> (<c>__col7</c>).
    /// </summary>
    public sealed record Entry(
        int Row,
        string Id,
        int? QuestFlagRow,
        string? QuestFlagId,
        int DelayMs,
        int CooldownMs,
        bool SkipIfBusy,
        bool AllowInterrupt,
        bool HasCharacterLines,
        bool HasGoddessLines,
        bool HasJackLines)
    {
        /// <summary>Whether at least one class, Oni-Goroshi (Goddess), or Jack the Axe voice line exists for this event.</summary>
        public bool HasAudio => HasCharacterLines || HasGoddessLines || HasJackLines;

        /// <summary>Whether the 3.29.3c client triggers this event from a <c>QuestFlags</c> 0 → 1 transition.</summary>
        public bool IsServerDriven => QuestFlagRow is not null && QuestFlagId is not null;
    }

    readonly Dictionary<string, Entry> _byId;
    readonly Dictionary<string, Entry> _byQuestFlag;
    readonly Dictionary<int, Entry> _byQuestFlagRow;

    public IReadOnlyList<Entry> All { get; }
    public IReadOnlyList<Entry> ServerDriven { get; }

    CharacterAudioCatalog(IReadOnlyList<Entry> entries)
    {
        All = entries;
        ServerDriven = [.. entries.Where(e => e.IsServerDriven)];
        _byId = new(entries.Count, StringComparer.Ordinal);
        _byQuestFlag = new(ServerDriven.Count, StringComparer.Ordinal);
        _byQuestFlagRow = new(ServerDriven.Count);
        foreach (var e in entries)
        {
            if (e.Id.Length > 0) _byId.TryAdd(e.Id, e);
            if (e.QuestFlagId is { Length: > 0 } flagId) _byQuestFlag.TryAdd(flagId, e);
            if (e.QuestFlagRow is int flagRow) _byQuestFlagRow.TryAdd(flagRow, e);
        }
    }

    public static CharacterAudioCatalog Default { get; private set; } = default!;

    public static void LoadGameData() => Default = Load();

    public Entry? ById(string? id) => id is not null && _byId.TryGetValue(id, out var e) ? e : null;

    public Entry? ByQuestFlag(string? questFlagId) =>
        questFlagId is not null && _byQuestFlag.TryGetValue(questFlagId, out var e) ? e : null;

    public Entry? ByQuestFlagRow(int questFlagRow) =>
        _byQuestFlagRow.TryGetValue(questFlagRow, out var e) ? e : null;

    public static CharacterAudioCatalog Load()
    {
        if (!ClientTables.Has("CharacterAudioEvents") || !ClientTables.Has("QuestFlags"))
            return new([]);
        try
        {
            List<string> flags = [.. ClientTables.Table("QuestFlags").Select(r => r.Text("Id") ?? "")];
            var eventsWithCharacterLines = new HashSet<int>();
            if (ClientTables.Has("CharacterEventTextAudio"))
            {
                foreach (var r in ClientTables.Table("CharacterEventTextAudio"))
                    if (r.IntOrNull("Event") is int eventRow && r.Ints("TextAudio").Length > 0)
                        eventsWithCharacterLines.Add(eventRow);
            }

            var entries = new List<Entry>();
            foreach (var r in ClientTables.Table("CharacterAudioEvents"))
            {
                int row = r.Index;
                string id = r.Text("Id") ?? "";
                int? flagRow = r.IntOrNull("Event");
                string? flagId = flagRow is int i && i >= 0 && i < flags.Count && flags[i].Length > 0
                    ? flags[i] : null;
                int delayMs = r.IntOrNull("__col2") ?? 0;
                int cooldownMs = r.IntOrNull("__col3") ?? 0;
                bool hasGoddess = r.Ints("Goddess_CharacterTextAudioKeys").Length > 0;
                bool hasJack = r.Ints("JackTheAxe_CharacterTextAudioKeys").Length > 0;
                bool skipIfBusy = r.BoolOrNull("__col6") == true;
                bool allowInterrupt = r.BoolOrNull("__col7") == true;
                entries.Add(new Entry(
                    row, id, flagId is not null ? flagRow : null, flagId,
                    delayMs, cooldownMs, skipIfBusy, allowInterrupt,
                    eventsWithCharacterLines.Contains(row), hasGoddess, hasJack));
            }
            return new(entries);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] character audio: {ex.Message}");
            return new([]);
        }
    }
}
