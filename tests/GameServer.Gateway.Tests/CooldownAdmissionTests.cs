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
        CheckCommittedDashSharing(f);
        CheckTravelChargeRecovery(f);
    }

    static void CheckTravelChargeRecovery(Fixture f)
    {
        var c = f.Conn;
        c.Clock.Stop();
        var inventory = Game.ItemsOf(c);
        foreach (var (slot, item) in inventory.EquipmentSnapshot()) inventory.Remove(slot, item.Id);
        c.SingleUseCooldowns.Clear(); c.SkillReadyAt.Clear(); c.CooldownGroups.Clear(); c.CooldownSources.Clear();
        c.PlayerStats.Remove(Program.Stats.Key("convocation_cooldown_speed_+%")!.Value);
        var travel = f.Equip(PlayerInventories.Weapon1, "Dash", "Frostblink");
        var linked = f.Equip(PlayerInventories.Helmet, "Flame Dash", "Second Wind Support");
        ushort bomb = f.Equip(PlayerInventories.BodyArmour, "Frost Bomb").Single();
        ushort dash = travel.Single(id => Game.SkillsFor(c).EffectId(Game.SupportedSkill(c, id).Effect) == "Dash");
        ushort frost = travel.Single(id => id != dash);
        var ids = travel.Concat(linked).ToArray();
        var charges = Game.ChargesOf(c);
        Dictionary<uint, int> stats = [];
        void Count(int count)
        {
            stats = Game.CharacterPlayerStats(c);
            charges.Admit(Game.ResourceEffectsTime(c), [new ChargeEvent.Set(ChargeType.Frenzy, count)],
                name => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0,
                () => Game.WeaponProfileFor(c), true, new Random(1));
            stats = Game.CharacterPlayerStats(c);
        }
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        SingleUseCooldown Plan(ushort id) => Game.CooldownForSkill(c, id)!.Value;
        int Published(ushort id) => Game.PlayerSkills(c).Single(s => s.Id == id).Stats!
            .Single(s => s.Key == Program.Stats.Key("virtual_cooldown_speed_+%")!.Value).Value;
        Count(0);
        var ordinary = ids.ToDictionary(id => id, id => Plan(id));
        int bombRecovery = Plan(bomb).RecoveryPercent;
        foreach (string name in new[] { "Badge of the Brotherhood", "Replica Badge of the Brotherhood" })
        {
            var source = inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create(name, new Random(504))!);
            Count(0);
            int coefficient = Get("travel_skills_cooldown_speed_+%_per_frenzy_charge");
            Assert.InRange(coefficient, 7, 10);
            foreach (int count in new[] { 0, 1, 3, 0 })
            {
                Count(count); Assert.Equal(count, Get("current_frenzy_charges"));
                foreach (ushort id in ids)
                {
                    var plan = Plan(id);
                    Assert.Equal(ordinary[id].RecoveryPercent + count * coefficient, plan.RecoveryPercent);
                    Assert.Equal(plan.RecoveryPercent, Published(id));
                    Assert.Equal(ordinary[id].BaseMilliseconds, plan.BaseMilliseconds);
                    Assert.Equal(ordinary[id].Uses, plan.Uses);
                    Assert.Equal(9, plan.SharedGroup);
                }
                Assert.Equal(bombRecovery, Plan(bomb).RecoveryPercent);
            }
            uint minimum = Program.Stats.Key("base_minimum_frenzy_charges")!.Value;
            c.PlayerStats[minimum] = 2; Count(0);
            Assert.Equal(2, Get("current_frenzy_charges"));
            Assert.Equal(ordinary[dash].RecoveryPercent + 2 * coefficient, Plan(dash).RecoveryPercent);
            c.PlayerStats.Remove(minimum); Count(3);
            var converted = inventory.Add(PlayerInventories.Belt, 0, 0,
                new UniqueCatalog(Program.Items).Create("Olesya's Delight", new Random(504))!);
            stats = Game.CharacterPlayerStats(c);
            Assert.Equal(0, Get("current_frenzy_charges")); Assert.Equal(3, Get("current_affliction_charges"));
            Assert.Equal(ordinary[dash].RecoveryPercent, Plan(dash).RecoveryPercent);
            inventory.Remove(PlayerInventories.Belt, converted.Id); Count(3);

            c.PlayerMana = 1000;
            c.Clock.Start();
            Assert.True(Game.TryBeginSkillCooldownUse(c, dash, Game.SupportedSkill(c, dash).Effect,
                Plan(dash), false, () => { }, out var ticket), $"{name}: life={c.PlayerLife} paused={c.Paused} "
                    + $"pay={Game.CanPaySkillCost(c, dash)} disabled={Game.ActorSkillInstanceDisabled(c, Game.SupportedSkill(c, dash).Effect, dash)} "
                    + $"pools={c.SingleUseCooldowns.Count} plan={Plan(dash)}");
            Assert.True(ticket!.CommitNativeUse(c, dash));
            c.Clock.Stop();
            var debt = c.SingleUseCooldowns[Game.CooldownKey(c, Game.SupportedSkill(c, dash).Effect, dash)];
            var frostDebt = c.SingleUseCooldowns[Game.SupportedSkill(c, frost).Effect];
            Assert.Equal(Plan(dash).RecoveryRate, debt.Rate);
            Assert.Equal(Plan(frost).RecoveryRate, frostDebt.Rate);
            var elapsed = new SingleUseCooldownState(0, debt.Rate);
            Assert.True(elapsed.TryUse(0, Plan(dash)));
            elapsed.Refresh(.5, ordinary[dash].RecoveryRate);
            double expectedRemaining = Plan(dash).BaseMilliseconds - 500 * Plan(dash).RecoveryRate;
            Assert.Equal(expectedRemaining, elapsed.RemainingBaseMilliseconds, 6);
            elapsed.Refresh(1, Plan(dash).RecoveryRate);
            Assert.Equal(expectedRemaining - 500 * ordinary[dash].RecoveryRate, elapsed.RemainingBaseMilliseconds, 6);
            debt.Refresh(c.Clock.Elapsed.TotalSeconds, debt.Rate);
            double remaining = debt.RemainingBaseMilliseconds;
            Count(0); Game.RefreshSingleUseCooldowns(c);
            Assert.Same(debt, c.SingleUseCooldowns[Game.CooldownKey(c, Game.SupportedSkill(c, dash).Effect, dash)]);
            Assert.Equal(remaining, debt.RemainingBaseMilliseconds, 6);
            Assert.Equal(ordinary[dash].RecoveryRate, debt.Rate); Assert.Equal(1, debt.SpentUses);
            Count(3); Game.RefreshSingleUseCooldowns(c);
            Assert.Equal(ordinary[dash].RecoveryRate + .03 * coefficient, debt.Rate, 6);
            Assert.Equal(remaining, debt.RemainingBaseMilliseconds, 6);
            Assert.True(debt.NeedsNativeRefresh);
            c.Experience = CharacterStats.ExperienceFor(1); c.Pools = Game.PoolsFor(c, 1);
            stats = Game.CharacterPlayerStats(c);
            Assert.True(Game.IsInactive(c, source.Id));
            Assert.Equal(ordinary[dash].RecoveryPercent, Plan(dash).RecoveryPercent);
            c.Experience = CharacterStats.ExperienceFor(100); c.Pools = Game.PoolsFor(c, 100); Count(3);
            Assert.Equal(ordinary[dash].RecoveryPercent + 3 * coefficient, Plan(dash).RecoveryPercent);
            inventory.Remove(5, source.Id); stats = Game.CharacterPlayerStats(c);
            Game.RefreshSingleUseCooldowns(c);
            Assert.Equal(ordinary[dash].RecoveryRate, debt.Rate);
            Assert.Equal(remaining, debt.RemainingBaseMilliseconds, 6); Assert.Equal(1, debt.SpentUses);
            c.SingleUseCooldowns.Clear(); c.SkillReadyAt.Clear(); c.CooldownGroups.Clear(); c.CooldownSources.Clear();
            charges.Die();
        }
    }

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

    static void CheckCooldownGroups(Fixture f)
    {
        var c = f.Conn;
        bool Use(ushort id) => Game.TryBeginSkillCooldownUse(c, id, Game.SupportedSkill(c, id).Effect,
            Game.CooldownForSkill(c, id), false, () => { }, out _);

        uint groupKey = Program.Stats.Key("skill_cooldown_group_override")!.Value;
        int NativeGroup(ushort id) => Game.PlayerSkills(c).Single(skill => skill.Id == id)
            .Stats!.Single(stat => stat.Key == groupKey).Value;
        var cries = f.Equip(PlayerInventories.Helmet, "Enduring Cry", "Seismic Cry", "Rallying Cry", "Infernal Cry");
        Assert.Equal(36, NativeGroup(cries[0]));
        Assert.True(Use(cries[0]));
        Assert.True(Use(cries[1]));
        c.PlayerStats[Program.Stats.Key("warcry_skills_share_cooldowns")!.Value] = 1;
        Assert.Equal(1, NativeGroup(cries[0]));
        Assert.False(Use(cries[0])); // Turning sharing on cannot spread an earlier independent use.
        Assert.True(Use(cries[2]));
        Assert.False(Use(cries[3]));

        var stances = f.Equip(PlayerInventories.Boots, "Blood and Sand", "Flesh and Stone", "Convocation");
        ushort recall = stances[2];
        int recovery = Game.CooldownForSkill(c, recall)!.Value.RecoveryPercent;
        c.PlayerStats[Program.Stats.Key("convocation_cooldown_speed_+%")!.Value] = 35;
        Assert.Equal(recovery + 35, Game.CooldownForSkill(c, recall)!.Value.RecoveryPercent);

        Assert.Equal(36, NativeGroup(stances[0]));
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
        // Dash's native aliases use one normalized key; Flame Dash still owes its independent stored uses.
        f.Conn.SingleUseCooldowns[Game.CooldownKey(f.Conn, dash.Effect, dashId)] = new(0, 1);
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
