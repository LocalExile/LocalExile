using GameServer.Core;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BlockLuckTests
{
    static void WithTestCharacter(Action<Player> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"block-luck-{Guid.NewGuid():N}.db");
        var oldLogic = Program.GameLogicOrNull;
        var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var player = new Player(new(1, "blockluck", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character =
            {
                Name = "Hero" + Guid.NewGuid().ToString("N")[..8],
                ClassRow = 0,
                Experience = CharacterStats.LevelStarts[80],
            },
            PlayerLife = 3000,
            PlayerMana = 2000,
            Connection = { PlayerSent = true },
            Instance = new(),
            Pools = new(80, 3000, 2000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        player.Clock.Stop();
        TestRequirements.Waive(player);
        IDisposable? reqHook = null;
        try
        {
            Saves.Open(path);
            reqHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (ReferenceEquals(p, player))
                {
                    s[Stat.VirtualStrengthForRequirements] = 1000;
                    s[Stat.VirtualDexterityForRequirements] = 1000;
                    s[Stat.VirtualIntelligenceForRequirements] = 1000;
                }
                return s;
            });
            body(player);
        }
        finally
        {
            reqHook?.Dispose();
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = oldLifetime;
            Program.SetGameLogic(oldLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Svalinn_and_Gladiator_More_Than_Skill_grant_lucky_block_panel_stats()
    {
        var uniques = new UniqueCatalog(Program.Items);
        var svalinn = uniques.Create("Svalinn", new Random(17));
        Assert.NotNull(svalinn);

        WithTestCharacter(player =>
        {
            Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0, svalinn!);
            player.MarkStatsDirty(StatInput.Items);
            player.Pools = Game.PoolsFor(player, 80);

            Assert.True(player.Pools.AttackBlockLucky);
            Assert.True(player.Pools.SpellBlockLucky);
            Assert.Equal(65, player.Pools.BlockChanceMax);
            Assert.Equal(65, player.Pools.SpellBlockChanceMax);
            Assert.True(player.Pools.Ward >= 100);

            var panelAttrs = Game.ClassAttributes(player.Character.ClassRow, player.Pools).ToDictionary(p => p.Item1, p => p.Item2);
            Assert.Equal(1, panelAttrs[Stat.AttackBlockLuck]);
            Assert.Equal(1, panelAttrs[Stat.SpellBlockLuck]);

            // Unlucky emits -1; simultaneous lucky and unlucky cancel to 0 matching DamageModel.EffectiveBlockChance.
            var unluckyAttrs = Game.ClassAttributes(player.Character.ClassRow,
                player.Pools with { AttackBlockLucky = false, AttackBlockUnlucky = true, SpellBlockLucky = true, SpellBlockUnlucky = true })
                .ToDictionary(p => p.Item1, p => p.Item2);
            Assert.Equal(-1, unluckyAttrs[Stat.AttackBlockLuck]);
            Assert.Equal(0, unluckyAttrs[Stat.SpellBlockLuck]);
        });

        // Gladiator "More Than Skill" (PassiveSkillGraphId 2598): end-to-end PoolsFor + ClassAttributes before/after blocking.
        WithTestCharacter(player =>
        {
            player.Character.Passives.Add(2598);
            player.MarkStatsDirty(StatInput.Passives);
            player.Pools = Game.PoolsFor(player, 80);
            Assert.False(player.Pools.AttackBlockLucky);
            Assert.False(player.Pools.SpellBlockLucky);

            player.RecentEvents.Record(StatConditions.Blocked, Game.AreaTime(player));
            player.MarkStatsDirty(StatInput.Passives);
            player.Pools = Game.PoolsFor(player, 80);
            Assert.True(player.Pools.AttackBlockLucky);
            Assert.True(player.Pools.SpellBlockLucky);
            var attrs = Game.ClassAttributes(player.Character.ClassRow, player.Pools).ToDictionary(p => p.Item1, p => p.Item2);
            Assert.Equal(1, attrs[Stat.AttackBlockLuck]);
            Assert.Equal(1, attrs[Stat.SpellBlockLuck]);
        });

        // StatConditions unit verification for spell_and_attack_block_chance_is_lucky_if_blocked_recently.
        bool Exists(string name) => Program.Stats.Key(name) is not null;
        var recentEmpty = new StatConditions.Subject(Recent: new HashSet<string>());
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("spell_and_attack_block_chance_is_lucky_if_blocked_recently", recentEmpty));

        var recentBlocked = new StatConditions.Subject(Recent: new HashSet<string>([StatConditions.Blocked]));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("spell_and_attack_block_chance_is_lucky_if_blocked_recently", recentBlocked));
        StatConditions.Counted("spell_and_attack_block_chance_is_lucky_if_blocked_recently", 1,
            StatConditions.State.Holds, recentBlocked, Exists, out var targets);
        Assert.Equal(["base_block_luck"], targets);
    }

    [Fact]
    public void Aegis_Aurora_recovers_energy_shield_from_armour_on_block()
    {
        var player = new Player(new(1, "aegisblock", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            PlayerLife = 3000,
            Pools = new(80, 3000, 1000, 2000, 0, Armour: 15_000),
        };
        player.EnergyShield.SetMaximum(2000, refill: false);
        var stats = new Dictionary<uint, int>
        {
            [Stat.EnergyShieldPctGainedOnBlock] = 5,
            [Stat.EnergyShieldPctOfArmourRatingGainedOnBlock] = 2,
        };
        var (_, _, es) = Game.BlockRecovery(player, stats);
        // 5% of 2000 ES (100) + 2% of 15000 Armour (300) = 400 ES recovered on block.
        Assert.Equal(400u, es);
    }
}
