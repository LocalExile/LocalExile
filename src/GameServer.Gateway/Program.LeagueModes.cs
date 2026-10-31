using GameServer.Core.Accounts;
using System.Collections.Concurrent;
using GameServer.Core.Diagnostics;

partial class Program
{
    /// <summary>The character's league: Standard, or its module's (Modding/ServerLeagues.cs), decided once per connection.</summary>
    internal static LeagueRuntime LeagueFor(ClientState conn) =>
        conn.LeagueModule ??= ServerLeagues.Resolve(conn.LeagueForm, conn.League, LeagueCatalog.Startup);
}
