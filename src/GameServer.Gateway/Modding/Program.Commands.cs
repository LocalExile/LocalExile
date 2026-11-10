using GameServer.Core.Accounts;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;

public partial class Program
{
    public static IEnumerable<(string Name, string Usage, string[] Arguments)> CommandUsages() => Commands("", null!, null!, null!).Usages;

    // The chat commands of one connection (ChatCommands.cs).
    public static ChatCommands Commands(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var commands = new ChatCommands
        {
            Send = (bytes, log) => Send(remote, stream, ciphers, bytes, log),
            // S→C 0x17 chat lines in the player's chat panel, one per line of the text (an empty line as a space).
            Reply = async text =>
            {
                foreach (string line in text.Split('\n').Select(l => l.Length == 0 ? " " : l))
                    await Send(remote, stream, ciphers, InstanceChat.ChatLine(line), $"0x0017 chat line \"{line}\"");
            },
        }
        .Add("module", "/module (the modules loaded when the Gateway started, from the folders in Modules/: version, kind and hooks, and why others did not load)", async _ =>
        {
            var modules = ServerModules.Mods();
            await conn.Commands!.Reply(string.Join('\n', modules.Select(m => m.Error is { } error ? $"{m.Id} - not loaded, {error}"
                : $"{m.Id} {m.Version} - {m.Kind?.ToString().ToLowerInvariant()}, {m.Hooks} hook(s)").Prepend($"--- Modules in {ServerModules.Folder}/ ---")));
        })
        .Add("module_reload", "/module_reload [id] (hot-reload one module and those that depend on it, or every module; their state starts over, and what is wrong with the new files leaves them running)", async args =>
        {
            string? id = args.Length > 0 ? args[0] : null;
            ServerModules.Reload(id);
            await conn.Commands!.Reply(id is null ? "Modules reloaded (/module for the list)." : $"Module {id} reloaded (/module for the list).");
        })
        .Add("mod", "/mod (the mods in Mods/Binaries/: loaded ones first, with their version, kind and hooks, then why the others are not)", async _ =>
        {
            // Loaded first, then the rest; each group keeps the host's order.
            var mods = ServerMods.Mods().OrderBy(m => m.Error is not null).ToList();
            await conn.Commands!.Reply(mods.Count == 0 ? $"No mods in {ServerMods.Folder}/ (Agents/docs/dev/modding.md)."
                : string.Join('\n', mods.Select(m => m.Error is { } error ? $"{m.Id} - not loaded, {error}"
                    : $"{m.Id} {m.Version} - {m.Kind?.ToString().ToLowerInvariant()}, {m.Hooks} hook(s)").Prepend($"--- Mods in {ServerMods.Folder}/ ---")));
        })
        .Add("mod_reload", "/mod_reload [id] (reload one mod and the mods that depend on it from its folder, or every mod; their state starts over)", async args =>
        {
            string? id = args.Length > 0 ? args[0] : null;
            ServerMods.Reload(id);
            await conn.Commands!.Reply(id is null ? "Mods reloaded (/mod for the list)." : $"Mod {id} reloaded (/mod for the list).");
        })
        .Add("mod_enable", "/mod_enable <id> (load a mod, now and at every start)", args => SetModEnabled(conn, args, true))
        .Add("mod_disable", "/mod_disable <id> (unload a mod and the mods that depend on it, now and at every start)", args => SetModEnabled(conn, args, false))
        .Add("reload", "/reload (read world.db again: settings, catalogues, drop tables, unique weights, overrides; no restart)", async _ =>
        {
            if (GameData.Store is null)
            {
                await conn.Commands!.Reply("world.db is not open");
                return;
            }
            try
            {
                GameData.Reload("command");
                await conn.Commands!.Reply($"Game data reloaded: revision {GameData.Current.Revision}; {SystemSettings.Default.Values.Count} " +
                    $"setting(s), {GameData.Current.ModRanges.Count} mod range override(s), {GameData.Current.Limits.Count} limit " +
                    $"override(s); {GameDataCatalog.Summary}");
            }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                await conn.Commands!.Reply($"Reload failed, the previous data stays: {e.Message}");
            }
        })
        .Add("lastpackets", "/lastpackets [n] (the connection's last n messages, both directions, to the Gateway log; default 64)", async args =>
        {
            int n = args.Length > 0 && int.TryParse(args[0], out int count) ? Math.Clamp(count, 1, PacketHistory.Capacity) : 64;
            conn.Packets.Print(remote, "/lastpackets", n);
            await conn.Commands!.Reply($"The last {Math.Min(n, conn.Packets.Count)} of {conn.Packets.Count} packet(s) are in logs\\live-gateway.log.");
        });
        return GameEvents.RunCommandsBuilding(conn, commands, remote, stream, ciphers);
    }
}
