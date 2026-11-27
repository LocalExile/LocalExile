using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string ThunderstormEffect = "Thunderstorm";
    const string ThunderburstEffect = "ThunderstormMiniTornados";
    const string WindstormArt = "Metadata/Effects/Spells/windstorm/Storm.ao";
    const string WindstormImpactArt = "Metadata/Effects/Spells/windstorm/Arrow_Impact.ao";
    const string WindstormBurstArt = "Metadata/Effects/Spells/windstorm/Detonate.ao";
    const string TornadoProjectile = "Metadata/Projectiles/WindstormTornado";
    const string TornadoBurstArt = "Metadata/Effects/Spells/windstorm/TornadoDetonate.ao";

    static readonly ProjectileSkillDefinition ThunderburstProjectile = new(TornadoProjectile,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(TornadoProjectile, get, speed, attack: true)));

    internal sealed record ThunderstormPlan(ProjectileUse Landing, ProjectileUse? Burst, int Radius, int Limit,
        int MaximumStage, long Life, long StageInterval, long EscapeWindow, int AreaPerStage, double DetonationMore, int BlindLess);

    static void ReleaseThunderstorm(ProjectileSession session, ProjectileUse use,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> playerStats, SkillCriticalRoll critical,
        double? actionStart)
    {
        var player = session.Player;
        int Own(uint key) => own.GetValueOrDefault(key);
        int Caster(uint key) => playerStats.GetValueOrDefault(key);
        int Get(uint key) => ActorScale.Saturate((long)Own(key) + Caster(key));
        int radius = SkillRadius(player, own, spell: false, gem.Effect);
        int limit = Math.Clamp(Get(Stat.WindstormStormLimit), 0, 64);
        int stages = Math.Clamp(Get(Stat.WindstormStormMaximumStages), 0, 64);
        long lifetime = ProjectileRuntime.Time(SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
            Caster, Own, DurationKinds(player, gem.Effect)));
        long stageInterval = ProjectileRuntime.Time(Get(Stat.WindstormStormStageGainedPerXMs) is > 0 and var ms
            ? ms / 1000d : Own(Stat.BaseWindstormStormStageGainedPerXMs) / 1000d);
        long window = ProjectileRuntime.Time(Get(Stat.WindstormStormPerfectTimingWindowEscapeTimeMs) / 1000d);
        if (radius <= 0 || lifetime <= 0 || stageInterval <= 0 || window <= 0 || stages == 0 || limit == 0) return;
        double more = Math.Max(0, 1 + (Get(Stat.WindstormStormHitDamagePlusPctFinalOnDetonation)
            + (long)Get(Stat.WindstormStormHitDamagePlusPctFinalOnDetonationPerStage) * stages) / 100d);
        var burst = CaptureThunderburst(session, gem, ((int)Math.Round(use.Aim.X), (int)Math.Round(use.Aim.Y)), critical, use.Sequence);
        var plan = new ThunderstormPlan(use, burst, radius, limit, stages, lifetime, stageInterval, window,
            Get(Stat.WindstormStormAreaOfEffectPlusPctFinalPerStage), more, BlindEffectLess(playerStats));
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        int fallSpeed = RainOfArrowsFallSpeed(player, gem.Id, playerStats, own, false);
        double fall = .25 + .3 * Math.Min(2.4, 100d / (100 + Math.Max(-99, fallSpeed)));
        long at = ProjectileRuntime.Time((actionStart ?? now) + fall);
        session.ScheduleContact(at, () => player.PlayerLife > 0 && player.ProjectileActive(use.Owner.Generation),
            () => session.LandThunderstorm(plan));
    }

    static ProjectileUse? CaptureThunderburst(ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) aim, SkillCriticalRoll critical, ushort sequence)
    {
        var player = session.Player;
        var source = SupportedSkill(player, gem.Id);
        if (source.SourceGem is not { } sourceGem) return null;
        var sourceIdentity = PlayerInventories.GemIdentity(sourceGem);
        var child = GemSkillEffects(player).FirstOrDefault(g => g.Gem == gem.Gem
            && SkillsFor(player).EffectId(g.Effect) == ThunderburstEffect
            && SupportedSkill(player, g.Id).SourceGem is { } childGem
            && ReferenceEquals(sourceIdentity, PlayerInventories.GemIdentity(childGem)));
        if (child.Name is null) return null;
        var stats = SupportedSkill(player, child.Id).Stats;
        int Own(uint key) => stats.GetValueOrDefault(key);
        var caster = CharacterPlayerStats(player);
        double life = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), caster.GetValueOrDefault, Own,
            DurationKinds(player, child.Effect));
        int radius = SkillRadius(player, stats, spell: false, child.Effect);
        if (life <= 0 || radius <= 0) return null;
        var use = CaptureGemProjectile(player, session, child.Id, child, aim, critical, sequence);
        var shot = (ProjectileOffense)use.Payload!;
        double areaMore = Math.Max(0, 1 + Own(Stat.ActiveSkillAreaDamagePlusPctFinal) / 100d);
        return use with
        {
            Payload = shot with
            {
                Label = "Thunderburst", ClientPredicted = false, AreaVisual = TornadoBurstArt,
                CriticalTrigger = null, MirageSource = null
            },
            Profile = use.Profile with
            {
                Returns = false, CannotReturn = true, NativeVolley = new() { Nova = true },
                EndArea = new("Thunderburst expiry", radius, Multiplier: areaMore),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Lifetime },
                Legs = index => new SparkLegs(sequence, index, use.Owner.Id, life,
                    Own(Stat.MiniTornadoRandomDurationPct), use.Profile.Speed)
            }
        };
    }

    public sealed partial class ProjectileSession
    {
        sealed class Thunderstorm(ThunderstormPlan plan, long expires)
        {
            public ThunderstormPlan Plan { get; } = plan;
            public ProjectileVector Centre => Plan.Landing.Aim;
            public long Expires { get; } = expires;
            public long? Entered;
            public int Stage, Radius = plan.Radius;
            public Action? RemoveArt;
        }

        List<Thunderstorm>? thunderstorms;
        Dictionary<SpawnedMonster, int>? thunderstormBlind, nextThunderstormBlind;
        long nextThunderstormBlindAt;

        internal void LandThunderstorm(ThunderstormPlan plan)
        {
            var storms = thunderstorms ??= [];
            bool idle = storms.Count == 0;
            while (storms.Count >= plan.Limit) RemoveThunderstormAt(0);
            var storm = new Thunderstorm(plan, Runtime.Now + plan.Life);
            storms.Add(storm);
            Runtime.Impact(plan.Landing, storm.Centre, new("Thunderstorm landing", plan.Radius));
            RainArt(storm.Centre, WindstormImpactArt, .6, storm.Radius);
            DrawThunderstorm(storm);
            if (idle) CheckThunderstorms();
        }

        void DrawThunderstorm(Thunderstorm storm)
        {
            storm.RemoveArt?.Invoke();
            Effect((int)Math.Round(storm.Centre.X), (int)Math.Round(storm.Centre.Y), WindstormArt,
                storm.Expires - Runtime.Now, storm.Radius, captureRemoval: remove => storm.RemoveArt = remove);
        }

        void RemoveThunderstormAt(int index)
        {
            var storms = thunderstorms!;
            storms[index].RemoveArt?.Invoke();
            storms.RemoveAt(index);
        }

        void CheckThunderstorms()
        {
            if (thunderstorms is not { Count: > 0 } storms) return;
            if (!Active || Player.PlayerLife == 0) { ClearThunderstorms(); return; }
            long now = Runtime.Now;
            double at = now / (double)ProjectileRuntime.QuantaPerSecond;
            var owner = Vector(Player.Walk.Position(at));
            bool changed = false;
            for (int i = storms.Count - 1; i >= 0; i--)
            {
                var storm = storms[i];
                var plan = storm.Plan;
                long fullAt = storm.Entered is long entered ? entered + plan.StageInterval * plan.MaximumStage : long.MaxValue;
                if (now >= storm.Expires || fullAt != long.MaxValue && now - fullAt >= plan.EscapeWindow)
                {
                    RemoveThunderstormAt(i);
                    changed = true;
                    continue;
                }
                if ((owner - storm.Centre).Length > storm.Radius)
                {
                    if (storm.Entered is null) continue;
                    RemoveThunderstormAt(i);
                    changed = true;
                    if (storm.Stage == plan.MaximumStage)
                    {
                        var shot = ((ProjectileOffense)plan.Landing.Payload!).ScaleHitAndAilmentDamage(plan.DetonationMore);
                        Runtime.Impact(plan.Landing with { Payload = shot }, storm.Centre, new("Thunderstorm detonation", storm.Radius));
                        RainArt(storm.Centre, WindstormBurstArt, .6, storm.Radius);
                        if (plan.Burst is { } burst)
                            ReleaseNow(burst with { Origin = storm.Centre, Aim = storm.Centre + new ProjectileVector(1, 0) });
                    }
                    continue;
                }

                storm.Entered ??= now;
                int stage = (int)Math.Min(plan.MaximumStage, (now - storm.Entered.Value) / plan.StageInterval);
                if (stage == storm.Stage) continue;
                storm.Stage = stage;
                storm.Radius = Math.Max(1, (int)Math.Round(plan.Radius * Math.Sqrt(Math.Max(0, 1 + stage * plan.AreaPerStage / 100d))));
                DrawThunderstorm(storm);
                changed = true;
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[thunderstorm] stage={stage} radius={storm.Radius} at={at:F4}");
            }
            if (changed || now >= nextThunderstormBlindAt)
            {
                UpdateThunderstormBlind(storms, at);
                nextThunderstormBlindAt = now + ProjectileRuntime.Time(.1);
            }
            if (storms.Count != 0) Runtime.Schedule(now + ProjectileRuntime.TickQuanta, CheckThunderstorms);
        }

        void UpdateThunderstormBlind(List<Thunderstorm> storms, double at)
        {
            var next = nextThunderstormBlind ??= new(ReferenceEqualityComparer.Instance);
            var prior = thunderstormBlind ??= new(ReferenceEqualityComparer.Instance);
            var area = Player.Area;
            foreach (var storm in storms)
            {
                if (storm.Plan.BlindLess <= 0) continue;
                var centre = ((int)Math.Round(storm.Centre.X), (int)Math.Round(storm.Centre.Y));
                foreach (var (_, monster) in area.MonstersNear(centre, storm.Radius))
                    if (monster is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                        && monster.Mods.Effects[Stat.CannotBeBlinded] == 0
                        && (Vector(monster.Walk.Position(at)) - storm.Centre).Length <= storm.Radius)
                        next[monster] = Math.Max(next.GetValueOrDefault(monster), storm.Plan.BlindLess);
            }
            ushort row = BuffRows.Find("windstorm_debuff");
            if (row == 0) row = FlaskGrounds.BlindedBuffRow;
            foreach (var (monster, less) in next)
                if (prior.GetValueOrDefault(monster) != less) SetStormBlind(area, monster, less, storms, row, tag: 0);
            foreach (var monster in prior.Keys)
                if (!next.ContainsKey(monster)) SetStormBlind(area, monster, 0, storms, row, tag: 0);
            (thunderstormBlind, nextThunderstormBlind) = (next, prior);
            prior.Clear();
        }

        void ClearThunderstorms()
        {
            if (thunderstorms is not { } storms) return;
            while (storms.Count != 0) RemoveThunderstormAt(storms.Count - 1);
            if (thunderstormBlind is { Count: > 0 }) UpdateThunderstormBlind(storms, 0);
            nextThunderstormBlindAt = 0;
        }
    }
}
