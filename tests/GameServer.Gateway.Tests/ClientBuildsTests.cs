using System.Text.RegularExpressions;
using GameServer.Core.Protocol;

namespace GameServer.Gateway.Tests;

public class ClientBuildsTests
{
    /// <summary>A build is its executable's exact bytes: one changed byte (a patched copy, another build) is not it,
    /// and the refusal names what was read and what would have been accepted.</summary>
    [Fact]
    public void OnlyAListedExecutableIsABuild()
    {
        byte[] executable = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7))];
        ClientBuild[] builds = [new("9.9.9a", "release", ClientBuilds.Sha256(executable), 0x140001000)];

        Assert.Same(builds[0], ClientBuilds.Find(executable, builds));
        Assert.Same(builds[0], ClientBuilds.Require(executable, builds));

        byte[] other = [.. executable];
        other[100] ^= 1;
        Assert.Null(ClientBuilds.Find(other, builds));
        var refused = Assert.Throws<UnknownClientBuildException>(() => ClientBuilds.Require(other, builds));
        Assert.Contains(ClientBuilds.Sha256(other)[..16], refused.Message);
        Assert.Contains("9.9.9a (release)", refused.Message);
        Assert.Null(ClientBuilds.Find(executable));
    }

    /// <summary>The shipped list: every row is one build (no hash twice), its hashes are whole lower-case SHA-256
    /// values (a comparison is exact text), and its mask address is inside a 64-bit client image.</summary>
    [Fact]
    public void TheAllowedBuildsAreWellFormed()
    {
        var hash = new Regex("^[0-9a-f]{64}$");
        Assert.NotEmpty(ClientBuilds.Allowed);
        Assert.All(ClientBuilds.Allowed, build =>
        {
            Assert.Matches(hash, build.ExecutableSha256);
            if (build.SteamIndexSha256 is not null) Assert.Matches(hash, build.SteamIndexSha256);
            Assert.InRange(build.HncMaskVa, 0x140000000, 0x150000000);
            Assert.False(string.IsNullOrWhiteSpace(build.Version) || string.IsNullOrWhiteSpace(build.Name));
            // A build a tool can download from Steam is one whose Steam files are accepted, a depot named once.
            if (build.SteamManifests is { } manifests)
            {
                Assert.NotNull(build.SteamIndexSha256);
                Assert.NotEmpty(manifests);
                Assert.Equal(manifests.Count, manifests.Select(m => m.Depot).Distinct().Count());
            }
        });
        Assert.Equal(ClientBuilds.Allowed.Count, ClientBuilds.Allowed.Select(b => b.ExecutableSha256).Distinct().Count());
        // What a person starting a client is told names their own build and every supported version.
        Assert.All(ClientBuilds.Allowed, b =>
        {
            Assert.Contains(b.ToString(), ClientBuilds.SupportNote(b));
            Assert.All(ClientBuilds.Allowed, other => Assert.Contains(other.Version, ClientBuilds.SupportNote(b)));
        });
        var steam = ClientBuilds.Allowed.Where(b => b.SteamIndexSha256 is not null).Select(b => b.SteamIndexSha256).ToList();
        Assert.Equal(steam.Count, steam.Distinct().Count());
        Assert.All(steam, index => Assert.Single(ClientBuilds.WithSteamIndex(index!)));
    }
}
