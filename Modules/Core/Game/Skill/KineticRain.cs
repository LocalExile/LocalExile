using GameServer.Core.Projectiles;

public static partial class Game
{
    const string KineticAnomalyEffect = "KineticRainKineticInstability";
    const string RainWandProjectile = "Metadata/Projectiles/WandProjectile";
    const string KineticRainMarker = "Metadata/Effects/Spells/kinetic_rain/runic_marker.ao";
    const string KineticRainFall = "Metadata/Effects/Spells/kinetic_rain/projectile.ao";
    const string KineticRainOrb = "Metadata/Effects/Spells/kinetic_rain/orb.ao";
    static readonly ProjectileSkillDefinition GroundWandRain = new(RainWandProjectile,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(RainWandProjectile, get, speed, attack: true)));

    static bool IsKineticRain(string? id) => id is "KineticRain" or "KineticRainAltX";

    static void ReleaseKineticRain(ProjectileSession session, ProjectileUse use,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        string effect, (int X, int Y) aim, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, SkillCriticalRoll critical, ushort sequence)
    {
        var player = session.Player;
        int reach = SkillRadius(player, own.GetValueOrDefault(Stat.ActiveSkillBaseAreaOfEffectRadius), own, false, gem.Effect, playerStats);
        int halfWidth = SkillRadius(player, own.GetValueOrDefault(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), own, false, gem.Effect, playerStats);
        int radius = SkillRadius(player, own.GetValueOrDefault(Stat.ActiveSkillBaseTertiaryAreaOfEffectRadius), own, false, gem.Effect, playerStats);
        if (reach <= 0 || halfWidth <= 0 || radius <= 0) return;

        var target = ProjectileSession.Vector(aim);
        var direction = (target - use.Origin).Unit;
        var across = new ProjectileVector(-direction.Y, direction.X);
        var areas = session.ActorsNear(use.Origin, Math.Max(reach, halfWidth), session.Runtime.Now)
            .Where(actor => actor.Team != use.Team && actor.Key != use.Owner)
            .Select(actor => actor.Position(session.Runtime.Now))
            .Where(point => WithinRain(point) && session.TraceTerrain(use.Origin, point) is null)
            .OrderBy(point => (point - target).Length)
            .Take(use.Profile.Count).ToList();
        bool WithinRain(ProjectileVector point)
        {
            var delta = point - use.Origin;
            double forward = delta.Dot(direction) / reach;
            double side = delta.Dot(across) / halfWidth;
            return forward >= 0 && forward * forward + side * side <= 1;
        }

        var random = AreaRng(player);
        // Give each target a landing before dispersing the other projectiles.
        for (int attempts = 0; areas.Count < use.Profile.Count && attempts < use.Profile.Count * 3; attempts++)
        {
            double angle = (random.NextDouble() - .5) * Math.PI;
            double distance = Math.Sqrt(random.NextDouble());
            var point = use.Origin + direction * (Math.Cos(angle) * reach * distance)
                + across * (Math.Sin(angle) * halfWidth * distance);
            if (session.TraceTerrain(use.Origin, point) is null) areas.Add(point);
        }

        ProjectileUse? anomaly = null;
        double anomalyDuration = 0;
        int anomalyRadius = 0, anomalyLimit = 0;
        if (effect == "KineticRain")
        {
            var child = GemSkillEffects(player).FirstOrDefault(g => g.Gem == gem.Gem &&
                SkillsFor(player).EffectId(g.Effect) == KineticAnomalyEffect);
            if (child.Name is not null)
            {
                var stats = SupportedSkill(player, child.Id).Stats;
                anomalyDuration = SkillDurationModifiers.Seconds(stats.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                    key => playerStats.GetValueOrDefault(key), key => stats.GetValueOrDefault(key));
                anomalyRadius = SkillRadius(player, stats, spell: false, child.Effect);
                anomalyLimit = stats.GetValueOrDefault(Stat.KineticInstabilityMaximumNumberOfInstabilityOrbsAllowed);
                if (anomalyDuration > 0 && anomalyRadius > 0 && anomalyLimit > 0)
                {
                    anomaly = CaptureGemProjectile(player, session, child.Id, child, aim, critical, sequence);
                    var offense = (ProjectileOffense)anomaly.Payload!;
                    anomaly = anomaly with { Payload = offense with { AreaVisual = null, MirageSource = null, CriticalTrigger = null } };
                }
            }
        }

        // Use the ground-volley scheduler's contact lead and spacing until the wand animation is measured.
        const double travel = .35, spacing = .04;
        for (int i = 0; i < areas.Count; i++)
        {
            var position = areas[i];
            double flight = travel + spacing * i;
            session.RainArt(position, KineticRainMarker, flight, clip: 0);
            session.Runtime.Schedule(session.Runtime.Now + ProjectileRuntime.Time(flight - .2), () =>
            {
                if (session.Active && player.PlayerLife > 0) session.RainArt(position, KineticRainFall, .35, clip: 0);
            });
            session.Runtime.Schedule(session.Runtime.Now + ProjectileRuntime.Time(flight), () =>
            {
                if (!session.Active || player.PlayerLife == 0) return;
                session.Runtime.Impact(use, position, new(effect, radius));
                if (anomaly is not null) session.PlantKineticAnomaly(anomaly, position, anomalyRadius, anomalyDuration, anomalyLimit);
            });
        }
    }

    public sealed partial class ProjectileSession
    {
        sealed record KineticAnomaly(ProjectileUse Use, ProjectileVector At, int Radius, long Until, Action Remove);
        List<KineticAnomaly>? kineticAnomalies;

        internal void PlantKineticAnomaly(ProjectileUse use, ProjectileVector at, int radius, double duration, int limit)
        {
            var orbs = kineticAnomalies ??= [];
            bool idle = orbs.Count == 0;
            while (orbs.Count >= limit)
            {
                orbs[0].Remove();
                orbs.RemoveAt(0);
            }
            long lifetime = ProjectileRuntime.Time(duration);
            Action remove = () => { };
            Effect((int)Math.Round(at.X), (int)Math.Round(at.Y), KineticRainOrb, lifetime, radius,
                startClip: 0, captureRemoval: retire => remove = retire);
            orbs.Add(new(use, at, radius, Runtime.Now + lifetime, remove));
            if (idle) Runtime.Schedule(Runtime.Now + ProjectileRuntime.TickQuanta, CheckKineticAnomalies);
        }

        void CheckKineticAnomalies()
        {
            if (kineticAnomalies is not { } orbs) return;
            bool canTrigger = Active && Player.PlayerLife > 0;
            long now = Runtime.Now;
            for (int i = 0; i < orbs.Count;)
            {
                var orb = orbs[i];
                bool active = canTrigger && now < orb.Until;
                bool hit = active && HasKineticAnomalyTarget(orb, now);
                if (active && !hit) { i++; continue; }
                orbs.RemoveAt(i);
                orb.Remove();
                if (hit) Runtime.Impact(orb.Use, orb.At, new(KineticAnomalyEffect, orb.Radius));
            }
            if (orbs.Count > 0) Runtime.Schedule(now + ProjectileRuntime.TickQuanta, CheckKineticAnomalies);
        }

        bool HasKineticAnomalyTarget(KineticAnomaly orb, long now)
        {
            var area = Player.Area;
            double at = now / (double)ProjectileRuntime.QuantaPerSecond;
            var centre = orb.At;
            int radius = orb.Radius;
            if (area.Driven)
            {
                nearActorIds.Clear();
                area.Grid.Near(((int)Math.Round(centre.X), (int)Math.Round(centre.Y)),
                    radius + GameArea.WalkSlack + 1, nearActorIds);
                foreach (var id in nearActorIds)
                    if (area.Monsters.TryGetValue(id.Value, out var monster) && CanSetOff(monster)) return true;
            }
            else
                foreach (var (_, monster) in area.SyncLivingMonsters())
                    if (CanSetOff(monster)) return true;
            return false;

            bool CanSetOff(SpawnedMonster monster)
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                    || ArenaHazards.Untargetable(Player, monster)) return false;
                var delta = Vector(monster.Walk.Position(at)) - centre;
                return delta.Dot(delta) <= (double)radius * radius;
            }
        }
    }
}
