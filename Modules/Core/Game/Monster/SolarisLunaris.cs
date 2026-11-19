using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>The Harbour Bridge's corporeal/celestial handoff, isolated from standalone/map variants.</summary>
public static class SolarisLunaris
{
    public const string Area = "2_8_13", Root = "Metadata/Monsters/LunarisSolaris/",
        Sun = Root + "Solaris", Moon = Root + "Lunaris";
    internal const ushort TransformSkill = 0x4ffe;
    // Rotation cadence is a development calibration; the native OT supplies the 1.6-second entrance timer.
    internal const double RotationSeconds = 20, EntranceSeconds = 1.6, TransformSeconds = 1.8;
    static readonly string MoonActive = Quests.InstanceFlag("Sisters-MoonActive");
    static readonly Store<InstanceState, Fight> Fights = new();
    public static bool IsBody(MonsterCatalog.Variety? v) => v?.Id is Sun or Moon;
    public static bool IsCelestial(MonsterCatalog.Variety? v) => v?.Id is Root + "SolarisCelestialForm" or Root + "LunarisCelestialForm";
    public static bool Held(ClientState c, SpawnedMonster m) => c.Layout.Id == Area && IsBody(m.Variety) && m.EntranceGrace;
    public static byte StartState(ClientState c, MonsterCatalog.Variety v, byte otherwise) =>
        c.Layout.Id == Area && IsBody(v) ? (byte)1 : otherwise;
    internal static IReadOnlyList<GrantedSkill> AddSkills(ClientState c, MonsterCatalog.Variety v, byte level,
        IReadOnlyList<GrantedSkill> skills)
    {
        if (c.Layout.Id != Area || !IsBody(v)) return skills;
        int effect = v.Id == Sun ? 6739 : 6740;
        var catalog = SkillsFor(c); int rank = catalog.MaxLevelAt(effect, level);
        var stats = MonsterActionSkillStats(c, effect, rank, BossKits.AttackFor(c, v, level, effect, TransformSkill));
        return catalog.FindByRow(TransformSkill, effect, rank, stats) is { } transform ? [.. skills, transform] : skills;
    }
    public static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        if (!Fights.TryGetValue(c.Instance, out var f))
        {
            KeyValuePair<uint, SpawnedMonster> sun, moon;
            lock (c.Monsters)
            {
                sun = c.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Sun);
                moon = c.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Moon);
            }
            if (sun.Value is null || moon.Value is null) return;
            f = Fights.GetValue(c.Instance, _ => new(sun.Key, sun.Value, moon.Key, moon.Value, c));
        }
        await f.Gate.WaitAsync();
        try { await f.Bind(c); if (IsMonsterRunner(c)) await f.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { f.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable { public void Dispose() { foreach (var f in Fights) f.Value.Stop(); } }
    internal sealed class Fight(uint sunId, SpawnedMonster sun, uint moonId, SpawnedMonster moon, ClientState owner)
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly HashSet<ClientState> players = [];
        int active = owner.Instance.HasState(MoonActive) ? 1 : 0;
        bool initialized, changing, stopped, entering;
        CelestialHazards? celestial;
        double nextSwap, deadline;
        (uint Id, SpawnedMonster Monster) Body(int index) => index == 0 ? (sunId, sun) : (moonId, moon);
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, owner.Instance));
        static bool Alive(SpawnedMonster m) => !m.Dead && m.Life > 0;
        public async Task Bind(ClientState c) { players.Add(c); if (celestial is not null) await celestial.Bind(c); }
        async Task State(ClientState c, int index, byte state)
        {
            var (id, m) = Body(index);
            lock (c.Monsters)
            {
                if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = state } };
            }
            int component = m.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable");
            foreach (var p in Present.Where(p => p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id)).ToArray())
            { var w = p.Wire; await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id,
                InstanceEntities.TransitionableUpdate(id, (byte)component, state), $"[Sisters] {m.Variety.Name} state {state}"); }
            bool wasHidden;
            lock (c.Monsters) { wasHidden = m.WorldHidden; m.WorldHidden = state == 4; }
            if (wasHidden != m.WorldHidden) foreach (var p in Present.ToArray())
                await SyncInstanceMonsters(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p);
        }
        static void Hold(ClientState c, SpawnedMonster m, bool held, double now)
        {
            lock (c.Monsters)
            {
                m.EntranceGrace = held;
                if (!held) { m.BusyUntil = now; return; }
                m.ActionGeneration++; m.SwingGeneration = null; m.StunSerial++; m.FlinchUntil = 0;
                m.Leg = null;
                var at = m.Walk.Position(now); m.Walk.Teleport(at.X, at.Y, now);
            }
        }
        async Task RemoveOrbs(ClientState c)
        {
            if (celestial is not null) { await celestial.Clear(); celestial.Stop(); celestial = null; }
            uint[] ids;
            lock (c.Monsters)
            {
                ids = [.. c.Monsters.Where(p => IsCelestial(p.Value.Variety)).Select(p => p.Key)];
                foreach (uint id in ids) { c.Monsters.Remove(id); c.Instance.World.MonsterOrigins.TryRemove(id, out _); }
            }
            foreach (var p in Present.ToArray())
                foreach (uint id in ids)
                {
                    bool shown;
                    lock (p.Monsters) { shown = p.MonsterViews.Remove(id); p.AsleepMonsters.Remove(id); }
                    if (shown) { var w = p.Wire; await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, p, id, "[Sisters] celestial form leaves"); }
                }
        }
        async Task Orb(ClientState c, int index, double now)
        {
            var (_, body) = Body(index); string type = body.Variety!.Id + "CelestialForm";
            lock (c.Monsters) if (c.Monsters.Values.Any(m => m.Variety?.Id == type && Alive(m))) return;
            var at = body.Walk.Position(now); var w = c.Wire;
            uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, c, Monsters.Find(type)!, body.Level,
                MonsterRarity.Normal, at.X, at.Y, scripted: true, owned: (_, m) =>
                { m.NoRewards = true; m.EntranceGrace = true; m.Aggro = true; }, ownedCurrent: () => !stopped);
            if (id != 0 && c.Monsters.TryGetValue(id, out var actor))
            {
                celestial = new(id, actor, c, () => !stopped && Alive(sun) && Alive(moon));
                foreach (var p in Present.ToArray()) await celestial.Bind(p);
            }
        }
        async Task Activate(ClientState c, double now)
        {
            await RemoveOrbs(c);
            int other = 1 - active;
            if (Alive(Body(other).Monster))
            { await State(c, other, 4); await Orb(c, other, now); }
            await State(c, active, Alive(Body(other).Monster) ? (byte)2 : (byte)5);
            entering = true; changing = false; deadline = now + EntranceSeconds; nextSwap = deadline + RotationSeconds;
            if (active == 1) c.Instance.SetState(MoonActive); else c.Instance.ClearState(MoonActive);
        }
        public async Task Step(ClientState c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused) return;
            if (celestial is not null) await celestial.Step(c, now);
            if (!Alive(sun) && !Alive(moon)) { await RemoveOrbs(c); return; }
            if (!Present.Any(p => p.PlayerLife > 0 && (p.MonsterViews.ContainsKey(sunId) || p.MonsterViews.ContainsKey(moonId)))) return;
            if (!initialized)
            {
                initialized = true; Hold(c, sun, true, now); Hold(c, moon, true, now);
                if (!Alive(Body(active).Monster)) active = 1 - active;
                await Activate(c, now); return;
            }
            if (!Alive(Body(active).Monster))
            {
                Hold(c, sun, true, now); Hold(c, moon, true, now);
                active = 1 - active; await Activate(c, now); return;
            }
            if (entering)
            {
                if (now < deadline) return;
                entering = false; Hold(c, Body(active).Monster, false, now);
            }
            if (changing)
            {
                if (now < deadline) return;
                active = 1 - active; await Activate(c, now); return;
            }
            if (!Alive(Body(1 - active).Monster) || now < nextSwap) return;
            // Stop admitting new AI orders while the current action finishes, or continuous attacks can starve the handoff.
            lock (c.Monsters) Body(active).Monster.EntranceGrace = true;
            if (now < Body(active).Monster.BusyUntil) return;
            var (id, body) = Body(active);
            Hold(c, body, true, now); await State(c, active, 3);
            var at = body.Walk.Position(now);
            foreach (var p in Present.Where(p => p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id)).ToArray())
            { var w = p.Wire; await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id,
                InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, TransformSkill,
                    p.NextActionSequence(), 0x0800, id), "[Sisters] corporeal form submerges"); }
            changing = true; deadline = now + TransformSeconds;
        }
        public void Stop() { stopped = true; celestial?.Stop(); }
    }
}
