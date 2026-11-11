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

    // A successful manual physical removal is already the source's end. Retire every canonical row for that gem, including
    // inactive regular/Vaal rows, then eagerly remove the actual owned actors and persist before the handler's first send
    // suspend. The normal equipment publication that follows reconciles the player's stat and skill lists.
    static void RetireDirectMinionSourcesLocked(ClientState conn, IReadOnlyList<PlayerSkillSource> sources)
    {
        if (sources.Count == 0) return;
        var expectedRf = conn.RighteousFire;
        bool matchesRetiringRf = expectedRf is { SourceLifetime: { } lifetime }
            && ReferenceEquals(conn.RighteousFire, expectedRf)
            && ReferenceEquals(conn.GemSourceLifetimes.Current(lifetime.Key), lifetime)
            && sources.Any(source => source.Identity == lifetime.Key);
        RetireGemSourceLifetimesLocked(conn, sources);
        if (matchesRetiringRf && PlanRighteousFireDetachLocked(conn, expectedRf) is { } cleanup)
            QueueRighteousFireCleanupLocked(conn, cleanup, "Righteous Fire source retired");
        var sourceIds = sources.Select(source => source.Id).ToHashSet();
        using (new PlayerStatScope(conn, fresh: true))
            EndNonRighteousFireSkillEffectsLocked(conn, sourceIds);
        using (new PlayerStatScope(conn, fresh: true))
            EnsureOwnAuraPools(conn);
        using (new PlayerStatScope(conn, fresh: true))
        {
            RecomputePoolsAndSpeed(conn);
            ReconcileBeastCompanionsLocked(conn);
            var plan = PlanGolemBuffsLocked(conn, CharacterPlayerStats(conn));
            if (plan.Packets.Length > 0)
                conn.Minions.OutputQueue.Add(new(plan.Packets, "golem owner state after direct source retirement"));
        }
        SaveSummons(conn);
        SaveGolems(conn);
    }

    // Actual equipment writers capture the canonical rows before their inventory write. After that write and its fresh
    // stat/source rebuild, an old physical source either continues by identity or ends here. Removing its actors and
    // Golem owner state can make another old source fail its requirements (for example, by losing a Golem slot), so
    // repeat fresh qualification until no old identity becomes newly invalid. Every pass must end at least one old key;
    // therefore no unchanged read can keep this loop alive and no retired lifetime can be reactivated.
    public static void ReconcileEquipmentSourceLifetimesLocked(ClientState conn, IReadOnlyList<PlayerSkillSource> before)
    {
        if (before.Count == 0) return;
        var pending = before.DistinctBy(source => source.Identity).ToList();
        int limit = pending.Count + 1;
        for (int pass = 0; pass < limit && pending.Count > 0; pass++)
        {
            var retiring = new List<PlayerSkillSource>();
            using (new PlayerStatScope(conn, fresh: true))
            {
                var current = PlayerSkillSourcesLocked(conn);
                foreach (var old in pending)
                {
                    var now = current.FirstOrDefault(source => source.Identity == old.Identity);
                    if (now is null || !now.Active || !SupportedSkill(conn, now.Id).RequirementsMet)
                        retiring.Add(old);
                }
            }
            if (retiring.Count == 0) return;
            var identities = retiring.Select(source => source.Identity).ToHashSet();
            pending.RemoveAll(source => identities.Contains(source.Identity));
            RetireDirectMinionSourcesLocked(conn, retiring);
        }
    }
}
