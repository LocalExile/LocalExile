using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // The original action guards the release only. Once its object exists, the arrow owns the area's flight lifetime.
    internal static bool OrdinaryBowReleaseCurrent(ClientState conn, int action, int epoch)
    {
        lock (conn.Monsters) return conn.ProjectileActive(epoch) && conn.PlayerLife > 0
            && conn.ActionCount == action && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // A pause can win the send gate after an action was accepted. Keep its echo pending rather than releasing an
    // unconfirmed arrow after resume; a later action, death or transfer still cancels it with its original token.
    internal static async Task<bool> SendOrdinaryBowAction(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte[] bytes, string? log, int action, int epoch)
    {
        bool sent = false;
        while (!sent && OrdinaryBowReleaseCurrent(conn, action, epoch))
        {
            await Send(remote, stream, ciphers, bytes, log, still: () =>
            {
                lock (conn.Monsters) return sent = OrdinaryBowReleaseCurrent(conn, action, epoch) && !conn.Paused;
            });
            if (!sent) await Task.Delay(16);
        }
        return sent;
    }

    static async Task ConfirmAndLaunchOrdinaryBow(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte[] actionBytes, string actionLog, (int X, int Y) from, (int X, int Y) aim,
        double attackSeconds, int action, int epoch)
    {
        await LaunchOrdinaryBow(remote, stream, ciphers, conn, from, aim, attackSeconds, action, epoch);
        await SendOrdinaryBowAction(remote, stream, ciphers, conn, actionBytes, actionLog, action, epoch);
    }

    static Task LaunchOrdinaryBow(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) from, (int X, int Y) aim, double attackSeconds, int action, int epoch) =>
        ScheduleOrdinaryBow(remote, stream, ciphers, conn, aim, attackSeconds, action, epoch);
}
