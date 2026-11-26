using GameServer.Core.Protocol.Login;
using System.Runtime.CompilerServices;

/// <summary>What a player the server drives takes part in beside playing: whoever opens it chooses (a client's player takes
/// part in all of it).</summary>
/// <param name="EarnsProgress">Its account's progress counts: achievements, play time, kill and death counts.</param>
/// <param name="Listed">It is found by name or account: whispers, /who, friends, the guild, party invites, travel to it.</param>
public sealed record ServerDriven(bool EarnsProgress = false, bool Listed = false);

public partial class Program
{
    static readonly ConditionalWeakTable<ConnectionCiphers, object> Unwired = new();

    /// <summary>Whether these are the ciphers of a player with no client: what is written to them goes nowhere.</summary>
    internal static bool HasNoWire(ConnectionCiphers ciphers) => Unwired.TryGetValue(ciphers, out _);

    /// <summary>
    /// Makes a player the server drives: one with no client (<see cref="Player.HasClient"/>). It counts as connected, so the
    /// rules run for it as for a client's player and the others' clients see what it does; what is sent to it is ordered and
    /// logged as any player's and then dropped. A module or mod gives it its inputs (the game's OnClientMessages), places it in
    /// an area and ends it as a connection ends: the framework holds no rules for one.
    /// </summary>
    public static void DriveByServer(Player player, string name, ServerDriven? takesPartIn = null)
    {
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        Unwired.Add(ciphers, new());
        player.Connection.Driven = takesPartIn ?? new();
        player.Connection.Session = new($"server:{name}", null!, ciphers);
        player.Connection.Outbox = (bytes, log) => Send(player.Connection.Session.Remote, null!, ciphers, bytes, log);
    }
}
