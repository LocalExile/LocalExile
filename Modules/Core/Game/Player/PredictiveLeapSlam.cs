using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The predictive owner has already started this jump. Its positive-sequence 0x1ab only checks the old source
    // cell and snaps back when it is 92 cells away; 0x1ac alone acknowledges the pending action (07 §6).
    internal static bool PredictsLeapSlam(ClientState conn, string? effect, ushort sequence) =>
        !conn.LockstepClient && sequence < 0x8000 && effect == "LeapSlam";

    sealed record LeapSlamPeerAction(ConnectionCiphers Owner, byte[] Records);
    static readonly ConditionalWeakTable<byte[], LeapSlamPeerAction> LeapSlamPeerActions = new();

    // Key by the exact outgoing buffer, not by actor or sequence: queued and batched sends must keep their own
    // observer action. Outgoing sees this buffer before encryption; the weak key releases an unsent reply too.
    internal static byte[] LeapSlamAcknowledgement(ConnectionCiphers ciphers, byte[] peerAction)
    {
        var reply = InstanceProtocol.ActionEnded(PlayerId);
        LeapSlamPeerActions.Add(reply, new(ciphers, peerAction));
        return reply;
    }
}
