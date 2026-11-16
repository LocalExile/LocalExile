using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

static class UniqueVariantTestCases
{
    public static void World_variants_keep_coupled_modifiers_bases_and_default_creation()
    {
        var catalog = new UniqueCatalog(Program.Items);
        var random = new Random(832);
        var yriel = Assert.IsType<UniqueCatalog.Definition>(catalog.Find("Yriel's Fostering"));
        string[] grants = ["GrantsSummonBeastUrsaUnique__1", "GrantsSummonBeastRhoaUnique__1", "GrantsSummonBeastSnakeUnique__1"];
        foreach (var (variant, damage, ailment) in new[]
        {
            ("Ursa", "AddedPhysicalDamageToAttacksBeastialMinionUnique__1", "ProjectileAttacksChanceToBleedBeastialMinionUnique__1_"),
            ("Rhoa", "AttackAndMovementSpeedBeastialMinionUnique__1", "ProjectileAttacksChanceToMaimBeastialMinionUnique__1"),
            ("Snake", "AddedChaosDamageToAttacksBeastialMinionUnique__1", "ProjectileAttacksChanceToPoisonBeastialMinionUnique__1"),
        })
        {
            var definition = Assert.IsType<UniqueCatalog.Definition>(catalog.Find($"Yriel's Fostering ({variant})"));
            var item = Assert.IsType<Item>(catalog.Create(definition, random));
            var ids = item.Mods.Select(m => Program.Items.Mods.ByHash(m.Hash)?.Id).ToArray();
            Assert.Contains(damage, ids);
            Assert.Contains(ailment, ids);
            Assert.Single(ids, id => grants.Contains(id));
        }
        var observed = new HashSet<string>();
        for (int i = 0; i < 60; i++)
        {
            var item = Assert.IsType<Item>(catalog.Create(yriel, random));
            var grant = Assert.Single(item.Mods.Select(m => Program.Items.Mods.ByHash(m.Hash)?.Id), id => grants.Contains(id));
            observed.Add(grant!);
        }
        Assert.Equal(3, observed.Count);
        var ordinaryBeacon = Assert.IsType<UniqueCatalog.Definition>(catalog.Find("Beacon of Madness"));
        var defaultBeacon = Assert.IsType<Item>(catalog.Create(ordinaryBeacon, random));
        Assert.Equal("Metadata/Items/Armours/Boots/BootsAtlas1", Program.Items.ByHash(defaultBeacon.TypeHash)!.Id);
        foreach (var (name, baseId) in new[] { ("Chaos", "BootsAtlas1"), ("Physical", "BootsAtlas2"), ("Elemental", "BootsAtlas3") })
        {
            var definition = Assert.IsType<UniqueCatalog.Definition>(catalog.Find($"Beacon of Madness ({name})"));
            var item = Assert.IsType<Item>(catalog.Create(definition, random));
            Assert.Equal("Metadata/Items/Armours/Boots/" + baseId, Program.Items.ByHash(item.TypeHash)!.Id);
        }
        Assert.Null(catalog.Find("Yriel's Fostering (Unknown)"));
        Assert.IsType<Item>(catalog.Create(Assert.IsType<UniqueCatalog.Definition>(catalog.Find("Tabula Rasa")), random));
    }
}
