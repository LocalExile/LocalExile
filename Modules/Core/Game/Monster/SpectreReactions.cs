using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const int MeatsackCausticCloud = 5687;
    const int EldritchEyeBeam = 7605;
    const int EldritchEyeShockExplosion = 7606;

    static void SpectreDamagedLocked(Player player, GolemState.Golem spectre, double now, Random random)
    {
        if (spectre.Definition.Kind != "Spectre"
            || spectre.Abilities.FirstOrDefault(a => a.Effect == MeatsackCausticCloud) is not { } cloud
            || !GolemReady(spectre, cloud, now)
            || !RandomRolls.Chance(GolemStat(cloud.Stats, Stat.CastOnAnyDamageTakenPct), 100, random)) return;
        double seconds = GolemStat(cloud.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        var (dps, unscaledDps, increase) = GolemHazardDamage(player, spectre, now,
            GolemStat(cloud.Stats, Stat.BaseChaosDamageToDealPerMinute) / 60d, area: true);
        if (seconds <= 0 || dps <= 0 || cloud.Radius <= 0) return;
        GolemStartCooldown(spectre, cloud, now);
        var at = spectre.Monster.Walk.Position(now);
        uint? objectId = null;
        if (GroundKindOf("CausticCloud") is { } kind)
        {
            uint id = player.TakeObjectId();
            objectId = id;
            float scale = kind.ArtCells > 0 ? (float)(cloud.Radius / kind.ArtCells) : 1;
            var ground = new GroundEffectObject(at.X, at.Y, kind.ArtRow, kind.Art, (float)seconds) { Scale = scale, Team = 0 };
            player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(GroundEffectObject.TypePath),
                ground.Blob()), null, WorldId: id, CreatesObject: true));
            player.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
        }
        double more = DoedreSewers.MinionDamageFactor(player) * MapMinionDamageFactor(player);
        spectre.Hazards.Add(new(cloud.Effect, at, now + seconds, cloud.Radius, dps * more, DamageType.Chaos,
            UnitDamagePerSecond: unscaledDps * more, NormalDamageIncrease: increase) { LastAt = now, Object = objectId });
    }

    static void SpectreShockExplosionLocked(Player player, GolemState.Golem spectre, GolemState.Ability hit,
        SpawnedMonster target, (int X, int Y) centre, double now)
    {
        if (hit.Effect != EldritchEyeBeam || spectre.Definition.Kind != "Spectre"
            || spectre.Abilities.FirstOrDefault(a => a.Effect == EldritchEyeShockExplosion) is not { } burst
            || !GolemReady(spectre, burst, now)
            || !target.Ailments.RemoveActive(MonsterElementalAilments.Kind.Shock, now)) return;
        GolemStartCooldown(spectre, burst, now);
        target.PresentationChanged?.Invoke();
        foreach (var (id, enemy) in player.Area.Monsters.ToArray())
            if (enemy.Hostile && !enemy.Dead
                && Dist(enemy.Walk.Position(now), centre) <= burst.Radius + LivingCollision.Size(enemy))
                GolemHitLocked(player, spectre, burst, id, enemy, now);
    }
}
