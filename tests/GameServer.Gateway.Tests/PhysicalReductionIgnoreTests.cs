using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class PhysicalReductionIgnoreTests
{
    [Fact]
    public void General_physical_reduction_ignore_applies_to_hits_and_stacks_with_active_tinctures_for_attacks()
    {
        var target = new SpawnedMonster("armored target", new(10, 10, 0), 0, 0, 1000)
        {
            EncounterStats = new Dictionary<string, int> { ["base_additional_physical_damage_reduction_%"] = 50 }
        };
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
        uint general = Program.Stats.Key("hits_ignore_enemy_monster_physical_damage_reduction_%_chance")!.Value;
        uint tinctured = Program.Stats.Key("hits_ignore_enemy_monster_physical_damage_reduction_%_chance_with_tinctured_weapons")!.Value;
        uint active = Program.Stats.Key("tincture_applied")!.Value;
        int Damage(HitResistances.Mods mods, bool attack) =>
            Game.MonsterHitDamage(target, hit, critical: false, attack, now: 1, mods).Damage;

        Assert.Equal(50, Damage(default, attack: true));
        var razor = new Dictionary<uint, int> { [general] = 35, [tinctured] = 65 };
        var idleTincture = Game.HitResistMods(razor);
        Assert.Equal(35, idleTincture.IgnorePhysicalReductionChance);
        Assert.Equal(0, idleTincture.TincturedWeaponIgnorePhysicalReductionChance);
        razor[active] = 1;
        Assert.Equal(100, Damage(Game.HitResistMods(razor), attack: true));
        Assert.Equal(100, Damage(Game.HitResistMods(new Dictionary<uint, int> { [general] = 100 }), attack: false));
        Assert.Equal(50, Damage(Game.HitResistMods(new Dictionary<uint, int> { [tinctured] = 100, [active] = 1 }),
            attack: false));
    }
}
