using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SirusTests
{
    [GameFilesFact]
    public async Task Normal_encounter_uses_native_actors_and_four_guarded_phases_and_pays_once_after_the_body_dies()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var arrival = new ClientState(AreaLayouts.For(1789, 625343342), 1, 625343342) { Instance = new() };
        Sirus.ConfigureArrival(arrival);
        Assert.InRange(arrival.Layout.SpawnX, 1750, 1900);
        Assert.Equal(6, arrival.Layout.AreaTransitions.Count(t => t.Id is >= 0x2f00 and <= 0x2f05));
        var c = new ClientState(new(1789, Sirus.Area, 4000, 4000, 1845, 1994, (_, _) => true, []), 1, 1)
        {
            Character = "SirusRegression", PlayerSent = true, Instance = new(), PlayerLife = 1000000,
            Pools = new(100, 1000000, 100, 1, 0), Wire = ("sirus-regression", wire.Stream, wire.Ciphers), Immune = true,
        };
        runtime.Player = c; c.LaterFormPoints[Sirus.Body] = (2000, 2000);
        foreach (string type in new[] { Sirus.Body, Sirus.Throne, Sirus.Apparition, Sirus.Storm, Sirus.StationaryStorm })
            Assert.True(Game.Monsters.Find(type) is { Supported: true }, type);
        var v = Game.Monsters.Find(Sirus.Body)!;
        var roster = Sirus.SkillsFor(c, v, 84)!;
        Assert.Equal(32, roster.Count);
        Assert.Equal(roster.Count, roster.Select(s => s.Id).Distinct().Count());
        Assert.True(Sirus.Attack(c, v, 84, 7008).Damage[DamageType.Physical].Max > 0);
        var body = new SpawnedMonster("Sirus", new PlayerWalk(2000, 2000, 0), 200, 10, 1000)
        { Variety = v, Level = 84, Rarity = MonsterRarity.Unique, States = Sirus.StartStates(v), LifePhases = Sirus.Phases(v), EntranceGrace = true };
        var dot = Sirus.Dot(c, 10973, false);
        Assert.True(dot[DamageType.Chaos].Min > 0);
        Assert.Equal(dot[DamageType.Physical].Min, dot[DamageType.Chaos].Min, 5);
        Assert.Equal(dot.MinTotal / 2, Sirus.Dot(c, 10973, true).MinTotal, 5);
        c.Immune = false;
        Sirus.OnHit(c, body, Sirus.Attack(c, v, 84, 7022), DamagePacket.Of(DamageType.Cold, 100000, 100000),
            DamagePacket.Of(DamageType.Cold, 100000, 100000), false, false, 0, new Dictionary<uint, int>());
        Assert.True(c.ElementalAilments.FreezeUntil(0) > 0);
        c.ElementalAilments.Clear(); c.Immune = true;
        uint id = 0x8000100; c.Monsters[id] = body; c.MonsterViews[id] = new((2000, 2000), (2000, 2000), 1000);
        var fight = new Sirus.Fight(c); await fight.Bind(c); await fight.Step(c, 1);
        Assert.Equal(2, fight.Stage); Assert.True(body.Invulnerable);
        c.Walk.Teleport(2000, 2020, 1); await fight.Step(c, 2); Assert.Equal(3, fight.Stage);
        await fight.Step(c, 5); Assert.Equal(4, fight.Stage); Assert.False(body.Invulnerable);
        for (int now = 6; now < 80; now++) await fight.Step(c, now);
        for (int phase = 0; phase < 3; phase++)
        {
            double now = 100 + phase * 100;
            body.Life = body.LifePhases!.Damage(body.Life, body.MaxLife, int.MaxValue);
            Assert.Equal((uint)(750 - phase * 250), body.Life);
            Assert.Empty(Sirus.Rewards(c, body));
            await fight.Step(c, now); Assert.Equal(5, fight.Stage); Assert.True(body.Invulnerable);
            Assert.Equal(body.Life, body.LifePhases.Damage(body.Life, body.MaxLife, int.MaxValue));
            await fight.Step(c, now + 9); Assert.Equal(6, fight.Stage);
            if (phase == 1)
            {
                fight.Stop(); fight = new(c); await fight.Bind(c); await fight.Step(c, now + 10);
                Assert.Equal(6, fight.Stage); Assert.True(body.Invulnerable);
            }
            await fight.Step(c, now + 16); Assert.Equal(3, fight.Stage);
            await fight.Step(c, now + 21); Assert.Equal(4, fight.Stage); Assert.False(body.Invulnerable);
            Assert.Equal(phase + 1, fight.Round);
        }
        Assert.Equal(750u, body.Life);
        for (int now = 380; now < 460; now++) await fight.Step(c, now);
        body.Life = body.LifePhases!.Damage(body.Life, body.MaxLife, int.MaxValue); body.Dead = true;
        Assert.Equal(0u, body.Life);
        var rewards = Sirus.Rewards(c, body).ToArray();
        Assert.Contains(rewards, r => r.Item.Rarity == ItemRarity.Unique);
        Assert.Empty(Sirus.Rewards(c, body));
        await fight.Step(c, 500); Assert.Equal(7, fight.Stage);
        await fight.Step(c, 507); Assert.Equal(8, fight.Stage);
        Assert.Equal(0, c.TrapDegenPerSecond); Assert.Empty(c.Blockers);
        fight.Stop();
    }
    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-sirus-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public ClientState? Player;
        public Runtime() { Saves.Open(saves); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic()); }
        public void Dispose() { if (Player is { } c) c.Closed = true; lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(saves); }
    }
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire() { listener.Start(); var accept = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port); reader = accept.GetAwaiter().GetResult(); _ = Drain(); }
        async Task Drain() { try { var buffer = new byte[8192]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
