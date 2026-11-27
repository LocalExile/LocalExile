namespace GameServer.Gateway.Tests;

public sealed class EviscerateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eviscerate_requires_a_shield_and_uses_the_main_hand_axe_to_deal_damage(bool withShield)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var inventory = Game.ItemsOf(player);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        var axe = ItemCatalog.Create(Program.Items.Find("Rusted Hatchet")!);
        inventory.Change(PlayerInventories.Weapon1, main.Id, _ => axe);
        ushort skill = Assert.Single(fixture.Equip(("Eviscerate", 1)));
        int effect = Game.SupportedSkill(player, skill).Effect;

        if (withShield)
        {
            inventory.Add(PlayerInventories.Offhand1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
            Game.RecomputePoolsAndSpeed(player);
            Assert.Null(Game.WeaponRestricted(player, effect));
            Assert.Null(Game.ShieldWeapon(player, skill));
            Assert.True(Game.HandWeapon(player, AttackHand.Main).MinDamage > 0);
        }
        else
            Assert.NotNull(Game.WeaponRestricted(player, effect));
    }
}
