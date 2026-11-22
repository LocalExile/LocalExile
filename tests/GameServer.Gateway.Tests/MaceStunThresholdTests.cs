using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MaceStunThresholdTests
{
    [Fact]
    public void Skull_cracking_reduces_stun_threshold_for_the_weapon_used()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        var inventory = Game.ItemsOf(conn);
        uint weapon = Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id;
        var mace = Program.Items.Find("Driftwood Maul")!;
        inventory.Change(PlayerInventories.Weapon1, weapon, _ => ItemCatalog.Create(mace));
        var target = new SpawnedMonster("stun target", new(10, 10, 0), 0, 0, 1000);
        var damage = DamagePacket.Of(DamageType.Physical, 1000, 1000);
        var stats = new Dictionary<uint, int>();
        int Threshold(string? actualWeapon, bool spell) =>
            Game.OwnerStun(conn, target, 1000, damage, melee: !spell, spell, critical: false, stats, null,
                actualWeapon).ThresholdReductionPct;

        int baseline = Threshold(null, spell: false);
        int spellBaseline = Threshold(null, spell: true);
        stats[Program.Stats.Key("while_using_mace_stun_threshold_reduction_+%")!.Value] = 12;
        Assert.Equal(baseline + 12, Threshold(null, spell: false));
        Assert.Equal(baseline + 12, Threshold("Sceptre", spell: false));
        Assert.Equal(baseline, Threshold("One Hand Sword", spell: false));
        Assert.Equal(spellBaseline, Threshold(null, spell: true));
    }
}
