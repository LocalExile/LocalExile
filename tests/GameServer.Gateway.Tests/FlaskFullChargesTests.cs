using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FlaskFullChargesTests
{
    [Fact]
    public async Task Standing_full_flask_uses_once_and_rechecks_charges_after_waiting()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var catalog = Program.Items;
        var type = catalog.All.First(t => t.Class == "UtilityFlask" && t.Name == "Granite Flask");
        var utility = catalog.UtilityFlaskOf(type)!;
        var plain = ItemCatalog.Create(type) with { Charges = utility.MaxCharges };
        var trigger = catalog.Mods.ById("FlaskEnchantmentInjectorOnFullCharges__")!.Roll(ModSection.Enchant, new Random(2205));
        var inventory = Game.ItemsOf(player);
        var flask = inventory.Put(PlayerInventories.Flask, new(301, 0, 0, plain with { Mods = [trigger] }));
        inventory.Put(PlayerInventories.Flask, new(302, 1, 0, plain));
        int Held() => inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges;

        await player.FlaskAutomation.UseGate.WaitAsync();
        Task drain;
        try
        {
            Game.QueueFullFlasks(player);
            Game.QueueFullFlasks(player);
            Assert.Equal(flask.Id, Assert.Single(player.FlaskAutomation.Events).Item);
            drain = Game.DrainFlaskTriggers(player);
            Assert.False(drain.IsCompleted);
            // A manual use already held the gate when the automatic use saw a full bar.
            inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges - utility.ChargesPerUse });
        }
        finally { player.FlaskAutomation.UseGate.Release(); }
        await drain;
        Assert.Equal(utility.MaxCharges - utility.ChargesPerUse, Held());
        Assert.Null(player.UtilityFlaskEffects.Of(flask.Id));

        inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges });
        await Game.DrainFlaskTriggers(player);
        await Game.DrainFlaskTriggers(player);
        Assert.Equal(utility.MaxCharges - utility.ChargesPerUse, Held());
        Assert.NotNull(player.UtilityFlaskEffects.Of(flask.Id));
        Assert.Equal(utility.MaxCharges, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == 302).Item.Charges);
    }
}
