using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class IceShotProjectileTests
{
    [Fact]
    public async Task Ice_shot_resolves_its_primary_arrow_and_publishes_the_equipped_cone_modifiers()
    {
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        int effect = skills.EffectRow("IceShot");
        Assert.True(effect >= 0 && skills.IsProjectile(effect));
        var projectile = skills.ProjectileFor(effect);
        Assert.NotNull(projectile);
        Assert.Equal("Metadata/Projectiles/IceArrow", projectile.Value.Id);
        Assert.Equal(3260, projectile.Value.Speed);
        var hit = SkillHitPlan.ForSkill(skills, effect, 1);
        Assert.True(hit.CanHit);
        Assert.Equal(SkillHitKind.Attack, hit.Kind);
        Assert.Contains(skills.EffectStats(effect, 1), s =>
            s.Item1 == stats.Key("skill_physical_damage_%_to_convert_to_cold") && s.Item2 == 60);
        var definition = Game.CatalogProjectileDefinition(effect);
        Assert.NotNull(definition);
        Assert.Equal(projectile.Value.Id, definition.Metadata);
        var profile = definition.Parameters.Resolve(key => key == "number_of_additional_arrows" ? 3 : 0, 326, true);
        Assert.Equal(4, profile.Count);

        using var battle = new ArrowRainAndMirageTests.Battle("Ice Shot", "Fireball");
        var items = Game.ItemsOf(battle.Conn);
        var bow = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with
        {
            Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = 4 } }, item.Sockets[1]]
        });
        var gems = Game.GemSkillEffects(battle.Conn).ToDictionary(g => g.Name);
        uint angle = stats.Key("skill_area_angle_+%")!.Value;
        uint radius = stats.Key("active_skill_area_of_effect_radius")!.Value;
        var helmet = ItemCatalog.Create(Program.Items.Find("Iron Hat")!);

        foreach (var (enchant, expectedAngle, expectedRadius) in new (string?, int, int)[]
        {
            ("EnchantmentIceShotConeAngle1", 46, 24), ("EnchantmentIceShotConeAngle2", 76, 24),
            ("EnchantmentIceShotRadius1", 16, 25), ("EnchantmentIceShotRadius2_", 16, 26), (null, 16, 24)
        })
        {
            if (items.Items(PlayerInventories.Helmet).SingleOrDefault() is { } worn)
                items.Remove(PlayerInventories.Helmet, worn.Id);
            if (enchant is not null)
            {
                var mod = Program.Items.Mods.ById(enchant)!;
                items.Add(PlayerInventories.Helmet, 0, 0, helmet with { Mods = [new ItemMod(ModSection.Enchant, mod.Hash, mod.MaxValues())] });
            }
            await Game.SendEquipment("ice-shot", battle.Stream, battle.Ciphers, battle.Conn, items);
            var published = Game.PlayerSkills(battle.Conn);
            var ice = published.Single(s => s.Id == gems["Ice Shot"].Id).Stats!;
            Assert.Equal(expectedAngle, ice.Single(s => s.Key == angle).Value);
            Assert.Equal(expectedRadius, ice.Single(s => s.Key == radius).Value);
            var fireball = published.Single(s => s.Id == gems["Fireball"].Id).Stats!;
            Assert.DoesNotContain(fireball, s => s.Key == angle && s.Value != 0);
            Assert.Equal(10, fireball.Single(s => s.Key == radius).Value);
        }
    }
}
