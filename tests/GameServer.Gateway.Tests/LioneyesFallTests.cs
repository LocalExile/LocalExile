using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class LioneyesFallTests
{
    [Fact]
    public void Lioneyes_fall_transforms_the_allocated_melee_passive_only_inside_its_radius()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lioneyes-fall-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var conn = new ClientState(new(1, "bow", 100, 100, 100, 100, null, []), 1, 1) { Character = "BowConversion" };
        try
        {
            var tree = Program.Passives;
            var items = Program.Items;
            var jewel = new UniqueCatalog(items).Create("Lioneye's Fall", new Random(3))!;
            var conversion = RadiusJewelCatalogue.ReadConversion(jewel, items.Mods)!;
            var location = (from i in Enumerable.Range(0, 60)
                            let socket = tree.JewelNodeForSlot(i)
                            where socket is not null && tree.ClusterNodeForSlot(i) is null
                            from id in tree.NodesInRadius(socket.Value, conversion.Radius)
                            let node = tree.Find(id)!
                            where !node.IsAscendancy && !node.IsMastery
                            from stat in node.Stats
                            from move in conversion.Moves
                            where move.From == stat.Key && stat.Value > 0
                            select (Slot: i, Socket: socket.Value, Node: id, Move: move)).First();
            var point = location.Node;
            var source = location.Move.From;
            var target = location.Move.To;
            Dictionary<uint, int> Read(params ushort[] nodes) => Game.EquipmentStatSources(conn, [], nodes, 100).Sources;
            var before = Read(point, location.Socket);
            int original = before.GetValueOrDefault(source);
            Assert.True(original > 0);
            Game.ItemsOf(conn).Put(PlayerInventories.PassiveJewels, new(11, (byte)location.Slot, 0, jewel));
            var converted = Read(point, location.Socket);
            Assert.Equal(0, converted.GetValueOrDefault(source));
            Assert.True(converted.GetValueOrDefault(target) >= before.GetValueOrDefault(target) + original);
            Assert.Equal(original, Read(point).GetValueOrDefault(source));
        }
        finally { PlayerInventories.Forget(conn.Character!); TestEnvironment.ReleaseSaves(path); }
    }

    [Fact]
    public void Bow_damage_follows_a_physical_hit_through_conversion_and_excludes_other_weapons()
    {
        int Get(string n) => n switch { "physical_bow_damage_+%" => 10, "bow_damage_+%" => 20,
            "bow_hit_and_ailment_damage_+%" => 30, "bow_elemental_damage_+%" => 40, _ => 0 };
        var packet = DamagePacket.Of(DamageType.Physical, 100, 100);
        var conversion = new DamageConversion();
        conversion.AddSkill(DamageType.Physical, DamageType.Fire, 100);
        double Hit(bool bow, bool spell) => PlayerHitDamage.Resolve(packet, Get, conversion, spell, projectile: true,
            wieldingBow: bow)[DamageType.Fire].Min;
        Assert.Equal(200, Hit(true, false));
        Assert.Equal(100, Hit(false, false));
        Assert.Equal(100, Hit(true, true));

        int Ignite(bool bow) => AilmentPanelStats.Compute(new("main_hand", Spell: false),
            packet, conversion, n => n == "main_hand_weapon_type" ? WeaponTypeStats.Of(bow ? "Bow" : "Wand") : Get(n)).Single(row => row.Stat == "minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage").Value;
        Assert.Equal(2 * Ignite(false), Ignite(true));
        Assert.True(Ignite(false) > 0);
    }
}
