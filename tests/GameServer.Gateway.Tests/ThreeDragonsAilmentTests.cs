using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Kind = MonsterElementalAilments.Kind;

namespace GameServer.Gateway.Tests;

public sealed class ThreeDragonsAilmentTests
{
    [Fact]
    public void Three_dragons_reassigns_each_elements_ailments_only_while_worn()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        fixture.Equip(("Ice Nova", 1), ("Fireball", 1));
        ushort coldSkill = Game.GemSkillEffects(conn).Single(skill => skill.Name == "Ice Nova").Id;
        ushort fireSkill = Game.GemSkillEffects(conn).Single(skill => skill.Name == "Fireball").Id;
        DamagePacket Hit(DamageType type) => DamagePacket.Of(type, 100, 100);
        double Ailment(DamageType type, Kind kind)
        {
            var target = new SpawnedMonster("ailment target", new(100, 100, 0), 0, 0, 100);
            Game.AilmentsOnHitLocked(conn, 1, target, Hit(type), spell: true, critical: true, 150,
                Game.CharacterPlayerStats(conn), null, 1, new Random(1));
            return target.Ailments.Magnitude(kind, 1);
        }
        var items = Game.ItemsOf(conn);
        var helmet = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("The Three Dragons", new Random(11)));
        items.Put(PlayerInventories.Helmet, new(100, 0, 0, helmet));
        var stats = Game.CharacterPlayerStats(conn);
        Assert.True(Ailment(DamageType.Fire, Kind.Shock) > 0);
        Assert.Equal(0, Ailment(DamageType.Cold, Kind.Freeze));
        Assert.True(Ailment(DamageType.Lightning, Kind.Freeze) > 0);
        Assert.Equal(0, Ailment(DamageType.Lightning, Kind.Shock));
        var cold = Game.SupportedIgnite(conn, coldSkill, spell: true, stats);
        Assert.True(cold.CanIgnite(Hit(DamageType.Cold)) && cold.CriticalMax > 0);
        var fire = Game.SupportedIgnite(conn, fireSkill, spell: true, stats);
        Assert.False(fire.CanIgnite(Hit(DamageType.Fire)));

        items.Remove(PlayerInventories.Helmet, 100);
        Assert.Equal(0, Ailment(DamageType.Fire, Kind.Shock));
        Assert.True(Ailment(DamageType.Cold, Kind.Freeze) > 0);
    }
}
