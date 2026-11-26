using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        long flameSurgeGroundReady, combustingGroundReady;

        internal void PlaceFlameSurgeGround(SpawnedMonster target, int effect, bool combusting, double now, double dps,
            IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> caster)
        {
            if (dps <= 0 || own.GetValueOrDefault(Stat.FlameSurgeCannotCreateBurningGround) != 0) return;
            long contact = ProjectileRuntime.Time(now);
            ref long ready = ref (combusting ? ref combustingGroundReady : ref flameSurgeGroundReady);
            if (contact < ready) return;
            double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                key => caster.GetValueOrDefault(key), key => own.GetValueOrDefault(key));
            int radius = SkillRadius(Player, 25, own, spell: true, effect, caster);
            if (duration <= 0 || radius <= 0) return;
            int cooldown = combusting ? 1000 : own.GetValueOrDefault(Stat.FlameSurgeBurningGroundCreationCooldownMs);
            ready = contact + ProjectileRuntime.Time(Math.Max(0, cooldown) / 1000d);
            PlaceDamageGround(Vector(target.Walk.Position(now)), radius, duration, DamageType.Fire, dps, contact);
        }
    }
}
