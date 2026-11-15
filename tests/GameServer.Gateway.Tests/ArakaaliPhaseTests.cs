using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class ArakaaliPhaseTests
{
    [Fact]
    public async Task Campaign_phase_changes_survive_reload_and_keep_their_own_attacks()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        using var sleepingWire = new Wire(); using var observerWire = new Wire();
        var c = new ClientState(new(1, Arakaali.Area, 3000, 3000, 1000, 1000, (_, _) => true, []), 1, 1)
            { Character = "ArakaaliPhaseRegression", Instance = new(), PlayerSent = true, PlayerLife = 1000,
                Wire = ("arakaali-phase", wire.Stream, wire.Ciphers) };
        runtime.Player = c;
        var v = Game.Monsters.Find(Arakaali.Boss)!;
        uint id = await Game.SpawnMonster(c.Wire.Remote, wire.Stream, wire.Ciphers, c, v, 55,
            MonsterRarity.Unique, 1000, 1000);
        var boss = c.Monsters[id];
        var sleeping = new ClientState(c.Layout, 2, 1) { Instance = c.Instance, PlayerSent = true, PlayerLife = 1000,
            Wire = ("arakaali-sleeping", sleepingWire.Stream, sleepingWire.Ciphers) };
        var unbound = new ClientState(c.Layout, 3, 1) { Instance = c.Instance, PlayerSent = true, PlayerLife = 1000,
            Wire = ("arakaali-unbound", observerWire.Stream, observerWire.Ciphers) };
        runtime.Observers.AddRange([sleeping, unbound]);
        sleeping.MonsterViews[id] = unbound.MonsterViews[id] = c.MonsterViews[id];
        sleeping.AsleepMonsters.Add(id);
        Program.InGame.TryAdd(sleeping, 0); Program.InGame.TryAdd(unbound, 0);
        Assert.Equal(0, c.Instance.World.MonsterOrigins[id].Object.ActorStance);
        Assert.False(Arakaali.Allowed(boss, 6555));
        Assert.True(Arakaali.Allowed(boss, 4259));
        for (int tick = 0; tick < 30; tick++)
        {
            var action = Assert.IsType<MonsterAttack>(BossKits.Choose(boss, tick, 2));
            var selected = Assert.Single(boss.Kit, entry => entry.Attack.SkillId == action.SkillId);
            Assert.True(Arakaali.Allowed(boss, selected.Effect));
            Assert.DoesNotContain(selected.Effect, new[] { 6555, 6557, 6545, 6549, 6550, 6551, 5570 });
            BossKits.Consume(boss, action, tick);
        }
        boss.Life = boss.LifePhases!.Damage(boss.Life, boss.MaxLife, int.MaxValue);
        Assert.Equal(boss.MaxLife / 2, boss.Life);
        var fight = new Arakaali.Fight(id, boss, c); fight.Bind(c);
        await fight.Step(c, 100);
        Assert.True(boss.Invulnerable);
        // StageRunning belongs to the generic BossFights.Stage replay worker. A joining player must not
        // resume this encounter transition through that worker with a null Stage.
        Assert.Null(boss.Stage); Assert.False(boss.StageRunning);
        Assert.Empty(Game.PlanMonsterAi(c, new(), 101).Swings);
        Assert.Equal(boss.Life, boss.LifePhases.Damage(boss.Life, boss.MaxLife, int.MaxValue));
        await fight.Step(c, 106);
        Assert.True(boss.Invulnerable);
        fight.Stop();
        fight = new(id, boss, c); fight.Bind(c);
        await fight.Step(c, 110);
        Assert.True(boss.Invulnerable);
        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        Assert.True(gate.Wait(0));
        Task completed;
        try
        {
            completed = fight.Step(c, 116.334);
            Assert.False(completed.IsCompleted);
            Assert.True(boss.Invulnerable);
            Assert.Empty(Game.PlanMonsterAi(c, new(), 117).Swings);
            Assert.False(Arakaali.Allowed(boss, 6555));
        }
        finally { gate.Release(); }
        await completed;
        Assert.False(boss.Invulnerable); Assert.False(boss.StageRunning);
        Assert.Equal((uint)((ulong)boss.MaxLife * 60 / 100), boss.Life);
        Assert.True(c.Instance.HasState(Arakaali.SecondPhase));
        Assert.Equal(1, c.Instance.World.MonsterOrigins[id].Object.ActorStance);
        Assert.Equal(1, c.MonsterViews[id].ActorStance);
        Assert.Equal(1, unbound.MonsterViews[id].ActorStance);
        Assert.Equal(0, sleeping.MonsterViews[id].ActorStance);
        var shown = new List<byte[]>();
        var previousOutgoing = Program.Outgoing;
        try
        {
            Program.Outgoing = (cipher, bytes) =>
            { if (ReferenceEquals(cipher, sleepingWire.Ciphers)) shown.Add(bytes.ToArray()); };
            await Game.KeepWakeZone(sleeping.Wire.Remote, sleeping.Wire.Stream, sleeping.Wire.Ciphers, sleeping);
            Assert.Equal(1, sleeping.MonsterViews[id].ActorStance);
            var at = boss.Walk.Position(sleeping.Clock.Elapsed.TotalSeconds);
            var wake = InstanceProtocol.EnterWakeZone(id);
            var placed = InstanceProtocol.Teleport(id, at.X, at.Y, 0x7fffffff);
            var stance = InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, 5828, 0x8001, 0x0800, id);
            var publication = Assert.Single(shown, bytes => bytes.AsSpan().IndexOf(stance) >= 0);
            Assert.Equal(0, publication.AsSpan().IndexOf(wake));
            Assert.True(publication.AsSpan().IndexOf(placed) > 0);
            Assert.True(publication.AsSpan().IndexOf(placed) < publication.AsSpan().IndexOf(stance));
            shown.Clear();
            sleeping.AsleepMonsters.Add(id); sleeping.NextWakeZoneAt = 0;
            await Game.KeepWakeZone(sleeping.Wire.Remote, sleeping.Wire.Stream, sleeping.Wire.Ciphers, sleeping);
            Assert.DoesNotContain(shown, bytes => bytes.AsSpan().IndexOf(stance.AsSpan(0, 6)) >= 0);
        }
        finally { Program.Outgoing = previousOutgoing; }
        Assert.True(Arakaali.Allowed(boss, 6555)); Assert.False(Arakaali.Allowed(boss, 4259));
        var after = BossKits.Choose(boss, 117, 2)!;
        Assert.True(Arakaali.Allowed(boss, Assert.Single(boss.Kit, e => e.Attack.SkillId == after.SkillId).Effect));
        Assert.Single(Game.PlanMonsterAi(c, new(), 117).Swings);
        fight.Stop();
        uint restored = await Game.RestoreMonster(c.Wire.Remote, wire.Stream, wire.Ciphers, c, v,
            new(v.Row, 55, MonsterRarity.Unique, true, 1000, 1000, boss.Life));
        var recovered = c.Monsters[restored];
        Assert.Equal(1, recovered.LifePhases!.Completed);
        Assert.Equal(1, c.Instance.World.MonsterOrigins[restored].Object.ActorStance);
        Assert.Equal(1, c.MonsterViews[restored].ActorStance);
        unbound.MonsterViews.Clear();
        await Game.SyncInstanceMonsters(unbound.Wire.Remote, unbound.Wire.Stream, unbound.Wire.Ciphers, unbound);
        Assert.Equal(1, unbound.MonsterViews[id].ActorStance);
        await Game.SyncInstanceMonsters(unbound.Wire.Remote, unbound.Wire.Stream, unbound.Wire.Ciphers, unbound);
        Assert.Equal(1, unbound.MonsterViews[id].ActorStance);
        lock (c.Monsters) Assert.Null(Arakaali.StanceUpdateLocked(unbound, id, 118));
        Assert.Equal(0u, recovered.LifePhases.Damage(recovered.Life, recovered.MaxLife, int.MaxValue));
        Assert.Null(Arakaali.Phases(new(new(1, "other", 10, 10, 1, 1, null, []), 1, 1), v));
        var outsideCampaign = new SpawnedMonster("Arakaali outside campaign", new(1, 1, 20), 55, 8, 1000)
            { Variety = v };
        Assert.True(Arakaali.Allowed(outsideCampaign, 4259));
        Assert.True(Arakaali.Allowed(outsideCampaign, 6555));
        var plain = c.Instance.World.MonsterOrigins[restored].Object with { ActorStance = 0 };
        var stanceByte = Assert.Single(plain.Blob().Zip(c.Instance.World.MonsterOrigins[restored].Object.Blob()),
            pair => pair.First != pair.Second);
        Assert.Equal(1, stanceByte.Second);
        await TurretReferences(c, id, boss);
        await MirrorIntervals();
        await SisterHandoff();
    }
    static async Task MirrorIntervals()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var c = new ClientState(new(1, Yugul.Area, 4000, 4000, 1940, 1620, (_, _) => true, []), 1, 1)
            { Character = "MirrorIntervalRegression", Instance = new(), PlayerSent = true, PlayerLife = 1000,
                Wire = ("mirror-phase", wire.Stream, wire.Ciphers) };
        runtime.Player = c; c.Walk.Teleport(1940, 1620, 0);
        var v = Game.Monsters.Find(Yugul.Boss)!;
        uint id = await Game.SpawnMonster(c.Wire.Remote, wire.Stream, wire.Ciphers, c, v, 58,
            MonsterRarity.Unique, 1940, 1620, unrisen: false);
        var boss = c.Monsters[id]; var phase = Assert.IsType<LifePhaseGate>(boss.LifePhases);
        Assert.Contains(c.Instance.World.MonsterOrigins[id].Object.Skills!, s => s.Id == Yugul.HideSkill);
        boss.Life = phase.Damage(boss.Life, boss.MaxLife, int.MaxValue);
        Assert.Equal((uint)((ulong)boss.MaxLife * 66 / 100), boss.Life);
        var fight = new Yugul.Fight(id, boss, c); await fight.Bind(c); await fight.Step(c, 100);
        Assert.True(boss.Invulnerable); await fight.Step(c, 103.2);
        var images = c.Monsters.Values.Where(m => m.Variety?.Id == Yugul.Image).ToArray();
        Assert.Equal(10, images.Length);
        Assert.True(boss.WorldHidden); Assert.DoesNotContain(id, c.MonsterViews.Keys);
        Assert.False(LivingCollision.Blocks(boss)); Assert.False(LivingCollision.StepsWithBodies(boss));
        var projectiles = new Game.ProjectileSession(c.Wire.Remote, wire.Stream, wire.Ciphers, c);
        Assert.DoesNotContain(projectiles.Actors(0, 0), actor => actor.Key.Id == id);
        Assert.Contains(projectiles.Actors(0, 0), actor => c.Monsters.TryGetValue(actor.Key.Id, out var m) && images.Contains(m));
        Assert.All(images, m => { Assert.True(m.NoRewards); Assert.Equal(id, m.SummonerId); Assert.NotEmpty(m.Kit); });
        foreach (var m in images.Take(9)) m.Life = 0;
        fight.Stop(); fight = new(id, boss, c); await fight.Bind(c); await fight.Step(c, 110); await fight.Step(c, 114);
        Assert.Equal(10, c.Monsters.Values.Count(m => m.Variety?.Id == Yugul.Image));
        Assert.True(boss.Invulnerable); images[^1].Life = 0; await fight.Step(c, 115);
        Assert.True(boss.Invulnerable); await fight.Step(c, 116.2);
        Assert.False(boss.Invulnerable); Assert.False(boss.StageRunning); Assert.Equal(1, phase.Completed);
        Assert.False(boss.WorldHidden); Assert.Contains(id, c.MonsterViews.Keys);
        Assert.True(LivingCollision.Blocks(boss));
        Assert.Contains(projectiles.Actors(0, 0), actor => actor.Key.Id == id);
        boss.Life = phase.Damage(boss.Life, boss.MaxLife, int.MaxValue);
        Assert.Equal((uint)((ulong)boss.MaxLife * 33 / 100), boss.Life);
        await fight.Step(c, 120); await fight.Step(c, 124);
        Assert.Equal(20, c.Monsters.Values.Count(m => m.Variety?.Id == Yugul.Image));
        foreach (var m in c.Monsters.Values.Where(m => m.Variety?.Id == Yugul.Image)) m.Life = 0;
        await fight.Step(c, 125); await fight.Step(c, 126.2);
        Assert.Equal(2, phase.Completed); Assert.False(boss.Invulnerable);
        Assert.Equal(0u, phase.Damage(boss.Life, boss.MaxLife, int.MaxValue));
        Assert.Equal(2, Yugul.Phases(c, v)!.Completed);
        Assert.Null(Yugul.Phases(new(new(1, "other", 10, 10, 1, 1, null, []), 1, 1), v));
        fight.Stop();
    }
    static async Task SisterHandoff()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var c = new ClientState(new(1, SolarisLunaris.Area, 4000, 4000, 1000, 1000, (_, _) => true, []), 1, 1)
            { Character = "SisterHandoffRegression", Instance = new(), PlayerSent = true, PlayerLife = 1000,
                Wire = ("sister-phase", wire.Stream, wire.Ciphers) };
        runtime.Player = c; c.Walk.Teleport(1000, 1000, 0);
        async Task<uint> Spawn(string type, int x) => await Game.SpawnMonster(c.Wire.Remote, wire.Stream, wire.Ciphers,
            c, Game.Monsters.Find(type)!, 60, MonsterRarity.Unique, x, 1000, unrisen: false);
        uint sid = await Spawn(SolarisLunaris.Sun, 1000), mid = await Spawn(SolarisLunaris.Moon, 1010);
        var sun = c.Monsters[sid]; var moon = c.Monsters[mid];
        Assert.True(sun.Invulnerable); Assert.True(moon.Invulnerable);
        Assert.Equal(1, c.Instance.World.MonsterOrigins[sid].Object.TransitionState);
        var fight = new SolarisLunaris.Fight(sid, sun, mid, moon, c); await fight.Bind(c);
        await fight.Step(c, 100); await fight.Step(c, 102);
        Assert.False(sun.Invulnerable); Assert.True(moon.Invulnerable);
        Assert.True(moon.WorldHidden); Assert.DoesNotContain(mid, c.MonsterViews.Keys);
        Assert.Equal(4, c.Instance.World.MonsterOrigins[mid].Object.TransitionState);
        Assert.Single(c.Monsters.Values, m => SolarisLunaris.IsCelestial(m.Variety));
        await fight.Step(c, 123);
        Assert.True(sun.Invulnerable); Assert.True(moon.Invulnerable);
        await fight.Step(c, 125); await fight.Step(c, 127);
        Assert.True(sun.Invulnerable); Assert.False(moon.Invulnerable);
        Assert.True(sun.WorldHidden); Assert.False(moon.WorldHidden);
        Assert.Single(c.Monsters.Values, m => SolarisLunaris.IsCelestial(m.Variety));
        fight.Stop(); fight = new(sid, sun, mid, moon, c); await fight.Bind(c);
        await fight.Step(c, 130); await fight.Step(c, 132);
        Assert.True(sun.Invulnerable); Assert.False(moon.Invulnerable);
        Assert.Single(c.Monsters.Values, m => SolarisLunaris.IsCelestial(m.Variety));
        moon.Life = 0; moon.Dead = true; await fight.Step(c, 133); await fight.Step(c, 135);
        Assert.False(sun.Invulnerable); Assert.Equal(5, c.Instance.World.MonsterOrigins[sid].Object.TransitionState);
        Assert.DoesNotContain(c.Monsters.Values, m => SolarisLunaris.IsCelestial(m.Variety));
        sun.Life = 0; sun.Dead = true; await fight.Step(c, 136); fight.Stop();
    }
    static async Task TurretReferences(ClientState c, uint id, SpawnedMonster boss)
    {
        var field = new ArakaaliLasers.Field(c, id, boss);
        Assert.Equal(26, field.Lanes.Count);
        Assert.Equal(26, field.Lanes.Select(l => l.End).Distinct().Count());
        var reference = new QuestObject(1, 2, ["Positioned"])
            { EntityVariables = new Dictionary<uint, uint> { [ArakaaliLasers.NextVariable] = 0x04000102 } };
        Assert.Equal(new byte[] { 0, 1, 0x5e, 0x55, 0xf4, 0x61, 0, 1, 1, 2, 1, 0, 4 }, reference.Blob()[..13]);
        foreach (var lane in field.Lanes)
        {
            var source = c.Instance.World.SceneObjects[lane.Source]; var end = c.Instance.World.SceneObjects[lane.End];
            Assert.Equal(end.Id, source.Object.EntityVariables[ArakaaliLasers.NextVariable]);
            Assert.True(source.Object.GridX == end.Object.GridX || source.Object.GridY == end.Object.GridY);
        }
        await field.Bind(c); await field.Step(c, 200); await field.Step(c, 206); await field.Step(c, 207);
        Assert.Equal(6, c.Instance.World.SceneObjects.Values.Count(s => s.Object.TransitionState == 2));
        int seen = c.SceneViews.Count; Assert.Equal(52, seen); field.Stop();
        var rebound = new ArakaaliLasers.Field(c, id, boss); await rebound.Bind(c); await rebound.Step(c, 210);
        Assert.Equal(seen, c.SceneViews.Count); Assert.Equal(52, c.Instance.World.SceneObjects.Count);
        Assert.DoesNotContain(c.Instance.World.SceneObjects.Values, s => s.Object.TransitionState == 2);
        await rebound.Step(c, 216); await rebound.Step(c, 217);
        Assert.Equal(6, c.Instance.World.SceneObjects.Values.Count(s => s.Object.TransitionState == 2));
        c.Instance.World.Runner = c; boss.Dead = true;
        await ArakaaliLasers.Tick(c);
        Assert.DoesNotContain(c.Instance.World.SceneObjects.Values, s => s.Object.TransitionState == 2);
        boss.Dead = false;
        Assert.Equal(0, ArakaaliLasers.Field.LineDistance((5, 0), (0, 0), (10, 0)));
        Assert.True(ArakaaliLasers.Field.LineDistance((5, 8), (0, 0), (10, 0)) > ArakaaliLasers.Radius);
        rebound.Stop();

    }
    sealed class Runtime : IDisposable
    {
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public ClientState? Player;
        public readonly List<ClientState> Observers = [];
        public Runtime() { Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic()); }
        public void Dispose() { if (Player is { } c) c.Closed = true;
            foreach (var observer in Observers) { observer.Closed = true; Program.InGame.TryRemove(observer, out _); }
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previousLogic!); }
    }
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire() { listener.Start(); var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port); reader = accept.GetAwaiter().GetResult(); _ = Drain(); }
        async Task Drain() { try { var bytes = new byte[8192]; while (await reader.GetStream().ReadAsync(bytes) > 0) { } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
