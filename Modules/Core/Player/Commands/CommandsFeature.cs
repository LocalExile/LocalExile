using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.M_commands;
using GameServer.Storage;

/// <summary>The chat commands GGG's servers have. The development and testing commands are the gm-commands mod
/// (LocalExile-Mods).</summary>
static class CommandsFeature
{
    static readonly Lazy<Dictionary<string, string>> AreaNames = new(() =>
        ClientTables.TableOrNull("WorldAreas") is { } rows
            ? rows.Where(r => !string.IsNullOrEmpty(r.Text("Id")) && !string.IsNullOrEmpty(r.Text("Name")))
                .GroupBy(r => r.Text("Id")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Text("Name")!, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase));

    internal static ChatCommands Register(ChatCommands commands, string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, CharacterPlaytime played, CharacterDeaths deaths)
    {
        return commands
        .Add("played", "/played (time spent online with this character)", async _ =>
            await conn.Commands!.Reply(CharacterPlaytime.Describe(played.Seconds(conn))))
        .Add("age", "/age (how long ago this character was created)", async _ =>
            await conn.Commands!.Reply(DescribeAge(conn)))
        .Add("remaining", "/remaining (alive hostile monsters remaining in this area)", async _ =>
            await conn.Commands!.Reply(DescribeRemaining(conn)))
        .Add("deaths", "/deaths (how many times this character has died)", async _ =>
            await conn.Commands!.Reply(CharacterDeaths.Describe(deaths.Count(conn))))
        .Add("whois", "/whois <name> (level, class, league and current area of a character)", async args =>
            await conn.Commands!.Reply(DescribeWhois(string.Join(' ', args))))
        .Add("invite", "/invite <name> (invite an online player to your party)", async args =>
            await InviteAsync(conn, string.Join(' ', args)))
        .Add("afk", "/afk [message] (toggle AFK mode and whisper autoreply)", async args =>
            await conn.Commands!.Reply(ChatPresence.ToggleAfk(conn, string.Join(' ', args))))
        .Add("autoreply", "/autoreply [message] (set or clear whisper autoreply)", async args =>
            await conn.Commands!.Reply(ChatPresence.SetAutoReply(conn, string.Join(' ', args))))
        .Add("dnd", "/dnd [message] (toggle Do Not Disturb mode and block incoming whispers)", async args =>
            await conn.Commands!.Reply(ChatPresence.ToggleDnd(conn, string.Join(' ', args))))
        .Add("donotdisturb", "/donotdisturb [message] (toggle Do Not Disturb mode)", async args =>
            await conn.Commands!.Reply(ChatPresence.ToggleDnd(conn, string.Join(' ', args))))
        .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
        {
            var (points, refundPoints, nodes, _) = PassiveState(conn);
            int unspent = points + PassivesFor(conn).PointsGranted(nodes) + AscendancyOrdinaryPoints(conn) - nodes.Count;
            await conn.Commands!.Reply($"Level {conn.Pools.Level}, {conn.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
                string.Join("\n", nodes.Select(n => $"{n} {PassivesFor(conn).Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
        })
        .Add("oos", "/oos (puts you and the monsters around you back where the server has them when you are out of sync)", async _ =>
        {
            await SendResync(remote, stream, ciphers, conn, "/oos");
            await conn.Commands!.Reply("Resynced.");
        });
    }

    internal static string DescribeRemaining(ClientState conn)
    {
        if (InTown(conn) || HideoutCatalog.ByArea(conn.Layout.Hash) is not null)
            return "0 monsters remain.";
        int count;
        lock (conn.Monsters) count = LivingMonstersLocked(conn);
        return count switch
        {
            <= 0 => "0 monsters remain.",
            1 => "1 monster remains.",
            <= 50 => $"{count} monsters remain.",
            _ => "More than 50 monsters remain.",
        };
    }

    internal static string DescribeAge(ClientState conn)
    {
        var character = new SqliteAccountStore(Saves.Db).FindCharacter(CharacterName(conn));
        if (character is null) return "Character creation time is unavailable.";
        return FormatAge((DateTimeOffset.UtcNow - character.CreatedUtc).TotalSeconds);
    }

    internal static string FormatAge(double seconds)
    {
        long whole = (long)Math.Min(Math.Max(0, seconds), long.MaxValue / 2);
        static string Unit(long n, string name) => $"{n} {name}{(n == 1 ? "" : "s")}";
        return $"This character was created {Unit(whole / 86400, "day")}, {Unit(whole / 3600 % 24, "hour")}, {Unit(whole / 60 % 60, "minute")} and {Unit(whole % 60, "second")} ago.";
    }

    internal static string DescribeWhois(string rawTarget)
    {
        string targetName = rawTarget.Trim();
        if (targetName.Length == 0) return "Usage: /whois <character name>";
        var online = InGame.Keys.FirstOrDefault(c => !c.Controlled && !c.Closed
            && string.Equals(CharacterName(c), targetName, StringComparison.OrdinalIgnoreCase));
        if (online is not null)
        {
            string area = AreaNames.Value.GetValueOrDefault(online.Layout.Id) ?? online.Layout.Id;
            var baseCls = SkillsFor(online).ClassFor(online.ClassRow);
            string cls = Game.AscendancyRules.Validate((byte)baseCls.IntegerId, online.MainAscendancy)?.Name
                ?? (Enum.IsDefined(typeof(CharacterClass), baseCls.IntegerId) ? ((CharacterClass)baseCls.IntegerId).ToString() : "Exile");
            string league = string.IsNullOrWhiteSpace(online.League) ? "Standard" : online.League;
            return $"{CharacterName(online)} is a level {online.Pools.Level} {cls} in the {league} league who is currently playing in {area}.";
        }
        var saved = new SqliteAccountStore(Saves.Db).FindCharacter(targetName);
        if (saved is not null)
            return $"{saved.Name} is a level {saved.Level} {saved.Class} in the {saved.League} league who is currently offline.";
        return "The specified character does not exist.";
    }

    internal static async Task InviteAsync(ClientState conn, string rawTarget)
    {
        string targetName = rawTarget.Trim();
        if (targetName.Length == 0)
        {
            await conn.Commands!.Reply("Usage: /invite <character name>");
            return;
        }
        if (AppContext.GetData("LocalExile.Social.InviteHook") is Func<ClientState, string, Task<bool>> invite
            && await invite(conn, targetName))
            return;
        await conn.Commands!.Reply("Player not found.");
    }
}
