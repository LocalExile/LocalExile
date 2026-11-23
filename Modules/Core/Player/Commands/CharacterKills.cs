using System.Diagnostics;
using System.Globalization;

namespace GameServer.Modules.M_commands;

/// <summary>Persistent per-character experience-granting kill counter for /kills, batched in memory like
/// <see cref="CharacterPlaytime"/> so high-density map clearing never writes SQLite per kill.</summary>
public sealed class CharacterKills(Func<double>? time = null) : IDisposable
{
    public const string SaveKey = "commands.kills";

    readonly Func<double> now = time ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    readonly Dictionary<Player, Pending> sessions = [];
    bool retired;

    sealed class Pending(string character, double at)
    {
        public readonly string Character = character;
        public long Kills;
        public double FlushedAt = at;
    }

    static long Parse(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= 0 ? v : 0;

    static long Saved(string name) => Parse(CharacterSaves.ModuleData(name, SaveKey));

    Pending? Session(Player player)
    {
        if (retired || player.Controlled || player.Character.Name is not { Length: > 0 } name || player.Closed || player.LeavingArea) return null;
        lock (sessions)
        {
            if (retired) return null;
            if (!sessions.TryGetValue(player, out var s)) sessions[player] = s = new(name, now());
            return s;
        }
    }

    public void Record(Player player)
    {
        if (Session(player) is not { } s) return;
        lock (s) s.Kills++;
    }

    public void Step(Player player)
    {
        if (!player.Connection.PlayerSent) return;
        Pending? s;
        lock (sessions) sessions.TryGetValue(player, out s);
        if (s is not null && s.Kills > 0 && now() - s.FlushedAt >= 30) Flush(s);
    }

    public long Count(Player player)
    {
        if (player.Character.Name is not { Length: > 0 } name) return 0;
        long pending = 0;
        Pending? s;
        lock (sessions) sessions.TryGetValue(player, out s);
        if (s is not null) lock (s) pending = s.Kills;
        return Saved(name) + pending;
    }

    void Flush(Pending s)
    {
        long delta;
        lock (s)
        {
            delta = s.Kills;
            if (delta <= 0) { s.FlushedAt = now(); return; }
            s.Kills = 0;
            s.FlushedAt = now();
        }
        try
        {
            Saves.Atomically(() => CharacterSaves.ChangeModuleData(s.Character, SaveKey,
                current => (Parse(current) + delta).ToString(CultureInfo.InvariantCulture)));
        }
        catch
        {
            lock (s) s.Kills += delta;
            throw;
        }
    }

    public void End(Player player)
    {
        Pending? s;
        lock (sessions) sessions.Remove(player, out s);
        if (s is not null) Flush(s);
    }

    public void Dispose()
    {
        Pending[] open;
        lock (sessions)
        {
            retired = true;
            open = [.. sessions.Values];
            sessions.Clear();
        }
        foreach (var s in open) Flush(s);
    }

    // BackendErrors PlayerKillCount1 (579) / PlayerKillCountN (176).
    public static string Describe(long kills) =>
        kills == 1 ? "You have killed 1 monster." : $"You have killed {kills:N0} monsters.";
}
