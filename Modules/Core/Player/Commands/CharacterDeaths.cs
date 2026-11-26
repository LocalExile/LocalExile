using System.Globalization;
using System.Runtime.CompilerServices;

namespace GameServer.Modules.M_commands;

/// <summary>Tracks per-character death counts in <see cref="CharacterSaves.ModuleData"/> using
/// <see cref="Player.DeathNotificationEpoch"/> so each death is persisted once across ticks, resurrections and
/// area changes.</summary>
public sealed class CharacterDeaths
{
    public const string SaveKey = "commands.deaths";

    sealed class DeathTracker
    {
        public readonly Lock Gate = new();
        public long LastEpoch = -1;
    }

    static readonly ConditionalWeakTable<Player, DeathTracker> Trackers = new();

    static int Parse(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        && value >= 0 ? value : 0;

    /// <summary>Persists a newly observed death for <paramref name="player"/> once per distinct death epoch.</summary>
    public void Observe(Player player)
    {
        if (!player.EarnsProgress || player.Character.Name is not { Length: > 0 } name) return;
        long epoch = player.DeathNotificationEpoch;
        if (epoch < 0) return;
        // Fast lock-free short-circuit on every AreaTick once this death epoch has been committed.
        if (Trackers.TryGetValue(player, out var existing) && Volatile.Read(ref existing.LastEpoch) == epoch)
            return;
        var tracker = Trackers.GetOrCreateValue(player);
        lock (tracker.Gate)
        {
            if (Volatile.Read(ref tracker.LastEpoch) == epoch) return;
            Saves.Atomically(() => CharacterSaves.ChangeModuleData(name, SaveKey,
                current => (Parse(current) + 1).ToString(CultureInfo.InvariantCulture)));
            Volatile.Write(ref tracker.LastEpoch, epoch);
        }
    }

    public int Count(Player player)
    {
        Observe(player);
        return player.Character.Name is { Length: > 0 } name ? Parse(CharacterSaves.ModuleData(name, SaveKey)) : 0;
    }

    public static string Describe(int deaths) =>
        deaths == 1 ? "You have died 1 time." : $"You have died {deaths} times.";
}
