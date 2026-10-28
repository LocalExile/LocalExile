using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RuthlessGameplayTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Scarcity_parameters_change_real_drop_generation_and_zero_disables_it()
    {
        var catalog = new ItemCatalog(Data(), ruthless: true);
        var drops = new ItemDrops(catalog, new ItemRolls(catalog, Data()), Data());
        int normal = 0, scarce = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            normal += drops.Roll(30, MonsterRarity.Rare, new Random(seed)).Count;
            scarce += drops.Roll(30, MonsterRarity.Rare, new Random(seed), quantityMultiplier: 0.2).Count;
            Assert.Empty(drops.Roll(30, MonsterRarity.Rare, new Random(seed), quantityMultiplier: 0));
            Assert.Null(drops.Gold(30, MonsterRarity.Rare, new Random(seed), multiplier: 0));
            Assert.All(drops.Roll(30, MonsterRarity.Rare, new Random(seed), rarityMultiplier: 0),
                d => Assert.Equal(ItemRarity.Normal, d.Item.Rarity));
        }
        Assert.InRange(scarce, (int)(normal * 0.1), (int)(normal * 0.3));
    }

    [Fact]
    public void Small_kills_accumulate_reduced_gem_XP_instead_of_losing_every_fraction()
    {
        double carry = 0;
        int awarded = 0;
        for (int kill = 0; kill < 10; kill++) awarded += RuthlessGameplay.GemExperience(4, 0.25, ref carry);
        Assert.Equal(1, awarded);
        Assert.InRange(carry, 0, 0.00001);
        Assert.Equal(0, RuthlessGameplay.GemExperience(100, 0, ref carry));
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void Active_utility_flasks_require_the_explicit_Ruthless_charge_exception(
        bool ruthless, bool active, bool exception, bool allowed) =>
        Assert.Equal(allowed, RuthlessGameplay.CanGainUtilityCharges(ruthless, active, exception));

    [Fact]
    public void Boss_recovery_is_capped_and_does_not_revive_a_dead_boss()
    {
        Assert.Equal(75u, RuthlessGameplay.BossLifeAfterDeath(50, 100, 0.25));
        Assert.Equal(100u, RuthlessGameplay.BossLifeAfterDeath(90, 100, 0.25));
        Assert.Equal(0u, RuthlessGameplay.BossLifeAfterDeath(0, 100, 0.25));
        Assert.Equal(50u, RuthlessGameplay.BossLifeAfterDeath(50, 100, 0));
    }
}
