using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// An Instilling "Used when Charges reach full" flask that STANDS at its maximum charges is queued for use, not only
// one that rises to it: the official captures of a Pathfinder with five such flasks enter the area with all five at
// their maximum and use all five together shortly after the player's first input, before the first hit on a monster
// and before the first kill, so with no charge change at all. This test checks the queueing, not the use itself:
// a drain needs live game work, which the test harness has no way to open.
public class FlaskFullChargesTests
{
    [Fact]
    public void A_flask_standing_at_full_charges_is_queued_once_and_a_plain_flask_is_not()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flask-full-{Guid.NewGuid():N}.db");
        string name = "FlaskFull" + Guid.NewGuid().ToString("N");
        Saves.Open(path);
        var player = new Player(new(1, name, 100, 100, 100, 100, null, []), 1, 1) { Character = { Name = name } };
        try
        {
            var items = Program.Items;
            var type = items.All.First(t => t.Class == "UtilityFlask" && t.Name == "Granite Flask");
            var utility = items.UtilityFlaskOf(type)!;
            var injector = items.Mods.ById("FlaskEnchantmentInjectorOnFullCharges__")!;
            var plain = ItemCatalog.Create(type) with { Charges = utility.MaxCharges };
            var enchanted = plain with { Mods = [injector.Roll(ModSection.Enchant, new Random(2205))] };
            Assert.True(FlaskEnchantments.Matches(ItemModStats.Local(enchanted, items.Mods),
                FlaskEnchantments.Trigger.Full, 0, 0, active: false), "the test item does not carry the trigger");

            var bar = Game.ItemsOf(player);
            var flask = bar.Put(PlayerInventories.Flask, new(301, 0, 0, enchanted));
            Assert.Equal(utility.MaxCharges, flask.Item.Charges);
            // No charge change at all: the flask has stood at its maximum since it was put on the bar.
            Game.QueueFullFlasks(player);
            var signal = Assert.Single(player.FlaskAutomation.Events);
            Assert.Equal(FlaskEnchantments.Trigger.Full, signal.Trigger);
            Assert.Equal(flask.Id, signal.Item);
            // A standing condition is checked again on the next drain, but the same signal is already in flight:
            // it queues one use, not one per tick.
            Game.QueueFullFlasks(player);
            Assert.Single(player.FlaskAutomation.Events);

            // The same flask without the enchantment is left alone, so the standing condition is the enchantment's.
            player.FlaskAutomation.Discard();
            bar.Remove(PlayerInventories.Flask, flask.Id);
            bar.Put(PlayerInventories.Flask, new(302, 0, 0, plain));
            Game.QueueFullFlasks(player);
            Assert.Empty(player.FlaskAutomation.Events);
        }
        finally { PlayerInventories.Forget(name); TestEnvironment.ReleaseSaves(path); }
    }
}
