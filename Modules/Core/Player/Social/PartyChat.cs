using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>A party's chat: a "%…" line, sent to every human member of the sender's party as a 0x17 whose sender has the
/// party prefix. The members need not be in the sender's area: the party is what the line reaches.</summary>
public static class PartyChat
{
    /// <summary>Carry one party line to the party; false when the sender is in none, so nothing delivered it.</summary>
    public static async Task<bool> Deliver(ClientState conn, string text, IReadOnlyList<ChatLineItem>? items)
    {
        uint party = PartyRoster.PartyOf(conn);
        if (party == 0) return false;
        string name = CharacterName(conn);
        string sender = InstanceChat.Sender(TypedChannel.Party, name);
        bool delivered = false;
        foreach (var member in PartyRoster.Players(party))
        {
            if (member is not { Controlled: false, Closed: false, Outbox: { } outbox }) continue;
            // Each member gets its own bytes, as a send encrypts them in place: a second client given the same array read
            // noise and crashed (live 2026-11-07).
            try
            {
                await outbox(InstanceChat.ChatLine(text, sender, items: items), $"0x0017 party chat line from {name}");
                delivered = true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
        return delivered;
    }
}
