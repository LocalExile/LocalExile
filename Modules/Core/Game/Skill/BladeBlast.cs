using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static bool IsGroundBladeBlast(string? effect) => effect is "BladeBlast" or "BladeBlastRoyale";
    static bool IsBladeBlastOfUnloading(string? effect) => effect == "BladeBlastAltX";

    public sealed partial class ProjectileSession
    {
        internal void ReleaseBladeBlastOfUnloading(uint actor, int effect, IReadOnlyDictionary<uint, int> own,
            IReadOnlyDictionary<uint, int> caster, Func<(int X, int Y)> centre, Func<bool> current,
            Action<(int X, int Y), int, double> hit, int repeat = 0)
        {
            var area = SkillAreaStats(Player, own, effect, caster);
            int Get(uint stat) => area.GetValueOrDefault(stat);
            area[SkillArea.Increase] = Get(SkillArea.Increase) + repeat * Get(SkillArea.IncreasedPerRepeat)
                + Get(Stat.BladeBlastSkillAreaOfEffectPlusPct);
            int baseRadius = Get(Stat.ActiveSkillBaseAreaOfEffectRadius);
            if (baseRadius <= 0) baseRadius = 16;
            // BladeBurst's native chain tracks the number used out of ten (3.29.3c 0x141a9c050).
            var run = bladeVortices.GetValueOrDefault(actor);
            var blades = run is not null && run.Current()
                ? run.Blades.UnorderedItems.Where(b => b.Priority > Runtime.Now)
                    .OrderBy(b => b.Priority).ThenBy(b => b.Element).Take(10).Select(b => b.Element).ToArray()
                : [];
            double damage = Math.Max(0, 1 + (long)blades.Length * Get(Stat.BladeVortexDamagePlusPctPerBladeFinal) / 100d);
            hit(centre(), SkillArea.Radius(baseRadius, Get, spell: true), damage);
            if (blades.Length == 0 || run is null) return;

            int detonated = 0;
            int growth = Get(Stat.BladeBurstAreaOfEffectPlusPctFinalPerBladeVortexBladeDetonated);
            int retainChance = Get(Stat.BladeBlastDetonatedBladesNotRemovedPctChance);
            // One area frame between successive detonations, pending a measured server contact interval.
            long due = Runtime.Now;
            long spacing = ProjectileRuntime.Time(FrameSeconds);
            foreach (ushort blade in blades)
            {
                due += spacing;
                Runtime.Schedule(due, () =>
                {
                    if (!Active || !current() || !DetonateBladeVortexBlade(actor, run, blade, retainChance)) return;
                    double areaMore = Math.Max(0, 1 + (long)++detonated * growth / 100d);
                    int radius = SkillArea.Radius(baseRadius, Get, spell: true, areaMore);
                    var at = centre();
                    Effect(at.X, at.Y, "Metadata/Effects/Spells/bladeburst/bladeburst_vortex.ao", ProjectileRuntime.Time(.4), radius);
                    hit(at, radius, damage);
                });
            }
        }

        internal void ReleaseBladeBlast(int effect, IReadOnlyDictionary<uint, int> own,
            IReadOnlyDictionary<uint, int> caster, (int X, int Y) aim, Func<bool> current,
            Action<(int X, int Y), int, bool> hit, int repeat = 0)
        {
            var area = SkillAreaStats(Player, own, effect, caster);
            int Get(uint stat) => area.GetValueOrDefault(stat);
            int genericIncrease = Get(SkillArea.Increase) + repeat * Get(SkillArea.IncreasedPerRepeat);
            area[SkillArea.Increase] = genericIncrease + Get(Stat.BladeBlastSkillAreaOfEffectPlusPct);
            int initialRadius = SkillArea.Radius(SkillArea.BaseRadius(SkillsFor(Player).EffectId(effect), Get), Get, spell: true);
            hit(aim, initialRadius, false);
            if (Player.LingeringBladeSources.Count == 0) return;

            // BladeBurst (3.29.3c 0x141a9b850, 0x141a9bff0): each chain has a 50-blade budget and
            // the secondary radius starts at 20 cells, with its own increased-area modifier.
            area[SkillArea.Increase] = genericIncrease + Get(Stat.BladeBlastTriggerDetonationAreaOfEffectPlusPct);
            int burstRadius = SkillArea.Radius(20 + Get(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), Get, spell: true);
            int keepChance = Get(Stat.BladeBlastDetonatedBladesNotRemovedPctChance);
            var queued = new HashSet<uint>();
            const int limit = 50;
            int detonated = 0;
            FindBlades(aim, initialRadius);

            void FindBlades((int X, int Y) centre, int radius)
            {
                if (detonated >= limit) return;
                double now = ProjectileWorldTime(Player);
                long due = Runtime.Now + ProjectileRuntime.Time(FrameSeconds);
                foreach (var source in Player.LingeringBladeSources)
                    foreach (var blade in source.Blades)
                        if (blade.Landed == 0 && blade.Gone > now && Dist(centre, blade.At) <= radius && queued.Add(blade.Id))
                            Runtime.Schedule(due, () => Detonate(source, blade.Id));
            }

            void Detonate(BladefallTrarthusState source, uint id)
            {
                if (!Active || !current() || detonated >= limit) return;
                double now = ProjectileWorldTime(Player);
                int index = source.Blades.FindIndex(b => b.Id == id && b.Landed == 0 && b.Gone > now);
                if (index < 0) return;
                var blade = source.Blades[index];
                if (keepChance <= 0 || keepChance < 100 && AreaRng(Player).Next(100) >= keepChance)
                {
                    source.Blades.RemoveAt(index);
                    World(id, InstanceEntities.TransitionableUpdate(id, GroundBladeObject.TransitionableIndex, 3));
                    // Keep the fading object in the normal area-transfer cleanup until its removal is sent.
                    Player.TrackProjectileObject(id);
                    Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.4), () => RemoveProjectileBlade(id));
                }
                detonated++;
                hit(blade.At, burstRadius, true);
                FindBlades(blade.At, burstRadius);
            }
        }
    }
}
