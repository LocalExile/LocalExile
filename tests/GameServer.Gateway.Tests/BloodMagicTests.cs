using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BloodMagicTests
{
    [Theory]
    [InlineData("SkillGemBloodreap", 0, 25)]
    [InlineData("SkillGemBloodreap", 5, 50)]
    [InlineData("SkillGemExsanguinate", 0, 16)]
    [InlineData("SkillGemCorruptingFever", 0, 78)]
    public void Native_life_cost_is_required_before_admission_and_paid_once(string gemName, int charges, uint expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"native-life-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = SpellTotemExecutionTests.PlayerOf(gemName);
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + gemName)!);
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new(SocketColour.White, gem)], Links = [1],
            });
            var skill = Game.GemSkillEffects(player).Single();
            player.BloodCharges = charges;
            player.PlayerLife = expected;
            Assert.False(Game.CanPaySkillCost(player, skill.Id));
            Assert.False(Game.PayMana(player, skill.Id, out _, out _));
            Assert.Equal(expected, player.PlayerLife);
            player.PlayerLife++;
            Assert.True(Game.CanPaySkillCost(player, skill.Id));
            Assert.True(Game.PayMana(player, skill.Id, out uint mana, out uint life));
            Assert.Equal(expected, life);
            Assert.Equal(0u, mana);
            Assert.Equal(1u, player.PlayerLife);
            Assert.Equal(1000u, player.PlayerMana);
        }
        finally
        {
            PlayerInventories.Forget(gemName);
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Reap_gains_a_charge_only_when_no_hit_enemy_dies()
    {
        Assert.Equal(1, BloodCharges.Settle(2, 1, 1, 5));
        Assert.Equal(3, BloodCharges.Settle(2, 0, 2, 5));
        Assert.Equal(2, BloodCharges.Settle(2, 0, 0, 5));
    }

    // The Blood Magic keystone turns a skill's whole Mana cost into a Life cost: Tornado Shot's 9 Mana is paid as
    // 9 Life, Mana stays full, and no Lifetap buff comes with it (that buff belongs to the support gem).
}
