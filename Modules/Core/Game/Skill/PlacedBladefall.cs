using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool BladefallEffect(string? effect) => effect is "Bladefall" or "BladefallAltX" or "BladefallAltY";

    public sealed partial class ProjectileSession
    {
        BladefallTrarthusState? placedBladefallBlades;
        const string BladefallVolleyArt = "Metadata/Effects/Spells/rain_of_blade_02/bladefall.ao";
        const double BladefallVolleyInterval = .1;
        const int BladefallVolleyGap = 8;

        internal void SchedulePlacedBladefall(PlacedAreaHit placement, (int X, int Y) aim, Func<bool> current)
        {
            var device = placement.Device;
            Runtime.Schedule(Runtime.Now, () =>
            {
                if (!Active || !current()) return;
                bool spellImpale = SkillsFor(Player).EffectId(device.Effect) == "BladefallAltY";
                ReleaseBladefall(device.Skill, device.Effect, device.Own, device.At, aim, current,
                    (at, radius, now, more, criticalIncrease) =>
                        PlacedAreaDamageLocked(Player, device, at, radius, now, placement.DamageMore * more,
                            currentSkill: true, spellDamage: true, extraCriticalIncrease: criticalIncrease, spellImpale: spellImpale));
            });
        }

        internal void ReleaseTotemBladefall(TotemState.Totem totem, (int X, int Y) aim, ushort sequence, Func<bool> current)
        {
            var source = SupportedSkill(Player, totem.SkillId);
            bool impales = SkillsFor(Player).EffectId(source.Effect) == "BladefallAltY";
            ReleaseBladefall(totem.SkillId, source.Effect, source.Stats, totem.At, aim, current,
                (at, radius, _, more, criticalIncrease) => TotemNovaLocked(Player, this, totem, at, radius, sequence,
                    damageMore: more, spellImpale: impales, extraCriticalIncrease: criticalIncrease));
        }

        void ReleaseBladefall(ushort skill, int effect, IReadOnlyDictionary<uint, int> own,
            (int X, int Y) from, (int X, int Y) at, Func<bool> current, Action<(int X, int Y), int, double, double, int> hit)
        {
            var caster = CharacterPlayerStats(Player);
            int Own(uint key) => own.GetValueOrDefault(key);
            int Source(uint key) => caster.GetValueOrDefault(key);
            int volleys = Math.Clamp(Own(Stat.BladefallNumberOfVolleys) + Source(Stat.BladefallNumberOfVolleys), 0, 64);
            int bladeEvery = Own(Stat.BladefallBladeLeftInGroundForEveryXVolleys);
            int limit = Own(Stat.MaximumNumberOfBladesLeftInGround);
            int radius = SkillRadius(Player, own, spell: true, effect);
            bool expanding = SkillsFor(Player).EffectId(effect) == "BladefallAltX";
            int stageMore = Own(Stat.BladefallDamagePerStagePlusPctFinal);
            int stageCriticalIncrease = Own(Stat.BladefallCriticalStrikeChancePlusPctPerStage);
            double lifetime = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Source, Own);
            double spacing = BladefallVolleyGap * Math.Max(0, 1 + (Own(Stat.BladefallVolleyGapDistancePlusPct)
                + Source(Stat.BladefallVolleyGapDistancePlusPct)) / 100d);
            var direction = Vector(at) - Vector(from);
            direction = direction.Length > 0 ? direction.Unit : new(1, 0);
            long start = Runtime.Now;
            float angle = (float)Math.Atan2(direction.X, -direction.Y);
            for (int i = 0; i < volleys; i++)
            {
                int volley = i;
                long due = start + ProjectileRuntime.Time(BladefallVolleyInterval * (volley + 1));
                var c = Vector(at) + direction * (volley * spacing);
                var centre = (X: (int)Math.Round(c.X), Y: (int)Math.Round(c.Y));
                Runtime.Schedule(due, () =>
                {
                    if (!Active || !current()) return;
                    // The Volleys variant grows after each impact. The widening coefficient is a server estimate.
                    int volleyRadius = expanding ? (int)Math.Round(radius * SkillArea.RadiusMultiplier(8 * volley)) : radius;
                    Effect(centre.X, centre.Y, BladefallVolleyArt, ProjectileRuntime.Time(.5), volleyRadius);
                    hit(centre, volleyRadius, ProjectileWorldTime(Player), Math.Max(0, 1 + (double)stageMore * volley / 100),
                        ActorScale.Saturate((long)stageCriticalIncrease * volley));
                    if (bladeEvery <= 0 || (volley + 1) % bladeEvery != 0 || lifetime <= 0
                        || !Player.Layout.WalkableFor(centre.X, centre.Y, 0)) return;
                    var blades = placedBladefallBlades ??= new(0, 0, skill) { Ended = true };
                    LeaveProjectileBlade(blades, centre, angle, 1, limit, due + ProjectileRuntime.Time(lifetime));
                });
            }
        }
    }
}
