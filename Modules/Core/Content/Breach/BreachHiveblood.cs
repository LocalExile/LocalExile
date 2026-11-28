using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// Per-account/league Hiveblood (Graftblood) balance, client HUD sync (<c>S→C 0x02d2</c>), storage capacity
/// (3.29.3c client UI gauge max = <c>100,000</c>), <c>BrequelItemResourceValues</c> level cost table, and cumulative
/// Genesis Tree spend milestones (<c>BrequelFruitQuests</c>, 300..70300 → LQF 1594..1600).
/// Persisted atomically in <see cref="IPlayerWorld.AccountData"/> so all characters in a league share Hiveblood as in
/// PoE 3.29.
/// </summary>
public static class BreachHiveblood
{
    const string DataKey = "breach.hiveblood";

    /// <summary>S→C 0x02d2: syncs current Hiveblood to the client HUD, Genesis Tree gauge, and Wombgift tooltips
    /// (<c>u16 BE 0x02d2</c> + <c>CompactUInt currentHiveblood</c>).</summary>
    public const ushort HivebloodPacketId = 0x02d2;

    /// <summary>3.29.3c client UI gauge cap (<c>fill = current * 1e-5f</c> at <c>0x1403bf997</c> &amp;
    /// <c>0x1403ce4c3</c>).</summary>
    public const int MaxHiveblood = 100_000;

    /// <summary>One row of <c>BrequelFruitQuests</c> (1..7): feeding cumulative Hiveblood into the Genesis Tree sets
    /// <see cref="ReachedLeagueFlag"/> (<c>ChayulaTreeJuiced1..7</c> = 1594..1600), unlocking Ailith's turn-in dialogue
    /// for <see cref="TurnedInLeagueFlag"/> (<c>ChayulaTreeJuicedTurnedIn1..7</c> = 1587..1593).</summary>
    public readonly record struct FruitQuestMilestone(
        int Id,
        int PrerequisiteLeagueFlag,
        int ReachedLeagueFlag,
        int TurnedInLeagueFlag,
        int StepRequired,
        int CumulativeRequired,
        int PreviousCumulative);

    sealed class SavedState
    {
        public int Balance { get; set; }
        public int LifetimeSpent { get; set; }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    static Dictionary<int, int> StorageByLevel = [];
    static FruitQuestMilestone[] Milestones = [];

    /// <summary>All loaded <c>BrequelFruitQuests</c> milestones (ordered by Id 1..7).</summary>
    public static IReadOnlyList<FruitQuestMilestone> FruitQuestMilestones => Milestones;

    /// <summary>Loads <c>BrequelItemResourceValues</c> (keyed by <c>FleshgraftLevel == 1..100</c>) and
    /// <c>BrequelFruitQuests</c> from <c>client.db</c>.</summary>
    public static void LoadGameData()
    {
        var storage = new Dictionary<int, int>();
        foreach (var row in ClientTables.TableOrEmpty("BrequelItemResourceValues"))
        {
            if (row.IntOrNull("FleshgraftLevel") is int lvl && row.IntOrNull("GraftbloodStorage") is int cap)
                storage[lvl] = cap;
        }
        StorageByLevel = storage;

        var milestones = new List<FruitQuestMilestone>();
        foreach (var row in ClientTables.TableOrEmpty("BrequelFruitQuests"))
        {
            int id = row.IntOrNull("Id") ?? (row.Index + 1);
            int prereq = row.IntOrNull("__col1") ?? (BreachHub.LqfTutorialCompleted + id - 1);
            int reached = row.IntOrNull("__col2") ?? (BreachHub.LqfTreeJuicedFirst + id - 1);
            int step = row.IntOrNull("TreeGraftbloodRequired") ?? 0;
            int cumulative = row.IntOrNull("__col4") ?? step;
            int prevCumulative = row.IntOrNull("__col5") ?? Math.Max(0, cumulative - step);
            int turnedIn = BreachHub.LqfTreeJuicedTurnedInFirst + id - 1;
            milestones.Add(new(id, prereq, reached, turnedIn, step, cumulative, prevCumulative));
        }
        milestones.Sort((a, b) => a.Id.CompareTo(b.Id));
        Milestones = [.. milestones];
    }

    /// <summary>Builds the binary-verified <c>S→C 0x02d2</c> Hiveblood counter packet.</summary>
    public static byte[] BuildHivebloodPacket(int currentHiveblood)
    {
        var w = new LoginWriter().U16(HivebloodPacketId);
        InstanceProtocol.CompactUInt(w, (uint)Math.Max(0, currentHiveblood));
        return w.ToArray();
    }

    /// <summary>Enter-area message pair for <c>GameEvents.EnterMessages</c>.</summary>
    public static (string Log, byte[] Message) EnterMessage(Player player)
    {
        int current = Balance(player);
        return ($"0x02d2 Hiveblood sync ({current}/{Capacity(player)})", BuildHivebloodPacket(current));
    }

    static SavedState Load(Player player)
    {
        string? raw = WorldOf(player).AccountData.Get(DataKey);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<SavedState>(raw, JsonOptions) ?? new(); }
        catch (JsonException) { return new(); }
    }

    static bool Mutate(Player player, Func<SavedState, bool> update, out SavedState result)
    {
        SavedState finalState = new();
        bool changed = WorldOf(player).AccountData.Change(DataKey, raw =>
        {
            SavedState state;
            if (string.IsNullOrWhiteSpace(raw)) state = new();
            else
            {
                try { state = JsonSerializer.Deserialize<SavedState>(raw, JsonOptions) ?? new(); }
                catch (JsonException) { state = new(); }
            }
            if (!update(state)) return null;
            finalState = state;
            return JsonSerializer.Serialize(state, JsonOptions);
        });
        result = changed ? finalState : Load(player);
        return changed;
    }

