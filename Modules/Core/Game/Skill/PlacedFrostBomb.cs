using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const ushort FrostBombDebuffRow = 473;
    sealed record FrostBombDebuff(double Started, double Until, int Regeneration);
    static readonly RuleTable<SpawnedMonster, FrostBombDebuff> FrostBombDebuffs = new();

    static double FrostBombRegenerationFactor(SpawnedMonster monster, double from, double to)
    {
        if (!FrostBombDebuffs.TryGetValue(monster, out var active)) return 1;
        if (from >= active.Until)
        {
            FrostBombDebuffs.Remove(monster);
            return 1;
        }
        double overlap = Math.Max(0, Math.Min(to, active.Until) - Math.Max(from, active.Started));
        return 1 - overlap / (to - from) * Math.Clamp(-active.Regeneration / 100d, 0, 1);
    }

    public sealed partial class ProjectileSession
    {
        internal void SchedulePlacedFrostBomb(PlacedAreaHit placement, Func<bool> current)
        {
            var mine = placement.Device;
            long start = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player)));
            Runtime.Schedule(start, () =>
            {
                if (!Active || !current()) return;
                var caster = CharacterPlayerStats(Player);
                int Own(uint key) => mine.Own.GetValueOrDefault(key);
                int Source(uint key) => caster.GetValueOrDefault(key);
                int Get(uint key) => ActorScale.Add(key, Own(key), Source(key));
                double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Source, Own);
                double debuff = SkillDurationModifiers.Seconds(Own(Stat.BaseSecondarySkillEffectDuration), Source, Own);
                if (duration <= 0) return;
                int radius = SkillRadius(Player, SkillArea.BaseRadius("FrostBomb", Own), mine.Own,
                    spell: true, mine.Effect, caster);
                int exposure = InflictedExposure(Get, DamageType.Cold, Own(Stat.BaseColdDamageResistancePct));
                int recovery = Own(Stat.LifeRegenerationRatePlusPct);
                var centre = mine.At;
                long end = Runtime.Now + ProjectileRuntime.Time(duration);
                Effect(centre.X, centre.Y, "Metadata/Effects/Spells/frost_bomb/ice_crystal_loop.ao",
                    ProjectileRuntime.Time(duration));
                // Pulses carry the gem's secondary duration. The hit occurs only at the end of the crystal's life.
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
                if (debuff > 0) Pulse();
                Runtime.Schedule(end, () =>
                {
                    if (!Active || !current()) return;
                    Effect(centre.X, centre.Y, "Metadata/Effects/Spells/frost_bomb/ice_discharge.ao",
                        ProjectileRuntime.Time(1.5), radius);
                    placement.Apply(Player, centre, radius, ProjectileWorldTime(Player));
                });
            });
        }
    }
}
