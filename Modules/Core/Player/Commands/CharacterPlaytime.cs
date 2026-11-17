using System.Diagnostics;
using System.Globalization;

namespace GameServer.Modules.M_commands;

public sealed class CharacterPlaytime(Func<double>? time = null) : IDisposable
{
    public const string SaveKey = "commands.played-seconds";
    readonly Func<double> now = time ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    readonly Dictionary<ClientState, Session> sessions = [];
    bool retired;
    sealed class Session(string character, double at)
    {
        public readonly string Character = character;
        public double At = at;
    }

    public void Begin(ClientState conn)
    {
        lock (sessions)
            if (!retired && !conn.Controlled && conn.Character is { Length: > 0 } name && !conn.Closed && !conn.LeavingArea)
                sessions.TryAdd(conn, new(name, now()));
    }

    public void Step(ClientState conn)
    {
        if (!conn.PlayerSent) return;
        Begin(conn);
        lock (sessions)
            if (sessions.TryGetValue(conn, out var session) && now() - session.At >= 30) Save(session);
    }

    static double Saved(string name) => double.TryParse(CharacterSaves.ModuleData(name, SaveKey),
        NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0 ? value : 0;

    public double Seconds(ClientState conn)
    {
        Begin(conn);
        lock (sessions)
            return sessions.TryGetValue(conn, out var session)
                ? Saved(session.Character) + Math.Max(0, now() - session.At)
                : conn.Character is { } name ? Saved(name) : 0;
    }

    void Save(Session session)
    {
        double at = now(), elapsed = Math.Max(0, at - session.At);
        if (elapsed <= 0) return;
        Saves.Atomically(() => CharacterSaves.ChangeModuleData(session.Character, SaveKey,
            _ => (Saved(session.Character) + elapsed).ToString("R", CultureInfo.InvariantCulture)));
        session.At = at;
    }

    public void End(ClientState conn)
    {
        lock (sessions)
            if (sessions.TryGetValue(conn, out var session)) { Save(session); sessions.Remove(conn); }
    }

    public void Dispose()
    {
        lock (sessions)
        {
            retired = true;
            foreach (var session in sessions.Values) Save(session);
            sessions.Clear();
        }
    }

    public static string Describe(double seconds)
    {
        long whole = (long)Math.Min(Math.Max(0, seconds), long.MaxValue / 2);
        return $"You have played this character for {whole / 86400} days, {whole / 3600 % 24} hours, {whole / 60 % 60} minutes and {whole % 60} seconds.";
    }
}
