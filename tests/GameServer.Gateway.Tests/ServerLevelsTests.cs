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

    [Fact] public void NoBaselineSupportsNothing() => Assert.Empty(ServerLevels.LoadSupported(_file + ".missing"));

    // The servers run in the runtime directory, which can be outside the checkout that holds the baseline.
    [Fact] public void TheBaselineIsFoundFromTheRuntimeDirectory() =>
        Assert.True(File.Exists(GameServer.Core.World.ServerLevelAreas.BaselinePath));

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
