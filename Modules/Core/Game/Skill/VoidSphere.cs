using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static bool VoidSphereEffect(string? effect) => effect is "VoidSphere" or "VoidSphereAltX" or "VoidSphereRoyale";

    sealed record VoidSphere(ProjectileSession Source, uint Caster, uint Id, (int X, int Y) Centre,
        int Radius, int Hinder, double Start, double End, Func<bool> Current)
    {
        public bool Active(double at) => at >= Start && at < End && Source.Active && Source.Player.PlayerLife > 0;
    }

    static readonly RuleTable<GameArea, Dictionary<uint, VoidSphere>> VoidSpheres = new();

    static int VoidSphereHinder(GameArea area, SpawnedMonster target, double now)
    {
        if (!VoidSpheres.TryGetValue(area, out var spheres) || spheres.Count == 0
            || !target.Hostile || target.Dead || target.WorldHidden || target.OwnerId is not null
            || target.Effects[Stat.ImmuneToHinder] != 0 || target.Effects[Stat.YouCannotBeHindered] != 0) return 0;
        var at = target.Walk.Position(now);
        double slow = 0;
        foreach (var sphere in spheres.Values)
            if (sphere.Active(now))
                slow = Math.Max(slow, sphere.Hinder * (1 - Dist(at, sphere.Centre) / sphere.Radius));
        return (int)Math.Round(slow * Math.Max(0, 1 + target.Effects[Stat.HinderEffectOnSelfPlusPct] / 100d));
    }

    // Run at death; a pre-existing corpse in the circle is not consumed.
    static bool ConsumeVoidSphereCorpseLocked(Player player, uint id, SpawnedMonster corpse, double now)
    {
        var area = player.Area;
        if (!VoidSpheres.TryGetValue(area, out var spheres) || spheres.Count == 0
            || !corpse.Hostile || corpse.OwnerId is not null || corpse.Ailments.Shattered || !IsCorpse(corpse)
            || area.Monsters.GetValueOrDefault(id) != corpse) return false;
        VoidSphere? consumer = null;
        var at = corpse.Walk.Position(now);
        foreach (var sphere in spheres.Values)
            if (sphere.Active(now) && Dist(sphere.Centre, at) <= sphere.Radius && sphere.Current()
                && (consumer is null || sphere.Start > consumer.Start || sphere.Start == consumer.Start && sphere.Id < consumer.Id))
                consumer = sphere;
        if (consumer is null) return false;
        corpse.CorpseConsumed = true;
        area.Monsters.Remove(id);
        if (consumer.Caster == consumer.Source.Player.Id)
            NoteCorpseConsumedLocked(consumer.Source.Player, corpse);
        return true;
    }

    public sealed partial class ProjectileSession
    {
        const string VoidSphereArt = "Metadata/Effects/Spells/black_hole/void.ao";
        const string VoidSpherePulseArt = "Metadata/Effects/Spells/black_hole/void_shockwave.ao";
        GameArea? voidSphereArea;

        internal bool HasVoidSphere(uint caster) => VoidSpheres.TryGetValue(Player.Area, out var spheres)
            && spheres.ContainsKey(caster);

        internal void ReleaseVoidSphere(uint caster, int effect, IReadOnlyDictionary<uint, int> own,
            (int X, int Y) centre, Func<bool> current, Action<(int X, int Y), int, double> hit)
        {
            var stats = CharacterPlayerStats(Player);
            double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                stats.GetValueOrDefault, own.GetValueOrDefault, DurationKinds(Player, effect));
            int milliseconds = own.GetValueOrDefault(Stat.BaseBlackholeTickRateMs);
            int radius = SkillRadius(Player, own, spell: true, effect);
            if (duration <= 0 || milliseconds <= 0 || radius <= 0) return;
            double rate = 1 + ((double)stats.GetValueOrDefault(Stat.BlackholePulseFrequencyPlusPct)
                + own.GetValueOrDefault(Stat.BlackholePulseFrequencyPlusPct)) / 100;
            long interval = ProjectileRuntime.Time(Math.Max(TickSeconds, milliseconds / (1000 * Math.Max(.01, rate))));
            long end = Runtime.Now + ProjectileRuntime.Time(duration);
            int hinder = Math.Max(0, -own.GetValueOrDefault(Stat.BlackholeHinderPct));
            uint id = Player.TakeObjectId();
            var area = Player.Area;
            if (!VoidSpheres.TryGetValue(area, out var spheres)) VoidSpheres.AddOrUpdate(area, spheres = []);
            if (spheres.TryGetValue(caster, out var previous)) previous.Source.RemoveVoidSphere(spheres, previous, retire: false);
            var sphere = new VoidSphere(this, caster, id, centre, radius, hinder, ProjectileWorldTime(Player),
                end / (double)ProjectileRuntime.QuantaPerSecond, current);
            voidSphereArea = area;
            spheres.Add(caster, sphere);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(centre.X, centre.Y, VoidSphereArt).Blob()), wake: true);
            Runtime.Schedule(end, () => RemoveVoidSphere(spheres, sphere, retire: false));
            int pulses = 0;
            Pulse();

            void Pulse()
            {
                if (spheres.GetValueOrDefault(caster) != sphere) return;
                if (!Active || !current()) { RemoveVoidSphere(spheres, sphere, retire: false); return; }
                double now = ProjectileWorldTime(Player);
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[void-sphere] actor={id:x} caster={caster:x} at={now:F3} centre={centre.X},{centre.Y} pulse={pulses + 1}");
                // The first pulses draw enemies more strongly. Pull does not stagger or restart an enemy's attack.
                double pull = pulses++ < 3 ? 4 : 1;
                foreach (var (targetId, monster) in area.MonstersNear(centre, radius))
                {
                    if (monster is not { Hostile: true, WorldHidden: false, Dead: false, Life: > 0, OwnerId: null,
                            Scripted: false, StageRunning: false, Asleep: false, Rarity: not MonsterRarity.Unique }
                        || monster.Variety is { MovementSpeed: 0 } or { Immobile: true }) continue;
                    var at = monster.Walk.Position(now);
                    if (Dist(at, centre) > radius) continue;
                    int size = LivingCollision.Size(monster);
                    if (KnockbackDestination(centre, at, pull, (x, y) => Player.Layout.WalkableFor(x, y, size)
                        && !Player.Barrier.Contains((x, y)), reversed: true, minSeparation: size) is not { } landing) continue;
                    monster.Walk.Teleport(landing.X, landing.Y, now);
                    World(targetId, InstanceProtocol.Teleport(targetId, landing.X, landing.Y));
                }
                Effect(centre.X, centre.Y, VoidSpherePulseArt, Math.Min(interval, ProjectileRuntime.Time(.35)), radius);
                hit(centre, radius, now);
                long next = Runtime.Now + interval;
                if (next < end) Runtime.Schedule(next, Pulse);
            }
        }

        void RemoveVoidSphere(Dictionary<uint, VoidSphere> spheres, VoidSphere sphere, bool retire)
        {
            if (spheres.GetValueOrDefault(sphere.Caster) != sphere) return;
            spheres.Remove(sphere.Caster);
            if (retire) RetireSkillEffectObject(sphere.Id);
            else WorldRecords(sphere.Id, () =>
            {
                Player.UntrackProjectileObject(sphere.Id);
                return [InstanceProtocol.RemoveObject(sphere.Id)];
            }, remove: true);
        }

        void ClearVoidSpheres()
        {
            if (voidSphereArea is not { } area) return;
            voidSphereArea = null;
            if (area.Driven && !area.Owned) area.Defer(Remove);
            else Remove(area);
            void Remove(GameArea target)
            {
                if (!VoidSpheres.TryGetValue(target, out var spheres)) return;
                foreach (var sphere in spheres.Values.Where(sphere => sphere.Source == this).ToArray())
                    RemoveVoidSphere(spheres, sphere, retire: true);
            }
        }
    }
}
