using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Village.Mapping;

namespace GameServer.Modules.Village;

/// <summary>
/// The Kingsmarch village (Settlers of Kalguur): the settlement the panels are drawn from, and the action channel those
/// panels send.
///
/// The client keeps one record per ruleset kind on its <c>ClientVillage</c> (list at <c>+0xcfd8</c>); kind <c>0x9b</c> is the
/// town record (<c>0x5a8</c> bytes) and is built by S→C <c>0x1f</c> (u8 kind, u32 value) from the client's factory table. The
/// panels are HUD member windows opened by S→C <c>0x7b</c> kinds (screen table <c>0x142fd89f0</c>: <c>0x54</c> recombinator,
/// <c>0x55</c> recruitment, <c>0x56</c> town map, <c>0x57</c> mine, <c>0x58</c> port, <c>0x59</c> farm, <c>0x5a</c> blacksmith,
/// <c>0x5b</c> disenchanting, <c>0x5c</c> map runners, <c>0x5d</c> fishing, <c>0x5e</c> currency exchange; each is a HUD member
/// window, so the client always has it and <c>0x142169dd0</c> just Shows it),
/// and every button they show is sent as C→S <c>0x2ca</c>: <c>u16 id | u8 kind | compact-int length | blob</c>, where the blob
/// is the action byte and its arguments.
/// </summary>
public sealed class VillageModule : IModule
{
    /// <summary>C→S 0x2ca: what a Kingsmarch panel sends when a button is pressed.</summary>
    public const ushort ActionId = InstanceVillage.ActionId;

    /// <summary>The ruleset record kind of the town (the client's <c>ClientVillage</c>): 0x5a8 bytes, created by S→C 0x1f.
    /// The farm record is 0x74.</summary>
    public const byte TownRecordKind = InstanceVillage.TownKind;

    /// <summary>The village's own state: what the treasury holds and who works where. The panel draws these numbers from
    /// the settlement record the client keeps; the server owns them.</summary>
    public sealed class Settlement
    {
        /// <summary>The treasury (the panel's "Treasury Gold").</summary>
        public uint Gold;
        /// <summary>How many of the population have a job (the panel's "Population n/90").</summary>
        public byte Workers;
        /// <summary>How many the village can house (the client's VillageBaseStats row 20728: 90).</summary>
        public byte Capacity = 90;
        /// <summary>Worker index → VillageJobs row.</summary>
        public readonly Dictionary<byte, byte> Assignments = [];
        /// <summary>The recruits waiting to be hired (the recruitment panel).</summary>
        public readonly List<byte> Recruits = [];
        /// <summary>The <c>VillageUpgradeCategories</c> rows this settlement has bought.</summary>
        public readonly HashSet<int> Upgrades = [];
        /// <summary>Workers told to stop mapping after the map they are in (the town map's pause button).</summary>
        public readonly HashSet<byte> Paused = [];
    }

    readonly ConditionalWeakTable<IPlayer, Settlement> _settlements = new();

    /// <summary>What the panel's action byte means. The names come from the client's own action lambdas; the ones marked
    /// "live" were read off a real click.</summary>
    static readonly Dictionary<byte, string> Actions = new()
    {
        [0x00] = "reroll recruits (u8 tier)",
        [0x03] = "assign worker (u8 worker, u8 VillageJobs row)",
        [0x04] = "assign farm (u8 worker, u8 VillageResources row)",
        [0x05] = "perform upgrade (u8, VillageUpgrades row)",
        [0x06] = "send shipment (u8 slot, u8 port, u8 failure, 16 u32)",
        [0x07] = "abandon shipment (u8 slot)",
        [0x0a] = "abandon captured shipment",
        [0x0b] = "reroll port quota (u8 VillageShippingPorts row)",
        [0x12] = "pause mapping after this map (u8, u8)",
        [0x13] = "take reward (3 u32)",
        [0x14] = "take reward to location (6 u32)",
        [0x15] = "take reward to socket (5 u32)",
        [0x16] = "take all rewards (u32)",
        [0x18] = "give treasury gold (u32)",
        [0x19] = "take treasury gold (u32)",
    };

    /// <summary>The village of a player in the league.</summary>
    public Settlement Of(IPlayer player) => _settlements.GetValue(player, _ => new Settlement());

