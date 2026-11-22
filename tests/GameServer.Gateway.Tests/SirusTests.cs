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
        var freezeBeam = Sirus.Attack(c, v, 84, 7022);
        Assert.Equal(100, freezeBeam.Ailments.FreezeChance);
        Assert.Equal(200, freezeBeam.Ailments.ChillEffectFinalPct);
        void FreezeBeam(bool critical, double now, IReadOnlyDictionary<uint, int> stats)
        {
            var cold = DamagePacket.Of(DamageType.Cold, 100000, 100000);
            Game.ApplyPlayerAilmentsLocked(c, cold, cold, critical, false, now, stats, freezeBeam.Ailments, source: 42);
            Game.ApplyCorruptedBloodOnHit(c, freezeBeam, cold, false, now, stats);
        }
        FreezeBeam(false, 0, new Dictionary<uint, int>());
        Assert.True(c.ElementalAilments.FreezeUntil(0) > 0);
        Assert.Equal(.6, c.ElementalAilments.FreezeUntil(0), 9);
        Assert.Equal(30, c.ElementalAilments.Snapshot(0).Chill); // Native +200% reaches the cap at this damage.
        Assert.True(c.RecentEvents.Happened(StatConditions.BeenFrozen, 0));
        Assert.Single(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.Freeze);
        c.FlaskAutomation.Discard();
        FreezeBeam(false, .1, new Dictionary<uint, int>());
        Assert.Empty(c.FlaskAutomation.Events); // Refreshing an existing ailment is not a new flask activation.
        c.ElementalAilments.Clear(); c.RecentEvents.Clear();
        var cannotFreeze = new Dictionary<uint, int> { [Program.Stats.Key("cannot_be_frozen")!.Value] = 1 };
        FreezeBeam(false, .2, cannotFreeze);
        Assert.False(c.ElementalAilments.Snapshot(.2).Freeze);
        Assert.False(c.RecentEvents.Happened(StatConditions.BeenFrozen, .2));
        Assert.DoesNotContain(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.Freeze);
        var avoidFreeze = new Dictionary<uint, int> { [Program.Stats.Key("avoid_freeze_%")!.Value] = 50 };
        foreach (bool critical in new[] { false, true })
        {
            c.ElementalAilments.Clear(); c.FlaskAutomation.Discard();
            var accepted = new AilmentRolls(99); c.IncomingHitRandom = accepted;
            FreezeBeam(critical, .3, avoidFreeze);
            Assert.Equal(1, accepted.ChanceRolls); // Native guarantee and crit are one freeze, not two avoidance rolls.
            Assert.True(c.ElementalAilments.Snapshot(.3).Freeze);
            Assert.Single(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.Freeze);
            c.ElementalAilments.Clear(); c.FlaskAutomation.Discard();
            var avoided = new AilmentRolls(0); c.IncomingHitRandom = avoided;
            FreezeBeam(critical, .4, avoidFreeze);
            Assert.Equal(1, avoided.ChanceRolls);
            Assert.False(c.ElementalAilments.Snapshot(.4).Freeze);
            Assert.DoesNotContain(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.Freeze);
        }
        // Native forced ignite uses the common base-damage roll and retains its monster source for damage taken.
        var igniteBeam = Sirus.Attack(c, v, 84, 7025);
        Assert.Equal(100, igniteBeam.Ailments.IgniteChance);
        Assert.Equal(200, igniteBeam.Ailments.IgniteDamageFinalPct);
        Assert.Equal(500, Sirus.Attack(c, v, 85, 7025).Ailments.IgniteDamageFinalPct);
        c.ElementalAilments.Clear(); c.FlaskAutomation.Discard(); c.IncomingHitRandom = new AilmentRolls(99);
        var fire = DamagePacket.Of(DamageType.Fire, 100, 300);
        Game.ApplyPlayerAilmentsLocked(c, fire, fire, false, false, 1, new Dictionary<uint, int>(), igniteBeam.Ailments, source: 42);
        bool sourceRead = false;
        Assert.Equal(405, c.ElementalAilments.Advance(1, 2, _ => 0, 0, 75, false, source =>
        { Assert.Equal(42u, source); sourceRead = true; return -50; }));
        Assert.True(sourceRead); // 300 base roll * .9 ignite * 3 native * .5 taken; no critical multiplier or mean shortcut.
        c.ElementalAilments.Clear();
        var lightningOnly = DamagePacket.Of(DamageType.Lightning, 100000, 100000);
        Game.ApplyPlayerAilmentsLocked(c, lightningOnly, lightningOnly, false, false, 2,
            new Dictionary<uint, int>(), igniteBeam.Ailments, source: 42);
        Assert.False(c.ElementalAilments.Snapshot(2).Ignite); // Forced chance still requires fire damage.
        var shockBeam = Sirus.Attack(c, v, 84, 7028);
        Assert.Equal(100, shockBeam.Ailments.ShockChance);
        Assert.Equal(200, shockBeam.Ailments.ShockDurationFinalPct);
        foreach (bool critical in new[] { false, true })
        {
            c.ElementalAilments.Clear(); c.FlaskAutomation.Discard();
            Game.ApplyPlayerAilmentsLocked(c, lightningOnly, lightningOnly, critical, false, 3,
                new Dictionary<uint, int>(), shockBeam.Ailments, source: 42);
            Assert.True(c.ElementalAilments.Snapshot(8.99).Shock > 0);
            Assert.Equal(0, c.ElementalAilments.Snapshot(9).Shock); // Native +200%: six seconds, including on crit.
            Assert.Equal(FlaskEnchantments.Trigger.Shock, Assert.Single(c.FlaskAutomation.Events).Trigger);
        }
        c.ElementalAilments.Clear(); c.Immune = true; c.FlaskAutomation.Discard();
        FreezeBeam(false, 4, new Dictionary<uint, int>());
        Assert.False(c.ElementalAilments.HasEffects);
        Assert.Empty(c.FlaskAutomation.Events);
        c.FlaskAutomation.Discard();
        c.IncomingHitRandom = Random.Shared;
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
    sealed class AilmentRolls(int chanceRoll) : Random
    {
        public int ChanceRolls { get; private set; }
        public override int Next(int minValue, int maxValue)
        {
            if (minValue == 0 && maxValue == 100) { ChanceRolls++; return chanceRoll; }
            return maxValue - 1;
        }
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
