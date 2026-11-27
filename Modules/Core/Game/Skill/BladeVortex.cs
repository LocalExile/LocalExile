using GameServer.Core.Projectiles;

public static partial class Game
{
    const int BladeVortexSizeReach = 16;   // the widest monster body a vortex hit looks past its radius for

    // A monster is hit when its body reaches into the vortex (radius plus its size, as the client
    // draws the blades at the full radius) and no wall stands between it and the caster.
    static bool BladeVortexReaches(Player player, (int X, int Y) origin, int radius, uint id, (int X, int Y) p) =>
        player.Area.Monsters.TryGetValue(id, out var m)
        && Dist(origin, p) <= radius + Math.Max(0, LivingCollision.Size(m))
        && MeleeArcSight(player, origin, p);

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
            // The buffs the client draws and counts (SyncBladeVortexBuffs).
            public uint Art;
            public ushort ActorSkill;
            public (ushort Id, ushort Action)? Active;
            public long CounterEnd;
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
            }
            run.Art = (uint)Math.Max(0, artVariation);
            run.ActorSkill = actorSkill;
            run.Active = active;
            SyncBladeVortexBuffs(actor, run);

            UpdateBladeVortexInterval(run);
            ScheduleBladeVortex(actor, run, row);

            int Combined(uint stat) => ActorScale.Saturate((long)Own(stat) + Caster(stat));

            void RemoveBlade() => run.Blades.Dequeue();
        }

        void RemoveExpiredBladeVortex(uint actor, BladeVortexRun run, ushort row)
        {
            int before = run.Blades.Count;
            while (run.Blades.TryPeek(out _, out long expiry) && expiry <= Runtime.Now)
                run.Blades.Dequeue();
            if (run.Blades.Count != before) SyncBladeVortexBuffs(actor, run);
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
            SyncBladeVortexBuffs(actor, run);
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
            run.Blades.Clear();
            SyncBladeVortexBuffs(actor, run);
        }

        // One new_new_blade_vortex instance for the vortex, its stacks the blade count: its NewBladeVortexBuff
        // (3.29.3c 0x1417befa0) draws one ring of blades, the skin from its first value (skill_art_variation, read by
        // 0x141cbfda0), the ring art sized to the vortex radius when its list entry is not 1, and as many blades as the
        // actor's current_number_of_spinning_blades (the row's Current_StatsKey). blade_vortex_counter is the icon: the
        // count and a countdown to the last blade's end (a changed end restarts it on a new instance; stack drops keep
        // the instance in place).
        void SyncBladeVortexBuffs(uint actor, BladeVortexRun run)
        {
            ushort row = BuffRows.Find("new_new_blade_vortex"), counter = BuffRows.Find("blade_vortex_counter");
            int count = run.Blades.Count;
            Player.Area.Stats.Set(actor, Stat.CurrentNumberOfSpinningBlades, count);
            Player.Area.Stats.Set(actor, Stat.VirtualCurrentNumberOfSpinningBlades, count);
            if (count == 0)
            {
                Player.Area.Buffs.Clear(actor, row, actor);
                if (counter != 0) Player.Area.Buffs.Clear(actor, counter, actor);
                run.CounterEnd = 0;
                return;
            }
            Player.Area.Buffs.Set(actor, BuffRows.Spec(row, actor, null) with
            {
                SourceSkill = run.ActorSkill, ActiveSkill = run.Active, Stacks = (ushort)count, Values = [run.Art], List = [0],
            });
            if (counter == 0) return;
            long last = run.Blades.UnorderedItems.Max(b => b.Priority);
            bool deadlineChanged = run.CounterEnd == 0 || last != run.CounterEnd;
            if (deadlineChanged) run.CounterEnd = last;
            double remaining = Math.Max(0, (last - Runtime.Now) / (double)ProjectileRuntime.QuantaPerSecond);
            Player.Area.Buffs.Set(actor, BuffRows.Spec(counter, actor, null, remaining) with
            {
                SourceSkill = run.ActorSkill, ActiveSkill = run.Active, Stacks = (ushort)count,
                KeepInstance = !deadlineChanged,
            });
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
