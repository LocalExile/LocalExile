using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CooldownAdmissionTests
{

    static void CheckCommittedDashSharing(Fixture f)
    {
        var c = f.Player;
        var inventory = Game.ItemsOf(c);
        foreach (var (slot, item) in inventory.EquipmentSnapshot()) inventory.Remove(slot, item.Id);
        c.SingleUseCooldowns.Clear(); c.SkillReadyAt.Clear(); c.CooldownGroups.Clear(); c.CooldownSources.Clear();
        c.Use(new(AreaLayouts.TheCoast, "1_1_2", 1000, 1000, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        c.Connection.PlayerSent = c.Connection.PlayerObjectCreated = true;
        c.Walk.Teleport(100, 100, 0);
        var ids = f.Equip(PlayerInventories.Weapon1, "Dash", "Frostblink");
        ushort dashId = ids.Single(id => Game.SkillsFor(c).EffectId(Game.SupportedSkill(c, id).Effect) == "Dash");
        ushort frostId = ids.Single(id => id != dashId);
        var dash = Game.SupportedSkill(c, dashId); var frost = Game.SupportedSkill(c, frostId);
        c.PlayerMana = 0;
        Assert.False(Game.TryCommitDashUse(c, dashId, dash.Effect, (130, 100), out _, flags: 1));
        Assert.Equal((100, 100), c.Walk.Position(0)); Assert.Empty(c.SingleUseCooldowns);
        c.PlayerMana = 1000;
        Assert.True(Game.TryCommitDashUse(c, dashId, dash.Effect, (130, 100), out var receipt, flags: 1));
        Assert.True(receipt!.To.X < receipt.From.X); // The native reverse flag survives atomic admission.
        Assert.Equal(1000u - receipt.ManaCost, c.PlayerMana);
        Assert.Equal(1, c.SingleUseCooldowns[frost.Effect].SpentUses);
        Assert.False(Game.TryBeginSkillCooldownUse(c, frostId, frost.Effect, Game.CooldownForSkill(c, frostId),
            false, () => throw new Exception("Dash already spent the shared Blink use"), out _));
        c.ClientSkillIds.UnionWith(ids);
        var current = Game.GemSkillEffects(c).Select(g => (g.Id, g.Effect)).ToArray();
        Assert.Null(Game.NativeCooldownRefresh(c, c.ProjectileEpoch, () => current));
        Assert.True(receipt.ConfirmNativeUse(c));
        Assert.NotNull(Game.NativeCooldownRefresh(c, c.ProjectileEpoch, () => current));
        Assert.Null(Game.NativeCooldownRefresh(c, c.ProjectileEpoch, () => current));
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time;
        public double Now(Player _) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    [Fact]
    public void Shared_blink_debit_publishes_the_dash_copy_whose_plan_it_used()
    {
        using var f = new Fixture();
        var player = f.Player;
        ushort first = f.Equip(PlayerInventories.Weapon1, "Dash").Single();
        f.Equip(PlayerInventories.Helmet, "Dash", "Second Wind Support");
        ushort second = Game.GemSkillEffects(player).Single(g => g.Name == "Dash" && g.Id != first).Id;
        ushort frostblink = f.Equip(PlayerInventories.Gloves, "Frostblink").Single();
        var firstPlan = Game.CooldownForSkill(player, first)!.Value;
        var secondPlan = Game.CooldownForSkill(player, second)!.Value;
        Assert.NotEqual(firstPlan.RecoveryRate, secondPlan.RecoveryRate);
        int dashEffect = Game.SupportedSkill(player, second).Effect;
        int frostblinkEffect = Game.SupportedSkill(player, frostblink).Effect;
        Assert.True(Game.CooldownKey(player, dashEffect, second) < 0);
        player.ClientSkillIds.UnionWith([first, second, frostblink]);

        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            Assert.True(Game.TryBeginSkillCooldownUse(player, second, dashEffect, secondPlan, true, () => { }, out var dash));
            Assert.True(dash!.CommitNativeUse(player, second));
            clock.Time += 60;
            Game.RefreshSingleUseCooldowns(player);

            Assert.True(Game.TryBeginSkillCooldownUse(player, frostblink, frostblinkEffect,
                Game.CooldownForSkill(player, frostblink), true, () => { }, out var frost));
            var receipt = Assert.Single(frost!.SharedUses);
            Assert.Equal(secondPlan.RecoveryRate, receipt.State.Rate);
            Assert.Equal(second, receipt.Skill);
            Assert.True(frost.CommitNativeUse(player, frostblink));

            float seconds = secondPlan.BaseMilliseconds / 1000f;
            var expected = InstanceEntities.PlayerSingleUseCooldowns(player.Id,
                [new(second, seconds, seconds, (float)secondPlan.RecoveryRate, 1)]);
            var actual = Game.NativeCooldownRefresh(player, player.ProjectileEpoch,
                () => [(first, dashEffect), (second, dashEffect)]);
            Assert.Equal(expected, actual);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public void Duplicate_active_gems_keep_stored_uses_and_do_not_reset_recharge_debt()
    {
        using var f = new Fixture();
        var ids = f.Equip(PlayerInventories.Weapon1, "Flame Dash", "Second Wind Support");
        var original = Game.SupportedSkill(f.Player, ids[0]);
        var first = Game.CooldownFor(f.Player, original.Effect, original.Level, original.Quality);
        Assert.NotNull(first); Assert.Equal(4, first.Value.Uses);
        Assert.True(Game.TryBeginSkillCooldownUse(f.Player, ids[0], original.Effect, first, false, () => { }, out _));
        var state = f.Player.SingleUseCooldowns[original.Effect];
        f.Equip(PlayerInventories.Helmet, "Flame Dash");
        Assert.Equal(2, Game.GemSkillEffects(f.Player).Count(g => g.Effect == original.Effect));
        Assert.NotNull(Game.CooldownFor(f.Player, original.Effect, original.Level, original.Quality));
        var copies = Game.GemSkillEffects(f.Player).Where(g => g.Effect == original.Effect).ToArray();
        Assert.Equal(4, Game.CooldownForSkill(f.Player, ids[0])!.Value.Uses);
        Assert.Equal(3, Game.CooldownForSkill(f.Player, copies.Single(g => g.Id != ids[0]).Id)!.Value.Uses);
        Game.RefreshSingleUseCooldowns(f.Player);
        Assert.Same(state, f.Player.SingleUseCooldowns[original.Effect]);
        Assert.Equal(1, state.SpentUses);
        ushort second = copies.Single(g => g.Id != ids[0]).Id;
        Assert.True(Game.TryBeginSkillCooldownUse(f.Player, second, original.Effect,
            Game.CooldownForSkill(f.Player, second), false, () => { }, out _));
        var dashId = f.Equip(PlayerInventories.BodyArmour, "Dash").Single();
        var dash = Game.SupportedSkill(f.Player, dashId);
        // Dash's native aliases use one normalized key; Flame Dash still owes its independent stored uses.
        f.Player.SingleUseCooldowns[Game.CooldownKey(f.Player, dash.Effect, dashId)] = new(0, 1);
        Assert.True(Game.TryBeginSkillCooldownUse(f.Player, dashId, dash.Effect,
            Game.CooldownForSkill(f.Player, dashId), false, () => { }, out _));
        Assert.Equal(3, state.Uses); Assert.Equal(1d, state.Rate);

        foreach (var i in Game.ItemsOf(f.Player).Items(PlayerInventories.Helmet)) Game.ItemsOf(f.Player).Remove(PlayerInventories.Helmet, i.Id);
        Game.RefreshSingleUseCooldowns(f.Player);
        Assert.Same(state, f.Player.SingleUseCooldowns[original.Effect]); Assert.Equal(3, state.SpentUses);
    }

    [Fact]
    public void Frostblink_crosses_unwalkable_gaps_but_keeps_endpoint_and_quest_barrier_checks()
    {
        using var f = new Fixture();
        var c = f.Player;
        c.Connection.PlayerSent = true;
        c.Use(new(1, "blink-ledge", 200, 200, 100, 100,
            (x, y) => x >= 0 && y >= 0 && x < 200 && y < 200 && (x < 115 || x > 135), [])
        { CellHeights = Enumerable.Range(0, 40000).Select(i => (short)(i % 200 >= 130 ? 32 : 0)).ToArray() });
        Assert.True(InstantLocationSkill.TryResolve(c, c.ProjectileEpoch, (140, 100), false, _ => 0, out _, out var to));
        Assert.Equal((140, 100), to);
        c.Barrier = new HashSet<(int, int)> { (120, 100) };
        Assert.False(InstantLocationSkill.TryResolve(c, c.ProjectileEpoch, (140, 100), false, _ => 0, out _, out _));
        c.Barrier = new HashSet<(int, int)>();
        Assert.False(InstantLocationSkill.TryResolve(c, c.ProjectileEpoch, (120, 100), false, _ => 0, out _, out _));
        Assert.False(InstantLocationSkill.TryResolve(c, c.ProjectileEpoch, (140, 100), true, _ => 0, out _, out _));
        Assert.True(InstantLocationSkill.TryCommit(c, c.ProjectileEpoch, (140, 100), false, _ => 0, out to));
        Assert.Equal((140, 100), c.Walk.Position(Game.ProjectileWorldTime(c)));
    }

    [Fact]
    public async Task Frostblink_slides_around_monster_blockers_and_queues_cooldown_refresh_in_area_frame()
    {
        using var f = new Fixture();
        var c = f.Player;
        c.Connection.PlayerSent = true;
        c.Use(new(1, "blink-blocker", 200, 200, 100, 100, (x, y) => x >= 0 && y >= 0 && x < 200 && y < 200, []));
        // Aiming onto a live monster's blocker footprint slides to the nearest clear cell instead of dropping the cast.
        var zombie = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite"));
        c.Area.Monsters[0x80000001u] = new("blocker", new(140, 100, 0), 0, 0, 1000) { Hostile = true, Variety = zombie };
        Assert.True(InstantLocationSkill.TryResolve(c, c.ProjectileEpoch, (140, 100), false, _ => 0, out _, out var to));
        Assert.True(Math.Max(Math.Abs(to.X - 140), Math.Abs(to.Y - 100)) - (zombie.ObjectSize - 1) >= 2);
        Assert.InRange(Math.Max(Math.Abs(to.X - 140), Math.Abs(to.Y - 100)), 4, 6);

        // Large-footprint monsters (ObjectSize > 8) expand the blocker slide radius beyond 8 cells.
        c.Area.Monsters.Clear();
        var large = zombie with { ObjectSize = 15 };
        c.Area.Monsters[0x80000002u] = new("large-blocker", new(140, 100, 0), 0, 0, 1000) { Hostile = true, Variety = large };
        Assert.True(InstantLocationSkill.TryResolve(c, c.ProjectileEpoch, (140, 100), false, _ => 0, out _, out var largeTo));
        Assert.True(Math.Max(Math.Abs(largeTo.X - 140), Math.Abs(largeTo.Y - 100)) - (large.ObjectSize - 1) >= 2);

        // Inside an area frame, SendNativeCooldownRefresh queues 0x02bc into Area.Delta (only: player) after 0x01ce.
        c.Connection.LockstepClient = false;
        var frostId = f.Equip(PlayerInventories.Weapon1, "Frostblink").Single();
        var frost = Game.SupportedSkill(c, frostId);
        c.ClientSkillIds.Add(frostId);
        var plan = Game.CooldownForSkill(c, frostId);
        Assert.NotNull(plan);
        Assert.True(Game.TryBeginSkillCooldownUse(c, frostId, frost.Effect, plan,
            true, () => { }, out var use));
        Assert.True(use!.CommitNativeUse(c, frostId));
        Assert.Contains(c.SingleUseCooldowns.Values, s => s.NeedsNativeRefresh);
        c.Connection.WorldOutput.Begin(1);
        var previousArea = GameArea.Own(c.Area);
        try
        {
            Assert.True(c.Area.Owned);
            Assert.True(c.Connection.WorldOutput.Started);
            Program.ToArea(c, Game.PlayerInstantAction(c, frostId, (140, 100)), "0x01ce instant action", only: c);
            await Game.SendNativeCooldownRefresh(c);
            var records = c.Area.Delta.Take();
            Assert.Equal(2, records.Count);
            Assert.Equal(InstanceProtocol.InstantActionId, (ushort)(records[0].Bytes[0] << 8 | records[0].Bytes[1]));
            Assert.Equal(InstanceEntities.ComponentUpdateId, (ushort)(records[1].Bytes[0] << 8 | records[1].Bytes[1]));
            Assert.Equal(c.Id, records[1].Entity);
            Assert.Same(c, records[1].Only);
        }
        finally
        {
            GameArea.Own(previousArea);
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"cooldown-admission-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly ModuleLifetime rules = new("Game");
        internal readonly Player Player = new(new(1, "cooldown", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Cooldown" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        internal Fixture()
        {
            Saves.Open(path); Game.CombatLifetime = rules;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(rules));
            TestRequirements.Waive(Player);
        }
        internal ushort[] Equip(byte row, params string[] names)
        {
            string hostName = row switch
            {
                PlayerInventories.Helmet => "Iron Hat",
                PlayerInventories.Boots => "Iron Greaves",
                PlayerInventories.Gloves => "Iron Gauntlets",
                PlayerInventories.BodyArmour => "Plate Vest",
                _ => "Driftwood Wand"
            };
            var host = ItemCatalog.Create(Program.Items.Find(hostName)!);
            static Item MakeGem(string n) => Program.Items.FindGem(n) is { } found
                ? ItemCatalog.Create(found.Type) with { GemVariant = found.Variant.Index }
                : ItemCatalog.Create(Program.Items.Find(n)!);
            Game.ItemsOf(Player).Add(row, 0, 0, host with
            {
                Sockets = names.Select(n => new ItemSocket(SocketColour.White, MakeGem(n))).ToArray(),
                Links = names.Length == 2 ? [2] : Enumerable.Repeat((byte)1, names.Length).ToArray()
            });
            return Game.GemSkillEffects(Player).Where(g => names.Contains(g.Name)).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Player.Closed = true; Player.RetireOwnedCombat(); rules.Stop(_ => { });
            Game.CombatLifetime = previousRules; Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(Player.Character.Name!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
