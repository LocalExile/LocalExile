using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// A cooldown skill's use pays its cost when it is accepted, with its cooldown use, as the client pays at the start of the
// use: an underfunded use spends nothing and moves no one, and an accepted one cannot be left spent and unpaid.
public sealed class CooldownPaymentTests
{
    [Fact]
    public void A_cooldown_use_pays_when_it_is_accepted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-cooldown-pay-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var c = new ClientState(new(1, "cooldown-pay", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "CooldownPayment", PlayerLife = 1000, PlayerMana = 0, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var club = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFlickerStrike")!);
            Game.ItemsOf(c).Add(PlayerInventories.Weapon1, 0, 0, club with { Sockets = [new ItemSocket(SocketColour.Green, gem)], Links = [1] });
            foreach (var name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) c.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var skill = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).EffectId(g.Effect) == "FlickerStrike");
            var plan = Game.CooldownFor(c, skill.Effect, skill.Level, skill.Quality);
            Assert.NotNull(plan);
            uint cost = (uint)Game.SupportedStats(c, skill.Id).GetValueOrDefault(Program.Stats.Key("mana_cost")!.Value);
            Assert.True(cost > 0);

            // Underfunded: refused before the cooldown use or the side effects (Flicker Strike's teleport).
            Assert.False(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, plan, false,
                () => throw new Exception("An underfunded use must not move the player"), out _));
            Assert.Empty(c.SingleUseCooldowns);

            // Funded: the cost is paid with the accepted use, and the ticket carries it so the cast does not pay twice.
            c.PlayerMana = 1000;
            int moved = 0;
            Assert.True(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, plan, false, () => moved++, out var ticket));
            Assert.Equal(1, moved);
            Assert.Equal(1000 - cost, c.PlayerMana);
            Assert.Equal((cost, 0u), ticket!.Paid);

            // Cooling down with no charge to bypass it: refused, and nothing is paid.
            Assert.False(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect, plan, false,
                () => throw new Exception("A refused use must not move the player"), out _));
            Assert.Equal(1000 - cost, c.PlayerMana);
        }
        finally
        {
            c.Closed = true; c.RetireOwnedCombat(); lifetime.Stop(ex => throw ex);
            PlayerInventories.Forget(c.Character!);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
