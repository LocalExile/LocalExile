using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Modules.PhreciaAscendancies;

namespace GameServer.Gateway.Tests;

public sealed class PhreciaFourClassesTests
{
    // Ancestral Commander, Paladin, Polytheist and Surfcaster: each is a Phrecia class of the catalog, played only with its
    // own opt-in, and its nodes' stats reach the player once the PhreciaAscendancies module owns the selection.
    // The family's choices are its module's own: the catalog has them only while a provider is registered.
    internal static AscendancyTree PhreciaTree() => ((IAscendancyFamilyPolicy)Activator.CreateInstance(
        typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly
            .GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!).Tree;

    [Theory]
    [InlineData("Ancestral Commander", "Juggernaut", "Game_VERIFIED_ANCESTRAL_COMMANDER")]
    [InlineData("Paladin", "Champion", "Game_VERIFIED_PALADIN")]
    [InlineData("Polytheist", "Guardian", "Game_VERIFIED_POLYTHEIST")]
    [InlineData("Surfcaster", "Saboteur", "Game_VERIFIED_SURFCASTER")]
    public void Each_class_is_admitted_with_its_own_opt_in(string name, string id, string optIn)
    {
        var definition = Assert.Single(PhreciaFourClassesTests.PhreciaTree().Definitions,
            candidate => candidate.Name == name);
        Assert.Equal(id, definition.Identity.Id);
        var policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(
            typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly
                .GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!;
        string? previous = Environment.GetEnvironmentVariable(optIn);
        try
        {
            Environment.SetEnvironmentVariable(optIn, "0");
            Assert.False(policy.Supports(definition.Identity));
            Environment.SetEnvironmentVariable(optIn, "1");
            Assert.True(policy.Supports(definition.Identity));
            var node = definition.Nodes.Values.First(n => n.Stats.Count > 0);
            Assert.Equal(node.Stats, policy.NodeStats(definition, node.Id, node.Stats));
        }
        finally { Environment.SetEnvironmentVariable(optIn, previous); }
    }

    // "+# to Maximum Power and Frenzy Charges" is only this family's: its module gives the game the two maximums, and
    // the charge maximums come out as they did when the rules read the combined stat themselves.
    [Fact]
    public void The_Whisperers_combined_charge_maximum_reaches_the_rules_as_the_two_maximums()
    {
        const string optIn = "Game_VERIFIED_WHISPERER";
        var definition = Assert.Single(PhreciaTree().Definitions, candidate => candidate.Name == "Whisperer");
        var policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(
            typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly
                .GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!;
        string? previous = Environment.GetEnvironmentVariable(optIn);
        try
        {
            Environment.SetEnvironmentVariable(optIn, "1");
            var node = Assert.Single(definition.Nodes.Values, n => n.Stats.Any(s => s.Key == Stat.MaximumPowerAndFrenzyChargesPlus));
            int combined = node.Stats.Single(s => s.Key == Stat.MaximumPowerAndFrenzyChargesPlus).Value;
            Assert.True(combined > 0);
            var given = policy.NodeStats(definition, node.Id, node.Stats);
            Assert.DoesNotContain(given, s => s.Key == Stat.MaximumPowerAndFrenzyChargesPlus);
            Assert.Contains((Stat.MaxFrenzyCharges, combined), given);
            Assert.Contains((Stat.MaxPowerCharges, combined), given);
            // Every other stat of the node passes as it is, in its place.
            Assert.Equal(node.Stats.Where(s => s.Key != Stat.MaximumPowerAndFrenzyChargesPlus),
                given.Where(s => s.Key != Stat.MaxFrenzyCharges && s.Key != Stat.MaxPowerCharges));
            var sums = given.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
            var maximums = ChargePanelStats.Compute(key => sums.GetValueOrDefault(key)).ToDictionary(r => r.Stat, r => r.Value);
            Assert.Equal(ChargePanelStats.BaseMaximumCharges + combined, maximums[Stat.VirtualMaximumFrenzyCharges]);
            Assert.Equal(ChargePanelStats.BaseMaximumCharges + combined, maximums[Stat.VirtualMaximumPowerCharges]);
            Assert.Equal(ChargePanelStats.BaseMaximumCharges, maximums[Stat.VirtualMaximumEnduranceCharges]);
        }
        finally { Environment.SetEnvironmentVariable(optIn, previous); }
    }

    // One action-speed bucket (main) with Changing Seasons and the base-speed ceiling (this PR): a reversed 20% chill
    // speeds the player up, the character's own action_speed_-% 10 still slows, and Juggernaut's
    // not_affected_by_action_speed_modifiers caps the result at base speed.
    [Fact]
    public void A_reversed_chill_and_the_characters_action_speed_share_one_bucket()
    {
        Func<uint, int> Stats(params (string Name, int Value)[] stats) =>
            name => stats.FirstOrDefault(s => Stat.Of(s.Name) == name).Value;
        double Factor(params (string, int)[] stats)
        {
            var ailments = new PlayerElementalAilments();
            var get = Stats(stats);
            ailments.Reconcile(get);
            ailments.Inflict(PlayerElementalAilments.Kind.Chill, 20, 2, 0, get, new Random(1));
            return ailments.TotalActionFactor(1, 10);
        }
        Assert.Equal(.70, Factor(), 6);
        Assert.Equal(1.10, Factor(("chill_effect_is_reversed", 1)), 6);
        Assert.Equal(1, Factor(("chill_effect_is_reversed", 1), ("not_affected_by_action_speed_modifiers", 1)), 6);
    }

    // Three of the stats only this family's nodes give, by the module's own rules: the values the game's rules gave when
    // they read the stats themselves (30% more maximum, 455 Strength is 45 groups of ten, 107 Dexterity at 2 Mana each).
    [Fact]
    public void The_familys_own_stats_keep_their_values_in_the_module()
    {
        var hit = PhreciaStats.MaximumAttackDamage(DamagePacket.Of(DamageType.Physical, 10, 20).Add(DamageType.Fire, new(4, 8)),
            key => key == Stat.MaximumAttackDamagePlusPctFinalFromAscendancy ? 30 : 0);
        Assert.Equal(10, hit[DamageType.Physical].Min);
        Assert.Equal(26, hit[DamageType.Physical].Max, 9);
        Assert.Equal(4, hit[DamageType.Fire].Min);
        Assert.Equal(10.4, hit[DamageType.Fire].Max, 9);

        var stats = new PlainStatMap(new() { [Stat.Strength] = 455, [Stat.SkillEffectDurationPlusPct] = 10 });
        PhreciaStats.DurationPerTenStrength(null!, stats);
        Assert.Equal(10, stats[Stat.SkillEffectDurationPlusPct]);
        stats[Stat.SkillEffectDurationPlusPctPer10Strength] = 1;
        PhreciaStats.DurationPerTenStrength(null!, stats);
        Assert.Equal(55, stats[Stat.SkillEffectDurationPlusPct]);

        var sources = new Dictionary<uint, int> { [Stat.BaseMaximumMana] = 15, [Stat.XManaPerDexterity] = 2 };
        PhreciaStats.ManaPerDexterity(null!, sources, (0, 107, 0));
        Assert.Equal(229, sources[Stat.BaseMaximumMana]);
    }

    // The game's hand-overs run where its rules read those stats before: the default attack's hit, the stats on the final
    // Strength, the pool's sources on the final Dexterity (the pool comes out as with the same flat Mana), and the cast.
    [Fact]
    public void The_games_hand_overs_give_the_familys_own_stats_their_effect()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        bool Mine(Player p) => ReferenceEquals(p, player);
        void Rebuild() { player.StatSourcesCache = null; Game.RecomputePoolsAndSpeed(player); }

        Rebuild();
        var weapon = Game.WeaponFor(player);
        var withMore = new Dictionary<uint, int>(Game.CharacterPlayerStats(player)) { [Stat.MaximumAttackDamagePlusPctFinalFromAscendancy] = 30 };
        DamageRange Swing() => Game.AttackHit(weapon, withMore, melee: true, accuracy: new(Game.PlayerAccuracy(player)))[DamageType.Physical];
        var plain = Swing();
        Assert.True(plain.Max > 0);
        using (GameEvents.AttackHit.Add("Game", PhreciaStats.MaximumAttackDamage))
        {
            Assert.Equal(plain.Min, Swing().Min, 9);
            Assert.Equal(plain.Max * 1.3, Swing().Max, 9);
        }

        int durationBefore = Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.SkillEffectDurationPlusPct);
        using (GameEvents.DerivedStats.Add("Game", PhreciaStats.DurationPerTenStrength))
        using (GameEvents.PlayerStatSources.Add("Game", (p, s) => !Mine(p) ? s
            : new Dictionary<uint, int>(s) { [Stat.SkillEffectDurationPlusPctPer10Strength] = 1 }))
        {
            Rebuild();
            var built = Game.CharacterPlayerStats(player);
            Assert.True(built[Stat.Strength] >= 10);
            Assert.Equal(durationBefore + built[Stat.Strength] / 10, built[Stat.SkillEffectDurationPlusPct]);
        }

        player.God = true;
        Rebuild();
        var (manaBefore, dexterity) = (player.Pools.Mana, player.Pools.Dexterity);
        Assert.True(dexterity > 1000);
        using (GameEvents.PlayerStatSources.Add("Game", (p, s) => !Mine(p) ? s
            : new Dictionary<uint, int>(s) { [Stat.XManaPerDexterity] = 2 }))
        using (GameEvents.PoolSources.Add("Game", PhreciaStats.ManaPerDexterity))
            Rebuild();
        var manaPerDexterity = player.Pools.Mana;
        Assert.True(manaPerDexterity > manaBefore);
        using (GameEvents.PlayerStatSources.Add("Game", (p, s) => !Mine(p) ? s
            : new Dictionary<uint, int>(s) { [Stat.BaseMaximumMana] = s.GetValueOrDefault(Stat.BaseMaximumMana) + 2 * dexterity }))
        {
            Rebuild();
            Assert.Equal(player.Pools.Mana, manaPerDexterity);
        }

        using (GameEvents.CastSpeedMore.Add("Game", PhreciaStats.CastSpeedWithFishingRod))
        {
            int Speed() => Game.FinalCastSpeedPct(key => key == Stat.CastSpeedPlusPctFinalWhileHoldingFishingRod ? 50 : 0, player, 0);
            Assert.Equal(0, Speed());
            var items = Game.ItemsOf(player);
            foreach (var held in items.Items(PlayerInventories.Weapon1).ToArray()) items.Remove(PlayerInventories.Weapon1, held.Id);
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Fishing Rod")!));
            Rebuild();
            Assert.Equal(50, Speed());
        }

        // The buff rows of the family's nodes (Ghost of the Deep 37553, Light of Divinity 13763, Born under Fire 32105)
        // act while an owner applies them: one owner letting go does not take another's away.
        bool Self() => PassiveBuffAuras.SelfBuffsFor([37553], false).Any(a => a.Template == "CustomAscendancyIllegalFisherman");
        bool Enemy() => PassiveBuffAuras.EnemyAurasFor([13763], false, Game.EnemyAuraActs)
            .Any(a => a.Template == "CustomAscendancyNearbyEnemiesIncreasedElementalDamageTaken");
        bool Converts() => PassiveBuffAuras.EnemyAurasFor([32105], false, Game.EnemyAuraActs)
            .Any(a => a.Template == "CustomAscendancyEnemyPhysicalDamageConvertedToFire");
        Assert.False(Self() || Enemy() || Converts());
        var one = PassiveBuffAuras.Apply(fixture.Lifetime, "CustomAscendancyIllegalFisherman");
        using (PassiveBuffAuras.Apply(fixture.Lifetime, "CustomAscendancyIllegalFisherman"))
        using (PassiveBuffAuras.Apply(fixture.Lifetime, "CustomAscendancyNearbyEnemiesIncreasedElementalDamageTaken"))
        using (PassiveBuffAuras.Apply(fixture.Lifetime, "CustomAscendancyEnemyPhysicalDamageConvertedToFire"))
        {
            one.Dispose();
            Assert.True(Self() && Enemy() && Converts());
        }
        Assert.False(Self() || Enemy() || Converts());
    }
}
