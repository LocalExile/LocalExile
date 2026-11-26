using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool IsBladeVortex(string? effect) => effect is
        "BladeVortex" or "BladeVortexAltX" or "BladeVortexAltY" or "BladeVortexRoyale" or "NewNewBladeVortex";

    public sealed partial class ProjectileSession
    {
        sealed class BladeVortexRun(ushort skill, SupportedGem source)
        {
            public readonly ushort Skill = skill;
            public readonly SupportedGem Source = source;
            public readonly PriorityQueue<ushort, long> Blades = new();
            public long NextHit, Spacing;
            public long Wake = long.MaxValue;
            public int Radius, HitRate, Damage, Critical, BaseInterval;
            public Func<bool> Current = null!;
            public Action<int, double, int> Hit = null!;
        }

        readonly Dictionary<uint, BladeVortexRun> bladeVortices = [];
        ushort nextBladeVortexTag;

        internal void ReleaseBladeVortex(uint actor, ushort skill, int artVariation, SupportedGem source,
            IReadOnlyDictionary<uint, int> caster, Func<bool> current,
            Action<int, double, int> hit)
        {
            int Own(uint key) => source.Stats.GetValueOrDefault(key);
            int Caster(uint key) => caster.GetValueOrDefault(key);
            int cap = (int)Math.Clamp((long)Own(Stat.MaximumNumberOfSpinningBlades) + Caster(Stat.MaximumNumberOfSpinningBlades), 0, 256);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Caster,
                key => Own(key) + (key == SkillDurationModifiers.IncreasedDuration
                    ? Own(Stat.BladeVortexDurationPlusPct) + Caster(Stat.BladeVortexDurationPlusPct) : 0),
                DurationKinds(Player, source.Effect));
            ushort row = BuffRows.Find("new_new_blade_vortex");
            if (cap == 0 || duration <= 0 || row == 0) return;
            if (!bladeVortices.TryGetValue(actor, out var run) || run.Skill != skill || !SameTriggerSource(run.Source, source))
            {
                ClearBladeVortex(actor, row);
                bladeVortices[actor] = run = new(skill, source);
            }

            var area = SkillAreaStats(Player, source.Stats, source.Effect, caster);
            int Get(uint stat) => area.GetValueOrDefault(stat);
            int baseRadius = Get(Stat.ActiveSkillBaseAreaOfEffectRadius);
            run.Radius = SkillArea.Radius(baseRadius > 0 ? baseRadius : 14,
                key => Get(key) + (key == Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal
                    ? Get(Stat.BladeVortexRadiusPlusPct) : 0), spell: true);

            run.HitRate = Combined(Stat.BladeVortexHitRatePlusPctPerBlade);
            run.Damage = Combined(Stat.BladeVortexDamagePlusPctPerBladeFinal);
            run.Critical = Combined(Stat.BladeVortexCriticalStrikeChancePlusPctPerBlade);
            int baseInterval = Own(Stat.BaseBladeVortexHitRateMs);
            run.BaseInterval = baseInterval > 0 ? baseInterval : 600;
            run.Current = current;
            run.Hit = hit;

            RemoveExpiredBladeVortex(actor, run, row);
            if (run.Blades.Count == 0) run.Spacing = 0;
            int extraChance = Combined(Stat.BladeVortexAdditionalBladeChancePct);
            int gained = 1 + (extraChance > 0 && (extraChance >= 100 || AreaRng(Player).Next(100) < extraChance) ? 1 : 0);
            ushort actorSkill = actor == Player.Id ? skill : (ushort)0x4000;
            (ushort Id, ushort Action)? active = SkillsFor(Player).ActionHash(source.Effect) is ushort action
                ? (actorSkill, action) : null;
            for (int i = 0; i < gained; i++)
            {
                while (run.Blades.Count >= cap) RemoveBlade();
                ushort tag = ++nextBladeVortexTag;
                if (tag == 0) tag = ++nextBladeVortexTag;
                run.Blades.Enqueue(tag, Runtime.Now + ProjectileRuntime.Time(duration));
                var buff = BuffRows.Spec(row, actor, null, duration, tag);
                // The client's blade visual dereferences the first trailing value (3.29.3c 0x1417befa0).
                Player.Area.Buffs.Set(actor, buff with
                {
                    SourceSkill = actorSkill, ActiveSkill = active, Stacks = 1,
                    Values = [(uint)Math.Max(0, artVariation)], List = [0],
                });
            }

            UpdateBladeVortexInterval(run);
            ScheduleBladeVortex(actor, run, row);

            int Combined(uint stat) => ActorScale.Saturate((long)Own(stat) + Caster(stat));

            void RemoveBlade() => Player.Area.Buffs.Clear(actor, row, actor, tag: run.Blades.Dequeue());
        }

        void RemoveExpiredBladeVortex(uint actor, BladeVortexRun run, ushort row)
        {
            while (run.Blades.TryPeek(out ushort tag, out long expiry) && expiry <= Runtime.Now)
            {
                run.Blades.Dequeue();
                Player.Area.Buffs.Clear(actor, row, actor, tag: tag);
            }
        }

        void UpdateBladeVortexInterval(BladeVortexRun run)
        {
            long old = run.Spacing;
            double rate = Math.Max(.01, 1 + (long)run.Blades.Count * run.HitRate / 100d);
            run.Spacing = Math.Max(1, ProjectileRuntime.Time(run.BaseInterval / (1000 * rate)));
            if (old == 0) run.NextHit = Runtime.Now + run.Spacing;
            else if (run.NextHit > Runtime.Now && old != run.Spacing)
                run.NextHit = Runtime.Now + (long)Math.Ceiling((run.NextHit - Runtime.Now) * (double)run.Spacing / old);
        }

        void ScheduleBladeVortex(uint actor, BladeVortexRun run, ushort row)
        {
            if (!run.Blades.TryPeek(out _, out long expiry)) return;
            long wake = Math.Min(expiry, run.NextHit);
            if (wake >= run.Wake) return;
            run.Wake = wake;
            Runtime.Schedule(wake, () =>
            {
                if (!ReferenceEquals(bladeVortices.GetValueOrDefault(actor), run) || run.Wake != wake) return;
                run.Wake = long.MaxValue;
                if (!run.Current())
                {
                    ClearBladeVortex(actor, row);
                    return;
                }
                int previous = run.Blades.Count;
                RemoveExpiredBladeVortex(actor, run, row);
                int count = run.Blades.Count;
                if (count == 0)
                {
                    bladeVortices.Remove(actor);
                    return;
                }
                if (count != previous) UpdateBladeVortexInterval(run);
                if (Runtime.Now >= run.NextHit)
                {
                    run.NextHit = Runtime.Now + run.Spacing;
                    if (!Player.Paused && run.Radius > 0)
                        run.Hit(run.Radius, Math.Max(0, 1 + (long)count * run.Damage / 100d),
                            ActorScale.Saturate((long)count * run.Critical));
                }
                ScheduleBladeVortex(actor, run, row);
            });
        }

        bool DetonateBladeVortexBlade(uint actor, BladeVortexRun run, ushort tag, int retainChance)
        {
            if (!ReferenceEquals(bladeVortices.GetValueOrDefault(actor), run) || !run.Current()
                || !run.Blades.Remove(tag, out _, out long expiry)) return false;
            ushort row = BuffRows.Find("new_new_blade_vortex");
            bool unexpired = expiry > Runtime.Now;
            if (unexpired && retainChance > 0 && (retainChance >= 100 || AreaRng(Player).Next(100) < retainChance))
            {
                run.Blades.Enqueue(tag, expiry);
                return true;
            }
            Player.Area.Buffs.Clear(actor, row, actor, tag: tag);
            RemoveExpiredBladeVortex(actor, run, row);
            if (run.Blades.Count == 0)
                bladeVortices.Remove(actor);
            else
            {
                UpdateBladeVortexInterval(run);
                ScheduleBladeVortex(actor, run, row);
            }
            return unexpired;
        }

        void ClearBladeVortex(uint actor, ushort row)
        {
            if (!bladeVortices.Remove(actor, out var run)) return;
            while (run.Blades.TryDequeue(out ushort tag, out _))
                Player.Area.Buffs.Clear(actor, row, actor, tag: tag);
        }

        void ClearBladeVortices()
        {
            if (bladeVortices.Count == 0) return;
            ushort row = BuffRows.Find("new_new_blade_vortex");
            foreach (uint actor in bladeVortices.Keys.ToArray())
                ClearBladeVortex(actor, row);
        }
    }
}
