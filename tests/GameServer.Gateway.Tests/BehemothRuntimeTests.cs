using System.Reflection;
using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

/// <summary>The Vaal engine the Behemoth uses, which needs no mod; the class itself is the phreciaascendancies mod's
/// (LocalExile-Mods), with its tests.</summary>
public sealed class BehemothRuntimeTests
{
    [Fact]
    public void Vaal_commit_with_inspiration_support_gains_charges_on_paid_mana()
    {
        string save = Path.Combine(Path.GetTempPath(), $"behemoth-vaal-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(save);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var player = new Player(new(1, "behemoth", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Behemoth" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            God = true,
            Pools = new(100, 1000, 1000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        player.Clock.Start();
        using var waived = TestRequirements.Waive(player);
        using var statBuff = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
        {
            if (!ReferenceEquals(p, player)) return sources;
            var copy = new Dictionary<uint, int>(sources)
            {
                [Stat.VaalAttackRageCostInsteadOfSoulsPerUse] = 1,
                [Stat.NoInherentRageLoss] = 1,
                [ManaRecoveryRules.RagePer200] = 10,
                [Stat.CannotGainRageDuringSoulGainPrevention] = 0,
            };
            return copy;
        });
        try
        {
            var vaal = Program.Items.FindGem("Vaal Ground Slam")!.Value;
            var inspiration = Program.Items.FindGem("Inspiration Support")!.Value;
            var club = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
            {
                Sockets =
                [
                    new(SocketColour.White, ItemCatalog.Create(vaal.Type) with { GemVariant = vaal.Variant.Index }),
                    new(SocketColour.White, ItemCatalog.Create(inspiration.Type) with { GemVariant = inspiration.Variant.Index }),
                ],
                Links = [2],
            };
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, club);
            var skill = Game.GemSkillEffects(player).Single(g => Game.SkillsFor(player).EffectId(g.Effect) == "VaalGroundSlam");
            Assert.Contains(Game.SupportedSkill(player, skill.Id).Supports, support => support.Effect.Id == "SupportInspiration");
            var rage = new RageState.Inputs(NoInherentLoss: true);
            Assert.True(player.Rage.Gain(30, 0, rage));
            var resources = Game.ResourceEffectsOf(player);
            resources.Mana.Spent(199, 10);
            var plan = (VaalUseReservation)typeof(Game).GetMethod("PlanVaalUse", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [player, skill.Id])!;
            Assert.Equal(0u, plan.Mana); // The native Vaal Ground Slam has no mana cost.
            // Exercise the positive-mana reservation already accepted by the commit path.
            plan = plan with { Mana = 1, PreventionSeconds = 0 };
            uint manaBeforeCommit = player.PlayerMana;
            typeof(Game).GetMethod("CommitVaalUse", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [player, plan, 123d]);
            Assert.Equal(manaBeforeCommit - 1, player.PlayerMana);
            Assert.Equal(0, resources.Mana.SpentRemainder);
            Assert.Equal(1, player.SupportGems.InspirationCharges);
            Assert.Equal(30 - plan.Rage + 10, player.Rage.Read(0, rage).Current);
            Assert.Equal(123, player.NextAttackAt);
        }
        finally
        {
            player.Closed = true;
            lifetime.Stop(_ => { });
            PlayerInventories.Forget(player.Character.Name!);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(save);
        }
    }
}
