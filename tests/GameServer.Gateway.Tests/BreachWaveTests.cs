using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using GameServer.Modules.Breach;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A party member's touch opens the instance's Breach for both clients (the environment override, the Expanding state),
/// the wave is the Breach packs' varieties at the area's level created transitioned (BrequelMonster.ot's
/// create_untransitioned hides them), and the close takes the Initiator and the living wave out of the instance.
/// Nothing waits on real time: the touch time is wound back by hand.
/// </summary>
public sealed class BreachWaveTests
{
    [Fact]
    public async Task A_touch_opens_a_transitioned_wave_and_the_close_takes_it_away()
    {
        var saved = SystemSettings.Default.Values;
        SystemSettings.Default.Replace(new Dictionary<string, double>(saved, StringComparer.OrdinalIgnoreCase) { ["breach_enrage"] = 0 });
        try
        {
            await using var area = await BreachArea.Open();
            var encounter = new BreachEncounter();
            Assert.Null(await encounter.Populate(new AreaPopulated(area.A, area.Level, 7, [])));
            var breach = Assert.Single(encounter.Of(area.Instance)!.Breaches);
            Assert.NotEmpty(BreachTables.Packs);

            // B (not the one that runs the instance) walks up to it.
            area.B.Walk.Teleport(breach.X, breach.Y, Game.AreaTime(area.B));
            await encounter.Step(area.A);
            Assert.Equal(BreachTables.Expanding, breach.State);
            Assert.True(encounter.Of(area.Instance)!.EnvironmentOn);

            // A second of the curve is due.
            breach.TouchedAt -= 1.0;
            await encounter.Step(area.A);
            Assert.InRange(breach.Monsters.Count, 1, BreachTables.MaxWavePerTick + 8);
            foreach (uint id in breach.Monsters)
            {
                var m = area.A.Area.Monsters[id];
                Assert.StartsWith("Metadata/Monsters/Breach/", m.Variety!.Id, StringComparison.Ordinal);
                Assert.Equal(area.Level, m.Level);
                Assert.Equal(breach.Initiator, m.SummonerId);
                // Created in its last Transitionable state: create_transitioned enables its rendering and collision.
                Assert.Equal(2, area.Instance.World.MonsterOrigins[id].Object.TransitionState);
            }
            var wave = breach.Monsters.ToArray();

            // Without the enrage it closes 60 s after the touch, and goes with the end clip.
            breach.TouchedAt -= BreachTables.OpenSeconds;
            await encounter.Step(area.A);
            Assert.Equal(BreachTables.Closing, breach.State);
            breach.Since -= BreachTables.RemoveAfter;
            await encounter.Step(area.A);
            Assert.True(breach.Gone);
            Assert.False(area.A.Area.Objects.Has(breach.Initiator));
            Assert.All(wave, id => Assert.False(area.A.Area.Monsters.ContainsKey(id) || area.Instance.World.MonsterOrigins.ContainsKey(id)));
            Assert.False(encounter.Of(area.Instance)!.EnvironmentOn);
            // Nothing is left to show a client that enters now.
            Assert.Empty(encounter.EnterMessages(area.B));

            byte[] on = InstanceEnvironments.Data((BreachTables.EnvRow!.Value, BreachTables.EnvSeconds)), off = InstanceEnvironments.Data();
            byte[] a = await area.WireA.Finish(), b = await area.WireB.Finish();
            Assert.True(BreachArea.Find(a, on) is int aOn and >= 0 && BreachArea.Find(a.AsSpan(aOn).ToArray(), off) >= 0, "A: the override, then cleared");
            Assert.True(BreachArea.Find(b, on) is int bOn and >= 0 && BreachArea.Find(b.AsSpan(bOn).ToArray(), off) >= 0, "B: the override, then cleared");

            // Outer exclusions block extra Breaches even when MapContainsAdditionalUnstableBreaches is set.
            var map = new AreaInfo("MapWorldsReef", false, [], IsMapArea: true, LeagueChance: _ => 100);
            Assert.Equal(2, BreachEncounter.UnstableBreaches(map with { Stats = new Dictionary<int, int> { [(int)Stat.MapContainsAdditionalUnstableBreaches] = 2 } }, new LevelRandom(1, false)));
            foreach (var (stat, value) in new (uint, int)[]
            {
                (Stat.IsBlightedMap, 1), (Stat.MapDisableBreachAbyss, 1), (Stat.MapNukeEverything, 1),
                (Stat.GoAwayOtherLeagues, 1), (Stat.AtlasKeystoneNoExtraContent, 1), (Stat.MapDisableLeagues, 1 << LeagueChances.Breach),
            })
            {
                var excluded = map with { Stats = new Dictionary<int, int> { [(int)Stat.MapContainsAdditionalUnstableBreaches] = 2, [(int)stat] = value } };
                Assert.Equal(0, BreachEncounter.UnstableBreaches(excluded, new LevelRandom(1, false)));
            }
        }
        finally
        {
            SystemSettings.Default.Replace(saved);
        }
    }
}
