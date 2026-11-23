using GameServer.Core.Projectiles;

static partial class Game
{
    internal static bool IsFrostBlades(int effect) => Skills.EffectId(effect) == "FrostBlades";

    static ProjectileUse? ReleaseFrostBlades(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll critical, ushort sequence,
        int repeat, bool cryExerted, double ruthless)
    {
        double now = ProjectileWorldTime(player);
        var stats = SupportedSkill(player, skillId).Stats;
        int extraReach = stats.GetValueOrDefault(Stat.MeleeWeaponRangePlus);
        uint id = LightningStrikeTarget(player, from, aim, targetId, now, extraReach);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var target)) return null;

        var at = target.Walk.Position(now);
        var origin = ProjectileSession.Vector(at);
        var excluded = session.ActorKey(id, target);
        double more = MeleeRepeatDamageMore(player, skillId, repeat);
        bool hit = false;
        var melee = AreaSkillHitForUse(ruthless, player, skillId, gem, from, at, immediate: true, sequence: sequence,
            criticalRoll: critical, targetId: id, singleStrikeTarget: id, onHit: _ => hit = true,
            radiusOverride: WeaponFor(player).Range + extraReach + target.StopRange, damageMore: more,
            scheduledSession: session, cryExerted: cryExerted, repeat: repeat);
        if (!melee.IsCompleted) throw new InvalidOperationException("Scheduled Frost Blades contact must not yield.");
        melee.GetAwaiter().GetResult();
        if (!hit) return null;

        // The blades start at the accepted melee contact and aim outward within the gem's native spread.
        var use = CaptureGemProjectile(player, session, skillId, gem, at, critical, sequence, cryExerted: cryExerted, from: at);
        var shot = (ProjectileOffense)use.Payload!;
        var forward = (origin - ProjectileSession.Vector(from)).Unit;
        double heading = Math.Atan2(forward.Y, forward.X);
        double half = Math.Clamp(stats.GetValueOrDefault(Stat.TotalProjectileSpreadAngleOverride), 0, 360) * Math.PI / 360;
        double Offset(ProjectileVector point)
        {
            var delta = point - origin;
            return Math.IEEERemainder(Math.Atan2(delta.Y, delta.X) - heading, Math.Tau);
        }
        var angles = player.Area.MonstersNear(at, use.Profile.Range)
            .Where(pair => pair.Key != id && pair.Value is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false })
            .Select(pair => ProjectileSession.Vector(pair.Value.Walk.Position(now)))
            .Where(point => (point - origin).Length <= use.Profile.Range && Math.Abs(Offset(point)) <= half)
            .OrderBy(point => (point - origin).Length)
            .Select(Offset).Take(use.Profile.Count).ToList();
        int free = use.Profile.Count - angles.Count;
        for (int i = 0; i < free; i++)
            angles.Add(free == 1 ? 0 : -half + 2 * half * i / (free - 1));

        return use with
        {
            Origin = origin,
            Aim = origin + forward,
            Profile = use.Profile with { NativeVolley = new() { Angles = [.. angles] } },
            Payload = shot with
            {
                ExcludedTarget = excluded,
                Hit = shot.Hit.Scale(more),
                Ignite = RepeatAilment(shot.Ignite, more),
                Bleed = RepeatAilment(shot.Bleed, more),
                Poison = RepeatAilment(shot.Poison, more),
            },
        };
    }
}
