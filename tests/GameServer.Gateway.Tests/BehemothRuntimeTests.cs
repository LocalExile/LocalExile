using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BehemothRuntimeTests
{
    sealed class OwnedWork : ITransientCombatState
    {
        public int Clears;
        public void Clear() => Clears++;
        public IReadOnlyCollection<uint> RetiredObjects => [42u];
        public IReadOnlyCollection<(uint Actor, ushort Buff)> RetiredBuffs => [(99u, (ushort)5)];
    }

    sealed class Runtime : IProjectileRuntime
    {
        public bool Active { get; private set; } = true;
        public double NowTicks => 1_200_000;
        public void Invalidate() => Active = false;
    }

    sealed class OptIn : IDisposable
    {
        const string Name = "Game_VERIFIED_BEHEMOTH";
        readonly string? previous = Environment.GetEnvironmentVariable(Name);
        public void Set(bool enabled) => Environment.SetEnvironmentVariable(Name, enabled ? "1" : "0");
        public void Dispose() => Environment.SetEnvironmentVariable(Name, previous);
    }

    static Player Connection(params string[] modules) => new(new(1, "behemoth", 300, 300, 100, 100, null, []), 1, 1)
    {
        PlayerLife = 100, PlayerMana = 80,
        LeagueModule = new(new League("test", "Standard", false, false), modules.ToHashSet(StringComparer.OrdinalIgnoreCase)),
    };

    [Fact]
    public void Provider_retirement_releases_owned_state_and_old_generations_cannot_reclaim_a_worker()
    {
        var definition = Assert.Single(new AscendancyCatalog().Definitions,
            candidate => candidate.Identity.Family == AscendancyFamily.Phrecia && candidate.Name == "Behemoth");
        Assert.Equal("Berserker", definition.Identity.Id);
        Assert.Equal((byte)1, definition.BaseClass); // Characters row0 is the Marauder (IntegerId1).
        Assert.Equal((byte)2, definition.ClassNo);
        var identity = definition.Identity;
        using var optIn = new OptIn(); optIn.Set(true);
        var moduleAssembly = typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly;
        var policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(
            moduleAssembly.GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!,
            nonPublic: true)!;
        Assert.True(policy.Supports(identity));
        Assert.False(policy.Supports(identity with { Id = "Juggernaut" }));
        var conn = Connection("PhreciaAscendancies");
        var game = new ModuleLifetime("Game");
        var owner = new ModuleLifetime("PhreciaAscendancies");
        using var registration = AscendancyPolicies.Register(owner, policy);
        Assert.Null(AscendancyPolicies.SourceOwner(conn)); // Ordinary/unascended needs no optional provider.
        conn.MainAscendancy = new(identity, [7128, 49937], ["normal"]);
        var excluded = Connection(); excluded.MainAscendancy = conn.MainAscendancy;
        Assert.Null(AscendancyPolicies.SourceOwner(excluded));
        Assert.Same(owner, AscendancyPolicies.SourceOwner(conn));
        Assert.True(AscendancyPolicies.Available(conn, identity));
        var rampager = definition.Nodes[49937];
        Assert.Contains(AscendancyPolicies.NodeStats(conn, definition, rampager.Id, rampager.Stats),
            stat => stat.Key == Stat.MeleeHitsGrantRampageStacks && stat.Value != 0);
        optIn.Set(false);
        Assert.Null(AscendancyPolicies.SourceOwner(conn)); Assert.False(AscendancyPolicies.Available(conn, identity));
        optIn.Set(true);
        AscendancyPolicies.SelectionChanged(conn);
        var work = new OwnedWork(); conn.OwnedCombatState = work;
        conn.RampageBuff = 7;
        conn.Monsters[42] = new("owned", new(100, 100, 0), 0, 0, 100) { OwnerId = Program.PlayerId };
        conn.Monsters[99] = new("blinded", new(105, 100, 0), 0, 0, 100) { RampageBlindUntil = 9 };
        Assert.True(conn.OwnedCombatWorker.TryStart(conn, game, owner, work, out long oldGeneration));
        Assert.False(conn.OwnedCombatWorker.TryStart(conn, game, owner, work, out _));
        conn.VaalUseState = new object();
        owner.Stop(ex => throw ex); // Independent optional module retirement; Game is still active.
        Assert.True(game.IsActive); Assert.True(conn.Closed);
        Assert.Equal(1, work.Clears); Assert.Null(conn.OwnedCombatState); Assert.Null(conn.VaalUseState);
        Assert.DoesNotContain(42u, conn.Monsters.Keys); Assert.Equal(0, conn.Monsters[99].RampageBlindUntil);
        Assert.Contains(42u, conn.RetiredCombatObjects); Assert.Contains((99u, (ushort)5), conn.RetiredCombatBuffs);
        Assert.Contains((Program.PlayerId, (ushort)7), conn.RetiredCombatBuffs); Assert.Equal(0, conn.RampageBuff);
        Assert.False(conn.OwnedCombatWorker.Current(game, owner, work, oldGeneration));
        Assert.False(conn.OwnedCombatWorker.TryStart(conn, game, owner, work, out _));
        Assert.Null(AscendancyPolicies.SourceOwner(conn)); Assert.Equal(identity, conn.MainAscendancy.Selection);

        var nextOwner = new ModuleLifetime(owner.Id);
        using var current = AscendancyPolicies.Register(nextOwner, policy);
        var next = Connection("PhreciaAscendancies"); next.MainAscendancy = conn.MainAscendancy;
        var nextWork = new OwnedWork(); next.OwnedCombatState = nextWork;
        Assert.True(nextOwner.Generation > owner.Generation);
        Assert.True(next.OwnedCombatWorker.TryStart(next, game, nextOwner, nextWork, out long generation));
        next.OwnedCombatWorker.Complete(game, owner, work, oldGeneration);
        Assert.True(next.OwnedCombatWorker.Current(game, nextOwner, nextWork, generation));
        registration.Dispose(); Assert.Same(nextOwner, AscendancyPolicies.SourceOwner(next));
        for (int i = 0; i < 100; i++) Assert.True(next.Rampage.MeleeHit(10, true, true, true, true, true));
        next.Clock.Reset(); var runtime = new Runtime(); next.ProjectileRuntimeState = runtime;
        Assert.True(next.TryBeginAreaChange(new(2, 123, true), out _));
        Assert.False(runtime.Active); Assert.Equal(10, next.RampageTransferAt);
        Assert.Null(next.OwnedCombatState); Assert.Equal(1, nextWork.Clears);
        Assert.False(next.OwnedCombatWorker.Current(game, nextOwner, nextWork, generation));
        var carried = next.Rampage.Carry(next.RampageTransferAt!.Value);
        Assert.Equal(100, carried.Stacks); Assert.Equal(5, carried.Remaining);
        next.Rampage.Restore(carried, 1000);
        Assert.Equal(100, next.Rampage.Observe(1004.999, true, true, true).Stacks);
        Assert.Equal(0, next.Rampage.Observe(1005, true, true, true).Stacks);
        var afterDeath = new OwnedWork(); next.OwnedCombatState = afterDeath;
        Assert.True(next.OwnedCombatWorker.TryStart(next, game, nextOwner, afterDeath, out _));
        next.VaalUseState = new object(); next.VaalPreventionUntil = 20;
        next.PlayerLife = 0; next.PlayerLife = 100;
        Assert.Equal(1, afterDeath.Clears); Assert.Null(next.OwnedCombatState);
        Assert.Null(next.VaalUseState); Assert.Equal(0, next.VaalPreventionUntil);
        var gameWork = new OwnedWork(); next.OwnedCombatState = gameWork;
        Assert.True(next.OwnedCombatWorker.TryStart(next, game, nextOwner, gameWork, out _));
        game.Stop(ex => throw ex); Assert.Equal(1, gameWork.Clears); Assert.Null(next.OwnedCombatState);
        nextOwner.Stop(ex => throw ex);
        Assert.NotEqual(ConnectionWorker.NextEpoch(), ConnectionWorker.NextEpoch());
    }

    static Task Dispatch(Player conn, ushort skill, bool ground, ushort flags, ConnectionCiphers ciphers)
    {
        ushort id = ground ? InstanceProtocol.UseSkillAtLocationId : InstanceTransfer.UseSkillOnObjectId;
        var packet = new LoginWriter().U16(id);
        if (ground) packet.U32(105).U32(100); else packet.U32(43);
        byte[] bytes = packet.U16(skill).U16(1).U16(flags).ToArray();
        return Game.OnClientMessages([(id, bytes)], [], conn);
    }
}
