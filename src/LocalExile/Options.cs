namespace LocalExile;

enum ExitCode { Ok = 0, Unexpected = 1, Usage = 2, Client = 3, Extraction = 4, Server = 5, Busy = 6, NeedsInput = 7, ClientStart = 8 }

/// <summary>A failure the player can act on: its message is shown as it is, and its code is the process's exit code.</summary>
sealed class LauncherException(ExitCode code, string message) : Exception(message)
{
    public ExitCode Code { get; } = code;
}

sealed class Options
{
    public string Command { get; private set; } = "run";
    public string? Client { get; private set; }
    public string? ClientMode { get; private set; }
    public bool NoClient { get; private set; }
    public int PortBase { get; private set; }
    public string? AccountName { get; private set; }
    public bool PasswordStdin { get; private set; }
    public bool NoUpdateCheck { get; private set; }
    public bool Json { get; private set; }
    public string? Error { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        string? Next(string flag)
        {
            if (++i < args.Length) return args[i];
            o.Error ??= $"{flag} needs a value";
            return null;
        }
        if (args.Contains("--json")) o.Json = true;
        for (; i < args.Length && o.Error is null; i++)
            switch (args[i])
            {
                case "run" or "setup" or "check" or "stop" or "version" or "client-command" or "help": o.Command = args[i]; break;
                case "--help" or "-h": o.Command = "help"; break;
                case "account":
                    if (i + 2 >= args.Length || args[i + 1] != "create") { o.Error = "usage: account create <name>"; break; }
                    o.Command = "account";
                    o.AccountName = args[i + 2];
                    i += 2;
                    break;
                case "--client": o.Client = Next("--client"); break;
                case "--client-mode": o.ClientMode = Next("--client-mode"); break;
                case "--no-client": o.NoClient = true; break;
                case "--port-base":
                    if (!int.TryParse(Next("--port-base"), out int port) || port < 1024 || port > 65530) o.Error ??= "--port-base needs a port from 1024 to 65530";
                    else o.PortBase = port;
                    break;
                case "--account": o.AccountName = Next("--account"); break;
                case "--password-stdin": o.PasswordStdin = true; break;
                case "--no-update-check": o.NoUpdateCheck = true; break;
                case "--json": break;
                default: o.Error = $"unknown argument: {args[i]}"; break;
            }
        if (o.ClientMode is not (null or "copy" or "link")) o.Error ??= "--client-mode is copy or link";
        // The client asks only the default ports' servers: its login list in the GGPK names one login server.
        if (o.PortBase != 0 && !o.NoClient && o.Command == "run") o.Error ??= "--port-base needs --no-client";
        return o;
    }
}

/// <summary>The servers' ports: the defaults, or five in a row from a base, as live.ps1 gives them.</summary>
sealed record Ports(int Patch, int PatchWeb, int Login, int Control, int Gateway)
{
    public const int DefaultPatch = 12995;

    public static Ports From(int portBase) => portBase == 0
        ? new(DefaultPatch, 8080, 20493, 20495, 6112)
        : new(portBase, portBase + 1, portBase + 2, portBase + 3, portBase + 4);

    public int[] All => [Patch, PatchWeb, Login, Control, Gateway];
}
