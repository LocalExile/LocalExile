using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// The local item builder: a loopback-only web page (Game_ITEM_TOOL=1) backed by the running Gateway. It searches the
/// extracted BaseItemTypes and Mods tables, builds a Normal/Magic/Rare item from the chosen base, level, quality and
/// explicit affixes, adds it to the active character's main inventory and sends the 0xbf change to the client.
/// </summary>
static class ItemTool
{
    public sealed record Active(uint Ticket, string Character, string Remote, NetworkStream Stream,
        ConnectionCiphers Ciphers, ClientState Conn, SemaphoreSlim Gate);

    static readonly ConcurrentDictionary<uint, Active> Sessions = new();
    static readonly Lazy<string> Page = new(() =>
    {
        using var stream = typeof(ItemTool).Assembly.GetManifestResourceStream("ItemToolPage.html")
            ?? throw new InvalidOperationException("ItemToolPage.html is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public static void Register(uint ticket, string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Sessions[ticket] = new(ticket, conn.Character ?? "?", remote, stream, ciphers, conn, new SemaphoreSlim(1, 1));

    public static void Unregister(uint ticket) => Sessions.TryRemove(ticket, out _);

    public static async Task<WebApplication?> StartAsync(CancellationToken stopping)
    {
        int port = int.TryParse(Environment.GetEnvironmentVariable("Game_ITEM_TOOL_PORT"), out int p) && p is > 0 and <= 65535 ? p : 6113;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(Page.Value, "text/html; charset=utf-8"));
        app.MapGet("/api/sessions", () => Results.Json(Sessions.Values
            .Where(s => !s.Conn.Closed)
            .OrderByDescending(s => s.Conn.PlayerSent)
            .Select(s => new
            {
                ticket = s.Ticket, character = s.Character, level = s.Conn.Pools.Level,
                area = $"0x{s.Conn.Layout.Hash:x4}", ready = s.Conn.PlayerSent,
            })));
        app.MapGet("/api/bases", (string? q, int? limit) => Results.Json(Bases(q, limit ?? 60)));
        app.MapGet("/api/mods", (string? q, uint? type, int? limit) => Mods(q, type, limit ?? 100));
        app.MapGet("/api/stats", (string? q, uint? type, int? limit) => Stats(q, type, limit ?? 60));
        app.MapPost("/api/create", CreateAsync);
        app.MapPost("/api/import", Import);

        try
        {
            await app.StartAsync(stopping);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gateway] item tool not started on 127.0.0.1:{port}: {e.Message}");
            await app.DisposeAsync();
            return null;
        }
        Console.WriteLine($"[gateway] item tool: http://127.0.0.1:{port}/");
        return app;
    }

    static IEnumerable<object> Bases(string? query, int limit)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0) return [];
        return Program.Items.All
            .Where(t => t.Supported && t.Components.Contains("Mods") && !t.Components.Contains("SkillGem") &&
                !t.Components.Contains("Map") &&
                (Matches(t.Name, q) || Matches(t.Id, q)))
            .OrderBy(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(t => new
            {
                hash = t.Hash, id = t.Id, name = t.Name, @class = t.Class, domain = t.ModDomain,
                width = t.Width, height = t.Height, maxStack = t.MaxStack,
                sockets = t.Components.Contains("Sockets"),
            });
    }

    static IResult Mods(string? query, uint? typeHash, int limit)
    {
        if (typeHash is not uint hash || Program.Items.ByHash(hash) is not { } type) return Error("unknown base item");
        string q = (query ?? "").Trim();
        if (q.Length < 2) return Results.Json(Array.Empty<object>());
        var mods = Program.Items.Mods.All
            .Where(m => m.Domain == type.ModDomain && m.GenerationType is ItemRolls.Prefix or ItemRolls.Suffix &&
                !m.EssenceOnly && m.Stats.Count > 0)
            .Where(m => Matches(m.Id, q) || Matches(m.Name, q) || Matches(m.ModType, q) ||
                m.Stats.Any(s => Matches(s.Id, q)))
            .OrderByDescending(m => m.Level).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(m => new
            {
                hash = m.Hash, id = m.Id, name = m.Name, modType = m.ModType, level = m.Level,
                generation = m.GenerationType == ItemRolls.Prefix ? "prefix" : "suffix",
                families = m.Families,
                stats = m.Stats.Select(s => new { id = s.Id, min = s.Min, max = s.Max }),
            });
        return Results.Json(mods);
    }

