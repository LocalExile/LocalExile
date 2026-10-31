using GameServer.Core.Protocol.Instance;

/// <summary>
/// Slash commands typed in the client's chat box (C→S 0x15, Agents/docs/protocol/10-chat-and-commands.md). Dev tools for now:
/// every command is accepted from every player.
/// </summary>
sealed class ChatCommands
{
    /// <summary>Sends one S→C message (bytes, log line).</summary>
    public required Func<byte[], string, Task> Send { get; init; }

    /// <summary>Tells the player something (chat lines, one per line of the text).</summary>
    public required Func<string, Task> Reply { get; init; }

    /// <summary>The loaded mods' commands (ModHost), run when none of ours has the name: false when no mod has it either.
    /// Asked at every use, so a mod's reload changes them on a connection that is already open.</summary>
    public Func<string, string[], Task<bool>>? ModCommand { get; init; }

    /// <summary>The loaded mods' command names and usages, for /commands.</summary>
    public Func<IEnumerable<(string Name, string Usage)>>? ModUsages { get; init; }

    /// <summary>Commands per /help page.</summary>
    public const int PageSize = 8;

    private readonly Dictionary<string, (string Usage, Func<string[], Task> Run, string[] Arguments)> _commands = new(StringComparer.OrdinalIgnoreCase);

    // Ours, then the mods' that ours do not shadow.
    IEnumerable<(string Name, string Usage)> All() => _commands.Select(c => (c.Key, c.Value.Usage))
        .Concat((ModUsages?.Invoke() ?? []).Where(m => !_commands.ContainsKey(m.Name)));

    /// <param name="arguments">What each leading argument names, for an external tool's suggestions
    /// (<see cref="Program.CompletionValues"/>: area, monster, item, gem, stat, npc, quest_flag); the other arguments
    /// can be suggested from the usage's own choices ("[normal|magic|rare]").</param>
    public ChatCommands Add(string name, string usage, Func<string[], Task> run, params string[] arguments)
    {
        _commands[name] = (usage, run, arguments);
        return this;
    }

    /// <summary>Runs a command line ("/name args..."). Returns false if it is not one of ours (unknown commands are
    /// answered with a pointer to /commands).</summary>
    public async Task<bool> RunAsync(string line)
    {
        if (!line.StartsWith('/')) return false;
        string[] parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts[0].Equals("commands", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("com", StringComparison.OrdinalIgnoreCase))
        {
            await Reply(parts.Length < 2 ? List(1) : int.TryParse(parts[1], out int page) ? List(page) : Lookup(parts[1]));
            return true;
        }
        if (!_commands.TryGetValue(parts[0], out var cmd))
        {
            if (ModCommand is not null && await ModCommand(parts[0], parts[1..])) return true;
            await Reply($"Unknown command /{parts[0]}. Type /commands (or /com) for the list.");
            return false;
        }
        try { await cmd.Run(parts[1..]); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            await Reply($"Usage: {cmd.Usage} ({ex.Message})");
        }
        return true;
    }

    /// <summary>One /commands page (1-based, clamped): four blank lines, a green header line, one usage per line
    /// without its explanation, a blank line and how to go on. The client draws a sender-less line's text up to the
    /// first ": " as a speaker name (green), so the header ends with ':' and the usages must not contain ": ".</summary>
    public string List(int page)
    {
        string[] usages = [.. All().Select(c => ShortUsage(c.Usage)).Order()];
        int pages = Math.Max(1, (usages.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pages);
        string footer = (page < pages ? $"/com {page + 1} for the next page, " : "") + "/com <name> for one command";
        return string.Join('\n', usages.Skip((page - 1) * PageSize).Take(PageSize)
            .Prepend($"--- Commands page {page}/{pages} ---:").Prepend("\n\n\n").Append("").Append(footer));
    }

    /// <summary>Every command's name, full usage and argument kinds by name, /commands and the mods' included
    /// (`GameServer.Gateway commands`, for tools such as the desktop launcher).</summary>
    public IEnumerable<(string Name, string Usage, string[] Arguments)> Usages =>
        _commands.Select(c => (c.Key, c.Value.Usage, c.Value.Arguments))
            .Concat((ModUsages?.Invoke() ?? []).Where(m => !_commands.ContainsKey(m.Name)).Select(m => (m.Name, m.Usage, Array.Empty<string>())))
            .Append(("commands", "/commands [page | name] (/com; the command list, or the commands whose name contains it)", []))
            .OrderBy(c => c.Item1, StringComparer.OrdinalIgnoreCase);

    /// <summary>The usage without its " (explanation)".</summary>
    static string ShortUsage(string usage) => usage.IndexOf(" (") is int i and >= 0 ? usage[..i] : usage.TrimEnd();

    /// <summary>/commands &lt;name&gt;: the command of that name, else every command whose name contains it.</summary>
    public string Lookup(string name)
    {
        var all = All().ToList();
        if (all.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Usage: { } usage }) return usage;
        string[] usages = [.. all.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Usage).Order()];
        return usages.Length == 0 ? $"No command matches \"{name}\". Type /com for the list."
            : string.Join('\n', usages.Prepend($"--- Commands matching \"{name}\" ---"));
    }

    /// <summary>/raw: hex bytes of one S→C message, sent as is (spaces allowed).</summary>
    public static byte[] ParseHex(string[] args) => Convert.FromHexString(string.Concat(args));
}
