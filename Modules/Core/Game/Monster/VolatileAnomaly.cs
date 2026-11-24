using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string VolatileAnomalyFamily = "VolatileAnomaly";
    const int VolatileAnomalyDetonation = 8759;
    // The native orb's approach is a proximity trigger, independent of the explosion and Vortex radii.
    const int VolatileAnomalyContactRadius = 5;

    static readonly SummonCatalog.Definition ItemVolatileAnomaly = new("VolatileAnomaly", VolatileAnomalyFamily,
        "Metadata/Monsters/Anomaly/AnomalyUniqueRing", 0, 0, Follows: false, Targetable: false, LimitWithoutStat: int.MaxValue);

    static void VolatileAnomalyStepLocked(Player player, GolemState.Golem orb, double now)
    {
        var at = orb.Monster.Walk.Position(now);
        const int seekRange = 105;
        var nearest = player.Area.MonstersNear(at, seekRange)
            .Where(pair => pair.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false })
            .Select(pair => (pair.Key, At: pair.Value.Walk.Position(now)))
            .Where(candidate => Dist(candidate.At, at) <= seekRange)
            .OrderBy(candidate => Dist(candidate.At, at)).ThenBy(candidate => candidate.Key).FirstOrDefault();
        if (nearest.Key == 0) return;
        if (Dist(nearest.At, at) <= VolatileAnomalyContactRadius)
        {
            ReleaseVolatileAnomalyLocked(player, orb, now);
            RemoveSummonLocked(player, orb, now);
            return;
        }
        GolemMoveLocked(player, orb, nearest.At, now);
    }

    static void ReleaseVolatileAnomalyLocked(Player player, GolemState.Golem orb, double now)
    {
        var gem = SupportedSkill(player, orb.Source);
        if (!gem.RequirementsMet || gem.WornGrant is not { } grant || TriggeredItemMinion(grant) != ItemVolatileAnomaly) return;
        var catalog = SkillsFor(player);
        var native = catalog.EffectStats(VolatileAnomalyDetonation, 1).ToDictionary(p => p.Item1, p => p.Item2);
        var stats = MergeSkillStats(CharacterPlayerStats(player), gem.Stats);
        int Get(uint stat) => GolemStat(stats, stat);
        int Own(uint stat) => GolemStat(gem.Stats, stat);
        int blast = SkillArea.Radius(native.GetValueOrDefault(Stat.ActiveSkillBaseAreaOfEffectRadius), Get, spell: true);
        int radius = SkillArea.Radius(native.GetValueOrDefault(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), Get, spell: true);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSecondarySkillEffectDuration), Get, Own,
            DurationKinds(player, gem.Effect) | SkillDurationModifiers.Kinds.Secondary);
        double dps = ColdSpellDamageOverTime(gem.Stats, Get);
        int epoch = player.ProjectileEpoch;
        bool Current() => player.ProjectileActive(epoch) && player.PlayerLife > 0;
        var at = orb.Monster.Walk.Position(now);
        var session = ProjectilesFor(player);
        if (catalog.ActionHash(VolatileAnomalyDetonation) is ushort hash)
            session.World(orb.Id, InstanceProtocol.ActionStarted(orb.Id, at.X, at.Y, at.X, at.Y, (ushort)(hash & 0x3fff),
                0xffff, player.MonsterOrderFlags));
        session.PlaceDamageGround(ProjectileSession.Vector(at), radius, seconds, DamageType.Cold, dps, session.Runtime.Now,
            Own(Stat.GroundMaelstromArtVariation), stats);
        session.Enqueue(() => AreaSkillHit(player, orb.Source, (orb.Source, gem.Effect, gem.Name ?? ItemVolatileAnomaly.Id,
            gem.Level, gem.Quality, 0), at, at, immediate: true, melee: false, radiusOverride: blast, triggeredCurrent: Current));
    }
}
