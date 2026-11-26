using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool ShockNovaEffect(string? effect) => effect is "ShockNova" or "ShockNovaAltX" or "ShockNovaRoyale";

    static int ShockNovaProcessionRepeats(Player player, int effect, int level, int quality) =>
        SkillsFor(player).EffectId(effect) == "ShockNovaAltX"
            ? Math.Clamp(SkillsFor(player).EffectStats(effect, level, quality)
                .FirstOrDefault(s => s.Item1 == Stat.BaseSpellRepeatCount).Item2, 0, 8)
            : 0;

    readonly record struct ShockNovaPulse((int X, int Y) Centre, int Radius, int InnerRadius,
        double DamageMore, int ExtraShockChance)
    {
        public bool OutsideInnerRadius((int X, int Y) at) => InnerRadius == 0 || Dist(at, Centre) >= InnerRadius;
    }

    public sealed partial class ProjectileSession
    {
        void ScheduleShockNova(int effect, int level, int quality, (int X, int Y) from, (int X, int Y) aim,
            IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> caster, Func<bool> current,
            Action<ShockNovaPulse> contact)
        {
            var direction = GroundSlamWave.Direction(from, aim);
            if (direction == default) direction = (1, 0);
            int Radius(int baseRadius) => SkillRadius(Player, baseRadius, own, spell: true, effect, caster);
            int radius = Radius(26), ringOuter = Radius(20), ringInner = Radius(13);
            int total = ShockNovaProcessionRepeats(Player, effect, level, quality) + 1;
            int ringShockChance = own.GetValueOrDefault(Stat.ShockNovaRingChanceToShockPlusPct);
            double ringDamage = Math.Max(0, 1 + own.GetValueOrDefault(Stat.NewshocknovaFirstRingDamagePlusPctFinal) / 100d);
            // The geometry and spacing are local estimates. Spell Echo is handled separately from the intrinsic repeats.
            long start = Runtime.Now;
            for (int pulse = 0; pulse < total; pulse++)
            {
                double offset = pulse * ringOuter;
                var centre = (from.X + (int)Math.Round(offset * direction.X), from.Y + (int)Math.Round(offset * direction.Y));
                long due = start + ProjectileRuntime.Time(pulse * .16);
                ScheduleContact(due, current, () => contact(new(centre, ringOuter, ringInner, ringDamage, ringShockChance)));
                ScheduleContact(due + ProjectileRuntime.Time(.045), current, () => contact(new(centre, radius, 0, 1, 0)));
            }
        }

        internal void ReleaseTotemShockNova(TotemState.Totem totem, (int X, int Y) aim, ushort sequence, Func<bool> current)
        {
            var skill = SupportedSkill(Player, totem.SkillId);
            ScheduleShockNova(skill.Effect, skill.Level, skill.Quality, totem.At, aim, skill.Stats,
                TotemCasterStats(CharacterPlayerStats(Player)), current,
                pulse => TotemNovaLocked(Player, this, totem, pulse.Centre, pulse.Radius, sequence,
                    covers: pulse.OutsideInnerRadius, damageMore: pulse.DamageMore, extraShockChance: pulse.ExtraShockChance));
        }

        internal void ReleasePlacedShockNova(PlacedSkills.Placed placed, (int X, int Y) aim, double damageMore, Func<bool> current)
        {
            var skill = SupportedSkill(Player, placed.Skill);
            ScheduleShockNova(placed.Effect, skill.Level, skill.Quality, placed.At, aim, placed.Own,
                placed.Offense?.Caster ?? CharacterPlayerStats(Player), current, pulse =>
                    PlacedAreaDamageLocked(Player, placed, pulse.Centre, pulse.Radius, ProjectileWorldTime(Player),
                        damageMore * pulse.DamageMore, currentSkill: true, spellDamage: true, shockNova: new(this, pulse)));
        }
    }

    sealed record PlacedShockNova(ProjectileSession Session, ShockNovaPulse Pulse);
}
