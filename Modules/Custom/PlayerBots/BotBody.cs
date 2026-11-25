using System.Runtime.CompilerServices;
using AccountCharacter = GameServer.Core.Accounts.Character;

namespace GameServer.Modules.PlayerBots;

/// <summary>One order for a bot (<c>auto</c>, <c>hold</c>, <c>move</c>, <c>attack</c>, <c>cast</c>, <c>flask</c>, <c>focus</c>,
/// <c>pickup</c>) and how long it holds.</summary>
public sealed record ControlledPlayerIntent(string Id, string Action, double Until, ushort Skill = 0,
    uint Target = 0, int X = 0, int Y = 0, int Flask = 0, long WallDeadline = 0);

public static partial class BotPlay
{
    /// <summary>What a bot's player keeps between steps. Prepared: its save may be written.</summary>
    public sealed class Body
    {
        public bool Prepared;
        public double NextAction, DeadSince = double.NaN;
    }

    static readonly ConditionalWeakTable<Player, Body> Bodies = new();
    public static Body BodyOf(Player player) => Bodies.GetValue(player, _ => new());

    /// <summary>A bot's player in its leader's instance: a player the server drives (Program.DriveByServer), which earns its
    /// account nothing and is not found by name.</summary>
    public static Player Open(AccountCharacter character, string account, Player leader)
    {
        var player = new Player(leader.Layout, 0, leader.Seed)
        {
            Character = { Name = character.Name, Id = character.Id, Account = account, League = character.League, ClassRow = Program.Skills.RowOfClass((int)character.Class), LastTown = leader.Character.LastTown },
            Pools = CharacterStats.For(Program.Skills.ClassFor(Program.Skills.RowOfClass((int)character.Class)), character.Level),
            Instance = leader.Instance,
            InstanceNumber = leader.InstanceNumber,
        };
        Program.DriveByServer(player, character.Name!);
        // It gives itself the predictive mode's messages.
        player.Connection.LockstepClient = false;
        return player;
    }

    /// <summary>Ends a bot's player as a connection ends: out of the game and its instance, saved after its last update.</summary>
    public static void Close(Player player)
    {
        if (player.Closed) return;
        var updates = player.PlayerUpdatesState is PlayerUpdatesState state ? state.Completion : Task.CompletedTask;
        player.Closed = true;
        Program.GameLogicOrNull?.Closing(player);
        Program.InGame.TryRemove(player, out _);
        player.Instance.RemovePlayer(player.Id);
        // Its updates run in the area's frame, maybe the one calling: they end there, and it is saved after them.
        _ = Finish();

        async Task Finish()
        {
            try { await updates; } catch (OperationCanceledException) { }
            Program.GameLogicOrNull?.Save(player, prepared: BodyOf(player).Prepared);
            Program.GameLogicOrNull?.Left(player);
        }
    }
}
