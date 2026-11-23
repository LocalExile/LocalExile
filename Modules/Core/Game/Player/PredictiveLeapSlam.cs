using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The predictive owner has already started this jump. Its positive-sequence 0x1ab only checks the old source
    // cell and snaps back when it is 92 cells away; 0x1ac alone acknowledges the pending action (07 §6).
    internal static bool PredictsLeapSlam(Player player, string? effect, ushort sequence) =>
        !player.Connection.LockstepClient && sequence < 0x8000 && effect == "LeapSlam";

    // In the area's frame the owner's client gets the 0x1ac alone and every other client the whole action; nothing is
    // left for the caller to send.
    internal static byte[] LeapSlamAcknowledgement(Player owner, byte[] peerAction)
    {
        var reply = InstanceProtocol.ActionEnded(owner.Id);
        if (!InAreaFrame(owner)) return reply;
        Program.ToArea(owner, peerAction, null, except: owner);
        Program.ToArea(owner, reply, null, only: owner);
        return [];
    }
}
