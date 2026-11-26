using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort SoulrendBuffRow = 1266;
    const string SoulrendPulseStage = "soulrend-debuff";
    const int SoulrendRadius = 10; // Legion balance notes, shared by the normal and spiral projectile.
    const int SoulrendSeekRadius = 30;
    const ushort SoulrendAction = 4702;
    const byte SoulrendCoordinatorDetach = 255;
    const long SoulrendTargetInterval = ProjectileRuntime.QuantaPerSecond / 5;
    const long SoulrendTurnInterval = ProjectileRuntime.QuantaPerSecond / 10;
    static readonly RuleTable<GameArea, Dictionary<uint, AreaChaosDot>> SoulrendDots = new();

    static bool HasSoulrendDamage(GameArea area) => SoulrendDots.TryGetValue(area, out var dots) && dots.Count > 0;

    static ProjectileUse SoulrendRelease(ProjectileReleaseContext context, ProjectileUse use, ProjectileOffense shot,
        Func<uint, int> areaStat)
    {
        var player = context.Player;
        use = use with { Profile = use.Profile with { Steering = SoulrendSteering(context.Session) } };
        var get = context.Get;
        var own = context.Own;
        var placed = PlacedKind(SupportedSkill(player, context.SkillId));
        var owner = CharacterPlayerStats(player);
        var caster = shot.ActualCaster ?? (placed is (true, _) or (_, true) ? TotemCasterStats(owner) : owner);
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ChaosDamagePlusPct)
            + get(Stat.DamageOverTimePlusPct) + get(Stat.ChaosDamageOverTimePlusPct)
            + get(Stat.DamageOverTimePlusPctWithSpells) + get(Stat.SoulrendDamagePlusPct);
        if (own(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += get(Stat.SpellDamagePlusPct);
        if (placed.Trap || placed.Mine)
            increased += (double)get(placed.Mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct) + get(Stat.TrapOrMineDamagePlusPct);
        double more = shot.HitProfileMore;
        foreach (uint stat in EssenceDrainSupportMore) more *= Math.Max(0, 1 + own(stat) / 100d);
        if (placed.Trap || placed.Mine)
            more *= Math.Max(0, 1 + own(Stat.SupportTrapAndMineDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + own(placed.Mine ? Stat.SupportRemoteMine2DamagePlusPctFinal : Stat.SupportTrapDamagePlusPctFinal) / 100d);
        double multiplier = 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ChaosDotMultiplierPlus)
            + get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100d;
        double dps = own(Stat.BaseChaosDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d)
            * more * Math.Max(0, multiplier);
        double seconds = SkillDurationModifiers.Seconds(own(Stat.BaseSkillEffectDuration), context.PlayerStat, own);
        if (DamageStats.Prohibited(DamageType.Chaos, get)) return use;
        var damage = new SkillDamageContext(SkillHitKind.Spell, caster).OverTime(new(DamageType.Chaos, dps, seconds));
        int radius = SkillArea.Radius(SoulrendRadius, areaStat, spell: true);
        if (damage.DamagePerSecond <= 0 || seconds <= 0 || radius <= 0) return use;

        const long interval = ProjectileRuntime.TickQuanta;
        return use with
        {
            Profile = use.Profile with
            {
                Pulse = new(SoulrendPulseStage, radius, interval)
                {
                    HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Interval, interval),
                },
            },
            Payload = shot with { Soulrend = new(damage.DamagePerSecond, seconds, radius, Caster: player) },
        };
    }

    static ProjectileSteeringRecipe SoulrendSteering(ProjectileSession session)
    {
        var acquired = new HashSet<long>();
        return new(SoulrendTargetInterval, SoulrendTurnInterval, Math.PI / 9, (flight, at) =>
        {
            bool native = session.SoulrendCoordinator(flight, out ushort coordinator);
            var direction = SoulrendTargetDirection(session, flight, at);
            if (!native) return direction;
            if (direction is null && !acquired.Remove(flight.Id)) return null;
            if (direction is not null) acquired.Add(flight.Id);

            // The client keeps its previous target direction. Align it with the current path when the target is lost.
            var aim = direction ?? flight.Direction;
            double angle = Math.Atan2(aim.X, -aim.Y);
            if (angle < 0) angle += Math.Tau;
            byte bearing = (byte)Math.Round(angle * 255 / Math.Tau);
            session.SendSoulrendDirection(flight, coordinator, bearing, firstSample: at == flight.CreatedAt);
            var offset = new ProjectileVector(0, -30).Rotate(Math.Tau * bearing / 255);
            return new ProjectileVector(Math.Round(offset.X), Math.Round(offset.Y)).Unit;
        });
    }

    static ProjectileVector? SoulrendTargetDirection(ProjectileSession session, ProjectileEntity flight, long at)
    {
        double reach = Math.Min(SoulrendSeekRadius, flight.Remaining);
        double best = reach * reach;
        ProjectileVector? aim = null;
        foreach (var actor in session.ActorsNear(flight.Position, reach, at))
        {
            if (actor.Team == flight.Use.Team || actor.Key == flight.Use.Owner) continue;
            var target = actor.Position(at);
            var offset = target - flight.Position;
            double distance = offset.Dot(offset);
            if (distance >= best || flight.Direction.Dot(offset) <= 0
                || session.TraceTerrain(flight.Position, target) is not null) continue;
            aim = offset;
            best = distance;
        }
        return aim;
    }

    public sealed partial class ProjectileSession
    {
        readonly Dictionary<ushort, ushort> soulrendCoordinators = [];

        internal bool SoulrendCoordinator(ProjectileEntity flight, out ushort sequence)
        {
            sequence = 0;
            if (flight.ParentId != 0 || flight.VolleyIndex >= SoulrendCoordinatorDetach - 2 || flight.Use.Emitter.Id != Player.Id
                || flight.Use.Payload is not ProjectileOffense { ClientPredicted: true } shot) return false;
            soulrendCoordinators.TryAdd(shot.SourceSkill, flight.Use.Sequence);
            sequence = soulrendCoordinators[shot.SourceSkill];
            return true;
        }

        internal void SendSoulrendDirection(ProjectileEntity flight, ushort coordinator, byte direction, bool firstSample)
        {
            if (flight.Use.Payload is not ProjectileOffense shot) return;
            // 3.29.3c Soulrend ActionUpdate reader (0x141522ad0): one cast group containing one detached flight.
            var data = new BlobWriter().U16(1).U16(flight.Use.Sequence).U8(1).U8((byte)(2 + flight.VolleyIndex)).U8(direction).ToArray();
            void Send() => WorldRecords(Player.Id, () => [InstanceProtocol.ActionUpdate(Player.Id, SoulrendAction, shot.SourceSkill,
                coordinator, data, detachId: SoulrendCoordinatorDetach)],
                ServerTrace.Projectiles ? $"Soulrend direction cast={flight.Use.Sequence} slot={flight.VolleyIndex} bearing={direction}" : null);
            // Predictive can receive the first direction before its detached flight has joined the cast group.
            if (firstSample && !Player.Connection.LockstepClient) Runtime.Schedule(Runtime.Now + ProjectileRuntime.TickQuanta, Send);
            else Send();
        }
    }

    static ProjectileHitResult ApplySoulrendDamage(Player player, uint id, SpawnedMonster target,
        MonsterContagion.Cast cast, double now)
    {
        if (!target.Hostile || target.OwnerId is not null || target.Invulnerable || target.Effects[Stat.BaseCannotBeDamaged] != 0
            || cast.DamagePerSecond <= 0) return ProjectileHitResult.Miss;
        var dots = SoulrendDots.GetOrCreateValue(player.Area);
        if (!dots.TryGetValue(id, out var entry) || !ReferenceEquals(entry.Monster, target))
            dots[id] = entry = new(target, new());
        var dot = entry.Damage;
        var previous = dot.ApplicationAt(now)?.Cast;
        dot.Apply(now, cast, trackContagion: false, target.RecipientDebuffRate);
        if (dot.Buff is null || !ReferenceEquals(previous, dot.ApplicationAt(now)?.Cast))
            PresentSoulrendDamage(player, id, dot, now);
        return ProjectileHitResult.Hit;
    }

    static void PresentSoulrendDamage(Player player, uint id, MonsterContagion dot, double now)
    {
        if (dot.ApplicationAt(now) is not { } active)
        {
            EndDot(player, id, ref dot.Buff, dot.BuffSource);
            return;
        }
        // Row 1266 is not IsRecovery, so its wire record has no inline stat values.
        // The flight refreshes the server timer every pulse and expiry removes the visual.
        ShowDot(player, id, ref dot.Buff, ref dot.BuffSource, new(SoulrendBuffRow, active.Cast.Caster?.Id ?? 0));
    }

    static void RetimeSoulrendDamage(GameArea area, uint id, SpawnedMonster monster, double now, double oldRate, double rate)
    {
        if (SoulrendDots.TryGetValue(area, out var dots) && dots.TryGetValue(id, out var state) && state.Monster == monster)
            state.Damage.RetimeExpiry(now, oldRate, rate);
    }
}
