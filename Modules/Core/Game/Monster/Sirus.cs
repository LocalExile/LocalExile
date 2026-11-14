using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

static partial class Sirus
{
    static readonly RuleTable<InstanceState, Fight> Fights = new();
    internal static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        var f = Fights.GetValue(c.Instance, _ => new(c));
        await f.Gate.WaitAsync();
        try { await f.Bind(c); if (IsMonsterRunner(c)) await f.Step(c, c.Clock.Elapsed.TotalSeconds); }
        catch { f.Stop(); throw; }
        finally { f.Gate.Release(); }
    }
    internal static async Task Leaving(ClientState c)
    {
        await RemoveExposure(c);
    }
    internal static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var f in Fights) f.Value.Stop();
            foreach (var e in Exposures) ClearExposure(e.Key);
        }
    }
    internal sealed partial class Fight
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly List<ClientState> players = [];
        readonly HashSet<ClientState> bound = [];
        readonly Scene scene;
        SpawnedMonster? body;
        uint id, throne, apparition;
        bool initialized, stopped;
        int stage, introBeat, rotation;
        double deadline, ready, nextApparition, introAt;
        // 0 entrance, 1 dialogue, 2 throne, 3 descend, 4 combat, 5 ascend, 6 storm intermission, 7 death, 8 won.
        internal int Stage => stage;
        internal int Round => body?.LifePhases?.Completed ?? 0;
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea);
        bool Awake(ClientState p, uint actor) => p.MonsterViews.ContainsKey(actor) && !p.AsleepMonsters.Contains(actor);
        (int X, int Y) Position(double now) => body?.Walk.Position(now) ?? scene.Centre;
        public Fight(ClientState c)
        {
            scene = SceneOf(c);
            lock (c.Monsters)
                foreach (var (key, m) in c.Monsters)
                {
                    if (m.Variety?.Id == Body) { id = key; body = m; }
                    if (m.Variety?.Id == Throne) throne = key;
                    if (m.Variety?.Id == Apparition) apparition = key;
                }
            if (body?.LifePhases is { } phases)
                for (int n = phases.Completed + 1; n <= 3 && c.Instance.HasState(Flag("Phase" + n)); n++)
                { phases.Begin((uint)((ulong)body.MaxLife * (uint)phases.NextPercent / 100), body.MaxLife); phases.Finish(); }
            stage = c.Instance.HasState(Flag("Won")) ? 8
                : body?.Dead == true ? 7
                : body?.LifePhases?.Active == true ? 6
                : c.Instance.HasState(Flag("Started")) ? 4 : c.Instance.HasState(Flag("IntroDone")) ? 2 : 0;
        }
        public async Task Bind(ClientState c)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(c)) { players.Add(c); foreach (var fx in visuals) await CreateFx(c, fx); }
            if (body is not null && bound.Add(c) && Awake(c, id) && SkillsFor(c, body.Variety!, body.Level) is { } skills)
                await SendWorldRecord(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, id,
                    InstanceEntities.PlayerSkillsChange(id, skills, []), "[Sirus] native skills");
        }
        async Task All(uint actor, byte[] bytes, bool awake = false) => await All(actor, new[] { bytes }, awake);
        async Task All(uint actor, byte[][] records, bool awake = false)
        {
            foreach (var p in Present.Where(p => !awake || Awake(p, actor)).ToArray())
            {
                var w = p.Wire;
                await SendWorldRecords(w.Remote, w.Stream, w.Ciphers, p, actor, () => records.Select(r => r.ToArray()).ToArray(), null,
                    still: () => CurrentGameWork && !stopped && !p.Closed && !p.LeavingArea);
            }
        }
        async Task Remove(uint actor)
        {
            foreach (var p in Present.ToArray()) { var w = p.Wire; await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, p, actor); }
        }
        async Task Voice(ushort line, ushort speaker = 407)
        {
            foreach (var p in Present.ToArray()) { var w = p.Wire; await Send(w.Remote, w.Stream, w.Ciphers, InstanceNpcs.TextAudio(0, speaker, line), $"[Sirus] NPCTextAudio {line}"); }
        }
        async Task State(ClientState c, uint actor, params (string Name, int Value)[] changes)
        {
            if (!c.Monsters.TryGetValue(actor, out var m) || m.States is not { } states) return;
            foreach (var (name, value) in changes) states = states.Set(name, value);
            m.States = states;
            if (c.Instance.World.MonsterOrigins.TryGetValue(actor, out var origin))
                c.Instance.World.MonsterOrigins[actor] = origin with { Object = origin.Object with { StateMachine = states } };
            int component = m.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (component >= 0) await All(actor, InstanceEntities.StateMachineUpdate(actor, (byte)component, states), true);
        }
        async Task Bar(ClientState c, bool show)
        {
            body!.BossBarShown = show;
            await State(c, id, ("boss_life_bar", show ? 1 : 0), ("disable_life_bar", show ? 0 : 1));
            foreach (var p in Present) { if (show) p.BossBarsSeen.Add(id); else p.BossBarsSeen.Remove(id); }
        }
        async Task Basic(ClientState c, uint actor, string action, double now, double duration)
        {
            if (!c.Monsters.TryGetValue(actor, out var m) || Game.SkillsFor(c).ActionSkillId(action) is not { } skill) return;
            var from = m.Walk.Position(now);
            lock (c.Monsters) { m.ActionGeneration++; m.SwingGeneration = null; m.BusyUntil = now + duration; }
            await All(actor, [InstanceProtocol.ActionStarted(actor, from.X, from.Y, from.X, from.Y, skill, c.NextActionSequence(), 0x0408),
                InstanceProtocol.ActionEnded(actor)], true);
        }
        async Task Teleport(ClientState c, uint actor, (int X, int Y) at, double now)
        {
            if (!c.Monsters.TryGetValue(actor, out var m)) return;
            lock (c.Monsters) m.Walk.Teleport(at.X, at.Y, now);
            await All(actor, InstanceProtocol.Teleport(actor, at.X, at.Y), true);
        }
        async Task<uint> Actor(ClientState c, string type, (int X, int Y) at, bool rewards = false)
        {
            if (Monsters.Find(type) is not { Supported: true } v || v.Id != type) throw new InvalidOperationException("Unsupported Sirus actor: " + type);
            return await SpawnMonster(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, v, 84, MonsterRarity.Unique, at.X, at.Y,
                rotation: 3 * MathF.PI / 2, scripted: true, owned: (_, m) => { m.NoRewards = !rewards; m.EntranceGrace = true; });
        }
        async Task Initialize(ClientState c, double now)
        {
            if (throne == 0) throne = await Actor(c, Throne, scene.Centre);
            if (apparition == 0 && Round < 3 && body?.Dead != true) apparition = await Actor(c, Apparition, scene.Centre);
            if (body is null)
            {
                id = await Actor(c, Body, scene.Centre, rewards: true);
                body = c.Monsters[id]; body.LifePhases ??= Phases(body.Variety!);
                await Teleport(c, id, scene.Centre, now);
            }
            body.LifePhases ??= Phases(body.Variety!);
            lock (c.Monsters)
                foreach (var (key, m) in c.Monsters)
                    if (m.Variety?.Id is Storm or StationaryStorm) { m.EntranceGrace = true; m.NoRewards = true; RegisterStorm(key, m, now); }
            if (stage == 4) { body.EntranceGrace = false; await State(c, id, ("norendering", 0)); await Bar(c, true); ready = now + 1; }
            if (stage == 6) { body.EntranceGrace = true; deadline = now + 4; }
            if (stage == 0 && Distance(c.Walk.Position(now), scene.Centre) < 220)
            {
                // An already-open instance from the old module starts at its existing arena, without moving its player.
                c.Instance.SetState(Flag("IntroDone")); stage = 2;
            }
            if (!c.Instance.HasState(Flag("Storms")))
            {
                foreach (var point in AreaPopulation.LoadPoints(Area, c.Seed, []).Where(p => p.Name == StationaryStorm))
                {
                    uint key = await Actor(c, StationaryStorm, (point.X, point.Y));
                    RegisterStorm(key, c.Monsters[key], now);
                }
                c.Instance.SetState(Flag("Storms"));
            }
            initialized = true;
        }
        public async Task Step(ClientState c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused) return;
            if (!initialized) await Initialize(c, now);
            await Maintenance(c, now);
            if (stage == 8) return;
            if (body!.Dead || body.Life == 0)
            {
                if (stage != 7)
                {
                    CancelCombat(); stage = 7; deadline = now + 6.5;
                    await Bar(c, false); await Voice(5698); await State(c, throne, ("human", 0), ("visible", 0));
                    await Retire(c, apparition); apparition = 0;
                    foreach (var p in Present.ToArray()) await RemoveExposure(p);
                    Console.WriteLine("[Sirus] defeated; native death sequence");
                }
                if (now >= deadline) { c.Instance.SetState(Flag("Won")); stage = 8; await ClearHazards(); }
                return;
            }
            var target = Present.FirstOrDefault(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p));
            if (target is null) return;
            var player = target.Walk.Position(now);
            if (stage is 0 or 1) { await Intro(c, target, now); return; }
            if (stage == 2)
            {
                if (Distance(player, scene.Centre) > 60) return;
                c.Instance.SetState(Flag("Started"));
                await Voice(5684); await Descend(c, now); return;
            }
            if (stage == 3 && now >= deadline)
            {
                if (body.LifePhases!.Active)
                {
                    body.LifePhases.Finish();
                    c.Instance.SetState(Flag("Phase" + Round));
                    if (Round == 3)
                    {
                        body.Life = (uint)((ulong)body.MaxLife * 75 / 100);
                        await All(id, InstanceProtocol.LifeUpdate(id, body.Life));
                        await State(c, id, ("apparition_merged", 1));
                        await Retire(c, apparition); apparition = 0;
                        await State(c, throne, ("visible", 0));
                    }
                }
                body.EntranceGrace = false; stage = 4; ready = now + .8; nextApparition = now + 7;
                await Bar(c, true); Console.WriteLine($"[Sirus] phase {Round + 1}, life {body.Life}/{body.MaxLife}");
            }
            if (stage == 4)
            {
                if (body.LifePhases!.Begin(body.Life, body.MaxLife)) { await Ascend(c, now); return; }
                if (Distance(player, Position(now)) > 140) return;
                await Combat(target, now);
                if (Round < 3 && now >= nextApparition) { await ApparitionAttack(target, now); nextApparition = now + 9; }
            }
            if (stage == 5 && now >= deadline)
            {
                await State(c, id, ("norendering", 1)); stage = 6; deadline = now + 5;
                uint storm = await Actor(c, Storm, Position(now)); RegisterStorm(storm, c.Monsters[storm], now);
            }
            if (stage == 6 && now >= deadline && Distance(player, Position(now)) < 48) await Descend(c, now);
        }
        async Task Descend(ClientState c, double now)
        {
            bool final = body!.LifePhases is { Active: true, Completed: 2 };
            body.EntranceGrace = true; stage = 3; deadline = now + (final ? 4 : 2);
            await State(c, id, ("norendering", 0), ("throne", 0), ("rocks", 1), ("cloak", final ? 0 : 1), ("eyes", 1), ("point_light", 1));
            await State(c, throne, ("human", 0));
            await Basic(c, id, final ? "ChangeToStance2" : "Emerge", now, final ? 4 : 2);
            await Basic(c, throne, "Emerge", now, 2);
            if (Round > 0 || final) await Voice(final ? (ushort)5696 : Round == 0 ? (ushort)5692 : (ushort)5694);
            await ClearNearbyStorms(c, Position(now), 70);
        }
        async Task Ascend(ClientState c, double now)
        {
            CancelCombat(); body!.EntranceGrace = true; body.LifePhases!.DamageTakenPercent = 100;
            await Bar(c, false); await ClearTemporary(c);
            var at = Position(now);
            await Teleport(c, throne, at, now); await Teleport(c, apparition, at, now);
            await State(c, id, ("throne", 1)); await State(c, throne, ("human", 1), ("visible", 1));
            await Basic(c, id, "Emerge2", now, 8); await Basic(c, throne, "Emerge2", now, 8);
            await Voice((ushort)(5681 + Math.Min(2, Round))); stage = 5; deadline = now + 8;
            Console.WriteLine($"[Sirus] storm intermission after phase {Round + 1}");
        }
        public void Stop()
        {
            stopped = true; CancelCombat(); ClearAllExposure();
            foreach (var p in players)
                foreach (var fx in visuals)
                {
                    p.RemoveBlocker(fx.Id);
                    if (!p.Closed && !p.LeavingArea)
                        p.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(fx.Id), null, WorldId: fx.Id, RemovesObject: true));
                }
            visuals.Clear(); patches.Clear(); quads.Clear(); carry.Clear();
        }
        void ClearAllExposure()
        {
            foreach (var p in players)
                foreach (ushort buff in ClearExposure(p).Where(b => b != 0))
                    if (!p.Closed && !p.LeavingArea)
                        p.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, buff), null, WorldId: PlayerId));
        }
    }
}
