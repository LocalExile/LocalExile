using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CooldownAdmissionTests
{

    static void CheckCommittedDashSharing(Fixture f)
    {
        var c = f.Conn;
        var inventory = Game.ItemsOf(c);
        foreach (var (slot, item) in inventory.EquipmentSnapshot()) inventory.Remove(slot, item.Id);
        c.SingleUseCooldowns.Clear(); c.SkillReadyAt.Clear(); c.CooldownGroups.Clear(); c.CooldownSources.Clear();
        c.Use(new(AreaLayouts.TheCoast, "1_1_2", 1000, 1000, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        c.PlayerSent = c.PlayerObjectCreated = true;
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

    [Fact]
    public void Duplicate_active_gems_keep_stored_uses_and_do_not_reset_recharge_debt()
    {
        using var f = new Fixture();
        var ids = f.Equip(PlayerInventories.Weapon1, "Flame Dash", "Second Wind Support");
        var original = Game.SupportedSkill(f.Conn, ids[0]);
        var first = Game.CooldownFor(f.Conn, original.Effect, original.Level, original.Quality);
        Assert.NotNull(first); Assert.Equal(4, first.Value.Uses);
        Assert.True(Game.TryBeginSkillCooldownUse(f.Conn, ids[0], original.Effect, first, false, () => { }, out _));
        var state = f.Conn.SingleUseCooldowns[original.Effect];
        f.Equip(PlayerInventories.Helmet, "Flame Dash");
        Assert.Equal(2, Game.GemSkillEffects(f.Conn).Count(g => g.Effect == original.Effect));
        Assert.NotNull(Game.CooldownFor(f.Conn, original.Effect, original.Level, original.Quality));
        var copies = Game.GemSkillEffects(f.Conn).Where(g => g.Effect == original.Effect).ToArray();
        Assert.Equal(4, Game.CooldownForSkill(f.Conn, ids[0])!.Value.Uses);
        Assert.Equal(3, Game.CooldownForSkill(f.Conn, copies.Single(g => g.Id != ids[0]).Id)!.Value.Uses);
        Game.RefreshSingleUseCooldowns(f.Conn);
        Assert.Same(state, f.Conn.SingleUseCooldowns[original.Effect]);
        Assert.Equal(1, state.SpentUses);
        ushort second = copies.Single(g => g.Id != ids[0]).Id;
        Assert.True(Game.TryBeginSkillCooldownUse(f.Conn, second, original.Effect,
            Game.CooldownForSkill(f.Conn, second), false, () => { }, out _));
        var dashId = f.Equip(PlayerInventories.BodyArmour, "Dash").Single();
        var dash = Game.SupportedSkill(f.Conn, dashId);
        // Dash's native aliases use one normalized key; Flame Dash still owes its independent stored uses.
        f.Conn.SingleUseCooldowns[Game.CooldownKey(f.Conn, dash.Effect, dashId)] = new(0, 1);
        Assert.True(Game.TryBeginSkillCooldownUse(f.Conn, dashId, dash.Effect,
            Game.CooldownForSkill(f.Conn, dashId), false, () => { }, out _));
        Assert.Equal(3, state.Uses); Assert.Equal(1d, state.Rate);

        foreach (var i in Game.ItemsOf(f.Conn).Items(PlayerInventories.Helmet)) Game.ItemsOf(f.Conn).Remove(PlayerInventories.Helmet, i.Id);
        Game.RefreshSingleUseCooldowns(f.Conn);
        Assert.Same(state, f.Conn.SingleUseCooldowns[original.Effect]); Assert.Equal(3, state.SpentUses);
    }

    [Fact]
    public void Frostblink_crosses_unwalkable_gaps_but_keeps_endpoint_and_quest_barrier_checks()
    {
        using var f = new Fixture();
        var c = f.Conn;
        c.PlayerSent = true;
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

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"cooldown-admission-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly ModuleLifetime rules = new("Game");
        internal readonly Player Conn = new(new(1, "cooldown", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "Cooldown" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        internal Fixture()
        {
            Saves.Open(path); Game.CombatLifetime = rules;
            foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(rules));
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
            Game.ItemsOf(Conn).Add(row, 0, 0, host with
            {
                Sockets = names.Select(n => new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(n)!))).ToArray(),
                Links = names.Length == 2 ? [2] : Enumerable.Repeat((byte)1, names.Length).ToArray()
            });
            return Game.GemSkillEffects(Conn).Where(g => names.Contains(g.Name)).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Conn.Closed = true; Conn.RetireOwnedCombat(); rules.Stop(_ => { });
            Game.CombatLifetime = previousRules; Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(Conn.Character!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
