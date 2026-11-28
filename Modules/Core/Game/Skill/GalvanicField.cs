using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static bool GalvanicFieldEffect(string? effect) => effect is "GalvanicField" or "GalvanicFieldAltX" or "GalvanicFieldAltY";

    public sealed partial class ProjectileSession
    {
        const ushort GalvanicBuffRow = 2091;
        const ushort GalvanicBeamRow = 517;
        const string GalvanicSphereArt = "Metadata/Effects/Spells/shock_plasmacharge/shock_field.ao";
        const string GalvanicSurgeArt = "Metadata/Effects/Spells/shock_plasmacharge/surge.ao";
        const int GalvanicBaseRadius = 37;

        sealed record GalvanicCharge(SupportedGem Source, int ShockChance, int ShockDuration, Func<bool> Current,
            Action<(int X, int Y), int, uint, double> Hit);

        sealed class GalvanicSphere(GalvanicCharge charge, SpawnedMonster anchor, (int X, int Y) at,
            long until, long interval, long retarget, int radius, int chains, double damage)
        {
            public GalvanicCharge Charge { get; } = charge;
            public SpawnedMonster Anchor { get; } = anchor;
            public (int X, int Y) At = at;
            public long Until { get; } = until;
            public long Interval { get; } = interval;
            public long Retarget { get; } = retarget;
            public int Radius { get; } = radius;
            public int Chains { get; } = chains;
            public double Damage { get; } = damage;
            public uint Visual;
            public uint Parent;
            public readonly Dictionary<uint, long> TargetReady = [];
        }

        GalvanicCharge? galvanicCharge;
        GalvanicSphere? galvanicSphere;
        readonly HashSet<uint> galvanicObjects = [];

        internal void ArmGalvanicField(ushort skillId, SupportedGem source, Func<bool> current,
            Action<(int X, int Y), int, uint, double> hit)
        {
            int chance = source.Stats.GetValueOrDefault(Stat.BaseChanceToShockPctFromSkill);
            int duration = source.Stats.GetValueOrDefault(Stat.SkillBuffGrantsShockDurationPlusPct);
            var charge = new GalvanicCharge(source, chance, duration, current, hit);
            galvanicCharge = charge;
            Player.Area.Buffs.Set(Player.Id, new(GalvanicBuffRow, Player.Id, SourceSkill: skillId));
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[galvanic-armed] chance={chance} shockDuration={duration}");
            CheckGalvanicCharge(charge);
        }

        void CheckGalvanicCharge(GalvanicCharge charge)
        {
            if (!ReferenceEquals(galvanicCharge, charge)) return;
            if (!Active || !charge.Current()) { ClearGalvanicCharge(); return; }
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.25), () => CheckGalvanicCharge(charge));
        }

        internal (int Chance, int Duration) GalvanicShockBonus() =>
            galvanicCharge is { } charge && charge.Current() ? (charge.ShockChance, charge.ShockDuration) : default;

        void ClearGalvanicCharge()
        {
            if (galvanicCharge is null) return;
            galvanicCharge = null;
            Player.Area.Buffs.Clear(Player.Id, GalvanicBuffRow, Player.Id);
        }

        internal void GalvanicOnShock(SpawnedMonster monster, double now)
        {
            if (galvanicCharge is not { } charge || !charge.Current() || !monster.Hostile || monster.Invulnerable
                || monster.EntranceGrace || monster.ObjectId == 0
                || !ReferenceEquals(Player.Area.Monsters.GetValueOrDefault(monster.ObjectId), monster)) return;
            double magnitude = monster.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, now);
            if (magnitude <= 0) return;
            ClearGalvanicCharge();
            if (galvanicSphere is { } previous) EndGalvanicSphere(previous);
            var stats = CharacterPlayerStats(Player);
            int effect = charge.Source.Effect;
            int Own(uint name) => charge.Source.Stats.GetValueOrDefault(name);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), stats.GetValueOrDefault,
                Own, DurationKinds(Player, effect));
            int milliseconds = Own(Stat.BaseGalvanicFieldBeamDelayMs);
            if (duration <= 0 || milliseconds <= 0 || Own(Stat.GalvanicFieldMaximumNumberOfSpheres) <= 0) return;
            double frequency = Math.Max(.01, 1 + (Own(Stat.GalvanicFieldBeamFrequencyPlusPct)
                + stats.GetValueOrDefault(Stat.GalvanicFieldBeamFrequencyPlusPct)) / 100d);
            long interval = ProjectileRuntime.Time(Math.Max(TickSeconds, milliseconds / (1000d * frequency)));
            long retarget = ProjectileRuntime.Time(Own(Stat.GalvanicFieldRetargetingDelayMs) / 1000d);
            int baseRadius = GalvanicBaseRadius + (int)(magnitude / 10) * Own(Stat.GalvanicFieldRadiusPlusPer10PctIncreasedDamageTakenFromShock);
            int radius = SkillRadius(Player, baseRadius, charge.Source.Stats, spell: true, effect, stats);
            double damage = 1 + (int)(magnitude / 5) * Own(Stat.GalvanicFieldDamagePlusPctFinalPer5PctIncreasedDamageTakenFromShock) / 100d;
            int chains = Math.Clamp(Own(Stat.NumberOfChains) + stats.GetValueOrDefault(Stat.NumberOfChains)
                + Own(Stat.GalvanicFieldNumberOfChains), 0, 256);
            long at = Math.Max(Runtime.Now, ProjectileRuntime.Time(now));
            var field = new GalvanicSphere(charge, monster, monster.Walk.Position(now), at + ProjectileRuntime.Time(duration),
                interval, retarget, radius, chains, damage);
            galvanicSphere = field;
            PresentGalvanicSphere(field, monster.Dead ? 0 : monster.ObjectId);
            uint surge = Player.TakeObjectId();
            var opening = new EffectObject(field.At.X, field.At.Y, GalvanicSurgeArt);
            TrackGalvanicObject(surge, EffectObject.TypePath, opening.Blob());
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.5), () => RemoveGalvanicObject(surge));
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[galvanic-open] object={field.Visual:x} anchor={monster.ObjectId:x} shock={magnitude:F2} radius={radius} more={damage:F2} period={interval} retarget={retarget}");
            Runtime.Schedule(Math.Min(field.Until, at + interval), () => StepGalvanicSphere(field));
        }

        void PresentGalvanicSphere(GalvanicSphere field, uint parent)
        {
            if (field.Visual != 0) RemoveGalvanicObject(field.Visual);
            field.Parent = parent;
            field.Visual = Player.TakeObjectId();
            uint id = field.Visual;
            var body = new EffectObject(field.At.X, field.At.Y, GalvanicSphereArt)
                { Parent = parent, Scale = (float)(field.Radius * (250d / 23) / 400) };
            TrackGalvanicObject(id, EffectObject.TypePath, body.Blob());
        }

        void StepGalvanicSphere(GalvanicSphere field)
        {
            if (!ReferenceEquals(galvanicSphere, field)) return;
            long at = Runtime.Now;
            if (!Active || !field.Charge.Current() || at >= field.Until) { EndGalvanicSphere(field); return; }
            double now = ProjectileWorldTime(Player);
            var anchor = field.Anchor;
            if (ReferenceEquals(Player.Area.Monsters.GetValueOrDefault(anchor.ObjectId), anchor))
                field.At = anchor.Walk.Position(now);
            if (field.Parent != 0 && (anchor.Dead || !ReferenceEquals(Player.Area.Monsters.GetValueOrDefault(field.Parent), anchor)))
                PresentGalvanicSphere(field, 0);

            var origin = field.At;
            uint from = field.Visual;
            for (int remaining = field.Chains; remaining >= 0; remaining--)
            {
                uint target = 0;
                (int X, int Y) position = default;
                double best = double.PositiveInfinity;
                foreach (var (id, monster) in Player.Area.MonstersNear(field.At, field.Radius))
                {
                    if (at < field.TargetReady.GetValueOrDefault(id)
                        || monster is not { Hostile: true, Life: > 0, Dead: false, WorldHidden: false, Invulnerable: false }
                        || ArenaHazards.Untargetable(Player, monster)) continue;
                    var cell = monster.Walk.Position(now);
                    double distance = Dist(origin, cell);
                    if (distance >= best || Dist(field.At, cell) > field.Radius) continue;
                    target = id;
                    position = cell;
                    best = distance;
                }
                if (target == 0) break;
                field.TargetReady[target] = at + Math.Max(1, field.Retarget);
                GalvanicBeam(origin, from, position, target);
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[galvanic-beam] target={target:x} at={now:F3} centre={field.At.X},{field.At.Y}");
                field.Charge.Hit(field.At, field.Radius, target, field.Damage);
                origin = position;
                from = target;
            }
            Runtime.Schedule(Math.Min(field.Until, at + field.Interval), () => StepGalvanicSphere(field));
        }

        void GalvanicBeam((int X, int Y) at, uint from, (int X, int Y) end, uint target)
        {
            uint id = Player.TakeObjectId();
            var beam = new BeamEffectObject(at.X, at.Y, from, end.X * BeamEffectObject.WorldPerCell,
                end.Y * BeamEffectObject.WorldPerCell, target, GalvanicBeamRow);
            TrackGalvanicObject(id, BeamEffectObject.TypePath, beam.Blob());
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.5), () => RemoveGalvanicObject(id));
        }

        void TrackGalvanicObject(uint id, string type, byte[] blob)
        {
            galvanicObjects.Add(id);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), blob), wake: true);
        }

        void RemoveGalvanicObject(uint id) => WorldRecords(id, () =>
        {
            if (!galvanicObjects.Remove(id)) return null;
            Player.UntrackProjectileObject(id);
            return [InstanceProtocol.RemoveObject(id)];
        }, remove: true);

        void EndGalvanicSphere(GalvanicSphere field)
        {
            galvanicSphere = null;
            RemoveGalvanicObject(field.Visual);
            if (ServerTrace.Projectiles) Console.WriteLine($"[galvanic-end] object={field.Visual:x}");
        }

        void ClearGalvanicField()
        {
            ClearGalvanicCharge();
            galvanicSphere = null;
            foreach (uint id in galvanicObjects) RetireSkillEffectObject(id);
            galvanicObjects.Clear();
        }
    }
}
