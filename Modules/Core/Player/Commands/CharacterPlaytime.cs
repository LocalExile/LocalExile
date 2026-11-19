using System.Diagnostics;
using System.Globalization;

namespace GameServer.Modules.M_commands;

public sealed class CharacterPlaytime(Func<double>? time = null) : IDisposable
{
    public const string SaveKey = "commands.played-seconds";
    readonly Func<double> now = time ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    readonly Dictionary<Player, Session> sessions = [];
    bool retired;
    sealed class Session(string character, double at)
    {
        public readonly string Character = character;
        public double At = at;
    }

    public void Begin(Player conn)
    {
        lock (sessions)
            if (!retired && !conn.Controlled && conn.Character is { Length: > 0 } name && !conn.Closed && !conn.LeavingArea)
                sessions.TryAdd(conn, new(name, now()));
    }

    public void Step(Player conn)
    {
        if (!conn.PlayerSent) return;
        Begin(conn);
        Session? session;
        lock (sessions) sessions.TryGetValue(conn, out session);
        if (session is not null && now() - session.At >= 30) Save(session);
    }

    static double Parse(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && double.IsFinite(value) && value >= 0 ? value : 0;

    static double Saved(string name) => Parse(CharacterSaves.ModuleData(name, SaveKey));

    public double Seconds(Player conn)
    {
        Begin(conn);
        Session? session;
        lock (sessions) sessions.TryGetValue(conn, out session);
        if (session is null) return conn.Character is { } name ? Saved(name) : 0;
        lock (session) return Saved(session.Character) + Math.Max(0, now() - session.At);
    }

    // The write rewrites the whole save (16 ms for 400 items): only this character's lock is held, never every player's.
    void Save(Session session)
    {
        lock (session)
        {
            double at = now(), elapsed = Math.Max(0, at - session.At);
            if (elapsed <= 0) return;
            Saves.Atomically(() => CharacterSaves.ChangeModuleData(session.Character, SaveKey,
                current => (Parse(current) + elapsed).ToString("R", CultureInfo.InvariantCulture)));
            session.At = at;
        }
    }

    public void End(Player conn)
    {
        Session? session;
        lock (sessions) sessions.Remove(conn, out session);
        if (session is not null) Save(session);
    }

    public void Dispose()
    {
        Session[] open;
        lock (sessions)
        {
            retired = true;
            open = [.. sessions.Values];
            sessions.Clear();
        }
        foreach (var session in open) Save(session);
    }

    public static string Describe(double seconds)
    {
        long whole = (long)Math.Min(Math.Max(0, seconds), long.MaxValue / 2);
        static string Unit(long n, string name) => $"{n} {name}{(n == 1 ? "" : "s")}";
        // BackendErrors PlayedTime: "You have played for {0}."
        return $"You have played for {Unit(whole / 86400, "day")}, {Unit(whole / 3600 % 24, "hour")}, {Unit(whole / 60 % 60, "minute")} and {Unit(whole % 60, "second")}.";
    }
}
