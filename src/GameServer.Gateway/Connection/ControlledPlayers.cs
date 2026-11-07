using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;

// Server-controlled players use the same rules and ordered output as socket players. Only the
// final transport is absent; action replication still sees their unencrypted outgoing packets.
interface IControlledPlayerRules
{
    CharacterSaves.Save BuildControlled(CharacterClass characterClass, int level);
    Task InitializeControlled(ClientState player, ClientState leader);
    Task StepControlled(ClientState player, ClientState leader);
    Task ControlledCompletion(ClientState player);
}

static class ControlledPlayers
{
    static readonly ConditionalWeakTable<ConnectionCiphers, object> Transports = new();
    internal static bool IsTransport(ConnectionCiphers ciphers) => Transports.TryGetValue(ciphers, out _);

    internal static ClientState Open(Character character, string account, ClientState leader)
    {
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        Transports.Add(ciphers, new());
        var player = new ClientState(leader.Layout, 0, leader.Seed)
        {
            Character = character.Name, CharacterId = character.Id, Account = account, League = character.League,
            ClassRow = Program.Skills.RowOfClass((int)character.Class),
            Pools = CharacterStats.For(Program.Skills.ClassFor(Program.Skills.RowOfClass((int)character.Class)), character.Level),
            Instance = leader.Instance, InstanceNumber = leader.InstanceNumber, LastTown = leader.LastTown,
            Controlled = true,
        };
        // The stream is borrowed solely as an opaque routing argument. Write rejects its virtual
        // cipher before touching or encrypting anything; closing a bot never disposes the leader's stream.
        player.Wire = ($"bot:{character.Name}", leader.Wire.Stream, ciphers);
        player.Outbox = (bytes, log) => Program.Send(player.Wire.Remote, player.Wire.Stream, ciphers, bytes, log);
        return player;
    }

    internal static void Close(ClientState player)
    {
        if (player.Closed) return;
        var updates = (Program.GameLogicOrNull as IControlledPlayerRules)?.ControlledCompletion(player) ?? Task.CompletedTask;
        player.Closed = true;
        Program.GameLogicOrNull?.Closing(player);
        Program.InGame.TryRemove(player, out _);
        player.Instance.RemovePlayer(player.PeerId);
        try { updates.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Program.GameLogicOrNull?.Save(player, prepared: player.ControlledPrepared);
        Program.GameLogicOrNull?.Left(player);
    }
}
