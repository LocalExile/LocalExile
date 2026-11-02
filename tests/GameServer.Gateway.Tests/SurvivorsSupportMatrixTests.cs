using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
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

    [Fact]
    public void Every_support_moves_its_own_number_on_every_skill_it_can_support()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-matrix-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SurvivorsMatrix");
        try
        {
            var catalog = Game.SurvivorsCatalogFor(conn);
            var supports = Game.SurvivorsSupportsFor(conn);
            conn.SurvivorsCatalog = catalog;
            conn.SurvivorsSupports = supports;
            Assert.Equal(8, supports.Count);
            var roster = new Game.SurvivorsRoster([], [], 1, 20);
            int pairs = 0, unimplemented = 0;
            var tooFew = new List<string>();
            foreach (int skillIndex in Enumerable.Range(0, catalog.Count))
            {
                var skill = catalog[skillIndex];
                var bare = Game.SurvivorsComputeCast(conn, RunFor(skillIndex), skill, 1, skillIndex, roster);
                if (bare is null) continue;
                if (skill.Kind == SurvivorsSkillKind.Herald)
                {
                    // The real gem is a buff: these damage supports have no pulse hit to modify, so the offer never
                    // pairs one with a herald.
                    Assert.All(Enumerable.Range(0, supports.Count),
                        support => Assert.False(Game.SurvivorsCanSupport(conn, skillIndex, support)));
                    continue;
                }
                int known = 0;
                for (int supportIndex = 0; supportIndex < supports.Count; supportIndex++)
                {
                    bool legal = Game.SurvivorsCanSupport(conn, skillIndex, supportIndex);
                    var with = Game.SurvivorsComputeCast(conn, RunFor(skillIndex, supportIndex), skill, 1, skillIndex, roster);
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
                        SurvivorsSupport.AddedFire => bare.Raw![DamageType.Physical].Max > 0
                            ? with!.Shot!.Hit[DamageType.Fire].Max > bare.Shot!.Hit[DamageType.Fire].Max
                            : with!.Shot!.Hit[DamageType.Fire].Max == bare.Shot!.Hit[DamageType.Fire].Max,
                        SurvivorsSupport.AddedCold => with!.Shot!.Hit[DamageType.Cold].Max > bare.Shot!.Hit[DamageType.Cold].Max,
                        SurvivorsSupport.AddedLightning => with!.Shot!.Hit[DamageType.Lightning].Max > bare.Shot!.Hit[DamageType.Lightning].Max,
                        SurvivorsSupport.FasterCasting => with!.Interval < bare.Interval || bare.Interval <= 0.2000001,
                        SurvivorsSupport.Area => with!.Profile!.Radius > bare.Profile!.Radius,
                        SurvivorsSupport.Critical => with!.Shot!.CriticalChance > bare.Shot!.CriticalChance,
                        SurvivorsSupport.FasterProjectiles => with!.Profile!.Speed > bare.Profile!.Speed
                            && with.Shot!.Hit.MaxTotal > bare.Shot!.Hit.MaxTotal,
                        SurvivorsSupport.LifeLeech => with!.Shot!.Leech.Life.MinTotal > 0,
                        _ => false,
                    };
                    if (!moved)
                    {
                        unimplemented++;
                        Assert.Fail($"{supports[supportIndex].Name} -> {skill.Name}: the support moved none of its numbers " +
                            $"(interval {bare.Interval}->{with.Interval}, radius {bare.Profile?.Radius}->{with.Profile?.Radius}, " +
                            $"crit {bare.Shot?.CriticalChance}->{with.Shot?.CriticalChance}, fire {bare.Shot?.Hit[DamageType.Fire]}->" +
                            $"{with.Shot?.Hit[DamageType.Fire]}, cold {bare.Shot?.Hit[DamageType.Cold]}->{with.Shot?.Hit[DamageType.Cold]}, " +
                            $"lightning {bare.Shot?.Hit[DamageType.Lightning]}->{with.Shot?.Hit[DamageType.Lightning]}, " +
                            $"speed {bare.Profile?.Speed}->{with.Profile?.Speed}, leech {bare.Shot?.Leech.Life.MinTotal}->" +
                            $"{with.Shot?.Leech.Life.MinTotal}).");
                    }
                }
                if (known < 5) tooFew.Add($"{skill.Name} ({known})");
            }
            Assert.Empty(tooFew);
            Assert.True(pairs > 150, $"only {pairs} legal skill/support pairs");
            Assert.Equal(0, unimplemented);

            // The named cases the live matrix prints: an attack refuses the spell-only and area-only supports, while a
            // spell accepts Faster Casting and refuses Area when the skill has no area type; Added Fire is legal on any
            // damaging attack or spell even when its physical base is empty.
            int tornado = catalog.ToList().FindIndex(s => s.Name == "Tornado Shot");
            int knives = catalog.ToList().FindIndex(s => s.Name == "Ethereal Knives");
            int fire = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.AddedFire);
            int fasterCast = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.FasterCasting);
            int area = supports.ToList().FindIndex(s => s.Support == SurvivorsSupport.Area);
            Assert.True(tornado >= 0 && knives >= 0 && fire >= 0 && fasterCast >= 0 && area >= 0);
            Assert.True(Game.SurvivorsCanSupport(conn, tornado, fire));
            Assert.False(Game.SurvivorsCanSupport(conn, tornado, fasterCast));
            Assert.False(Game.SurvivorsCanSupport(conn, tornado, area));
            Assert.True(Game.SurvivorsCanSupport(conn, knives, fasterCast));
            Assert.False(Game.SurvivorsCanSupport(conn, knives, area));
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
