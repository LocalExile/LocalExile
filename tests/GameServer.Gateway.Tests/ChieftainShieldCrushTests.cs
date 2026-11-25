using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ChieftainShieldCrushTests
{
    [Fact]
    public void Chieftain_shield_crush_adds_fire_damage_from_the_equipped_shields_armour()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip(("Shield Crush of the Chieftain", 20), ("Shield Crush", 20));
        var items = Game.ItemsOf(player);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Remove(PlayerInventories.Weapon1, host.Id);
        Assert.NotNull(items.Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Rusted Sword")!)));
        var shieldType = Program.Items.Find("Colossal Tower Shield")!;
        var shield = ItemCatalog.Create(shieldType) with { Sockets = host.Item.Sockets, Links = host.Item.Links };
        Assert.NotNull(items.Add(PlayerInventories.Offhand1, 0, 0, shield));
        var baseDefences = Program.Items.Defences(shieldType)!.Value;
        var (armour, _, _) = ItemDefences.Calculate(shield, baseDefences, Program.Items.Mods);
        var armourSteps = (int)(armour / 15);
        Assert.True(armourSteps > 0);

        var skills = Game.GemSkillEffects(player).ToDictionary(g => g.Name, g => g.Id);
        var chieftain = Game.ShieldWeapon(player, skills["Shield Crush of the Chieftain"])!;
        var regular = Game.ShieldWeapon(player, skills["Shield Crush"])!;
        var expected = new DamageRange(389 + 16 * armourSteps, 583 + 24 * armourSteps);
        Assert.Equal(expected, (chieftain.LocalDamage ?? DamagePacket.Empty)[DamageType.Fire]);
        Assert.Equal(DamageRange.Zero, (regular.LocalDamage ?? DamagePacket.Empty)[DamageType.Fire]);

        var raw = Game.SupportedHitInputsFor(player, skills["Shield Crush of the Chieftain"], weapon: chieftain)!.Raw;
        Assert.True(raw[DamageType.Fire].Min > 0);
    }
}
