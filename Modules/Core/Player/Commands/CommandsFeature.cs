using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have. The development and testing commands are the gm-commands mod
/// (LocalExile-Mods).</summary>
public static class CommandsFeature
{
    static Dictionary<string, string> AreaNames = new(StringComparer.OrdinalIgnoreCase);

    internal static void LoadData()
    {
        AreaNames = ClientTables.TableOrNull("WorldAreas") is { } rows
            ? rows.Where(r => !string.IsNullOrEmpty(r.Text("Id")) && !string.IsNullOrEmpty(r.Text("Name")))
                .GroupBy(r => r.Text("Id")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Text("Name")!, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    internal static ChatCommands Register(ChatCommands commands, Player player, CharacterPlaytime played, CharacterDeaths deaths, CharacterKills kills)
    {
        return commands
        .Add("played", "/played (time spent online with this character)", async _ =>
            await player.Commands!.Reply(CharacterPlaytime.Describe(played.Seconds(player))))
        .Add("age", "/age (how long ago this character was created)", async _ =>
            await player.Commands!.Reply(DescribeAge(player)))
        .Add("remaining", "/remaining (alive hostile monsters remaining in this area)", async _ =>
            await ReplyRemaining(player))
        .Add("deaths", "/deaths (how many times this character has died)", async _ =>
            await player.Commands!.Reply(CharacterDeaths.Describe(deaths.Count(player))))
        .Add("kills", "/kills (experience-granting monsters slain on this character)", async _ =>
            await player.Commands!.Reply(CharacterKills.Describe(kills.Count(player))))
        .Add("itemlevel", "/itemlevel (item level of the item on your cursor)", async _ =>
            await player.Commands!.Reply(DescribeItemLevel(player)))
        .Add("whois", "/whois <name> (level, class, league and current area of a character)", async args =>
            await player.Commands!.Reply(DescribeWhois(string.Join(' ', args))))
        .Add("ladder", "/ladder (top ten characters on the current league ladder)", async _ =>
            await player.Commands!.Reply(DescribeLadder(player)))
        .Add("invite", "/invite <name> (invite an online player to your party)", async args =>
            await InviteAsync(player, string.Join(' ', args)))
        .Add("afk", "/afk [message] (toggle AFK mode and whisper autoreply)", async args =>
            await player.Commands!.Reply(ChatPresence.ToggleAfk(player, string.Join(' ', args))))
        .Add("autoreply", "/autoreply [message] (set or clear whisper autoreply)", async args =>
            await player.Commands!.Reply(ChatPresence.SetAutoReply(player, string.Join(' ', args))))
        .Add("dnd", "/dnd [message] (toggle Do Not Disturb mode and ignore incoming chat)", async args =>
            await player.Commands!.Reply(ChatPresence.ToggleDnd(player, string.Join(' ', args))))
        .Add("donotdisturb", "/donotdisturb [message] (toggle Do Not Disturb mode)", async args =>
            await player.Commands!.Reply(ChatPresence.ToggleDnd(player, string.Join(' ', args))))
        .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
        {
            var (points, refundPoints, nodes, _) = PassiveState(player);
            int unspent = points + PassivesFor(player).PointsGranted(nodes) + AscendancyOrdinaryPoints(player) - nodes.Count;
            await player.Commands!.Reply($"Level {player.Pools.Level}, {player.Character.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
                string.Join("\n", nodes.Select(n => $"{n} {PassivesFor(player).Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
        })
        .Add("atlaspassives", "/atlaspassives (breakdown of your Atlas Passive Skill points)", async _ =>
            await player.Commands!.Reply(DescribeAtlasPassives(player)))
        .Add("oos", "/oos (puts you and the monsters around you back where the server has them when you are out of sync)", async _ =>
        {
            await SendResync(player, "/oos");
            await player.Commands!.Reply("Resynced.");
        })
        .Add("oosme", "/oosme (resyncs your position with the server)", async _ =>
        {
            await SendResync(player, "/oosme");
            await player.Commands!.Reply("Resynced.");
        });
    }

    internal static async Task ReplyRemaining(Player player)
    {
        string reply = DescribeRemaining(player);
        await SendRemainingMonsters(player, force: true);
        await player.Commands!.Reply(reply);
    }

    public static string DescribeRemaining(Player player)
    {
        if (InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null)
            return "0 monsters remain.";
        int count = LivingMonstersLocked(player);
        return count switch
        {
            <= 0 => "0 monsters remain.",
            1 => "1 monster remains.",
            <= 50 => $"{count} monsters remain.",
            _ => "More than 50 monsters remain.",
        };
    }

    internal static string DescribeAge(Player player)
    {
        var summary = CharacterSaves.LookupCharacter(CharacterName(player));
        if (summary is null) return "Your character has no record of creation time.";
        return FormatAge((DateTimeOffset.UtcNow - summary.CreatedUtc).TotalSeconds);
    }

    internal static string FormatAge(double seconds)
    {
        long whole = (long)Math.Min(Math.Max(0, seconds), long.MaxValue / 2);
        static string Unit(long n, string name) => $"{n} {name}{(n == 1 ? "" : "s")}";
        // BackendErrors CharacterAge (212): "Your character was created {0} ago."
        return $"Your character was created {Unit(whole / 86400, "day")}, {Unit(whole / 3600 % 24, "hour")}, {Unit(whole / 60 % 60, "minute")} and {Unit(whole % 60, "second")} ago.";
    }

    internal static string DescribeItemLevel(Player player)
    {
        var cursor = ItemsOf(player).Items(PlayerInventories.Cursor).FirstOrDefault();
        return cursor is null ? "No item is currently held on the cursor." : $"Item Level: {cursor.Item.ItemLevel}";
    }

    public static string DescribeWhois(string rawTarget)
    {
        string targetName = rawTarget.Trim();
        if (targetName.Length == 0) return "Usage: /whois <character name>";
        var online = InGame.Keys.FirstOrDefault(c => !c.Controlled && !c.Closed
            && string.Equals(CharacterName(c), targetName, StringComparison.OrdinalIgnoreCase));
        if (online is not null)
        {
            string area = AreaNames.GetValueOrDefault(online.Layout.Id) ?? AreaTransitionCatalog.NameOf(online.Layout.Hash) ?? online.Layout.Id;
            string cls = ClassDisplayName(online);
            string league = string.IsNullOrWhiteSpace(online.Character.League) ? "Standard" : online.Character.League;
            // BackendErrors WhoIsOnline (200): "{0} is a level {2} {3} in the {1} league and is currently playing in {4}."
            return $"{CharacterName(online)} is a level {online.Pools.Level} {cls} in the {league} league and is currently playing in {area}.";
        }
        var saved = CharacterSaves.LookupCharacter(targetName);
        if (saved is not null)
        {
            // BackendErrors WhoIsOffline (199): "{0} is a level {2} {3} in the {1} league who is not currently online."
            return $"{saved.Name} is a level {saved.Level} {ClassDisplayName(saved.Class, saved.Ascendancy)} in the {saved.League} league who is not currently online.";
        }
        return "The specified character does not exist.";
    }

    internal static string DescribeLadder(Player player)
    {
        string league = string.IsNullOrWhiteSpace(player.Character.League) ? "Standard" : player.Character.League;
        var onlineByName = InGame.Keys
            .Where(c => !c.Controlled && !c.Closed && !c.LeavingArea
                && string.Equals(string.IsNullOrWhiteSpace(c.Character.League) ? "Standard" : c.Character.League, league, StringComparison.OrdinalIgnoreCase))
            .GroupBy(CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rows = CharacterSaves.TopLeagueCharacters(league, 10)
            .Select(s =>
            {
                if (onlineByName.TryGetValue(s.Name, out var live))
                    return (Name: CharacterName(live), Level: live.Pools.Level, Class: ClassDisplayName(live),
                        Exp: live.Character.Experience, Dead: s.HardcoreDead || live.Character.HardcoreDeathRecorded);
                return (Name: s.Name, Level: s.Level, Class: ClassDisplayName(s.Class, s.Ascendancy),
                    Exp: s.Experience, Dead: s.HardcoreDead);
            })
            .OrderByDescending(r => r.Level)
            .ThenByDescending(r => r.Exp)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (rows.Count == 0) return $"No characters found on the {league} ladder.";
        // BackendErrors LadderRankAlive (140) / LadderRankDead (141).
        return string.Join("\n", rows.Select((r, i) =>
            $"{i + 1}. {r.Name}{(r.Dead ? " (Dead)" : "")} Level {r.Level} {r.Class} Exp: {r.Exp:N0}"));
    }

    internal static string DescribeAtlasPassives(Player player)
    {
        int preset = AtlasActivePreset(player);
        var allocated = player.Character.AtlasPassives[preset];
        ushort total = player.Character.AtlasPassivePoints;
        int unspent = Math.Max(0, total - allocated.Count);
        ushort respec = AtlasRespecPoints(player);
        var catalog = LeagueFor(player).HardMode && HardModeAtlasPassives is not null ? HardModeAtlasPassives : AtlasPassiveNodes;
        string names = allocated.Count == 0
            ? "No Atlas passives allocated."
            : string.Join(", ", allocated.Select(id => catalog?.Find(id)?.Name ?? id.ToString()));
        return $"Atlas Tree {preset + 1}: {allocated.Count}/{total} Atlas passive skill point(s) allocated, {unspent} unspent, {respec} respec point(s).\n{names}";
    }

    internal static async Task InviteAsync(Player player, string rawTarget)
    {
        string targetName = rawTarget.Trim();
        if (targetName.Length == 0)
        {
            await player.Commands!.Reply("Usage: /invite <character name>");
            return;
        }
        if (await GameEvents.RunPartyInvite(player, targetName))
            return;
        await player.Commands!.Reply("Player not found.");
    }
}
