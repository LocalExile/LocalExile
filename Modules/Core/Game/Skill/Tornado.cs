using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string TornadoTriggeredEffect = "TornadoTriggered";
    static bool TornadoEffect(string? effect) => effect is "Tornado" or "TornadoAltX" or "TornadoAltY" or TornadoTriggeredEffect;

    static void TriggerWeaponTornado(Player player, int effect, (int X, int Y) aim, double now)
    {
        if (SkillsFor(player).EffectId(effect) is not ("SplitArrow" or "SplitArrowAltX" or "SplitArrowAltY"
            or "TornadoShot" or "TornadoShotAltX" or "TornadoShotAltY")) return;
        if (!AllWornGrantedSkillsOf(player).Any(grant => grant.EffectId == TornadoTriggeredEffect)) return;
        using var mutation = ProjectileMutation(player, advance: false);
        var state = LinkedTriggersOf(player);
        var skill = GemSkillEffects(player).FirstOrDefault(s => SkillsFor(player).EffectId(s.Effect) == TornadoTriggeredEffect);
        if (skill.Id == 0 || now < state.ReadyAt.GetValueOrDefault(skill.Effect)) return;
        var source = SupportedSkill(player, skill.Id);
        if (source.WornGrant is not { EffectId: TornadoTriggeredEffect } grant || !source.RequirementsMet
            || !RandomRolls.Chance(source.Stats.GetValueOrDefault(Stat.ChanceToTriggerLevel20TornadoOnAttackFromSplitArrowOrTornadoShotPct),
                100, AreaRng(player))) return;
        var session = ProjectilesFor(player);
        bool Current() => session.Active && player.ProjectileActive(state.Epoch) && !player.Paused && player.PlayerLife > 0
            && SupportedSkill(player, skill.Id) is { RequirementsMet: true } live && live.WornGrant == grant
            && SameTriggerSource(source, live);
        TryReleaseAutomaticPayload(player, session, skill.Id, source, null, aim, Current, now, "Tornado", out _);
    }

    static DamageType? TornadoElement(Player player, SupportedGem source) =>
        source.Stats.GetValueOrDefault(Stat.ElementalStrikePhysicalDamagePctToConvert) <= 0
            ? null : AreaRng(player).Next(3) switch { 0 => DamageType.Fire, 1 => DamageType.Cold, _ => DamageType.Lightning };

    static SupportedGem TornadoDamageSource(SupportedGem source, DamageType? element)
    {
        if (element is null) return source;
        uint conversion = element switch
        {
            DamageType.Fire => Stat.SkillPhysicalDamagePctToConvertToFire,
            DamageType.Cold => Stat.SkillPhysicalDamagePctToConvertToCold,
            _ => Stat.SkillPhysicalDamagePctToConvertToLightning,
        };
        return source with
        {
            Stats = new Dictionary<uint, int>(source.Stats)
            {
                [conversion] = source.Stats.GetValueOrDefault(Stat.ElementalStrikePhysicalDamagePctToConvert),
            },
        };
    }

    sealed class TornadoOrb(uint id, int family, MonsterCatalog.Variety variety, ProjectileVector at, ProjectileVector direction,
        long created, long chaseAt, long endsAt, long pulseEvery, int radius, double speed, int hitLimit, int absorb,
        DamageType? element, Func<bool> current, Action<(int X, int Y), int> hit)
    {
        public uint Id { get; } = id;
        public long CreatedAt { get; } = created;
        public int Family { get; } = family;
        public MonsterCatalog.Variety Variety { get; } = variety;
        public ProjectileVector At = at;
        public ProjectileVector Destination = at;
        public ProjectileVector Direction { get; } = direction;
        public long MoveStart = created, MoveEnd = created;
        public long ChaseAt { get; } = chaseAt;
        public long EndsAt { get; } = endsAt;
        public long NextPulse = created + pulseEvery;
        public long PulseEvery { get; } = pulseEvery;
        public int Radius { get; } = radius;
        public double Speed { get; } = speed;
        public int HitLimit { get; } = hitLimit;
        public int Absorb { get; } = absorb;
        public int Hits;
        public DamagePacket Absorbed = DamagePacket.Empty;
        public DamageType? Element { get; } = element;
        public Func<bool> Current { get; } = current;
        public Action<(int X, int Y), int> Hit { get; } = hit;
        public ProjectileActor Actor = null!;

        public ProjectileVector Position(long now) => now >= MoveEnd ? Destination
            : At + (Destination - At) * Math.Clamp((now - MoveStart) / (double)(MoveEnd - MoveStart), 0, 1);
        public bool Absorbs(long now) => now < ChaseAt && Hits < HitLimit;
    }

    static readonly RuleTable<GameArea, Dictionary<uint, TornadoOrb>> AreaTornadoes = new();

    static int TornadoHinder(GameArea area, SpawnedMonster monster, double now)
    {
        if (!AreaTornadoes.TryGetValue(area, out var active) || active.Count == 0
            || !monster.Hostile || monster.Dead || monster.WorldHidden
            || monster.Effects[Stat.ImmuneToHinder] != 0 || monster.Effects[Stat.YouCannotBeHindered] != 0) return 0;
        long tick = ProjectileRuntime.Time(now);
        var cell = monster.Walk.Position(now);
        var position = new ProjectileVector(cell.X, cell.Y);
        foreach (var tornado in active.Values)
            if (tick < tornado.EndsAt && (tornado.Position(tick) - position).Length <= tornado.Radius)
                return (int)Math.Round(30 * Math.Max(0, 1 + monster.Effects[Stat.HinderEffectOnSelfPlusPct] / 100d));
        return 0;
    }

    public sealed partial class ProjectileSession
    {
        const string TornadoBody = "Metadata/Monsters/Tornado/Tornado";
        const int TornadoRadius = 23;
        static readonly long TornadoStep = ProjectileRuntime.Time(.1);
        readonly Dictionary<uint, TornadoOrb> tornadoes = [];
        readonly HashSet<uint> endingTornadoes = [];

        internal void ReleaseTornado(int effect, int level, SupportedGem source, DamageType? element,
            (int X, int Y) from, (int X, int Y) aim, Func<bool> current, Action<(int X, int Y), int> hit)
        {
            if (Monsters.Find(TornadoBody) is not { Supported: true, StateMachine: not null } variety) return;
            var own = source.Stats;
            int Own(uint stat) => own.GetValueOrDefault(stat);
            int limit = Own(Stat.NumberOfTornadosAllowed);
            int interval = Own(Stat.TornadoBaseDamageIntervalMs);
            if (limit <= 0 || interval <= 0) return;
            var stats = CharacterPlayerStats(Player);
            double Seconds(uint stat) => SkillDurationModifiers.Seconds(Own(stat), stats.GetValueOrDefault, Own, DurationKinds(Player, effect));
            double first = Seconds(Stat.BaseSkillEffectDuration);
            bool transfigured = element is not null;
            double primary = transfigured ? 0 : first * Math.Max(0, 1 + Own(Stat.TornadoOnlyPrimaryDurationPlusPct) / 100d);
            double secondary = transfigured ? first : Seconds(Stat.BaseSecondarySkillEffectDuration);
            if (primary + secondary <= 0) return;
            int family = transfigured ? 1 : 0;
            while (tornadoes.Values.Count(t => t.Family == family) >= limit)
                RemoveTornado(tornadoes.Values.Where(t => t.Family == family)
                    .MinBy(t => (t.CreatedAt, t.Id))!, replaced: true);

            var direction = from == aim ? new ProjectileVector(1, 0) : (Vector(aim) - Vector(from)).Unit;
            long now = Runtime.Now;
            var tornado = new TornadoOrb(Player.TakeObjectId(), family, variety, Vector(from), direction,
                now, now + ProjectileRuntime.Time(primary), now + ProjectileRuntime.Time(primary + secondary),
                ProjectileRuntime.Time(interval / 1000d), SkillRadius(Player, TornadoRadius, own, spell: true, effect),
                variety.MovementSpeed * Math.Max(.01, 1 + Own(Stat.TornadoMovementSpeedPlusPct) / 100d),
                Own(Stat.TornadoMaximumNumberOfHits), Own(Stat.TornadoDamageAbsorbedPct), element, current, hit);
            tornado.Actor = new(new(tornado.Id, epoch), 2, tornado.Position, 0)
                { Size = Math.Max(0, variety.ObjectSize - 1), CanBeChainTarget = false };
            tornadoes.Add(tornado.Id, tornado);
            AreaTornadoes.GetOrCreateValue(Player.Area).Add(tornado.Id, tornado);
            Player.TrackProjectileObject(tornado.Id);
            byte actorLevel = (byte)Math.Clamp(level, 1, 100);
            _ = RegisterMonsterType(Player, (ushort)variety.Row, actorLevel, "Tornado object type");
            Create(tornado.Id, () =>
            {
                var position = RoundCell(tornado.Position(Runtime.Now));
                var body = new MonsterObject(position.X, position.Y, 1, variety.ComponentsOrBase,
                    StateMachine: TornadoStates(tornado, phase: tornado.ChaseAt <= Runtime.Now ? 1 : 0),
                    Stats: [(Stat.IsTornadoOwnerId, (int)Player.Id), (Stat.HideMiniLifeBar, 1)])
                { Blocking = false, Chain = variety.Chain, LifeData = variety.LifeData };
                return InstanceEntities.CreateObject(tornado.Id,
                    InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, actorLevel)), body.Blob());
            }, wake: true);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[tornado-create] actor={tornado.Id:x} initial={primary:F3} secondary={secondary:F3} element={element} radius={tornado.Radius} limit={limit}");
            AdvanceTornado(tornado);
        }

        static int TornadoVisualElement(DamageType? type) => type switch
        { DamageType.Cold => 2, DamageType.Fire => 3, DamageType.Lightning => 4, DamageType.Chaos => 5, _ => 1 };

        static StateMachineData TornadoStates(TornadoOrb orb, int phase) => orb.Variety.StateMachine!
            .Set("tornado_radius", (int)Math.Round(orb.Radius * 250d / 23))
            .Set("tornado_state", phase)
            .Set("tornado_main_element", TornadoVisualElement(orb.Element));

        void PresentTornado(TornadoOrb orb, int phase) => World(orb.Id, InstanceEntities.StateMachineUpdate(
            orb.Id, (byte)orb.Variety.StateMachineComponentIndex, TornadoStates(orb, phase)));

        void AdvanceTornado(TornadoOrb orb)
        {
            if (!tornadoes.ContainsKey(orb.Id)) return;
            long now = Runtime.Now;
            if (!Active || !orb.Current() || now >= orb.EndsAt) { RemoveTornado(orb); return; }
            bool chase = now >= orb.ChaseAt;
            if (now == orb.ChaseAt && orb.HitLimit > 0) PresentTornado(orb, 1);
            double seconds = ProjectileWorldTime(Player);
            var position = orb.Position(now);
            var centre = RoundCell(position);
            if (now >= orb.NextPulse)
            {
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[tornado-pulse] actor={orb.Id:x} at={seconds:F3} chase={chase} absorbed={orb.Hits} centre={centre.X},{centre.Y}");
                orb.Hit(centre, orb.Radius);
                if (chase && !orb.Absorbed.IsZero) ReflectTornado(orb, centre, seconds);
                orb.NextPulse += orb.PulseEvery;
            }

            long next = Math.Min(orb.NextPulse, Math.Min(now + TornadoStep, chase ? orb.EndsAt : orb.ChaseAt));
            double step = (next - now) / (double)ProjectileRuntime.QuantaPerSecond;
            var move = orb.Direction * (orb.Speed * step);
            if (chase)
            {
                move = default;
                double best = orb.Variety.AggroRange;
                foreach (var (_, monster) in Player.Area.MonstersNear(centre, best))
                {
                    if (monster is not { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }) continue;
                    var target = Vector(monster.Walk.Position(seconds));
                    var direction = target - position;
                    double distance = direction.Length;
                    if (distance >= best || TraceTerrain(position, target) is not null) continue;
                    best = distance;
                    move = direction.Unit * Math.Min(distance, orb.Speed * step);
                }
            }
            var destination = position + move;
            if (TraceTerrain(position, destination) is { } wall)
                destination = position + move * Math.Max(0, wall.Fraction - .01);
            orb.At = position;
            orb.Destination = destination;
            orb.MoveStart = now;
            orb.MoveEnd = next;
            var cell = RoundCell(destination);
            if (cell != centre)
                World(orb.Id, InstanceProtocol.MoveTo(orb.Id, cell.X, cell.Y, (float)step, InstanceProtocol.FollowTerrain));
            Runtime.Schedule(next, () => AdvanceTornado(orb));
        }

        ProjectileHitResult ContactTornado(TornadoOrb orb, ProjectileDamage contact, ProjectileOffense shot)
        {
            if (!orb.Absorbs(contact.At) || !orb.Current() || contact.IsArea || shot.ActualCaster is not null
                || shot.FromTrapOrMine || shot.FromTotem || contact.Projectile.Use.Owner.Id != Player.Id)
                return ProjectileHitResult.Miss;

            var stats = CharacterPlayerStats(Player);
            var rng = contact.Projectile.RandomSource;
            var packet = new SkillDamageContext(shot.Spell ? SkillHitKind.Spell : SkillHitKind.Attack, stats)
                .Hit(shot.Hit).Scale(contact.Multiplier).Roll(rng);
            if (RollCriticalStrike(Player, shot.CriticalChance, shot.SkillStats, attack: !shot.Spell,
                skillRoll: shot.CriticalRoll, playerStats: stats))
                packet = packet.Scale(Math.Max(100, shot.CriticalMultiplier) / 100d);
            orb.Absorbed = orb.Absorbed.Add(packet.Scale(orb.Absorb / 100d));
            orb.Hits++;
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[tornado-absorb] actor={orb.Id:x} count={orb.Hits} projectile={contact.Projectile.Id}");
            return ProjectileHitResult.Hit;
        }

        void ReflectTornado(TornadoOrb orb, (int X, int Y) centre, double now)
        {
            foreach (var (id, monster) in Player.Area.MonstersNear(centre, orb.Radius).ToArray())
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                    || Dist(monster.Walk.Position(now), centre) > orb.Radius || ArenaHazards.Untargetable(Player, monster)) continue;
                var reflected = orb.Absorbed.Select((type, range) =>
                {
                    int increased = monster.Effects[Stat.ReflectDamageTakenPlusPct];
                    if (type == DamageType.Physical)
                    {
                        if (monster.Effects[Stat.CannotTakeReflectedPhysicalDamage] != 0) return default;
                        increased += monster.Effects[Stat.PhysicalReflectDamageTakenPlusPct];
                    }
                    if (DamageTypes.IsElemental(type))
                    {
                        if (monster.Effects[Stat.CannotTakeReflectedElementalDamage] != 0) return default;
                        increased += monster.Effects[Stat.ElementalReflectDamageTakenPlusPct];
                    }
                    return range.Scale(Math.Max(0, 1 + increased / 100d));
                });
                var (damage, _, mitigated, rules) = ResolveMonsterHit(monster, reflected, false, attack: false, now, default);
                if (damage <= 0 || ApplyTypedMonsterHitLocked(Player, monster, damage,
                    MonsterHitChaosDamage(monster, mitigated, spell: true, now, damage, rules), allyAt: centre) is not { } hit) continue;
                if (hit.StageStarts) { CommitProjectileBossStage(this, id, monster, Runtime.Now); hit = hit with { StageStarts = false }; }
                CommitAcceptedHitLocked(Player, id, monster, hit, damage, now, "Tornado reflection", playerKill: false, stun: new(0));
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[tornado-reflect] actor={orb.Id:x} target={id:x} damage={damage}");
            }
        }

        void AddTornadoActors(List<ProjectileActor> actors, long at)
        {
            foreach (var tornado in tornadoes.Values)
                if (tornado.Absorbs(at)) InOrder(actors, tornado.Actor);
        }

        void RemoveTornado(TornadoOrb orb, bool replaced = false, bool retire = false)
        {
            if (!tornadoes.Remove(orb.Id)) return;
            if (AreaTornadoes.TryGetValue(Player.Area, out var active)) active.Remove(orb.Id);
            if (retire) { RetireSkillEffectObject(orb.Id); return; }
            endingTornadoes.Add(orb.Id);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[tornado-remove] actor={orb.Id:x} replaced={replaced}");
            PresentTornado(orb, replaced ? 3 : 2);
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(replaced ? .25 : 1.4), () =>
                WorldRecords(orb.Id, () =>
                {
                    endingTornadoes.Remove(orb.Id);
                    Player.UntrackProjectileObject(orb.Id);
                    return [InstanceProtocol.RemoveObject(orb.Id)];
                }, remove: true));
        }

        void ClearTornadoes()
        {
            foreach (var orb in tornadoes.Values.ToArray()) RemoveTornado(orb, retire: true);
            foreach (uint id in endingTornadoes) RetireSkillEffectObject(id);
            endingTornadoes.Clear();
        }
    }
}
