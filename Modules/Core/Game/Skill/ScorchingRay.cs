using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort ScorchingRayBuffRow = 762;
    const double RayBaseLength = 60;
    const double RayHalfWidth = 3;

    sealed class ScorchingRayArea
    {
        public readonly Dictionary<Player, RayBeam> Beams = [];
        public readonly Dictionary<uint, SpawnedMonster> Targets = [];
        public readonly List<KeyValuePair<uint, SpawnedMonster>> Pending = [];
    }

    sealed class RayBeam(Player caster, AimedPulseChannel channel, int action, double angle, double now)
    {
        public readonly Player Caster = caster;
        public readonly AimedPulseChannel Channel = channel;
        public readonly int Action = action, Interruption = caster.ActionInterruptionGeneration;
        public double Angle = angle, UpdatedAt = now;
        public double Length, TurnSpeed, Dps, Duration, ExtraStage;
        public int Pulse, Maximum, Exposure;
    }

    sealed class RayBurn(double now)
    {
        public readonly Dictionary<Player, RayMark> Marks = [];
        public double Cursor = now, Carry;
        public Player? DamageOwner;
        public uint ShownSource;
        public int ShownStages;

        public void Accrue(SpawnedMonster target, double now)
        {
            if (now <= Cursor) return;
            double factor = MonsterDotTaken(target, DamageType.Fire, Cursor);
            while (Cursor < now && Marks.Count > 0)
            {
                double end = now, best = 0;
                foreach (var mark in Marks.Values)
                {
                    if (mark.Until <= Cursor) continue;
                    end = Math.Min(end, mark.Until);
                    if (mark.Dps <= best) continue;
                    best = mark.Dps;
                    DamageOwner = mark.Beam.Caster;
                }
                Carry += best * factor * (end - Cursor);
                Cursor = end;
            }
            Cursor = now;
            foreach (var (caster, mark) in Marks)
                if (mark.Until <= now) Marks.Remove(caster);
        }
    }

    sealed class RayMark(RayBeam beam)
    {
        public RayBeam Beam = beam;
        public int Pulse, Stages;
        public double Until, Dps;
    }

    static readonly RuleTable<GameArea, ScorchingRayArea> ScorchingRayAreas = new();
    static readonly RuleTable<SpawnedMonster, RayBurn> ScorchingRayBurns = new();

    static bool IsScorchingRay(string? effect) => effect is "ScorchingRay" or "ScorchingRayAltX" or "ScorchingRayAltY"
        or "ScorchingRayDivineUnique";

    static bool HasScorchingRayDamage(GameArea area) =>
        ScorchingRayAreas.TryGetValue(area, out var rays) && rays.Targets.Count != 0;

    static void PayScorchingRayPulse(Player player, AimedPulseChannel channel, int effect, int action,
        double interval, double elapsed, double pact)
    {
        var own = SupportedSkill(player, channel.Skill).Stats;
        var playerStats = CharacterPlayerStats(player);
        var stats = MergeSkillStats(playerStats, own);
        int Get(uint stat) => stats.GetValueOrDefault(stat);
        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.FireDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.FireDamageOverTimePlusPct) + Get(Stat.BurnDamagePlusPct)
            + Get(Stat.FireBeamDamagePlusPct) + Get(Stat.DamageOverTimePlusPctWithSpells);
        if (Get(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += Get(Stat.SpellDamagePlusPct);
        double more = DamageStats.ElementalMore(Get(Stat.CombinedElementalDamagePlusPctFinal));
        foreach (uint key in SearingBondDamageMore)
            more *= Math.Max(0, 1 + own.GetValueOrDefault(key) / 100d);
        more *= pact * FocusedChannelMore(elapsed, Get(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));
        double multiplier = Math.Max(0, 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.FireDotMultiplierPlus)
            + Get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100d);
        double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            playerStats.GetValueOrDefault, own.GetValueOrDefault);
        double dps = own.GetValueOrDefault(Stat.BaseFireDamageToDealPerMinute) / 60d
            * Math.Max(0, 1 + increased / 100d) * more * multiplier;
        dps = new SkillDamageContext(SkillHitKind.Spell, stats).OverTime(new(DamageType.Fire, dps, duration)).DamagePerSecond;
        int exposure = own.GetValueOrDefault(Stat.BaseFireDamageResistancePct);
        double unmodified = SkillsFor(player).CastMs(effect) / 1000d;

        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            if (!ChannelCurrent(player, channel.Skill, action, channel.Generation) || PlayerActionBlocked(player, now, now))
                return;
            var area = ScorchingRayAreas.GetOrCreateValue(player.Area);
            if (!area.Beams.TryGetValue(player, out var beam) || !ReferenceEquals(beam.Channel, channel))
            {
                var direction = channel.DirectionFrom(player.Walk.Position(now));
                area.Beams[player] = beam = new(player, channel, action, Math.Atan2(direction.Y, direction.X), now);
            }

            beam.Pulse++;
            beam.Length = RayBaseLength * Math.Max(0, 1 + Get(Stat.FireBeamLengthPlusPct) / 100d);
            beam.TurnSpeed = Math.Tau * Math.Clamp(unmodified / interval, 0, 2.5)
                * Math.Max(0, 1 + Get(Stat.FireBeamRotationSpeedPlusPct) / 100d);
            beam.Dps = dps;
            beam.Duration = duration;
            beam.Maximum = Math.Max(1, own.GetValueOrDefault(Stat.DisplayMaxFireBeamStacks));
            beam.ExtraStage = Math.Max(0, 1 + own.GetValueOrDefault(Stat.FireBeamAdditionalStackDamagePlusPctFinal) / 100d);
            beam.Exposure = exposure < 0 ? InflictedExposure(Get, DamageType.Fire, exposure) : 0;
            AdvanceRayBeam(area, beam, now);
        }
    }

    internal static void AdvanceScorchingRays(GameArea area)
    {
        if (!ScorchingRayAreas.TryGetValue(area, out var rays) || rays.Beams.Count == 0) return;
        foreach (var (player, beam) in rays.Beams)
        {
            if (!ReferenceEquals(player.Area, area) || beam.Interruption != player.ActionInterruptionGeneration
                || !ChannelCurrent(player, beam.Channel.Skill, beam.Action, beam.Channel.Generation))
            {
                rays.Beams.Remove(player);
                continue;
            }
            if (player.Paused || PlayerActionBlocked(player, area.Time, area.Time)) continue;
            AdvanceRayBeam(rays, beam, area.Time);
        }
    }

    static void AdvanceRayBeam(ScorchingRayArea area, RayBeam beam, double now)
    {
        if (beam.Length <= 0 || beam.Duration <= 0 || beam.Dps <= 0) return;
        var player = beam.Caster;
        var origin = player.Walk.Position(now);
        var direction = beam.Channel.DirectionFrom(origin);
        double desired = Math.Atan2(direction.Y, direction.X);
        double turn = Math.IEEERemainder(desired - beam.Angle, Math.Tau);
        double limit = beam.TurnSpeed * Math.Max(0, now - beam.UpdatedAt);
        beam.Angle += Math.Clamp(turn, -limit, limit);
        beam.UpdatedAt = Math.Max(beam.UpdatedAt, now);
        var (sin, cos) = Math.SinCos(beam.Angle);
        var middle = (origin.X + cos * beam.Length / 2, origin.Y + sin * beam.Length / 2);
        foreach (var (id, target) in player.Area.MonstersNear(middle, beam.Length / 2 + RayHalfWidth))
        {
            if (target is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, OwnerId: null, WorldHidden: false }
                || ArenaHazards.Untargetable(player, target) || target.Effects[Stat.BaseCannotBeDamaged] != 0) continue;
            var at = target.Walk.Position(now);
            double dx = at.X - origin.X, dy = at.Y - origin.Y;
            double along = dx * cos + dy * sin;
            if (along < 0 || along > beam.Length || Math.Abs(dx * sin - dy * cos) > RayHalfWidth) continue;
            if (player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(origin, at, sight, 1)) continue;

            area.Targets[id] = target;
            var burn = ScorchingRayBurns.GetValue(target, _ => new(now));
            burn.Accrue(target, now);
            if (!burn.Marks.TryGetValue(player, out var mark))
                burn.Marks[player] = mark = new(beam);
            if (mark.Until <= now) mark.Stages = 0;
            if (!ReferenceEquals(mark.Beam, beam) || mark.Pulse != beam.Pulse || mark.Stages == 0)
            {
                mark.Beam = beam;
                mark.Pulse = beam.Pulse;
                mark.Stages = Math.Min(beam.Maximum, mark.Stages + 1);
                if (mark.Stages == beam.Maximum && beam.Exposure < 0 && target.Effects[Stat.ImmuneToExposure] == 0)
                    Expose(target, 0, beam.Exposure, now);
                if (ServerTrace.Skills)
                    Console.WriteLine($"[gateway] Scorching Ray stage target 0x{id:x} stages={mark.Stages} pulse={beam.Pulse} now={now:F3}");
            }
            mark.Dps = beam.Dps * (1 + (mark.Stages - 1) * beam.ExtraStage);
            mark.Until = now + beam.Duration / Math.Max(.25, target.RecipientDebuffRate);
            PresentScorchingRay(player.Area, id, burn, now);
        }
    }

    static void PresentScorchingRay(GameArea area, uint id, RayBurn burn, double now, bool living = true)
    {
        int stages = 0;
        uint source = 0;
        if (living)
            foreach (var mark in burn.Marks.Values)
                if (mark.Until > now && mark.Stages > stages)
                    (stages, source) = (mark.Stages, mark.Beam.Caster.Id);

        if (stages == burn.ShownStages && source == burn.ShownSource) return;
        if (burn.ShownStages != 0 && burn.ShownSource != source)
            area.Buffs.Clear(id, ScorchingRayBuffRow, burn.ShownSource);
        if (stages > 0)
            area.Buffs.Set(id, new(ScorchingRayBuffRow, source, Stacks: (ushort)stages, KeepInstance: true));
        (burn.ShownStages, burn.ShownSource) = (stages, source);
    }

    static void RetimeScorchingRay(SpawnedMonster target, double now, double oldRate, double rate)
    {
        if (!ScorchingRayBurns.TryGetValue(target, out var burn)) return;
        burn.Accrue(target, now);
        double scale = Math.Max(.25, oldRate) / Math.Max(.25, rate);
        foreach (var mark in burn.Marks.Values)
            if (mark.Until > now) mark.Until = now + (mark.Until - now) * scale;
    }
}
