using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class KitavaActTenTests
{
    [Fact]
    public async Task Final_Kitava_survives_bursts_and_reload_through_three_guarded_hearts_before_Sin_opens_the_exit()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var originalExit = new AreaTransitionObject(1900, 1900, AreaTransitionCatalog.HashOf("2_10_town")!.Value, "portal.ao")
            { SourceType = KitavaActTen.PortalType };
        var layout = new AreaLayout(1, KitavaActTen.Area, 4000, 4000, 2000, 1930, (_, _) => true, [(8195, originalExit)]);
        var c = new ClientState(layout, 1, 1) { Character = "FinalKitavaRegression", PlayerSent = true, PlayerLife = 1000000,
            Instance = new(), Pools = new(67, 1000000, 100, 1, 0), Wire = ("kitava-final", wire.Stream, wire.Ciphers) };
        runtime.Player = c; c.LaterFormPoints[KitavaActTen.Body] = (2000, 2000);
        c.QuestFlags.Add(KitavaActTen.Started); c.QuestFlags.Add("a5q6-CompletedBoss");
        var v = Game.Monsters.Find(KitavaActTen.Body)!;
        var body = new SpawnedMonster("Kitava", new PlayerWalk(2000, 2000, 0), 200, 20, 1000)
        { Variety = v, Level = 67, Rarity = MonsterRarity.Unique, States = v.StateMachine,
            LifePhases = KitavaActTen.Phases(v), EntranceGrace = true };
        uint id = 0x8000100; c.Monsters[id] = body; c.MonsterViews[id] = new((2000, 2000), (2000, 2000), 1000);
        Assert.Equal([8195u], KitavaActTen.ConfigurePortal(c));
        var exit = c.Layout.AreaTransitions.Single().Transition;
        Assert.Equal(KitavaActTen.Epilogue, AreaTransitionCatalog.IdOf(exit.DestinationHash16));
        Assert.Equal(KitavaActTen.EpilogueLabel, exit.Label);
        Assert.Equal((originalExit.GridX, originalExit.GridY, originalExit.Model), (exit.GridX, exit.GridY, exit.Model));
        Assert.Empty(KitavaActTen.ConfigurePortal(c));
        Assert.Same(originalExit, layout.AreaTransitions.Single().Transition);
        Assert.Null(c.PendingTransfer); // Configuring/opening the portal never moves the player.
        Assert.False(KitavaActTen.GateShown(c, exit));
        var fight = new KitavaActTen.Fight(id, body, c); await fight.Bind(c); await fight.Step(c, 1); await fight.Step(c, 7);
        Assert.Equal(2, fight.Stage);
        for (int interval = 0; interval < 3; interval++)
        {
            double now = 100 + interval * 100;
            body.Life = body.LifePhases!.Damage(body.Life, body.MaxLife, int.MaxValue);
            Assert.True(body.Life > 0); await fight.Step(c, now); Assert.True(body.Invulnerable);
            await fight.Step(c, now + 5);
            var heart = c.Monsters.Values.Single(m => m.Variety?.Id == KitavaActTen.Heart);
            Assert.False(heart.Invulnerable);
            Assert.Contains(c.Monsters.Values, m => !m.Dead && m.Variety?.Id == KitavaActTen.Root + "KitavaMinionBoss2");
            Assert.DoesNotContain(c.Monsters.Values, m => !m.Dead && m.Variety?.Id == KitavaActTen.Root + "KitavaMinionBoss1");
            await fight.Step(c, now + 13);
            Assert.Contains(c.Monsters.Values, m => !m.Dead && m.Variety?.Id == KitavaActTen.Root + "KitavaMinionBoss1");
            if (interval == 1)
            {
                foreach (var m in c.Monsters.Values.Where(m => m.Variety?.Id == KitavaActTen.Root + "KitavaMinionBoss1")) m.Dead = true;
                fight.Stop(); fight = new(id, body, c); await fight.Bind(c); await fight.Step(c, now + 14);
                Assert.DoesNotContain(c.Monsters.Values, m => !m.Dead && m.Variety?.Id == KitavaActTen.Root + "KitavaMinionBoss1");
            }
            await fight.Step(c, now + 21);
            Assert.Equal(interval == 2, c.Monsters.Values.Any(m => !m.Dead && m.Variety?.Id == KitavaActTen.Root + "KitavaMinionBoss6"));
            Assert.False(c.HasQuestFlag(Quests.EndGameAvailable)); Assert.False(KitavaActTen.GateShown(c, exit));
            heart.Life = heart.LifePhases!.Damage(heart.Life, heart.MaxLife, int.MaxValue);
            await fight.Step(c, now + 22); Assert.True(heart.Invulnerable);
            await fight.Step(c, now + 29);
            if (interval == 1)
            {
                Assert.Equal(6, fight.Stage);
                fight.Stop(); fight = new(id, body, c); await fight.Bind(c); await fight.Step(c, now + 30);
                Assert.True(body.Invulnerable); await fight.Step(c, now + 35); Assert.Equal(7, fight.Stage);
                await fight.Step(c, now + 40); Assert.Equal(2, fight.Stage); Assert.False(body.Invulnerable);
            }
            else if (interval == 2)
            {
                Assert.Equal(9, fight.Stage); Assert.False(c.HasQuestFlag(Quests.EndGameAvailable));
                await fight.Step(c, now + 47); Assert.Equal(10, fight.Stage);
            }
        }
        Assert.True(c.HasQuestFlag(Quests.EndGameAvailable)); Assert.Equal(-60, Game.ResistancePenalty(c));
        Assert.False(KitavaActTen.GateShown(c, exit));
        var sin = c.Monsters.Single(m => m.Value.Variety?.Id == KitavaActTen.Sin);
        Assert.False(sin.Value.Hostile); Assert.NotNull(c.Npcs.GetValueOrDefault(sin.Key));
        Assert.True(c.Instance.World.MonsterOrigins[sin.Key].Object.HasDialogue);
        var speaker = c.Npcs[sin.Key]; await fight.Step(c, 399); Assert.Same(speaker, c.Npcs[sin.Key]);
        c.Walk.Teleport(c.Npcs[sin.Key].X, c.Npcs[sin.Key].Y, c.Clock.Elapsed.TotalSeconds);
        await Game.OnUseObject("kitava-final", wire.Stream, wire.Ciphers, c, new(sin.Key, 0x266, 0, 0x408), c.Clock.Elapsed.TotalSeconds);
        Assert.Equal(sin.Key, c.TalkingTo);
        Assert.Equal(1, sin.Value.States!.SharedStates[sin.Value.States.IndexOf("finished")]);
        Game.CommitQuestFlags(c, [KitavaActTen.Talked]); Assert.True(KitavaActTen.GateShown(c, exit));
        await fight.Step(c, 400); Assert.Equal(-60, Game.ResistancePenalty(c));
        Assert.DoesNotContain(c.Monsters.Values, m => m.SummonerId == id && m.Hostile && !m.Dead);
        fight.Stop();
    }

    [Fact]
    public async Task Final_roster_uses_actor_level_conversion_and_damage_respects_live_immunity_and_unload()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var c = new ClientState(new(1, KitavaActTen.Area, 4000, 4000, 2000, 1930, (_, _) => true, []), 1, 1)
        { Character = "FinalKitavaDamage", PlayerSent = true, Instance = new(), PlayerLife = 1000000,
            Pools = new(67, 1000000, 100, 1, 0), Wire = ("kitava-damage", wire.Stream, wire.Ciphers) };
        runtime.Player = c; c.LaterFormPoints[KitavaActTen.Body] = (2000, 2000); c.QuestFlags.Add(KitavaActTen.Started);
        var v = Game.Monsters.Find(KitavaActTen.Body)!;
        Assert.Equal(30, KitavaActTen.SkillsFor(c, v, 67)!.Count);
        var beam = KitavaActTen.Attack(c, v, 67, 8484);
        Assert.True(beam.Spell); Assert.True(beam.Damage[DamageType.Fire].Max > 0);
        Assert.Equal(beam.Damage[DamageType.Fire], beam.Damage[DamageType.Physical]);
        Assert.True(KitavaActTen.Attack(c, v, 83, 8484).Damage[DamageType.Fire].Max > beam.Damage[DamageType.Fire].Max);
        Assert.False(KitavaActTen.Attack(c, v, 67, 6592).Spell);
        Assert.Null(KitavaActTen.Phases(Game.Monsters.Find("Metadata/Monsters/KitavaBoss/Kitava1")!));
        var body = new SpawnedMonster("Kitava", new PlayerWalk(2000, 2000, 0), 200, 20, 1000)
        { Variety = v, Level = 67, States = v.StateMachine, LifePhases = KitavaActTen.Phases(v), EntranceGrace = true };
        uint id = 0x8000100; c.Monsters[id] = body; c.MonsterViews[id] = new((2000, 2000), (2000, 2000), 1000);
        var fight = new KitavaActTen.Fight(id, body, c); await fight.Bind(c); await fight.Step(c, 1); await fight.Step(c, 7);
        await fight.Step(c, 9); Assert.True(c.PlayerLife < 1000000);
        c.Immune = true; uint life = c.PlayerLife;
        for (int now = 10; now < 90; now++) await fight.Step(c, now);
        Assert.Equal(life, c.PlayerLife); Assert.Equal(0, c.TrapDegenPerSecond);
        fight.Stop(); c.Immune = false; await fight.Step(c, 200);
        Assert.Equal(life, c.PlayerLife); Assert.Equal(0, c.TrapDegenPerSecond);
    }
    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-kitava-final-{Guid.NewGuid():N}.db");
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
