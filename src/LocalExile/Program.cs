using System.Reflection;
using LocalExile;

// LocalExile.exe: what a player runs in a release (Agents/docs/dev/releases.md). It finds the client, extracts the game
// data, makes the keys and the account, starts the servers and starts the client the only safe way (ClientLaunch).
// It always works from its own folder, whatever folder it was started in: the servers' paths are relative to it.
//   run (default)  everything above, until the client closes
//   setup          everything above except the servers and the client
//   check          say whether a folder holds the 3.29.3b client; changes nothing
//   stop           stop this install's servers and client
//   account create <name>
//   version | client-command | help
// Options: --client <folder> --client-mode copy|link --no-client --port-base <n> --account <name> --password-stdin
//          --no-update-check --json

const string Usage = """
    LocalExile.exe [run|setup|check|stop|version|client-command|help] [options]
    LocalExile.exe account create <name> [--password-stdin]

      --client <folder>       the Path of Exile 3.29.3b install to use
      --client-mode <mode>    copy (a separate copy, the default) or link (use the folder itself; its data is changed)
      --no-client             start only the servers
      --port-base <n>         with --no-client: five ports in a row from n instead of the defaults
      --account <name>        the account to create when there is none
      --password-stdin        read its password from standard input
      --no-update-check       do not ask GitHub whether a newer release exists
      --json                  one JSON event per line on standard output; never asks anything

    Exit codes: 0 done, 1 unexpected error, 2 bad usage, 3 client not usable, 4 data extraction failed,
    5 a server failed, 6 a port is in use or already running, 7 input needed, 8 the client failed to start.
    """;

var options = Options.Parse(args);
var log = new Output(options.Json);
if (options.Error is { } usage)
{
    log.Fail(ExitCode.Usage, $"{usage}{(options.Json ? "" : "\n\n" + Usage)}");
    return (int)ExitCode.Usage;
}

string root = Path.GetDirectoryName(Environment.ProcessPath!)!;
Directory.SetCurrentDirectory(root);
var ports = Ports.From(options.PortBase);
string version = typeof(Options).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

try
{
    switch (options.Command)
    {
        case "help":
            Console.WriteLine(Usage);
            return 0;
        case "version":
            log.Value("version", version);
            return 0;
        case "client-command":
            log.Value("client-command", ClientLaunch.CommandLine(ports.Patch));
            return 0;
        case "check":
            ClientInstall.Check(options.Client ?? "Client", log);
            return 0;
        case "stop":
            Servers.StopInstall(root, log);
            return 0;
        case "account":
            Accounts.Create(options.AccountName!, options, log);
            return 0;
    }

    log.Started(version);
    if (options.Command == "run" && !options.NoUpdateCheck) await UpdateCheck.RunAsync(version, log);
    using var single = new Mutex(true, $"Local\\LocalExile-{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant()))[..Math.Min(200, root.Length * 2)]}", out bool first);
    if (!first) throw new LauncherException(ExitCode.Busy, "this install is already running (close it, or: LocalExile.exe stop)");
    // The servers, the client and the tools end with this process, however it ends.
    KillOnExitJob.AddCurrentProcess();

    bool withClient = options.Command == "run" && !options.NoClient;
    if (withClient) ClientLaunch.AssertNoneRunning();
    if (options.Command == "run") Servers.AssertPortsFree(ports);

    ClientInstall.Ensure(options, log);
    Extraction.Run(log);
    ClientInstall.EnsureKeysAndPatch(log);
    Accounts.EnsureOne(options, log);
    ClientInstall.SelectLocalLogin(ports, log);
    if (options.Command == "setup")
    {
        log.Done("setup finished; start the game with LocalExile.exe");
        return 0;
    }

    using var servers = await Servers.StartAsync(ports, log);
    if (!withClient)
    {
        log.Ready(ports, null);
        await servers.WaitForAnyExitAsync();
        throw new LauncherException(ExitCode.Server, "a server stopped (see logs\\)");
    }

    ClientInstall.RefreshRootHash(log);
    using var client = ClientLaunch.Start(ports.Patch, log);
    log.Ready(ports, client.Id);
    await Task.WhenAny(client.WaitForExitAsync(), servers.WaitForAnyExitAsync());
    if (!client.HasExited) throw new LauncherException(ExitCode.Server, "a server stopped while the client was running (see logs\\)");
    log.Done("the client closed; stopping the servers");
    return 0;
}
catch (LauncherException e)
{
    log.Fail(e.Code, e.Message);
    return (int)e.Code;
}
catch (Exception e)
{
    log.Fail(ExitCode.Unexpected, e.ToString());
    return (int)ExitCode.Unexpected;
}
