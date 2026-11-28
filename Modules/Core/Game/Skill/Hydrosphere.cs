using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string HydrosphereTriggeredEffect = "HydrosphereTriggered";
    static bool HydrosphereEffect(string? effect) => effect is "Hydrosphere" or "HydrosphereRoyale"
        or HydrosphereTriggeredEffect or "HydrosphereAltX" or "HydrosphereAltY";

    static ushort WinterHydrosphereSkill(Player player) => AllWornGrantedSkillsOf(player)
        .Any(grant => grant.EffectId == HydrosphereTriggeredEffect)
        ? GemSkillEffects(player).FirstOrDefault(skill => SkillsFor(player).EffectId(skill.Effect) == HydrosphereTriggeredEffect).Id
        : (ushort)0;

    static void TriggerWinterHydrosphere(Player player, ushort skill, (int X, int Y) aim, int generation)
    {
        using var mutation = ProjectileMutation(player, advance: false);
        if (player.PaidChannel is not { } paid || paid.Generation != generation || !PaidChannelCurrent(player, paid)) return;
        var state = LinkedTriggersOf(player);
        double now = AreaTime(player);
        var supported = SupportedSkill(player, skill);
        if (supported.WornGrant is not { EffectId: HydrosphereTriggeredEffect } grant || !supported.RequirementsMet
            || now < state.ReadyAt.GetValueOrDefault(supported.Effect)
            || !RandomRolls.Chance(supported.Stats.GetValueOrDefault(Stat.ChanceToTriggerLevel20HydrosphereWhileChannelingWinterOrbPct),
                100, AreaRng(player))) return;
        var session = ProjectilesFor(player);
        bool Current() => session.Active && player.ProjectileActive(state.Epoch) && !player.Paused && player.PlayerLife > 0
            && SupportedSkill(player, skill) is { RequirementsMet: true } source && source.WornGrant == grant
            && SameTriggerSource(supported, source);
        if (aim == default) aim = player.Walk.Position(now);
        TryReleaseAutomaticPayload(player, session, skill, supported, null, aim, Current, now, "Winter Orb Hydrosphere", out _);
    }

    static SupportedGem HydroDamageSource(SupportedGem source, int cold, int lightning) => source with
    {
        Stats = new Dictionary<uint, int>(source.Stats)
        {
            [Stat.SkillPhysicalDamagePctToConvertToCold] = cold,
            [Stat.SkillPhysicalDamagePctToConvertToLightning] = lightning,
        },
    };

    public sealed partial class ProjectileSession
    {
        const string HydrosphereBody = "Metadata/Monsters/Anomalies/WaterSphere";
        const string HydroArt = "Metadata/Effects/Spells/hydro_catalyst/";
        const int HydroDrenchRadius = 19;
        const int HydroPulseRadius = 60;
        const double HydroRadiusWorldUnits = 250d / 23;
        const double HydroTravelSpeed = 150;
        const int HydroBodySize = 4;
        const byte HydroStateComponent = 7;
        static readonly long HydroDrenchTick = ProjectileRuntime.Time(.1);

        sealed class WaterSphere(uint id, (int X, int Y) at)
        {
            public uint Id { get; } = id;
            public uint Caster;
            public ProjectileVector From = Vector(at), Destination = Vector(at);
            public long StartMove, EndMove, Until, NextPulse, LastDrench, HitAgain;
            public int MoveGeneration;
            public int DrenchRadius, PulseRadius, ColdExposure, LightningExposure, AilmentThreshold;
            public double DrenchSeconds;
            public long PulseEvery, HitCooldown;
            public long FrozenUntil, ShockedUntil, BrittleUntil, SappedUntil;
            public bool Moving, ColdVisual, LightningVisual;
            public Func<bool> Current = null!;
            public Action<(int X, int Y), int, Func<uint, (int X, int Y), bool>, int, int> Hit = null!;
            public readonly Dictionary<SpawnedMonster, double> Drenched = [];
            public ProjectileActor? Actor;

            public ProjectileVector Position(long at) => !Moving || at >= EndMove ? Destination
                : From + (Destination - From) * Math.Clamp((at - StartMove) / (double)(EndMove - StartMove), 0, 1);
            public bool Cold(long at) => FrozenUntil > at || BrittleUntil > at;
            public bool Lightning(long at) => ShockedUntil > at || SappedUntil > at;
        }

        WaterSphere? waterSphere;

        internal void ReleaseHydrosphere(uint caster, int effect, IReadOnlyDictionary<uint, int> own,
            (int X, int Y) aim, Func<bool> current,
            Action<(int X, int Y), int, Func<uint, (int X, int Y), bool>, int, int> hit)
        {
            var stats = CharacterPlayerStats(Player);
            int Own(uint stat) => own.GetValueOrDefault(stat);
            int Get(uint stat) => ActorScale.Add(stat, Own(stat), stats.GetValueOrDefault(stat));
            double Seconds(uint stat) => SkillDurationModifiers.Seconds(Own(stat), stats.GetValueOrDefault, Own,
                DurationKinds(Player, effect));
            double duration = Seconds(Stat.BaseSkillEffectDuration);
            if (duration <= 0 || Own(Stat.HydroSphereBasePulseFrequencyMs) <= 0) return;
            var sphere = waterSphere;
            if (sphere is not null && !HydroCurrent(sphere, Runtime.Now))
            {
                RemoveHydrosphere(sphere, retire: false);
                sphere = null;
            }
            bool created = sphere is null;
            sphere ??= new WaterSphere(Player.TakeObjectId(), aim);
            sphere.Caster = caster;
            sphere.Current = current;
            sphere.Hit = hit;
            sphere.DrenchRadius = SkillRadius(Player, HydroDrenchRadius, own, spell: true, effect);
            sphere.PulseRadius = SkillRadius(Player, HydroPulseRadius, own, spell: true, effect);
            sphere.DrenchSeconds = Seconds(Stat.BaseSecondarySkillEffectDuration);
            sphere.AilmentThreshold = Math.Max(1, Own(Stat.HydrosphereAilmentThreshold));
            int exposure = Own(Stat.WaterSphereColdLightningExposurePct);
            sphere.ColdExposure = InflictedExposure(Get, DamageType.Cold, exposure);
            sphere.LightningExposure = InflictedExposure(Get, DamageType.Lightning, exposure);
            double pulseRate = Math.Max(.01, 1 + Get(Stat.HydroSpherePulseFrequencyPlusPct) / 100d);
            sphere.PulseEvery = ProjectileRuntime.Time(Math.Max(TickSeconds,
                Own(Stat.HydroSphereBasePulseFrequencyMs) / (1000 * pulseRate)));
            sphere.HitCooldown = ProjectileRuntime.Time(Math.Max(0, Own(Stat.HydrosphereHitCooldownMs)) / 1000d);
            sphere.Until = Runtime.Now + ProjectileRuntime.Time(duration);
            var destination = Vector(aim);
            if (created)
            {
                sphere.Actor = new(new(sphere.Id, epoch), 2, sphere.Position, HydroBodySize) { Size = HydroBodySize, CanBeChainTarget = false };
                waterSphere = sphere;
                Player.TrackProjectileObject(sphere.Id);
                Create(sphere.Id, () =>
                {
                    var pos = RoundCell(sphere.Position(Runtime.Now));
                    var obj = new MonsterObject(pos.X, pos.Y, 1, MonsterObject.AnomalyComponents,
                        StateMachine: HydroStates(sphere), Stats: [(Stat.IsWaterSphereOwnerId, (int)Player.Id),
                            (Stat.HideMiniLifeBar, 1)])
                    { Blocking = false };
                    return InstanceEntities.CreateObject(sphere.Id, InstanceEntities.ObjectTypeHash(HydrosphereBody), obj.Blob());
                }, wake: true);
                Drench(sphere, destination, destination);
                PulseHydrosphere(sphere);
                Runtime.Schedule(Runtime.Now + HydroDrenchTick, () => StepHydrosphere(sphere));
                return;
            }

            var from = sphere.Position(Runtime.Now);
            sphere.From = from;
            sphere.Destination = destination;
            sphere.StartMove = Runtime.Now;
            sphere.LastDrench = Runtime.Now;
            long travel = Math.Max(ProjectileRuntime.TickQuanta, ProjectileRuntime.Time((destination - from).Length / HydroTravelSpeed));
            sphere.EndMove = Runtime.Now + travel;
            sphere.Moving = true;
            World(sphere.Id, InstanceProtocol.MoveTo(sphere.Id, aim.X, aim.Y, (float)(travel / (double)ProjectileRuntime.QuantaPerSecond),
                InstanceProtocol.FollowTerrain));
            PresentHydroState(sphere);
            int movement = ++sphere.MoveGeneration;
            long arrives = sphere.EndMove;
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[hydrosphere-move] actor={sphere.Id:x} caster={caster:x} aim={aim.X},{aim.Y}");
            Runtime.Schedule(arrives, () =>
            {
                if (sphere.MoveGeneration != movement || !HydroCurrent(sphere, arrives)) return;
                Drench(sphere, sphere.Position(sphere.LastDrench), destination);
                sphere.Moving = false;
                World(sphere.Id, InstanceProtocol.Teleport(sphere.Id, aim.X, aim.Y));
                PresentHydroState(sphere);
                PulseHydrosphere(sphere);
            });
        }

        bool HydroCurrent(WaterSphere sphere, long at) => Active && Player.PlayerLife > 0
            && ReferenceEquals(waterSphere, sphere) && at < sphere.Until && sphere.Current();

        static StateMachineData HydroStates(WaterSphere sphere) => new StateMachineData([0, 0, 0, 0], 0,
            ["moving", "shocked", "frozen", "aura_radius"])
            .Set("moving", sphere.Moving ? 1 : 0)
            .Set("shocked", sphere.LightningVisual ? 1 : 0)
            .Set("frozen", sphere.ColdVisual ? 1 : 0)
            .Set("aura_radius", (int)Math.Round(sphere.DrenchRadius * HydroRadiusWorldUnits));

        void PresentHydroState(WaterSphere sphere) =>
            World(sphere.Id, InstanceEntities.StateMachineUpdate(sphere.Id, HydroStateComponent, HydroStates(sphere)));

        void Drench(WaterSphere sphere, ProjectileVector from, ProjectileVector to)
        {
            var segment = to - from;
            double length = segment.Length;
            var centre = RoundCell(from + segment * .5);
            double now = ProjectileWorldTime(Player);
            foreach (var (_, target) in Player.Area.MonstersNear(centre, sphere.DrenchRadius + length * .5 + 1))
            {
                if (target is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false }) continue;
                var at = Vector(target.Walk.Position(now));
                var closest = length == 0 ? from : from + segment * Math.Clamp((at - from).Dot(segment) / (length * length), 0, 1);
                if ((at - closest).Length > sphere.DrenchRadius) continue;
                sphere.Drenched[target] = now + sphere.DrenchSeconds;
                if (target.Effects[Stat.ImmuneToExposure] != 0) continue;
                // Refresh the resistance modifier on contact without replacing both timed icons on every spatial sample.
                if (sphere.ColdExposure < 0) Expose(target, 1, sphere.ColdExposure, now, sphere.DrenchSeconds, refreshWindow: .5);
                if (sphere.LightningExposure < 0) Expose(target, 2, sphere.LightningExposure, now, sphere.DrenchSeconds, refreshWindow: .5);
            }
            sphere.LastDrench = Runtime.Now;
        }

        void StepHydrosphere(WaterSphere sphere)
        {
            long now = Runtime.Now;
            if (!HydroCurrent(sphere, now)) { RemoveHydrosphere(sphere, retire: false); return; }
            Drench(sphere, sphere.Position(sphere.LastDrench), sphere.Position(now));
            bool cold = sphere.Cold(now), lightning = sphere.Lightning(now);
            if (cold != sphere.ColdVisual || lightning != sphere.LightningVisual)
            {
                sphere.ColdVisual = cold;
                sphere.LightningVisual = lightning;
                PresentHydroState(sphere);
            }
            bool pulsing = !sphere.Moving && (cold || lightning);
            if (pulsing && now >= sphere.NextPulse) PulseHydrosphere(sphere);
            long next = Math.Min(sphere.Until, now + HydroDrenchTick);
            if (pulsing) next = Math.Min(next, sphere.NextPulse);
            Runtime.Schedule(next, () => StepHydrosphere(sphere));
        }

        void PulseHydrosphere(WaterSphere sphere)
        {
            long now = Runtime.Now;
            double seconds = ProjectileWorldTime(Player);
            foreach (var target in sphere.Drenched.Keys.Where(target => target.Dead || sphere.Drenched[target] <= seconds).ToArray())
                sphere.Drenched.Remove(target);
            bool cold = sphere.Cold(now), lightning = sphere.Lightning(now);
            var at = RoundCell(sphere.Position(now));
            string art = lightning ? cold ? "combined_nova.ao" : "shock_nova.ao" : cold ? "ice_nova.ao" : "water_nova.ao";
            Effect(at.X, at.Y, HydroArt + art, ProjectileRuntime.Time(.31), sphere.PulseRadius);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[hydrosphere] actor={sphere.Id:x} caster={sphere.Caster:x} at={seconds:F3} centre={at.X},{at.Y} wet={sphere.Drenched.Count} cold={cold} lightning={lightning}");
            sphere.Hit(at, sphere.PulseRadius, (id, _) => Player.Area.Monsters.GetValueOrDefault(id) is { } target
                && sphere.Drenched.GetValueOrDefault(target) > seconds, lightning ? cold ? 50 : 0 : 100, lightning ? cold ? 50 : 100 : 0);
            sphere.NextPulse = now + sphere.PulseEvery;
        }

        void RemoveHydrosphere(WaterSphere sphere, bool retire)
        {
            if (!ReferenceEquals(waterSphere, sphere)) return;
            waterSphere = null;
            if (retire) RetireSkillEffectObject(sphere.Id);
            else WorldRecords(sphere.Id, () =>
            {
                Player.UntrackProjectileObject(sphere.Id);
                return [InstanceProtocol.RemoveObject(sphere.Id)];
            }, remove: true);
        }

        void AddHydrosphereActors(List<ProjectileActor> actors, long at)
        {
            if (waterSphere is { } sphere && at < sphere.Until && at >= sphere.HitAgain)
                InOrder(actors, sphere.Actor!);
        }

        ProjectileHitResult ContactHydrosphere(WaterSphere sphere, ProjectileDamage contact, ProjectileOffense shot)
        {
            if (contact.Target != sphere.Actor!.Key || !HydroCurrent(sphere, contact.At)
                || contact.At < sphere.HitAgain || HydrosphereEffect(SkillsFor(Player).EffectId(shot.Effect))
                || !contact.IsArea && contact.Projectile.CompletedChains > 0
                || shot.ActualCaster is not null && !shot.FromTrapOrMine && !shot.FromTotem)
                return ProjectileHitResult.Miss;
            var stats = shot.ActualCaster ?? CharacterPlayerStats(Player);
            var rng = contact.Projectile.RandomSource;
            var packet = contact.IsArea ? shot.AreaHit ?? shot.Hit : shot.Hit;
            var hit = new SkillDamageContext(shot.Spell ? SkillHitKind.Spell : SkillHitKind.Attack, stats)
                .Hit(packet).Scale(contact.Multiplier).Roll(rng);
            bool critical = RollCriticalStrike(Player, shot.CriticalChance, shot.SkillStats,
                attack: !shot.Spell, skillRoll: shot.CriticalRoll, playerStats: stats);
            ApplyHydroHit(sphere, hit, critical, stats, shot.SkillStats, contact.At, rng);
            return ProjectileHitResult.Hit;
        }

        internal void HydrospheresInArea(ushort skill, int effect, (int X, int Y) centre, int radius, bool spell,
            SkillCriticalRoll? criticalRoll)
        {
            if (waterSphere is not { } sphere || HydrosphereEffect(SkillsFor(Player).EffectId(effect))) return;
            long at = Runtime.Now;
            if (at < sphere.HitAgain || !HydroCurrent(sphere, at)
                || (sphere.Position(at) - Vector(centre)).Length > radius
                || SupportedHitInputsFor(Player, skill, area: true) is not { } inputs) return;
            var supported = SupportedSkill(Player, skill);
            var caster = CharacterPlayerStats(Player);
            var random = AreaRng(Player);
            var packet = new SkillDamageContext(spell ? SkillHitKind.Spell : SkillHitKind.Attack, caster)
                .Hit(SupportedHitFrom(inputs, suppress: false)).Roll(random);
            bool crit = RollCriticalStrike(Player, SupportedCriticalChanceFor(Player, skill, release: false), supported.Stats,
                attack: !spell, skillRoll: criticalRoll, playerStats: caster);
            ApplyHydroHit(sphere, packet, crit, caster, supported.Stats, at, random);
        }

        void ApplyHydroHit(WaterSphere sphere, DamagePacket damage, bool critical,
            IReadOnlyDictionary<uint, int> caster, IReadOnlyDictionary<uint, int> skill, long at, Random random)
        {
            sphere.HitAgain = at + sphere.HitCooldown;
            double now = at / (double)ProjectileRuntime.QuantaPerSecond;
            int Get(uint name) => name == Stat.NeverChill ? 1
                : ActorScale.Add(name, caster.GetValueOrDefault(name), PanelStats.Of(name, skill.GetValueOrDefault));
            // WaterSphere.ot applies a 40% freeze threshold multiplier and 1500% for shock, brittle and sap.
            double baseThreshold = sphere.AilmentThreshold;
            var standard = new MonsterElementalAilments().Inflict(damage, critical, baseThreshold * 15, now, Get,
                _ => 0, random, area: 1, freezeDamageMultiplier: 37.5);
            foreach (var ailment in standard)
            {
                if (ailment.Kind == MonsterElementalAilments.Kind.Freeze)
                    sphere.FrozenUntil = Math.Max(sphere.FrozenUntil, ProjectileRuntime.Time(ailment.Until));
                else if (ailment.Kind == MonsterElementalAilments.Kind.Shock)
                    sphere.ShockedUntil = Math.Max(sphere.ShockedUntil, ProjectileRuntime.Time(ailment.Until));
            }
            int Target(uint key) => key is Stat.BrittleAilmentThresholdPlusPctFinal or Stat.SapAilmentThresholdPlusPctFinal ? 1400 : 0;
            foreach (var (ailment, instance) in AlternateAilmentsOnHit(new RuleAilments(), damage, critical, baseThreshold, now, Get, Target, random))
            {
                if (ailment.Kind == AlternateKind.Brittle)
                    sphere.BrittleUntil = Math.Max(sphere.BrittleUntil, ProjectileRuntime.Time(instance.Until));
                else if (ailment.Kind == AlternateKind.Sap)
                    sphere.SappedUntil = Math.Max(sphere.SappedUntil, ProjectileRuntime.Time(instance.Until));
            }
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[hydrosphere-hit] actor={sphere.Id:x} at={now:F3} cold={sphere.Cold(at)} lightning={sphere.Lightning(at)}");
        }

        void ClearHydrosphere()
        {
            if (waterSphere is { } sphere) RemoveHydrosphere(sphere, retire: true);
        }
    }
}
