using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Passive mastery effects that read the equipped items, the allocated masteries or the life threshold,
/// through the real pool build (PassiveSkillMasteryEffects of the target client).</summary>
public sealed class PassiveMasteryEffectTests
{
    static Player NewPlayer(string name) => new(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
    {
        Character = { Name = name + Guid.NewGuid().ToString("N"), ClassRow = 1 }, Instance = new(),
        LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
    };

    static Item Base(string id) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/" + id)!) with
    {
        DefenceBaseRoll = Item.MaximumDefenceBaseRoll,
    };

    static void Equip(Player player, byte row, Item item)
    {
        var items = Game.ItemsOf(player);
        foreach (var old in items.Items(row).ToList()) items.Remove(row, old.Id);
        items.Add(row, 0, 0, item);
    }

    // Mastery nodes offering the effect.
    static ushort[] Mastery(string effect) => [.. Enumerable.Range(1, ushort.MaxValue - 1).Select(i => (ushort)i)
        .Where(i => Program.Passives.Find(i) is { IsMastery: true } && Program.Passives.EffectsFor(i).Any(e => e.Id == effect))];

    static void Select(Player player, string effect, int index = 0) => Allocate(player, Mastery(effect)[index], effect);

    static void Allocate(Player player, ushort node, string effect)
    {
        player.Character.Passives.Add(node);
        player.Character.MasteryEffects = new Dictionary<ushort, ushort>(player.Character.MasteryEffects)
        {
            [node] = Program.Passives.EffectsFor(node).Single(e => e.Id == effect).Hash,
        };
    }

    static (decimal Armour, decimal Evasion, int EnergyShield) Local(Player player, byte row) =>
        Game.EquippedArmourDefences(player).Single(d => d.Row == row).Defence is var d ? (d.Armour, d.Evasion, d.EnergyShield) : default;

    [Fact]
    public void Defence_masteries_scale_their_equipment_slot_and_check_the_other_pieces()
    {
        var player = NewPlayer("defence-masteries");
        using var _ = TestRequirements.Waive(player);
        // Armour and Energy Shield on every piece.
        Equip(player, PlayerInventories.Helmet, Base("Armours/Helmets/HelmetStrInt1"));
        Equip(player, PlayerInventories.BodyArmour, Base("Armours/BodyArmours/BodyStrInt1"));
        Equip(player, PlayerInventories.Gloves, Base("Armours/Gloves/GlovesStrInt1"));
        Equip(player, PlayerInventories.Boots, Base("Armours/Boots/BootsStrInt1"));
        var helmet = Local(player, PlayerInventories.Helmet);
        var body = Local(player, PlayerInventories.BodyArmour);
        decimal glovesBoots = Local(player, PlayerInventories.Gloves).Armour + Local(player, PlayerInventories.Boots).Armour;
        var before = Game.PoolsFor(player, 50);

        // "100% increased Energy Shield from Equipped Helmet" adds the helmet's own ES once more.
        Select(player, "EnergyShield6");
        var withHelmet = Game.PoolsFor(player, 50);
        Assert.InRange((long)withHelmet.EnergyShield - before.EnergyShield, helmet.EnergyShield - 1, helmet.EnergyShield + 1);
        Assert.Equal(before.Armour, withHelmet.Armour);

        // "100% increased Armour from Equipped Boots and Gloves".
        Select(player, "Armour6");
        var withGloves = Game.PoolsFor(player, 50);
        Assert.InRange(withGloves.Armour - withHelmet.Armour, glovesBoots - 1, glovesBoots + 1);

        // "+1% to all maximum Elemental Resistances if Helmet, Body Armour, Gloves and Boots all have Armour" and
        // "100% increased Armour and Energy Shield from Body Armour if Helmet, Gloves and Boots have both".
        Select(player, "Armour4", 1);
        Select(player, "ArmourEnergyShield5");
        var conditional = Game.PoolsFor(player, 50);
        Assert.Equal(withGloves.FireMaxResistance + 1, conditional.FireMaxResistance);
        Assert.InRange(conditional.Armour - withGloves.Armour, body.Armour - 1, body.Armour + 1);
        Assert.InRange((long)conditional.EnergyShield - withGloves.EnergyShield, body.EnergyShield - 1, body.EnergyShield + 1);

        // An Evasion helmet breaks both conditions; the Evasion Mastery's body evasion increase reads the body slot.
        Equip(player, PlayerInventories.Helmet, Base("Armours/Helmets/HelmetDex1"));
        Equip(player, PlayerInventories.BodyArmour, Base("Armours/BodyArmours/BodyDex1"));
        var broken = Game.PoolsFor(player, 50);
        Assert.Equal(before.FireMaxResistance, broken.FireMaxResistance);
        Select(player, "Evasion6");
        decimal bodyEvasion = Local(player, PlayerInventories.BodyArmour).Evasion;
        Assert.InRange(Game.PoolsFor(player, 50).Evasion - broken.Evasion, bodyEvasion - 1, bodyEvasion + 1);
    }

    [Fact]
    public void Counting_and_threshold_masteries_follow_the_allocation_and_the_equipment()
    {
        var player = NewPlayer("counting-masteries");
        using var _ = TestRequirements.Waive(player);
        var before = Game.PoolsFor(player, 50);
        // "+5 to Strength per Allocated Mastery Passive Skill", with two other masteries allocated.
        Select(player, "EnergyShield6");
        Select(player, "Armour6");
        Select(player, "Attributes3");
        Assert.Equal(before.Strength + 15, Game.PoolsFor(player, 50).Strength);

        // "10% more Maximum Life if you have at least 6 Life Masteries allocated": the sixth one counts.
        var life = Mastery("Life1");
        for (int i = 0; i < 5; i++)
            Allocate(player, life[i], i == 0 ? "Life1" : "Life4"); // Life4: the low life threshold only
        var five = Game.PoolsFor(player, 50);
        Allocate(player, life[5], "Life4");
        var six = Game.PoolsFor(player, 50);
        Assert.True(six.Life > five.Life * 105 / 100, $"{five.Life} -> {six.Life}");

        // "You count as on Full Life while at 90% of maximum Life or above".
        var threshold = new Dictionary<uint, int> { [Stat.FullLifeThresholdPctOverride] = 90 };
        Assert.True(Game.CountsAsFullLife(90, 100, threshold));
        Assert.False(Game.CountsAsFullLife(89, 100, threshold));
        Assert.False(Game.CountsAsFullLife(99, 100, new Dictionary<uint, int>()));

        // "+10% chance to Suppress Spell Damage for each Dagger you're Wielding".
        Select(player, "Dagger3");
        int none = Game.PoolsFor(player, 50).SpellSuppressionChance;
        Equip(player, PlayerInventories.Weapon1, Base("Weapons/OneHandWeapons/Daggers/Dagger1"));
        Equip(player, PlayerInventories.Offhand1, Base("Weapons/OneHandWeapons/Daggers/Dagger1"));
        Assert.Equal(none + 20, Game.PoolsFor(player, 50).SpellSuppressionChance);
    }
}
