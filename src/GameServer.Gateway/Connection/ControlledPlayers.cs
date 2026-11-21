using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;
using AccountCharacter = GameServer.Core.Accounts.Character;
using GameServer.Core.Protocol.Login;

// Server-controlled players use the same rules and ordered output as socket players. Only the
// final transport is absent; action replication still sees their unencrypted outgoing packets.
public interface IControlledPlayerRules
{
    CharacterSaves.Save BuildControlled(CharacterClass characterClass, int level);
    Task InitializeControlled(Player player, Player leader);
    Task StepControlled(Player player, Player leader);
    Task ControlledCompletion(Player player);
}

// Optional command boundary supplied by a game module. The transport does not implement gameplay rules.
public sealed record ControlledPlayerIntent(string Id, string Action, double Until, ushort Skill = 0,
    uint Target = 0, int X = 0, int Y = 0, int Flask = 0, long WallDeadline = 0);

public interface IControlledPlayerCommands
{
    double MaximumLeaderDistance { get; }
    void QueueControlledIntent(Player player, ControlledPlayerIntent intent);
    void ClearControlledIntent(Player player);
    object ObserveControlledPlayer(Player player, Player leader);
}

public static class ControlledPlayers
{
    static readonly ConditionalWeakTable<ConnectionCiphers, object> Transports = new();
    public static bool IsTransport(ConnectionCiphers ciphers) => Transports.TryGetValue(ciphers, out _);

    public static Player Open(AccountCharacter character, string account, Player leader)
    {
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        Transports.Add(ciphers, new());
        var player = new Player(leader.Layout, 0, leader.Seed)
        {
            Character = { Name = character.Name, Id = character.Id, Account = account, League = character.League, ClassRow = Program.Skills.RowOfClass((int)character.Class), LastTown = leader.Character.LastTown },
            Pools = CharacterStats.For(Program.Skills.ClassFor(Program.Skills.RowOfClass((int)character.Class)), character.Level),
            Instance = leader.Instance,
            InstanceNumber = leader.InstanceNumber,
            Controlled = true
        };
        // The stream is borrowed solely as an opaque routing argument. Write rejects its virtual
        // cipher before touching or encrypting anything; closing a bot never disposes the leader's stream.
        player.Connection.Session = new($"bot:{character.Name}", leader.Connection.Session.Stream, ciphers);
        player.Connection.Outbox = (bytes, log) => Program.Send(player.Connection.Session.Remote, player.Connection.Session.Stream, ciphers, bytes, log);
        return player;
    }

    public static void Close(Player player)
    {
        if (player.Closed) return;
        var updates = (Program.GameLogicOrNull as IControlledPlayerRules)?.ControlledCompletion(player) ?? Task.CompletedTask;
        player.Closed = true;
        Program.GameLogicOrNull?.Closing(player);
        Program.InGame.TryRemove(player, out _);
        player.Instance.RemovePlayer(player.Id);
        // Its updates run in the area's frame, maybe the one calling: they end there, and it is saved after them.
        _ = Finish();

        async Task Finish()
        {
            try { await updates; } catch (OperationCanceledException) { }
            Program.GameLogicOrNull?.Save(player, prepared: player.ControlledPrepared);
            Program.GameLogicOrNull?.Left(player);
        }
    }
}
