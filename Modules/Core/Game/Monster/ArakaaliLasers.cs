using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>The native arena rails, including their explicit opposite-end references.</summary>
public static class ArakaaliLasers
{
    const string Root = "Metadata/Monsters/Arakaali/Turrets/ArakaaliTurret", Prefix = "Arakaali/";
    // GEAL identifier hash: 1406db8f0 (Murmur2 over UTF-16LE, seed 0).
    internal const uint NextVariable = 0x61f4555e;
    // turret_rig.amd: activate 533 ms; fire's contact 1999 ms, end 3333 ms.
    internal const double Charge = .533, Contact = 1.999, Duration = 3.333;
    internal const double Radius = 3; // Collision-band calibration in grid cells.
    static readonly RuleTable<InstanceState, Field> Fields = new();
    public static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Arakaali.Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        KeyValuePair<uint, SpawnedMonster> found;
        lock (c.Monsters) found = c.Monsters.FirstOrDefault(p => Arakaali.IsBoss(p.Value.Variety));
        if (found.Value is null) return;
        var field = Fields.GetValue(c.Instance, _ => new(c, found.Key, found.Value));
        await field.Gate.WaitAsync();
        try { await field.Bind(c); if (IsMonsterRunner(c)) await field.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { field.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable { public void Dispose() { foreach (var f in Fields) f.Value.Stop(); } }
    internal sealed record Lane(string Source, string End, bool A);
    internal sealed class Field
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly uint bossId;
        readonly SpawnedMonster boss;
        readonly InstanceState instance;
        readonly HashSet<ClientState> players = [];
        readonly List<Lane> lanes = [];
        internal IReadOnlyList<Lane> Lanes => lanes;
        Lane[] firing = [];
        double next, deadline, hitAt;
        int stage, wave;
        bool hit, stopped, reset = true;
        public Field(ClientState c, uint id, SpawnedMonster actor)
        {
            bossId = id; boss = actor; instance = c.Instance;
            var places = new QuestObjectTypes.AreaPlacements(Arakaali.Area, c.Seed, []);
            var a = places.Of(Root + "_A"); var b = places.Of(Root + "_B"); var displays = places.Of(Root + "Display");
            if (a.Count == 0 || b.Count == 0 || displays.Count != a.Count + b.Count) return;
            var points = displays.Concat(a).Concat(b).ToArray();
            string Key(GlyphQuest.Placement p) => $"{Prefix}{p.Type}:{p.X},{p.Y}";
            var lookup = ObjectTypes.All().Where(t => t.Type.StartsWith(Root, StringComparison.Ordinal))
                .ToDictionary(t => t.Type, t => (Info: QuestObjectTypes.Find(t.Type)!,
                    States: ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())!));
            lock (c.Monsters)
            {
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
                    lanes.Add(new(Key(source), Key(end), source.Type.EndsWith("_A", StringComparison.Ordinal)));
                }
            }
        }
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, instance));
        public async Task Bind(ClientState c)
        {
            if (!c.MonsterViews.ContainsKey(bossId) || c.AsleepMonsters.Contains(bossId)) return;
            players.Add(c);
            (uint Id, string Type, QuestObject Object)[] scenes;
            lock (c.Monsters) scenes = [.. instance.World.SceneObjects.Where(p => p.Key.StartsWith(Prefix, StringComparison.Ordinal)).Select(p => p.Value)];
            foreach (var s in scenes)
            {
                lock (c.Monsters) if (c.SceneViews.Contains(s.Id)) continue;
                var w = c.Wire;
                if (await SendWorldCreate(c, s.Id, () => InstanceEntities.CreateObject(s.Id, InstanceEntities.ObjectTypeHash(s.Type), s.Object.Blob()),
                    "[Arakaali] linked arena turret", wake: true, transport: w))
                    lock (c.Monsters) c.SceneViews.Add(s.Id);
            }
        }
        async Task State(string key, byte transition, int active)
        {
            (uint Id, string Type, QuestObject Object) s;
            lock (instance.World.Monsters)
            {
                s = instance.World.SceneObjects[key];
                s.Object = s.Object with { TransitionState = transition, StateMachine = s.Object.StateMachine!.Set("active", active) };
                instance.World.SceneObjects[key] = s;
            }
            var components = s.Object.Components.ToList();
            foreach (var p in Present.Where(p => p.SceneViews.Contains(s.Id)).ToArray())
            {
                var w = p.Wire;
                await SendWorldRecords(w.Remote, w.Stream, w.Ciphers, p, s.Id, () =>
                    [InstanceEntities.StateMachineUpdate(s.Id, (byte)components.IndexOf("StateMachine"), s.Object.StateMachine!),
                     InstanceEntities.TransitionableUpdate(s.Id, (byte)components.IndexOf("Transitionable"), transition)], "[Arakaali] turret rail state");
            }
        }
        async Task Reset()
        {
            foreach (var lane in lanes) { await State(lane.Source, 1, 0); await State(lane.End, 1, 0); }
            firing = []; stage = 0;
        }
        internal static double LineDistance((int X, int Y) p, (int X, int Y) from, (int X, int Y) to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y, length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((p.X - from.X) * dx + (p.Y - from.Y) * dy) / length, 0, 1);
            return Math.Sqrt(Math.Pow(p.X - from.X - t * dx, 2) + Math.Pow(p.Y - from.Y - t * dy, 2));
        }
        async Task Damage(ClientState p, Lane lane)
        {
            var from = instance.World.SceneObjects[lane.Source].Object;
            var to = instance.World.SceneObjects[lane.End].Object;
            bool Inside((int X, int Y) at) => LineDistance(at, (from.GridX, from.GridY), (to.GridX, to.GridY)) <= Radius;
            var attack = BossKits.AttackFor(p, boss.Variety!, boss.Level, 6879, boss.Attack?.SkillId ?? 0x4000)
                with { Spell = true, Scripted = true };
            var w = p.Wire; MonsterHitOutcome? outcome = null;
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, bossId, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    if (stopped || !CurrentGameWork || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p) || boss.Dead || boss.Life == 0 || boss.StageRunning || boss.LifePhases?.Active == true) return null;
                    double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
                    var minionAttack = attack with { Damage = attack.Damage.Scale(.5) };
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                        if (!g.Monster.Dead && Inside(g.Monster.Walk.Position(now))) GolemTakeHitLocked(p, g, bossId, boss, minionAttack, now, area: true);
                    if (p.PlayerLife == 0 || !Inside(p.Walk.Position(now))) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, bossId, boss, attack, now, p.Walk.Position(now),
                        (from.GridX, from.GridY), false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, bossId, outcome);
        }
        public async Task Step(ClientState c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused || lanes.Count == 0) return;
            if (reset) { await Reset(); reset = false; next = now + 5; }
            if (boss.Dead || boss.StageRunning || boss.LifePhases?.Active == true)
            { if (stage != 0) await Reset(); next = now + 5; return; }
            if (!Present.Any(p => p.PlayerLife > 0 && p.MonsterViews.ContainsKey(bossId) && !p.AsleepMonsters.Contains(bossId))) return;
            if (stage == 0)
            {
                if (now < next) return;
                var group = lanes.Where(l => l.A == (wave % 2 == 0)).ToArray();
                firing = Enumerable.Range(0, Math.Min(3, group.Length)).Select(i => group[(wave / 2 + i * 4) % group.Length]).ToArray();
                wave++;
                foreach (var l in firing) { await State(l.Source, 1, 1); await State(l.End, 1, 1); }
                stage = 1; deadline = now + Charge; return;
            }
            if (stage == 1 && now >= deadline)
            {
                foreach (var l in firing) { await State(l.Source, 2, 1); await State(l.End, 2, 1); }
                hitAt = now + Contact; deadline = now + Duration; hit = false; stage = 2;
                Console.WriteLine($"[Arakaali] native laser volley {wave}");
            }
            if (stage != 2) return;
            if (!hit && now >= hitAt) { hit = true; foreach (var l in firing) foreach (var p in Present.ToArray()) await Damage(p, l); }
            if (now < deadline) return;
            foreach (var l in firing) { await State(l.Source, 3, 2); await State(l.End, 3, 2); }
            stage = 0; next = now + Math.Max(4, SkillsFor(c).CooldownMs(6879, SkillsFor(c).MaxLevelAt(6879, boss.Level)) / 1000d);
        }
        public void Stop() { stopped = true; }
    }
}
