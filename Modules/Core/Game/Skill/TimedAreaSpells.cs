using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool TimedAreaSpellEffect(string? effect) =>
        CorpseDamageSpell(effect) || CremationEffect(effect) || BladefallEffect(effect)
        || FrostBombEffect(effect) || effect == "BallLightningAltY";

    public sealed partial class ProjectileSession
    {
        internal void ScheduleTimedAreaSpell(
            (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) aim, uint target, double windup, ushort sequence, Func<bool> commit,
            SkillCriticalRoll? critical, SacrificeUse sacrifice, double more, int repeat)
        {
            var intensity = CastingIntensity(Player, gem.Id);
            var source = SupportedSkill(Player, gem.Id);
            string? effect = SkillsFor(Player).EffectId(gem.Effect);
            var lifetime = CombatLifetime;
            var sourceLifetime = CaptureGemSourceLifetimeLocked(Player, gem.Id, gem.Effect);
            bool Current() => Active && ReferenceEquals(lifetime, CombatLifetime) && Player.PlayerLife > 0
                && sourceLifetime is not null && Player.GemSourceLifetimes.IsCurrent(sourceLifetime)
                && sacrifice.Current && SameTriggerSource(source, SupportedSkill(Player, gem.Id));
            long release = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player))) + ProjectileRuntime.Time(windup);
            Runtime.Schedule(release, () =>
            {
                if (!Current() || !commit()) return;
                double now = ProjectileWorldTime(Player);
                if (VolatileDeadEffect(effect))
                {
                    ReleaseVolatileDead(gem.Id, aim, now, Current, (at, radius, time) => Hit(at, radius, time), more: more);
                    return;
                }

                if (CremationEffect(effect))
                {
                    ReleaseCremation(gem.Id, aim, target, effect == "CremationAltY", now, Current,
                        (at, radius, time) => Hit(at, radius, time), more);
                    return;
                }
                if (FrostBombEffect(effect))
                {
                    ReleaseFrostBomb(gem.Effect, source.Stats, aim, Current, (at, radius, time) => Hit(at, radius, time));
                    return;
                }
                if (effect == "BallLightningAltY")
                {
                    ReleaseStaticBall(gem.Effect, source.Stats, aim, Current, (at, radius, time) => Hit(at, radius, time));
                    return;
                }
                if (BladefallEffect(effect))
                {
                    ReleaseBladefall(gem.Id, gem.Effect, source.Stats, Player.Walk.Position(now), aim, Current, Hit);
                    return;
                }

                bool scavenging = effect == "DetonateDeadAltX";
                bool vaal = effect == "VaalDetonateDead";
                int limit = vaal ? 22 : effect == "DetonateDeadAltY" ? 8 : 1;
                int radius = SkillRadius(Player, source.Stats, spell: true, gem.Effect);
                bool Usable(SpawnedMonster m) => IsCorpse(m) && m.Hostile && !m.Ailments.Shattered
                    && (!scavenging || !m.CreatedAsCorpse);

                // An explicit target is tied to that corpse. A removed corpse cannot redirect a paid cast.
                if (target != 0)
                {
                    if (Player.Area.Monsters.TryGetValue(target, out var direct) && Usable(direct))
                        Detonate(new(target, direct), now, 0);
                }
                else
                    DetonateNearest(now, 0);

                void DetonateNearest(double at, int index)
                {
                    var next = PlacedSpellCorpses(aim, PlacedCorpseSearchRadius, at).FirstOrDefault(entry => Usable(entry.Value));
                    if (next.Value is not null) Detonate(next, at, index);
                }

                void Detonate(KeyValuePair<uint, SpawnedMonster> corpse, double at, int index)
                {
                    var pos = corpse.Value.Walk.Position(at);
                    ConsumeSpellCorpse(corpse.Key, corpse.Value, at);
                    Effect(pos.X, pos.Y, DetonateDeadArt, ProjectileRuntime.Time(.75), radius);
                    if (!scavenging) Hit(pos, radius, at);
                    ApplyCorpseExplosion(gem.Id, corpse.Value.MaxLife, pos, radius, at, more: more);
                    if (index + 1 >= limit || !Current()) return;
                    double gap = vaal ? .16 + .055 * index : Math.Max(.04, .22 - .023 * index);
                    Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(gap), () =>
                    {
                        if (Current()) DetonateNearest(ProjectileWorldTime(Player), index + 1);
                    });
                }
            });

            void Hit((int X, int Y) at, int radius, double atTime, double stageMore = 1, int extraCriticalIncrease = 0)
            {
                using var scope = intensity is null ? null : new IntensityScope(intensity);
                _ = AreaSkillHitForUse(null, Player, gem.Id, gem, at, at, immediate: true, sequence: sequence,
                    radiusOverride: radius, criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: Current,
                    scheduledSession: this, repeat: repeat, damageMore: more * stageMore,
                    extraCriticalIncrease: extraCriticalIncrease);
            }
        }
    }
}
