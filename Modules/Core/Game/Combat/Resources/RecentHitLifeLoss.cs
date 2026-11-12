using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

// Life loss is a resource operation, not damage or negative regeneration. Keep its fraction and accepted-hit
// interval independent of resistance, ES, recovery scaling and Petrified Blood's hit prevention.
public sealed class RecentHitLifeLossState
{
    public double At = double.NaN, Started, Until, Pending;
    public int PercentPerMinute, Epoch;
    public object? Rules;
    public uint[] Hosts = [];
    public Item[] Items = [];
    public uint[] ItemIds = [];
    public bool Frozen;

    public void Clear(double at)
    {
        At = at; Started = Until = Pending = 0; PercentPerMinute = 0; Hosts = []; Items = []; ItemIds = []; Frozen = false;
    }

    public void Settle(double now, uint maximumLife)
    {
        if (Frozen || !double.IsFinite(now)) return;
        if (double.IsNaN(At)) { At = now; return; }
        if (now <= At) return;
        double seconds = Math.Max(0, Math.Min(now, Until) - Math.Max(At, Started));
        Pending += seconds * maximumLife * Math.Max(0, PercentPerMinute) / 6000d;
        At = now;
    }
}

public static partial class Game
{
    public const string LifeLossIfHitRecently = "life_loss_%_per_minute_if_have_been_hit_recently";
    static readonly RuleTable<ClientState, RecentHitLifeLossState> RecentHitLifeLossStates = new();
    public sealed record CarriedRecentHitLifeLoss(double Pending);
    public static RecentHitLifeLossState RecentHitLifeLossOf(ClientState conn) => RecentHitLifeLossStates.GetOrCreateValue(conn);

    static void ConfigureRecentHitLifeLoss(ClientState conn, IReadOnlyList<ItemModStats.Worn> worn, HashSet<uint> inactive)
    {
        double now = ResourceEffectsTime(conn);
        var state = RecentHitLifeLossOf(conn);
        if (!CurrentGameWork || conn.Closed || conn.RageRetired || conn.PlayerLife == 0)
        { state.Clear(now); return; }
        if (conn.LeavingArea || state.Frozen) return;
        var active = worn.Where(w => !inactive.Contains(w.Id)).ToArray();
        var items = active.Select(w => w.Item).ToArray();
        var ids = active.Select(w => w.Id).ToArray();
        bool changed = !ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch
            || !ids.SequenceEqual(state.ItemIds) || items.Length != state.Items.Length
            || items.Where((item, i) => !ReferenceEquals(item, state.Items[i])).Any();
        int coefficient = state.PercentPerMinute;
        var hosts = state.Hosts;
        if (changed)
        {
            coefficient = Math.Max(0, ItemModStats.Sum(items, ItemsFor(conn).Mods).GetValueOrDefault(LifeLossIfHitRecently));
            hosts = active.Where(w => ItemModStats.Sum([w.Item], ItemsFor(conn).Mods)
                .GetValueOrDefault(LifeLossIfHitRecently) > 0).Select(w => w.Id).Order().ToArray();
        }
        if (coefficient > 0) ResourceEffectsOf(conn);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch)
        { state.Clear(now); state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; }
        state.Settle(now, conn.PlayerMaxLife);
        state.PercentPerMinute = coefficient;
        state.Hosts = hosts; state.Items = items; state.ItemIds = ids;
        var window = conn.RecentEvents.LastWindow(StatConditions.BeenHit);
        (state.Started, state.Until) = window ?? (now, now);
    }

    public static void RecentHitLifeLossOnHitLocked(ClientState conn, double now)
    {
        if (!CurrentGameWork || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || conn.RageRetired) return;
        var state = RecentHitLifeLossOf(conn);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch)
        { state.Clear(now); state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; }
        state.Settle(now, conn.PlayerMaxLife);
        state.Started = now; state.Until = now + RecentEvents.WindowSeconds;
    }

    public static uint AdvanceRecentHitLifeLossLocked(ClientState conn, double now)
    {
        if (!RecentHitLifeLossStates.TryGetValue(conn, out var state)) return 0;
        if (!CurrentGameWork || conn.Closed || conn.RageRetired || conn.PlayerLife == 0
            || !ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch)
        { state.Clear(now); return 0; }
        if (conn.LeavingArea || state.Frozen) return 0;
        state.Settle(now, conn.PlayerMaxLife);
        uint lost = (uint)Math.Min(conn.PlayerLife, Math.Max(0, Math.Floor(state.Pending)));
        state.Pending -= lost;
        conn.PlayerLife -= lost;
        if (conn.PlayerLife == 0)
        {
            state.Clear(now); conn.InvalidateIncomingHits(); RecordHardcoreDeath(conn);
        }
        return lost;
    }

    public static CarriedRecentHitLifeLoss? CaptureRecentHitLifeLoss(ClientState conn, double at)
    {
        if (!RecentHitLifeLossStates.TryGetValue(conn, out var state)) return null;
        state.Settle(at, conn.PlayerMaxLife);
        return state.Pending > 0 ? new(state.Pending) : null;
    }

    public static void PauseRecentHitLifeLoss(ClientState conn, double at)
    {
        if (!RecentHitLifeLossStates.TryGetValue(conn, out var state)) return;
        state.Settle(at, conn.PlayerMaxLife); state.Frozen = true;
    }

    // Existing generic recent histories belong to an area connection. Transfer carries only already accrued loss;
    // a refused load can resume that connection's remaining history, without charging any loading time.
    public static void RestoreRecentHitLifeLoss(ClientState conn, CarriedRecentHitLifeLoss? value, double at)
    {
        var state = RecentHitLifeLossOf(conn); state.Clear(at);
        state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch;
        state.Pending = Math.Max(0, value?.Pending ?? 0);
        (state.Started, state.Until) = conn.RecentEvents.LastWindow(StatConditions.BeenHit) ?? (at, at);
    }

    public static void RetireRecentHitLifeLoss(ClientState conn)
    {
        if (RecentHitLifeLossStates.TryGetValue(conn, out var state)) state.Clear(ResourceEffectsTime(conn));
    }
}
