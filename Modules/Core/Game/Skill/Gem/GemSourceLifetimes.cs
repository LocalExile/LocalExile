using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static IReadOnlyList<PlayerSkillSource> CaptureGemSourceRowsLocked(Player player) =>
        [.. PlayerSkillSourcesLocked(player)];

    static GemSourceLifetimes.SourceLifetime CaptureGemSourceLifetimeLocked(Player player,
        PlayerSkillSource source)
    {
        var marker = ItemsOf(player).CaptureGemSourceLifetime(source.Identity);
        return marker is null
            ? player.GemSourceLifetimes.CaptureRetired(source.Identity)
            : player.GemSourceLifetimes.Capture(source.Identity, marker);
    }

    static GemSourceLifetimes.SourceLifetime? CaptureGemSourceLifetimeLocked(Player player, ushort skillId, int effect)
    {
        var source = CurrentPlayerSkillSourceLocked(player, skillId);
        return source is { Active: true } && source.Effect == effect
            ? CaptureGemSourceLifetimeLocked(player, source) : null;
    }

    static bool GemSourceLifetimeCurrentLocked(Player player, ushort skillId, int effect,
        GemSourceLifetimes.SourceLifetime? lifetime)
    {
        if (lifetime is null || !player.GemSourceLifetimes.IsCurrent(lifetime)) return false;
        var source = CurrentPlayerSkillSourceLocked(player, skillId);
        return source is { Active: true } && source.Effect == effect && source.Identity == lifetime.Key;
    }

    public static IReadOnlyList<PlayerSkillSource> GemSourceRowsLocked(Player player, uint host, int socket, Item gem)
    {
        var effects = ItemsFor(player).GemEffects(gem).ToHashSet();
        return [.. PlayerSkillSourcesLocked(player).Where(source =>
            source.Identity.Source == GemSkillIds.SourceKind.SocketedGem
            && source.Identity.Host == host && source.Identity.Socket == socket && effects.Contains(source.Effect))];
    }

    static void RetireGemSourceLifetimesLocked(Player player, IReadOnlyList<PlayerSkillSource> sources)
    {
        foreach (var source in sources) player.GemSourceLifetimes.Retire(source.Identity);
    }

    // A successful manual physical removal is already the source's end. Retire every canonical row for that gem, including
    // inactive regular/Vaal rows, then eagerly remove the actual owned actors and persist before the handler's first send
    // suspend. The normal equipment publication that follows reconciles the player's stat and skill lists.
    public static void RetireDirectMinionSourcesLocked(Player player, IReadOnlyList<PlayerSkillSource> sources)
    {
        if (sources.Count == 0) return;
        var expectedRf = player.RighteousFire;
        bool matchesRetiringRf = expectedRf is { SourceLifetime: { } lifetime }
            && ReferenceEquals(player.RighteousFire, expectedRf)
            && ReferenceEquals(player.GemSourceLifetimes.Current(lifetime.Key), lifetime)
            && sources.Any(source => source.Identity == lifetime.Key);
        RetireGemSourceLifetimesLocked(player, sources);
        if (matchesRetiringRf && PlanRighteousFireDetachLocked(player, expectedRf) is { } cleanup)
            QueueRighteousFireCleanupLocked(player, cleanup, "Righteous Fire source retired");
        var sourceIds = sources.Select(source => source.Id).ToHashSet();
        using (new PlayerStatScope(player, fresh: true))
            EndNonRighteousFireSkillEffectsLocked(player, sourceIds);
        using (new PlayerStatScope(player, fresh: true))
            EnsureOwnAuraPools(player);
        using (new PlayerStatScope(player, fresh: true))
        {
            RecomputePoolsAndSpeed(player);
            ReconcileBeastCompanionsLocked(player);
            var plan = PlanGolemBuffsLocked(player, new Dictionary<uint, int>(CharacterPlayerStats(player)));
            foreach (var packet in plan.Packets)
                player.Minions.OutputQueue.Add(new(packet, "golem owner state after direct source retirement", WorldId: player.Id));
        }
        SaveSummons(player);
        SaveGolems(player);
    }

    // Actual equipment writers capture the canonical rows before their inventory write. After that write and its fresh
    // stat/source rebuild, an old physical source either continues by identity or ends here. Removing its actors and
    // Golem owner state can make another old source fail its requirements (for example, by losing a Golem slot), so
    // repeat fresh qualification until no old identity becomes newly invalid. Every pass must end at least one old key;
    // therefore no unchanged read can keep this loop alive and no retired lifetime can be reactivated.
    public static void ReconcileEquipmentSourceLifetimesLocked(Player player, IReadOnlyList<PlayerSkillSource> before)
    {
        if (before.Count == 0) return;
        var pending = before.DistinctBy(source => source.Identity).ToList();
        int limit = pending.Count + 1;
        for (int pass = 0; pass < limit && pending.Count > 0; pass++)
        {
            var retiring = new List<PlayerSkillSource>();
            using (new PlayerStatScope(player, fresh: true))
            {
                var current = PlayerSkillSourcesLocked(player);
                foreach (var old in pending)
                {
                    var now = current.FirstOrDefault(source => source.Identity == old.Identity);
                    if (now is null || !now.Active || !SupportedSkill(player, now.Id).RequirementsMet)
                        retiring.Add(old);
                }
            }
            if (retiring.Count == 0) return;
            var identities = retiring.Select(source => source.Identity).ToHashSet();
            pending.RemoveAll(source => identities.Contains(source.Identity));
            if (ShaperMemories.TryGetValue(player, out var memory) && retiring.Any(source => source.WornGrant == memory.Grant))
                RetireShaperMemoryLocked(player, memory);
            RetireDirectMinionSourcesLocked(player, retiring);
        }
    }
}
