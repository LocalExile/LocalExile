using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;

// Server-controlled players use the same rules and ordered output as socket players. Only the
// final transport is absent; action replication still sees their unencrypted outgoing packets.
public interface IControlledPlayerRules
{
    CharacterSaves.Save BuildControlled(CharacterClass characterClass, int level);
    Task InitializeControlled(ClientState player, ClientState leader);
    Task StepControlled(ClientState player, ClientState leader);
    Task ControlledCompletion(ClientState player);
}

// Optional command boundary supplied by a game module. The transport does not implement gameplay rules.
public sealed record ControlledPlayerIntent(string Id, string Action, double Until, ushort Skill = 0,
    uint Target = 0, int X = 0, int Y = 0, int Flask = 0, long WallDeadline = 0);

public interface IControlledPlayerCommands
{
    double MaximumLeaderDistance { get; }
    void QueueControlledIntent(ClientState player, ControlledPlayerIntent intent);
    void ClearControlledIntent(ClientState player);
    object ObserveControlledPlayer(ClientState player, ClientState leader);
}

public static class ControlledPlayers
{
    static readonly ConditionalWeakTable<ConnectionCiphers, object> Transports = new();
    public static bool IsTransport(ConnectionCiphers ciphers) => Transports.TryGetValue(ciphers, out _);

    public static ClientState Open(Character character, string account, ClientState leader)
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

    public static void Close(ClientState player)
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
