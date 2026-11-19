using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>The two campaign mirror intervals; map frogs have their own encounter.</summary>
public static class Yugul
{
    public const string Area = "2_8_4", Boss = "Metadata/Monsters/Frog/FrogGod/SilverFrog",
        Image = "Metadata/Monsters/Frog/FrogGod/SilverFrogImage";
    internal const ushort HideSkill = 0x4fff;
    internal const int HideEffect = 8455;
    // Frog/rig.amd: jump_into 3166 ms; jump_out 1166 ms.
    internal const double HideSeconds = 3.166, RiseSeconds = 1.166;
    static readonly Store<InstanceState, Fight> Fights = new();
    static string Done(int round) => Quests.InstanceFlag($"Yugul-Mirrors-{round}");
    static string Spawned(int round, int slot) => Quests.InstanceFlag($"Yugul-Mirrors-{round}-Image-{slot}");
    public static bool IsBoss(MonsterCatalog.Variety? v) => v?.Id == Boss;
    public static LifePhaseGate? Phases(Player c, MonsterCatalog.Variety v)
    {
        if (c.Layout.Id != Area || !IsBoss(v)) return null;
        var phases = new LifePhaseGate([66, 33]);
        for (int n = 0; n < 2 && c.Instance.HasState(Done(n)); n++) { phases.Begin(0, 1); phases.Finish(); }
        return phases;
    }
    internal static IReadOnlyList<GrantedSkill> AddSkills(Player c, MonsterCatalog.Variety v, byte level,
        IReadOnlyList<GrantedSkill> skills)
    {
        if (c.Layout.Id != Area || !IsBoss(v)) return skills;
        var catalog = SkillsFor(c);
        int rank = catalog.MaxLevelAt(HideEffect, level);
        var stats = MonsterActionSkillStats(c, HideEffect, rank,
            BossKits.AttackFor(c, v, level, HideEffect, HideSkill));
        return catalog.FindByRow(HideSkill, HideEffect, rank, stats) is { } hide ? [.. skills, hide] : skills;
    }
    public static async Task Tick(Player c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        KeyValuePair<uint, SpawnedMonster> found;
        lock (c.Monsters) found = c.Monsters.FirstOrDefault(p => p.Value.Hostile && IsBoss(p.Value.Variety));
        if (found.Value is null) return;
        var fight = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        await fight.Gate.WaitAsync();
        try { await fight.Bind(c); if (IsMonsterRunner(c)) await fight.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { fight.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable { public void Dispose() { foreach (var f in Fights) f.Value.Stop(); } }
    internal sealed class Fight(uint id, SpawnedMonster boss, Player owner)
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly YugulHazards hazards = new(id, boss, owner);
        readonly HashSet<Player> players = [];
        double deadline = double.NaN;
        bool rising, stopped;
        IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea
            && ReferenceEquals(p.Instance, owner.Instance));
        public async Task Bind(Player c)
        {
            await hazards.Bind(c);
            if (!players.Add(c) || !boss.LifePhases!.Active || double.IsNaN(deadline)) return;
            if (!rising) await Hide(c);
        }
        async Task Hide(Player c)
        {
            if (!c.MonsterViews.ContainsKey(id) || c.AsleepMonsters.Contains(id)) return;
            var at = boss.Walk.Position(c.Clock.Elapsed.TotalSeconds);
            await SendWorldRecord(c, id,
                InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, HideSkill,
                    c.NextActionSequence(), 0x0800, id), "[Yugul] dive for mirror interval");
        }
        async Task MirrorState(Player c, int value)
        {
            lock (c.Monsters)
            {
                boss.States = boss.States!.Set("mirror_images", value);
                if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = boss.States } };
            }
            int component = boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
            foreach (var p in Present.Where(p => p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id)).ToArray())
            { await SendWorldRecord(p, id,
                InstanceEntities.StateMachineUpdate(id, (byte)component, boss.States), "[Yugul] mirror state"); }
        }
        public async Task Step(Player c, double now)
        {
            await hazards.Step(c, now);
            if (stopped || !CurrentGameWork || c.Paused || boss.Dead || boss.Life == 0) return;
            boss.LifePhases ??= Phases(c, boss.Variety!)!;
            var phase = boss.LifePhases;
            if (phase.Completed == 2) return;
            if (double.IsNaN(deadline))
            {
                if (!Present.Any(p => p.PlayerLife > 0 && (phase.Active || p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id)))) return;
                lock (c.Monsters)
                {
                    if (!phase.Active && !phase.Begin(boss.Life, boss.MaxLife)) return;
                    boss.ActionGeneration++; boss.SwingGeneration = null;
                    boss.FlinchUntil = 0; boss.StunSerial++;
                    var at = boss.Walk.Position(now); boss.Walk.Teleport(at.X, at.Y, now);
                    boss.BusyUntil = deadline = now + HideSeconds;
                }
                await MirrorState(c, 1);
                foreach (var p in Present.ToArray()) await Hide(p);
                return;
            }
            if (now < deadline) return;
            if (rising)
            {
                lock (c.Monsters)
                {
                    c.Instance.SetState(Done(phase.Completed)); phase.Finish();
                    boss.StageRunning = false; boss.BusyUntil = now;
                }
                deadline = double.NaN; rising = false;
                Console.WriteLine($"[Yugul] mirror interval {phase.Completed} complete");
                return;
            }
            var points = new QuestObjectTypes.AreaPlacements(Area, c.Seed, [])
                .Of("Metadata/Monsters/Frog/FrogGod/FrogImageSpawner");
            if (points.Count == 0) return;
            bool hidden;
            lock (c.Monsters) { hidden = boss.WorldHidden; boss.WorldHidden = true; }
            if (!hidden) foreach (var p in Present.ToArray()) await SyncInstanceMonsters(p);
            int round = phase.Completed;
            lock (c.Monsters)
                foreach (var m in c.Monsters.Values.Where(m => m.Variety?.Id == Image))
                { m.SummonerId = id; m.NoRewards = true; }
            for (int slot = 0; slot < 10; slot++)
            {
                if (c.Instance.HasState(Spawned(round, slot))) continue;
                var point = points[slot % points.Count]; var at = c.Layout.Near((point.X, point.Y));
                int admittedSlot = slot;
                uint image = await SpawnMonster(c, Monsters.Find(Image)!, boss.Level,
                    MonsterRarity.Normal, at.X, at.Y, owned: (_, m) =>
                    {
                        m.SummonerId = id; m.NoRewards = true; m.Aggro = true;
                        c.Instance.SetState(Spawned(round, admittedSlot));
                    }, ownedCurrent: () => !stopped && phase.Active && phase.Completed == round);
                if (image == 0) return;
            }
            lock (c.Monsters)
                if (c.Monsters.Values.Any(m => m.Variety?.Id == Image && !m.Dead && m.Life > 0)) return;
            // Native Hide listens for reappear, dispatched by mirror_images=0 in SilverFrog.ot.
            await MirrorState(c, 0);
            lock (c.Monsters) boss.WorldHidden = false;
            foreach (var p in Present.ToArray())
            {
                await SyncInstanceMonsters(p);
                var at = boss.Walk.Position(now);
                await SendWorldRecord(p, id,
                    InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, 0x0101,
                        p.NextActionSequence(), 0x0800, id), "[Yugul] emerges after mirrors");
            }
            rising = true; deadline = now + RiseSeconds; boss.BusyUntil = deadline;
        }
        public void Stop() { stopped = true; boss.StageRunning = false; hazards.Stop(); }
    }
}
