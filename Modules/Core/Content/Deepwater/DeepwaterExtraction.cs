using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>How a run ends: the Bathysphere (<c>ExtractionObject</c>), the Allflame Capsules of a Voyage
/// (<c>SummonExtractionObject</c>, their window on inventory 113 and its Send to Surface / Call Bathysphere, ruleset ops
/// 2 and 3), a Chart's backup capsule (<c>SummonExtractionObjectFallback</c>), the ride up to the encounter's own deck
/// with the Sulphur and the CollectionChest (inventory 114), and the failures: death, drowning, leaving another way.
/// <para>H: the objects, their states (<c>landed</c> 0 suspended, 1 dropped, 2 the start one), the gates (QuestFlags
/// 3562 on the Bathysphere, 2020 and a filled 113 for Send to Surface, an empty 113 for Call Bathysphere), the window
/// kind 0x68 and the ruleset modes. M/G: the window's first u32 = the capsule (what enables its buttons was not found),
/// the 30 s descent and the task's data (30, 0), the backup capsule beside the Bathysphere, "the last capsule" = the last
/// unsent one, the fail line under 25 % of the run's possible Sulphur, the CollectionChest's window kind 0x6f, a death
/// or another exit ending the run for everyone in it, and the Sulphur as one stack at the deck's LootSpiralMarker.</para></summary>
internal static class DeepwaterExtraction
{
    public enum Kind { Bathysphere, Capsule, Backup }

    /// <summary>A Bathysphere or capsule of the run: its object, place and state.</summary>
    public sealed class Device
    {
        public required Kind Kind { get; init; }
        public required uint Id { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        /// <summary>A Bathysphere's <c>landed</c>: 2 the start one, 0 while a called one descends, 1 once it landed.</summary>
        public int Landed { get; set; }
        /// <summary>A capsule that sent its items up, or that called the Bathysphere: it took off.</summary>
        public bool Sent, Called;
        public string Type => Kind switch
        {
            Kind.Bathysphere => DeepwaterConstants.ExtractionObject,
            Kind.Capsule => DeepwaterConstants.SummonExtractionObject,
            _ => DeepwaterConstants.SummonExtractionObjectFallback,
        };
    }

    /// <summary>The capsule window's owner (ClientState.ModuleWindow).</summary>
    public const string CapsuleWindow = "deepwater-capsule";
    /// <summary>Inventories rows 113 (DeepwaterExtractionInventory1, the capsule's 12×5) and 114
    /// (DeepwaterCollectionInventory1, the deck's 12×40; 05-inventories-and-ui.md).</summary>
    public const byte CapsuleRow = 113, CollectionRow = 114;
    /// <summary>S→C 0x7b kinds: ExtractionInventory (the capsule window) and CollectionInventory.</summary>
    const byte CapsuleUi = 0x68, CollectionUi = 0x6f;
    /// <summary>G: the called Bathysphere's descent.</summary>
    public const double Descent = 30;
    /// <summary>G: the backup capsule's distance from the start Bathysphere (size 12).</summary>
    const int BackupBeside = 16;
    const string CollectionChest = "Metadata/Terrain/Leagues/Deepwater/Objects/CollectionChest";
    const string LootSpiralMarker = "Metadata/Terrain/Leagues/Deepwater/Objects/LootSpiralMarker";
    const string Sulphur = "Metadata/Items/Currency/CurrencyDeepwater";

    static readonly Lazy<Dictionary<string, StateMachineData?>> States = new(() =>
    {
        var shared = ObjectTypes.SharedStates();
        string[] types = [DeepwaterConstants.ExtractionObject, CollectionChest];
        return ObjectTypes.All().Where(t => types.Contains(t.Type)).ToDictionary(t => t.Type, t => ObjectTypes.StateMachine(t.Row, shared));
    });

