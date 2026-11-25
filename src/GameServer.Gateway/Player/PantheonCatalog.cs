using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The Pantheon's gods (Agents/docs/game/campaign/pantheon.md): PantheonPanelLayout rows (one per panel slot: major or
/// minor, the quest flag that unlocks the god, the god's power and three upgrades as stats) and PantheonSouls (the
/// upgrades' quest flags: a captured soul handed to Sin sets <c>&lt;God&gt;Upgrade&lt;N&gt;</c>). Rows 4, 5, 7 and 12 are disabled
/// placeholders (QuestFlag NEVER_SET). The data the Pantheon module (Modules/Core/Player/Pantheon) chooses and applies the
/// gods by; the choice itself is the character's (Player and the save: PantheonMajor, PantheonMinor).
/// </summary>
public sealed class PantheonCatalog
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        Default = Load();
    }

    /// <summary>The empty Divine Vessel BaseItemTypes Ids accepted alongside a map in the Map Device.</summary>
    public const string DivineVesselId = "Metadata/Items/MapFragments/FragmentPantheonFlask";
    public const string CurrencyDivineVesselId = "Metadata/Items/MapFragments/CurrencyFragmentPantheonFlask";

    /// <summary>Whether a BaseItemTypes Id is an empty Divine Vessel.</summary>
    public static bool IsDivineVessel(string? baseId) =>
        string.Equals(baseId, DivineVesselId, StringComparison.Ordinal)
        || string.Equals(baseId, CurrencyDivineVesselId, StringComparison.Ordinal);

    /// <summary>One PantheonSouls row: the Atlas map whose boss fills a Divine Vessel into <see cref="VesselItemId"/>,
    /// which Sin exchanges for <see cref="UpgradeFlag"/> on <see cref="PanelRow"/>.</summary>
    public sealed record Soul(int Row, string WorldAreaId, ushort WorldAreaHash16, string VesselItemId,
        int VesselItemRow, int PanelRow, string UpgradeFlag, string Description);

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
    public IReadOnlyList<Soul> Souls { get; }

    PantheonCatalog(IReadOnlyList<God> gods, IReadOnlyList<Soul>? souls = null)
    {
        Gods = gods;
        Souls = souls ?? [];
    }

    public static PantheonCatalog Default = default!;

    public God? ByWire(byte wire) => wire >= 1 && wire <= Gods.Count ? Gods[wire - 1] : null;
    public God? ById(string? id) => id is null ? null : Gods.FirstOrDefault(g => g.Id == id);

    /// <summary>The capturable Pantheon soul bound to an Atlas map's WorldAreas Id, or null when that map has none.</summary>
    public Soul? SoulForArea(string? worldAreaId) => string.IsNullOrEmpty(worldAreaId)
        ? null : Souls.FirstOrDefault(s => string.Equals(s.WorldAreaId, worldAreaId, StringComparison.Ordinal));

    /// <summary>The Pantheon soul matching a filled CapturedVessel BaseItemTypes Id.</summary>
    public Soul? SoulByVessel(string? vesselId) => string.IsNullOrEmpty(vesselId)
        ? null : Souls.FirstOrDefault(s => string.Equals(s.VesselItemId, vesselId, StringComparison.Ordinal));

    /// <summary>The Pantheon soul unlocked by a QuestFlags Id (<c>BrineKingUpgrade1</c> …).</summary>
    public Soul? SoulByUpgradeFlag(string? flag) => string.IsNullOrEmpty(flag)
        ? null : Souls.FirstOrDefault(s => string.Equals(s.UpgradeFlag, flag, StringComparison.Ordinal));

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
    /// (<see cref="Player.EarnedQuestFlag"/>, as Kitava's Act 10 resistance penalty).</summary>
    public List<(uint Key, int Value)> ChosenStats(Player player, Func<string, bool> has)
    {
        bool endgame = player.Character.EarnedQuestFlag(Quests.EndGameAvailable);
        string? major, minor;
        (major, minor) = (player.Character.PantheonMajor, player.Character.PantheonMinor);
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
            var souls = new List<Soul>();
            var areas = ClientTables.Has("WorldAreas") ? ClientTables.Table("WorldAreas") : null;
            var baseItems = ClientTables.Has("BaseItemTypes") ? ClientTables.Table("BaseItemTypes") : null;
            if (ClientTables.Has("PantheonSouls"))
            {
                int soulRow = 0;
                foreach (var s in ClientTables.Table("PantheonSouls"))
                {
                    int currentRow = soulRow++;
                    string? upgradeFlag = Flag(s.IntOrNull("QuestFlagUpgrade"));
                    if (s.IntOrNull("PanelLayout") is int p)
                    {
                        if (!upgradeFlags.TryGetValue(p, out var list)) upgradeFlags[p] = list = [];
                        list.Add(upgradeFlag);
                        if (areas is not null && baseItems is not null
                            && s.IntOrNull("WorldArea") is int waRow && waRow >= 0 && waRow < areas.Count
                            && s.IntOrNull("CapturedVessel") is int itemRow && itemRow >= 0 && itemRow < baseItems.Count
                            && upgradeFlag is not null)
                        {
                            var wa = areas[waRow];
                            string vesselId = baseItems[itemRow].Text("Id") ?? "";
                            if (vesselId.Length > 0)
                                souls.Add(new Soul(currentRow, wa.Text("Id") ?? "", (ushort)(wa.IntOrNull("HASH16") ?? 0),
                                    vesselId, itemRow, p, upgradeFlag, s.TextOrNull("CapturedMonsterDescription") ?? ""));
                        }
                    }
                }
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
            return new(gods, souls);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] pantheon: {ex.Message}");
            return new([]);
        }
    }
}
