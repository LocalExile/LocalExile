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

    static Player PlayerOf(params string[] modules) => new(new(1, "behemoth", 300, 300, 100, 100, null, []), 1, 1)
    {
        PlayerLife = 100, PlayerMana = 80, Instance = new(),
        LeagueModule = new(new League("test", "Standard", false, false), modules.ToHashSet(StringComparer.OrdinalIgnoreCase)),
    };

    [Fact]
    public void Provider_retirement_releases_owned_state_and_old_generations_cannot_reclaim_a_worker()
    {
        var definition = Assert.Single(PhreciaFourClassesTests.PhreciaTree().Definitions,
            candidate => candidate.Name == "Behemoth");
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
        var player = PlayerOf("PhreciaAscendancies");
        var game = new ModuleLifetime("Game");
        var owner = new ModuleLifetime("PhreciaAscendancies");
        using var registration = AscendancyPolicies.Register(owner, policy);
        Assert.Null(AscendancyPolicies.SourceOwner(player)); // Ordinary/unascended needs no optional provider.
        player.Character.MainAscendancy = new(identity, [7128, 49937], ["normal"]);
        var excluded = PlayerOf(); excluded.Character.MainAscendancy = player.Character.MainAscendancy;
        Assert.Null(AscendancyPolicies.SourceOwner(excluded));
        Assert.Same(owner, AscendancyPolicies.SourceOwner(player));
        Assert.True(AscendancyPolicies.Available(player, identity));
        var rampager = definition.Nodes[49937];
        Assert.Contains(AscendancyPolicies.NodeStats(player, definition, rampager.Id, rampager.Stats),
            stat => stat.Key == Stat.MeleeHitsGrantRampageStacks && stat.Value != 0);
        optIn.Set(false);
        Assert.Null(AscendancyPolicies.SourceOwner(player)); Assert.False(AscendancyPolicies.Available(player, identity));
        optIn.Set(true);
        AscendancyPolicies.SelectionChanged(player);
        var work = new OwnedWork(); player.OwnedCombatState = work;
        player.RampageBuff = 7;
        player.Area.Monsters[42] = new("owned", new(100, 100, 0), 0, 0, 100) { OwnerId = player.Id };
        player.Area.Monsters[99] = new("blinded", new(105, 100, 0), 0, 0, 100) { RampageBlindUntil = 9 };
        Assert.True(player.OwnedCombatWorker.TryStart(player, game, owner, work, out long oldGeneration));
        Assert.False(player.OwnedCombatWorker.TryStart(player, game, owner, work, out _));
        player.VaalUseState = new object();
        owner.Stop(ex => throw ex); // Independent optional module retirement; Game is still active.
        Assert.True(game.IsActive); Assert.True(player.Closed);
        Assert.Equal(1, work.Clears); Assert.Null(player.OwnedCombatState); Assert.Null(player.VaalUseState);
        Assert.DoesNotContain(42u, player.Area.Monsters.Keys); Assert.Equal(0, player.Area.Monsters[99].RampageBlindUntil);
        Assert.Contains(42u, player.RetiredCombatObjects); Assert.Equal(0, player.RampageBuff);
        Assert.False(player.OwnedCombatWorker.Current(game, owner, work, oldGeneration));
        Assert.False(player.OwnedCombatWorker.TryStart(player, game, owner, work, out _));
        Assert.Null(AscendancyPolicies.SourceOwner(player)); Assert.Equal(identity, player.Character.MainAscendancy.Selection);

        var nextOwner = new ModuleLifetime(owner.Id);
        using var current = AscendancyPolicies.Register(nextOwner, policy);
        var next = PlayerOf("PhreciaAscendancies"); next.Character.MainAscendancy = player.Character.MainAscendancy;
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
    public void Vaal_commit_with_inspiration_support_gains_charges_on_paid_mana()
    {
        string save = Path.Combine(Path.GetTempPath(), $"behemoth-vaal-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(save);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var player = new Player(new(1, "behemoth", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Behemoth" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            God = true,
            Pools = new(100, 1000, 1000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        player.Clock.Start();
        using var waived = TestRequirements.Waive(player);
        using var statBuff = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
        {
            if (!ReferenceEquals(p, player)) return sources;
            var copy = new Dictionary<uint, int>(sources)
            {
                [Stat.VaalAttackRageCostInsteadOfSoulsPerUse] = 1,
                [Stat.NoInherentRageLoss] = 1,
                [ManaRecoveryRules.RagePer200] = 10,
                [Stat.CannotGainRageDuringSoulGainPrevention] = 0,
            };
            return copy;
        });
        try
        {
            var vaal = Program.Items.FindGem("Vaal Ground Slam")!.Value;
            var inspiration = Program.Items.FindGem("Inspiration Support")!.Value;
            var club = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
            {
                Sockets =
                [
                    new(SocketColour.White, ItemCatalog.Create(vaal.Type) with { GemVariant = vaal.Variant.Index }),
                    new(SocketColour.White, ItemCatalog.Create(inspiration.Type) with { GemVariant = inspiration.Variant.Index }),
                ],
                Links = [2],
            };
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, club);
            var skill = Game.GemSkillEffects(player).Single(g => Game.SkillsFor(player).EffectId(g.Effect) == "VaalGroundSlam");
            Assert.Contains(Game.SupportedSkill(player, skill.Id).Supports, support => support.Effect.Id == "SupportInspiration");
            var rage = new RageState.Inputs(NoInherentLoss: true);
            Assert.True(player.Rage.Gain(30, 0, rage));
            var resources = Game.ResourceEffectsOf(player);
            resources.Mana.Spent(199, 10);
            var plan = (VaalUseReservation)typeof(Game).GetMethod("PlanVaalUse", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [player, skill.Id])!;
            Assert.Equal(0u, plan.Mana); // The native Vaal Ground Slam has no mana cost.
            // Exercise the positive-mana reservation already accepted by the commit path.
            plan = plan with { Mana = 1, PreventionSeconds = 0 };
            uint manaBeforeCommit = player.PlayerMana;
            typeof(Game).GetMethod("CommitVaalUse", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [player, plan, 123d]);
            Assert.Equal(manaBeforeCommit - 1, player.PlayerMana);
            Assert.Equal(0, resources.Mana.SpentRemainder);
            Assert.Equal(1, player.SupportGems.InspirationCharges);
            Assert.Equal(30 - plan.Rage + 10, player.Rage.Read(0, rage).Current);
            Assert.Equal(123, player.NextAttackAt);
        }
        finally
        {
            player.Closed = true;
            lifetime.Stop(_ => { });
            PlayerInventories.Forget(player.Character.Name!);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(save);
        }
    }

    static Task Dispatch(Player player, ushort skill, bool ground, ushort flags, ConnectionCiphers ciphers)
    {
        ushort id = ground ? InstanceProtocol.UseSkillAtLocationId : InstanceTransfer.UseSkillOnObjectId;
        var packet = new LoginWriter().U16(id);
        if (ground) packet.U32(105).U32(100); else packet.U32(43);
        byte[] bytes = packet.U16(skill).U16(1).U16(flags).ToArray();
        return Game.OnClientMessages([(id, bytes)], [], player);
    }
}
