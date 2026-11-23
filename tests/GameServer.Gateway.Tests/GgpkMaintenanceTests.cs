using GameServer.Data.Ggpk;

namespace GameServer.Gateway.Tests;

public class GgpkMaintenanceTests
{
    /// <summary>scripts/dev/live.ps1 reads the gateway list (GameServer.Patch gateway) while other checkouts' clients run.
    /// A client opens Content.ggpk for reading and shares it only with readers; reading the list must still work. Uses
    /// the checkout's client GGPK (opened read-only, never written; the working directory is the repository root,
    /// TestEnvironment); skipped without one.</summary>
    [ClientArchiveFact]
    public void ReadsAFileWhileAClientHoldsTheGgpk()
    {
        string ggpk = Path.Combine("Client", "Content.ggpk");
        using var client = new FileStream(ggpk, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[]? list = ClientFiles.TryReadFile(ggpk, "Bundles2/gateway_list.txt") ?? ClientFiles.TryReadFile(ggpk, "gateway_list.txt");
        Assert.NotNull(list);
        Assert.Equal(32, GgpkMaintenance.RootHash(ggpk).Length);
    }
}
