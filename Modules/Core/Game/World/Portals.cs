using System.Net.Sockets;
using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    sealed class MapRunProgress(Guid run, int[] bosses)
    {
        public readonly Guid Run = run;
        public readonly HashSet<int> Bosses = [.. bosses];
        public readonly HashSet<int> Killed = [];
        public double NextCheck;
        public volatile bool Completed;
    }
    static readonly Store<InstanceState, MapRunProgress> MapRuns = new();

    // A run survives its owner's area change and belongs to guests too. The instance keeps its exact run even when
    // the owner opens another copy of the same area; neither character names nor area ids identify a run.
    public static bool RunBossKilled(MapDevice.OpenMap? run) => run is not null
        && MapRuns.Any(p => p.Value.Run == run.Run && p.Value.Completed);

    static int[] CompletionBosses(string area)
    {
        var bosses = (Packs.For(area)?.Bosses ?? []).Select(row => Monsters.Find(row.ToString()))
            .OfType<MonsterCatalog.Variety>().ToArray();
        // LaterForms places a second form only when its first flees or falls. Only the terminal form has to die;
        // multiple distinct bosses (Strand, for example) must all die. Other unique monsters are irrelevant.
        return [.. bosses.Where(first => !bosses.Any(last => last.Id == first.Id + "2" && last.Name == first.Name))
            .Select(b => b.Row)];
    }

    public static async Task TickMapRunBoss(Player player, bool force = false)
    {
        if (!player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Instance is not { } instance
            || MapDevice.ForInstance(player) is not { } run) return;
        var progress = MapRuns.GetValue(instance, _ => new(run.Run, CompletionBosses(run.Area.Id)));
        double now = AreaTime(player);
        if (progress.Completed || progress.Bosses.Count == 0 || !force && now < progress.NextCheck) return;
        progress.NextCheck = now + 0.25;
        bool alive = false;
        foreach (var monster in player.Area.Monsters.Values.Concat(player.Area.DormantMonsters.Values))
        {
            if (!monster.Hostile || monster.Variety is not { } variety || !progress.Bosses.Contains(variety.Row)) continue;
            if (monster.Dead || monster.Life <= 0) progress.Killed.Add(variety.Row);
            else alive = true;
        }
        // Remember kills before a corpse is consumed, but never complete while a twin or a later boss is alive
        // or a required boss has not spawned yet.
        if (alive || !progress.Bosses.IsSubsetOf(progress.Killed)) return;
        progress.Completed = true;
        Console.WriteLine($"[gateway] {run.Owner} map run {run.Run} completed: {run.Area.Id}");
        if (run.PantheonSoul is { } soul)
            await DeliverCapturedSouls(player, run.Owner, soul.VesselItemId);
        foreach (var member in Program.InGame.Keys.Where(c => ReferenceEquals(c.Instance, instance)
            && !c.Closed && !c.LeavingArea && !c.Character.LeagueQuestFlags.Contains(1037)))
        {
            try { await SetLeagueQuestFlags(member, [1037]); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        foreach (var visitor in Program.InGame.Keys.Where(c => c.MapPortalRun?.Run == run.Run
            && c.Connection.PlayerSent && !c.Closed && !c.LeavingArea))
        {
            try
            {
                await SendMapPortals(visitor,
                    MapDeviceHere(visitor)?.Id ?? 0, run.Area.Hash16);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A visitor disconnected; the next listing still reads this run's completed state.
            }
        }
    }

    const string PendingSoulsKey = "pantheon.pendingSouls";

    // A guest can finish the owner's map while the owner is offline or has both inventories full.
    // Keep undelivered vessels in the character save until the owner makes space and opens the device.
    public static async Task DeliverCapturedSouls(Player context, string ownerName, string? captured = null)
    {
        var owner = Program.InGame.Keys.FirstOrDefault(p => !p.Closed
            && string.Equals(CharacterName(p), ownerName, StringComparison.OrdinalIgnoreCase));
        var catalog = ItemsFor(owner ?? context);
        var items = PlayerInventories.For(ownerName, catalog);
        List<(byte Row, PlayerInventories.Stacking Change)> placed = [];
        Saves.Atomically(() =>
        {
            string prior = CharacterSaves.ModuleData(ownerName, PendingSoulsKey) ?? "";
            if (captured is null && prior.Length == 0) return;
            var pending = prior.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (captured is not null) pending.Add(captured);
            var remaining = new List<string>();
            foreach (string id in pending)
            {
                if (catalog.Find(id) is not { Supported: true } type) { remaining.Add(id); continue; }
                var changes = items.StackAcross([PlayerInventories.MapDevice, PlayerInventories.MainInventory],
                    ItemCatalog.Create(type), ShapeOf);
                if (changes.Values.All(c => !c.Any)) remaining.Add(id);
                foreach (var (row, change) in changes)
                    if (change.Any) placed.Add((row, change));
            }
            string next = string.Join('|', remaining);
            if (next != prior) CharacterSaves.ChangeModuleData(ownerName, PendingSoulsKey, _ => next);
        });
        if (owner is null || owner.Closed) return;
        try
        {
            foreach (var (row, change) in placed)
                await Send(owner, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                    items.NextRevision(row), change.RemovedIds, change.AddedItems), "0x00bf captured Pantheon soul");
            if (captured is not null && placed.Count == 0)
                await Send(owner, InstanceChat.ChatLine("Soul captured. Make room and open your Map Device to collect it."),
                    "0x0017 pending Pantheon soul");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public static (uint Id, int X, int Y, bool Campaign)? MapDeviceHere(Player player)
    {
        if (player.Layout.Id == MapDevice.CampaignDeviceArea)
            lock (player.QuestObjects)
                foreach (var (id, obj) in player.QuestObjects)
                    if (obj.Rule.ObjectType == MapDevice.CampaignDeviceType)
                        return (id, obj.At.X, obj.At.Y, true);
        lock (player.HideoutDoodads)
            foreach (var (id, device) in player.HideoutDoodads)
                if (HideoutCatalog.IsPlaced(device)) return (id, device.X, device.Y, false);
        return null;
    }
    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device or
    // using another Portal Scroll: 0x008f carries the variation of every entry and the client re-applies the visual.
    // False when nothing stands.
    public static async Task<bool> ResendMapPortals(Player player)
    {
        bool single = await SendSinglePortals(player, "listed again");
        uint deviceId = MapDeviceHere(player)?.Id ?? 0;
        lock (player.MapPortals)
            if (player.MapPortals.Count == 0) return single;
        await SendMapPortals(player, deviceId,
            player.MapPortalRun?.Area.Hash16 ?? player.Layout.Hash);
        return true;
    }

    // The portals that stand without a map device (a Portal Scroll's, a map's own way back, the portal back to an open
    // town portal): object id Ã¢â€ ’ the area it leads to, for the connection that made them.
    static readonly Store<Player, System.Collections.Concurrent.ConcurrentDictionary<uint, (ushort Area, string? Owner, ushort? Variation)>> SinglePortals = new();

    /// <summary>
    /// Lists a portal that stands without a map device, <paramref name="afterMs"/> after its create and once more
    /// MapPortalRelistMs (or <paramref name="relistMs"/>) later. 0x8f only reaches a portal object the client has built.
    /// The client reads its socket once per update and handles 0x8f at once, while the create waits for its world frame,
    /// so a list that arrives in the same read as the create is lost and the portal is never drawn (seen live 2026-11-12,
    /// a background window in the lockstep mode: 0x8f handled 3 ms before the object was built, sent 100 ms after it).
    /// An entry listed again unchanged leaves the portal as it is.
    /// </summary>
    public static void ListPortal(Player player, uint id, ushort area,
        string what, int afterMs, int? relistMs = null, string? owner = null, ushort? variation = null)
    {
        SinglePortals.GetOrCreateValue(player)[id] = (area, owner, variation);
        _ = AreaFrames.Start(async () =>
        {
            try
            {
                await AreaDelay(player, afterMs / 1000.0);
                if (!await SendSinglePortals(player, what, id)) return;
                await AreaDelay(player, (relistMs ?? MapPortalRelistMs) / 1000.0);
                await SendSinglePortals(player, what + ", listed again", id);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection closed while sending.
            }
        });
    }

    /// <summary>
    /// The effect one portal is drawn with. The Portal slot has two choices that are no cosmetic
    /// (MicrotransactionSlotAdditionalDefaultOptions; CÃ¢â€ ’S 0x1f9 with record 0 and the option): 1 "Use one random
    /// variation for every portal" and 2 "Use random variations for all portals". The pick is kept, per area visit for
    /// 1 and per portal for 2, so a portal listed again keeps its effect. Otherwise the worn effect.
    /// </summary>
    public static ushort PortalVariation(Player player, uint portal)
    {
        byte mode = CharacterSaves.Cosmetics(CharacterName(player)).GetValueOrDefault(InstanceMicrotransactions.PortalSlot) is
            { Record: 0, Option: 1 or 2 } choice ? choice.Option : (byte)0;
        if (mode == 0 || Cosmetics.Portals.Count == 0) return player.Character.CosmeticPortalVariation;
        int seed = mode == 1 ? HashCode.Combine(player.Seed, player.Ticket) : HashCode.Combine(player.Seed, player.Ticket, portal);
        return (ushort)Cosmetics.Portals[(int)((uint)seed % (uint)Cosmetics.Portals.Count)].Index;
    }

    static InstanceMapDevice.Portal SinglePortalEntry(Player player, uint id, ushort area, string? owner = null, ushort? variation = null) =>
        new(id, area, owner ?? CharacterName(player), Variation: variation ?? PortalVariation(player, id));

    /// <summary>
    /// For a portal that already stands when the player arrives: its 0x8f entry, sent before its create. The client plays a
    /// portal's opening whenever 0x8f lists it for the first time (or changed), and it keeps an entry whose object does not
    /// exist yet, so the lists after the create (ListPortal, the same entry) show it idle (21-hideouts.md §5).
    /// </summary>
    public static Task ListStandingPortal(Player player, uint id, ushort area, string what,
        string? owner = null, ushort? variation = null) =>
        Send(player, InstanceMapDevice.Portals(0, [SinglePortalEntry(player, id, area, owner, variation)]),
            $"0x008f portal 0x{id:x} leads to 0x{area:x4} before its create ({what} stands already)");

    // Custom standalone portals opened by modules/mods via IPlayerWorld.SpawnPortalAsync.
    public sealed class CustomPortal(uint id, int x, int y, ushort targetArea, PortalOptions options)
    {
        public uint Id { get; } = id;
        public int X { get; } = x;
        public int Y { get; } = y;
        public ushort TargetArea { get; } = targetArea;
        public PortalOptions Options { get; } = options;
        public int? UsesLeft = options.UsesLeft;
        public ulong ResolvedInstance = options.TargetInstance;
    }

    sealed record CustomReturnOrigin(ushort Area, ulong Instance, int X, int Y, string? Owner, ushort? Variation, DateTimeOffset RecordedAt);
    sealed record CustomReturnKey(ushort Area, ulong Instance);

    static readonly Store<Player, System.Collections.Concurrent.ConcurrentDictionary<uint, CustomPortal>> CustomPortals = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(ushort Area, ulong Instance), CustomReturnOrigin> CustomReturnOrigins = new();
    static readonly Store<InstanceState, CustomReturnKey> CustomReturnInstanceKeys = new();

    public static bool HasCustomPortal(Player player, uint id) =>
        CustomPortals.TryGetValue(player, out var portals) && portals.ContainsKey(id);

    internal static void RecordCustomReturnOrigin(ushort targetArea, ulong targetInstance,
        ushort originArea, ulong originInstance, int originX, int originY, string? owner = null, ushort? variation = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (CustomReturnOrigins.Count >= 64)
        {
            // Belt-and-suspenders sweep for transfers abandoned before the target instance was ever entered.
            foreach (var entry in CustomReturnOrigins)
                if (now - entry.Value.RecordedAt > TimeSpan.FromHours(1))
                    CustomReturnOrigins.TryRemove(entry.Key, out _);
        }
        CustomReturnOrigins[(targetArea, targetInstance)] =
            new CustomReturnOrigin(originArea, originInstance, originX, originY, owner, variation, now);
    }

    internal static bool HasCustomReturnOrigin(ushort targetArea, ulong targetInstance) =>
        CustomReturnOrigins.ContainsKey((targetArea, targetInstance));

    /// <summary>Evicts any custom return-portal origin bound to a closing sub-area instance (<see cref="InstanceEvents.InstanceClosing"/>).</summary>
    public static void ReleaseCustomReturnOrigin(InstanceState instance)
    {
        if (CustomReturnInstanceKeys.TryGetValue(instance, out var key))
            CustomReturnOrigins.TryRemove((key.Area, key.Instance), out _);
    }

    /// <summary>Spawns a clickable standalone portal at <c>(x, y)</c> leading to <paramref name="targetArea"/>.</summary>
    public static async Task<uint?> SpawnCustomPortal(Player player, int x, int y, ushort targetArea, PortalOptions? options = null)
    {
        if (player.Closed || player.LeavingArea || targetArea == 0) return null;
        var opts = options ?? new PortalOptions();
        var (px, py) = player.Layout.Walkable?.Invoke(x, y) == false ? player.Layout.Near((x, y)) : (x, y);
        uint id = player.TakeObjectId();
        var portal = new CustomPortal(id, px, py, targetArea, opts);
        CustomPortals.GetOrCreateValue(player)[id] = portal;
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(px, py, -1, MapPortalType, Open: 1, Rotation: player.PortalRotationRadians).Blob()),
            $"0x02bb create custom portal 0x{id:x} at ({px}, {py}) -> 0x{targetArea:x4}", wake: true, awaitBirth: false);
        ListPortal(player, id, targetArea, "custom portal", MapPortalEntryMs, owner: opts.Owner, variation: opts.Variation);
        return id;
    }

    /// <summary>Removes a standalone custom portal spawned via <see cref="SpawnCustomPortal"/>.</summary>
    public static async Task<bool> RemoveCustomPortal(Player player, uint id)
    {
        if (!CustomPortals.TryGetValue(player, out var portals) || !portals.TryRemove(id, out _)) return false;
        if (SinglePortals.TryGetValue(player, out var singles)) singles.TryRemove(id, out _);
        if (!player.Closed && !player.LeavingArea)
            await SendWorldRemoval(player, id, $"0x02bd remove custom portal 0x{id:x}");
        return true;
    }

    /// <summary>Handles clicks on standalone custom portals (GameEvents.ObjectUse).</summary>
    public static async Task<bool> TryUseCustomPortal(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!CustomPortals.TryGetValue(player, out var portals) || !portals.TryGetValue(use.TargetId, out var portal))
            return false;
        if (!TryBeginObjectWalk(player, requestedAt, portal.X, portal.Y, AreaTransitionObjectSize, out var walk)) return true;
        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action) || player.PendingTransfer is not null) return true;
        if (!portals.TryGetValue(use.TargetId, out var active) || !ReferenceEquals(active, portal)) return true;

        bool spent = false;
        lock (portal)
        {
            if (portal.UsesLeft is int left)
            {
                if (left <= 0) return true;
                portal.UsesLeft = left - 1;
                spent = portal.UsesLeft == 0;
            }
        }
        if (spent) await RemoveCustomPortal(player, portal.Id);

        ulong targetInstance = portal.ResolvedInstance;
        bool newInstance = targetInstance == 0 && portal.Options.NewInstance;
        if (targetInstance != 0 && await AreaInstances.Gone(player, portal.TargetArea, targetInstance))
        {
            CustomReturnOrigins.TryRemove((portal.TargetArea, targetInstance), out _);
            if (AreaInstances.RecreatedOnEntry(portal.TargetArea))
            {
                targetInstance = 0;
                newInstance = false;
            }
            else
            {
                await Send(player, InstanceChat.ChatLine("The instance does not exist."), "0x0017 custom portal instance expired");
                await RemoveCustomPortal(player, portal.Id);
                return true;
            }
        }

        ushort originArea = player.Layout.Hash;
        ulong originInstance = player.InstanceNumber;
        int originX = portal.X, originY = portal.Y;
        var transfer = new PendingTransfer(portal.TargetArea, targetInstance, newInstance, ArriveAt: portal.Options.ArriveAt)
        {
            Done = response =>
            {
                if (!response.Ok || response.Instance == 0) return;
                lock (portal)
                    if (portal.ResolvedInstance == 0) portal.ResolvedInstance = response.Instance;
                if (portal.Options.SpawnReturnPortal)
                    RecordCustomReturnOrigin(portal.TargetArea, response.Instance,
                        originArea, originInstance, originX, originY, portal.Options.Owner, portal.Options.Variation);
            },
        };
        await ChangeArea(player, transfer, $"custom portal to 0x{portal.TargetArea:x4}");
        return true;
    }

    /// <summary>Spawns an automatic return portal when entering an instance opened with <see cref="PortalOptions.SpawnReturnPortal"/>.</summary>
    public static async Task SpawnCustomReturnPortal(Player player)
    {
        if (player.InstanceNumber == 0
            || !CustomReturnOrigins.TryGetValue((player.Layout.Hash, player.InstanceNumber), out var origin))
            return;
        if (player.Instance is { } instance)
            CustomReturnInstanceKeys.GetValue(instance, _ => new CustomReturnKey(player.Layout.Hash, player.InstanceNumber));
        var (x, y) = player.Layout.Near((player.Layout.SpawnX, player.Layout.SpawnY));
        var opts = new PortalOptions(
            TargetInstance: origin.Instance,
            NewInstance: false,
            ArriveAt: (origin.X, origin.Y),
            UsesLeft: null,
            SpawnReturnPortal: false,
            Owner: origin.Owner,
            Variation: origin.Variation);
        await SpawnCustomPortal(player, x, y, origin.Area, opts);
    }

    // One 0x8f without a device for every portal of SinglePortals that still stands, each with the worn portal effect.
    // With an id, only while that portal stands. False when nothing was sent.
    static async Task<bool> SendSinglePortals(Player player, string why, uint only = 0)
    {
        if (player.Closed || !SinglePortals.TryGetValue(player, out var portals)) return false;
        bool Stands(uint id)
        {
            if (player.ReturnPortal?.Id == id || player.BackPortal?.Id == id || HasCustomPortal(player, id)) return true;
            lock (player.PeerPortals) return player.PeerPortals.ContainsKey(id);
        }
        foreach (uint gone in portals.Keys.Where(id => !Stands(id)).ToList()) portals.TryRemove(gone, out _);
        if (portals.IsEmpty || (only != 0 && !portals.ContainsKey(only))) return false;
        var list = portals.OrderBy(p => p.Key)
            .Select(p => SinglePortalEntry(player, p.Key, p.Value.Area, p.Value.Owner, p.Value.Variation)).ToList();
        await Send(player, InstanceMapDevice.Portals(0, list),
            $"0x008f {string.Join(", ", list.Select(p => $"portal 0x{p.ObjectId:x} leads to 0x{p.Area:x4}"))} (variation {string.Join(", ", list.Select(p => p.Variation))}; {why})");
        return true;
    }

    // SÃ¢â€ ’C 0x8f: the device's portals and where they lead (21-hideouts.md Ã‚§5). Without it the client never draws them; a
    // portal listed with area 0 (one just taken) closes.
    public static async Task SendMapPortals(Player player, uint deviceId, ushort area, uint closed = 0)
    {
        byte style = player.MapPortalStyle;
        List<(uint Id, int Index)> ids;
        MapDevice.OpenMap? run;
        lock (player.MapPortals)
        {
            ids = [.. player.MapPortals.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Index))];
            run = player.MapPortalRun;
            area = run?.Area.Hash16 ?? area;
        }
        string owner = run?.Owner ?? CharacterName(player);
        var list = (closed == 0 ? [] : new List<InstanceMapDevice.Portal> { new(closed, 0) })
            .Concat(ids.Select(p => new InstanceMapDevice.Portal(p.Id, area, owner, style,
                run is null ? PortalVariation(player, p.Id) : MapPortalVariation(run, p.Index)))).ToList();
        await Send(player, InstanceMapDevice.Portals(deviceId, list, RunBossKilled(run)),
            $"0x008f map device 0x{deviceId:x} portals: {ids.Count} open to 0x{area:x4}{(closed == 0 ? "" : $", 0x{closed:x} closed")}");
    }

    static ushort MapPortalVariation(MapDevice.OpenMap run, int index)
    {
        byte mode = CharacterSaves.Cosmetics(run.Owner).GetValueOrDefault(InstanceMicrotransactions.PortalSlot) is
            { Record: 0, Option: 1 or 2 } choice ? choice.Option : (byte)0;
        if (mode == 0 || Cosmetics.Portals.Count == 0) return CharacterSaves.PortalCosmetic(run.Owner);
        int seed = HashCode.Combine(run.Run, mode == 1 ? 0 : index);
        return (ushort)Cosmetics.Portals[(int)((uint)seed % (uint)Cosmetics.Portals.Count)].Index;
    }
}
