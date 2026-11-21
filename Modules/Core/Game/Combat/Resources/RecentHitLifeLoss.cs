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
    public const uint LifeLossIfHitRecently = Stat.LifeLossPctPerMinuteIfHaveBeenHitRecently;
    static readonly Store<Player, RecentHitLifeLossState> RecentHitLifeLossStates = new();
    public sealed record CarriedRecentHitLifeLoss(double Pending);
    public static RecentHitLifeLossState RecentHitLifeLossOf(Player player) => RecentHitLifeLossStates.GetOrCreateValue(player);

    static void ConfigureRecentHitLifeLoss(Player player, IReadOnlyList<ItemModStats.Worn> worn, HashSet<uint> inactive)
    {
        double now = ResourceEffectsTime(player);
        var state = RecentHitLifeLossOf(player);
        if (!CurrentGameWork || player.Closed || player.RageRetired || player.PlayerLife == 0)
        { state.Clear(now); return; }
        if (player.LeavingArea || state.Frozen) return;
        var active = worn.Where(w => !inactive.Contains(w.Id)).ToArray();
        var items = active.Select(w => w.Item).ToArray();
        var ids = active.Select(w => w.Id).ToArray();
        bool changed = !ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch
            || !ids.SequenceEqual(state.ItemIds) || items.Length != state.Items.Length
            || items.Where((item, i) => !ReferenceEquals(item, state.Items[i])).Any();
        int coefficient = state.PercentPerMinute;
        var hosts = state.Hosts;
        if (changed)
        {
            coefficient = Math.Max(0, ItemModStats.Sum(items, ItemsFor(player).Mods).GetValueOrDefault(LifeLossIfHitRecently));
            hosts = active.Where(w => ItemModStats.Sum([w.Item], ItemsFor(player).Mods)
                .GetValueOrDefault(LifeLossIfHitRecently) > 0).Select(w => w.Id).Order().ToArray();
        }
        if (coefficient > 0) ResourceEffectsOf(player);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch)
        { state.Clear(now); state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch; }
        state.Settle(now, player.PlayerMaxLife);
        state.PercentPerMinute = coefficient;
        state.Hosts = hosts; state.Items = items; state.ItemIds = ids;
        var window = player.RecentEvents.LastWindow(StatConditions.BeenHit);
        (state.Started, state.Until) = window ?? (now, now);
    }

    public static void RecentHitLifeLossOnHitLocked(Player player, double now)
    {
        if (!CurrentGameWork || player.PlayerLife == 0 || player.Closed || player.LeavingArea || player.RageRetired) return;
        var state = RecentHitLifeLossOf(player);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch)
        { state.Clear(now); state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch; }
        state.Settle(now, player.PlayerMaxLife);
        state.Started = now; state.Until = now + RecentEvents.WindowSeconds;
    }

    public static uint AdvanceRecentHitLifeLossLocked(Player player, double now)
    {
        if (!RecentHitLifeLossStates.TryGetValue(player, out var state)) return 0;
        if (!CurrentGameWork || player.Closed || player.RageRetired || player.PlayerLife == 0
            || !ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch)
        { state.Clear(now); return 0; }
        if (player.LeavingArea || state.Frozen) return 0;
        state.Settle(now, player.PlayerMaxLife);
        uint lost = (uint)Math.Min(player.PlayerLife, Math.Max(0, Math.Floor(state.Pending)));
        state.Pending -= lost;
        player.PlayerLife -= lost;
        if (player.PlayerLife == 0)
        {
            state.Clear(now); player.InvalidateIncomingHits(); RecordHardcoreDeath(player);
        }
        return lost;
    }

    public static CarriedRecentHitLifeLoss? CaptureRecentHitLifeLoss(Player player, double at)
    {
        if (!RecentHitLifeLossStates.TryGetValue(player, out var state)) return null;
        state.Settle(at, player.PlayerMaxLife);
        return state.Pending > 0 ? new(state.Pending) : null;
    }

    public static void PauseRecentHitLifeLoss(Player player, double at)
    {
        if (!RecentHitLifeLossStates.TryGetValue(player, out var state)) return;
        state.Settle(at, player.PlayerMaxLife); state.Frozen = true;
    }

    // Existing generic recent histories belong to an area connection. Transfer carries only already accrued loss;
    // a refused load can resume that connection's remaining history, without charging any loading time.
    public static void RestoreRecentHitLifeLoss(Player player, CarriedRecentHitLifeLoss? value, double at)
    {
        var state = RecentHitLifeLossOf(player); state.Clear(at);
        state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch;
        state.Pending = Math.Max(0, value?.Pending ?? 0);
        (state.Started, state.Until) = player.RecentEvents.LastWindow(StatConditions.BeenHit) ?? (at, at);
    }

    public static void RetireRecentHitLifeLoss(Player player)
    {
        if (RecentHitLifeLossStates.TryGetValue(player, out var state)) state.Clear(ResourceEffectsTime(player));
    }
}
