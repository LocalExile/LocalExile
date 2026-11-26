using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SkillAdmissionCostTests
{
    [Fact]
    public async Task Voidbringer_applies_after_use_and_leaves_mana_unchanged_when_turning_an_aura_off()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        using var clock = new AdmissionClock(f);
        f.Equip(("Clarity", 1), ("Ice Nova", 1));
        var gems = Game.GemSkillEffects(player);
        var clarity = gems.Single(g => g.Name == "Clarity");
        var nova = gems.Single(g => g.Name == "Ice Nova");
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.LoseXManaWhenYouUseSkill] = 69;
                values[Stat.SkillManaCostPlus] = -100;
            }
            return values;
        });
        Game.RecomputePoolsAndSpeed(player);
        player.Walk.Teleport(100, 100, Game.AreaTime(player));
        async Task Use(ushort skill, int effect)
        {
            clock.At += 2;
            Assert.True(Game.TryBeginSkillCooldownUse(player, skill, effect, Game.CooldownForSkill(player, skill),
                false, () => player.ActionCount++, out var ticket));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (100, 100), cooldownUse: ticket));
        }

        player.PlayerMana = player.Pools.Mana;
        await Use(clarity.Id, clarity.Effect);
        Assert.True(player.Auras.ContainsKey(clarity.Id));
        uint manaWhileReserved = player.PlayerMana;
        await Use(clarity.Id, clarity.Effect);
        Assert.False(player.Auras.ContainsKey(clarity.Id));
        Assert.Equal(manaWhileReserved, player.PlayerMana);

        Assert.Equal(0, Game.PlayerSkills(player).Single(s => s.Id == nova.Id).Stats!
            .FirstOrDefault(s => s.Key == Stat.ManaCost).Value);
        foreach (uint before in new[] { player.Pools.Mana, player.Pools.Mana / 2 + 1 })
        {
            Assert.True(before > 69);
            player.PlayerMana = before;
            player.AuraStatsDirty = false;
            await Use(nova.Id, nova.Effect);
            Assert.Equal(before - 69, player.PlayerMana);
            Assert.True(player.AuraStatsDirty);
        }
    }

    sealed class AdmissionClock : IPlayerUpdateSchedule, IDisposable
    {
        readonly PlayerUpdatesState? previous = Game.AreaUpdateContext.Value;
        public double At { get; set; } = 1;

        public AdmissionClock(MinionUtilityTests.Fixture f) => Game.AreaUpdateContext.Value = new(f.Lifetime, this);
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
        public void Dispose() => Game.AreaUpdateContext.Value = previous;
    }

    [Fact]
    public void Skill_cost_admission_and_wire_costs_apply_efficiency_deathmarked_cannibalistic_rite_covenant_and_rathpith()
    {
        using var f = new Fixture();
        var player = f.Player;

        ushort fireball = f.Equip(PlayerInventories.Weapon1, "Metadata/Items/Gems/SkillGemFireball").Single();
        ushort vulnerability = f.Equip(PlayerInventories.Helmet, "Metadata/Items/Gems/SkillGemNewVulnerability").Single();
        ushort mark = f.Equip(PlayerInventories.Gloves, "Metadata/Items/Gems/SkillGemCriticalWeakness").Single();
        ushort heavyStrike = f.Equip(PlayerInventories.Boots, "Metadata/Items/Gems/SkillGemHeavyStrike").Single();

        // 1. Spell and Curse Mana cost efficiency (both server admission and wire Skills packet).
        int fireballBase = f.WireStat(fireball, Stat.ManaCost);
        int vulnBase = f.WireStat(vulnerability, Stat.ManaCost);
        int heavyBase = f.WireStat(heavyStrike, Stat.ManaCost);
        Assert.True(fireballBase > 0 && vulnBase > 0 && heavyBase > 0);

        f.SetStats((Stat.BaseSpellManaCostEfficiencyPlusPct, 100));
        int fireballHalf = (int)decimal.Round(fireballBase * 100m / 200m, 0, MidpointRounding.AwayFromZero);
        int vulnHalf = (int)decimal.Round(vulnBase * 100m / 200m, 0, MidpointRounding.AwayFromZero);
        Assert.Equal(fireballHalf, f.WireStat(fireball, Stat.ManaCost));
        Assert.Equal(vulnHalf, f.WireStat(vulnerability, Stat.ManaCost));
        Assert.Equal(heavyBase, f.WireStat(heavyStrike, Stat.ManaCost));
        player.PlayerMana = 1000;
        Assert.True(Game.PayMana(player, fireball, out uint paidMana, out uint paidLife));
        Assert.Equal((uint)fireballHalf, paidMana);
        Assert.Equal(0u, paidLife);

        f.SetStats((Stat.BaseSpellManaCostEfficiencyPlusPct, 100), (Stat.BaseManaCostEfficiencyPlusPctOfCurseSkills, 100));
        int vulnThird = (int)decimal.Round(vulnBase * 100m / 300m, 0, MidpointRounding.AwayFromZero);
        Assert.Equal(vulnThird, f.WireStat(vulnerability, Stat.ManaCost));
        Assert.Equal(fireballHalf, f.WireStat(fireball, Stat.ManaCost));
        player.PlayerMana = 1000;
        Assert.True(Game.PayMana(player, vulnerability, out paidMana, out paidLife));
        Assert.Equal((uint)vulnThird, paidMana);
        Assert.Equal(0u, paidLife);

        // 2. Deathmarked (Mark Skills Cost no Mana): zero wire ManaCost, admitted at 0 Mana, 0 Petrified Blood Life.
        f.SetStats((Stat.MarkSkillsCostNoMana, 1));
        player.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 7, now: 0);
        player.PlayerLife = 1000;
        player.PlayerMana = 0;
        Assert.Equal(0, f.WireStat(mark, Stat.ManaCost));
        Assert.True(Game.CanPaySkillCost(player, mark));
        Assert.True(Game.PayMana(player, mark, out paidMana, out paidLife));
        Assert.Equal(0u, paidMana);
        Assert.Equal(0u, paidLife);
        Assert.Equal(1000u, player.PlayerLife);
        Assert.True(f.WireStat(vulnerability, Stat.ManaCost) > 0);
        Assert.False(Game.CanPaySkillCost(player, vulnerability));
        player.PetrifiedBlood.Suspend();

        // 3. Cannibalistic Rite: attack Life/Mana split (conservation, negative flat -> 0 Life, efficiency, PB order, pool preservation).
        // 3a. Effective cost = heavyBase + 1 (= 7), split 30% -> 2 Life + 5 Mana = 7 (zero rounding loss), Spell unaffected.
        int targetOddCost = heavyBase + 1;
        int expectedLife = (int)decimal.Round(targetOddCost * 30m / 100m, 0, MidpointRounding.AwayFromZero);
        int expectedMana = targetOddCost - expectedLife;
        f.SetStats((Stat.BaseAttackSkillCostLifeInsteadOfManaPct, 30), (Stat.SkillManaCostPlus, 1));
        Assert.Equal(expectedMana, f.WireStat(heavyStrike, Stat.ManaCost));
        Assert.Equal(expectedLife, f.WireStat(heavyStrike, Stat.LifeCost));
        Assert.Equal(0, f.WireStat(fireball, Stat.LifeCost));
        player.PlayerMana = 1000;
        player.PlayerLife = 1000;
        Assert.True(Game.PayMana(player, heavyStrike, out paidMana, out paidLife));
        Assert.Equal((uint)expectedMana, paidMana);
        Assert.Equal((uint)expectedLife, paidLife);

        // 3b. Insufficient Mana or Life preserves both pools.
        player.PlayerMana = (uint)(expectedMana - 1);
        player.PlayerLife = 100;
        Assert.False(Game.PayMana(player, heavyStrike, out _, out _));
        Assert.Equal((uint)(expectedMana - 1), player.PlayerMana);
        Assert.Equal(100u, player.PlayerLife);
        player.PlayerMana = 100;
        player.PlayerLife = (uint)expectedLife;
        Assert.False(Game.PayMana(player, heavyStrike, out _, out _));
        Assert.Equal(100u, player.PlayerMana);
        Assert.Equal((uint)expectedLife, player.PlayerLife);

        // 3c. Negative flat Mana cost reducing total cost to 0 charges 0 Life and 0 Mana (admitted + wire).
        f.SetStats((Stat.BaseAttackSkillCostLifeInsteadOfManaPct, 30), (Stat.SkillManaCostPlus, -(heavyBase + 1)));
        Assert.Equal(0, f.WireStat(heavyStrike, Stat.ManaCost));
        Assert.Equal(0, f.WireStat(heavyStrike, Stat.LifeCost));
        player.PlayerMana = 0;
        player.PlayerLife = 10;
        Assert.True(Game.PayMana(player, heavyStrike, out paidMana, out paidLife));
        Assert.Equal(0u, paidMana);
        Assert.Equal(0u, paidLife);
        Assert.Equal(10u, player.PlayerLife);

        // 3d. Petrified Blood applies AFTER Cannibalistic Rite split to the remaining Mana share, not the unsplit total.
        int flatDelta = 10 - heavyBase;
        f.SetStats((Stat.BaseAttackSkillCostLifeInsteadOfManaPct, 30), (Stat.SkillManaCostPlus, flatDelta));
        player.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
            manaPercent: 3500, lowLifeThresholdPct: 50, buff: 8, now: 0);
        player.PlayerMana = 1000;
        player.PlayerLife = 1000;
        Assert.Equal(7, f.WireStat(heavyStrike, Stat.ManaCost));
        Assert.Equal(3, f.WireStat(heavyStrike, Stat.LifeCost));
        Assert.True(Game.PayMana(player, heavyStrike, out paidMana, out paidLife));
        Assert.Equal(7u, paidMana);
        Assert.Equal(6u, paidLife); // 3 (Cannibalistic Rite) + Round(7 * 0.4) = 3 (Petrified Blood), not 3 + 4 = 7.
        player.PetrifiedBlood.Suspend();

        // 3e. Mana efficiency applies to the remaining Mana share only, never the converted Life share.
        f.SetStats((Stat.BaseAttackSkillCostLifeInsteadOfManaPct, 30), (Stat.ManaCostPlusPct, 100), (ItemChargeCosts.ManaEfficiency, 100));
        int doubledBase = heavyBase * 2;
        int effLife = (int)decimal.Round(doubledBase * 30m / 100m, 0, MidpointRounding.AwayFromZero);
        int effMana = (int)decimal.Round((doubledBase - effLife) * 100m / 200m, 0, MidpointRounding.AwayFromZero);
        Assert.Equal(effMana, f.WireStat(heavyStrike, Stat.ManaCost));
        Assert.Equal(effLife, f.WireStat(heavyStrike, Stat.LifeCost));
        player.PlayerMana = 1000;
        player.PlayerLife = 1000;
        Assert.True(Game.PayMana(player, heavyStrike, out paidMana, out paidLife));
        Assert.Equal((uint)effMana, paidMana);
        Assert.Equal((uint)effLife, paidLife);

        // 4. The Covenant added Life cost (refusal preserves Mana; Blood Magic makes Covenant a no-op).
        f.SetStats((Stat.BaseSkillGainLifeCostPctOfManaCost, 100));
        Assert.Equal(fireballBase, f.WireStat(fireball, Stat.ManaCost));
        Assert.Equal(fireballBase, f.WireStat(fireball, Stat.LifeCost));
        player.PlayerMana = 1000;
        player.PlayerLife = (uint)fireballBase;
        Assert.False(Game.PayMana(player, fireball, out _, out _));
        Assert.Equal(1000u, player.PlayerMana);
        Assert.Equal((uint)fireballBase, player.PlayerLife);
        player.PlayerLife = 1000;
        Assert.True(Game.PayMana(player, fireball, out paidMana, out paidLife));
        Assert.Equal((uint)fireballBase, paidMana);
        Assert.Equal((uint)fireballBase, paidLife);

        f.SetStats((Stat.BaseSkillGainLifeCostPctOfManaCost, 100), (Stat.KeystoneBloodMagic, 1));
        Assert.Equal(0, f.WireStat(fireball, Stat.ManaCost));
        Assert.Equal(fireballBase, f.WireStat(fireball, Stat.LifeCost));
        player.PlayerMana = 1000;
        player.PlayerLife = 1000;
        Assert.True(Game.PayMana(player, fireball, out paidMana, out paidLife));
        Assert.Equal(0u, paidMana);
        Assert.Equal((uint)fireballBase, paidLife);

        // 5. Rathpith Globe: sacrifices 10% current Life once per Spell use, skipped for attacks and useSpell: false (channel tick / Brand Recall).
        f.SetStats((Stat.SacrificePctLifeOnSpellSkill, 10));
        player.PlayerMana = 1000;
        player.PlayerLife = 1000;
        Assert.True(Game.PayMana(player, fireball, out paidMana, out paidLife));
        Assert.Equal((uint)fireballBase, paidMana);
        Assert.Equal(100u, paidLife);
        Assert.Equal(900u, player.PlayerLife);

        // Channel tick / Brand Recall activation (useSpell: false) pays cost without re-sacrificing Life.
        Assert.True(Game.PayMana(player, fireball, out paidMana, out paidLife, costPercent: 100, useSpell: false));
        Assert.Equal((uint)fireballBase, paidMana);
        Assert.Equal(0u, paidLife);
        Assert.Equal(900u, player.PlayerLife);

        Assert.True(Game.PayMana(player, fireball, out paidMana, out paidLife, costPercent: 50, useSpell: false));
        Assert.Equal((uint)(fireballBase / 2), paidMana);
        Assert.Equal(0u, paidLife);
        Assert.Equal(900u, player.PlayerLife);

        // Attacks never trigger Rathpith Globe's Spell life sacrifice.
        Assert.True(Game.PayMana(player, heavyStrike, out paidMana, out paidLife));
        Assert.Equal((uint)heavyBase, paidMana);
        Assert.Equal(0u, paidLife);
        Assert.Equal(900u, player.PlayerLife);
    }

    [Fact]
    public void Movement_and_stance_cooldown_recovery_speed_scales_cooldown_duration()
    {
        using var f = new Fixture();
        var player = f.Player;
        var skills = Game.SkillsFor(player);

        ushort flameDash = f.Equip(PlayerInventories.Weapon1, "Metadata/Items/Gems/SkillGemFlameDash").Single();
        ushort bloodAndSand = f.Equip(PlayerInventories.Helmet, "Metadata/Items/Gems/SkillGemBloodAndSand").Single();
        ushort fleshAndStone = f.Equip(PlayerInventories.Gloves, "Metadata/Items/Gems/SkillGemFleshAndStone").Single();

        var dashGem = Game.SupportedSkill(player, flameDash);
        var bloodGem = Game.SupportedSkill(player, bloodAndSand);
        var fleshGem = Game.SupportedSkill(player, fleshAndStone);

        Assert.True(skills.IsMovement(dashGem.Effect));
        Assert.True(skills.IsTravel(dashGem.Effect));
        Assert.True(skills.HasType(bloodGem.Effect, "Stance"));
        Assert.True(skills.HasType(fleshGem.Effect, "Stance"));

        // Baseline: 0% recovery speed.
        Assert.Equal(0, Game.CooldownForSkill(player, flameDash)!.Value.RecoveryPercent);
        Assert.Equal(0, f.WireStat(flameDash, Stat.VirtualCooldownSpeedPlusPct));
        Assert.Equal(0, Game.CooldownForSkill(player, bloodAndSand)!.Value.RecoveryPercent);
        Assert.Equal(0, f.WireStat(bloodAndSand, Stat.VirtualCooldownSpeedPlusPct));
        Assert.Equal(0, Game.CooldownForSkill(player, fleshAndStone)!.Value.RecoveryPercent);
        Assert.Equal(0, f.WireStat(fleshAndStone, Stat.VirtualCooldownSpeedPlusPct));

        // Movement cooldown recovery speed scales Flame Dash and stacks with Travel cooldown recovery speed.
        f.SetStats((Stat.MovementSkillsCooldownSpeedPlusPct, 50));
        var directDash = SingleUseCooldown.ForSkill(skills, Program.Stats, dashGem.Effect, dashGem.Level,
            new Dictionary<uint, int> { [Stat.MovementSkillsCooldownSpeedPlusPct] = 50 }, dashGem.Stats)!.Value;
        Assert.Equal(50, directDash.RecoveryPercent);
        Assert.Equal(1.5, directDash.RecoveryRate, 6);
        Assert.Equal(50, Game.CooldownForSkill(player, flameDash)!.Value.RecoveryPercent);
        Assert.Equal(50, f.WireStat(flameDash, Stat.VirtualCooldownSpeedPlusPct));
        Assert.Equal(0, Game.CooldownForSkill(player, bloodAndSand)!.Value.RecoveryPercent);

        f.SetStats((Stat.MovementSkillsCooldownSpeedPlusPct, 50), (Stat.TravelSkillCooldownSpeedPlusPct, 30));
        var stackedDash = Game.CooldownForSkill(player, flameDash)!.Value;
        Assert.Equal(80, stackedDash.RecoveryPercent);
        Assert.Equal(1.8, stackedDash.RecoveryRate, 6);
        Assert.Equal(80, f.WireStat(flameDash, Stat.VirtualCooldownSpeedPlusPct));
        var dashState = new SingleUseCooldownState(0, stackedDash.RecoveryRate);
        for (int i = 0; i < stackedDash.Uses; i++) Assert.True(dashState.TryUse(0, stackedDash));
        Assert.Equal(stackedDash.BaseMilliseconds / 1800.0, dashState.RemainingSeconds, 6);

        // Stance cooldown recovery speed scales both Blood and Sand and Flesh and Stone (SingleUseCooldown.ForSkill + wire).
        f.SetStats((Stat.StanceSkillCooldownSpeedPlusPct, 100));
        foreach (var (skillId, gem) in new[] { (bloodAndSand, bloodGem), (fleshAndStone, fleshGem) })
        {
            var direct = SingleUseCooldown.ForSkill(skills, Program.Stats, gem.Effect, gem.Level,
                new Dictionary<uint, int> { [Stat.StanceSkillCooldownSpeedPlusPct] = 100 }, gem.Stats)!.Value;
            Assert.Equal(100, direct.RecoveryPercent);
            Assert.Equal(2.0, direct.RecoveryRate, 6);
            var plan = Game.CooldownForSkill(player, skillId)!.Value;
            Assert.Equal(100, plan.RecoveryPercent);
            Assert.Equal(100, f.WireStat(skillId, Stat.VirtualCooldownSpeedPlusPct));
            var state = new SingleUseCooldownState(0, plan.RecoveryRate);
            Assert.True(state.TryUse(0, plan));
            Assert.Equal(plan.BaseMilliseconds / 2000.0, state.RemainingSeconds, 6);
        }
        Assert.Equal(0, Game.CooldownForSkill(player, flameDash)!.Value.RecoveryPercent);
        Assert.Equal(0, f.WireStat(flameDash, Stat.VirtualCooldownSpeedPlusPct));
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"skill-admission-cost-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly ModuleLifetime rules = new("Game");
        readonly IDisposable statSources;
        readonly Dictionary<uint, int> overrides = [];

        internal readonly Player Player = new(new(1, "skillcost", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "SkillCost" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };

        internal Fixture()
        {
            Saves.Open(path);
            Game.CombatLifetime = rules;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(rules));
            TestRequirements.Waive(Player);
            statSources = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, Player) || overrides.Count == 0) return sources;
                var copy = new Dictionary<uint, int>(sources);
                foreach (var (key, value) in overrides) copy[key] = value;
                return copy;
            });
        }

        internal void SetStats(params (uint Stat, int Value)[] entries)
        {
            overrides.Clear();
            foreach (var (stat, value) in entries) overrides[stat] = value;
            Player.PlayerSkillsCache = null;
        }

        internal int WireStat(ushort skillId, uint stat) =>
            Game.PlayerSkills(Player).Single(s => s.Id == skillId).Stats!.FirstOrDefault(kv => kv.Key == stat).Value;

        internal ushort[] Equip(byte row, params string[] metadataIds)
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
            var existing = Game.GemSkillEffects(Player).Select(g => g.Id).ToHashSet();
            Game.ItemsOf(Player).Add(row, 0, 0, host with
            {
                Sockets = metadataIds.Select(id => new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(id)!))).ToArray(),
                Links = Enumerable.Repeat((byte)1, metadataIds.Length).ToArray()
            });
            Player.PlayerSkillsCache = null;
            return Game.GemSkillEffects(Player).Where(g => !existing.Contains(g.Id)).Select(g => g.Id).ToArray();
        }

        public void Dispose()
        {
            statSources.Dispose();
            Player.Closed = true;
            Player.RetireOwnedCombat();
            rules.Stop(_ => { });
            Game.CombatLifetime = previousRules;
            Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(Player.Character.Name!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
