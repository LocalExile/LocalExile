using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Persistent reverse localhost control link from Gateway to Auth on <see cref="InstanceControl.Port"/>.
/// Auth pushes pre-login events such as <c>character_created</c> over this NDJSON stream so Gateway mods can inspect
/// creation options and configure starting town, level, or start-kit suppression before S→C 0x15 / 0x13 and first login.
/// </summary>
public static class GatewayControl
{
    static readonly IReadOnlyDictionary<string, string> EmptyOptions =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Maintains the <c>gateway_attach</c> connection to Auth until <paramref name="ct"/> is cancelled,
    /// reconnecting cleanly whenever Auth starts or restarts.</summary>
    public static async Task RunAsync(CancellationToken ct, int? port = null)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port ?? InstanceControl.Port, ct);
                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                byte[] hello = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ControlRequest { Op = "gateway_attach" }, InstanceControl.Json) + "\n");
                await stream.WriteAsync(hello, ct);
                await stream.FlushAsync(ct);

                string? ack = await reader.ReadLineAsync(ct);
                if (ack is null) continue;
                var ackResponse = JsonSerializer.Deserialize<ControlResponse>(ack, InstanceControl.Json);
                if (ackResponse is not { Ok: true })
                {
                    await DelayQuietly(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                Console.WriteLine("[gateway] attached to Auth control channel");
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    ControlResponse reply;
                    try
                    {
                        var req = JsonSerializer.Deserialize<ControlRequest>(line, InstanceControl.Json);
                        reply = req is null
                            ? new ControlResponse { Ok = false, Error = "empty control request" }
                            : await HandleRequestAsync(req);
                    }
                    catch (Exception ex)
                    {
                        reply = new ControlResponse { Ok = false, Error = ex.Message };
                    }
                    byte[] responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, InstanceControl.Json) + "\n");
                    await stream.WriteAsync(responseBytes, ct);
                    await stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or JsonException)
            {
                await DelayQuietly(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    /// <summary>Dispatches one Auth→Gateway control request.</summary>
    public static Task<ControlResponse> HandleRequestAsync(ControlRequest req) => req.Op switch
    {
        "character_created" => HandleCharacterCreatedAsync(req),
        _ => Task.FromResult(new ControlResponse { Ok = false, Error = $"unknown gateway control op {req.Op}" }),
    };

    /// <summary>Runs <see cref="GameEvents.CharacterCreated"/> and enforces the start-kit / starting-level save invariant.</summary>
    public static async Task<ControlResponse> HandleCharacterCreatedAsync(ControlRequest req, LeagueRuntime? league = null)
    {
        if (string.IsNullOrWhiteSpace(req.Character) || req.CharacterId is not { } charId)
            return new ControlResponse { Ok = false, Error = "missing character identity" };

        if (!GameEvents.CharacterCreated.Any)
            return new ControlResponse { Ok = true };

        IReadOnlyDictionary<string, string> options = req.CreationOptions is { Count: > 0 } raw
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase))
            : EmptyOptions;

        var cls = Enum.IsDefined((CharacterClass)req.Class) ? (CharacterClass)req.Class : CharacterClass.Witch;
        var ctx = new CharacterCreatedContext(
            req.Account ?? "",
            charId,
            req.Character,
            cls,
            req.League ?? LeagueCatalog.Standard,
            req.Hardcore,
            req.Ssf,
            req.LeagueForm,
            options);

        await GameEvents.RunCharacterCreated(ctx, league);

        int? clampedLevel = ctx.StartingLevel is int lvl ? Math.Clamp(lvl, 1, 100) : null;
        string? startingArea = string.IsNullOrWhiteSpace(ctx.StartingAreaId) ? null : ctx.StartingAreaId.Trim();

        // Invariant: only pre-seed character_saves when SuppressStartKit is true.
        // Merge onto any save a CharacterCreated subscriber already wrote (QuestFlags, Waypoints, MainAscendancy, etc.).
        // If SuppressStartKit is false and StartingLevel > 1, Auth commits account_characters.level and
        // PlayerInventories.For sees save == null so StartKit still runs while LoadProgress adopts the level.
        if (ctx.SuppressStartKit && Saves.IsOpen)
        {
            PlayerInventories.Forget(ctx.Name);
            uint? experience = clampedLevel is > 1 ? CharacterStats.ExperienceFor(clampedLevel.Value) : null;
            Saves.Atomically(() =>
            {
                var existing = CharacterSaves.Load(ctx.Name);
                if (experience is not null) CharacterSaves.ForgetExperience(ctx.Name);
                var merged = (existing ?? new CharacterSaves.Save([])) with
                {
                    Inventories = existing?.Inventories ?? [],
                    Experience = experience ?? existing?.Experience,
                    StartWeapon = null,
                    Tutorial = null,
                };
                CharacterSaves.Write(ctx.Name, merged);
            });
        }

        return new ControlResponse
        {
            Ok = true,
            StartingAreaId = startingArea,
            StartingLevel = clampedLevel,
            SuppressStartKit = ctx.SuppressStartKit,
        };
    }

    static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
