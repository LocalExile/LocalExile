using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class HinekoraLockTests
{
    static readonly ItemCatalog.BaseType Wand = Program.Items.Find("Driftwood Wand")!;
    static ItemRolls Rolls => new(Program.Items);

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    static ItemMod Explicit(string id) => Mod(id).Roll(ModSection.Explicit, new Random(1));

    static Item Rare(ItemCatalog.BaseType type, params ItemMod[] mods) =>
        ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            Mods = [.. ItemCatalog.Create(type).Mods.Where(m => m.Section != ModSection.Explicit), .. mods],
        };

    static Item FourModWand() => Rare(Wand,
        Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
        Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")) with { ItemLevel = 1 };

    [Fact]
    public void Lock_round_trip_is_deterministic_and_refuses_invalid_targets()
    {
        var locked = Rolls.ApplyLock(FourModWand(), new Random(1));
        Assert.NotNull(locked);
        Assert.True(locked.Foreseeing);
        Assert.NotEqual(0u, locked.ForeseeSeed);

        Assert.Null(Rolls.ApplyLock(ItemCatalog.Create(Wand), new Random(1)));
        Assert.Null(Rolls.ApplyLock(ItemCatalog.Create(Wand) with { Rarity = ItemRarity.Magic }, new Random(1)));
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
        Assert.Null(Rolls.ApplyLock(gem, new Random(1)));
        Assert.Null(Rolls.ApplyLock(FourModWand() with { Corrupted = true }, new Random(1)));
        Assert.Null(Rolls.ApplyLock(FourModWand() with { Mirrored = true }, new Random(1)));
        var map = Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
        Assert.Null(Rolls.ApplyLock(Rare(map,
            Explicit("LocalIncreasedPhysicalDamagePercent1"), Explicit("IncreasedManaWeapon1"),
            Explicit("LocalIncreasedAttackSpeed1"), Explicit("Intelligence1")), new Random(1)));
        Assert.Null(Rolls.ApplyLock(FourModWand() with { Foreseeing = true, ForeseeSeed = 123 }, new Random(1)));

        var cleared = ItemRolls.ClearForesee(locked);
        Assert.False(cleared.Foreseeing);
        Assert.Equal(0u, cleared.ForeseeSeed);
        Assert.Equal(locked.Mods, cleared.Mods);
        Assert.Equal(locked.Rarity, cleared.Rarity);

        uint chaos = Program.Items.Find(CurrencyEffects.ChaosOrb)!.Hash;
        var preview = CurrencyEffects.Chaos(locked, Rolls, ItemRolls.ForeseeRandom(locked, chaos));
        var commit = CurrencyEffects.Chaos(locked, Rolls, ItemRolls.ForeseeRandom(locked, chaos));
        Assert.NotNull(preview);
        Assert.Equal(preview!.Payload(), commit!.Payload());
        Assert.False(ItemRolls.ClearForesee(commit!).Foreseeing);

        uint alchemy = Program.Items.Find(CurrencyEffects.OrbOfAlchemy)!.Hash;
        var alchemyStream = ItemRolls.ForeseeRandom(locked, alchemy);
        var chaosStream = ItemRolls.ForeseeRandom(locked, chaos);
        var a = Enumerable.Range(0, 10).Select(_ => alchemyStream.Next()).ToArray();
        var b = Enumerable.Range(0, 10).Select(_ => chaosStream.Next()).ToArray();
        Assert.False(a.SequenceEqual(b));
    }
}