    /// <summary>The states the client has already been sent, per connection: the file's stamp and what went out.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPlayer, string> _sent = new();

    /// <summary>Where the candidate state frames are written for a live test: one <c>kind subtag hex</c> line per frame,
    /// built from the settlement the server actually holds. Copy a line into <see cref="StateFile"/> to send just that one
    /// frame: the client's reader does not resynchronise, so a frame it cannot parse takes the connection with it.</summary>
    public const string FramesFile = "data/kingsmarch-frames.txt";

    /// <summary>Where the state frames to push are written, one per line: <c>kind subtag hex</c>. Rewriting the file
    /// re-sends them on the next tick, so a payload can be tried against the live client without a rebuild.</summary>
    public const string StateFile = "data/kingsmarch-state.txt";

    /// <summary>Put a file here and the server opens the port window itself, the stand-in for clicking Tujen's Shipping
    /// line: a test client reaches the port panel without a hand on the mouse. Once per connection, in the town.</summary>
    public const string OpenFile = "data/kingsmarch-open-port.txt";

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPlayer, Counter> _opened = new();

    sealed class Counter { public int Value; }

    static async Task OpenPort(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        if (!File.Exists(OpenFile)) return;
        if (_opened.GetValue(player, _ => new Counter()).Value++ != 0) return;
        await player.Send(InstanceWaypoints.OpenUi(0x58),
            "0x007b 0x57 open KalguurPortWindow (dev: " + OpenFile + ")");
    }

