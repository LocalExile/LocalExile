namespace GameServer.Gateway.Tests;

// Which areas the Gateway generates itself: those whose seed-1 trace passes every gate check and whose logged loads
// all match (scripts/re/levelgen/gate-baseline.json).
public class ServerLevelsTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"baseline{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    [Fact] public void OnlyAreasPassingEveryCheckAreSupported()
    {
        File.WriteAllText(_file, """
        {
          "capture good": {"Hash":"match","Walk":0,"Projectile":0,"Placed":3,"Matched":3,"Captured":3,"Heights":0,"Objects":0},
          "capture stopped": {"Hash":"match","FirstDiff":"0x141530658 (port stopped)","Walk":0,"Projectile":0,"Captured":-1},
          "capture drawdiff": {"Hash":"match","FirstDiff":"0x1420437eb","Walk":0,"Projectile":0},
          "capture grid": {"Hash":"match","Walk":4,"Projectile":0},
          "capture doodads": {"Hash":"match","Walk":0,"Projectile":0,"Placed":3,"Matched":2,"Captured":3},
          "capture heights": {"Hash":"match","Walk":0,"Projectile":0,"Heights":12},
          "capture objects": {"Hash":"match","Walk":0,"Projectile":0,"Objects":1},
          "capture hash": {"Hash":"mismatch","Walk":0,"Projectile":0},
          "capture oracle": {"Hash":"match","Walk":0,"Projectile":0},
          "oracle oracle 77": {"Hash":"mismatch"},
          "oracle good 5": {"Hash":"match"}
        }
        """);
        Assert.Equal(["good", "stopped"], ServerLevels.LoadSupported(_file).Order());
    }

    // An area whose seed 1 the client cannot generate has no seed-1 trace: it is supported when the port fails seed 1
    // the same way and a trace at another seed ("load") passes every check, and played at the lowest such seed; without
    // one it keeps a logged seed the client built in place of seed 1 but is not generated. Any entry of an area that does
    // not match (a load, a logged load, a failure the port does not reproduce) leaves it out.
    [Fact] public void AreasTheClientCannotBuildAtSeedOneNeedATraceAtAnotherSeed()
    {
        File.WriteAllText(_file, """
        {
          "failure split 1": {"Hash":"match"},
          "oracle split 40": {"Hash":"match"},
          "oracle split 900": {"Hash":"match"},
          "load split 900": {"Hash":"match","Walk":0,"Projectile":0,"Placed":3,"Matched":3,"Captured":3,"Heights":0,"Objects":0},
          "failure hashonly 1": {"Hash":"match"},
          "oracle hashonly 8": {"Hash":"match"},
          "failure badgrid 1": {"Hash":"match"},
          "oracle badgrid 6": {"Hash":"match"},
          "load badgrid 6": {"Hash":"match","Walk":12,"Projectile":0,"Heights":0,"Objects":0},
          "failure badload 1": {"Hash":"match"},
          "load badload 8": {"Hash":"match","Walk":0,"Projectile":0,"Heights":0,"Objects":0},
          "oracle badload 9": {"Hash":"mismatch"},
          "failure portdiffers 1": {"Hash":"generated (the client: No suitable room found)"},
          "load portdiffers 7": {"Hash":"match","Walk":0,"Projectile":0,"Heights":0,"Objects":0},
          "failure noload 1": {"Hash":"match"},
          "capture laterfail": {"Hash":"match","Walk":0,"Projectile":0,"Heights":0,"Objects":0},
          "failure laterfail 77": {"Hash":"generated (the client: Could not tilise edge)"}
        }
        """);
        Assert.Equal(["split"], ServerLevels.LoadSupported(_file).Order());
        var seeds = GameServer.Core.World.ServerLevelAreas.SeedsInPlaceOfOne(_file);
        Assert.Equal([("badgrid", 6u), ("badload", 8u), ("hashonly", 8u), ("portdiffers", 7u), ("split", 900u)],
            seeds.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)));
    }

    // The committed baseline: the campaign areas the client cannot generate at seed 1 (re/011 §8 "Client failures") have
    // a fully checked trace at their stand-in seed (re/011 §7), so the Gateway generates them at that seed.
    [Theory]
    [InlineData("1_2_3")]   // The Crossroads (Act 2)
    [InlineData("2_6_1")]   // The Twilight Strand (Act 6)
    [InlineData("2_6_5")]   // The Karui Fortress
    [InlineData("2_6_12")]  // The Southern Forest (Act 6)
    [InlineData("2_7_2")]   // The Crossroads (Act 7)
    public void CampaignAreasTheClientCannotBuildAtSeedOneAreSupported(string area)
    {
        var baseline = GameServer.Core.World.ServerLevelAreas.BaselineFile;
        Assert.Contains(area, ServerLevels.LoadSupported(baseline));
        Assert.NotEqual(1u, ServerLevels.SeedInPlaceOfOne(area) ?? 1);
    }

    // Seed 1 from any path (a /goto without a seed, a pinned seed) becomes the stand-in, which the port generates.
    [Fact] public void SeedOneOfTheKaruiFortressIsPlayedAtItsStandIn() =>
        Assert.Equal(ServerLevels.SeedInPlaceOfOne("2_6_5"), ServerLevels.PlayableSeed("2_6_5", 1));

    // Only areas whose seed 1 the client fails get a stand-in: none of those with a seed-1 capture, which keep seed 1.
    [Fact] public void AreasWithASeedOneCaptureHaveNoStandIn()
    {
        var baseline = GameServer.Core.World.ServerLevelAreas.BaselineFile;
        var inPlace = GameServer.Core.World.ServerLevelAreas.SeedsInPlaceOfOne(baseline);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(baseline));
        var captured = doc.RootElement.EnumerateObject().Where(p => p.Name.StartsWith("capture ", StringComparison.Ordinal))
            .Select(p => p.Name["capture ".Length..]).ToHashSet();
        Assert.Contains("1_1_1", captured);
        Assert.Contains("1_2_3", inPlace.Keys);
        Assert.DoesNotContain(inPlace.Keys, captured.Contains);
        Assert.Equal(1u, ServerLevels.PlayableSeed("NoSuchArea", 1));
    }

    [Fact] public void NoBaselineSupportsNothing() => Assert.Empty(ServerLevels.LoadSupported(_file + ".missing"));

    // A seed the port cannot generate is replaced by the same seeds for everyone entering the instance: distinct, in
    // Auth's range (2 … int.MaxValue), never the seed itself.
    [Theory]
    [InlineData(32124476u)]
    public void ReplacementSeedsAreFixedPerSeed(uint seed)
    {
        var seeds = ServerLevels.Replacements(seed).ToList();
        Assert.Equal(seeds, ServerLevels.Replacements(seed));
        Assert.Equal(8, seeds.Distinct().Count());
        Assert.All(seeds, s => Assert.InRange(s, 2u, (uint)int.MaxValue));
        Assert.DoesNotContain(seed, seeds);
    }

    [Fact] public void AnAreaTheGatewayDoesNotGenerateKeepsItsSeed() =>
        Assert.Equal(32124476u, ServerLevels.PlayableSeed("NoSuchArea", 32124476u));
}
