using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>The native arena rails, including their explicit opposite-end references,
/// sweeping across the arena in native <c>SpiderGodActivateLaserToward[7802]</c> and
/// <c>SpiderGodActivateLaserAway[7805]</c> orderings.</summary>
public static class ArakaaliLasers
{
    const string Root = "Metadata/Monsters/Arakaali/Turrets/ArakaaliTurret", Prefix = "Arakaali/";
    // GEAL identifier hash: 1406db8f0 (Murmur2 over UTF-16LE, seed 0).
    internal const uint NextVariable = 0x61f4555e;
    internal const int TowardEffect = 7802, AwayEffect = 7805;
    // turret_rig.amd: activate 533 ms; fire's contact 1999 ms, end 3333 ms.
    internal const double Charge = .533, Contact = 1.999, Duration = 3.333;
    internal const double Radius = 3; // Collision-band calibration in grid cells.
    static readonly Store<InstanceState, Field> Fields = new();
    public static async Task Tick(Player c)
    {
        if (c.Layout.Id != Arakaali.Area || !CurrentGameWork || !c.Connection.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        var found = c.Area.Monsters.FirstOrDefault(p => Arakaali.IsBoss(p.Value.Variety));
        if (found.Value is null) return;
        var field = Fields.GetValue(c.Instance, _ => new(c, found.Key, found.Value));
        await field.Gate.WaitAsync();
        try { await field.Bind(c); if (IsMonsterRunner(c)) await field.Step(c, AreaTime(c)); }
        finally { field.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable { public void Dispose() { foreach (var f in Fields) f.Value.Stop(); } }
    internal sealed record Lane(string Source, string End, bool A, double MidX, double MidY);
    sealed class ActiveLane(Lane lane, double startAt)
    {
        public readonly Lane Lane = lane;
        public readonly double StartAt = startAt;
        public int Stage; // 0 queued, 1 charging, 2 firing, 3 cooling/done
        public double Deadline, HitAt;
        public bool Hit;
    }
    internal sealed class Field
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly uint bossId;
        readonly SpawnedMonster boss;
        readonly InstanceState instance;
        readonly HashSet<Player> players = [];
        readonly List<Lane> lanes = [];
        readonly List<Lane> groupA = [], groupB = [];
        readonly List<ActiveLane> activeSweep = [];
        internal IReadOnlyList<Lane> Lanes => lanes;
        double next;
        int wave;
        bool stopped, reset = true;

        public Field(Player c, uint id, SpawnedMonster actor)
        {
            bossId = id; boss = actor; instance = c.Instance;
            var places = CampaignBossHelpers.AreaPlacements(c);
            var a = places.Of(Root + "_A"); var b = places.Of(Root + "_B"); var displays = places.Of(Root + "Display");
            if (a.Count == 0 || b.Count == 0 || displays.Count != a.Count + b.Count) return;
            var points = displays.Concat(a).Concat(b).ToArray();
            string Key(GlyphQuest.Placement p) => $"{Prefix}{p.Type}:{p.X},{p.Y}";
            var lookup = ObjectTypes.All().Where(t => t.Type.StartsWith(Root, StringComparison.Ordinal))
                .ToDictionary(t => t.Type, t => (Info: QuestObjectTypes.Find(t.Type)!,
                    States: ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())!));
            foreach (var p in points)
            {
                if (instance.World.SceneObjects.ContainsKey(Key(p))) continue;
                var info = lookup[p.Type];
                if (info.Info is null || info.States is null || !QuestObject.CanSerialize(info.Info.Components, true)) continue;
                instance.World.SceneObjects[Key(p)] = (instance.World.TakeObjectId(), p.Type,
                    new(p.X, p.Y, info.Info.Components, p.Rotation, p.Z, p.Model, 1) { StateMachine = info.States.Set("active", 0) });
            }
            var available = displays.ToList();
            foreach (var source in a.Concat(b))
            {
                if (!instance.World.SceneObjects.ContainsKey(Key(source)) || available.Count == 0) continue;
                var end = available.OrderBy(p => Math.Min(Math.Abs(p.X - source.X), Math.Abs(p.Y - source.Y)))
                    .ThenBy(p => Math.Abs(p.X - source.X) + Math.Abs(p.Y - source.Y)).First();
                if (!instance.World.SceneObjects.TryGetValue(Key(end), out var target)) continue;
                available.Remove(end);
                var origin = instance.World.SceneObjects[Key(source)];
                instance.World.SceneObjects[Key(source)] = (origin.Id, origin.Type,
                    origin.Object with { EntityVariables = new Dictionary<uint, uint> { [NextVariable] = target.Id } });
                double midX = (source.X + end.X) * 0.5, midY = (source.Y + end.Y) * 0.5;
                var lane = new Lane(Key(source), Key(end), source.Type.EndsWith("_A", StringComparison.Ordinal), midX, midY);
                lanes.Add(lane);
                (lane.A ? groupA : groupB).Add(lane);
            }
            SortGroupSpatially(groupA);
            SortGroupSpatially(groupB);
        }

        static void SortGroupSpatially(List<Lane> group)
        {
            if (group.Count <= 1) return;
            double spanX = group.Max(l => l.MidX) - group.Min(l => l.MidX);
            double spanY = group.Max(l => l.MidY) - group.Min(l => l.MidY);
            group.Sort((l1, l2) => spanX >= spanY ? l1.MidX.CompareTo(l2.MidX) : l1.MidY.CompareTo(l2.MidY));
        }

        IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, instance));

