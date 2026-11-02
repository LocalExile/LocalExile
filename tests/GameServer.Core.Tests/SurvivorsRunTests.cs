using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class SurvivorsRunTests
{
    [Fact]
    public void The_horde_grows_from_fourteen_to_the_cap()
    {
        Assert.Equal(14, SurvivorsRules.TargetAlive(0));
        Assert.Equal(67, SurvivorsRules.TargetAlive(450));
        Assert.Equal(120, SurvivorsRules.TargetAlive(900));
        Assert.Equal(120, SurvivorsRules.TargetAlive(9000));
        Assert.Equal(14, SurvivorsRules.TargetAlive(0, maxAlive: 25));
        Assert.Equal(25, SurvivorsRules.TargetAlive(900, maxAlive: 25));
    }

    [Fact]
    public void The_roster_unlocks_over_the_run()
    {
        Assert.Equal(1, SurvivorsRules.UnlockedCount(0, 10));
        Assert.Equal(6, SurvivorsRules.UnlockedCount(450, 10));
        Assert.Equal(10, SurvivorsRules.UnlockedCount(900, 10));
        Assert.Equal(0, SurvivorsRules.UnlockedCount(900, 0));
        Assert.Equal(3, SurvivorsRules.UnlockedCount(9000, 3));
    }

    [Fact]
    public void A_run_skill_grows_in_damage_and_speed_with_its_rank()
    {
        Assert.Equal(1.2, SurvivorsRules.SkillInterval(1), 6);
        Assert.Equal(1.116, SurvivorsRules.SkillInterval(2), 6);
        Assert.Equal(0.2, SurvivorsRules.SkillInterval(100), 6);
        Assert.Equal(20, SurvivorsRules.SkillDamage(20, 1));
        Assert.Equal(26, SurvivorsRules.SkillDamage(20, 2));
        Assert.Equal(32, SurvivorsRules.SkillDamage(20, 3));
        Assert.Equal(1, SurvivorsRules.SkillDamage(0, 1));
    }

    [Fact]
    public void A_run_skill_gains_a_projectile_at_ranks_three_five_and_seven()
    {
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], Enumerable.Range(1, SurvivorsRules.MaxRank).Select(SurvivorsRules.SkillProjectiles));
        // Its reach grows 8% a rank, before the run's area blessings.
        Assert.Equal(1, SurvivorsRules.SkillRadius(1), 6);
        Assert.Equal(1.16, SurvivorsRules.SkillRadius(3), 6);
        Assert.Equal(1.56, SurvivorsRules.SkillRadius(8), 6);
    }

    [Fact]
    public void Monsters_grow_tougher_and_hit_harder_over_a_run()
    {
        Assert.Equal(1, SurvivorsRules.MonsterLifeScale(0), 6);
        Assert.Equal(1.75, SurvivorsRules.MonsterLifeScale(900), 6);
        Assert.Equal(1, SurvivorsRules.MonsterDamageScale(0), 6);
        Assert.Equal(1.9, SurvivorsRules.MonsterDamageScale(900), 6);
        Assert.Equal(1.75, SurvivorsRules.MonsterLifeScale(9000), 6);
        Assert.Equal(1.9, SurvivorsRules.MonsterDamageScale(9000), 6);
    }

    [Fact]
    public void A_run_hit_reaches_the_spawn_ring()
    {
        Assert.Equal(34, SurvivorsRules.Range, 6);
    }

    [Fact]
    public void Incoming_monster_hits_are_quartered_and_fortitude_softens_them()
    {
        Assert.Equal(0, SurvivorsRules.IncomingDamage(0));
        Assert.Equal(1, SurvivorsRules.IncomingDamage(3));
        Assert.Equal(1, SurvivorsRules.IncomingDamage(7));
        Assert.Equal(5, SurvivorsRules.IncomingDamage(20));
        Assert.Equal(2, SurvivorsRules.IncomingDamage(20, 10));
        Assert.Equal(25, new SurvivorsRun().IncomingDamagePercent);
    }

    [Fact]
    public void Experience_follows_monster_rarity()
    {
        Assert.Equal(2, SurvivorsRules.ExperienceForKill(MonsterRarity.Normal));
        Assert.Equal(4, SurvivorsRules.ExperienceForKill(MonsterRarity.Magic));
        Assert.Equal(10, SurvivorsRules.ExperienceForKill(MonsterRarity.Rare));
        Assert.Equal(50, SurvivorsRules.ExperienceForKill(MonsterRarity.Unique));
    }

    [Fact]
    public void Kills_level_the_run_and_queue_choices()
    {
        var run = new SurvivorsRun();
        // A normal kill is worth 2: the first level needs 5, so the third kill levels the run.
        for (int i = 0; i < 2; i++) Assert.Equal(0, run.RecordKill(MonsterRarity.Normal));

        Assert.Equal(1, run.Level);
        Assert.Equal(1, run.RecordKill(MonsterRarity.Normal));
        Assert.Equal(2, run.Level);
        Assert.Equal(1, run.Experience);
        Assert.Equal(1, run.PendingChoices);
        Assert.True(run.NeedsOffer);
    }

    [Fact]
    public void A_unique_kill_can_grant_several_levels_at_once()
    {
        var run = new SurvivorsRun();

        Assert.Equal(3, run.RecordKill(MonsterRarity.Unique));

        Assert.Equal(4, run.Level);
        Assert.Equal(5, run.Experience);
        Assert.Equal(3, run.PendingChoices);
    }

    [Fact]
    public void An_offer_holds_three_distinct_new_skills()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        run.RecordKill(MonsterRarity.Unique);

        var offer = run.DealOffer(new Random(1), 8, 8, 8);

        Assert.Equal(3, offer.Count);
        Assert.All(offer, c => Assert.Equal(SurvivorsChoiceKind.NewSkill, c.Kind));
        Assert.Equal(3, offer.Select(c => c.Index).Distinct().Count());
        Assert.DoesNotContain(offer, c => c.Index == 0);
        Assert.Equal(offer, run.Offer);
        Assert.False(run.NeedsOffer);
    }

    [Fact]
    public void Choosing_a_new_skill_learns_it_and_closes_the_offer()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        run.RecordKill(MonsterRarity.Unique);
        var offer = run.DealOffer(new Random(7), 8, 8, 8);

        Assert.True(run.TryChoose(2, out var chosen));

        Assert.Equal(offer[1], chosen);
        Assert.Equal(1, run.RankOf(chosen.Index));
        Assert.Equal(2, run.PendingChoices);
        Assert.Empty(run.Offer);
        Assert.True(run.NeedsOffer);
    }

    [Fact]
    public void A_full_skill_bar_is_offered_supports_for_its_skills()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++) run.Learn(skill);
        run.RecordKill(MonsterRarity.Unique);

        var offer = run.DealOffer(new Random(5), 8, 8, 8);

        Assert.Equal(3, offer.Count);
        Assert.All(offer, c => Assert.Equal(SurvivorsChoiceKind.Support, c.Kind));
        Assert.True(run.TryChoose(1, out var chosen));
        Assert.Contains(chosen.Index, run.SupportsOf(chosen.Target));
    }

    [Fact]
    public void An_offer_only_pairs_a_support_with_a_skill_the_rule_allows()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        run.Learn(1);
        run.RecordKill(MonsterRarity.Unique);
        var allowed = new HashSet<(int Skill, int Support)> { (0, 2), (1, 1) };

        var offer = run.DealOffer(new Random(1), 2, 0, 4, null, (skill, support) => allowed.Contains((skill, support)));

        var supports = offer.Where(c => c.Kind == SurvivorsChoiceKind.Support).ToList();
        Assert.Equal(2, supports.Count);
        Assert.All(supports, c => Assert.Contains((c.Target, c.Index), allowed));
    }

    [Fact]
    public void Blessings_change_the_run_numbers()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++) run.Learn(skill);
        run.RecordKill(MonsterRarity.Unique);

        // A catalogue of one blessing always offers that blessing (Might), whatever the shuffle does.
        var offer = run.DealOffer(new Random(3), 8, 1, 0);
        int might = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Blessing
            && c.Index == (int)SurvivorsBlessing.Might);
        Assert.True(might >= 0);

        Assert.True(run.TryChoose(might + 1, out var chosen));
        Assert.Equal((int)SurvivorsBlessing.Might, chosen.Index);
        Assert.Equal(1, run.BlessingLevel(SurvivorsBlessing.Might));
        Assert.Equal(1.12, run.DamageMultiplier, 6);
    }

    [Fact]
    public void Every_skill_can_hold_five_supports()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++) run.Learn(skill);

        for (int round = 0; round < 20 && run.SupportsOf(0).Count < SurvivorsRules.MaxSupportsPerSkill; round++)
        {
            run.RecordKill(MonsterRarity.Unique);
            var offer = run.DealOffer(new Random(round), 8, 0, 8);
            int support = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Support && c.Target == 0);
            if (support < 0) support = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Support);
            Assert.True(support >= 0, $"round {round} offered no support at all");
            Assert.True(run.TryChoose(support + 1, out _));
        }

        Assert.Equal(SurvivorsRules.MaxSupportsPerSkill, run.SupportsOf(0).Count);
        run.RecordKill(MonsterRarity.Unique);
        var later = run.DealOffer(new Random(9), 8, 0, 8);
        Assert.DoesNotContain(later, c => c.Kind == SurvivorsChoiceKind.Support && c.Target == 0);
    }

    [Fact]
    public void A_unique_item_is_offered_every_third_level_and_only_once()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        // 2 experience a normal kill: level 3 needs 5 + 15 = 20.
        while (run.Level < 3) run.RecordKill(MonsterRarity.Normal);

        var offer = run.DealOffer(new Random(4), 8, 8, 8, [0, 1]);
        int at = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Item);
        Assert.True(at >= 0, "level 3 offered no unique item");
        Assert.True(run.TryChoose(at + 1, out var chosen));
        Assert.Contains(chosen.Index, run.Items);

        // The next item offer is at level 6, and never the one already taken (the Gateway sends only free ones).
        while (run.Level < 6) run.RecordKill(MonsterRarity.Normal);
        var later = run.DealOffer(new Random(5), 8, 8, 8, [1]);
        var item = Assert.Single(later, c => c.Kind == SurvivorsChoiceKind.Item);
        Assert.Equal(1, item.Index);
    }

    [Fact]
    public void An_invalid_choice_changes_nothing()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        run.RecordKill(MonsterRarity.Unique);
        var offer = run.DealOffer(new Random(3), 8, 8, 8);
        int before = run.PendingChoices;

        Assert.False(run.TryChoose(0, out _));
        Assert.False(run.TryChoose(4, out _));
        Assert.Equal(before, run.PendingChoices);
        Assert.Equal(offer, run.Offer);
        Assert.Equal(1, run.RankOf(0));
    }

    [Fact]
    public void A_second_level_up_does_not_overwrite_an_open_offer()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        run.RecordKill(MonsterRarity.Unique);
        var first = run.DealOffer(new Random(11), 8, 8, 8);
        run.RecordKill(MonsterRarity.Unique);

        Assert.Equal(first, run.DealOffer(new Random(99), 8, 8, 8));
        Assert.Equal(4, run.PendingChoices);
        Assert.Equal(5, run.Level);
    }

    [Fact]
    public void The_clock_ignores_paused_and_broken_steps()
    {
        var run = new SurvivorsRun();

        run.Advance(2.5);
        run.Advance(-1);
        run.Advance(double.NaN);
        run.Advance(double.PositiveInfinity);
        run.Advance(0);

        Assert.Equal(2.5, run.Elapsed, 6);
    }
}
