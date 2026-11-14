using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>C→S 0x92 "Travel": the party panel's button on a member's row (#715). The client only asks — the bytes are the
/// message id and that member's character name, as the party invite's are — and the server moves the player, as the real
/// game's own server does.</summary>
static class PartyTravel
{
    /// <summary>The member the request names, or null when this party has none by that character and account name (then
    /// the name may be another module's, as a party bot's is).</summary>
    internal static ClientState? Member(ClientState conn, string name)
    {
        uint party = PartyRoster.PartyOf(conn);
        if (party == 0) return null;
        return PartyRoster.Players(party).FirstOrDefault(c => !ReferenceEquals(c, conn)
            && (string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Account, name, StringComparison.OrdinalIgnoreCase)));
    }

    // The player changes area to the member's: with no instance picked, the party's transfer instance (PartyRoster.
    // SharedInstance) makes that the member's own instance of the area, so a party plays its maps together. The area's
    // spawn is the arrival, as the member may be anywhere in it.
    internal static async Task<bool> Travel(ClientState conn, string name)
    {
        var member = Member(conn, name);
        if (member is null) return false;
        string remote = conn.Wire.Remote;
        if (member.Layout.Hash == conn.Layout.Hash)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0092 travel to {name}: already in {conn.Layout.Id}");
            return true;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x0092 travel to {name}: {member.Layout.Id} (0x{member.Layout.Hash:x4})");
        await ChangeArea(remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
            new PendingTransfer(member.Layout.Hash, 0, false, AtSpawn: true), $"party panel: travel to {name}");
        return true;
    }
}