    static QuestObjectTypes.Info? Info(string type) =>
        QuestObjectTypes.Find(type) is { } info && QuestObject.CanSerialize(info.Components, States.Value.GetValueOrDefault(type) is not null) ? info : null;

    // ---- the plan ----

    /// <summary>The run's start devices: the Bathysphere on the ground at cell 0's <c>bathysphere</c> point (H), and a
    /// Chart's backup capsule beside it, or a Voyage's capsule at every other cell's <c>bathysphere</c> point (G).</summary>
    public static void Plan(ClientState conn, DeepwaterRun run, (int X, int Y) start, Action<string> log)
    {
        var devices = new List<Device> { new() { Kind = Kind.Bathysphere, Id = conn.TakeObjectId(), X = start.X, Y = start.Y, Landed = 2 } };
        if (!run.Voyage)
        {
            var at = conn.Layout.Near((start.X + BackupBeside, start.Y));
            devices.Add(new() { Kind = Kind.Backup, Id = conn.TakeObjectId(), X = at.X, Y = at.Y });
        }
        else
            foreach (var cell in run.Board.Cells.Skip(1))
                if (cell.Point("bathysphere") is { } p)
                    devices.Add(new() { Kind = Kind.Capsule, Id = conn.TakeObjectId(), X = p.X, Y = p.Y });
        lock (run.Gate) run.Devices.AddRange(devices);
        log($"Allflame extraction: Bathysphere at ({start.X}, {start.Y}), " + (run.Voyage
            ? $"{devices.Count(d => d.Kind == Kind.Capsule)} capsules" : $"backup capsule at ({devices[^1].X}, {devices[^1].Y})"));
    }

    // ---- the objects ----

    static async Task Create(ClientState conn, Device device, double now, double landAt)
    {
        if (Info(device.Type) is not { } info) return;
        var obj = new QuestObject(device.X, device.Y, info.Components, 0, null, null,
            TransitionState: (byte)(device.Sent || device.Called ? info.States : 1));
        if (device.Kind == Kind.Bathysphere)
            obj = obj with
            {
                StateMachine = States.Value.GetValueOrDefault(device.Type)?.Set("landed", device.Landed),
                // G: DeepwaterBathysphereDrop (f32, f32) read as (seconds left, elapsed).
                Tasks = device.Landed == 0 && landAt > now ? [(DeepwaterConstants.BathysphereDropTask, DropTask(landAt - now))] : [],
            };
        var player = PlayerOf(conn);
        if (!await global::Game.SendWorldCreate(player, device.Id, InstanceEntities.CreateObject(device.Id, InstanceEntities.ObjectTypeHash(device.Type), obj.Blob()),
            $"0x02bb Allflame {device.Kind} 0x{device.Id:x} at ({device.X}, {device.Y}){(device.Kind == Kind.Bathysphere ? $" landed {device.Landed}" : device.Sent || device.Called ? " (gone)" : "")}"))
            return;
        await global::Game.SendWorldRecord(player, device.Id, InstanceProtocol.EnterWakeZone(device.Id));
    }

