using GameServer.Core.Projectiles;

public static partial class Game
{
    static readonly RuleTable<GameArea, List<EarthshatterSpike>> EarthshatterSpikes = new();

    internal sealed record EarthshatterAttack(ProjectileSession Session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem, ushort Sequence, int Repeat,
        SkillCriticalRoll Critical, double Ruthless, bool Exerted, RetainedAreaAttack Fissure, RetainedAreaAttack Burst)
    {
        internal bool Active => Session.Active && Session.Player.PlayerLife > 0;

        internal void Detonate((int X, int Y) centre, int radius)
        {
            var candidates = Session.Player.Area.MonstersNear(centre, radius)
                .Where(enemy => enemy.Value is { Dead: false, Life: > 0, Hostile: true, OwnerId: null, WorldHidden: false }).Select(enemy => enemy.Key).ToArray();
            if (candidates.Length != 0) Hit(centre, radius, Burst, candidates: candidates);
        }

        internal void Hit((int X, int Y) centre, int radius, RetainedAreaAttack attack,
            ISet<uint>? struck = null, IReadOnlyList<uint>? candidates = null)
        {
            var player = Session.Player;
            var hit = AreaSkillHitStrike(player, Gem.Id, Gem, centre, centre, immediate: true, Sequence,
                actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, Critical,
                channelGeneration: null, targetId: 0, repeat: Repeat, radiusOverride: radius, once: struck, repeatDelay: 0,
                radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: () => Active, discharge: null, strikeIndex: 0,
                firstContactAt: null, Ruthless, priorTargets: null, scaledBlastChaos: 0,
                singleStrikeTarget: null, onHit: null, scheduledSession: Session, Exerted, prepaidRawExtra: 0,
                baseChaos: 0, hammer: null, retainedAttack: attack, allowMeleeSplash: false, contactTargets: candidates);
            if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Earthshatter hit must not yield.");
            hit.GetAwaiter().GetResult();
        }
    }

    sealed class EarthshatterSpike(EarthshatterAttack attack, (int X, int Y) at, long expires, int radius, bool timed)
    {
        internal EarthshatterAttack Attack { get; } = attack;
        internal (int X, int Y) At { get; } = at;
        internal long Expires { get; } = expires;
        internal int Radius { get; } = radius;
        internal bool Timed { get; } = timed;
    }

    public sealed partial class ProjectileSession
    {
        GameArea? earthshatterArea;

        void ClearEarthshatterSpikes()
        {
            if (earthshatterArea is not { } area) return;
            earthshatterArea = null;
            void Remove()
            {
                if (EarthshatterSpikes.TryGetValue(area, out var spikes))
                    spikes.RemoveAll(spike => ReferenceEquals(spike.Attack.Session, this));
            }
            if (area.Driven && !area.Owned) area.Defer(_ => Remove());
            else Remove();
        }

        internal void PlaceEarthshatterSpike(EarthshatterAttack attack, (int X, int Y) at, int capacity,
            double seconds, int radius, bool timed)
        {
            if (capacity <= 0 || seconds <= 0 || !attack.Active) return;
            var area = earthshatterArea ??= Player.Area;
            var spikes = EarthshatterSpikes.GetOrCreateValue(area);
            int overflow = spikes.Count(spike => ReferenceEquals(spike.Attack.Session, this)) - capacity + 1;
            for (int i = 0; i < spikes.Count && overflow > 0;)
            {
                if (ReferenceEquals(spikes[i].Attack.Session, this))
                {
                    spikes.RemoveAt(i);
                    overflow--;
                }
                else i++;
            }
            long expires = Runtime.Now + ProjectileRuntime.Time(seconds);
            var spike = new EarthshatterSpike(attack, at, expires, radius, timed);
            spikes.Add(spike);
            Runtime.Schedule(expires, () =>
            {
                if (spikes.Remove(spike) && timed && attack.Active) attack.Detonate(at, radius);
            });
        }
    }

    static void TriggerEarthshatterSpikesLocked(Player trigger, (int X, int Y) centre, double radius, double now)
    {
        if (!EarthshatterSpikes.TryGetValue(trigger.Area, out var spikes) || spikes.Count == 0) return;
        long triggeredAt = ProjectileRuntime.Time(now);
        long when = triggeredAt;
        long interval = ProjectileRuntime.Time(.05);
        for (int i = 0; i < spikes.Count;)
        {
            var spike = spikes[i];
            if (spike.Timed || triggeredAt >= spike.Expires || !spike.Attack.Active || Dist(centre, spike.At) > radius)
            {
                i++;
                continue;
            }
            spikes.RemoveAt(i);
            var session = spike.Attack.Session;
            using (ProjectileMutation(session.Player, advance: false))
            {
                if (spike.Attack.Active)
                    session.Runtime.Schedule(Math.Max(session.Runtime.Now, when), () =>
                    {
                        if (spike.Attack.Active) spike.Attack.Detonate(spike.At, spike.Radius);
                    });
            }
            when += interval;
        }
    }

