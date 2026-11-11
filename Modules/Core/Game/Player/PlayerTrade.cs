using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const uint PlayerTradeMine = NpcShops.FirstTabId + 0x10, PlayerTradeTheirs = PlayerTradeMine + 1;
    public const byte PlayerTradeWaitingFlags = InstanceNpcs.TradeActive,
        PlayerTradeOpenFlags = InstanceNpcs.TradeActive | InstanceSocial.TradeRequestFlag | InstanceNpcs.TradeNoMouseOver;
    public const byte PartyInvited = 0, PartyMember = 1, PartyLeader = 2;
    public const byte TradeMineAccepted = 0x04, TradeTheirsAccepted = 0x08;
}
