using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CooldownAdmissionTests
{
    [Fact]
    public void Unpayable_uses_leave_actions_and_cooldowns_alone_and_blinks_share_spent_uses()
    {
        using var f = new Fixture();
        var c = f.Conn;
        var ids = f.Equip(PlayerInventories.Weapon1, "Dash", "Frostblink", "Flame Dash");
        var dash = Game.SupportedSkill(c, ids[0]);
        var frost = Game.SupportedSkill(c, ids[1]);
        var flame = Game.SupportedSkill(c, ids[2]);
        CooldownUseTicket? ticket = null;
        bool Start(ushort id, Action action)
        {
            var gem = Game.SupportedSkill(c, id);
            return Game.TryBeginSkillCooldownUse(c, id, gem.Effect,
                Game.CooldownForSkill(c, id), false, action, out ticket);
        }
        c.PlayerMana = 0;
        int action = c.ActionCount;
        Assert.False(Start(ids[0], () => { c.ActionCount++; c.Walk.Teleport(150, 100, 0); }));
        Assert.Equal(action, c.ActionCount); Assert.Equal((100, 100), c.Walk.Position(0));
        Assert.Empty(c.SingleUseCooldowns); Assert.Empty(c.SkillReadyAt);
        c.PlayerStats[Program.Stats.Key("keystone_blood_magic")!.Value] = 1;
        c.PlayerLife = 1;
        Assert.False(Start(ids[0], () => throw new Exception("Unpayable life must not start an action")));
        Assert.Empty(c.SingleUseCooldowns);
        c.PlayerStats.Remove(Program.Stats.Key("keystone_blood_magic")!.Value); c.PlayerLife = 1000; c.PlayerMana = 1000;
        Assert.True(Start(ids[0], () => { }));
        uint dashCost = ticket!.Paid!.Value.Mana;
        Assert.True(dashCost > 0);
        Assert.Equal(1000u - dashCost, c.PlayerMana);
        c.ClientSkillIds.UnionWith(ids);
        var current = Game.GemSkillEffects(c).Select(g => (g.Id, g.Effect)).ToArray();
        Assert.Null(Game.NativeCooldownRefresh(c, c.ProjectileEpoch, () => current));
        Assert.True(ticket!.CommitNativeUse(c, ids[0]));
        Assert.NotNull(Game.NativeCooldownRefresh(c, c.ProjectileEpoch, () => current));
        Assert.False(Start(ids[1], () => throw new Exception("Another blink is still cooling down")));
        Assert.True(Start(ids[2], () => { }));
        uint flameCost = ticket!.Paid!.Value.Mana;
        Assert.True(flameCost > 0);
        Assert.True(Start(ids[2], () => { }));
        Assert.False(Start(ids[2], () => throw new Exception("Dash spent the first Flame Dash use")));
        Assert.Equal(3, c.SingleUseCooldowns[flame.Effect].SpentUses);
        Assert.Equal(1, c.SingleUseCooldowns[frost.Effect].SpentUses);
        Assert.Equal(1000u - dashCost - 2 * flameCost, c.PlayerMana);
        var expired = new SingleUseCooldownState(-100, 1);
        var expiredPlan = new SingleUseCooldown(1000, 0, 3, 9);
        for (int n = 0; n < 3; n++) Assert.True(expired.TryUse(-100, expiredPlan));
        var activeDebts = c.SingleUseCooldowns.ToArray(); c.SingleUseCooldowns.Clear();
        c.SingleUseCooldowns[-123] = expired; c.CooldownGroups[-123] = 9;
        foreach (var debt in activeDebts) c.SingleUseCooldowns[debt.Key] = debt.Value;
        c.SingleUseCooldowns.Remove(frost.Effect); c.SkillReadyAt.Remove(frost.Effect);
        Assert.Equal(1, Game.CooldownStateLocked(c, frost.Effect, Game.CooldownForSkill(c, ids[1])!.Value, 0).SpentUses);

        ushort link = f.Equip(PlayerInventories.Gloves, "Intuitive Link").Single();
        c.PlayerMana = 0;
        uint manaCostKey = Program.Stats.Key("mana_cost")!.Value;
        c.PlayerStats[manaCostKey] = 20;
        Assert.False(Game.CanPaySkillCost(c, link));
        Assert.True(Start(link, () => { })); // Link costs are paid over time.
        Assert.Equal(0u, c.PlayerMana);
        c.PlayerStats.Remove(manaCostKey);
        c.PlayerMana = 1000;

        // Native group 36 means per-skill: these two unrelated skills must retain independent pools.
        var independent = f.Equip(PlayerInventories.BodyArmour, "Frost Bomb", "Cold Snap");
        Assert.True(Start(independent[0], () => { }));
        Assert.True(Start(independent[1], () => { }));
        CheckCooldownGroups(f);
    }

    static void CheckCooldownGroups(Fixture f)
    {
        var c = f.Conn;
        bool Use(ushort id) => Game.TryBeginSkillCooldownUse(c, id, Game.SupportedSkill(c, id).Effect,
            Game.CooldownForSkill(c, id), false, () => { }, out _);

        var cries = f.Equip(PlayerInventories.Helmet, "Enduring Cry", "Seismic Cry", "Rallying Cry", "Infernal Cry");
        Assert.True(Use(cries[0]));
        Assert.True(Use(cries[1]));
        c.PlayerStats[Program.Stats.Key("warcry_skills_share_cooldowns")!.Value] = 1;
        Assert.False(Use(cries[0])); // Turning sharing on cannot spread an earlier independent use.
        Assert.True(Use(cries[2]));
        Assert.False(Use(cries[3]));

        var stances = f.Equip(PlayerInventories.Boots, "Blood and Sand", "Flesh and Stone", "Convocation");
        ushort recall = stances[2];
        int recovery = Game.CooldownForSkill(c, recall)!.Value.RecoveryPercent;
        c.PlayerStats[Program.Stats.Key("convocation_cooldown_speed_+%")!.Value] = 35;
        Assert.Equal(recovery + 35, Game.CooldownForSkill(c, recall)!.Value.RecoveryPercent);

        Assert.True(Use(stances[0]));
        Assert.True(Use(stances[1]));
        var guards = f.Equip(PlayerInventories.Offhand1, "Steelskin", "Molten Shell");
        Assert.True(Use(guards[0]));
        Assert.False(Use(guards[1]));
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
        f.Conn.SingleUseCooldowns[dash.Effect] = new(0, 1); // Dash has recovered while Flame Dash still owes uses.
        Assert.True(Game.TryBeginSkillCooldownUse(f.Conn, dashId, dash.Effect,
            Game.CooldownForSkill(f.Conn, dashId), false, () => { }, out _));
        Assert.Equal(3, state.Uses); Assert.Equal(1d, state.Rate);

        foreach (var i in Game.ItemsOf(f.Conn).Items(PlayerInventories.Helmet)) Game.ItemsOf(f.Conn).Remove(PlayerInventories.Helmet, i.Id);
        Game.RefreshSingleUseCooldowns(f.Conn);
        Assert.Same(state, f.Conn.SingleUseCooldowns[original.Effect]); Assert.Equal(3, state.SpentUses);
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"cooldown-admission-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly ModuleLifetime rules = new("Game");
        internal readonly ClientState Conn = new(new(1, "cooldown", 1000, 1000, 100, 100, null, []), 1, 1)
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
                Conn.PlayerStats[Program.Stats.Key("virtual_" + attr + "_for_requirements")!.Value] = 1000;
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
