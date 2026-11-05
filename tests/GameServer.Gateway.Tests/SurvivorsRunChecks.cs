using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modules.Survivors;

namespace GameServer.Gateway.Tests;

public class SurvivorsRunTests
{
    // A test kill with its orb already collected: the live path leaves the orb on the ground and the run tick picks
    // it up when the player passes near.
    static int Kill(SurvivorsRun run, MonsterRarity rarity)
    {
        run.RecordKill(rarity);
        return run.RecordOrbExperience(SurvivorsRules.ExperienceForKill(rarity));
    }

    public void The_horde_grows_from_fourteen_to_the_cap()
    {
        Assert.Equal(14, SurvivorsRules.TargetAlive(0));
        Assert.Equal(117, SurvivorsRules.TargetAlive(450));
        Assert.Equal(220, SurvivorsRules.TargetAlive(900));
        Assert.Equal(220, SurvivorsRules.TargetAlive(9000));
        Assert.Equal(14, SurvivorsRules.TargetAlive(0, maxAlive: 25));
        Assert.Equal(25, SurvivorsRules.TargetAlive(900, maxAlive: 25));
    }

    public void The_spawner_refills_a_sixth_of_the_gap_at_most_four()
    {
        Assert.Equal(0, SurvivorsRules.SpawnRefill(0));
        Assert.Equal(1, SurvivorsRules.SpawnRefill(1));
        Assert.Equal(1, SurvivorsRules.SpawnRefill(6));
        Assert.Equal(2, SurvivorsRules.SpawnRefill(7));
        Assert.Equal(4, SurvivorsRules.SpawnRefill(24));
        Assert.Equal(4, SurvivorsRules.SpawnRefill(220));
    }

    public void The_roster_walks_its_tier_pool_over_the_run()
    {
        // The reference's wave_pool: three consecutive tiers over thirty steps of a 30-tier roster.
        Assert.Equal([0, 1, 2], SurvivorsRules.WavePool(0, 30));
        Assert.Equal([14, 15, 16], SurvivorsRules.WavePool(450, 30));
        Assert.Equal([27, 28, 29], SurvivorsRules.WavePool(900, 30));
        Assert.Equal([7, 8, 9], SurvivorsRules.WavePool(900, 10));
        Assert.Equal([0, 1, 2], SurvivorsRules.WavePool(0, 3));
        Assert.Equal([0, 1], SurvivorsRules.WavePool(450, 2));
        Assert.Empty(SurvivorsRules.WavePool(450, 0));
    }

    public void A_run_skill_grows_in_damage_and_speed_with_its_rank()
    {
        Assert.Equal(1.2, SurvivorsRules.SkillInterval(1), 6);
        Assert.Equal(1.116, SurvivorsRules.SkillInterval(2), 6);
        Assert.Equal(0.2, SurvivorsRules.SkillInterval(100), 6);
        // The reference's rank curve: +30% a rank, 3.1× at rank 8 (mod.rs Owned::damage_mult).
        Assert.Equal(1.0, SurvivorsRules.SkillDamageMultiplier(1), 6);
        Assert.Equal(1.3, SurvivorsRules.SkillDamageMultiplier(2), 6);
        Assert.Equal(3.1, SurvivorsRules.SkillDamageMultiplier(8), 6);
        Assert.Equal(20, SurvivorsRules.SkillDamage(20, 1));
        Assert.Equal(26, SurvivorsRules.SkillDamage(20, 2));
        Assert.Equal(62, SurvivorsRules.SkillDamage(20, 8));
        Assert.Equal(1, SurvivorsRules.SkillDamage(0, 1));
    }

