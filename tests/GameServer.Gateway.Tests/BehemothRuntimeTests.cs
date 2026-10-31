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

    static ClientState Connection(params string[] modules) => new(new(1, "behemoth", 300, 300, 100, 100, null, []), 1, 1)
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
            stat => stat.Key == Program.Stats.Key("melee_hits_grant_rampage_stacks") && stat.Value != 0);
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

    [Fact]
    public async Task Vaal_dispatch_reserves_payment_and_shared_attack_cadence_together_before_echo()
    {
        using var setup = new AdmissionSetup();
        var c = setup.Conn;
        // Contact/kill accounting must not rebuild the full player snapshot for every sweep victim.
        int sourceReads = 0;
        using (GameEvents.PlayerStatSources.Add("Game", (_, sources) => { sourceReads++; return sources; }))
        {
            var melee = typeof(Game).GetMethod("RampageOnMeleeHitLocked", BindingFlags.NonPublic | BindingFlags.Static)!;
            var kill = typeof(Game).GetMethod("RampageOnKillLocked", BindingFlags.NonPublic | BindingFlags.Static)!;
            static object? Call(MethodInfo method, params object?[] arguments) => method.Invoke(null,
                [.. arguments, .. method.GetParameters().Skip(arguments.Length).Select(parameter => parameter.DefaultValue)]);
            var victim = new SpawnedMonster("rampage contact", new(105, 100, 0), 0, 0, 100);
            Call(melee, c, victim, false);
            Assert.Equal(0, sourceReads); Assert.Equal(0, c.Rampage.Current.Stacks);
            uint source = Program.Stats.Key(RampageSession.Source)!.Value;
            c.PlayerStats[source] = 1;
            Call(melee, c, victim, true);
            Call(kill, c, victim);
            Call(kill, c, victim); // A repeated lethal notification never grants again.
            Assert.Equal(2, c.Rampage.Current.Stacks);
            Assert.Equal(0, sourceReads); // Explicit current source needs no player or raw-source rebuild.
            c.PlayerStats[source] = 0;
            Call(melee, c, victim, true);
            Assert.Equal(0, c.Rampage.Current.Stacks); Assert.Equal(0, sourceReads);
            Assert.True(c.OwnedCombatState is not RampageWorld world || !world.Enabled);
            c.PlayerStats.Remove(source);
            var snapshot = new Dictionary<uint, int> { [source] = 1 };
            var nextVictim = new SpawnedMonster("snapshot contact", new(105, 100, 0), 0, 0, 100);
            var eligible = Call(melee, c, nextVictim, true, snapshot);
            Call(kill, c, nextVictim, eligible, snapshot);
            Assert.Equal(2, c.Rampage.Current.Stacks); Assert.Equal(0, sourceReads);
            c.PlayerStats[source] = 0; // Current removal wins over an earlier caller snapshot.
            Call(melee, c, nextVictim, true, snapshot);
            Assert.Equal(0, c.Rampage.Current.Stacks); Assert.Equal(0, sourceReads);
            c.PlayerStats.Remove(source);
        }
        var skill = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).EffectId(g.Effect) == "VaalGroundSlam");
        var rage = new RageState.Inputs(NoInherentLoss: true);
        Assert.True(c.Rage.Gain(30, 0, rage));
        c.NextAttackAt = 60; c.ActionCount = 17; c.ChannelSkill = 0x104;
        c.Walk.Order(140, 100, 0);
        var target = new SpawnedMonster("early target", new(105, 100, 0), 0, 0, 100000);
        c.Monsters[43] = target;
        foreach (var (ground, flags) in new[] { (false, 1), (false, 0), (true, 1) })
        {
            await Dispatch(c, skill.Id, ground, (ushort)flags, null!);
            Assert.Equal(17, c.ActionCount); Assert.Equal((ushort)0x104, c.ChannelSkill);
            Assert.Equal((140, 100), c.Walk.Destination); Assert.Equal(60, c.NextAttackAt);
            Assert.Equal(30, c.Rage.Read(0, rage).Current); Assert.Equal(1000u, c.PlayerMana);
            Assert.Equal(0, c.VaalPreventionUntil); Assert.Null(c.VaalUseState);
            Assert.Empty(c.SkillReadyAt); Assert.Empty(c.SingleUseCooldowns); Assert.Equal(target.MaxLife, target.Life);
        }
        var cooldown = new SingleUseCooldown(1000, 0);
        Assert.False(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, cooldown, false,
            () => throw new Exception("Early attack must not start an action"), out _));
        Assert.Empty(c.SingleUseCooldowns);
        c.NextAttackAt = 0;
        Assert.True(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, null, false,
            () => c.ActionCount++, out _, reserve: false));
        Assert.Equal(0, c.NextAttackAt); Assert.Null(c.VaalUseState); Assert.Equal(30, c.Rage.Read(0, rage).Current);
        c.NextAttackAt = 60;
        Assert.False(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, null, false,
            () => throw new Exception("Arrival must recheck the shared deadline"), out _, actionToken: c.ActionCount));
        c.NextAttackAt = 0;
        Assert.True(Game.TryBeginCooldownUse(c, skill.Effect, cooldown, false, () => { }, out _));
        Assert.False(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, cooldown, false,
            () => throw new Exception("Busy cooldown must not pay"), out _));
        Assert.Equal(0, c.NextAttackAt); Assert.Null(c.VaalUseState); Assert.Equal(30, c.Rage.Read(0, rage).Current);
        c.SkillReadyAt.Clear(); c.SingleUseCooldowns.Clear(); c.ChannelSkill = 0;
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1)); Assert.True(gate.Wait(0));
        var dispatch = Game.CastOn("behemoth-admission", null!, ciphers, c,
            new(43, skill.Id, 1, 1), target, c.Clock.Elapsed.TotalSeconds);
        Assert.False(dispatch.IsCompleted); // Deterministically held at the send gate, without a timer or socket.
        double deadline = c.NextAttackAt;
        Assert.True(deadline > 0); Assert.Equal(15, c.Rage.Read(0, rage).Current);
        Assert.True(c.VaalPreventionUntil > 0); Assert.NotNull(c.VaalUseState);
        var currentUse = typeof(Game).GetMethod("VaalAdmissionCurrent", BindingFlags.NonPublic | BindingFlags.Static)!;
        uint conversion = Program.Stats.Key("vaal_attack_rage_cost_instead_of_souls_per_use")!.Value;
        c.PlayerStats[conversion] = 0; // A refund/source removal within this same provider generation.
        Assert.False((bool)currentUse.Invoke(null, [c, c.VaalUseState])!);
        Assert.False(Game.SkillAcknowledgementAllowed(c, skill.Effect, skill.Id,
            () => throw new Exception("Removed conversion may not acknowledge stale paid work")));
        c.PlayerStats[conversion] = 1;
        Assert.True((bool)currentUse.Invoke(null, [c, c.VaalUseState])!);
        var execute = typeof(Game).GetMethod("ExecuteVaalUse", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(execute.Invoke(null, [c, skill.Id, skill.Effect]));
        Assert.Null(execute.Invoke(null, [c, skill.Id, skill.Effect]));
        Assert.Equal(deadline, c.NextAttackAt); // Execution cannot reserve the paid swing a second time.
        int action = c.ActionCount;
        await Dispatch(c, skill.Id, true, 1, ciphers);
        Assert.Equal(action, c.ActionCount); Assert.Equal(15, c.Rage.Read(0, rage).Current);
        var movement = Dispatch(c, 0x2909, true, 1, ciphers);
        Assert.Equal(action + 1, c.ActionCount); Assert.False(Game.PlayerActionCurrent(c, action));
        c.Closed = true; gate.Release();
        await Task.WhenAll(dispatch, movement);
        Assert.Equal(target.MaxLife, target.Life); Assert.Equal(deadline, c.NextAttackAt);
        Assert.Equal(15, c.Rage.Read(0, rage).Current);
        c.Closed = false; c.VaalUseState = null; c.NextAttackAt = 60;
        foreach (bool instant in new[] { false, true })
        {
            int effect = Game.SkillsFor(c).EffectRow(instant ? "Haste" : "Fireball");
            Assert.True(Game.TryBeginSkillCooldownUse(c, (ushort)(instant ? 0x501 : 0x500), effect, cooldown, instant,
                () => { if (!instant) c.ActionCount++; }, out var ticket));
            Assert.True(ticket!.TryExecute(c, effect)); Assert.Equal(60, c.NextAttackAt);
        }
    }

    static Task Dispatch(ClientState conn, ushort skill, bool ground, ushort flags, ConnectionCiphers ciphers)
    {
        ushort id = ground ? InstanceProtocol.UseSkillAtLocationId : InstanceTransfer.UseSkillOnObjectId;
        var packet = new LoginWriter().U16(id);
        if (ground) packet.U32(105).U32(100); else packet.U32(43);
        byte[] bytes = packet.U16(skill).U16(1).U16(flags).ToArray();
        return Game.OnClientMessages("behemoth-admission", null!, ciphers, [(id, bytes)], [], conn);
    }

    sealed class AdmissionSetup : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"localexile-behemoth-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        internal readonly ClientState Conn;
        public AdmissionSetup()
        {
            Saves.Open(path);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            Conn = new(new(1, "admission", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = "BehemothAdmission", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            var items = Game.ItemsOf(Conn);
            var club = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalGroundSlam")!);
            items.Add(PlayerInventories.Weapon1, 0, 0, club with
            {
                Sockets = [new ItemSocket(SocketColour.White, gem)], Links = [1],
            });
            foreach (var (name, value) in new[] { ("vaal_attack_rage_cost_instead_of_souls_per_use", 1),
                ("cannot_gain_rage_during_soul_gain_prevention", 1), ("no_inherent_rage_loss", 1),
                ("virtual_strength_for_requirements", 1000), ("virtual_dexterity_for_requirements", 1000),
                ("virtual_intelligence_for_requirements", 1000) }) Conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        }
        public void Dispose()
        {
            Conn.Closed = true; Conn.RetireOwnedCombat(); lifetime.Stop(ex => throw ex);
            PlayerInventories.Forget(Conn.Character!);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
