using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class KitavaActFiveTests
{
    [Fact]
    public async Task Rooftop_waits_for_both_heart_intervals_then_destroys_the_sign_and_rescues_the_player()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var layout = new AreaLayout(1, KitavaActFive.Area, 4000, 4000, 2000, 1930, (_, _) => true, [])
        {
            Level = new(4000, 4000, [], [], [], [], [], [], [])
            {
                RoomObjects = [new(KitavaActFive.Root + "SignOfPurity/SignOfPurity", "", 2000, 1900, 0, 0),
                    new("Metadata/Terrain/Act5/Area8/Objects/LandingZone", "", 3000, 1000, 0, 0)],
            },
        };
        var c = new ClientState(layout, 1, 1) { Character = "KitavaRegression", PlayerSent = true, PlayerLife = 1000000,
            Instance = new(), Pools = new(45, 1000000, 100, 1, 0), Wire = ("kitava", wire.Stream, wire.Ciphers) };
        runtime.Player = c; c.LaterFormPoints[KitavaActFive.First] = (2000, 2000);
        var v = Game.Monsters.Find(KitavaActFive.First)!;
        var body = new SpawnedMonster("Kitava", new PlayerWalk(2000, 2000, 0), 200, 20, 1000)
        { Variety = v, Level = 45, Rarity = MonsterRarity.Unique, States = v.StateMachine, LifePhases = KitavaActFive.Phases(v), EntranceGrace = true };
        uint id = 0x8000100; c.Monsters[id] = body; c.MonsterViews[id] = new((2000, 2000), (2000, 2000), 1000);
        Assert.Equal(11, KitavaActFive.SkillsFor(c, v, 45)!.Count);
        var debris = KitavaActFive.Attack(c, v, 45, 6636);
        Assert.Equal("Metadata/Projectiles/KitavaDebrisThrow", debris.Projectile!.Metadata);
        Assert.Equal(4, debris.Projectile.Count); Assert.True(debris.Projectile.Lobbed); Assert.NotNull(debris.Release);
        var ladder = new AreaTransitionObject(100, 100, 1) { SourceType = "Metadata/QuestObjects/Act5/CathedralKitavaLadder" };
        var decoration = ladder with { SourceType = "Metadata/MiscellaneousObjects/PortalToggleable", Model = "Metadata/Effects/Microtransactions/Town_Portals/steam_portals/steam_portal_04/rig.ao" };
        Assert.False(KitavaActFive.GateShown(c, decoration)); Assert.False(KitavaActFive.GateShown(c, ladder));
        var fight = new KitavaActFive.Fight(id, body, c); await fight.Bind(c); await fight.Step(c, 1); await fight.Step(c, 9);
        Assert.Equal(1, fight.Stage);
        body.Life = body.LifePhases!.Damage(body.Life, body.MaxLife, int.MaxValue);
        Assert.Equal(500u, body.Life); await fight.Step(c, 10); Assert.True(body.Invulnerable); Assert.Equal(2, fight.Stage);
        await fight.Step(c, 18);
        body = c.Monsters.Values.Single(m => m.Variety?.Id == KitavaActFive.Second);
        id = c.Monsters.Single(p => ReferenceEquals(p.Value, body)).Key;
        Assert.False(c.Monsters.ContainsKey(0x8000100)); Assert.Equal(14, KitavaActFive.SkillsFor(c, body.Variety!, 45)!.Count);
        await fight.Step(c, 25);
        for (int interval = 0; interval < 2; interval++)
        {
            double now = 30 + interval * 30;
            body.Life = body.LifePhases!.Damage(body.Life, body.MaxLife, int.MaxValue);
            Assert.Equal((uint)((ulong)body.MaxLife * (uint)(interval == 0 ? 75 : 50) / 100), body.Life);
            await fight.Step(c, now); Assert.True(body.Invulnerable);
            await fight.Step(c, now + 5);
            var heart = c.Monsters.Values.Single(m => m.Variety?.Id == KitavaActFive.Heart);
            Assert.False(heart.Invulnerable); Assert.Contains(c.Monsters.Values, m => m.Variety?.Id == KitavaActFive.Root + (interval == 0 ? "KitavaMinionBoss1" : "KitavaMinionBoss2"));
            Assert.False(c.HasQuestFlag(KitavaActFive.Complete));
            heart.Life = heart.LifePhases!.Damage(heart.Life, heart.MaxLife, int.MaxValue);
            await fight.Step(c, now + 6); Assert.True(heart.Invulnerable);
            if (interval == 0)
            {
                fight.Stop(); fight = new(id, body, c); await fight.Bind(c); await fight.Step(c, now + 7);
                await fight.Step(c, now + 13);
                Assert.False(body.LifePhases.Active); Assert.Equal(1, body.LifePhases.Completed);
            }
            else await fight.Step(c, now + 12);
        }
        Assert.Contains(c.Monsters.Values, m => m.Variety?.Id == KitavaActFive.Last);
        Assert.False(c.HasQuestFlag(KitavaActFive.Complete));
        c.Monsters[0x9000001] = new("dock pack", new PlayerWalk(3000, 1000, 0), 20, 2, 100) { Hostile = true };
        c.Monsters[0x9000002] = new("friendly minion", new PlayerWalk(3000, 1000, 0), 20, 2, 100) { Hostile = false };
        c.Monsters[0x9000003] = new("distant pack", new PlayerWalk(100, 100, 0), 20, 2, 100) { Hostile = true };
        await fight.Step(c, 80); await fight.Step(c, 88); await fight.Step(c, 89);
        Assert.True(c.HasQuestFlag(KitavaActFive.Complete)); Assert.Equal(-30, Game.ResistancePenalty(c));
        Assert.Equal(layout.Near((3000, 1000)), c.Walk.Position(89));
        Assert.True(KitavaActFive.GateShown(c, ladder)); Assert.False(KitavaActFive.GateShown(c, decoration));
        Assert.False(c.Monsters.ContainsKey(0x9000001)); Assert.True(c.Monsters.ContainsKey(0x9000002)); Assert.True(c.Monsters.ContainsKey(0x9000003));
        var landing = c.Walk.Position(89); await fight.Step(c, 100); Assert.Equal(landing, c.Walk.Position(100));
        fight.Stop();
    }

    [Fact]
    public void Each_earned_Kitava_penalty_rebuilds_immediately_and_never_stacks_with_its_alias_or_repeated_flag()
    {
        using var runtime = new Runtime();
        var c = new ClientState(new(1, "penalty", 100, 100, 20, 20, null, []), 1, 1)
        { Character = "KitavaPenaltyRegression", Pools = new(45, 1000, 100, 1, 0) };
        runtime.Player = c;
        c.QuestFlags.Add(Quests.EndGameAvailable); c.UnearnedQuestFlags.Add(Quests.EndGameAvailable);
        c.Pools = Game.PoolsFor(c, 45);
        DamageType[] types = [DamageType.Fire, DamageType.Cold, DamageType.Lightning, DamageType.Chaos];
        string[] names = ["fire", "cold", "lightning", "chaos"];
        int[] Panel() => names.Select(n => Game.CharacterPlayerStats(c).GetValueOrDefault(Program.Stats.Key($"uncapped_{n}_damage_resistance_%")!.Value)).ToArray();
        var before = types.Select(t => Game.Resistance(c, t)).ToArray();
        Assert.Equal(before, Panel());
        Assert.Equal(0, Game.ResistancePenalty(c));
        Assert.NotNull(Game.CommitQuestFlags(c, [KitavaActFive.Complete]));
        Assert.Equal(before.Select(r => r - 30), types.Select(t => Game.Resistance(c, t)));
        Assert.Equal(before.Select(r => r - 30), Panel());
        Assert.Null(Game.CommitQuestFlags(c, [KitavaActFive.Complete]));
        Game.CommitQuestFlags(c, ["SinOnKitavaDefeat"]); Assert.Equal(-30, Game.ResistancePenalty(c));
        Assert.NotNull(Game.CommitQuestFlags(c, [Quests.EndGameAvailable]));
        Assert.Equal(before.Select(r => r - 60), types.Select(t => Game.Resistance(c, t)));
        Assert.Equal(before.Select(r => r - 60), Panel());
        Assert.Null(Game.CommitQuestFlags(c, [Quests.EndGameAvailable]));
        Game.CommitQuestFlags(c, ["a10q3-UsedRewardBook"]); Assert.Equal(-60, Game.ResistancePenalty(c));
    }
    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-kitava-{Guid.NewGuid():N}.db");
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
        public Wire() { listener.Start(); var accept = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port); reader = accept.GetAwaiter().GetResult(); }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