    static void ScheduleEarthshatterLocked(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem admitted,
        (int X, int Y) from, (int X, int Y) to, ushort sequence, int repeats, AttackHand hand,
        SkillCatalog.Unarmed weapon, int? offCritical, SkillCriticalRoll critical, double radiusMore, double damageMore,
        double ruthless, bool exerted)
    {
        var session = ProjectilesFor(player);
        var layout = player.Layout;
        var own = admitted.Stats;
        var stats = CharacterPlayerStats(player);
        var areaStats = SkillAreaStats(player, own, gem.Effect, stats);
        int AreaStat(uint key) => areaStats.GetValueOrDefault(key);
        int Own(uint key) => own.GetValueOrDefault(key);
        bool CanPass((int X, int Y) cell) => cell.X >= 0 && cell.Y >= 0 && cell.X < layout.GridW && cell.Y < layout.GridH
            && layout.WalkableFor(cell.X, cell.Y, 0) && !player.Barrier.Contains(cell);

        int repeatCount = repeats == 0 ? 0 : Math.Max(0, MeleeAttackRepeats(player, skillId) - repeats);
        float areaScale = (float)((1 + SkillArea.RadiusPercent(AreaStat, spell: false) / 100d) * radiusMore);
        var fissures = EarthshatterFissures.Plan(from, to, sequence, player.Id, 0, (uint)repeatCount, 0,
            AreaStat(Stat.SpikeSlamNumSpikes), AreaStat(Stat.SpikeSlamAdditionalSpikePctChance),
            AreaStat(Stat.SpikeSlamFissureLengthPlusPct), areaScale, CanPass);
        if (fissures.Count == 0) return;

        double More(uint key) => Math.Max(0, 1 + AreaStat(key) / 100d);
        double fissureMore = More(Stat.SpikeSlamFissureDamagePlusPctFinal) * damageMore;
        double spikeMore = More(Stat.SpikeSlamExplosionDamagePlusPctFinal) * More(Stat.SpikeSlamSpikeDamagePlusPctFinal) * damageMore;
        var fissureSource = CaptureAreaAttack(player, skillId, admitted, hand, weapon, offCritical, fissureMore, fissureMore, "fissure");
        var attack = new EarthshatterAttack(session, gem, sequence, repeats, critical, ruthless, exerted, fissureSource,
            fissureSource with { HitMore = spikeMore, AilmentMore = spikeMore, Phase = "spike" });
        int capacity = AreaStat(Stat.SpikeSlamMaxSpikes);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), stats.GetValueOrDefault, Own,
            DurationKinds(player, gem.Effect));
        int burstRadius = SkillArea.Radius(18, AreaStat, spell: false,
            More(Stat.EarthshatterSpikeAreaOfEffectPlusPctFinal));
        burstRadius = (int)(burstRadius * radiusMore);
        bool timed = SkillsFor(player).BehaviourVariation(gem.Effect) == EarthshatterFissures.TimedVariation;
        var start = fissures[0].Start;
        int scan = fissures.Max(fissure => fissure.Length + fissure.Width) + 1;
        int steps = fissures.Max(fissure => fissure.Steps);
        var struck = new HashSet<uint>();
        var candidates = new List<uint>();
        long begins = session.Runtime.Now;
        int step = 0;
        void Advance()
        {
            if (!attack.Active) return;
            double now = ProjectileWorldTime(player);
            candidates.Clear();
            foreach (var (id, enemy) in player.Area.MonstersNear(start, scan))
                if (!struck.Contains(id) && enemy is { Dead: false, Life: > 0, Hostile: true, OwnerId: null, WorldHidden: false })
                {
                    var cell = enemy.Walk.Position(now);
                    double body = LivingCollision.Size(enemy);
                    foreach (var fissure in fissures)
                        if (step <= fissure.Steps && fissure.Reaches(cell, body, step))
                        {
                            candidates.Add(id);
                            break;
                        }
                }
            if (candidates.Count != 0) attack.Hit(start, scan, attack.Fissure, struck, candidates);
            foreach (var fissure in fissures)
                if (fissure.Steps == step) session.PlaceEarthshatterSpike(attack, fissure.End, capacity, duration, burstRadius, timed);

            if (step < steps)
                session.Runtime.Schedule(begins + ProjectileRuntime.Time(++step * EarthshatterFissures.StepSeconds), Advance);
        }
        Advance();
    }
}
