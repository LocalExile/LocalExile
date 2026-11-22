using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ElementalArmyExposureTests
{
    [Fact]
    public async Task Elemental_Army_exposes_only_the_largest_element_of_a_supported_hit()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20), ("Elemental Army Support", 20));
        var host = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [2] });
        ushort source = Game.GemSkillEffects(f.Conn).Single().Id;
        uint id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        var fighter = f.Conn.Summons.Fighters[id];
        var target = f.Conn.Monsters[900] = new("target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
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
        target.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 4, 0,
            stat => stat == "support_chills_also_grant_cold_damage_taken_+%_equal_to_slow_amount" ? 1 : 0,
            _ => 0, stated: 30);
        HitWith(DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamagePacket.Of(DamageType.Cold, 110, 110)));
        Assert.Equal(-10, Game.MonsterExposure(target, DamageType.Cold, 1));
        Assert.Equal(0, Game.MonsterExposure(target, DamageType.Fire, 1));

        target.Exposures.Clear();
        HitWith(DamagePacket.Of(DamageType.Physical, 100, 100));
        Assert.Empty(target.Exposures);
        Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [1, 1] });
        id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        fighter = f.Conn.Summons.Fighters[id];
        HitWith(elemental);
        Assert.Empty(target.Exposures);

        void HitWith(DamagePacket packet)
        {
            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, fighter, 1);
            var ability = fighter.Abilities.First() with { Attack = new(0x4000, 1, 20, packet) { Spell = true }, Crit = 0 };
            fighter.Abilities = [ability];
            Assert.True(Game.GolemHitLocked("test", f.Stream, f.Ciphers, f.Conn, fighter, ability, 900, target, 1));
        }
    }

    [Fact]
    public async Task Item_granted_beast_applies_exposure_from_the_support_in_its_armour()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Elemental Army Support", 20), ("Added Cold Damage Support", 20));
        var items = Game.ItemsOf(f.Conn);
        var weapon = items.Items(PlayerInventories.Weapon1).Single();
        var armour = new UniqueCatalog(Program.Items).Create("Yriel's Fostering (Ursa)", new Random(17))!;
        items.Remove(PlayerInventories.Weapon1, weapon.Id);
        items.Add(PlayerInventories.BodyArmour, 0, 0, armour with { Sockets = weapon.Item.Sockets, Links = [1, 1] });
        var source = Game.GemSkillEffects(f.Conn).Single();
        Game.QueueBeastCompanionLocked(f.Conn, Game.SupportedSkill(f.Conn, source.Id), source.Id, null);
        await Game.TickBeastCompanions("test", f.Stream, f.Ciphers, f.Conn, 1, Game.CombatLifetime);
        var beast = Assert.IsType<BeastCompanions.ActiveBeast>(f.Conn.Beasts.Active);
        var target = f.Conn.Monsters[900] = new("target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
        Assert.NotNull(Game.MinionHitTargetLocked(f.Conn, beast.Id, beast.Monster, 900, target, 1,
            new Random(1), checkReach: false, accuracy: 1000000));
        Assert.Equal(-10, Game.MonsterExposure(target, DamageType.Cold, 1));
    }
}
