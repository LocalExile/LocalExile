using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>The campaign spider's one-way Silk transition; arena turret patterns remain separate.</summary>
public static class Arakaali
{
    public const string Area = "2_7_12_2", Boss = "Metadata/Monsters/VaalSpiderGod/Arakaali";
    public static readonly string SecondPhase = Quests.InstanceFlag("ArakaaliSecondPhase");
    // VaalSpiderGod/rig.amd: phase_01_transition_silk_01, index 10, 6333 ms.
    internal const uint SilkClip = 10;
    internal const double SilkSeconds = 6.333;
    static readonly RuleTable<InstanceState, Fight> Fights = new();
    public static bool IsBoss(MonsterCatalog.Variety? variety) => variety?.Id == Boss;
    public static byte Stance(ClientState c, MonsterCatalog.Variety v) =>
        IsBoss(v) && c.Instance.HasState(SecondPhase) ? (byte)2 : (byte)0;
    public static LifePhaseGate? Phases(ClientState c, MonsterCatalog.Variety v)
    {
        if (!IsBoss(v) || c.Layout.Id != Area) return null;
        var phases = new LifePhaseGate([50]);
        if (c.Instance.HasState(SecondPhase)) { phases.Begin(0, 1); phases.Finish(); }
        return phases;
    }
    public static bool Allowed(SpawnedMonster m, int effect)
    {
        if (!IsBoss(m.Variety) || m.LifePhases is null) return true;
        bool second = m.LifePhases?.Completed > 0;
        return effect switch
        {
            6555 or 6557 or 6545 or 6549 or 6550 or 6551 or 5570 or 5222 => second,
            4259 or 4260 or 4261 or 5567 or 5221 => !second,
            _ => true,
        };
    }
    public static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        KeyValuePair<uint, SpawnedMonster> found;
        lock (c.Monsters) found = c.Monsters.FirstOrDefault(p => p.Value.Hostile && IsBoss(p.Value.Variety));
        if (found.Value is null) return;
        var fight = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        await fight.Gate.WaitAsync();
        try { fight.Bind(c); if (IsMonsterRunner(c)) await fight.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { fight.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { foreach (var fight in Fights) fight.Value.Stop(); }
    }
    internal sealed class Fight(uint id, SpawnedMonster boss, ClientState owner)
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly HashSet<ClientState> players = [];
        double deadline = double.NaN;
        bool stopped;
        public void Bind(ClientState c) => players.Add(c);
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, owner.Instance));
        async Task All(Func<ClientState, byte[]> message)
        {
            foreach (var p in Present.Where(p => p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id)).ToArray())
            {
                var w = p.Wire;
                await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id, message(p), null);
            }
        }
        public async Task Step(ClientState c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused || boss.Dead || boss.Life == 0) return;
            boss.LifePhases ??= Phases(c, boss.Variety!)!;
            var phases = boss.LifePhases;
            if (phases.Completed > 0) return;
            if (double.IsNaN(deadline))
            {
                if (!Present.Any(p => p.PlayerLife > 0 && p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id))) return;
                lock (c.Monsters)
                {
                    if (!phases.Active && !phases.Begin(boss.Life, boss.MaxLife)) return;
                    boss.StageRunning = true;
                    boss.ActionGeneration++; boss.SwingGeneration = null;
                    boss.FlinchUntil = 0; boss.StunSerial++;
                    boss.BusyUntil = deadline = now + SilkSeconds;
                }
                await All(_ => InstanceProtocol.AnimationUpdate(id, 0, SilkClip, 1));
                Console.WriteLine("[Arakaali] consumes Silk before the second stance");
                return;
            }
            if (now < deadline) return;
            lock (c.Monsters)
            {
                c.Instance.SetState(SecondPhase);
                boss.Life = Math.Max(boss.Life, (uint)((ulong)boss.MaxLife * 60 / 100));
                phases.Finish(); boss.StageRunning = false; boss.BusyUntil = now;
                if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { ActorStance = 2 } };
            }
            var at = boss.Walk.Position(now);
            await All(p => InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y,
                5828, p.NextActionSequence(), 0x0800, id));
            await All(_ => InstanceProtocol.LifeUpdate(id, boss.Life));
            Console.WriteLine("[Arakaali] second stance ready");
        }
        public void Stop() { stopped = true; boss.StageRunning = false; }
    }
}
