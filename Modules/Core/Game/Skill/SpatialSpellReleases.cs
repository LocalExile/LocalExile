using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool SpatialSpellEffect(string? effect) =>
        effect is "CracklingLanceAltX" or "CracklingLanceAltY" or "GlacialCascade" or "GlacialCascadeAltX" or "LightningConduitAltX";

    public sealed partial class ProjectileSession
    {
        // The gem rows provide the branch angle modifier and the cascade count and damage. Beam lengths, spike spacing
        // and spike travel time are local collision estimates in the absence of a captured client collision trace.
        const int LanceLength = 60;
        const double LanceBranchAngle = 24;
        const int CascadeBaseSpacing = 12;
        const double CascadeInterval = .045;

        internal void ScheduleSpatialSpell((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) from, (int X, int Y) aim, double windup, Func<bool> current, SacrificeUse sacrifice,
            SkillCriticalRoll? critical, ushort sequence = 0, int repeat = 0, double damageMore = 1,
            SpawnedMonster? brandedTarget = null)
        {
            if (!Active || !current()) return;
            var source = SupportedSkill(Player, gem.Id);
            long released = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player))) + ProjectileRuntime.Time(windup);
            Runtime.Schedule(released, () =>
            {
                if (!current() || !CurrentSource()) return;
                int Own(uint key) => source.Stats.GetValueOrDefault(key);
                var caster = CharacterPlayerStats(Player);
                var effect = SkillsFor(Player).EffectId(gem.Effect);
                var direction = GroundSlamWave.Direction(from, aim);
                if (direction == default) direction = (1, 0);
                int Radius(int baseRadius) => SkillRadius(Player, baseRadius, source.Stats, true, gem.Effect, caster);
                void Hit((int X, int Y) centre, int radius, double more = 1,
                    Func<uint, (int X, int Y), bool>? targetFilter = null) =>
                    Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, from, centre, immediate: true, sequence: sequence,
                        repeat: repeat, damageMore: damageMore * more, radiusOverride: radius, criticalRoll: critical,
                        sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget, targetFilter: targetFilter));

                if (effect == "LightningConduitAltX")
                {
                    int radius = Radius(Own(Stat.ActiveSkillBaseAreaOfEffectRadius));
                    double now = ProjectileWorldTime(Player);
                    var selected = Player.Area.MonstersNear(aim, radius)
                        .Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Invulnerable: false,
                            Asleep: false, WorldHidden: false } && !MonsterAsleep(Player, p.Key))
                        .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), aim)))
                        .Where(p => p.Distance <= radius).OrderBy(p => p.Distance).ThenBy(p => p.Id)
                        .Take(Math.Max(0, Own(Stat.LightningConduitMaxNumTargets))).Select(p => p.Id).ToHashSet();
                    if (selected.Count > 0) Hit(aim, radius, targetFilter: (id, _) => selected.Contains(id));
                }
                else if (effect is "CracklingLanceAltX" or "CracklingLanceAltY")
                {
                    int width = Radius(effect == "CracklingLanceAltX" ? 4 : 3);
                    double length = LanceLength * Math.Max(.25, Radius(12) / 12d);
                    if (width > 0)
                        Hit(from, (int)Math.Ceiling(length + width), targetFilter: (_, at) => LanceCovers(at));

                    bool LanceCovers((int X, int Y) at)
                    {
                        double x = at.X - from.X, y = at.Y - from.Y;
                        double along = x * direction.X + y * direction.Y;
                        double lateral = y * direction.X - x * direction.Y;
                        if (along < 0 || along > length) return false;
                        if (Math.Abs(lateral) <= width) return true;
                        if (effect != "CracklingLanceAltX") return false;
                        double radians = Math.Clamp(LanceBranchAngle * (1 + Own(Stat.DisintegrateSecondaryBeamAnglePlusPct) / 100d), 0, 80)
                            * Math.PI / 180d;
                        double forward = Math.Cos(radians), side = Math.Sin(radians);
                        return BranchAt(.34) || BranchAt(.60);

                        bool BranchAt(double fraction)
                        {
                            double a = along - length * fraction, b = Math.Abs(lateral);
                            double branchAlong = a * forward + b * side;
                            return branchAlong >= 0 && branchAlong <= length * .56
                                && Math.Abs(b * forward - a * side) <= width;
                        }
                    }
                }
                else if (effect is "GlacialCascade" or "GlacialCascadeAltX")
                {
                    int radius = SkillRadius(Player, source.Stats, spell: true, gem.Effect);
                    int spikes = Math.Clamp(Own(Stat.UpheavalNumberOfSpikes) + Own(Stat.GlacialCascadeNumberOfAdditionalBursts), 0, 32);
                    double spacing = CascadeBaseSpacing * Math.Max(.25, radius / 12d);
                    double interval = CascadeInterval / Math.Max(.1, 1 + Own(Stat.GlacialCascadeTravelSpeedPlusPct) / 100d);
                    for (int i = 0; i < spikes; i++)
                    {
                        double distance = (i + 1) * spacing;
                        var at = (from.X + (int)Math.Round(distance * direction.X), from.Y + (int)Math.Round(distance * direction.Y));
                        bool final = i == spikes - 1;
                        double more = final ? Math.Max(0, 1 + Own(Stat.GlacialCascadeFinalSpikeDamagePlusPctFinal) / 100d) : 1;
                        Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(i * interval), () =>
                        {
                            if (CurrentSource()) Hit(at, radius, more);
                        });
                    }
                }
            });

            bool CurrentSource() => Active && !Player.Paused && Player.PlayerLife > 0 && sacrifice.Current
                && SameTriggerSource(source, SupportedSkill(Player, gem.Id));
        }
    }
}