    /// <param name="player">A player in the league.</param>
    /// <param name="now">The tick's time.</param>
    static async Task StatePoll(IPlayer player, double now)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var file = new FileInfo(StateFile);
        if (!file.Exists) return;
        string stamp = $"{file.LastWriteTimeUtc.Ticks}/{file.Length}";
        if (_sent.TryGetValue(player, out string? sent) && sent == stamp) return;
        _sent.AddOrUpdate(player, stamp);
        foreach (string raw in await File.ReadAllLinesAsync(StateFile))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            byte kind = Convert.ToByte(parts[0], 16), subtag = Convert.ToByte(parts[1], 16);
            byte[] payload = Convert.FromHexString(parts[2]);
            await player.Send(StateFrame(kind, subtag, payload),
                $"0x02c9 village state kind 0x{kind:x2} subtag {subtag}: {Convert.ToHexString(payload)}");
        }
        if (!File.Exists(FramesFile)) await WriteFrames(player);
    }


    /// <summary>S→C 0x2c9, the village's state channel (the counterpart of 0x2ca): the client's reader 0x141fa7ca0 takes
    /// <c>u16 id | u8 kind | compact-int length | blob</c>, keeps the kind at +0x90 and hands the blob to ClientVillage
    /// (vtable slot 8, 0x141767f60), which reads one subtag byte and its payload. One frame carries one subtag.</summary>
    static byte[] StateFrame(byte kind, byte subtag, ReadOnlySpan<byte> payload)
    {
        var blob = new List<byte>(payload.Length + 1) { subtag };
        blob.AddRange(payload.ToArray());
        var frame = new List<byte>(blob.Count + 5) { 0x02, 0xc9, kind };
        int v = blob.Count;
        if (v < 0x80) frame.Add((byte)v);
        else if (v < 0x4000) { frame.Add((byte)(0x80 | (v >> 8))); frame.Add((byte)v); }
        else { frame.Add((byte)(0xc0 | (v >> 16))); frame.Add((byte)(v >> 8)); frame.Add((byte)v); }
        frame.AddRange(blob);
        return frame.ToArray();
    }

    /// <summary>The frames the town window would need, built from the settlement the server holds. The ship frames are
    /// exact (subtag 2: slot, port row, failure row, then the 16 cargo counts the panel's own send action carries); the
    /// port frames (subtag 9) are the same six numbers the pin's quota update takes, whose meaning is still unknown, so
    /// they are written out for a one-frame-at-a-time live test rather than pushed.</summary>
    static async Task WriteFrames(IPlayer player)
    {
        var lines = new List<string>
        {
            "# kind subtag hex — one frame per line; copy ONE line into " + StateFile + " and save",
        };
        var ports = KingsmarchPort.PortList();
        lines.Add($"# {ports.Count} ports");
        lines.Add($"9b 01 {ports.Count:x2}");
        foreach (var port in ports)
        {
            // Zeroes first, on purpose. The frame is only safe once the client has built the port window's pin board
            // ([ClientVillage+0x5c8], filled when KalguurPortWindow is opened): pushing a quota before that walks into a
            // NULL and takes the client down with c0000005 at 0x1402D1029 (WER event 1001, 2026-11-22 11:49:49).
            var six = new List<byte> { (byte)port.Row, 6 };
            for (int i = 0; i < 6; i++) six.AddRange(BitConverter.GetBytes(0u)[..4]);
            lines.Add($"9b 09 {Convert.ToHexString(six.ToArray())}");
        }
        var who = player.Character ?? "";
        foreach (var ship in KingsmarchPort.ShipList(who))
        {
            var frame = new List<byte> { (byte)ship.Slot, (byte)(ship.PortRow < 0 ? 0xff : ship.PortRow),
                (byte)(ship.FailureRow < 0 ? 0xff : ship.FailureRow) };
            foreach (int count in ship.Cargo) frame.AddRange(BitConverter.GetBytes((uint)count)[..4]);
            lines.Add($"9b 02 {Convert.ToHexString(frame.ToArray())}");
        }
        var vault = KingsmarchPort.Vault(who);
        lines.Add($"# crew {vault.Crew}, barter {vault.Barter}, gold {vault.Gold}");
        try { await File.WriteAllLinesAsync(FramesFile, lines); }
        catch (IOException ex) { Console.WriteLine($"[gateway] {FramesFile}: {ex.Message}"); }
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        MappingCommands.Register(module.Manifest.Id);
        GameEvents.Tick.Add(module.Manifest.Id, async (conn, now) =>
        {
            var player = Program.PlayerOf(conn);
            await OpenPort(player);
            await StatePoll(player, now);
            MappingEngine.Advance(player.Character ?? "", now);
        });
        GameEvents.Packet(ActionId).Add(module.Manifest.Id, (conn, bytes) =>
        {
            var kind = InstanceVillage.Kind(bytes);
            var blob = InstanceVillage.Blob(bytes);
            var action = blob.Length > 0 ? blob[0] : (byte)0;
            var named = Actions.GetValueOrDefault(action, "unknown action");
            var settlement = Of(Program.PlayerOf(conn));
            var args = blob.Length > 1 ? blob[1..] : [];

            // The panel's buttons are the settlement's own: the treasury buttons change the gold, the job list assigns a
            // worker. Each is applied to the village the server owns.
            var player = Program.PlayerOf(conn);
            var who = player.Character ?? "";
            var note = action switch
            {
                0x18 => Give(settlement, Amount(args), args, who),
                0x19 => Take(settlement, Amount(args), args, who),
                0x03 => Assign(settlement, args),
                0x06 => Sail(player, args),
                0x07 => args.Length == 0 ? "abandon (no slot)" : KingsmarchPort.DropShip(who, args[0]) ? $"ship {args[0]} abandoned" : $"no ship in slot {args[0]}",
                0x00 => Recruit(player, args),
                0x04 => Farm(player, args),
                0x05 => Upgrade(settlement, args),
                0x0a => "abandon captured shipment: none is captured here",
                0x0b => Reroll(who, args),
                0x12 => Pause(settlement, args),
                0x13 or 0x14 or 0x15 => Collect(who, action, args),
                0x16 => Collect(who, action, args),
                _ => null,
            };

            Console.WriteLine($"[gateway] 0x02ca village action 0x{action:x2} {named}, kind 0x{kind:x2}, " +
                $"blob {Convert.ToHexString(blob)}{(note is null ? "" : ", " + note)}");
            return Task.FromResult(true);
        });
    }

    /// <summary><c>VillageSharedConstants.json</c>: what one quota reroll costs.</summary>
    const int QuotaRerollCost = 15000;

    /// <summary>The gold an action carries. The panels write little-endian, like every other number on the wire.</summary>
    static uint Amount(ReadOnlySpan<byte> args) => args.Length switch
    {
        >= 4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(args),
        >= 2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(args),
        1 => args[0],
        _ => 0,
    };

    /// <summary>The port panel's send button: <c>u8 slot, u8 port row, u8 failure row, 16 × u32</c> cargo counts indexed
    /// by <c>VillageExports.json</c> row, exactly the array a dock slot is filled with on the way back.</summary>
    static string Sail(IPlayer player, ReadOnlySpan<byte> args)
    {
        if (args.Length < 3 + 16 * 4) return $"send shipment (short: {args.Length} bytes)";
        var cargo = new int[16];
        for (int i = 0; i < 16; i++)
            cargo[i] = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(args[(3 + i * 4)..]);
        string said = KingsmarchPort.SendCargo(player, args[1], cargo, out int value, out int crew);
        return $"send shipment slot {args[0]} port row {args[1]}: {said} (value {value}, crew {crew})";
    }

    /// <summary>The panel's ADD: the player puts gold into the treasury.</summary>
    static string Give(Settlement settlement, uint amount, ReadOnlySpan<byte> args, string who)
    {
        settlement.Gold += amount;
        long gold = KingsmarchPort.Fund(who, amount);
        return $"treasury +{amount} (gold now {gold})";
    }

    /// <summary>The panel's REMOVE: the player takes gold out of the treasury.</summary>
    static string Take(Settlement settlement, uint amount, ReadOnlySpan<byte> args, string who)
    {
        uint taken = Math.Min(amount, settlement.Gold);
        settlement.Gold -= taken;
        long gold = KingsmarchPort.Gold(who);
        return $"treasury -{taken} (gold now {gold})";
    }

    /// <summary>The recruitment panel's hire button: the tier of the slot the player picked.</summary>
    static string Recruit(IPlayer player, ReadOnlySpan<byte> args)
    {
        int index = args.Length > 0 ? args[0] + 1 : 1;
        string said = KingsmarchTown.Recruit(player, index).GetAwaiter().GetResult();
        return $"recruit (slot {index}): {said}";
    }

    /// <summary>The farm panel's assign button: which worker tends which plot. The panel names the plot by its row in the
    /// crop table, and the town's plots are numbered from 1, so the row is used as the plot's order.</summary>
    static string Farm(IPlayer player, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "assign farm (no arguments)";
        int worker = args[0] + 1, plot = args[1] + 1;
        string said = KingsmarchTown.Assign(player, worker, plot).GetAwaiter().GetResult();
        return $"assign farm (worker {worker} → plot {plot}): {said}";
    }

    /// <summary>The town map's upgrade button: which <c>VillageUpgradeCategories</c> row was bought.</summary>
    static string Upgrade(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "perform upgrade (no arguments)";
        int row = args[1];
        bool first = settlement.Upgrades.Add(row);
        return first ? $"upgrade row {row} bought" : $"upgrade row {row} was already bought";
    }

    /// <summary>The port panel's quota reroll: one port, paid out of the treasury.</summary>
    static string Reroll(string who, ReadOnlySpan<byte> args)
    {
        if (args.Length < 1) return "reroll quota (no port)";
        string said = KingsmarchPort.RerollQuota(who, args[0], QuotaRerollCost, out int[] quota);
        return $"{said} (quota {string.Join('/', quota)})";
    }

    /// <summary>The town map's pause button: stop mapping after the current map.</summary>
    static string Pause(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "pause mapping (no arguments)";
        byte worker = args[0];
        bool paused = args[1] != 0;
        if (paused) settlement.Paused.Add(worker);
        else settlement.Paused.Remove(worker);
        return $"worker {worker} {(paused ? "pauses" : "resumes")} mapping after this map";
    }

    /// <summary>The port panel's collect button: one shipment by index, or every one of them for <c>0x16</c>.</summary>
    static string Collect(string who, byte action, ReadOnlySpan<byte> args)
    {
        if (action == 0x16) return $"collected every shipment: {KingsmarchPort.TakeRewards(who, -1)} barter";
        if (args.Length < 4) return "collect reward (no index)";
        int index = (int)Amount(args);
        int taken = KingsmarchPort.TakeRewards(who, index);
        return taken == 0 ? $"no shipment waiting at index {index}" : $"collected shipment {index}: {taken} barter";
    }

    /// <summary>A worker takes a job (the panel's job list).</summary>
    static string Assign(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "assign worker (no arguments)";
        byte worker = args[0], row = args[1];
        settlement.Assignments[worker] = row;
        settlement.Workers = (byte)settlement.Assignments.Count;
        return $"worker {worker} on VillageJobs row {row} ({settlement.Workers}/{settlement.Capacity})";
    }
}
