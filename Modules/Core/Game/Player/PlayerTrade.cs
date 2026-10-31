using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal const uint PlayerTradeMine = NpcShops.FirstTabId + 0x10, PlayerTradeTheirs = PlayerTradeMine + 1;
    internal const byte PlayerTradeWaitingFlags = InstanceNpcs.TradeActive,
        PlayerTradeOpenFlags = InstanceNpcs.TradeActive | InstanceSocial.TradeRequestFlag | InstanceNpcs.TradeNoMouseOver;
    internal const byte PartyInvited = 0, PartyMember = 1, PartyLeader = 2;
    internal const byte TradeMineAccepted = 0x04, TradeTheirsAccepted = 0x08;
}
