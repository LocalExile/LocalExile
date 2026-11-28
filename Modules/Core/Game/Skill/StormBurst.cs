using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string StormBurstOrbArt = "Metadata/Effects/Spells/storm_burst/stormburst.ao";
    const int StormBurstJumpRadius = 16;
    const int StormBurstExplosionRadius = 22;
    const int StormBurstSpawnOffset = 18;
    const int StormBurstJumpDistance = 12;
    const int StormBurstOrbLimit = 64;

    internal sealed class StormBurstChannel(int action, int interruption, ushort sequence, int effect,
        GemSourceLifetimes.SourceLifetime source, bool repel, long duration, long interval,
        int radius, int explosionRadius, int damagePerJump, int extraOrbChance, int focus)
    {
        public readonly int Action = action, Interruption = interruption, Effect = effect;
        public readonly ushort Sequence = sequence;
        public readonly GemSourceLifetimes.SourceLifetime Source = source;
        public readonly bool Repel = repel;
        public readonly long Duration = duration, Interval = interval;
        public readonly int Radius = radius, ExplosionRadius = explosionRadius, DamagePerJump = damagePerJump;
        public readonly int ExtraOrbChance = extraOrbChance, Focus = focus;
        public readonly List<StormBurstOrb> Orbs = [];
        public long? ReleasedAt;
        public int Emitted;
    }

    internal sealed class StormBurstOrb(int number, (int X, int Y) position, ProjectileVector outward, long expires,
        SkillCriticalRoll critical, double more)
    {
        public readonly int Number = number;
        public (int X, int Y) Position = position;
        public readonly ProjectileVector Outward = outward;
        public readonly long Expires = expires;
        public readonly SkillCriticalRoll Critical = critical;
        public readonly double More = more;
        public Action? Remove;
        public Action<int, int, float>? Move;
    }

    static bool IsStormBurst(string? effect) => effect is "StormBurst" or "StormBurstAltX" or "StormBurstAltY";

    static StormBurstChannel? BeginStormBurst(Player player, ushort skill, ushort sequence, SupportedGem gem, int action)
    {
        var source = CaptureGemSourceLifetimeLocked(player, skill, gem.Effect);
        if (source is null) return null;
        var caster = CharacterPlayerStats(player);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        var area = SkillAreaStats(player, gem.Stats, gem.Effect);
        int Area(uint key, uint phase) => area.GetValueOrDefault(key) + (key == Stat.SkillAreaOfEffectPlusPct
            ? area.GetValueOrDefault(Stat.StormBurstAreaOfEffectPlusPct) + area.GetValueOrDefault(phase) : 0);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), caster.GetValueOrDefault, Own,
            DurationKinds(player, gem.Effect));
        double interval = Own(Stat.DisplayStormBurstJumpTimeMs) / 1000d;
        if (duration <= 0 || interval <= 0) return null;
        return new(action, player.ActionInterruptionGeneration, sequence, gem.Effect, source,
            SkillsFor(player).EffectId(gem.Effect) == "StormBurstAltX",
            ProjectileRuntime.Time(duration), ProjectileRuntime.Time(interval),
            SkillArea.Radius(StormBurstJumpRadius, key => Area(key, Stat.StormBurstZapAreaOfEffectPlusPct), spell: true),
            SkillArea.Radius(StormBurstExplosionRadius, key => Area(key, Stat.StormBurstExplosionAreaOfEffectPlusPct), spell: true),
            Own(Stat.StormBurstNewDamagePlusPctFinalPerRemainingTeleportZap),
            area.GetValueOrDefault(Stat.StormBurstAdditionalObjectChancePct)
                + area.GetValueOrDefault(Stat.VirtualStormBurstAdditionalObjectChancePct),
            Own(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));
    }

    static bool StormBurstHolding(Player player, AimedPulseChannel channel, StormBurstChannel burst, double now) =>
        burst.ReleasedAt is null && burst.Interruption == player.ActionInterruptionGeneration
        && ChannelCurrent(player, channel.Skill, burst.Action, channel.Generation, now)
        && !PlayerActionBlocked(player, now, now);

    static void PulseStormBurst(Player player, AimedPulseChannel channel, StormBurstChannel burst,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, double elapsed, double pact)
    {
        using var mutation = ProjectileMutation(player);
        if (StormBurstHolding(player, channel, burst, AreaTime(player))
            && GemSourceLifetimeCurrentLocked(player, channel.Skill, burst.Effect, burst.Source))
            ProjectilesFor(player).AddStormBurstOrbs(channel, burst, gem, pact * FocusedChannelMore(elapsed, burst.Focus));
    }

    static void ReleaseStormBurstLocked(Player player, double now)
    {
        if (ActiveAimedPulseChannel(player) is not { StormBurst: { Orbs.Count: > 0 } burst } channel
            || !StormBurstHolding(player, channel, burst, now)
            || player.ProjectileRuntime is not ProjectileSession { Active: true } session) return;
        long at = ProjectileRuntime.Time(now);
        burst.ReleasedAt = at;
        session.ScheduleContact(at, () => true, () => session.DetonateStormBurst(channel, burst));
    }

    static (int X, int Y) StormBurstCell(Player player, ProjectileVector desired, (int X, int Y) fallback)
    {
        var cell = ((int)Math.Round(desired.X), (int)Math.Round(desired.Y));
        return player.Layout.WalkableFor(cell.Item1, cell.Item2, 0) && !player.Barrier.Contains(cell)
            && (player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(fallback, cell, sight, 1)) ? cell : fallback;
    }

    public sealed partial class ProjectileSession
    {
        internal void AddStormBurstOrbs(AimedPulseChannel channel, StormBurstChannel burst,
            (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, double more)
        {
            var start = Player.Walk.Position(ProjectileWorldTime(Player));
            var aim = StormBurstCell(Player, Vector(channel.Aim), start);
            var direction = Vector(aim) - Vector(start);
            var outward = direction.Length > 0 ? direction.Unit * -1 : new ProjectileVector(-1, 0);
            var rng = AreaRng(Player);
            int count = RandomRolls.Chance(burst.ExtraOrbChance, 100, rng) ? 2 : 1;
            for (int i = 0; i < count; i++)
            {
                if (burst.Orbs.Count == StormBurstOrbLimit) RemoveStormBurstOrb(burst, burst.Orbs[0]);
                var centre = Vector(aim) + outward * Math.Min(StormBurstSpawnOffset, direction.Length)
                    + new ProjectileVector(-outward.Y, outward.X) * rng.Next(-6, 7);
                var cell = StormBurstCell(Player, centre, aim);
                var orb = new StormBurstOrb(++burst.Emitted, cell, outward, Runtime.Now + burst.Duration,
                    CaptureSkillCriticalRoll(Player, channel.Skill), more);
                burst.Orbs.Add(orb);
                Effect(cell.X, cell.Y, StormBurstOrbArt, burst.Duration,
                    captureRemoval: remove => orb.Remove = remove, captureMovement: move => orb.Move = move);
                StormBurstHit(channel, burst, gem, orb, burst.Radius, 1, "spawn");
                ScheduleStormBurstLeap(channel, burst, gem, orb);
            }
        }

        void ScheduleStormBurstLeap(AimedPulseChannel channel, StormBurstChannel burst,
            (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, StormBurstOrb orb)
        {
            long jump = Runtime.Now + burst.Interval;
            ScheduleContact(Math.Min(jump, orb.Expires), () => burst.ReleasedAt is null, () =>
            {
                if (!burst.Orbs.Contains(orb)) return;
                if (jump <= orb.Expires && StormBurstHolding(Player, channel, burst, ProjectileWorldTime(Player))
                    && GemSourceLifetimeCurrentLocked(Player, channel.Skill, burst.Effect, burst.Source))
                {
                    var toward = Vector(channel.Aim) - Vector(orb.Position);
                    var direction = burst.Repel ? (toward.Length > 0 ? toward.Unit * -1 : orb.Outward) : toward.Unit;
                    var step = burst.Repel ? StormBurstJumpDistance : Math.Min(toward.Length, StormBurstJumpDistance);
                    orb.Position = StormBurstCell(Player, Vector(orb.Position) + direction * step, orb.Position);
                    orb.Move?.Invoke(orb.Position.X, orb.Position.Y, 0);
                    StormBurstHit(channel, burst, gem, orb, burst.Radius, 1, "jump");
                    if (Runtime.Now < orb.Expires)
                    {
                        ScheduleStormBurstLeap(channel, burst, gem, orb);
                        return;
                    }
                }
                RemoveStormBurstOrb(burst, orb);
            });
        }

        internal void DetonateStormBurst(AimedPulseChannel channel, StormBurstChannel burst)
        {
            long at = burst.ReleasedAt!.Value;
            var gem = GemSkillEffects(Player).FirstOrDefault(g => g.Id == channel.Skill && g.Effect == burst.Effect);
            bool valid = Player.PlayerLife > 0 && gem.Name is not null
                && GemSourceLifetimeCurrentLocked(Player, channel.Skill, burst.Effect, burst.Source)
                && GemSkillInstanceDisabled(Player, burst.Effect, channel.Skill) is null;
            foreach (var orb in burst.Orbs)
            {
                orb.Remove?.Invoke();
                if (!valid || at >= orb.Expires) continue;
                long remainingJumps = (orb.Expires - at) / burst.Interval;
                double scale = Math.Max(1, burst.DamagePerJump * remainingJumps / 100d);
                StormBurstHit(channel, burst, gem, orb, burst.ExplosionRadius, scale, "release");
            }
            burst.Orbs.Clear();
        }

        void StormBurstHit(AimedPulseChannel channel, StormBurstChannel burst,
            (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, StormBurstOrb orb, int radius,
            double scale, string phase)
        {
            var at = orb.Position;
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[gateway] {Player.Connection.Remote} {gem.Name} orb {orb.Number} {phase} at ({at.X}, {at.Y})"
                    + $" aim=({channel.Aim.X}, {channel.Aim.Y}) radius={radius} scale={scale:0.##} t={ProjectileWorldTime(Player):0.000}");
            var hit = AreaSkillHitForUse(null, Player, channel.Skill, gem, at, at, immediate: true,
                sequence: burst.Sequence, criticalRoll: orb.Critical, radiusOverride: radius,
                scheduledSession: this, triggeredCurrent: () => Active && Player.PlayerLife > 0,
                hitAndAilmentMore: orb.More * scale, nearbyOnly: true,
                targetFilter: (id, p) => Player.Area.Monsters.TryGetValue(id, out var monster) && !monster.WorldHidden
                    && (Player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(at, p, sight, 1)));
            if (!hit.IsCompleted) throw new InvalidOperationException("Storm Burst contact must not yield.");
            hit.GetAwaiter().GetResult();
        }

        static void RemoveStormBurstOrb(StormBurstChannel burst, StormBurstOrb orb)
        {
            burst.Orbs.Remove(orb);
            orb.Remove?.Invoke();
        }
    }
}
