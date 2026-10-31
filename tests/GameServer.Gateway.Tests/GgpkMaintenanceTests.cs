using GameServer.Data.Ggpk;

namespace GameServer.Gateway.Tests;

public class GgpkMaintenanceTests
{
    /// <summary>scripts/dev/live.ps1 reads the gateway list (GameServer.Patch gateway) while other checkouts' clients run.
    /// A client opens Content.ggpk for reading and shares it only with readers; reading the list must still work. Uses
    /// the checkout's client GGPK (opened read-only, never written; the working directory is the repository root,
    /// TestEnvironment); without one there is nothing to check.</summary>
    [Fact]
    public void ReadsAFileWhileAClientHoldsTheGgpk()
    {
        string ggpk = Path.Combine("Client", "Content.ggpk");
        if (!File.Exists(ggpk)) return;
        using var client = new FileStream(ggpk, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[]? list = GgpkMaintenance.TryReadFile(ggpk, "Bundles2/gateway_list.txt") ?? GgpkMaintenance.TryReadFile(ggpk, "gateway_list.txt");
        Assert.NotNull(list);
        Assert.Equal(32, GgpkMaintenance.RootHash(ggpk).Length);
    }
}
