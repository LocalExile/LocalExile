using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static bool IsGroundBladeBlast(string? effect) => effect is "BladeBlast" or "BladeBlastRoyale";

    public sealed partial class ProjectileSession
    {
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
