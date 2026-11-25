using GameServer.Core.World;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        internal int SunderWaveGeneration { get; set; }
    }

    // NewSunder (ActionTypes row 464) gives the wave and its victim-centred shockwaves separate contacts.
    sealed class SunderWave
    {
        internal const ushort Action = 0x5fdd;
        const int FirstImpactDistance = 7, WaveBaseRadius = 9, ShockwaveBaseRadius = 12;
        const int BaseStepDistance = 10, RedirectRange = 120;

        readonly Player player;
        readonly ProjectileSession session;
        readonly int epoch;
        readonly int? generation;
        readonly int maxSegments, minimumSegments, radiusGrowth, baseRadiusPlus, shockwaveLimit;
        readonly double interval, waveScale;
        readonly int shockwaveRadius;
        readonly Func<uint, (int X, int Y), bool>? filter;
        readonly uint excluded;
        readonly uint? onlyTarget;
        readonly ISet<uint>? once;
        readonly HashSet<uint> waveTargets = [];
        readonly List<(uint Id, (int X, int Y) At)> emitters = [];
        readonly Func<int, int, int> terrain;
        (int X, int Y) centre;
        (double X, double Y) direction;
        double dueAt;
        int segments, headingSegments, redirects;

        internal bool IsShockwave { get; private set; }
        internal double ShockwaveMore { get; }

        internal SunderWave(Player player, (int X, int Y) from, (int X, int Y) aim,
            IReadOnlyDictionary<uint, int> skillStats, IReadOnlyDictionary<uint, int> areaStats,
            double radiusFactor, int epoch, uint excluded, uint? onlyTarget, ISet<uint>? once,
            Func<uint, (int X, int Y), bool>? filter)
        {
            this.player = player;
            this.epoch = epoch;
            this.excluded = excluded;
            this.onlyTarget = onlyTarget;
            this.once = once;
            this.filter = filter;
            session = ProjectilesFor(player);
            direction = GroundSlamWave.Direction(from, aim);
            dueAt = AreaTime(player);
            maxSegments = skillStats.GetValueOrDefault(Stat.SunderWaveMaxSteps);
            minimumSegments = skillStats.GetValueOrDefault(Stat.SunderWaveMinSteps);
            if (minimumSegments > 0) generation = ++session.SunderWaveGeneration;
            radiusGrowth = skillStats.GetValueOrDefault(Stat.SunderWaveRadiusPlusPerStep);
            baseRadiusPlus = areaStats.GetValueOrDefault(Stat.ActiveSkillBaseRadiusPlus);
            int delay = skillStats.GetValueOrDefault(Stat.BaseSunderWaveDelayMs);
            interval = Math.Max(FrameMs / 1000d, delay / 1000d *
                (1 + skillStats.GetValueOrDefault(Stat.SunderWaveDelayPlusPct) / 100d));
            shockwaveLimit = skillStats.GetValueOrDefault(Stat.SunderShockwaveLimitPerCascade);
            redirects = skillStats.GetValueOrDefault(Stat.SunderNumberOfFakeChains);
            ShockwaveMore = Math.Max(0, 1 + skillStats.GetValueOrDefault(Stat.ShockwaveSlamExplosionDamagePlusPctFinal) / 100d);
            waveScale = RadiusScale(Stat.SunderWaveAreaOfEffectPlusPct);
            shockwaveRadius = Math.Max(0, (int)Math.Floor((ShockwaveBaseRadius + baseRadiusPlus) *
                RadiusScale(Stat.SunderShockwaveAreaOfEffectPlusPct)));

            double RadiusScale(uint bonus)
            {
                int specific = areaStats.GetValueOrDefault(bonus);
                int Get(uint key) => areaStats.GetValueOrDefault(key) + (key == SkillArea.Increase ? specific : 0);
                double scale = Math.Floor(100 * Math.Sqrt(SkillArea.Modifier(Get, spell: false))) / 100;
                return scale * Math.Max(0, 1 + Get(Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal) / 100d) * radiusFactor;
            }

            var layout = player.Layout;
            terrain = (x, y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH
                && layout.WalkableFor(x, y, 0) && !player.Barrier.Contains((x, y)) ? 1 : 0;
            // The attack's contact marker is projected seven cells in front of the attacker.
            var impact = Offset(from, FirstImpactDistance);
            centre = ClientPathfinder.LineBelow(from, impact, terrain, 1, out var floor) ? floor : impact;
        }

        bool Active => CurrentGameWork && session.Active && player.ProjectileActive(epoch) && player.PlayerLife > 0;
        bool Superseded => generation is int use && use != session.SunderWaveGeneration && segments >= minimumSegments;

        internal void RecordEmitter(uint id, (int X, int Y) at) => emitters.Add((id, at));

        internal async Task<bool> AppendContacts(List<(uint Id, SpawnedMonster M)> targets)
        {
            int before = targets.Count;
            while (Active)
            {
                if (emitters.Count > 0)
                {
                    using (ProjectileMutation(player))
                    {
                        if (!Active) return false;
                        IsShockwave = true;
                        int count = Math.Min(emitters.Count, shockwaveLimit);
                        var rng = AreaRng(player);
                        for (int i = 0; i < count; i++)
                        {
                            int pick = rng.Next(i, emitters.Count);
                            (emitters[i], emitters[pick]) = (emitters[pick], emitters[i]);
                            var (id, at) = emitters[i];
                            Collect(targets, at, shockwaveRadius, id);
                        }
                        if (redirects > 0) TryRedirect();
                        emitters.Clear();
                    }
                    if (targets.Count > before) return true;
                }

                if (headingSegments >= maxSegments || Superseded
                    || !await WaitProjectileTime(player, dueAt, epoch)) return false;

                using (ProjectileMutation(player))
                {
                    if (!Active || Superseded) return false;
                    if (segments > 0 && !TryAdvance()) return false;
                    int radius = Math.Max(0, (int)Math.Floor((WaveBaseRadius + baseRadiusPlus + segments * radiusGrowth) * waveScale));
                    segments++;
                    headingSegments++;
                    dueAt += interval;
                    IsShockwave = false;
                    Collect(targets, centre, radius);
                }
                if (targets.Count > before) return true;
            }
            return false;
        }

        void Collect(List<(uint Id, SpawnedMonster M)> targets, (int X, int Y) at, int radius, uint emitter = 0)
        {
            if (radius <= 0) return;
            double now = ProjectileWorldTime(player);
            foreach (var (id, monster) in player.Area.MonstersNear(at, radius))
            {
                if (id == emitter || !Admits(id, monster)) continue;
                if (!IsShockwave && (waveTargets.Contains(id) || once?.Contains(id) == true)) continue;
                var position = monster.Walk.Position(now);
                if (!ClientPathfinder.InReach(at, position, 0, radius, LivingCollision.Size(monster), terrain, precision: 1)
                    || filter?.Invoke(id, position) == false) continue;
                if (!IsShockwave)
                {
                    waveTargets.Add(id);
                    once?.Add(id);
                }
                targets.Add((id, monster));
            }
        }

        bool Admits(uint id, SpawnedMonster monster) => id != excluded && (onlyTarget is null || id == onlyTarget)
            && monster is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false };

        bool TryAdvance()
        {
            int distance = Math.Max(1, (int)Math.Floor((BaseStepDistance + 2 * segments + baseRadiusPlus) * waveScale));
            while (true)
            {
                var next = Offset(centre, distance);
                if (!ClientPathfinder.LineBelow(centre, next, terrain, 1))
                {
                    centre = next;
                    return true;
                }
                if (!TryRedirect(distance)) return false;
            }
        }

        (int X, int Y) Offset((int X, int Y) from, int cells) => Offset(from, direction, cells);

        static (int X, int Y) Offset((int X, int Y) from, (double X, double Y) heading, int cells) =>
            (from.X + (int)Math.Floor(heading.X * cells + .5), from.Y + (int)Math.Floor(heading.Y * cells + .5));

        bool TryRedirect(int stepDistance = 0)
        {
            if (redirects <= 0) return false;
            double now = ProjectileWorldTime(player);
            (int X, int Y)? target = null;
            int eligible = 0;
            var rng = AreaRng(player);
            foreach (var (id, monster) in player.Area.MonstersNear(centre, RedirectRange))
            {
                if (waveTargets.Contains(id) || !Admits(id, monster)) continue;
                var at = monster.Walk.Position(now);
                if (at == centre || !ClientPathfinder.InReach(centre, at, 0, RedirectRange, 0, terrain, precision: 1)
                    || filter?.Invoke(id, at) == false) continue;
                if (stepDistance > 0 && ClientPathfinder.LineBelow(centre,
                        Offset(centre, GroundSlamWave.Direction(centre, at), stepDistance), terrain, 1)) continue;
                if (rng.Next(++eligible) == 0) target = at;
            }
            if (target is not { } heading) return false;
            direction = GroundSlamWave.Direction(centre, heading);
            headingSegments = 0;
            redirects--;
            return true;
        }
    }
}
