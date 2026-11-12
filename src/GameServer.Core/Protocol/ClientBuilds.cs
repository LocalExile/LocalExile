using System.Security.Cryptography;

namespace GameServer.Core.Protocol;

/// <summary>One build of the client LocalExile runs.</summary>
/// <param name="Version">The version players know it by ("3.29.3c").</param>
/// <param name="Name">Which build of that version ("3.29.3.5 hotfix of 2026-11-09, build 328126").</param>
/// <param name="ExecutableSha256">SHA-256 of its unpatched executable (<c>PathOfExile.exe</c>; Steam's
/// <c>PathOfExileSteam.exe</c> is the same file).</param>
/// <param name="HncMaskVa">Where its executable holds the 64-byte HNC mask (<c>HncSender</c>): data moves between
/// builds whose code does not.</param>
/// <param name="SteamIndexSha256">SHA-256 of <c>Bundles2/_.index.bin</c> in its Steam install; null when that build's
/// Steam files were never checked, and then its Steam install is not accepted.</param>
/// <param name="SteamManifests">Steam's manifest of each depot of <see cref="ClientBuilds.SteamApp"/> that holds this
/// build, for a tool that downloads it; null when they are not all known.</param>
public sealed record ClientBuild(string Version, string Name, string ExecutableSha256, long HncMaskVa, string? SteamIndexSha256 = null,
    IReadOnlyList<SteamManifest>? SteamManifests = null)
{
    public override string ToString() => $"{Version} ({Name})";
}

/// <summary>One depot of the game on Steam at one build: the game files are depot 238961, the Windows executables 238962.</summary>
public sealed record SteamManifest(uint Depot, ulong Manifest);

/// <summary>A client executable that is none of <see cref="ClientBuilds.Allowed"/>.</summary>
public sealed class UnknownClientBuildException(string message) : FormatException(message);

/// <summary>
/// The client builds LocalExile runs, and the only ones: a build is recognized by its executable's hash, and anything
/// else is refused. The server speaks one protocol, so every row is a build proven to speak it: the same code at the
/// same addresses and the same game tables as a build already here (how to check a new one:
/// Agents/docs/re/tooling.md section 11). A build that differs in either needs a port, not a row.
/// </summary>
public static class ClientBuilds
{
    /// <summary>Path of Exile's app id on Steam.</summary>
    public const uint SteamApp = 238960;

    public static IReadOnlyList<ClientBuild> Allowed { get; } =
    [
        // Its executables are depot 238962 manifest 6963407386864245615; its Steam game files were never checked.
        new("3.29.3c", "release, build 327984",
            "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c", 0x142feaed0),
        new("3.29.3c", "3.29.3.5 hotfix of 2026-11-09, build 328126",
            "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf", 0x142feaed0,
            SteamIndexSha256: "d23c78997206716586041a3e47a89dde0eb516f1488df4a1ccf39b0d51be54d0",
            SteamManifests: [new(238961, 2467965126630699088), new(238962, 1925736551784903498)]),
        new("3.29.3d", "release of 2026-11-10, build 328228",
            "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d", 0x142feaeb0,
            // Steam build 26554937: the index and PathOfExileSteam.exe are byte-identical to the standalone client's.
            SteamIndexSha256: "53560cc6391d2b11276f480ce5ebe86eb75a26935064c846aa99499f376bac29",
            SteamManifests: [new(238961, 5379264335337066375), new(238962, 8687196073893351989)]),
    ];

    /// <summary>For the person starting a client: which build theirs is, and the versions LocalExile supports.</summary>
    public static string SupportNote(ClientBuild build) =>
        $"This client is Path of Exile {build}. LocalExile supports {string.Join(" and ", Allowed.Select(b => b.Version).Distinct())}.";

    /// <summary>The versions of <see cref="Allowed"/>, for messages: "3.29.3c", or "3.29.3c or 3.29.3d".</summary>
    public static string Versions => VersionsOf(Allowed);

    public static string VersionsOf(IReadOnlyList<ClientBuild> builds) => string.Join(" or ", builds.Select(b => b.Version).Distinct());

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The allowed build this executable is, or null.</summary>
    public static ClientBuild? Find(ReadOnlySpan<byte> executable) => Find(executable, Allowed);

    public static ClientBuild? Find(ReadOnlySpan<byte> executable, IReadOnlyList<ClientBuild> builds)
    {
        string hash = Sha256(executable);
        return builds.FirstOrDefault(b => b.ExecutableSha256 == hash);
    }

    /// <summary>The allowed build this executable is; throws <see cref="UnknownClientBuildException"/> for any other
    /// file, a patched copy included (the hash is the unpatched executable's).</summary>
    public static ClientBuild Require(ReadOnlySpan<byte> executable) => Require(executable, Allowed);

    public static ClientBuild Require(ReadOnlySpan<byte> executable, IReadOnlyList<ClientBuild> builds)
    {
        string hash = Sha256(executable);
        return builds.FirstOrDefault(b => b.ExecutableSha256 == hash) ?? throw new UnknownClientBuildException(
            $"this is not a Path of Exile executable LocalExile runs (SHA-256 {hash[..16]}...): it runs {Describe(builds)}, and refuses any other build");
    }

    /// <summary>The allowed builds with these Steam game files (the hash of <c>Bundles2/_.index.bin</c>).</summary>
    public static IEnumerable<ClientBuild> WithSteamIndex(string indexSha256) => Allowed.Where(b => b.SteamIndexSha256 == indexSha256);

    /// <summary>Every build by name: "3.29.3c (release, build 327984); 3.29.3c (3.29.3.5 hotfix …)".</summary>
    public static string Describe(IEnumerable<ClientBuild> builds) => string.Join("; ", builds);
}