    static byte[] DropTask(double seconds)
    {
        var b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0), (float)seconds);
        BitConverter.TryWriteBytes(b.AsSpan(4), 0f);
        return b;
    }

    /// <summary>Creates the run's Bathyspheres and capsules on this connection as they are now, and the deck's
    /// CollectionChest once someone rode up.</summary>
    public static async Task Show(ClientState conn, DeepwaterRun run)
    {
        Device[] devices;
        double landAt;
        lock (run.Gate) (devices, landAt) = ([.. run.Devices], run.LandAt);
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var device in devices) await Create(conn, device, now, landAt);
        if (run.DeckChest != 0) await ShowDeckChest(conn, run);
    }

    static async Task Transition(ClientState conn, Device device)
    {
        if (Info(device.Type) is not { } info || QuestObject.TransitionableIndex(info.Components) is not (int t and >= 0)) return;
        await global::Game.SendWorldRecord(PlayerOf(conn), device.Id, InstanceEntities.TransitionableUpdate(device.Id, (byte)t, (byte)info.States),
            $"0x02bc Allflame {device.Kind} 0x{device.Id:x} takes off (Transitionable → {info.States})");
    }

    static async Task SendLanded(ClientState conn, Device device)
    {
        if (Info(device.Type) is not { } info || States.Value.GetValueOrDefault(device.Type) is not { } states) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        await global::Game.SendWorldRecord(PlayerOf(conn), device.Id, InstanceEntities.StateMachineUpdate(device.Id, (byte)index, states.Set("landed", device.Landed)),
            $"0x02bc Allflame Bathysphere 0x{device.Id:x} landed = {device.Landed}");
    }

    /// <summary>The run's flags on an entry: the Bathysphere's gate (3562) while it can be used, the Voyage's defence
    /// (3572) while a called Bathysphere is coming or waiting; the deck's 3625 only after a ride up.</summary>
    public static async Task SyncFlags(ClientState conn, DeepwaterRun run)
    {
        bool extract = run.Phase == DeepwaterRun.RunPhase.Extractable || run.Phase == DeepwaterRun.RunPhase.Collapsing && !run.Voyage;
        bool defend = run.Phase is DeepwaterRun.RunPhase.Descending or DeepwaterRun.RunPhase.Extractable;
        var on = new List<string>();
        var off = new List<string>();
        void Want(string flag, bool want) { if (conn.HasQuestFlag(flag) != want) (want ? on : off).Add(flag); }
        Want(DeepwaterConstants.CanExtract, extract);
        Want(DeepwaterConstants.VoyageDefend, defend);
        if (run.Phase != DeepwaterRun.RunPhase.Ended) Want(DeepwaterConstants.InPseudoHub, false);
        var (remote, stream, ciphers) = conn.Wire;
        if (on.Count > 0) await SetQuestFlags(remote, stream, ciphers, conn, on);
        if (off.Count > 0) await SetQuestFlags(remote, stream, ciphers, conn, off, on: false);
    }

    // ---- use ----

    /// <summary>C→S 0x24 on a Bathysphere, capsule or the deck's CollectionChest of the run; false when the object is not
    /// one of these.</summary>
    public static async Task<bool> Use(IModuleContext module, ClientState conn, DeepwaterRun run, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var world = WorldOf(conn);
        var player = PlayerOf(conn);
        if (run.DeckChest != 0 && use.TargetId == run.DeckChest)
        {
            if (DeckChestAt(conn) is { } chest && await world.WalkUpTo(chest.X, chest.Y, (Info(CollectionChest)?.Size ?? 1) + 2, requestedAt))
            {
                conn.ModuleWindow = null;
                await player.Send(InstanceWaypoints.OpenUi(CollectionUi, run.DeckChest), $"0x007b open CollectionInventory (kind 0x{CollectionUi:x2}, inventory 114) at the CollectionChest 0x{run.DeckChest:x} (G)");
            }
            return true;
        }
        Device? device;
        lock (run.Gate) device = run.Devices.FirstOrDefault(d => d.Id == use.TargetId);
        if (device is null) return false;
        double now = conn.Clock.Elapsed.TotalSeconds;
        string? why = null;
        if (run.Phase == DeepwaterRun.RunPhase.Ended) why = "the run ended";
        else if (conn.PlayerLife == 0) why = "dead";
        else if (device.Kind == Kind.Bathysphere && (!conn.HasQuestFlag(DeepwaterConstants.CanExtract) || device.Landed == 0))
            why = device.Landed == 0 ? "still descending" : "not DeepwaterCanExtract";
        else if (device.Kind != Kind.Bathysphere && (device.Sent || device.Called)) why = "it took off";
        else if (device.Kind != Kind.Bathysphere && !run.InLight((device.X, device.Y), now)) why = "not within Lanternlight";
        if (why is not null)
        {
            module.Log($"{player.Character} <= 0x0024 Allflame {device.Kind} 0x{device.Id:x} refused: {why}");
            await DeepwaterLoot.EndAction(conn);
            return true;
        }
        int reach = (Info(device.Type)?.Size ?? 1) + 2;
        if (!await world.WalkUpTo(device.X, device.Y, reach, requestedAt)) return true;
        switch (device.Kind)
        {
            case Kind.Bathysphere:
                module.Log($"{player.Character} <= 0x0024 Allflame Bathysphere 0x{device.Id:x}: ride up");
                await End(module, conn, run, backup: false);
                break;
            case Kind.Backup:
                lock (run.Gate)
                {
                    if (device.Called) return true;
                    device.Called = true;
                }
                module.Log($"{player.Character} <= 0x0024 Allflame backup capsule 0x{device.Id:x}: an early exit, a failure");
                foreach (var member in DeepwaterLoot.Members(conn)) await Transition(member, device);
                await End(module, conn, run, backup: true);
                break;
            default:
                lock (run.Gate) run.OpenCapsule[player.Character] = device.Id;
                conn.ModuleWindow = CapsuleWindow;
                await player.Send(InstanceWaypoints.OpenUi(CapsuleUi, device.Id),
                    $"0x007b open ExtractionInventory (kind 0x{CapsuleUi:x2}, inventory 113) for capsule 0x{device.Id:x} (G: the u32 is the capsule)");
                break;
        }
        return true;
    }

    /// <summary>Why the held item may not go into inventory 113: only through an open capsule window of a running run.</summary>
    public static string? CapsuleRefusal(ClientState conn, DeepwaterRun? run, byte row)
    {
        if (row != CapsuleRow) return null;
        if (run is null || run.Phase == DeepwaterRun.RunPhase.Ended) return "no Allflame run here";
        lock (run.Gate) return run.OpenCapsule.ContainsKey(CharacterName(conn)) && conn.ModuleWindow == CapsuleWindow ? null : "no capsule window is open";
    }

    // ---- the capsule's buttons ----

    static Device? OpenCapsuleOf(ClientState conn, DeepwaterRun run)
    {
        lock (run.Gate)
            return run.OpenCapsule.TryGetValue(CharacterName(conn), out uint id) ? run.Devices.FirstOrDefault(d => d.Id == id && d.Kind == Kind.Capsule) : null;
    }

    /// <summary>Ruleset op 2, Send to Surface: the capsule's items (113) go to the character's collection (114, saved with
    /// the character), the capsule takes off, QuestFlags 3547 is set, the window closes (mode 7) and the deck's count
    /// follows (mode 4). Needs IsMapOwner, a filled 113, an unsent capsule and another unsent one left (G: "the final
    /// Capsule" of ClientStrings 8441). Returns the ids moved.</summary>
    public static async Task<IReadOnlyList<uint>> SendToSurface(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        var player = PlayerOf(conn);
        var items = ItemsOf(conn);
        var capsule = OpenCapsuleOf(conn, run);
        string? why = null;
        if (capsule is null) why = "no capsule window open";
        else if (run.Phase == DeepwaterRun.RunPhase.Ended) why = "the run ended";
        else if (!conn.HasQuestFlag(DeepwaterConstants.MapOwner)) why = "not IsMapOwner";
        else if (items.Items(CapsuleRow).Count == 0) why = "the capsule is empty";
        else
            lock (run.Gate)
            {
                if (capsule.Sent || capsule.Called) why = "the capsule took off";
                else if (run.Devices.Count(d => d.Kind == Kind.Capsule && !d.Sent && !d.Called) <= 1) why = "the last capsule (the player would be stranded)";
                else capsule.Sent = true;
            }
        if (why is not null)
        {
            module.Log($"{player.Character} <= 0x02ca DeepwaterRules SendItemsToSurface refused: {why}");
            return [];
        }
        var moved = new List<InventoryItem>();
        var removed = new List<uint>();
        Saves.Atomically(() =>
        {
            foreach (var it in items.Items(CapsuleRow))
            {
                var shape = ShapeOf(it.Item) ?? new(1, 1, "");
                if (items.FreeCell(CollectionRow, shape.Width, shape.Height, ShapeOf) is not { } cell) break;
                if (items.Remove(CapsuleRow, it.Id) is null) continue;
                removed.Add(it.Id);
                moved.Add(items.Put(CollectionRow, it with { X = cell.X, Y = cell.Y }));
            }
        });
        if (removed.Count > 0)
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(CapsuleRow), items.NextRevision(CapsuleRow), removed, []),
                $"0x00bf inventory 113: {removed.Count} items sent to the surface");
        if (moved.Count > 0)
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(CollectionRow), items.NextRevision(CollectionRow), [], moved),
                $"0x00bf inventory 114: {moved.Count} items arrive");
        var (remote, stream, ciphers) = conn.Wire;
        await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.StoredItems]);
        foreach (var member in DeepwaterLoot.Members(conn)) await Transition(member, capsule!);
        await CloseWindow(conn, run);
        await SendCount(conn);
        int stayed = items.Items(CapsuleRow).Count;
        module.Log($"{player.Character} <= 0x02ca DeepwaterRules SendItemsToSurface: capsule 0x{capsule!.Id:x} sent {moved.Count} items to inventory 114"
            + (stayed > 0 ? $" ({stayed} did not fit and stay)" : ""));
        return removed;
    }

    /// <summary>Ruleset op 3, Call Bathysphere (also the HUD's leave button): at an open, unsent capsule of a Voyage with an
    /// empty 113. The capsule takes off, a Bathysphere descends there (<c>landed</c> 0, task 0x2387) for 30 s, the
    /// lanterns of the other cells collapse and the swarm starts; then it lands (<c>landed</c> 1) and can be used.</summary>
    public static async Task CallBathysphere(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        var player = PlayerOf(conn);
        var capsule = OpenCapsuleOf(conn, run);
        double now = conn.Clock.Elapsed.TotalSeconds;
        string? why = null;
        Device? bathysphere = null;
        if (!run.Voyage) why = "a Chart leaves by its Bathysphere or the backup capsule (G)";
        else if (capsule is null) why = "no capsule window open";
        else if (ItemsOf(conn).Items(CapsuleRow).Count > 0) why = "the capsule holds items";
        else
            lock (run.Gate)
            {
                if (run.Phase != DeepwaterRun.RunPhase.Exploring) why = $"the run is {run.Phase}";
                else if (capsule.Sent || capsule.Called) why = "the capsule took off";
                else
                {
                    capsule.Called = true;
                    run.Phase = DeepwaterRun.RunPhase.Descending;
                    run.LandAt = now + Descent;
                    bathysphere = new Device { Kind = Kind.Bathysphere, Id = conn.TakeObjectId(), X = capsule.X, Y = capsule.Y, Landed = 0 };
                    run.Devices.Add(bathysphere);
                }
            }
        if (why is not null || bathysphere is null)
        {
            module.Log($"{player.Character} <= 0x02ca DeepwaterRules RequestExtraction refused: {why}");
            return;
        }
        module.Log($"{player.Character} <= 0x02ca DeepwaterRules RequestExtraction: capsule 0x{capsule!.Id:x} calls the Bathysphere 0x{bathysphere.Id:x}, {Descent:0} s");
        await CloseWindow(conn, run);
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            await Transition(member, capsule);
            await Create(member, bathysphere, now, now + Descent);
            var (remote, stream, ciphers) = member.Wire;
            await SetQuestFlags(remote, stream, ciphers, member, [DeepwaterConstants.VoyageDefend]);
        }
        // The light at the capsule holds (G: the lanterns of its cell and any lighting it); the others collapse.
        var cell = run.Board.CellAt((capsule.X, capsule.Y));
        await DeepwaterCollapse.Start(module, conn, run, l =>
        {
            long dx = l.X - capsule.X, dy = l.Y - capsule.Y;
            return !(cell is not null && cell.Contains((l.X, l.Y)) || dx * dx + dy * dy <= (long)DeepwaterConstants.BaseLanternRadius * DeepwaterConstants.BaseLanternRadius);
        }, null);
    }

    static async Task CloseWindow(ClientState conn, DeepwaterRun run)
    {
        lock (run.Gate) run.OpenCapsule.Remove(CharacterName(conn));
        if (conn.ModuleWindow == CapsuleWindow) conn.ModuleWindow = null;
        if (DeepwaterRuleset.CloseCapsule() is { } close)
            await PlayerOf(conn).Send(close, "0x02c9 ruleset DeepwaterRules mode 7: close the capsule window");
    }

    static async Task SendCount(ClientState conn)
    {
        int count = ItemsOf(conn).Items(CollectionRow).Count;
        if (DeepwaterRuleset.CollectionCount((uint)count) is { } message)
            await PlayerOf(conn).Send(message, $"0x02c9 ruleset DeepwaterRules mode 4: collection {count} items");
    }

    // ---- per tick ----

    /// <summary>One tick: a dead player ends the run (a failure); a called Bathysphere lands after its descent.</summary>
    public static async Task Tick(IModuleContext module, ClientState conn, DeepwaterRun run, Func<ClientState, string, Task> failed)
    {
        if (run.Phase == DeepwaterRun.RunPhase.Ended || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return;
        if (conn.PlayerLife == 0)
        {
            await failed(conn, "died");
            return;
        }
        if (run.Phase != DeepwaterRun.RunPhase.Descending) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        Device? landed = null;
        lock (run.Gate)
            if (run.Phase == DeepwaterRun.RunPhase.Descending && run.LandAt > 0 && now >= run.LandAt)
            {
                run.Phase = DeepwaterRun.RunPhase.Extractable;
                run.LandAt = 0;
                landed = run.Devices.LastOrDefault(d => d.Kind == Kind.Bathysphere && d.Landed == 0);
                if (landed is not null) landed.Landed = 1;
            }
        if (landed is null) return;
        module.Log($"{CharacterName(conn)}: Allflame Bathysphere 0x{landed.Id:x} landed at ({landed.X}, {landed.Y})");
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            await SendLanded(member, landed);
            var (remote, stream, ciphers) = member.Wire;
            await SetQuestFlags(remote, stream, ciphers, member, [DeepwaterConstants.CanExtract]);
        }
    }

    // ---- the ends ----

    /// <summary>The ride up: everyone in the run goes to the deck's <c>default</c> point with the run's flags off and 3625
    /// on, the HUD's state 3 and the success or fail line (mode 5/6), the collection count (mode 4); the CollectionChest
    /// appears; the Sulphur drops as one stack at the deck's LootSpiralMarker (the clicker's). Success needs a Chart's
    /// lanterns all placed or a Voyage's called Bathysphere, and a quarter of the possible Sulphur (G); the backup capsule
    /// is a failure. Without a deck point the players travel to The Sovereign and the Sulphur goes to the inventory.
    /// Returns the members who rode up.</summary>
    public static async Task<IReadOnlyList<ClientState>> End(IModuleContext module, ClientState conn, DeepwaterRun run, bool backup)
    {
        int sulphur, cap;
        bool success;
        lock (run.Gate)
        {
            if (run.Phase == DeepwaterRun.RunPhase.Ended) return [];
            (sulphur, cap) = (run.Sulphur, run.SulphurCap);
            bool done = run.Voyage ? run.Phase == DeepwaterRun.RunPhase.Extractable : run.Phase == DeepwaterRun.RunPhase.Collapsing;
            success = !backup && done && (cap == 0 || sulphur * 4 >= cap);
            run.Phase = DeepwaterRun.RunPhase.Ended;
            run.LandAt = 0;
            run.Outcome = success ? "success" : backup ? "failure: the backup capsule" : !done ? "failure: not finished" : $"failure: {sulphur}/{cap} Sulphur";
            run.OpenCapsule.Clear();
        }
        var members = DeepwaterLoot.Members(conn).ToList();
        module.Log($"{CharacterName(conn)}: Allflame {(run.Voyage ? "Voyage" : "Chart")} ends by the {(backup ? "backup capsule" : "Bathysphere")}: {run.Outcome}, {sulphur} Sulphur, {members.Count} players ride up");
        var deck = run.Board.DeckDefault;
        if (deck is not null && run.DeckChest == 0 && DeckChestAt(conn) is not null) run.DeckChest = conn.TakeObjectId();
        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var (remote, stream, ciphers) = member.Wire;
            await SetQuestFlags(remote, stream, ciphers, member,
                [DeepwaterConstants.CanExtract, DeepwaterConstants.LanternController, DeepwaterConstants.MapOwner, DeepwaterConstants.VoyageDefend], on: false);
            if (deck is not { } at)
            {
                module.Log($"{CharacterName(member)}: the deck has no default point; travelling to The Sovereign");
                if (member == conn) await Receive(conn, sulphur);
                await WorldOf(member).TravelTo(DeepwaterModule.HubId, "Allflame extraction (no deck)");
                continue;
            }
            await SetQuestFlags(remote, stream, ciphers, member, [DeepwaterConstants.InPseudoHub]);
            var to = member.Layout.Near((at.X + 4 * i, at.Y));
            lock (member.Monsters)
            {
                Interlocked.Increment(ref member.ActionCount);
                member.Walk.Teleport(to.X, to.Y, member.Clock.Elapsed.TotalSeconds);
            }
            await global::Game.SendWorldRecord(PlayerOf(member), PlayerId, InstanceProtocol.Teleport(PlayerId, to.X, to.Y),
                $"player rides the Bathysphere up to the deck ({to.X}, {to.Y})");
            await SendEnd(member, success);
            if (run.DeckChest != 0) await ShowDeckChest(member, run);
            await SendCount(member);
        }
        if (deck is not null && sulphur > 0 && members.Contains(conn)) await DropSulphur(module, conn, sulphur);
        return members;
    }

    static async Task SendEnd(ClientState conn, bool success)
    {
        var player = PlayerOf(conn);
        if (DeepwaterRuleset.Collapse(3) is { } hide) await player.Send(hide, "0x02c9 ruleset DeepwaterRules mode 3: 3 (the run is over)");
        if ((success ? DeepwaterRuleset.Success() : DeepwaterRuleset.Failure()) is { } line)
            await player.Send(line, $"0x02c9 ruleset DeepwaterRules mode {(success ? 5 : 6)}: {(success ? "success" : "failure")}");
    }

    /// <summary>A failed run (a death, drowning, leaving another way): the run ends for everyone in it, its Sulphur is
    /// lost, the capsule's leftovers (113) are removed (G) and the fail line plays for those still here. The flags are
    /// put right at the next entry (StartRun). Returns the players it ended for.</summary>
    public static async Task<IReadOnlyList<ClientState>> Fail(IModuleContext module, ClientState conn, DeepwaterRun run, string reason)
    {
        lock (run.Gate)
        {
            if (run.Phase == DeepwaterRun.RunPhase.Ended) return [];
            run.Phase = DeepwaterRun.RunPhase.Ended;
            run.LandAt = 0;
            run.Outcome = $"failure: {reason}, {run.Sulphur} Sulphur lost";
            run.OpenCapsule.Clear();
        }
        var members = DeepwaterLoot.Members(conn).Append(conn).Distinct().ToList();
        module.Log($"{CharacterName(conn)}: Allflame {(run.Voyage ? "Voyage" : "Chart")} {run.Outcome}");
        foreach (var member in members)
        {
            bool live = !member.Closed && !member.LeavingArea;
            var items = ItemsOf(member);
            var left = items.Items(CapsuleRow);
            var removed = left.Where(it => items.Remove(CapsuleRow, it.Id) is not null).Select(it => it.Id).ToList();
            if (removed.Count > 0) module.Log($"{CharacterName(member)}: {removed.Count} items left in the capsule are lost");
            if (!live) continue;
            if (removed.Count > 0)
                await PlayerOf(member).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(CapsuleRow), items.NextRevision(CapsuleRow), removed, []),
                    $"0x00bf inventory 113: {removed.Count} items lost");
            if (member.ModuleWindow == CapsuleWindow) member.ModuleWindow = null;
            var (remote, stream, ciphers) = member.Wire;
            await SetQuestFlags(remote, stream, ciphers, member, [DeepwaterConstants.CanExtract, DeepwaterConstants.VoyageDefend], on: false);
            await SendEnd(member, success: false);
        }
        return members;
    }

    // ---- the haul ----

    static (int X, int Y)? DeckChestAt(ClientState conn) =>
        conn.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == CollectionChest) is { } o ? (o.X, o.Y) : null;

    static async Task ShowDeckChest(ClientState conn, DeepwaterRun run)
    {
        if (DeckChestAt(conn) is not { } at || Info(CollectionChest) is not { } info) return;
        var o = conn.Layout.Level!.RoomObjects.First(r => r.Type == CollectionChest);
        var blob = new QuestObject(at.X, at.Y, info.Components, o.Rotation, null, null) { StateMachine = States.Value.GetValueOrDefault(CollectionChest) }.Blob();
        var player = PlayerOf(conn);
        if (await global::Game.SendWorldCreate(player, run.DeckChest, InstanceEntities.CreateObject(run.DeckChest, InstanceEntities.ObjectTypeHash(CollectionChest), blob),
            $"0x02bb Allflame CollectionChest 0x{run.DeckChest:x} at ({at.X}, {at.Y})"))
            await global::Game.SendWorldRecord(player, run.DeckChest, InstanceProtocol.EnterWakeZone(run.DeckChest));
    }

    static async Task DropSulphur(IModuleContext module, ClientState conn, int amount)
    {
        if (ItemsFor(conn).Find(Sulphur) is not { Supported: true } type)
        {
            module.Log($"{CharacterName(conn)}: {Sulphur} is not an item the server writes; {amount} Sulphur not given");
            return;
        }
        var marker = conn.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == LootSpiralMarker);
        var player = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var at = conn.Layout.Near(marker is not null ? (marker.X, marker.Y) : (player.X + 3, player.Y));
        var (remote, stream, ciphers) = conn.Wire;
        int max = Math.Max(1, (int)(ShapeOf(ItemCatalog.Create(type))?.MaxStack ?? ushort.MaxValue));
        for (int left = amount, i = 0; left > 0; i++)
        {
            int stack = Math.Min(Math.Min(left, max), ushort.MaxValue);
            left -= stack;
            var ground = new WorldItemObject(at.X + i, at.Y, ItemCatalog.Create(type) with { StackSize = (ushort)stack });
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground,
                $"Allflame haul: {stack} Dead Man's Sulphur at ({ground.GridX}, {ground.GridY}){(marker is null ? " (beside the player, no LootSpiralMarker)" : "")}", justDropped: true);
        }
    }

    static async Task Receive(ClientState conn, int amount)
    {
        if (amount <= 0 || ItemsFor(conn).Find(Sulphur) is not { Supported: true } type) return;
        await WorldOf(conn).ReceiveCurrency(type.Hash, amount, _ => true, "Allflame haul");
    }
}
