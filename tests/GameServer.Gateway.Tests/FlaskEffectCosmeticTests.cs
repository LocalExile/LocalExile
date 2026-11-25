using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public class FlaskEffectCosmeticTests
{
    // A pick in a Flask Effect slot sets the running flask buff of its cell again with the cosmetic's BuffVisuals row: the
    // area's buffs write it in place, or on a new instance when its remaining time moved.
    [Fact]
    public void Flask_effect_pick_restyles_the_running_flask_buff_of_its_cell()
    {
        const uint flaskEffectVariation1 = 1863065008;
        var previous = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        var character = "FlaskEffect" + Guid.NewGuid().ToString("N");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var player = new Player(new(1, "1_1_1", 100, 100, 20, 20, null, []), 1, 1) { Character = { Name = character }, PlayerLife = 100 };
            var flask = Game.ItemsOf(player).Add(PlayerInventories.Flask, 2, 0, ItemCatalog.Create(Program.Items.Find("Quicksilver Flask")!));
            var utility = Program.Items.UtilityFlaskOf(Program.Items.Find("Quicksilver Flask")!)!;
            player.UtilityFlaskEffects.Start(new(flask.Id, 2, "test", utility, 7, null, [], 0, 1000));
            ushort shown = player.Area.Buffs.Next;
            player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, 7, utility.BuffRow, 2, 1000));
            player.Area.Buffs.Publish();

            CharacterSaves.SetCosmetic(character, 28, new(0, Hash: flaskEffectVariation1));
            player.Character.WornCosmetics = null;
            Game.RefreshFlaskEffectCosmetic(player, 28);
            ushort instance = player.Area.Buffs.Next;
            player.Area.Buffs.Publish();

            var restyled = player.Area.Buffs.Find(player.Id, shown) ?? player.Area.Buffs.Find(player.Id, instance);
            Assert.Equal(utility.BuffRow, restyled?.Row);
            Assert.Equal((ushort)2, restyled?.SourceSkill);
            Assert.Equal(Game.Art.BuffVisual("flask_effect", 1), restyled?.Visual);
        }
        finally
        {
            PlayerInventories.Forget(character);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previous!);
        }
    }
}
