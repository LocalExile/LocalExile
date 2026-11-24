using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class FortificationAurasAndUniquesTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Fact]
    public void Fortification_Champion_Kingmaker_and_uniques_apply_across_player_and_minions()
    {
        using (var f = new MinionUtilityTests.Fixture())
        {
            var player = f.Player;
            var stats = new Dictionary<uint, int>
            {
                [Key("you_have_your_maximum_fortification")] = 1,
                [FortificationState.MaximumStat] = 10,
                [Key("attack_speed_+%_per_fortification_above_20")] = 3,
                [Key("armour_evasion_rating_+%_final_per_fortification_above_20")] = 3,
                [Key("spell_suppression_chance_%_per_fortification")] = 4,
            };
            var eval = Game.EvaluateFortificationLocked(player, 0, stats);
            Assert.Equal(30, eval.Value.Current);
            Assert.Equal(30, eval.Value.Maximum);
            Assert.Null(eval.Value.LatestExpiry);
            Assert.Equal(30, eval.Above20AttackSpeedPct);
            Assert.Equal(30, eval.Above20DefenceMorePct);
            Assert.Equal(120, eval.SuppressionPct);

            // Normal Fortification reduces hit damage by Current%; Willowgift ("should_use_alternate_fortify") disables it
            Assert.Equal(700, Game.FortificationDamageTakenLocked(player, 1000, 0, stats));
            stats[Key("should_use_alternate_fortify")] = 1;
            Assert.Equal(1000, Game.FortificationDamageTakenLocked(player, 1000, 0, stats));

            // FortificationAllyPlayerStats populates current_fortification & uncapped_current_fortification without legacy has_fortify
            var allyStats = Game.FortificationAllyPlayerStats(player, stats);
            Assert.Equal(30, allyStats[FortificationSession.Current]);
            Assert.Equal(30, allyStats[FortificationSession.Uncapped]);
            Assert.False(allyStats.ContainsKey(Key("has_fortify")));

            // CharacterStats.Bonus ExtraDefenceMorePct multiplies Armour & Evasion cleanly
            var withHero = CharacterStats.For(Program.Skills.ClassFor(0), 80, new CharacterStats.Bonus(
                Armour: 1000, ArmourMorePct: 50, ExtraDefenceMorePct: 30), (_, _) => 1000);
            var basePools = CharacterStats.For(Program.Skills.ClassFor(0), 80, new CharacterStats.Bonus(
                Armour: 1000, ArmourMorePct: 50), (_, _) => 1000);
            Assert.Equal((int)Math.Round(basePools.Armour * 1.3m, MidpointRounding.AwayFromZero), withHero.Armour);
        }

        // Perseverance (+1% attack damage per 300 of min(Armour, Evasion))
        Assert.Equal(15, ArmourAttackDamage.Total(10, finalArmour: 2400, per450Armour: 0, finalEvasion: 1500, per450Evasion: 0, per300Min: 1));

        // Kingmaker reading
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items);
        var kingmaker = uniques.Create("Kingmaker", new Random(17));
        Assert.NotNull(kingmaker);
        var km = Game.ReadKingmaker([kingmaker], items.Mods);
        Assert.Equal(10, km.Fortify);
        Assert.True(km.Culling);
        Assert.Equal(50, km.CritMulti);
        Assert.Equal(30, km.Iir);
    }
}
