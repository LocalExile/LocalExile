using System.Runtime.CompilerServices;

public static partial class Game
{
    // Void Charges (Voidfletcher): "Gain a Void Charge every # seconds" up to "# Maximum Void Charges"; a bow attack
    // spends one to trigger Void Shot. Counted when looked at, from the time of the last gain, so nothing ticks.
    sealed class VoidChargeState { public int Count; public double Last = double.NaN; }
    static readonly ConditionalWeakTable<Player, VoidChargeState> VoidChargesOf = new();

    internal static int VoidChargesLocked(Player player, double now)
    {
        var stats = StatSourceValues(player);
        int maximum = stats.GetValueOrDefault(Stat.MaximumVoidArrows), everyMs = stats.GetValueOrDefault(Stat.GrantVoidArrowEveryXMs);
        if (maximum <= 0 || everyMs <= 0)
        {
            VoidChargesOf.Remove(player);
            return 0;
        }
        var state = VoidChargesOf.GetOrCreateValue(player);
        double every = everyMs / 1000.0;
        if (double.IsNaN(state.Last) || now < state.Last) state.Last = now;
        int gained = (int)Math.Min(maximum, (now - state.Last) / every);
        if (gained > 0)
        {
            state.Count = Math.Min(maximum, state.Count + gained);
            state.Last += gained * every;
        }
        // At the maximum nothing builds up: the next charge starts counting when one is spent.
        if (state.Count >= maximum) { state.Count = maximum; state.Last = now; }
        return state.Count;
    }

    internal static bool TakeVoidChargeLocked(Player player, double now)
    {
        if (VoidChargesLocked(player, now) <= 0 || !VoidChargesOf.TryGetValue(player, out var state)) return false;
        state.Count--;
        return true;
    }
}
