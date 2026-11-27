using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class FoulgraspTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recalling_supported_brands_spends_the_companion_cooldown_once(bool hungryLoop)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var at = (X: 100, Y: 100);
        player.Use(new(1, "foulgrasp-test", 240, 240, at.X, at.Y, (_, _) => true, []));
        player.Walk.Teleport(at.X, at.Y, Game.AreaTime(player));
        player.ProjectileRuntimeState = fixture.Session;
        player.Connection.PlayerObjectCreated = true;

        if (hungryLoop)
        {
            fixture.Equip(("Brand Recall", 1));
            var (baseType, variant) = Program.Items.FindGem("Wintertide Brand")!.Value;
            var brandGem = ItemCatalog.Create(baseType) with { GemVariant = variant.Index };
            var support = Program.Items.Mods.All.Single(m => m.Id == "HungryLoopSupportedByFoulgrasp");
            var ring = ItemCatalog.Create(Program.Items.Find("Unset Ring")!) with
            {
                Sockets = [new(SocketColour.White, brandGem)],
                Links = [1],
                Mods = [new(ModSection.Explicit, support.Hash, [1, 1, 3])],
            };
            Game.ItemsOf(player).Add(PlayerInventories.Ring1, 0, 0, ring);
        }
        else fixture.Equip(("Wintertide Brand", 1), ("Foulgrasp Support", 1), ("Brand Recall", 1));

        var skills = Game.GemSkillEffects(player).ToDictionary(s => Game.SkillsFor(player).EffectId(s.Effect)!);
        var recall = skills["BrandRecall"];
        var brand = skills["WintertideBrand"];
        var foulgrasp = skills["TriggeredSupportFoulgrasp"];
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, foulgrasp.Effect, foulgrasp.Id));
        Assert.Null(Game.GemSkillInstanceDisabled(player, foulgrasp.Effect, foulgrasp.Id, triggered: true));
        Assert.Null(Game.SingleUseState(player, foulgrasp.Effect, foulgrasp.Id));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            for (int i = 0; i < 2; i++)
                Assert.True(await Game.ResolveCastBrand(player, brand.Id, brand, at, player.ActionInterruptionGeneration, () => true));
            Assert.Equal(2, Game.BrandCountFor(player));

            Assert.True(await Game.CastBrandRecall(player, recall.Id, recall, () => true));
            var cooldown = Game.SingleUseState(player, foulgrasp.Effect, foulgrasp.Id);
            Assert.NotNull(cooldown);
            Assert.Equal(1000, cooldown.TotalBaseMilliseconds);
            Assert.Equal(1, cooldown.SpentUses);
            Assert.True(await Game.CastBrandRecall(player, recall.Id, recall, () => true));
            Assert.Equal(1, cooldown.UseVersion);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