    static IResult Stats(string? query, uint? typeHash, int limit)
    {
        if (typeHash is not uint hash || Program.Items.ByHash(hash) is not { } type) return Error("unknown base item");
        string q = (query ?? "").Trim();
        if (q.Length < 2) return Results.Json(Array.Empty<object>());
        var groups = Program.Items.Mods.All
            .Where(m => m.Domain == type.ModDomain && m.GenerationType is ItemRolls.Prefix or ItemRolls.Suffix &&
                !m.EssenceOnly && m.Stats.Count > 0)
            .Select(m => (Mod: m, Key: m.ModType.Length > 0 ? m.ModType : m.Stats[0].Id))
            .Where(x => Matches(x.Key, q) || Matches(x.Mod.Id, q) || Matches(x.Mod.Name, q) ||
                x.Mod.Stats.Any(s => Matches(s.Id, q)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(g => new
            {
                key = g.Key,
                label = Humanize(g.Key),
                statIds = g.SelectMany(x => x.Mod.Stats.Select(s => s.Id)).Distinct().OrderBy(s => s).ToArray(),
                tiers = g.Select(x => x.Mod).OrderByDescending(m => m.Level).ThenBy(m => m.Id)
                    .Select(m => new
                    {
                        hash = m.Hash, id = m.Id, name = m.Name, modType = m.ModType, level = m.Level,
                        generation = m.GenerationType == ItemRolls.Prefix ? "prefix" : "suffix",
                        families = m.Families,
                        stats = m.Stats.Select(s => new { id = s.Id, min = s.Min, max = s.Max }),
                    }),
            });
        return Results.Json(groups);
    }

    static bool Matches(string text, string query) => Normalize(text).Contains(Normalize(query), StringComparison.Ordinal);

    static string Normalize(string text) => new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    static string Humanize(string id) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(id, "([a-z0-9])([A-Z])", "$1 $2"), "[_%]+", " ").Trim();

    public sealed record ModChoice(ushort Hash, int[]? Values, bool Fractured = false, bool Crafted = false);
    public sealed record CreateRequest(uint Ticket, uint TypeHash, string Rarity, int ItemLevel, int Quality,
        int Count = 1, ModChoice[]? Mods = null);
    public sealed record ImportRequest(string Text);

    static IResult Import(ImportRequest request)
    {
        var parsed = new ItemTextImport(Program.Items).Parse(request.Text ?? "", out string error);
        if (parsed is null) return Error(error);
        return Results.Json(new
        {
            ok = true,
            itemBase = new
            {
                hash = parsed.Base.Hash, id = parsed.Base.Id, name = parsed.Base.Name, @class = parsed.Base.Class,
                domain = parsed.Base.ModDomain, width = parsed.Base.Width, height = parsed.Base.Height,
                maxStack = parsed.Base.MaxStack, sockets = parsed.Base.Components.Contains("Sockets"),
            },
            rarity = parsed.Rarity.ToString(), itemLevel = parsed.ItemLevel, quality = parsed.Quality,
            qualityType = parsed.QualityType,
            mods = parsed.Mods.Select(m => new
            {
                hash = m.Hash, id = m.Id, name = m.Name, modType = m.ModType, level = m.Level, generation = m.Generation,
                stats = m.Stats.Select(s => new { id = s.Id, min = s.Min, max = s.Max }), values = m.Values,
                fractured = m.Fractured, crafted = m.Crafted, line = m.Line,
            }),
            unmatched = parsed.Unmatched,
            notes = parsed.Notes,
        });
    }

    static async Task<IResult> CreateAsync(CreateRequest request)
    {
        if (!Sessions.TryGetValue(request.Ticket, out var session) || session.Conn.Closed)
            return Error("that game session is not connected", 409);
        if (!session.Conn.PlayerSent) return Error("enter the game world first", 409);
        if (!Enum.TryParse<ItemRarity>(request.Rarity, ignoreCase: true, out var rarity) || rarity is ItemRarity.Unique)
            return Error("rarity must be Normal, Magic or Rare");
        if (Program.Items.ByHash(request.TypeHash) is not { } type) return Error("unknown base item");
        if (!type.Supported || !type.Components.Contains("Mods")) return Error("that base cannot hold modifiers");
        if (request.ItemLevel is < 1 or > 100) return Error("item level must be 1 to 100");
        if (request.Quality is < 0 or > 30) return Error("quality must be 0 to 30");
        int count = Math.Clamp(request.Count <= 0 ? 1 : request.Count, 1, 20);

        await session.Gate.WaitAsync();
        try
        {
            var rng = Random.Shared;
            var items = PlayerInventories.For(session.Conn.Character ?? "Exile", Program.Items);
            var added = new List<InventoryItem>();
            for (int n = 0; n < count; n++)
            {
                var built = Build(Program.Items, Program.Rolls, type, rarity, request, rng);
                if (built.Error is { } why) return Error(why);
                if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
                {
                    if (added.Count == 0) return Error("no room in the main inventory", 409);
                    break;
                }
                added.Add(items.Put(PlayerInventories.MainInventory,
                    new InventoryItem(items.NewItemId(), cell.X, cell.Y, built.Item!)));
            }
            await Program.Send(session.Remote, session.Stream, session.Ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), [], added),
                $"0x00bf item tool: {type.Name} x {added.Count}");
            var first = added[0].Item;
            return Results.Json(new
            {
                ok = true, count = added.Count, first = added[0].Id,
                name = first.NameWords is { Count: > 0 } ? Program.Rolls.NameOf(first.NameWords) : type.Name,
                type = type.Name, cells = added.Select(i => new { id = i.Id, x = i.X, y = i.Y }),
            });
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gateway] item tool: {e.GetType().Name}: {e.Message}");
            return Error($"could not create the item: {e.Message}", 500);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    internal static (Item? Item, string? Error) Build(ItemCatalog items, ItemRolls rolls, ItemCatalog.BaseType type, ItemRarity rarity, CreateRequest request, Random rng)
    {
        var (sockets, links) = items.DefaultSockets(type, request.ItemLevel, request.Quality, rng);
        var item = ItemCatalog.Create(type, sockets, links) with
        {
            ItemLevel = (byte)request.ItemLevel, Quality = (byte)request.Quality, Rarity = rarity, Identified = true,
        };
        var choices = request.Mods ?? [];
        if (rarity == ItemRarity.Normal && choices.Length > 0) return (null, "a Normal item cannot have explicit modifiers");
        if (choices.Length == 0)
            return rarity == ItemRarity.Normal ? (item, null) : (rolls.Roll(item, rarity, rng), null);

        int maxEach = rarity == ItemRarity.Magic ? 1 : 3, maxTotal = rarity == ItemRarity.Magic ? 2 : 6;
        if (choices.Length > maxTotal) return (null, $"{rarity} items can have at most {maxTotal} explicit modifiers");
        var chosen = new List<ModCatalog.Mod>();
        var mods = new List<ItemMod>();
        var fracturedMods = new List<ItemMod>();
        foreach (var choice in choices)
        {
            if (items.Mods.ByHash(choice.Hash) is not { } mod) return (null, $"unknown modifier 0x{choice.Hash:x4}");
            if (mod.Domain != type.ModDomain && !(choice.Crafted && mod.Domain == 9))
                return (null, $"{mod.Id} cannot roll on this base");
            if (mod.GenerationType is not (ItemRolls.Prefix or ItemRolls.Suffix)) return (null, $"{mod.Id} is not an explicit affix");
            if (chosen.Any(c => c.Hash == mod.Hash || c.Families.Intersect(mod.Families).Any()))
                return (null, $"{mod.Id} conflicts with another selected modifier");
            if (chosen.Count(c => c.GenerationType == mod.GenerationType) >= maxEach)
                return (null, $"{rarity} items can have at most {maxEach} {(mod.GenerationType == ItemRolls.Prefix ? "prefixes" : "suffixes")}");
            int[] values;
            if (choice.Values is null)
                values = [.. mod.Stats.Select(s => rng.Next(Math.Min(s.Min, s.Max), Math.Max(s.Min, s.Max) + 1))];
            else
            {
                if (choice.Values.Length != mod.Stats.Count) return (null, $"{mod.Id} needs {mod.Stats.Count} value(s)");
                for (int i = 0; i < choice.Values.Length; i++)
                    if (choice.Values[i] < Math.Min(mod.Stats[i].Min, mod.Stats[i].Max) ||
                        choice.Values[i] > Math.Max(mod.Stats[i].Min, mod.Stats[i].Max))
                        return (null, $"{mod.Id} value {i + 1} is outside its range");
                values = choice.Values;
            }
            chosen.Add(mod);
            (choice.Fractured ? fracturedMods : mods).Add(new(ModSection.Explicit, mod.Hash, values));
        }
        return (item with
        {
            Mods = [.. item.Mods, .. fracturedMods, .. mods],
            FracturedCount = (byte)fracturedMods.Count,
            NameWords = rarity == ItemRarity.Rare ? rolls.RareName(type, rng) : [],
        }, null);
    }

    static PlayerInventories.Shape? ShapeOf(Item item) => Program.Items.ByHash(item.TypeHash) is { } t
        ? new PlayerInventories.Shape(t.Width, t.Height, t.Class, t.MaxStack,
            t.BecomesWhenFull && Program.Items.Row(t.FullStackRow) is { Supported: true } full
                ? () => ItemCatalog.Create(full) : null)
        : null;

    static IResult Error(string message, int status = 400) =>
        Results.Json(new { ok = false, error = message }, statusCode: status);
}
