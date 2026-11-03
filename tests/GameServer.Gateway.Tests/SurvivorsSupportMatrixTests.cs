using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modules.Survivors;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The survivors run offers eight real support gems. This walks every run skill x every support through the same
// SurvivorsComputeCast the live cast uses: the game's ActiveSkillType rules decide the link, and a linked support must
// move the number its own gem stats name (PoB's calcDamage / calcCastTime / calcRadius / crit / projectile inputs).
public sealed class SurvivorsSupportMatrixTests
{
    static ClientState Connection(string name) =>
        new(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };

    static SurvivorsRun RunFor(int skill, int? support = null)
    {
        var run = new SurvivorsRun();
        run.Learn(skill);
        if (support is int s) Assert.True(run.Link(skill, s));
        return run;
    }

    public void Every_support_moves_its_own_number_on_every_skill_it_can_support()    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-matrix-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SurvivorsMatrix");
        try
        {
            var catalog = SurvivorsSkills.SurvivorsCatalogFor(conn);
            var supports = SurvivorsSkills.SurvivorsSupportsFor(conn);
            SurvivorsSkills.StateOf(conn).SurvivorsCatalog = catalog;
            SurvivorsSkills.StateOf(conn).SurvivorsSupports = supports;
            Assert.Equal(8, supports.Count);
            var roster = new SurvivorsSkills.SurvivorsRoster([], [], 1, 20, 40);
            int pairs = 0, unimplemented = 0;
            var tooFew = new List<string>();
            foreach (int skillIndex in Enumerable.Range(0, catalog.Count))
            {
                var skill = catalog[skillIndex];
                if (skill.Kind == SurvivorsSkillKind.Herald)
                {
                    // The real gem is a buff: these damage supports have no pulse hit to modify, so the offer never
                    // pairs one with a herald.
                    Assert.All(Enumerable.Range(0, supports.Count),
                        support => Assert.False(SurvivorsSkills.SurvivorsCanSupport(conn, skillIndex, support)));
                    continue;
                }
                // An area gem's cast is a pulse with no projectile Shot; its supports are checked in the area helper.
                if (skill.Kind == SurvivorsSkillKind.Area) continue;
                // Rank 1 and the max rank 8: the support's per-level stats scale with the gem level it supports.
                foreach (int rank in new[] { 1, 8 })
                {
                    var bare = SurvivorsSkills.SurvivorsComputeCast(conn, RunFor(skillIndex), skill, rank, skillIndex, roster);
                    Assert.True(bare is not null, $"{skill.Name} rank {rank}: no cast");
                    int known = 0;
                    for (int supportIndex = 0; supportIndex < supports.Count; supportIndex++)
                    {
                        bool legal = SurvivorsSkills.SurvivorsCanSupport(conn, skillIndex, supportIndex);
                        var with = SurvivorsSkills.SurvivorsComputeCast(conn, RunFor(skillIndex, supportIndex), skill, rank, skillIndex, roster);
                        if (!legal)
                        {
                            // The offer never pairs these; the matrix must not accidentally claim they work.
                            Assert.NotNull(with);
                            continue;
                        }
                        known++;
                        pairs++;
                        var name = supports[supportIndex].Support;
                        Assert.NotNull(with);
                        bool moved = name switch
                        {
                            SurvivorsSupport.AddedFire => bare!.Raw![DamageType.Physical].Max > 0
                                ? with!.Shot!.Hit[DamageType.Fire].Max > bare.Shot!.Hit[DamageType.Fire].Max
                                : with!.Shot!.Hit[DamageType.Fire].Max == bare.Shot!.Hit[DamageType.Fire].Max,
                            SurvivorsSupport.AddedCold => with!.Shot!.Hit[DamageType.Cold].Max > bare!.Shot!.Hit[DamageType.Cold].Max,
                            SurvivorsSupport.AddedLightning => with!.Shot!.Hit[DamageType.Lightning].Max > bare!.Shot!.Hit[DamageType.Lightning].Max,
                            SurvivorsSupport.FasterCasting => with!.Interval < bare!.Interval || bare.Interval <= 0.2000001,
                            SurvivorsSupport.Area => with!.Profile!.Radius > bare!.Profile!.Radius,
                            SurvivorsSupport.Critical => with!.Shot!.CriticalChance > bare!.Shot!.CriticalChance,
                            SurvivorsSupport.FasterProjectiles => with!.Profile!.Speed > bare!.Profile!.Speed
                                && with.Shot!.Hit.MaxTotal > bare.Shot!.Hit.MaxTotal,
                            SurvivorsSupport.LifeLeech => with!.Shot!.Leech.Life.MinTotal > 0,
                            _ => false,
                        };
                        if (!moved)
                        {
                            unimplemented++;
                            Assert.Fail($"{supports[supportIndex].Name} -> {skill.Name} rank {rank}: the support moved none of its numbers " +
                                $"(interval {bare!.Interval}->{with.Interval}, radius {bare.Profile?.Radius}->{with.Profile?.Radius}, " +
                                $"crit {bare.Shot?.CriticalChance}->{with.Shot?.CriticalChance}, fire {bare.Shot?.Hit[DamageType.Fire]}->" +
                                $"{with.Shot?.Hit[DamageType.Fire]}, cold {bare.Shot?.Hit[DamageType.Cold]}->{with.Shot?.Hit[DamageType.Cold]}, " +
                                $"lightning {bare.Shot?.Hit[DamageType.Lightning]}->{with.Shot?.Hit[DamageType.Lightning]}, " +
                                $"speed {bare.Profile?.Speed}->{with.Profile?.Speed}, leech {bare.Shot?.Leech.Life.MinTotal}->" +
                                $"{with.Shot?.Leech.Life.MinTotal}).");
                        }
                    }
                    if (rank == 1 && known < 5) tooFew.Add($"{skill.Name} ({known})");
                }
            }
            Assert.Empty(tooFew);
            Assert.True(pairs > 150, $"only {pairs} legal skill/support pairs");
            Assert.Equal(0, unimplemented);

            // Every herald's pulse has its own hit effect: the five gems map to the game's .ao for that hit.
            var heraldEffects = catalog.Where(s => s.Kind == SurvivorsSkillKind.Herald)
                .Select(s => (s.Name, Impact: SurvivorsSkills.SurvivorsHeraldImpactFor(conn, s))).ToList();
            Assert.Equal(5, heraldEffects.Count);
            Assert.All(heraldEffects, herald => Assert.NotNull(herald.Impact));
            Assert.Equal(["HeraldOfAgony", "HeraldOfAsh", "HeraldOfIce", "HeraldOfPurity", "HeraldOfThunder"],
                SurvivorsSkills.SurvivorsHeraldImpacts.Keys.Order());
            Assert.All(SurvivorsSkills.SurvivorsHeraldImpacts.Values,
                impact => Assert.StartsWith("Metadata/Effects/Spells/herald/", impact.Ao, StringComparison.Ordinal));

            // The named cases the live matrix prints: an attack refuses the spell-only and area-only supports, while a
            // spell accepts Faster Casting and refuses Area when the skill has no area type; Added Fire is legal on any
            // damaging attack or spell even when its physical base is empty.
            int tornado = catalog.ToList().FindIndex(s => s.Name == "Tornado Shot");
            int knives = catalog.ToList().FindIndex(s => s.Name == "Ethereal Knives");
            int fire = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.AddedFire);
            int fasterCast = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.FasterCasting);
            int area = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.Area);
            Assert.True(tornado >= 0 && knives >= 0 && fire >= 0 && fasterCast >= 0 && area >= 0);
            Assert.True(SurvivorsSkills.SurvivorsCanSupport(conn, tornado, fire));
            Assert.False(SurvivorsSkills.SurvivorsCanSupport(conn, tornado, fasterCast));
            Assert.False(SurvivorsSkills.SurvivorsCanSupport(conn, tornado, area));
            Assert.True(SurvivorsSkills.SurvivorsCanSupport(conn, knives, fasterCast));
            Assert.False(SurvivorsSkills.SurvivorsCanSupport(conn, knives, area));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    public void A_run_skill_uses_the_reference_s_thirty_percent_a_rank()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-rank-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SurvivorsRank");
        try
        {
            var catalog = SurvivorsSkills.SurvivorsCatalogFor(conn);
            int index = catalog.ToList().FindIndex(s => s.Name == "Fireball");
            Assert.True(index >= 0);
            var skill = catalog[index];
            var roster = new SurvivorsSkills.SurvivorsRoster([], [], 1, 20, 40);
            var run = new SurvivorsRun();
            run.Learn(index);

            var rank1 = SurvivorsSkills.SurvivorsComputeCast(conn, run, skill, 1, index, roster);
            var rank8 = SurvivorsSkills.SurvivorsComputeCast(conn, run, skill, 8, index, roster);
            Assert.NotNull(rank1);
            Assert.NotNull(rank8);
            // The reference's Owned::damage_mult: 1 + 0.30 × (rank − 1), so rank 8 is 3.1× rank 1.
            double ratio = rank8!.Shot!.Hit.AverageTotal / rank1!.Shot!.Hit.AverageTotal;
            Assert.InRange(ratio, 3.05, 3.15);
            // The interval is ×0.93 a rank, never below the 200 ms floor.
            Assert.Equal(rank1.Interval * Math.Pow(0.93, 7), rank8.Interval, 4);
            Assert.True(rank8.Interval < rank1.Interval);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    public void Every_non_herald_skill_can_link_five_supports()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-links-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SurvivorsLinks");
        try
        {
            var catalog = SurvivorsSkills.SurvivorsCatalogFor(conn);
            var supports = SurvivorsSkills.SurvivorsSupportsFor(conn);
            SurvivorsSkills.StateOf(conn).SurvivorsCatalog = catalog;
            SurvivorsSkills.StateOf(conn).SurvivorsSupports = supports;
            // The run is a caster's mode: a melee attack gem (Elemental Hit, Cyclone) is never offered, and one gem one card.
            Assert.DoesNotContain(catalog, skill => Game.SkillsFor(conn).IsMelee(skill.Effect));
            Assert.Equal(catalog.Count, catalog.Select(skill => skill.Name.ToLowerInvariant()).Distinct().Count());
            // The links-first offer needs five supports a skill can actually take; a herald is a buff and takes none.
            var thin = new List<string>();
            foreach (int index in Enumerable.Range(0, catalog.Count))
            {
                if (catalog[index].Kind == SurvivorsSkillKind.Herald) continue;
                int compatible = Enumerable.Range(0, supports.Count)
                    .Count(support => SurvivorsSkills.SurvivorsCanSupport(conn, index, support));
                if (compatible < SurvivorsRules.MaxSupportsPerSkill) thin.Add($"{catalog[index].Name} ({compatible})");
            }
            Assert.Empty(thin);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    public void Area_gems_join_the_catalogue_and_cast_an_area_pulse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-area-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SurvivorsArea");
        try
        {
            var catalog = SurvivorsSkills.SurvivorsCatalogFor(conn);
            var areaSkills = catalog.Where(s => s.Kind == SurvivorsSkillKind.Area).ToList();
            Assert.True(areaSkills.Count >= 5, $"only {areaSkills.Count} area skill(s) in the catalogue");
            // Melee area skills (Cyclone, Ground Slam, Cleave) are attacks, not casters: the run never offers them.
            Assert.DoesNotContain(catalog, s => s.Kind == SurvivorsSkillKind.Area && s.Attack);
            Assert.DoesNotContain(catalog, s => s.Name is "Cyclone" or "Ground Slam" or "Cleave");
            var skill = areaSkills[0];
            int index = catalog.ToList().IndexOf(skill);
            var roster = new SurvivorsSkills.SurvivorsRoster([], [], 1, 20, 40);
            var run = new SurvivorsRun();
            run.Learn(index);
            var cast = SurvivorsSkills.SurvivorsComputeCast(conn, run, skill, 1, index, roster);
            Assert.NotNull(cast);
            Assert.True(cast!.Herald);
            Assert.True(cast.HeraldDamage > 0);
            Assert.True(cast.HeraldRadius > 0);
            Assert.Equal(CastAnimationKind.Area, SurvivorsSkills.SurvivorsCastKind(skill));
            // The real gem takes supports: the run's offer pairs it with the area support.
            var supports = SurvivorsSkills.SurvivorsSupportsFor(conn);
            SurvivorsSkills.StateOf(conn).SurvivorsCatalog = catalog;
            SurvivorsSkills.StateOf(conn).SurvivorsSupports = supports;
            int areaSupport = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.Area);
            Assert.True(areaSupport >= 0);
            Assert.True(SurvivorsSkills.SurvivorsCanSupport(conn, index, areaSupport));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
