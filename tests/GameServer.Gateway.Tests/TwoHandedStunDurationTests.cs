using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class TwoHandedStunDurationTests
{
    [Fact]
    public void Wrecking_ball_extends_an_actual_two_handed_melee_stun()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        var inventory = Game.ItemsOf(conn);
        uint weapon = Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id;
        var stats = new Dictionary<uint, int> { [Program.Stats.Key("two_handed_melee_stun_duration_+%")!.Value] = 40 };
        var damage = DamagePacket.Of(DamageType.Physical, 1000, 1000);

        double Duration(string itemName, bool melee)
        {
            var type = Program.Items.Find(itemName)!;
            inventory.Change(PlayerInventories.Weapon1, weapon, _ => ItemCatalog.Create(type));
            var target = new SpawnedMonster("stun target", new(10, 10, 0), 0, 0, 1000)
                { HitAnimations = [new(0, 2)] };
            lock (conn.Monsters)
                Assert.True(HitReaction.TryFlinch(target,
                    Game.OwnerStun(conn, target, 1000, damage, melee, spell: false, critical: false, stats, null), 20, new Random(11)));
            return target.FlinchUntil - 20;
        }

        Assert.Equal(.735, Duration("Driftwood Maul", melee: true), 6);
        Assert.Equal(.525, Duration("Rusted Sword", melee: true), 6);
        Assert.Equal(.350, Duration("Crude Bow", melee: false), 6);
    }
}
