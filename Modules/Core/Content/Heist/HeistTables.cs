using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Heist;

/// <summary>
/// Static Heist game-data catalog loaded from <c>data/client.db</c> at module load time (no lazy/deferred caches).
/// </summary>
public static class HeistTables
{
    public const string HeistCoinPath = "Metadata/Items/Heist/HeistCoin";
    public const string HeistHubId = "HeistHub";
    public const string HeistHubEndlessId = "HeistHubEndless";
    public const string HeistPluginId = "HeistPlugin";

    /// <summary>Player inventory rows from <c>Inventories.dat</c> (<see cref="InstanceInventories.PlayerDefaults"/>).</summary>
    public const byte ContractMissionRow = 44;
    public const byte FirstNpcEquipmentRow = 45;
    public const byte LastNpcEquipmentRow = 53;
    public const byte BlueprintMissionRow = 55;
    public const byte HeistLockerRow = 56;

    /// <summary>League quest flag id for visiting Rogue Harbour (<c>HeistVisitedHub</c>).</summary>
    public const int HeistVisitedHubFlag = 736;

    public sealed record RogueInfo(
        byte Row,
        int NpcRow,
        string Name,
        string AoFile,
        byte[] Jobs,
        byte[] MaxJobLevels,
        int? UnlockLeagueFlag,
        byte EquipmentInventoryRow);

    public sealed record RevealerInfo(byte Row, int NpcRow, int DiscountPct);
    public sealed record EquipmentRequirement(int BaseItemTypeRow, int? RequiredJob, int RequiredLevel);

    public static RogueInfo[] Rogues { get; private set; } = [];
    public static RevealerInfo[] Revealers { get; private set; } = [];
    public static Dictionary<(byte Job, byte Level), uint> XpThresholds { get; private set; } = [];
    public static Dictionary<int, EquipmentRequirement> EquipmentByBaseRow { get; private set; } = [];
    public static HashSet<string> MissionAreaIds { get; private set; } = new(StringComparer.Ordinal);
    public static HashSet<ushort> MissionAreaHashes { get; private set; } = [];
    public static byte EscapeRoomRow { get; private set; } = 0xff;
    public static int[] RogueUnlockFlags { get; private set; } = [];
    public static Dictionary<int, byte> RevealerByNpcRow { get; private set; } = [];

    public static bool IsRogueHarbour(string areaId) =>
        areaId is HeistHubId or HeistHubEndlessId;

    public static bool IsMissionArea(string areaId, ushort hash) =>
        MissionAreaIds.Contains(areaId) || MissionAreaHashes.Contains(hash);

    public static void LoadGameData()
    {
        var rogues = new List<RogueInfo>(InstanceHeist.RogueCount);
        var unlockFlags = new List<int>();
        foreach (var r in ClientTables.TableOrEmpty("HeistNPCs"))
        {
            if (r.Index >= InstanceHeist.RogueCount) break;
            var jobs = r.Ints("SkillLevel_HeistJobsKeys").Select(j => (byte)Math.Clamp(j, 0, 255)).ToArray();
            var maxLevels = r.Ints("SkillLevel_Values").Select(v => (byte)Math.Clamp(v, 1, 5)).ToArray();
            int rawFlag = r.IntOrNull("__col10") ?? Quests.League.Count + 1;
            int? unlock = rawFlag is > 0 and <= Quests.League.Count ? rawFlag : null;
            if (unlock is int f) unlockFlags.Add(f);
            byte invRow = (byte)(FirstNpcEquipmentRow + r.Index);
            rogues.Add(new(
                (byte)r.Index,
                r.IntOrNull("NPCsKey") ?? -1,
                r.Text("Name") ?? $"Rogue {r.Index}",
                r.Text("AOFile") ?? "",
                jobs,
                maxLevels,
                unlock,
                invRow));
        }
        Rogues = [.. rogues];
        RogueUnlockFlags = [.. unlockFlags.Distinct()];

        var revealers = new List<RevealerInfo>();
        var revByNpc = new Dictionary<int, byte>();
        foreach (var r in ClientTables.TableOrEmpty("HeistRevealingNPCs"))
        {
            int npcRow = r.IntOrNull("NPCsKey") ?? -1;
            int discount = r.Index switch { 1 => 40, 2 => 10, _ => 0 };
            revealers.Add(new((byte)r.Index, npcRow, discount));
            if (npcRow >= 0) revByNpc[npcRow] = (byte)r.Index;
        }
        Revealers = [.. revealers];
        RevealerByNpcRow = revByNpc;

        var xp = new Dictionary<(byte, byte), uint>();
        foreach (var r in ClientTables.TableOrEmpty("HeistJobsExperiencePerLevel"))
        {
            int job = r.IntOrNull("HeistJobsKey") ?? -1;
            int tier = r.IntOrNull("Tier") ?? 0;
            int exp = r.IntOrNull("Experience") ?? 0;
            if (job is >= 0 and <= 255 && tier is >= 1 and <= 5)
                xp[((byte)job, (byte)tier)] = (uint)Math.Max(0, exp);
        }
        XpThresholds = xp;

        var equip = new Dictionary<int, EquipmentRequirement>();
        foreach (var r in ClientTables.TableOrEmpty("HeistEquipment"))
        {
            int baseRow = r.IntOrNull("BaseItemTypesKey") ?? -1;
            if (baseRow < 0) continue;
            equip[baseRow] = new(baseRow, r.IntOrNull("RequiredJob_HeistJobsKey"), r.IntOrNull("RequiredLevel") ?? 0);
        }
        EquipmentByBaseRow = equip;

        byte escape = 0xff;
        foreach (var r in ClientTables.TableOrEmpty("HeistChestRewardTypes"))
            if (r.Text("Id") == "EscapeRoom" && r.Index <= byte.MaxValue) { escape = (byte)r.Index; break; }
        EscapeRoomRow = escape;

        var areaIds = new HashSet<string>(StringComparer.Ordinal);
        var areaHashes = new HashSet<ushort>();
        var worldAreas = ClientTables.TableOrEmpty("WorldAreas");
        foreach (var ha in ClientTables.TableOrEmpty("HeistAreas"))
            foreach (int waRow in ha.Ints("WorldAreasKeys"))
                if (waRow >= 0 && waRow < worldAreas.Count)
                {
                    var wa = worldAreas[waRow];
                    if (wa.Text("Id") is { Length: > 0 } id) areaIds.Add(id);
                    areaHashes.Add((ushort)wa.Int("HASH16"));
                }
        MissionAreaIds = areaIds;
        MissionAreaHashes = areaHashes;
    }

    public static byte JobLevelFromXp(byte jobRow, uint xp, byte maxLevel)
    {
        byte level = 1;
        for (byte tier = 2; tier <= maxLevel; tier++)
            if (XpThresholds.TryGetValue((jobRow, tier), out uint needed) && xp >= needed)
                level = tier;
        return level;
    }

    public static bool TryFindRogueJobSlot(byte rogueRow, byte jobRow, out int slotIndex, out byte maxLevel)
    {
        slotIndex = -1;
        maxLevel = 0;
        if (rogueRow >= Rogues.Length) return false;
        var rogue = Rogues[rogueRow];
        for (int i = 0; i < rogue.Jobs.Length && i < InstanceHeist.JobSlotsPerRogue; i++)
            if (rogue.Jobs[i] == jobRow)
            {
                slotIndex = i;
                maxLevel = i < rogue.MaxJobLevels.Length ? rogue.MaxJobLevels[i] : (byte)5;
                return true;
            }
        return false;
    }
}