        public async Task Bind(Player c)
        {
            if (!MonsterShown(c, bossId) || MonsterAsleep(c, bossId)) return;
            players.Add(c);
            var scenes = instance.World.SceneObjects
                .Where(p => p.Key.StartsWith(Prefix, StringComparison.Ordinal))
                .Select(p => p.Value).ToArray();
            foreach (var s in scenes)
            {
                if (Scenes(c).Contains(s.Id)) continue;
                if (await SendWorldCreate(c, s.Id, () => InstanceEntities.CreateObject(s.Id, InstanceEntities.ObjectTypeHash(s.Type), s.Object.Blob()),
                    "[Arakaali] linked arena turret", wake: true))
                    Scenes(c).Add(s.Id);
            }
        }

        async Task State(string key, byte transition, int active)
        {
            var s = instance.World.SceneObjects[key];
            s.Object = s.Object with { TransitionState = transition, StateMachine = s.Object.StateMachine!.Set("active", active) };
            instance.World.SceneObjects[key] = s;
            var components = s.Object.Components.ToList();
            if (Present.FirstOrDefault(p => Scenes(p).Contains(s.Id)) is { } p)
            {
                await SendWorldRecords(p, s.Id, () =>
                    [InstanceEntities.StateMachineUpdate(s.Id, (byte)components.IndexOf("StateMachine"), s.Object.StateMachine!),
                     InstanceEntities.TransitionableUpdate(s.Id, (byte)components.IndexOf("Transitionable"), transition)], "[Arakaali] turret rail state");
            }
        }

        async Task Reset()
        {
            foreach (var lane in lanes) { await State(lane.Source, 1, 0); await State(lane.End, 1, 0); }
            activeSweep.Clear();
        }

