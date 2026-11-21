using System.Runtime.CompilerServices;

// Held in place (a trap's spikes risen round it: the Labyrinth's spike traps, BuffDefinitions trap_pinned): a pinned
// player starts no walk, a pinned monster acts as a frozen one, until the time given (Clock seconds). The walk under way
// is not cut here: the risen spikes' wall stops it at its next step, as the client's walker stops (StepWalkers).
public static partial class Game
{
    static readonly ConditionalWeakTable<object, StrongBox<double>> PinnedUntil = new();

    /// <summary>Holds the player in place until <paramref name="until"/>. Under conn.Monsters.</summary>
    public static void PinPlayer(Player player, double until) => Pin(player, until);

    /// <summary>Holds the monster in place until <paramref name="until"/>. Under conn.Monsters.</summary>
    public static void PinMonster(SpawnedMonster m, double until) => Pin(m, until);

    static void Pin(object who, double until)
    {
        var box = PinnedUntil.GetValue(who, _ => new StrongBox<double>(double.NegativeInfinity));
        box.Value = Math.Max(box.Value, until);
    }

    public static bool Pinned(object who, double now) => PinnedUntil.TryGetValue(who, out var box) && now < box.Value;
}
