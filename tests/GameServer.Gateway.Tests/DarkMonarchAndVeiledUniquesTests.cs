using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DarkMonarchAndVeiledUniquesTests
{
    static readonly (string Name, int[] GroupCounts)[] VeiledUniqueContracts =
    [
        ("Bitterbind Point", [1]),
        ("Cinderswallow Urn", [1]),
        ("The Devouring Diadem", [1]),
        ("Paradoxica", [1, 1]),
        ("Cane of Kulemak", [1, 2]),
    ];

    [Fact]
    public void Dark_monarch_and_veiled_uniques_roll_native_mods_from_world_catalog()
    {
        var catalog = new UniqueCatalog(Program.Items);
        var monarchDef = Assert.IsType<UniqueCatalog.Definition>(catalog.Find("The Dark Monarch"));
        Assert.Contains("DoubleMinionLimitsUnique_1", monarchDef.Mods);

        var random = new Random(1337);
        var rolledVariants = new HashSet<int>();
        for (int trial = 0; trial < 48; trial++)
        {
            var helmet = Assert.IsType<Item>(catalog.Create(monarchDef, random));
            Assert.NotNull(catalog.RollsOf(helmet));
            var mod = Assert.Single(helmet.Mods, m => Program.Items.Mods.ByHash(m.Hash)?.Id == "DoubleMinionLimitsUnique_1");
            int variant = Assert.Single(mod.Values);
            Assert.InRange(variant, 1, 16);
            rolledVariants.Add(variant);
        }
        Assert.True(rolledVariants.Count >= 8);

        foreach (var (name, groupCounts) in VeiledUniqueContracts)
        {
            var def = Assert.IsType<UniqueCatalog.Definition>(catalog.Find(name));
            Assert.NotNull(def.RollGroups);
            Assert.Equal(groupCounts.Length, def.RollGroups!.Length);
            for (int g = 0; g < groupCounts.Length; g++)
                Assert.Equal(groupCounts[g], def.RollGroups[g].Count);

            for (int trial = 0; trial < 12; trial++)
            {
                var rolled = Assert.IsType<Item>(catalog.Create(def, random));
                var modIds = rolled.Mods.Select(m => Program.Items.Mods.ByHash(m.Hash)!.Id).ToArray();
                Assert.Equal(modIds.Length, modIds.Distinct().Count());
                for (int g = 0; g < groupCounts.Length; g++)
                    Assert.Equal(groupCounts[g], modIds.Count(id => def.RollGroups[g].Options.Contains(id)));
            }
        }
    }

    [Fact]
    public async Task Dark_monarch_and_minion_exclusivity_rules_match_retail_families()
    {
        Func<uint, int> Monarch(int value) => s => s == Stat.MaximumNumberOfXMinionTypeDoubled ? value : 0;

        for (int i = 1; i <= 16; i++)
            Assert.NotNull(Game.DarkMonarchMinionFamily(i));

        Assert.True(Game.MinionFamilyAllowed(Monarch(1), "AnimatedWeapon"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(1), "VaalAnimatedWeapon"));
        Assert.False(Game.MinionFamilyAllowed(Monarch(1), "AnimatedGuardian"));
        Assert.Equal(10, Game.ApplyDarkMonarchLimit(5, Monarch(4), "Spectre"));
        Assert.Equal(0, Game.ApplyDarkMonarchLimit(5, Monarch(4), "Zombie"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(8), "Absolution"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(8), "VaalAbsolution"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(9), "DominatingRare"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(9), "VaalDomination"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(10), "Purity"));
        Assert.Equal(8, Game.ApplyDarkMonarchLimit(4, Monarch(10), "Purity"));
        Assert.Equal(6, Game.ApplyDarkMonarchLimit(3, Monarch(12), "Phantasm"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(13), "Skeleton"));
        Assert.True(Game.MinionFamilyAllowed(Monarch(13), "VaalSkeleton"));

        Func<uint, int> SpectreOnly = s => s == Stat.YouCannotHaveNonSpectreMinions ? 1 : 0;
        Assert.True(Game.MinionFamilyAllowed(SpectreOnly, "Spectre"));
        Assert.False(Game.MinionFamilyAllowed(SpectreOnly, "Golem"));
        Assert.False(Game.MinionFamilyAllowed(SpectreOnly, "Purity"));

        Func<uint, int> AnimatedOnly = s => s == Stat.YouCannotHaveNonAnimatedMinions ? 1 : 0;
        Assert.True(Game.MinionFamilyAllowed(AnimatedOnly, "AnimatedWeapon"));
        Assert.True(Game.MinionFamilyAllowed(AnimatedOnly, "VaalAnimatedWeapon"));
        Assert.True(Game.MinionFamilyAllowed(AnimatedOnly, "AnimatedGuardian"));
        Assert.False(Game.MinionFamilyAllowed(AnimatedOnly, "Skeleton"));

        Func<uint, int> GolemOnly = s => s == Stat.YouCannotHaveNonGolemMinions ? 1 : 0;
        Assert.True(Game.MinionFamilyAllowed(GolemOnly, "Golem"));
        Assert.False(Game.MinionFamilyAllowed(GolemOnly, "Spectre"));

        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20), ("Raise Zombie", 20), ("Summon Chaos Golem", 20));
        var skeletonsDef = Game.SummonData.Find("SummonSkeletons")!;
        var zombiesDef = Game.SummonData.Find("RaiseZombie")!;
        var golemDef = Game.GolemData.Find("SummonChaosGolem")!;

        ushort SourceOf(string effect) => Game.GemSkillEffects(f.Player)
            .Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;

        ushort skelSource = SourceOf("SummonSkeletons");
        ushort zombieSource = SourceOf("RaiseZombie");
        ushort golemSource = SourceOf("SummonChaosGolem");

        int baseSkeletons = Game.SummonLimit(f.Player, skeletonsDef, Game.SupportedSkill(f.Player, skelSource));
        int baseZombies = Game.SummonLimit(f.Player, zombiesDef, Game.SupportedSkill(f.Player, zombieSource));
        Assert.True(baseSkeletons > 0);
        Assert.True(baseZombies > 0);

        // Summon a skeleton before equipping a non-skeleton Dark Monarch, and verify SummonsTick removes it.
        Assert.NotEqual(0u, await Game.SummonFighter(f.Player, skelSource, skeletonsDef, (100, 100)));
        Assert.Single(f.Player.Summons.Fighters);

        var doubleMod = Program.Items.Mods.ById("DoubleMinionLimitsUnique_1")!;
        var monarchType = Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetInt14")!;
        Item MonarchWithRoll(int roll) => ItemCatalog.Create(monarchType) with
        {
            Rarity = ItemRarity.Unique,
            Identified = true,
            Mods = [new(ModSection.Explicit, doubleMod.Hash, [roll])],
        };

        var helmSlot = Game.ItemsOf(f.Player).Add(PlayerInventories.Helmet, 0, 0, MonarchWithRoll(13));
        Assert.Equal(baseSkeletons * 2, Game.SummonLimit(f.Player, skeletonsDef, Game.SupportedSkill(f.Player, skelSource)));
        Assert.Equal(0, Game.SummonLimit(f.Player, zombiesDef, Game.SupportedSkill(f.Player, zombieSource)));
        Assert.Equal(0u, await Game.SummonGolem(f.Player, golemSource, golemDef, (100, 100)));

        // Switch helm roll to Golems (2): golems double and succeed; existing skeleton is unsummoned on tick.
        Game.ItemsOf(f.Player).Change(PlayerInventories.Helmet, helmSlot.Id, _ => MonarchWithRoll(2));
        Assert.Equal(0, Game.SummonLimit(f.Player, skeletonsDef, Game.SupportedSkill(f.Player, skelSource)));
        double now = f.Player.Clock.Elapsed.TotalSeconds + 1;
        await Game.SummonsTick(f.Player, now);
        Assert.Empty(f.Player.Summons.Fighters);

        Assert.NotEqual(0u, await Game.SummonGolem(f.Player, golemSource, golemDef, (100, 100)));
        var golem = Assert.Single(f.Player.Golems.All.Values);
        Assert.Equal(2, golem.Limit);
    }
}
