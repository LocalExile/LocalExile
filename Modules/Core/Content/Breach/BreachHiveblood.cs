using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// Per-account/league Hiveblood (Graftblood) balance, storage capacity (<c>BrequelItemResourceValues</c>), cumulative
/// Genesis Tree spend milestones (<c>BrequelFruitQuests</c>, 300..70300 -> LQF 1594..1600), and HUD counter sync.
/// Persisted atomically in <see cref="IPlayerWorld.AccountData"/> so all characters in a league share Hiveblood as in
/// PoE 3.29.
/// </summary>
public static class BreachHiveblood
{
    const string DataKey = "breach.hiveblood";

    /// <summary>Default 3.29 storage multiplier applied to <c>BrequelItemResourceValues</c> when Fleshgrafts are not
    /// equipped (3.29 removed Fleshgraft items while retaining the level-scaled cap table).</summary>
    const int BaseStorageMultiplier = 10;
    const int MinimumStorageCap = 500;

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

    /// <summary>Optional HUD sync sender wired once protocol details for the 3.29.3c Hiveblood counter land.</summary>
    public static Func<Player, int, int, string, Task>? SendHudSync { get; set; }

    /// <summary>Loads <c>BrequelItemResourceValues</c> and <c>BrequelFruitQuests</c> from <c>client.db</c>.</summary>
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

    /// <summary>Base storage value from <c>BrequelItemResourceValues</c> at <paramref name="level"/>.</summary>
    public static int BaseStorageForLevel(int level)
    {
        int clamped = Math.Clamp(level, 1, 100);
        if (StorageByLevel.TryGetValue(clamped, out int value)) return value;
        return clamped < 68 ? Math.Max(8, clamped * 2) : 210 + (clamped - 68) * 80;
    }

    /// <summary>Maximum Hiveblood this player can currently hold, derived from <c>BrequelItemResourceValues</c> at
    /// character level (and any <c>local_maximum_brequel_resource_+%</c> stat if present).</summary>
    public static int Capacity(Player player)
    {
        int level = Math.Clamp(player.Pools.Level, 1, 100);
        long baseCap = (long)BaseStorageForLevel(level) * BaseStorageMultiplier;
        int incPct = AreaLeagueStats(player).GetValueOrDefault(Stat.LocalMaximumBrequelResourcePlusPct);
        if (incPct != 0) baseCap = baseCap * Math.Max(10, 100 + incPct) / 100;
        return (int)Math.Clamp(baseCap, MinimumStorageCap, 1_000_000);
    }

    /// <summary>Awards Hiveblood from a Breach encounter, scaling by <c>brequel_resource_gained_+%</c>, clamping to
    /// <see cref="Capacity"/>, persisting to <c>AccountData</c>, and syncing the client HUD. Returns the actual amount
    /// added.</summary>
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
            hitCap = next >= cap && before + scaled > cap;
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

    /// <summary>Syncs the player's current Hiveblood balance &amp; cap to the client HUD.</summary>
    public static Task SyncAsync(Player player, string reason = "enter") =>
        SyncAsync(player, Balance(player), Capacity(player), reason);

    static async Task SyncAsync(Player player, int balance, int cap, string reason)
    {
        if (SendHudSync is { } sender)
            await sender(player, balance, cap, reason);
    }
}
