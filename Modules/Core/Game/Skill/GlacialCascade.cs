using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    enum CascadeBurst { None, Early, Final }

    sealed class CascadeKnockback
    {
        public double ReadyAt;
    }

    static readonly RuleTable<SpawnedMonster, CascadeKnockback> CascadeKnockbacks = new();

    static bool GlacialCascadeEffect(string? effect) => effect is "GlacialCascade" or "GlacialCascadeAltX" or "GlacialCascadeRoyale";
    static bool GlacialCascadeHasFinalBurst(string? effect) => effect is "GlacialCascade" or "GlacialCascadeRoyale";

    // The number of bursts and travel speed come from the skill data. These collision values are local estimates.
    const int CascadeBaseSpacing = 12;
    const double CascadeInterval = .045;

    // Knockback points along the cascade rather than radially away from an individual burst.
    static (int X, int Y) CascadeKnockbackOrigin((int X, int Y) target, (double X, double Y) direction) =>
        (target.X - (int)Math.Round(100 * direction.X), target.Y - (int)Math.Round(100 * direction.Y));

    readonly record struct CascadePulse((int X, int Y) Centre, int Radius, (double X, double Y) Direction,
        double DamageMore, CascadeBurst Burst)
    {
        public (int X, int Y) HitOrigin((int X, int Y) target, (int X, int Y) caster) =>
            Burst == CascadeBurst.Early ? CascadeKnockbackOrigin(target, Direction) : caster;
    }

    sealed record PlacedCascade(ProjectileSession Session, CascadePulse Pulse);

    public sealed partial class ProjectileSession
    {
        void ScheduleGlacialCascade(string effect, (int X, int Y) from, (int X, int Y) aim,
            IReadOnlyDictionary<uint, int> own, Func<int> skillRadius, Func<bool> current, Action<CascadePulse> contact)
        {
            int spikes = Math.Clamp(own.GetValueOrDefault(Stat.UpheavalNumberOfSpikes)
                + own.GetValueOrDefault(Stat.GlacialCascadeNumberOfAdditionalBursts), 0, 32);
            double interval = CascadeInterval / Math.Max(.1, 1 + own.GetValueOrDefault(Stat.GlacialCascadeTravelSpeedPlusPct) / 100d);
            bool hasFinal = GlacialCascadeHasFinalBurst(effect);
            double finalMore = Math.Max(0, 1 + own.GetValueOrDefault(Stat.GlacialCascadeFinalSpikeDamagePlusPctFinal) / 100d);
            var direction = GroundSlamWave.Direction(from, aim);
            if (direction == default) direction = (1, 0);
            long start = Runtime.Now;
            for (int i = 0; i < spikes; i++)
            {
                double distance = (i + 1) * CascadeBaseSpacing;
                var centre = (from.X + (int)Math.Round(distance * direction.X), from.Y + (int)Math.Round(distance * direction.Y));
                bool final = hasFinal && i == spikes - 1;
                CascadeBurst burst = final ? CascadeBurst.Final : hasFinal ? CascadeBurst.Early : CascadeBurst.None;
                ScheduleContact(start + ProjectileRuntime.Time(i * interval), current, () =>
                {
                    // Each burst reads its own radius without shifting its centre.
                    int radius = skillRadius();
                    if (final) radius = ActorScale.Saturate(2L * radius);
                    contact(new(centre, radius, direction, final ? finalMore : 1, burst));
                });
            }
        }

        internal void ReleaseTotemGlacialCascade(string effect, TotemState.Totem totem, (int X, int Y) aim,
            ushort sequence, Func<bool> current)
        {
            var skill = SupportedSkill(Player, totem.SkillId);
            ScheduleGlacialCascade(effect, totem.At, aim, skill.Stats,
                () => SkillRadius(Player, skill.Stats, spell: true, skill.Effect), current,
                pulse => TotemNovaLocked(Player, this, totem, pulse.Centre, pulse.Radius, sequence,
                    damageMore: pulse.DamageMore, cascade: pulse));
        }

        internal void ReleasePlacedGlacialCascade(string effect, PlacedSkills.Placed placed, (int X, int Y) aim,
            double damageMore, Func<bool> current)
        {
            int baseRadius = SkillArea.BaseRadius(effect, key => placed.Own.GetValueOrDefault(key));
            ScheduleGlacialCascade(effect, placed.At, aim, placed.Own,
                () => SkillRadius(Player, baseRadius, placed.Own, spell: true, placed.Effect, placed.Offense?.Caster), current,
                pulse => PlacedAreaDamageLocked(Player, placed, pulse.Centre, pulse.Radius, ProjectileWorldTime(Player),
                    damageMore * pulse.DamageMore, currentSkill: true, spellDamage: true, cascade: new(this, pulse)));
        }
    }
}
