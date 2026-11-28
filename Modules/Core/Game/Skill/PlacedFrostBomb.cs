using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string ForthcomingFrostBomb = "FrostBombAltY";
    static bool FrostBombEffect(string? effect) => effect is "FrostBomb" or "FrostBombAltX" or ForthcomingFrostBomb or "FrostBombRoyale";
    const ushort FrostBombDebuffRow = 473;
    sealed record FrostBombDebuff(double Started, double Until, int Regeneration);
    static readonly RuleTable<SpawnedMonster, FrostBombDebuff> FrostBombDebuffs = new();
    internal readonly record struct FrostBombImpact((int X, int Y) Centre, int Radius, double At, double HitMore, double AilmentMore);

    public sealed partial class ProjectileSession
    {
        internal void SchedulePlacedFrostBomb(PlacedAreaHit placement, Func<bool> current)
        {
            var mine = placement.Device;
            long start = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player)));
            Runtime.Schedule(start, () =>
            {
                if (Active && current())
                    ReleaseFrostBomb(mine.Effect, mine.Own, mine.At, current,
                        impact => placement.Apply(Player, impact.Centre, impact.Radius, impact.At, impact.HitMore, impact.AilmentMore));
            });
        }

        internal void ReleaseFrostBomb(int effect, IReadOnlyDictionary<uint, int> own, (int X, int Y) centre,
            Func<bool> current, Action<FrostBombImpact> hit)
        {
            var caster = CharacterPlayerStats(Player);
            int Own(uint key) => own.GetValueOrDefault(key);
            int Source(uint key) => caster.GetValueOrDefault(key);
            int Get(uint key) => ActorScale.Add(key, Own(key), Source(key));
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Source, Own);
            double debuff = SkillDurationModifiers.Seconds(Own(Stat.BaseSecondarySkillEffectDuration), Source, Own);
            if (duration <= 0) return;
            double steps = Math.Floor(duration * 10 + 1e-9);
            double More(uint key) => Math.Max(0, 1 + Own(key) * steps / 100);
            double hitMore = More(Stat.ActiveSkillHitDamagePlusPctFinalPer100msDuration);
            double ailmentMore = More(Stat.ActiveSkillAilmentDamagePlusPctFinalPer100msDuration);
            int radius = SkillRadius(Player, own, spell: true, effect);
            int exposure = InflictedExposure(Get, DamageType.Cold, Own(Stat.BaseColdDamageResistancePct));
            int recovery = Own(Stat.LifeRegenerationRatePlusPct);
            long end = Runtime.Now + ProjectileRuntime.Time(duration);
            Effect(centre.X, centre.Y, "Metadata/Effects/Spells/frost_bomb/ice_crystal_loop.ao",
                ProjectileRuntime.Time(duration));
            // Only crystals with a secondary debuff pulse before their explosion.
            if (debuff > 0) Pulse();
            Runtime.Schedule(end, () =>
            {
                if (!Active || !current()) return;
                Effect(centre.X, centre.Y, "Metadata/Effects/Spells/frost_bomb/ice_discharge.ao",
                    ProjectileRuntime.Time(1.5), radius);
                hit(new(centre, radius, ProjectileWorldTime(Player), hitMore, ailmentMore));
            });

            void Pulse()
            {
                if (!Active || !current()) return;
                double now = ProjectileWorldTime(Player);
                Effect(centre.X, centre.Y, "Metadata/Effects/Spells/frost_bomb/frostpulse.ao",
                    ProjectileRuntime.Time(.35), radius);
                foreach (var (id, monster) in Player.Area.MonstersNear(centre, radius))
                {
                    if (monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Invulnerable: false }
                        || Dist(monster.Walk.Position(now), centre) > radius) continue;
                    if (exposure < 0 && monster.Mods.Effects[Stat.ImmuneToExposure] == 0)
                        Expose(monster, 1, exposure, now, debuff);
                    double began = FrostBombDebuffs.TryGetValue(monster, out var old) && old.Until > now
                        ? old.Started : now;
                    FrostBombDebuffs.AddOrUpdate(monster, new(began, now + debuff, recovery));
                    Player.Area.Buffs.Set(id, BuffRows.Spec(FrostBombDebuffRow, 0,
                        new Dictionary<uint, int> { [Stat.LifeRegenerationRatePlusPct] = recovery }, debuff));
                }
                long next = Runtime.Now + ProjectileRuntime.Time(.5);
                if (next < end) Runtime.Schedule(next, Pulse);
            }
        }
    }
}
