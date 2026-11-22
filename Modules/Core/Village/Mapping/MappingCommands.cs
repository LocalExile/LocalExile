namespace GameServer.Modules.Village.Mapping;

/// <summary>
/// The chat command of the mapping line: one <c>/mapping</c> command with a subcommand for every move of the panel.
/// The line only exists in the Kingsmarch town area.
/// </summary>
public static class MappingCommands
{
    /// <summary>The area the mapping line belongs to (the client's <c>KalguuranSettlersLeague</c>).</summary>
    private const string Area = "KalguuranSettlersLeague";

    /// <summary>
    /// Registers the mapping command with the server.
    /// </summary>
    /// <param name="owner">The id of the module that owns the command.</param>
    public static void Register(string owner)
    {
        GameEvents.CommandsBuilding.Add(owner, (commands, remote, stream, ciphers, conn) =>
        {
            var player = Program.PlayerOf(conn);
            return commands.Add("mapping", "/mapping [status | start | suspend | stop | queue | assign | unassign | hire | reroll | fire | rescue | ransom | abandon | ack | take | give] (the Kingsmarch mapping devices)", async args =>
            {
                if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal))
                {
                    await player.Tell("The mapping line is only in Kingsmarch.");
                    return;
                }

                string who = player.Character ?? "";
                string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
                string report = verb switch
                {
                    "status" => MappingEngine.Status(who),
                    "start" => Device(who, verb, args, MappingEngine.Start),
                    "suspend" => Device(who, verb, args, MappingEngine.Suspend),
                    "stop" => Device(who, verb, args, MappingEngine.Interrupt),
                    "queue" => Queue(who, args),
                    "assign" => Pair(who, verb, args, MappingEngine.Assign),
                    "unassign" => Pair(who, verb, args, MappingEngine.Unassign),
                    "hire" => Hire(who, args),
                    "reroll" => Reroll(who, args),
                    "fire" => Fire(who, args),
                    "rescue" => Device(who, verb, args, MappingEngine.Rescue),
                    "ransom" => Device(who, verb, args, MappingEngine.Ransom),
                    "abandon" => Device(who, verb, args, MappingEngine.Abandon),
                    "ack" => Device(who, verb, args, MappingEngine.AcknowledgeLost),
                    "take" => Take(who, args),
                    "give" => Give(who, args),
                    _ => $"usage: /mapping {Usage(verb)}",
                };
                await player.Tell("Kingsmarch: " + report);
            });
        });
    }

    /// <summary>The usage of a subcommand.</summary>
    private static string Usage(string verb) => verb switch
    {
        "start" => "start <device>",
        "suspend" => "suspend <device>",
        "stop" => "stop <device>",
        "queue" => "queue <device> <mapTypeHash> <tier> <rarity> <affixes> [special]",
        "assign" => "assign <device> <runner>",
        "unassign" => "unassign <device> <runner>",
        "hire" => "hire <index>",
        "reroll" => "reroll [super]",
        "fire" => "fire <runner>",
        "rescue" => "rescue <device>",
        "ransom" => "ransom <device>",
        "abandon" => "abandon <device>",
        "ack" => "ack <device>",
        "take" => "take <device> [index]",
        "give" => "give <gold>",
        _ => "status",
    };

    /// <summary>A subcommand that names a device.</summary>
    private static string Device(string who, string verb, string[] args, Func<string, int, string> run)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int device))
            return $"usage: /mapping {Usage(verb)}";
        return run(who, device);
    }

    /// <summary>A subcommand that names a device and a runner.</summary>
    private static string Pair(string who, string verb, string[] args, Func<string, int, int, string> run)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out int device) || !int.TryParse(args[2], out int runner))
            return $"usage: /mapping {Usage(verb)}";
        return run(who, device, runner);
    }

    /// <summary>The subcommand that queues a map.</summary>
    private static string Queue(string who, string[] args)
    {
        if (args.Length < 6
            || !int.TryParse(args[1], out int device)
            || !uint.TryParse(args[2], out uint map)
            || !int.TryParse(args[3], out int tier)
            || !int.TryParse(args[4], out int rarity)
            || !int.TryParse(args[5], out int affixes))
            return $"usage: /mapping {Usage("queue")}";
        if (rarity < 0 || rarity > 3) return "The rarity of a map runs from 0 (white) to 3 (purple).";
        string special = args.Length > 6 ? args[6] : "";
        return MappingEngine.QueueMap(who, device, map, tier, (byte)rarity, affixes, special);
    }

    /// <summary>The subcommand that hires a candidate.</summary>
    private static string Hire(string who, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int index)) return $"usage: /mapping {Usage("hire")}";
        return MappingEngine.Hire(who, index);
    }

    /// <summary>The subcommand that rerolls the candidates.</summary>
    private static string Reroll(string who, string[] args)
    {
        bool super = args.Length > 1 && args[1].Equals("super", StringComparison.OrdinalIgnoreCase);
        return MappingEngine.Reroll(who, super);
    }

    /// <summary>The subcommand that fires a runner.</summary>
    private static string Fire(string who, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int runner)) return $"usage: /mapping {Usage("fire")}";
        return MappingEngine.Fire(who, runner);
    }

    /// <summary>The subcommand that takes the rewards of a device.</summary>
    private static string Take(string who, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int device)) return $"usage: /mapping {Usage("take")}";
        int index = -1;
        if (args.Length > 2 && !int.TryParse(args[2], out index)) return $"usage: /mapping {Usage("take")}";
        return MappingEngine.TakeRewards(who, device, index);
    }

    /// <summary>The subcommand that puts gold into the treasury, for testing the line.</summary>
    private static string Give(string who, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int gold) || gold <= 0) return $"usage: /mapping {Usage("give")}";
        long total = KingsmarchPort.Fund(who, gold);
        return $"{gold} gold goes into the treasury; it holds {total} now.";
    }
}
