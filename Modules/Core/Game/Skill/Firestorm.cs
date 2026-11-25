using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool IsFirestorm(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "Firestorm" or "FirestormAltX" or "FirestormAltY";

    public sealed partial class ProjectileSession
    {
        sealed class FirestormRun
        {
            public bool Replaced;
            public Action? RemoveArt;
        }
        readonly List<FirestormRun> firestorms = [];
        const int FirestormSpreadRadius = 25;
        // Meteors has no periodic interval in its data; use the base storm's first landing delay.
        const double FirestormSingleMeteorDelay = .15;
        const string FirestormStormArt = "Metadata/Effects/Spells/meteor_shower/meteor_shower.ao";
        const string FirestormImpactArt = "Metadata/Effects/Spells/meteor_shower/meteor_aoe.ao";
        const string FirestormSmallImpactArt = "Metadata/Effects/Spells/meteor_shower/meteor_aoe_shards.ao";

        internal void ScheduleFirestorm((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) aim, double windup, Func<bool> current, SacrificeUse sacrifice, SkillCriticalRoll? critical,
            int repeat = 0, double damageMore = 1, SpawnedMonster? brandedTarget = null, PlacedAreaHit? placedHit = null,
            bool triggered = false)
        {
            if (!Active || !current()) return;
            var source = SupportedSkill(Player, gem.Id);
            long release = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player))) + ProjectileRuntime.Time(windup);
            Runtime.Schedule(release, () =>
            {
                if (!Active || !current() || !sacrifice.Current || !SameTriggerSource(source, SupportedSkill(Player, gem.Id))) return;
                var playerStats = CharacterPlayerStats(Player);
                int Own(uint key) => source.Stats.GetValueOrDefault(key);
                int Caster(uint key) => playerStats.GetValueOrDefault(key);
                int radius = SkillRadius(Player, source.Stats, spell: true, gem.Effect);
                string? effect = SkillsFor(Player).EffectId(gem.Effect);
                bool emitArt = triggered || placedHit is not null;
                if (effect == "FirestormAltX")
                {
                    Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(FirestormSingleMeteorDelay), () =>
                    {
                        if (CanImpact()) ImpactAt(aim, radius, damageMore, large: true);
                    });
                    return;
                }
                double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Caster, Own)
                    * Math.Max(0, 1 + Caster(Stat.FirestormDurationPlusPct) / 100d);
                double interval = Own(Stat.FireStormFireballDelayMs) / 1000d * Math.Max(.01,
                    1 + (Own(Stat.FireStormFireballDelayPlusPct) + Caster(Stat.FireStormFireballDelayPlusPct)) / 100d);
                int limit = Own(Stat.FirestormMaxNumberOfStorms);
                if (duration <= 0 || interval <= 0 || limit <= 0) return;
                int spread = SkillRadius(Player, FirestormSpreadRadius, source.Stats, spell: true, gem.Effect, playerStats);
                int firstRadius = (int)Math.Round(radius * SkillArea.RadiusMultiplier(Own(Stat.FirestormInitialImpactAreaOfEffectPlusPctFinal)));
                double firstDamage = Math.Max(0, 1 + Own(Stat.FirestormInitialImpactDamagePlusPctFinal) / 100d);
                var run = new FirestormRun();
                while (firestorms.Count >= limit)
                {
                    firestorms[0].Replaced = true;
                    firestorms[0].RemoveArt?.Invoke();
                    firestorms.RemoveAt(0);
                }
                firestorms.Add(run);
                long end = Runtime.Now + ProjectileRuntime.Time(duration);
                long spacing = Math.Max(1, ProjectileRuntime.Time(interval));
                if (emitArt)
                    Effect(aim.X, aim.Y, FirestormStormArt, ProjectileRuntime.Time(duration), spread,
                        captureRemoval: remove => run.RemoveArt = remove);
                ScheduleImpact(Runtime.Now + spacing, large: effect == "Firestorm");
                Runtime.Schedule(end, () => firestorms.Remove(run));

                bool CanImpact() => Active && Player.PlayerLife > 0 && sacrifice.Current && (placedHit is null || current());
                void ImpactAt((int X, int Y) at, int hitRadius, double impactMore, bool large)
                {
                    if (emitArt)
                        Effect(at.X, at.Y, large ? FirestormImpactArt : FirestormSmallImpactArt,
                            ProjectileRuntime.Time(.4), hitRadius);
                    if (placedHit is { } proxy)
                        proxy.Apply(Player, at, hitRadius, ProjectileWorldTime(Player), impactMore);
                    else Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, at, at, immediate: true, repeat: repeat,
                        damageMore: impactMore, radiusOverride: hitRadius, criticalRoll: critical, sacrifice: sacrifice,
                        triggeredCurrent: CanImpact, brandedTarget: brandedTarget));
                }

                void ScheduleImpact(long due, bool large)
                {
                    if (due > end) return;
                    Runtime.Schedule(due, () =>
                    {
                        if (run.Replaced || !CanImpact()) return;
                        ImpactAt(large ? aim : Impact(), large ? firstRadius : radius,
                            damageMore * (large ? firstDamage : 1), large);
                        ScheduleImpact(due + spacing, large: false);
                    });
                }

                (int X, int Y) Impact()
                {
                    var rng = AreaRng(Player);
                    double angle = rng.NextDouble() * Math.Tau;
                    double distance = Math.Sqrt(rng.NextDouble()) * spread;
                    return (aim.X + (int)Math.Round(Math.Cos(angle) * distance), aim.Y + (int)Math.Round(Math.Sin(angle) * distance));
                }
            });
        }
    }
}
