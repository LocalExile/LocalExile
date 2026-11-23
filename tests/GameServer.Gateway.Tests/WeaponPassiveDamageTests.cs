using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WeaponPassiveDamageTests
{
    [Fact]
    public void A_mace_passive_increases_the_matching_elemental_attack_and_excludes_a_sword()
    {
        double Read(string weapon, bool passive)
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, host, item => ItemCatalog.Create(Program.Items.Find(weapon)!)
                with { Sockets = item.Sockets, Links = item.Links });
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.AttackMinimumAddedLightningDamage] = values[Stat.AttackMaximumAddedLightningDamage] = 700;
                if (passive) values[Stat.MaceElementalDamagePlusPct] = 25;
                return values;
            });
            ushort skill = Assert.Single(fixture.Equip(("Heavy Strike", 1)));
            return Game.SupportedHitFor(player, skill, suppress: false)[DamageType.Lightning].Max;
        }

        double mace = Read("Driftwood Club", passive: false);
        Assert.True(mace > 0);
        Assert.Equal(mace * 1.25, Read("Driftwood Club", passive: true), 7);
        Assert.Equal(Read("Rusted Sword", passive: false), Read("Rusted Sword", passive: true), 7);
    }

    [Fact]
    public void Attack_ignite_chance_only_applies_to_attacks()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.AttackIgniteChancePct] = 37;
            values[Stat.AttackMinimumAddedFireDamage] = values[Stat.AttackMaximumAddedFireDamage] = 700;
            return values;
        });
        ushort attack = Assert.Single(f.Equip(("Heavy Strike", 1)));
        var character = Game.CharacterPlayerStats(player);
        Assert.Equal(37, Game.SupportedIgnite(player, attack, spell: false, character).Chance);

        ushort spell = Assert.Single(f.Equip(("Ice Nova", 1)));
        Assert.Equal(0, Game.SupportedIgnite(player, spell, spell: true, character).Chance);
    }
}