    /// <summary>Current Hiveblood stored for this account/league.</summary>
    public static int Balance(Player player) => Math.Max(0, Load(player).Balance);

    /// <summary>Total cumulative Hiveblood fed into the Genesis Tree for this account/league.</summary>
    public static int LifetimeSpent(Player player) => Math.Max(0, Load(player).LifetimeSpent);

    /// <summary><c>BrequelItemResourceValues.GraftbloodStorage</c> keyed by <c>FleshgraftLevel</c> (1..100, NOT
    /// 0-based row index), used by the client's exact Wombgift cost formula (<c>0x1424ad630</c>).</summary>
    public static int ResourceValueForLevel(int level)
    {
        int clamped = Math.Clamp(level, 1, 100);
        if (StorageByLevel.TryGetValue(clamped, out int value)) return value;
        return clamped < 68 ? Math.Max(8, clamped * 2) : 210 + (clamped - 68) * 80;
    }

    /// <summary>Maximum Hiveblood this player can hold (3.29.3c client hardcodes <c>100,000</c>).</summary>
    public static int Capacity(Player player)
    {
        long cap = MaxHiveblood;
        int incPct = AreaLeagueStats(player).GetValueOrDefault(Stat.LocalMaximumBrequelResourcePlusPct);
        if (incPct != 0) cap = cap * Math.Max(10, 100 + incPct) / 100;
        return (int)Math.Clamp(cap, 1_000, MaxHiveblood);
    }

    /// <summary>Awards Hiveblood from a Breach encounter, scaling by <c>brequel_resource_gained_+%</c>, clamping to
    /// <see cref="Capacity"/>, persisting to <c>AccountData</c>, and syncing <c>S→C 0x02d2</c>. Returns the actual
    /// amount added.</summary>
    public static async Task<int> AwardAsync(Player player, int rawAmount, string? reason = null)
    {
        if (rawAmount <= 0) return 0;
        int incPct = AreaLeagueStats(player).GetValueOrDefault(Stat.BrequelResourceGainedPlusPct);
        int scaled = Math.Max(1, (int)Math.Round(rawAmount * Math.Max(0.1, (100 + incPct) / 100.0)));
        int cap = Capacity(player);
        int added = 0;
        bool hitCap = false;

        Mutate(player, state =>
        {
            int before = Math.Max(0, state.Balance);
            int next = Math.Min(cap, before + scaled);
            added = next - before;
            hitCap = before < cap && before + scaled > cap;   // once, when this award reaches the cap
            if (added <= 0) return false;
            state.Balance = next;
            return true;
        }, out var updated);

        if (added > 0)
            await SyncAsync(player, updated.Balance, cap, reason ?? $"award +{added}");

        if (hitCap)
            await PlayerOf(player).Tell("Cannot store any more Hiveblood! Visit the Genesis Tree in the Monastery of the Keepers to consume it.");

        return added;
    }

    /// <summary>Spends <paramref name="amount"/> Hiveblood. When <paramref name="countTowardTreeQuests"/> is true,
    /// increments <see cref="LifetimeSpent"/> and unlocks any reached <c>BrequelFruitQuests</c> league flags
    /// (<c>ChayulaTreeJuiced1..7</c>).</summary>
    public static async Task<bool> TrySpendAsync(Player player, int amount, bool countTowardTreeQuests = true, string? reason = null)
    {
        if (amount <= 0) return true;
        bool spent = Mutate(player, state =>
        {
            if (state.Balance < amount) return false;
            state.Balance -= amount;
            if (countTowardTreeQuests)
                state.LifetimeSpent = checked(state.LifetimeSpent + amount);
            return true;
        }, out var updated);

        if (!spent) return false;

        int cap = Capacity(player);
        await SyncAsync(player, updated.Balance, cap, reason ?? $"spend -{amount}");
        if (countTowardTreeQuests)
            await SyncFruitQuestFlagsAsync(player, updated.LifetimeSpent);
        return true;
    }

    /// <summary>Checks <see cref="LifetimeSpent"/> against <c>BrequelFruitQuests</c> and sets any newly reached
    /// <c>ChayulaTreeJuiced1..7</c> league flags.</summary>
    public static async Task SyncFruitQuestFlagsAsync(Player player, int? spentOverride = null)
    {
        int spent = spentOverride ?? LifetimeSpent(player);
        if (Milestones.Length == 0 || spent <= 0) return;

        List<int>? toSet = null;
        foreach (var m in Milestones)
        {
            if (spent >= m.CumulativeRequired && !player.Character.LeagueQuestFlags.Contains(m.ReachedLeagueFlag))
                (toSet ??= []).Add(m.ReachedLeagueFlag);
        }
        if (toSet is { Count: > 0 })
            await SetLeagueQuestFlags(player, toSet);
    }

    /// <summary>Syncs the player's current Hiveblood balance to the client via <c>S→C 0x02d2</c>.</summary>
    public static Task SyncAsync(Player player, string reason = "sync") =>
        SyncAsync(player, Balance(player), Capacity(player), reason);

    static Task SyncAsync(Player player, int balance, int cap, string reason) =>
        PlayerOf(player).Send(BuildHivebloodPacket(balance), $"0x02d2 Hiveblood {balance}/{cap} ({reason})");
}
