using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    /// <summary>Where the Crawler stands in the Azurite Mine Encampment, on the rails by the mine entrance, and its facing
    /// (placed live with /delve_crawler 408 310 90, 2026-10-30).</summary>
    static readonly (int X, int Y) DelveCrawlerAt = (408, 310);
    const float DelveCrawlerRotation = MathF.PI / 2;

    // The Delve Crawler in the Azurite Mine Encampment. A click on it opens the Subterranean Chart, as Niko's option does.
    static async Task SpawnDelveCrawler(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Hash != StashCatalog.DelveHub) return;
        var (x, y) = DelveCrawlerAt;
        uint id = conn.NextObjectId++;
        conn.DelveCrawlerObject = id;
        conn.DelveCrawlerAt = (x, y);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(DelveRobotObject.MetadataPath),
            new DelveRobotObject(x, y, DelveCrawlerRotation).Blob()), $"0x02b9 create the Delve Crawler 0x{id:x} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 Delve Crawler 0x{id:x} enters wake zone");
    }

    // C→S 0x24 on the Crawler: walk there, then the Subterranean Chart (S→C 0x7b kind 0x1c with 1).
    static async Task OpenDelveChart(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var at = conn.DelveCrawlerAt;
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, ClientState.StashBlockerSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 Delve Crawler ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 1), "0x007b open the Delve chart (Crawler)",
            still: () => PlayerActionCurrent(conn, action));
    }
}