        internal static double LineDistance((int X, int Y) p, (int X, int Y) from, (int X, int Y) to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y, length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((p.X - from.X) * dx + (p.Y - from.Y) * dy) / length, 0, 1);
            return Math.Sqrt(Math.Pow(p.X - from.X - t * dx, 2) + Math.Pow(p.Y - from.Y - t * dy, 2));
        }

        async Task Damage(Player p, Lane lane)
        {
            var from = instance.World.SceneObjects[lane.Source].Object;
            var to = instance.World.SceneObjects[lane.End].Object;
            bool Inside((int X, int Y) at) => LineDistance(at, (from.GridX, from.GridY), (to.GridX, to.GridY)) <= Radius;
            var attack = BossKits.AttackFor(p, boss.Variety!, boss.Level, 6879, boss.Attack?.SkillId ?? 0x4000)
                with { Spell = true, Scripted = true };
            MonsterHitOutcome? outcome = null;
            await SendWorldRecord(p, bossId, [], null, atGate: () =>
            {
                if (stopped || !CurrentGameWork || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p) || boss.Dead || boss.Life == 0 || boss.StageRunning || boss.LifePhases?.Active == true) return null;
                double now = Math.Max(AreaTime(p), ProjectileWorldTime(p));
                var minionAttack = attack with { Damage = attack.Damage.Scale(.5) };
                foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                    if (!g.Monster.Dead && Inside(g.Monster.Walk.Position(now))) GolemTakeHitLocked(p, g, bossId, boss, minionAttack, now, area: true);
                if (p.PlayerLife == 0 || !Inside(p.Walk.Position(now))) return null;
                outcome = CommitMonsterHitLocked(p, bossId, boss, attack, now, p.Walk.Position(now),
                    (from.GridX, from.GridY), false, p.NextActionSequence());
                return outcome?.HitPacket;
            });
            if (outcome is not null) await PresentMonsterHit(p, bossId, outcome);
        }

        List<Lane> BuildSweepOrder(List<Lane> group, (int X, int Y) targetPos, bool toward)
        {
            // Order spatially sorted lanes so Toward (7802) sweeps from the far side toward the player,
            // and Away (7805) sweeps from near the player outward across the arena.
            double dFirst = Math.Pow(group[0].MidX - targetPos.X, 2) + Math.Pow(group[0].MidY - targetPos.Y, 2);
            double dLast = Math.Pow(group[^1].MidX - targetPos.X, 2) + Math.Pow(group[^1].MidY - targetPos.Y, 2);
            bool startAtFirst = toward ? dFirst >= dLast : dFirst <= dLast;
            List<Lane> ordered = startAtFirst ? [.. group] : [.. Enumerable.Reverse(group)];
            // Leave a safe dodging rhythm: sweep up to 5 consecutive lanes in a rolling wave.
            int count = Math.Min(ordered.Count, boss.LifePhases?.Completed > 0 ? 5 : 4);
            return ordered.Take(count).ToList();
        }

        public async Task Step(Player c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused || lanes.Count == 0) return;
            if (boss.Dead || boss.StageRunning || boss.LifePhases?.Active == true)
            {
                if (reset || activeSweep.Count > 0) { await Reset(); reset = false; }
                next = now + 5;
                return;
            }
            var target = CampaignBossHelpers.NearestLivingTarget(c, boss.Walk.Position(now), now, 140, bossId);
            if (target is null) return;
            if (reset) { await Reset(); reset = false; next = now + 5; return; }

            if (activeSweep.Count == 0)
            {
                if (now < next) return;
                var group = (wave % 2 == 0) ? groupA : groupB;
                if (group.Count == 0) group = groupA.Count > 0 ? groupA : groupB;
                bool toward = (wave % 4) < 2;
                int patternEffect = toward ? TowardEffect : AwayEffect;
                var sweepLanes = BuildSweepOrder(group, target.Walk.Position(now), toward);
                double stagger = boss.LifePhases?.Completed > 0 ? 0.48 : 0.62;
                for (int i = 0; i < sweepLanes.Count; i++)
                    activeSweep.Add(new(sweepLanes[i], now + i * stagger));
                wave++;
                Console.WriteLine($"[Arakaali] native laser sweep {wave} ({(toward ? "Toward[7802]" : "Away[7805]")}, {sweepLanes.Count} lanes, eff {patternEffect})");
            }

            bool anyRemaining = false;
            foreach (var entry in activeSweep)
            {
                if (entry.Stage == 3) continue;
                anyRemaining = true;
                if (entry.Stage == 0)
                {
                    if (now < entry.StartAt) continue;
                    await State(entry.Lane.Source, 1, 1);
                    await State(entry.Lane.End, 1, 1);
                    entry.Stage = 1;
                    entry.Deadline = now + Charge;
                }
                else if (entry.Stage == 1 && now >= entry.Deadline)
                {
                    await State(entry.Lane.Source, 2, 1);
                    await State(entry.Lane.End, 2, 1);
                    entry.HitAt = now + Contact;
                    entry.Deadline = now + Duration;
                    entry.Hit = false;
                    entry.Stage = 2;
                }
                else if (entry.Stage == 2)
                {
                    if (!entry.Hit && now >= entry.HitAt)
                    {
                        entry.Hit = true;
                        foreach (var p in Present.ToArray()) await Damage(p, entry.Lane);
                    }
                    if (now >= entry.Deadline)
                    {
                        await State(entry.Lane.Source, 3, 2);
                        await State(entry.Lane.End, 3, 2);
                        entry.Stage = 3;
                    }
                }
            }

            if (!anyRemaining)
            {
                activeSweep.Clear();
                int eff = (wave % 4) < 2 ? TowardEffect : AwayEffect;
                double cd = SkillsFor(c).CooldownMs(eff, SkillsFor(c).MaxLevelAt(eff, boss.Level)) / 1000d;
                next = now + Math.Max(boss.LifePhases?.Completed > 0 ? 3.5 : 4.5, cd);
            }
        }
        public void Stop() { stopped = true; }
    }
}
