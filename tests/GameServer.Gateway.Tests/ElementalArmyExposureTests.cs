using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ElementalArmyExposureTests
{
    [Fact]
    public async Task Elemental_Army_exposes_only_the_largest_element_of_a_supported_hit()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20), ("Elemental Army Support", 20));
        var host = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [2] });
        ushort source = Game.GemSkillEffects(f.Player).Single().Id;
        uint id = await Game.SummonFighter(f.Player, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        var fighter = f.Player.Summons.Fighters[id];
        var target = f.Player.Area.Monsters[900] = new("target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
        var elemental = DamagePacket.Of(DamageType.Fire, 100, 100)
            .Add(DamagePacket.Of(DamageType.Cold, 200, 200));
        HitWith(elemental);
        Assert.Equal(-10, Game.MonsterExposure(target, DamageType.Cold, 1));
        Assert.Equal(0, Game.MonsterExposure(target, DamageType.Fire, 1));
        Assert.Equal(0, Game.MonsterExposure(target, DamageType.Cold, 5));

        target.Exposures.Clear();
        target.CoveredInAshUntil = 4;
        HitWith(DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamagePacket.Of(DamageType.Cold, 110, 110)));
        Assert.Equal(-10, Game.MonsterExposure(target, DamageType.Fire, 1));
        Assert.Equal(0, Game.MonsterExposure(target, DamageType.Cold, 1));

        target.Exposures.Clear();
        HitWith(DamagePacket.Of(DamageType.Physical, 100, 100));
        Assert.Empty(target.Exposures);
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [1, 1, 1] });
        id = await Game.SummonFighter(f.Player, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        fighter = f.Player.Summons.Fighters[id];
        HitWith(elemental);
        Assert.Empty(target.Exposures);

        void HitWith(DamagePacket packet)
        {
            Game.GolemStepLocked(f.Player, fighter, 1);
            var ability = fighter.Abilities.First() with { Attack = new(0x4000, 1, 20, packet) { Spell = true }, Crit = 0 };
            fighter.Abilities = [ability];
            Assert.True(Game.GolemHitLocked(f.Player, fighter, ability, 900, target, 1));
        }
    }

    [Fact]
    public async Task Item_granted_beast_applies_exposure_from_the_support_in_its_armour()
    {
        using var f = new MinionUtilityTests.Fixture();
        // 1000 more of each attribute so CurrentGemRequirements admits the level-20 supports socketed in the granting armour.
        f.Player.God = true;
        f.Equip(("Elemental Army Support", 20), ("Added Cold Damage Support", 20), ("Meat Shield Support", 20));
        var items = Game.ItemsOf(f.Player);
        var weapon = items.Items(PlayerInventories.Weapon1).Single();
        var armour = new UniqueCatalog(Program.Items).Create("Yriel's Fostering (Ursa)", new Random(17))!;
        items.Remove(PlayerInventories.Weapon1, weapon.Id);
        items.Add(PlayerInventories.BodyArmour, 0, 0, armour with { Sockets = weapon.Item.Sockets, Links = [1, 1, 1] });
        var source = Game.GemSkillEffects(f.Player).Single();
        Game.QueueBeastCompanionLocked(f.Player, Game.SupportedSkill(f.Player, source.Id), source.Id, null);
        await Game.TickBeastCompanions(f.Player, 1, Game.CombatLifetime);
        var beast = Assert.IsType<BeastCompanions.ActiveBeast>(f.Player.Beasts.Active);
        var target = f.Player.Area.Monsters[900] = new("target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
        Assert.NotNull(Game.MinionHitTargetLocked(f.Player, beast.Id, beast.Monster, 900, target, 1,
            new Random(1), checkReach: false, accuracy: 1000000));
        Assert.Equal(-10, Game.MonsterExposure(target, DamageType.Cold, 1));

        f.Player.Walk.Teleport(100, 100, 1);
        var near = Game.MinionHitTargetLocked(f.Player, beast.Id, beast.Monster, 900, target, 1,
            new Random(1), checkReach: false, accuracy: 1000000);
        f.Player.Walk.Teleport(150, 100, 1);
        Assert.Null(Game.MinionTargetLocked(f.Player, beast.Id, beast.Monster, (105, 100), (150, 100), 1));
        var far = Game.MinionHitTargetLocked(f.Player, beast.Id, beast.Monster, 900, target, 1,
            new Random(1), checkReach: false, accuracy: 1000000);
        Assert.True(near!.Value.Damage > far!.Value.Damage);
    }

    [Fact]
    public async Task Speakers_Wreath_keeps_an_unignited_enemy_alive_from_an_owned_beast_hit()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Player.God = true;
        var items = Game.ItemsOf(f.Player);
        var catalog = new UniqueCatalog(Program.Items);
        var armour = Assert.IsType<Item>(catalog.Create("Yriel's Fostering (Ursa)", new Random(17)));
        var wreath = Assert.IsType<Item>(catalog.Create("Speaker's Wreath", new Random(19)));
        var weapon = items.Items(PlayerInventories.Weapon1).Single();
        items.Remove(PlayerInventories.Weapon1, weapon.Id);
        items.Add(PlayerInventories.BodyArmour, 0, 0, armour);
        items.Add(PlayerInventories.Helmet, 0, 0, wreath);
        var source = Assert.Single(Game.GemSkillEffects(f.Player));
        Game.QueueBeastCompanionLocked(f.Player, Game.SupportedSkill(f.Player, source.Id), source.Id, null);
        await Game.TickBeastCompanions(f.Player, 1, Game.CombatLifetime);
        var beast = Assert.IsType<BeastCompanions.ActiveBeast>(f.Player.Beasts.Active);
        f.Player.Area.Monsters[900] = new("unignited target", new(105, 100, 0), 100, 3, 2) { Hostile = true };
        f.Player.Area.Monsters[901] = new("ignited target", new(105, 102, 0), 100, 3, 2) { Hostile = true };
        var ordinary = f.Player.Area.Monsters[900];
        var ignited = f.Player.Area.Monsters[901];
        Assert.True(ignited.Ignites.Add(1, 1, 4));
        Assert.NotNull(Game.MinionHitTargetLocked(f.Player, beast.Id, beast.Monster, 900, ordinary, 1,
            new Random(1), checkReach: false, accuracy: 1_000_000));
        Assert.NotNull(Game.MinionHitTargetLocked(f.Player, beast.Id, beast.Monster, 901, ignited, 1,
            new Random(1), checkReach: false, accuracy: 1_000_000));
        Assert.Equal(1u, ordinary.Life);
        Assert.True(ignited.Dead);
    }
}