    public void The_boss_ladder_and_its_experience_match_the_reference()
    {
        // data.rs BOSSES over wave_pool at each 135 s boss: 700 / 26 = 26.92, then 25.71, 49.41, 31.82, 50, 47.62,
        // and the seventh area's extension.
        Assert.Equal([26.92, 25.71, 49.41, 31.82, 50.0, 47.62, 60.0],
            Enumerable.Range(0, 7).Select(SurvivorsRules.BossLifeRatio));
        Assert.Equal([60, 120, 200, 300, 400, 500, 600],
            Enumerable.Range(0, 7).Select(SurvivorsRules.BossExperience));
        Assert.Equal(60.0, SurvivorsRules.BossLifeRatio(99));
        Assert.Equal(600, SurvivorsRules.BossExperience(99));
        // The per-minute cadence rolls the ladder: past the seventh boss it stays on the last rung.
        Assert.Equal(2, SurvivorsRules.BossLadderIndex(2));
        Assert.Equal(6, SurvivorsRules.BossLadderIndex(99));
    }

    public void The_run_has_the_reference_s_six_skill_slots()
    {
        Assert.Equal(6, SurvivorsRules.MaxSkills);
        Assert.Equal(6, SurvivorsRules.MaxBlessings);
        Assert.Equal(5, SurvivorsRules.MaxBlessingLevel);
        Assert.Equal(8, SurvivorsRules.MaxRank);
        Assert.Equal(900, SurvivorsRules.VictorySeconds);
        Assert.Equal(60, SurvivorsRules.BossEverySeconds);
        Assert.Equal(0.12, SurvivorsRules.SpawnIntervalSeconds, 6);
    }

