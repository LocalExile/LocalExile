using System.Globalization;
using System.Runtime.CompilerServices;

namespace GameServer.Modules.M_commands;

/// <summary>Tracks per-character death counts in <see cref="CharacterSaves.ModuleData"/> using
/// <see cref="Player.DeathNotificationEpoch"/> so each death is persisted once across ticks, resurrections,
/// area changes and collectible module reloads.</summary>
public sealed class CharacterDeaths
{
    public const string SaveKey = "commands.deaths";

    static readonly object Gate = Keep("DeathGate", () => new object());
    static readonly ConditionalWeakTable<Player, long[]> SeenEpochs =
        Keep("DeathEpochs", () => new ConditionalWeakTable<Player, long[]>());

    static T Keep<T>(string name, Func<T> create) where T : class
    {
        string key = "LocalExile.Commands." + name;
        if (AppContext.GetData(key) is T existing) return existing;
        var value = create();
        AppContext.SetData(key, value);
        return value;
    }

    static int Parse(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        && value >= 0 ? value : 0;

    /// <summary>Persists a newly observed death for <paramref name="player"/> once per distinct death epoch.</summary>
    public void Observe(Player player)
    {
        if (player.Controlled || player.Character.Name is not { Length: > 0 } name) return;
        long epoch = player.DeathNotificationEpoch;
        if (epoch < 0) return;
        lock (Gate)
        {
            long[] seen = SeenEpochs.GetValue(player, _ => [-1L]);
            if (seen[0] == epoch) return;
            seen[0] = epoch;
        }
        Saves.Atomically(() => CharacterSaves.ChangeModuleData(name, SaveKey,
            current => (Parse(current) + 1).ToString(CultureInfo.InvariantCulture)));
    }

    public int Count(Player player)
    {
        Observe(player);
        return player.Character.Name is { Length: > 0 } name ? Parse(CharacterSaves.ModuleData(name, SaveKey)) : 0;
    }

    public static string Describe(int deaths) =>
        deaths == 1 ? "You have died 1 time." : $"You have died {deaths} times.";
}
