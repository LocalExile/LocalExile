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
        HitWith(DamagePacket.Of(DamageType.Physical, 100, 100));
        Assert.Empty(target.Exposures);
        fighter.Own.Remove(Program.Stats.Key("minions_inflict_exposure_on_hit_%_chance")!.Value);
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
}