    public void A_run_skill_gains_a_projectile_at_ranks_three_five_and_seven()
    {
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], Enumerable.Range(1, SurvivorsRules.MaxRank).Select(SurvivorsRules.SkillProjectiles));
        // Its reach grows 8% a rank, before the run's area blessings.
        Assert.Equal(1, SurvivorsRules.SkillRadius(1), 6);
        Assert.Equal(1.16, SurvivorsRules.SkillRadius(3), 6);
        Assert.Equal(1.56, SurvivorsRules.SkillRadius(8), 6);
    }

    public void Monsters_grow_tougher_and_hit_harder_over_a_run()
    {
        Assert.Equal(1, SurvivorsRules.MonsterLifeScale(0), 6);
        Assert.Equal(3.5, SurvivorsRules.MonsterLifeScale(900), 6);
        Assert.Equal(1, SurvivorsRules.MonsterDamageScale(0), 6);
        Assert.Equal(3.5, SurvivorsRules.MonsterDamageScale(900), 6);
        Assert.Equal(3.5, SurvivorsRules.MonsterLifeScale(9000), 6);
        Assert.Equal(3.5, SurvivorsRules.MonsterDamageScale(9000), 6);
        // The playtest asks for enemies at least five times tougher on top of the reference curve.
        Assert.True(SurvivorsRules.EnemyLifeMultiplier >= 5);
        Assert.Equal(5, SurvivorsRules.EnemyLifeMultiplier, 6);
        // And hits that actually hurt through the run's 60% taken reduction.
        Assert.Equal(4, SurvivorsRules.EnemyDamageMultiplier, 6);
    }

    public void The_sustained_kill_rate_ramps_the_dps_pressure()
    {
        // At or below the baseline pace there is no extra pressure.
        Assert.Equal(1, SurvivorsRules.ThreatMultiplier(0), 6);
        Assert.Equal(1, SurvivorsRules.ThreatMultiplier(SurvivorsRules.BaseKillsPerSecond), 6);
        // Twice the baseline pace multiplies monster life, and the cap holds the pressure.
        Assert.True(SurvivorsRules.ThreatMultiplier(SurvivorsRules.BaseKillsPerSecond * 2) > 1);
        Assert.Equal(SurvivorsRules.MaxThreat, SurvivorsRules.ThreatMultiplier(1_000), 6);

        // The run measures the pace over its window: ten kills inside it, none once it has passed.
        var run = new SurvivorsRun();
        for (int i = 0; i < 10; i++) run.RecordKill(MonsterRarity.Normal);
        run.Advance(1);
        Assert.Equal(10d / SurvivorsRules.ThreatWindowSeconds, run.ThreatKillsPerSecond, 6);
        run.Advance(SurvivorsRules.ThreatWindowSeconds + 1);
        Assert.Equal(0, run.ThreatKillsPerSecond, 6);
    }

    public void Experience_orbs_are_collected_only_inside_the_radius()
    {
        SurvivorsExperienceOrb[] orbs =
        [
            new(1, 0, 0, 2), new(2, 9, 0, 4), new(3, 11, 0, 10), new(4, 20, 0, 50),
        ];
        var taken = SurvivorsSkills.SurvivorsOrbsInReach(orbs, (0, 0), SurvivorsRules.OrbPickupRadius);
        Assert.Equal([1u, 2u, 3u], taken.Select(orb => orb.Id));
        Assert.Equal(18, SurvivorsRules.OrbPickupRadius, 6);
    }

    public void A_death_zone_drains_a_quarter_of_max_life_each_second()
    {
        Assert.Equal(25, SurvivorsRules.DeathZoneDamagePercentPerSecond);
        Assert.Equal(25, SurvivorsRules.DeathZoneDamagePerSecond(100));
        Assert.Equal(50, SurvivorsRules.DeathZoneDamagePerSecond(200));
        Assert.Equal(1, SurvivorsRules.DeathZoneDamagePerSecond(1));  // at least one life a second
        Assert.Equal(4, SurvivorsRules.DeathZoneDurationSeconds, 6);
        Assert.Equal(5, SurvivorsRules.DeathZoneRadius);
    }

    public void A_run_hit_reaches_the_spawn_ring()
    {
        Assert.Equal(34, SurvivorsRules.Range, 6);
    }

    public void Incoming_monster_hits_are_taken_at_sixty_percent_and_fortitude_softens_them()
    {
        Assert.Equal(0, SurvivorsRules.IncomingDamage(0));
        Assert.Equal(1, SurvivorsRules.IncomingDamage(3));
        Assert.Equal(4, SurvivorsRules.IncomingDamage(7));
        Assert.Equal(12, SurvivorsRules.IncomingDamage(20));
        Assert.Equal(2, SurvivorsRules.IncomingDamage(20, 10));
        Assert.Equal(60, new SurvivorsRun().IncomingDamagePercent);
        // The reference's +20% maximum life a level maps to 1/1.2 less damage taken (60 → 24 at level 5).
        Assert.Equal([60, 50, 42, 35, 29, 24],
            Enumerable.Range(0, SurvivorsRules.MaxBlessingLevel + 1).Select(SurvivorsRules.FortitudeIncomingPercent));
    }

    public void The_herald_pulse_reaches_its_own_aura_and_draws_every_hit()
    {
        Assert.Equal(48, SurvivorsSkills.SurvivorsHeraldRadius, 6);
        // A pulse looks as far as its own aura, not the projectile cast range: a monster between them is still hit.
        Assert.Equal(48, SurvivorsSkills.SurvivorsCastReach(herald: true, radius: 48), 6);
        Assert.Equal(34, SurvivorsSkills.SurvivorsCastReach(herald: false, radius: 48), 6);
        // Every struck target plays its impact art: the old three-target visual cap is gone.
        Assert.Equal(6, SurvivorsSkills.SurvivorsHeraldEffectCount(6));
        Assert.Equal(30, SurvivorsSkills.SurvivorsHeraldEffectCount(30));
    }

    public void A_pulse_casts_where_the_skill_lands()
    {
        (int X, int Y) player = (10, 10), target = (30, 20);
        Assert.Equal(target, SurvivorsSkills.SurvivorsPulseCenter(selfCentered: false, player, target));
        Assert.Equal(player, SurvivorsSkills.SurvivorsPulseCenter(selfCentered: true, player, target));
    }

    public void The_horde_gets_a_ranged_ring_wave_every_twenty_seconds()
    {
        Assert.Equal(20, SurvivorsRules.RangedWaveIntervalSeconds);
        Assert.Equal(6, SurvivorsRules.RangedWaveCount(0));
        Assert.Equal(16, SurvivorsRules.RangedWaveCount(1));
    }

    public void The_stage_caps_the_skill_count()
    {
        // The first location allows one skill, the second two, …, the fifth five; after that only supports and ranks.
        Assert.Equal([1, 2, 3, 4, 5, 5, 5], Enumerable.Range(0, 7).Select(SurvivorsRules.SkillCap));
    }

    public void A_run_moves_through_the_act_s_areas_in_order()
    {
        // The original fixed progression: Act 1's areas, one stage each.
        Assert.Equal(["1_1_1", "1_1_2", "1_1_3", "1_1_5", "1_1_6", "1_1_9", "1_1_11_2"],
            SurvivorsRunRules.SurvivorsLevels);
    }

    public void Experience_follows_monster_rarity()
    {
        Assert.Equal(2, SurvivorsRules.ExperienceForKill(MonsterRarity.Normal));
        Assert.Equal(4, SurvivorsRules.ExperienceForKill(MonsterRarity.Magic));
        Assert.Equal(10, SurvivorsRules.ExperienceForKill(MonsterRarity.Rare));
        Assert.Equal(50, SurvivorsRules.ExperienceForKill(MonsterRarity.Unique));
    }

    public void Kills_level_the_run_and_queue_choices()
    {
        var run = new SurvivorsRun();
        // A normal kill is worth 2: the first level needs 5, so the third kill levels the run.
        for (int i = 0; i < 2; i++) Assert.Equal(0, Kill(run, MonsterRarity.Normal));

        Assert.Equal(1, run.Level);
        Assert.Equal(1, Kill(run, MonsterRarity.Normal));
        Assert.Equal(2, run.Level);
        Assert.Equal(1, run.Experience);
        Assert.Equal(1, run.PendingChoices);
        Assert.True(run.NeedsOffer);
    }

    public void A_unique_kill_can_grant_several_levels_at_once()
    {
        var run = new SurvivorsRun();

        Assert.Equal(3, Kill(run, MonsterRarity.Unique));

        Assert.Equal(4, run.Level);
        Assert.Equal(5, run.Experience);
        Assert.Equal(3, run.PendingChoices);
    }

    public void An_offer_holds_six_distinct_legal_choices()
    {
        // An unlinked skill is offered its missing supports first: six distinct ones, nothing else.
        var opening = new SurvivorsRun();
        opening.Learn(0);
        Kill(opening, MonsterRarity.Unique);
        var supports = opening.DealOffer(new Random(1), 8, 8, 8);
        Assert.Equal(SurvivorsRules.MaxOffers, supports.Count);
        Assert.Equal(SurvivorsRules.MaxOffers, supports.Select(c => (c.Kind, c.Index, c.Target)).Distinct().Count());
        Assert.All(supports, c => Assert.Equal(SurvivorsChoiceKind.Support, c.Kind));
        Assert.Equal(supports, opening.Offer);
        Assert.False(opening.NeedsOffer);

        // Once the skill holds every support it can take, a new location allows the next skill.
        var complete = Enumerable.Range(0, 30).SelectMany(seed =>
        {
            var r = new SurvivorsRun();
            r.Learn(0);
            for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) r.Link(0, support);
            Kill(r, MonsterRarity.Unique);
            return r.DealOffer(new Random(seed), 8, 8, 8, stage: 1);
        }).Select(c => c.Kind).ToHashSet();
        Assert.Contains(SurvivorsChoiceKind.NewSkill, complete);
        Assert.Contains(SurvivorsChoiceKind.SkillRank, complete);
        Assert.Contains(SurvivorsChoiceKind.Blessing, complete);
    }

    public void A_new_skill_waits_for_the_learned_skill_s_five_supports()
    {
        var unfinished = new SurvivorsRun();
        unfinished.Learn(0);
        for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill - 1; support++) unfinished.Link(0, support);
        Kill(unfinished, MonsterRarity.Unique);
        Assert.DoesNotContain(unfinished.DealOffer(new Random(1), 8, 8, 8), c => c.Kind == SurvivorsChoiceKind.NewSkill);

        var finished = new SurvivorsRun();
        finished.Learn(0);
        for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) finished.Link(0, support);
        Kill(finished, MonsterRarity.Unique);
        Assert.Contains(
            Enumerable.Range(0, 30).SelectMany(seed => finished.DealOffer(new Random(seed), 8, 8, 8, stage: 1)),
            c => c.Kind == SurvivorsChoiceKind.NewSkill);
    }

    public void Choosing_a_new_skill_learns_it_and_closes_the_offer()
    {
        var (run, offer, at) = DealUntil(Seed =>
            {
                var r = new SurvivorsRun();
                r.Learn(0);
                for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) r.Link(0, support);
                Kill(r, MonsterRarity.Unique);
                return r;
            },
            c => c.Kind == SurvivorsChoiceKind.NewSkill, skillCatalog: 8, blessingCatalog: 0, supportCatalog: 0, stage: 1);
        int pending = run.PendingChoices;

        Assert.True(run.TryChoose(at + 1, out var chosen));

        Assert.Equal(offer[at], chosen);
        Assert.Equal(1, run.RankOf(chosen.Index));
        Assert.Equal(pending - 1, run.PendingChoices);
        Assert.Empty(run.Offer);
        Assert.True(run.NeedsOffer);
    }

    public void A_full_skill_bar_is_offered_supports_for_its_skills()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++)
        {
            run.Learn(skill);
            Assert.True(run.SetRank(skill, SurvivorsRules.MaxRank));
        }
        Kill(run, MonsterRarity.Unique);

        var offer = run.DealOffer(new Random(5), 8, 0, 8);

        Assert.Equal(SurvivorsRules.MaxOffers, offer.Count);
        Assert.All(offer, c => Assert.Equal(SurvivorsChoiceKind.Support, c.Kind));
        Assert.True(run.TryChoose(1, out var chosen));
        Assert.Contains(chosen.Index, run.SupportsOf(chosen.Target));
    }

    public void An_offer_only_pairs_a_support_with_a_skill_the_rule_allows()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        run.Learn(1);
        run.SetRank(0, SurvivorsRules.MaxRank);
        run.SetRank(1, SurvivorsRules.MaxRank);
        Kill(run, MonsterRarity.Unique);
        var allowed = new HashSet<(int Skill, int Support)> { (0, 2), (1, 1) };

        var offer = run.DealOffer(new Random(1), 2, 0, 4, null, (skill, support) => allowed.Contains((skill, support)));

        var supports = offer.Where(c => c.Kind == SurvivorsChoiceKind.Support).ToList();
        Assert.Equal(2, supports.Count);
        Assert.All(supports, c => Assert.Contains((c.Target, c.Index), allowed));
    }

    public void Blessings_change_the_run_numbers()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++)
        {
            run.Learn(skill);
            run.SetRank(skill, SurvivorsRules.MaxRank);
            for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) run.Link(skill, support);
        }
        Kill(run, MonsterRarity.Unique);

        // A catalogue of one blessing always offers that blessing (Might): nothing else is left in the pool.
        var offer = run.DealOffer(new Random(3), 8, 1, 0);
        int might = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Blessing
            && c.Index == (int)SurvivorsBlessing.Might);
        Assert.True(might >= 0);

        Assert.True(run.TryChoose(might + 1, out var chosen));
        Assert.Equal((int)SurvivorsBlessing.Might, chosen.Index);
        Assert.Equal(1, run.BlessingLevel(SurvivorsBlessing.Might));
        // The reference's Might is +10% a level, not the old +12%.
        Assert.Equal(1.10, run.DamageMultiplier, 6);
        Assert.Equal(SurvivorsRules.CriticalPerLevelPermyriad, 500);
    }

    public void Every_skill_can_hold_five_supports()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++)
        {
            run.Learn(skill);
            run.SetRank(skill, SurvivorsRules.MaxRank);
        }
        run.RecordExperience(100_000);

        for (int round = 0; round < 80; round++)
        {
            if (Enumerable.Range(0, SurvivorsRules.MaxSkills)
                .All(skill => run.SupportsOf(skill).Count >= SurvivorsRules.MaxSupportsPerSkill)) break;
            var offer = run.DealOffer(new Random(round), 8, 0, 8);
            int support = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Support
                && run.SupportsOf(c.Target).Count < SurvivorsRules.MaxSupportsPerSkill);
            Assert.True(support >= 0, $"round {round} offered no support while one was free");
            Assert.True(run.TryChoose(support + 1, out _));
        }

        Assert.All(Enumerable.Range(0, SurvivorsRules.MaxSkills),
            skill => Assert.Equal(SurvivorsRules.MaxSupportsPerSkill, run.SupportsOf(skill).Count));
        var later = run.DealOffer(new Random(99), 8, 0, 8);
        Assert.DoesNotContain(later, c => c.Kind == SurvivorsChoiceKind.Support);
    }

    public void A_unique_item_is_offered_on_third_levels_from_the_free_pool()
    {
        // 2 experience a normal kill: level 3 needs 5 + 15 = 20. The skill's links come first, so they are filled.
        var (run, offer, at) = DealUntil(Seed =>
            {
                var r = new SurvivorsRun();
                r.Learn(0);
                for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) r.Link(0, support);
                while (r.Level < 3) Kill(r, MonsterRarity.Normal);
                return r;
            },
            c => c.Kind == SurvivorsChoiceKind.Item, itemPool: [0, 1]);
        Assert.True(run.TryChoose(at + 1, out var chosen));
        Assert.Contains(chosen.Index, run.Items);

        // The next deal at level 3 sees only the items the Gateway still calls free.
        var later = Assert.Single(Enumerable.Range(0, 200).Select(seed =>
        {
            var r = new SurvivorsRun();
            r.Learn(0);
            for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) r.Link(0, support);
            while (r.Level < 3) Kill(r, MonsterRarity.Normal);
            return r.DealOffer(new Random(seed), 8, 8, 8, [1]).Where(c => c.Kind == SurvivorsChoiceKind.Item).ToList();
        }).First(items => items.Count > 0));
        Assert.Equal(1, later.Index);
    }

    public void A_run_with_every_slot_mastered_offers_a_heal()
    {
        var run = MaxEverything();
        Kill(run, MonsterRarity.Unique);

        var offer = run.DealOffer(new Random(1), SurvivorsRules.MaxSkills, SurvivorsRules.MaxBlessings, 0);

        var only = Assert.Single(offer);
        Assert.Equal(SurvivorsChoiceKind.Heal, only.Kind);
        int pending = run.PendingChoices;
        Assert.True(run.TryChoose(1, out var chosen));
        Assert.Equal(SurvivorsChoiceKind.Heal, chosen.Kind);
        Assert.Equal(pending - 1, run.PendingChoices);
    }

    /// <summary>Deals offers over seeds until one holds a matching choice; the reference's offer is weighted, so the
    /// category is not guaranteed on the first deal.</summary>
    static (SurvivorsRun Run, IReadOnlyList<SurvivorsChoice> Offer, int At) DealUntil(
        Func<int, SurvivorsRun> make, Func<SurvivorsChoice, bool> match, int skillCatalog = 8, int blessingCatalog = 8,
        int supportCatalog = 8, IReadOnlyList<int>? itemPool = null, int stage = 0)
    {
        for (int seed = 0; seed < 500; seed++)
        {
            var run = make(seed);
            var offer = run.DealOffer(new Random(seed), skillCatalog, blessingCatalog, supportCatalog, itemPool, stage: stage);
            int at = offer.ToList().FindIndex(c => match(c));
            if (at >= 0) return (run, offer, at);
        }
        throw new InvalidOperationException("no seed produced a matching offer");
    }

    /// <summary>A run with six rank-8 skills, five supports each and six level-5 blessings: nothing left but the Healthstone.</summary>
    static SurvivorsRun MaxEverything()
    {
        var run = new SurvivorsRun();
        for (int skill = 0; skill < SurvivorsRules.MaxSkills; skill++)
        {
            run.Learn(skill);
            run.SetRank(skill, SurvivorsRules.MaxRank);
            for (int support = 0; support < SurvivorsRules.MaxSupportsPerSkill; support++) run.Link(skill, support);
        }
        run.RecordExperience(100_000);
        for (int seed = 0; seed < 200; seed++)
        {
            bool done = run.Blessings.Count >= SurvivorsRules.MaxBlessings
                && run.Blessings.Values.All(level => level >= SurvivorsRules.MaxBlessingLevel);
            if (done) break;
            var offer = run.DealOffer(new Random(seed), SurvivorsRules.MaxSkills, SurvivorsRules.MaxBlessings, 0);
            int at = offer.ToList().FindIndex(c => c.Kind == SurvivorsChoiceKind.Blessing
                && run.BlessingLevel((SurvivorsBlessing)c.Index) < SurvivorsRules.MaxBlessingLevel);
            Assert.True(at >= 0, $"seed {seed} offered no blessing while one was not maxed");
            Assert.True(run.TryChoose(at + 1, out _));
        }
        Assert.Equal(SurvivorsRules.MaxBlessings, run.Blessings.Count);
        Assert.All(run.Blessings.Values, level => Assert.Equal(SurvivorsRules.MaxBlessingLevel, level));
        return run;
    }

    public void An_invalid_choice_changes_nothing()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        Kill(run, MonsterRarity.Unique);
        var offer = run.DealOffer(new Random(3), 8, 8, 8);
        int before = run.PendingChoices;

        Assert.False(run.TryChoose(0, out _));
        Assert.False(run.TryChoose(SurvivorsRules.MaxOffers + 1, out _));
        Assert.Equal(before, run.PendingChoices);
        Assert.Equal(offer, run.Offer);
        Assert.Equal(1, run.RankOf(0));
    }

    public void A_second_level_up_does_not_overwrite_an_open_offer()
    {
        var run = new SurvivorsRun();
        run.Learn(0);
        Kill(run, MonsterRarity.Unique);
        var first = run.DealOffer(new Random(11), 8, 8, 8);
        Kill(run, MonsterRarity.Unique);

        Assert.Equal(first, run.DealOffer(new Random(99), 8, 8, 8));
        Assert.Equal(4, run.PendingChoices);
        Assert.Equal(5, run.Level);
    }

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

    public void The_gate_boss_scales_to_the_stage_and_the_player()
    {
        // The gate fights for the stage's average build: the old 105 s ladder at its 3x playtest stretch and 5x boss
        // life, in the 120 s window.
        Assert.Equal(461_486, SurvivorsRules.BossGateLife(1_000, 0, 0));
        Assert.Equal(3_085_714, SurvivorsRules.BossGateLife(1_000, 6, 720));
        Assert.Equal(5, SurvivorsRules.BossLifeMultiplier);
        Assert.True(SurvivorsRules.BossGateLife(1_000, 6, 720) > SurvivorsRules.BossGateLife(1_000, 0, 0));

        // A missed hit removes a seventh of the player's pool after mitigation; resistance and armour buy the extra hits.
        Assert.Equal(7, SurvivorsRules.BossHitsToDie);
        int physical = SurvivorsRules.BossHitDamage(7_000, 0, DamageType.Physical);
        Assert.Equal(1_000, Math.Round(physical * SurvivorsRules.TakenFraction(0, DamageType.Physical)));
        int fire = SurvivorsRules.BossHitDamage(7_000, 75, DamageType.Fire);
        Assert.Equal(1_000, Math.Round(fire * SurvivorsRules.TakenFraction(75, DamageType.Fire)));
        Assert.Equal(1_000, Math.Round(SurvivorsRules.BossHitDamage(7_000, 0, DamageType.Fire, 0.5)
            * SurvivorsRules.TakenFraction(0, DamageType.Fire) * 2));

        // Seven locations, seven different gate bosses, each with its own move set and adds.
        var kits = Enumerable.Range(0, 7).Select(SurvivorsRunRules.SurvivorsBossFor).ToList();
        Assert.Equal(7, kits.Select(kit => kit.Model).Distinct().Count());
        Assert.Equal(7, kits.Select(kit => kit.Name).Distinct().Count());
        Assert.All(kits, kit => Assert.True(kit.Moves.Count >= 2, $"{kit.Name} has too few moves"));
        Assert.All(kits, kit => Assert.True(kit.Adds > 0 && kit.SummonSeconds > 0, $"{kit.Name} summons nothing"));
    }
}
