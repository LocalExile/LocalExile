using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static IReadOnlyList<PlayerSkillSource> CaptureGemSourceRowsLocked(ClientState conn) =>
        [.. PlayerSkillSourcesLocked(conn)];

    static GemSourceLifetimes.SourceLifetime CaptureGemSourceLifetimeLocked(ClientState conn,
        PlayerSkillSource source)
    {
        var marker = ItemsOf(conn).CaptureGemSourceLifetime(source.Identity);
        return marker is null
            ? conn.GemSourceLifetimes.CaptureRetired(source.Identity)
            : conn.GemSourceLifetimes.Capture(source.Identity, marker);
    }

    static GemSourceLifetimes.SourceLifetime? CaptureGemSourceLifetimeLocked(ClientState conn, ushort skillId, int effect)
    {
        var source = CurrentPlayerSkillSourceLocked(conn, skillId);
        return source is { Active: true } && source.Effect == effect
            ? CaptureGemSourceLifetimeLocked(conn, source) : null;
    }

    static bool GemSourceLifetimeCurrentLocked(ClientState conn, ushort skillId, int effect,
        GemSourceLifetimes.SourceLifetime? lifetime)
    {
        if (lifetime is null || !conn.GemSourceLifetimes.IsCurrent(lifetime)) return false;
        var source = CurrentPlayerSkillSourceLocked(conn, skillId);
        return source is { Active: true } && source.Effect == effect && source.Identity == lifetime.Key;
    }

    static IReadOnlyList<PlayerSkillSource> GemSourceRowsLocked(ClientState conn, uint host, int socket, Item gem)
    {
        var effects = ItemsFor(conn).GemEffects(gem).ToHashSet();
        return [.. PlayerSkillSourcesLocked(conn).Where(source =>
            source.Identity.Source == GemSkillIds.SourceKind.SocketedGem
            && source.Identity.Host == host && source.Identity.Socket == socket && effects.Contains(source.Effect))];
    }

    static void RetireGemSourceLifetimesLocked(ClientState conn, IReadOnlyList<PlayerSkillSource> sources)
    {
        foreach (var source in sources) conn.GemSourceLifetimes.Retire(source.Identity);
    }
}
