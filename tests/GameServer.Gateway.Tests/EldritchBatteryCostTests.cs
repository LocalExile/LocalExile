using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// Eldritch Battery: "Spend Energy Shield before Mana for Skill Mana Costs". A use takes what Energy Shield can pay of its
// Mana cost and the rest from Mana; with neither enough it is refused and nothing is spent.
public sealed class EldritchBatteryCostTests
{
    [Fact]
    public void Eldritch_Battery_pays_mana_costs_from_energy_shield_first()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-eldritch-battery-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var c = new ClientState(new(1, "eldritch-battery", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "EldritchBattery", PlayerLife = 1000, PlayerMana = 0, Instance = new(),
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
            uint cost = (uint)Game.SupportedStats(c, skill.Id).GetValueOrDefault(Program.Stats.Key("mana_cost")!.Value);
            Assert.True(cost > 1);

            // Without the keystone, Energy Shield pays nothing: no Mana, no use.
            c.EnergyShield.SetMaximum(1000, refill: true);
            Assert.False(Game.CanPaySkillCost(c, skill.Id));

            // With it, Energy Shield pays first: less than the cost in it, the rest from Mana.
            c.PlayerStats[Program.Stats.Key("keystone_eldritch_battery")!.Value] = 1;
            c.EnergyShield.SetMaximum(cost - 1, refill: true);
            c.PlayerMana = 0;
            Assert.False(Game.CanPaySkillCost(c, skill.Id));
            c.PlayerMana = 5;
            Assert.True(Game.PayMana(c, skill.Id, out uint paid, out _));
            Assert.Equal(0u, c.EnergyShield.Current);
            Assert.Equal(1u, paid);
            Assert.Equal(4u, c.PlayerMana);

            // Enough Energy Shield pays it all and leaves Mana alone.
            c.EnergyShield.SetMaximum(cost * 2, refill: true);
            Assert.True(Game.PayMana(c, skill.Id, out paid, out _));
            Assert.Equal(cost, c.EnergyShield.Current);
            Assert.Equal(0u, paid);
            Assert.Equal(4u, c.PlayerMana);
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
