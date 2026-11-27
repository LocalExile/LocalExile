using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal static bool IsBladestorm(string? effect) =>
        effect is "Bladestorm" or "BladestormAltX" or "BladestormAltY" or "BladestormRoyale";

    static void CreateBladestormLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem admitted,
        AttackHand hand, SkillCatalog.Unarmed weapon, int? offCritical, (int X, int Y) at, (int X, int Y) aim,
        double interval, ushort sequence, double now, double radiusMore, double damageMore, double ruthless, int repeat,
        bool? cryExerted)
    {
        var stats = CharacterPlayerStats(player);
        int Own(uint key) => admitted.Stats.GetValueOrDefault(key);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), stats.GetValueOrDefault, Own);
        int limit = Own(Stat.BladestormMaximumNumberOfStormsAllowed);
        if (duration <= 0 || limit <= 0) return;
        var area = SkillAreaStats(player, admitted.Stats, gem.Effect, stats);
        int radius = (int)Math.Round(SkillArea.Radius(20, key => area.GetValueOrDefault(key), spell: false) * radiusMore);
        bool sand = player.Stance == StanceCatalog.SandSelection;
        int alternate = Own(Stat.BladestormCreateAlternateStanceStormPctChance);
        if (alternate > 0 && RandomRolls.Chance(alternate, 100, AreaRng(player))) sand = !sand;
        var stormStats = admitted.Stats;
        if (!sand)
            stormStats = new Dictionary<uint, int>(stormStats) { [Stat.BleedOnHitWithAttacksPct] = 100 };
        double more = Math.Max(0, 1 + Own(Stat.BladestormStormDamagePlusPctFinal) / 100d) * damageMore;
        if (repeat > 0) more *= MeleeRepeatDamageMore(player, gem.Id, repeat);
        var attack = CaptureAreaAttack(player, gem.Id, admitted with { Stats = stormStats }, hand, weapon, offCritical,
            more, more, sand ? "sand storm" : "blood storm");
        var session = ProjectilesFor(player);
        session.PlaceBladestorm(gem, attack, at, aim, sand, limit, radius, sequence, now, interval, duration, ruthless,
            cryExerted, Own(Stat.BladestormMovementSpeedPlusPctWhileInSandstorm), sand ? BlindEffectLess(stats) : 0);
    }

    static (bool Blood, int MoveIncreased) BladestormBonuses(Player player) =>
        player.ProjectileRuntime is ProjectileSession projectiles ? projectiles.BladestormOwnerBonuses : default;

    static readonly RuleTable<SpawnedMonster, SandstormBlind> SandstormBlinds = new();

    sealed class SandstormBlind
    {
        readonly Lock gate = new();
        readonly Dictionary<ProjectileSession, int> sources = [];
        int less;

        public int Less => Volatile.Read(ref less);

        public void Set(ProjectileSession source, int value)
        {
            lock (gate)
            {
                if (value > 0) sources[source] = value;
                else sources.Remove(source);
                int strongest = 0;
                foreach (int active in sources.Values) strongest = Math.Max(strongest, active);
                Volatile.Write(ref less, strongest);
            }
        }
    }

    static int SandstormBlindLess(SpawnedMonster monster) =>
        SandstormBlinds.TryGetValue(monster, out var blind) ? blind.Less : 0;

    public sealed partial class ProjectileSession
    {
        sealed record Bladestorm(Visual Visual, ProjectileVector Origin, ProjectileVector Destination, long Start,
            long TravelTime, long Expires, int Radius, bool Sand, int OwnerBonus, int BlindLess)
        {
            public (int X, int Y) Position(long at)
            {
                double progress = TravelTime > 0 ? Math.Clamp((at - Start) / (double)TravelTime, 0, 1) : 0;
                var position = Origin + (Destination - Origin) * progress;
                return ((int)Math.Round(position.X), (int)Math.Round(position.Y));
            }
        }

        List<Bladestorm>? bladestorms;
        internal (bool Blood, int MoveIncreased) BladestormOwnerBonuses { get; private set; }
        GameArea? sandBlindArea;
        Dictionary<SpawnedMonster, int>? stormBlind, sandTargets;
        long nextSandBlindUpdate;
        internal bool HasBladestormBlind => stormBlind is { Count: > 0 };

        internal void PlaceBladestorm(
            (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, RetainedAreaAttack attack,
            (int X, int Y) origin, (int X, int Y) aim, bool sand, int limit, int radius, ushort sequence, double now,
            double interval, double duration, double ruthless, bool? cryExerted, int moveIncreased,
            int blindLess)
        {
            bladestorms ??= [];
            while (bladestorms.Count >= limit) RemoveBladestorm(bladestorms[0]);
            // The Sand storm actors in MonsterVarieties move at 16 cells per second.
            const double sandPace = 16;
            var destination = origin;
            if (sand && aim != origin)
            {
                var ray = (Vector(aim) - Vector(origin)).Unit * (duration * sandPace);
                var end = (origin.X + (int)Math.Round(ray.X), origin.Y + (int)Math.Round(ray.Y));
                foreach (var cell in ClientPathfinder.Line(origin, end))
                {
                    if (!Player.Layout.WalkableFor(cell.X, cell.Y, 0) || Player.Barrier.Contains(cell)) break;
                    destination = cell;
                }
            }
            var visual = new Visual(Player.TakeObjectId(), default);
            long start = Math.Max(Runtime.Now, ProjectileRuntime.Time(now));
            long expires = start + ProjectileRuntime.Time(duration);
            long travelTime = ProjectileRuntime.Time(Dist(origin, destination) / sandPace);
            var storm = new Bladestorm(visual, Vector(origin), Vector(destination), start, travelTime, expires,
                radius, sand, moveIncreased, blindLess);
            bladestorms.Add(storm);
            if (sand) nextSandBlindUpdate = 0;
            const string artRoot = "Metadata/Effects/Spells/blood_sand_storm/";
            var effect = new EffectObject(origin.X, origin.Y, artRoot + (sand ? "storm_SAND.ao" : "storm_BLOOD.ao"))
                { Flags0 = EffectObject.FixedRotation, Scale = radius / 20f };
            Player.TrackProjectileObject(visual.Id);
            Create(visual.Id, () =>
            {
                if (visual.Ended) return null;
                visual.Created = true;
                return InstanceEntities.CreateObject(visual.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob());
            }, wake: true);
            if (travelTime > 0)
                WorldRecords(visual.Id, () => visual.Created && !visual.Ended
                    ? [InstanceProtocol.MoveTo(visual.Id, destination.X, destination.Y,
                        (float)(travelTime / (double)ProjectileRuntime.QuantaPerSecond), InstanceProtocol.FollowTerrain)]
                    : null);

            Runtime.Schedule(expires, () => RemoveBladestorm(storm));
            long cadence = Math.Max(1, ProjectileRuntime.Time(interval));
            SchedulePulse(start + cadence);

            bool Current() => Active && !visual.Ended && Player.PlayerLife != 0;

            void SchedulePulse(long when)
            {
                if (when >= storm.Expires || radius <= 0) return;
                ScheduleContact(when, Current, () =>
                {
                    var centre = storm.Position(when);
                    var hit = AreaSkillHitStrike(Player, gem.Id, gem, centre, centre, immediate: true, sequence,
                        actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0,
                        criticalRoll: CaptureSkillCriticalRoll(Player, gem.Id), channelGeneration: null, targetId: 0,
                        repeat: 0, radiusOverride: radius, once: null, repeatDelay: 0, radiusMore: 1, sacrifice: null,
                        swingAt: null, triggeredCurrent: Current, discharge: null, strikeIndex: 0, firstContactAt: null,
                        ruthlessMore: ruthless, priorTargets: null, scaledBlastChaos: 0, singleStrikeTarget: null,
                        onHit: null, scheduledSession: this, cryExerted, prepaidRawExtra: 0, baseChaos: 0, hammer: null,
                        retainedAttack: attack);
                    if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Bladestorm hit must not yield.");
                    hit.GetAwaiter().GetResult();
                    SchedulePulse(when + cadence);
                });
            }
        }

        void ReconcileBladestorms()
        {
            if (bladestorms is null) return;
            if (Player.PlayerLife == 0)
                while (bladestorms.Count > 0) RemoveBladestorm(bladestorms[^1]);
            if (bladestorms.Count == 0)
            {
                PublishBladestormBonuses(default);
                ClearBladestormBlind();
                bladestorms = null;
                return;
            }

            long at = Runtime.Now;
            double seconds = at / (double)ProjectileRuntime.QuantaPerSecond;
            var ownerAt = Player.Walk.Position(seconds);
            int moveIncreased = 0;
            bool blood = false, hasSand = false;
            foreach (var storm in bladestorms)
            {
                hasSand |= storm.Sand;
                if (Dist(ownerAt, storm.Position(at)) > storm.Radius) continue;
                if (storm.Sand) moveIncreased = Math.Max(moveIncreased, storm.OwnerBonus);
                else blood = true;
            }
            PublishBladestormBonuses((blood, moveIncreased));
            if (!hasSand)
            {
                ClearBladestormBlind();
                return;
            }
            if (at < nextSandBlindUpdate) return;
            nextSandBlindUpdate = at + ProjectileRuntime.Time(.1);
            var area = Player.Area;
            var next = sandTargets ??= new(ReferenceEqualityComparer.Instance);
            next.Clear();
            foreach (var storm in bladestorms)
            {
                if (storm.BlindLess <= 0 || storm.Radius <= 0) continue;
                var centre = storm.Position(at);
                foreach (var (_, enemy) in area.MonstersNear(centre, storm.Radius))
                    if (enemy is { Hostile: true, Life: > 0, Dead: false }
                        && enemy.Mods.Effects[Stat.CannotBeBlinded] == 0
                        && Dist(enemy.Walk.Position(seconds), centre) <= storm.Radius)
                        next[enemy] = Math.Max(next.GetValueOrDefault(enemy), storm.BlindLess);
            }

            var prior = stormBlind ??= new(ReferenceEqualityComparer.Instance);
            bool sameArea = ReferenceEquals(sandBlindArea, area);
            foreach (var enemy in prior.Keys)
                if (!sameArea || !next.ContainsKey(enemy)) SetStormBlind(sandBlindArea!, enemy, 0);
            foreach (var (enemy, less) in next)
                if (!sameArea || prior.GetValueOrDefault(enemy) != less) SetStormBlind(area, enemy, less);
            sandBlindArea = area;
            (stormBlind, sandTargets) = (next, prior);
            prior.Clear();
        }

        void SetStormBlind(GameArea area, SpawnedMonster enemy, int less)
        {
            if (less > 0) SandstormBlinds.GetOrCreateValue(enemy).Set(this, less);
            else if (SandstormBlinds.TryGetValue(enemy, out var blind)) blind.Set(this, 0);

            uint target = enemy.ObjectId;
            if (!area.Monsters.TryGetValue(target, out var current) || !ReferenceEquals(current, enemy)) return;
            const ushort tag = (ushort)BuffTag.BladestormBlind;
            if (less > 0) area.Buffs.Set(target, new(FlaskGrounds.BlindedBuffRow, Player.Id, Tag: tag));
            else area.Buffs.Clear(target, FlaskGrounds.BlindedBuffRow, Player.Id, tag: tag);
            enemy.PresentationChanged?.Invoke();
        }

        void ClearBladestormBlind()
        {
            if (stormBlind is not { Count: > 0 } prior) return;
            foreach (var enemy in prior.Keys) SetStormBlind(sandBlindArea!, enemy, 0);
            prior.Clear();
            sandBlindArea = null;
        }

        void PublishBladestormBonuses((bool Blood, int MoveIncreased) next)
        {
            if (next == BladestormOwnerBonuses) return;
            var previous = BladestormOwnerBonuses;
            BladestormOwnerBonuses = next;
            ChangeBuff(1306, previous.MoveIncreased > 0, next.MoveIncreased > 0);
            ChangeBuff(1307, previous.Blood, next.Blood);
            MarkPlayerStats(Player, PlayerStatStep.AttackSpeed);
            if (previous.MoveIncreased != next.MoveIncreased && CurrentGameRules && !Player.Closed && !Player.LeavingArea)
                RecomputePoolsAndSpeed(Player);

            void ChangeBuff(ushort row, bool old, bool current)
            {
                if (old == current) return;
                if (current) Player.Area.Buffs.Set(Player.Id, new(row, Player.Id));
                else Player.Area.Buffs.Clear(Player.Id, row, Player.Id);
            }
        }

        void ClearBladestorms()
        {
            if (bladestorms is { } active)
            {
                foreach (var storm in active)
                {
                    storm.Visual.Ended = true;
                    RetireSkillEffectObject(storm.Visual.Id);
                }
                bladestorms = null;
            }
            ClearBladestormBlind();
            PublishBladestormBonuses(default);
        }

        void RemoveBladestorm(Bladestorm storm)
        {
            var visual = storm.Visual;
            if (visual.Ended) return;
            visual.Ended = true;
            bladestorms?.Remove(storm);
            WorldRecords(visual.Id, () =>
            {
                Player.UntrackProjectileObject(visual.Id);
                return visual.Created ? [InstanceProtocol.RemoveObject(visual.Id)] : null;
            }, remove: true);
        }
    }
}
