using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Heist;

/// <summary>
/// Account-scoped Heist rogue progression, Ally Equipment job bonuses, rogue unlock flags, and S→C <c>0x027c</c> state sync.
/// Persisted atomically in <see cref="AccountStash.ModuleData"/> under <c>Heist.Progress</c>.
/// </summary>
public static class HeistProgress
{
    const string ModuleDataKey = "Heist.Progress";
    const byte DefaultWhakanoReveals = 3;
    const byte DefaultGiannaReveals = 1;
    const byte DefaultNilesReveals = 1;

    static readonly JsonSerializerOptions Json = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public sealed class ProgressData
    {
        public uint[] JobXp { get; set; } = new uint[InstanceHeist.RogueCount * InstanceHeist.JobSlotsPerRogue];
        public byte WhakanoReveals { get; set; } = DefaultWhakanoReveals;
        public byte GiannaReveals { get; set; } = DefaultGiannaReveals;
        public byte NilesReveals { get; set; } = DefaultNilesReveals;
        public int CompletedContracts { get; set; }

        public void Normalize()
        {
            const int expected = InstanceHeist.RogueCount * InstanceHeist.JobSlotsPerRogue;
            if (JobXp.Length != expected)
            {
                var next = new uint[expected];
                Array.Copy(JobXp, next, Math.Min(JobXp.Length, expected));
                JobXp = next;
            }
        }
    }

    public static ProgressData Load(Player player)
    {
        var stash = PersonalStashOf(player);
        var raw = stash.ModuleData(ModuleDataKey);
        if (string.IsNullOrEmpty(raw)) return new ProgressData();
        try
        {
            var data = JsonSerializer.Deserialize<ProgressData>(raw, Json) ?? new ProgressData();
            data.Normalize();
            return data;
        }
        catch (JsonException)
        {
            return new ProgressData();
        }
    }

    public static ProgressData Update(Player player, Action<ProgressData> mutate)
    {
        ProgressData result = new();
        PersonalStashOf(player).ChangeModuleData(ModuleDataKey, raw =>
        {
            ProgressData data;
            if (string.IsNullOrEmpty(raw)) data = new ProgressData();
            else
            {
                try { data = JsonSerializer.Deserialize<ProgressData>(raw, Json) ?? new ProgressData(); }
                catch (JsonException) { data = new ProgressData(); }
            }
            data.Normalize();
            mutate(data);
            data.Normalize();
            result = data;
            return JsonSerializer.Serialize(data, Json);
        });
        return result;
    }

    /// <summary>Ensures Rogue Harbour unlock league quest flags (<c>736</c>, <c>739..744</c>) are set on the account.</summary>
    public static async Task EnsureRogueUnlocks(Player player)
    {
        var missing = new List<int>();
        if (!player.Character.LeagueQuestFlags.Contains(HeistTables.HeistVisitedHubFlag))
            missing.Add(HeistTables.HeistVisitedHubFlag);
        foreach (int flag in HeistTables.RogueUnlockFlags)
            if (!player.Character.LeagueQuestFlags.Contains(flag))
                missing.Add(flag);
        if (missing.Count > 0)
            await SetLeagueQuestFlags(player, missing);
    }

    /// <summary>Computes a rogue's effective level for <paramref name="jobRow"/>: base level from XP + Ally Equipment job bonuses.</summary>
    public static int EffectiveJobLevel(Player player, ProgressData progress, byte rogueRow, byte jobRow)
    {
        if (!HeistTables.TryFindRogueJobSlot(rogueRow, jobRow, out int slot, out byte maxLevel))
            return 0;
        uint xp = progress.JobXp[rogueRow * InstanceHeist.JobSlotsPerRogue + slot];
        int level = HeistTables.JobLevelFromXp(jobRow, xp, maxLevel);
        level += EquippedJobLevelBonus(player, rogueRow, jobRow);
        return Math.Clamp(level, 1, 6);
    }

    /// <summary>Inspects the rogue's Ally Equipment inventory (<c>HeistNpcEquipment1..9</c>, rows 45..53) for +job-level mods.</summary>
    public static int EquippedJobLevelBonus(Player player, byte rogueRow, byte jobRow)
    {
        if (rogueRow >= InstanceHeist.RogueCount) return 0;
        byte invRow = (byte)(HeistTables.FirstNpcEquipmentRow + rogueRow);
        var items = ItemsOf(player).Items(invRow);
        if (items.Count == 0) return 0;
        var mods = ItemsFor(player).Mods;
        int bonus = 0;
        foreach (var entry in items)
            foreach (var mod in entry.Item.Mods)
            {
                if (mods.ByHash(mod.Hash) is not { } def) continue;
                for (int i = 0; i < def.Stats.Count && i < mod.Values.Count; i++)
                {
                    string statName = Stats.Name(def.Stats[i].Id);
                    if (statName.Contains("heist_job_level", StringComparison.OrdinalIgnoreCase)
                        || statName.Contains("heist_npc_job_level", StringComparison.OrdinalIgnoreCase))
                        bonus += Math.Max(0, mod.Values[i]);
                }
            }
        return Math.Min(bonus, 2);
    }

    /// <summary>Whether the character currently has a runnable contract in <c>HeistContractMission</c> or MainInventory.</summary>
    public static bool HasRunnableContract(Player player)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        bool IsContract(InventoryItem entry) =>
            catalog.ByHash(entry.Item.TypeHash) is { Class: "HeistContract" };
        return items.Items(HeistTables.ContractMissionRow).Any(IsContract)
            || items.Items(PlayerInventories.MainInventory).Any(IsContract);
    }

    public static byte[] BuildStatePacket(Player player, ProgressData? progress = null)
    {
        var data = progress ?? Load(player);
        data.Normalize();
        return InstanceHeist.HeistState(
            HasRunnableContract(player),
            data.JobXp,
            data.WhakanoReveals,
            data.GiannaReveals,
            data.NilesReveals);
    }

    public static Task SendState(Player player, ProgressData? progress = null, string reason = "sync") =>
        PlayerOf(player).Send(BuildStatePacket(player, progress), $"0x027c Heist state ({reason})");

    public static byte RevealsFor(ProgressData progress, byte revealerRow) => revealerRow switch
    {
        0 => progress.WhakanoReveals,
        1 => progress.GiannaReveals,
        2 => progress.NilesReveals,
        _ => 0,
    };

    public static void DecrementReveal(ProgressData progress, byte revealerRow)
    {
        switch (revealerRow)
        {
            case 0: if (progress.WhakanoReveals > 0) progress.WhakanoReveals--; break;
            case 1: if (progress.GiannaReveals > 0) progress.GiannaReveals--; break;
            case 2: if (progress.NilesReveals > 0) progress.NilesReveals--; break;
        }
    }
}
