using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;

/// <summary>Delve, as a built-in mod (Program.Mods.cs): the Crawler and the Voltaxic Generator in the Azurite Mine Encampment,
/// the chart, the equipment upgrades. It reaches the rest of the server through the hooks of <see cref="IModContext"/>; its
/// own work still uses the Gateway's internals.</summary>
sealed class DelveFeature : IMod
{
    public void Load(IModContext mod)
    {
        mod.OnAreaGenerated(async player =>
        {
            var conn = ConnOf(player);
            var (remote, stream, ciphers) = conn.Wire;
            await SpawnDelveCrawler(remote, stream, ciphers, conn);
            await SpawnDelveGenerator(remote, stream, ciphers, conn);
        });
        mod.OnObjectUsed(async use =>
        {
            var conn = ConnOf(use.Player);
            var (remote, stream, ciphers) = conn.Wire;
            if (conn.DelveGeneratorObject != 0 && use.ObjectId == conn.DelveGeneratorObject)
                await OpenDelveGenerator(remote, stream, ciphers, conn, use.RequestedAt);
            else if (conn.DelveCrawlerObject != 0 && use.ObjectId == conn.DelveCrawlerObject)
                await OpenDelveChart(remote, stream, ciphers, conn, use.ObjectId, use.RequestedAt);
            else return false;
            return true;
        });
        mod.OnPacket(InstanceDelve.RouteId, async packet =>
        {
            var conn = ConnOf(packet.Player);
            var route = InstanceDelve.ReadRoute(packet.Bytes.ToArray());
            Console.WriteLine($"[gateway] {conn.Wire.Remote} Delve route requested: {string.Join(" -> ", route)}; route execution is not implemented");
            if (conn.PlayerSent && conn.Layout.Hash is StashCatalog.DelveHub or InstanceDelve.MineArea)
                await conn.Commands!.Reply("This mine route is not available yet.");
            return true;
        });
        foreach (ushort id in new[] { InstanceDelve.UpgradesRequestId, InstanceDelve.UpgradeId, InstanceDelve.PurchaseId })
            mod.OnPacket(id, async packet =>
            {
                var conn = ConnOf(packet.Player);
                var (remote, stream, ciphers) = conn.Wire;
                await UseDelveEquipment(remote, stream, ciphers, conn, packet.Opcode, packet.Bytes.ToArray());
                return true;
            });
        // C→S 0x23f: the Delve chart (opened by Niko) asks for its data; S→C 0x240 answers (an empty chart for now).
        mod.OnPacket(InstanceDelve.ChartRequestId, async packet =>
        {
            if (packet.Bytes.Length != 2) return false;
            var conn = ConnOf(packet.Player);
            var (remote, stream, ciphers) = conn.Wire;
            var (a, b, cells) = conn.DelveChartProbe;
            await Send(remote, stream, ciphers, InstanceDelve.Chart(a, b, cells),
                $"0x0240 Delve chart: {a}, {b}, {cells.Count} cell(s) {string.Join(" ", cells)}");
            return true;
        });
        mod.OnChatCommand("delve_crawler", "/delve_crawler [x y degrees] (dev: create the Delve Crawler in the Azurite Mine Encampment, or move and turn the one there; a click on it opens the Subterranean Chart)", async c =>
        {
            var conn = ConnOf(c.Player);
            var (remote, stream, ciphers) = conn.Wire;
            string[] args = [.. c.Args];
            if (conn.Layout.Hash is not (StashCatalog.DelveHub or InstanceDelve.MineArea)) { await conn.Commands!.Reply("Only in the Azurite Mine Encampment or mine"); return; }
            if (args.Length == 3 && int.TryParse(args[0], out int x) && int.TryParse(args[1], out int y) && float.TryParse(args[2],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees))
            {
                if (conn.DelveCrawlerObject == 0) await SpawnDelveCrawler(remote, stream, ciphers, conn);
                float radians = degrees * MathF.PI / 180;
                conn.DelveCrawlerAt = (x, y);
                await Send(remote, stream, ciphers, InstanceProtocol.Teleport(conn.DelveCrawlerObject, x, y, 0x7fffffff), $"Delve Crawler to ({x}, {y})");
                await Send(remote, stream, ciphers, InstanceHideoutEditing.Rotation(conn.DelveCrawlerObject, radians), $"Delve Crawler turned {degrees}°");
                foreach (uint part in conn.DelveCrawlerAttachments)
                    await Send(remote, stream, ciphers, InstanceProtocol.Teleport(part, x, y, 0x7fffffff), null);
                await BlockDelveCrawler(remote, stream, ciphers, conn, x, y, radians);
                await conn.Commands!.Reply($"Delve Crawler at ({x}, {y}), {degrees}°");
                return;
            }
            await SpawnDelveCrawler(remote, stream, ciphers, conn);
            await conn.Commands!.Reply($"Delve Crawler 0x{conn.DelveCrawlerObject:x} at {conn.DelveCrawlerAt}");
        });
        mod.OnChatCommand("sulphite", "/sulphite <amount 0-65535> (dev: the character's Voltaxic Sulphite, saved and sent as the Delve data 0x228)", async c =>
        {
            var conn = ConnOf(c.Player);
            var (remote, stream, ciphers) = conn.Wire;
            string[] args = [.. c.Args];
            if (args.Length != 1 || !ushort.TryParse(args[0], out ushort amount)) throw new ArgumentException("/sulphite <amount 0-65535>");
            CharacterSaves.SetSulphite(CharacterName(conn), amount);
            await Send(remote, stream, ciphers, PlayerDelveData(conn), $"0x0228 Delve data: {amount} sulphite (/sulphite)");
            await conn.Commands!.Reply($"Sulphite: {amount}");
        });
        mod.OnChatCommand("delve_chart", "/delve_chart <u32> <u16> [<u16> <u16> <u64> <u8> <u32> ...] (dev: what the next Delve chart (S→C 0x240) carries, to work out its fields)", async c =>
        {
            var conn = ConnOf(c.Player);
            var (remote, stream, ciphers) = conn.Wire;
            string[] args = [.. c.Args];
            try
            {
                var n = args.Select(a => a.StartsWith("0x") ? Convert.ToUInt64(a[2..], 16) : ulong.Parse(a)).ToArray();
                var cells = new List<InstanceDelve.Cell>();
                for (int i = 2; i + 4 < n.Length; i += 5) cells.Add(new((ushort)n[i], (ushort)n[i + 1], n[i + 2], (byte)n[i + 3], (uint)n[i + 4]));
                conn.DelveChartProbe = (n.Length > 0 ? (uint)n[0] : 0, n.Length > 1 ? (ushort)n[1] : (ushort)0, cells);
                await conn.Commands!.Reply($"Next Delve chart: {conn.DelveChartProbe.A}, {conn.DelveChartProbe.B}, {cells.Count} cell(s). Open it from Niko.");
            }
            catch (FormatException) { await conn.Commands!.Reply("Numbers only (decimal or 0x…)."); }
        });
    }

    /// <summary>Where the Crawler stands in the Azurite Mine Encampment, on the rails by the mine entrance, and its facing
    /// (placed live with /delve_crawler 408 310 90, 2026-10-30).</summary>
    static readonly (int X, int Y) DelveCrawlerAt = (408, 310);
    const float DelveCrawlerRotation = MathF.PI / 2;
    // hub.arm: console (337,307), room origin (69,69), size621, orientation3 => (376,352).
    static readonly (int X, int Y) DelveGeneratorAt = (376, 352);

    internal static byte[] PlayerDelveData(ClientState conn)
    {
        var equipment = PersonalStashOf(conn).Delve;
        ushort sulphite = CharacterSaves.Load(CharacterName(conn))?.Sulphite ?? 0;
        return InstanceDelve.DelveData(sulphite, equipment.Azurite, equipment.Flares, equipment.Dynamite, equipment.Upgrades);
    }

    static async Task SpawnDelveGenerator(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Hash != StashCatalog.DelveHub || conn.DelveGeneratorObject != 0) return;
        uint id = conn.NextObjectId++;
        conn.DelveGeneratorObject = id;
        var (x, y) = DelveGeneratorAt;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(DelveGeneratorObject.MetadataPath), new DelveGeneratorObject(x, y, 5.12494f).Blob()),
            $"Voltaxic Generator 0x{id:x} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        conn.SetBlocker(id, x, y, WalkBlockerObject.Size - 1);
        uint cables = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(cables,
            InstanceEntities.ObjectTypeHash(DelveGeneratorCablesObject.MetadataPath),
            new DelveGeneratorCablesObject(382, 370, 5.15258f).Blob()), "Voltaxic Generator cables");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(cables), null);
    }

    static async Task OpenDelveGenerator(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double requestedAt)
    {
        var (x, y) = DelveGeneratorAt;
        if (!TryBeginObjectWalk(conn, requestedAt, x, y, WalkBlockerObject.Size - 1, out var walk)) return;
        if (walk.Arrive > walk.Now) await Task.Delay(TimeSpan.FromSeconds(walk.Arrive - walk.Now));
        if (!PlayerActionCurrent(conn, walk.Action) || !PlayerWalk.InWalkUpRange(walk.Stop, (x, y), WalkBlockerObject.Size - 1)) return;
        conn.DelveGeneratorOpen = true;
        await Send(remote, stream, ciphers, PlayerDelveData(conn), "Voltaxic Generator resources");
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceDelve.UpgradesUi, 1), "Open Delve equipment upgrades");
    }

    static async Task UseDelveEquipment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort id, byte[] bytes)
    {
        if (!conn.PlayerSent || conn.Layout.Hash != StashCatalog.DelveHub || !conn.DelveGeneratorOpen || conn.DelveGeneratorObject == 0) return;
        (int X, int Y) here;
        lock (conn.Monsters) here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        if (!PlayerWalk.InWalkUpRange(here, DelveGeneratorAt, WalkBlockerObject.Size - 1))
        {
            conn.DelveGeneratorOpen = false;
            return;
        }
        bool changed = false;
        if (id == InstanceDelve.UpgradeId)
        {
            int type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
            changed = PersonalStashOf(conn).ChangeDelve(p => DelveEquipment.UpgradeEquipment(p, type));
        }
        else if (id == InstanceDelve.PurchaseId && bytes[2] <= 1)
            changed = PersonalStashOf(conn).ChangeDelve(p => DelveEquipment.Purchase(p, bytes[2] != 0, bytes[3]));
        await Send(remote, stream, ciphers, PlayerDelveData(conn), "Delve equipment state");
        if (changed) await SendCharacterStats(remote, stream, ciphers, conn, "Delve equipment upgrade");
    }

    // The Delve Crawler in the Azurite Mine Encampment. A click on it opens the Subterranean Chart, as Niko's option does.
    internal static async Task SpawnDelveCrawler(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Hash is not (StashCatalog.DelveHub or InstanceDelve.MineArea) || conn.DelveCrawlerObject != 0) return;
        var (x, y) = conn.Layout.Hash == StashCatalog.DelveHub ? DelveCrawlerAt
            : (conn.Layout.SpawnX, conn.Layout.SpawnY - 18);
        uint id = conn.NextObjectId++;
        conn.DelveCrawlerObject = id;
        conn.DelveCrawlerAt = (x, y);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(DelveMapViewerObject.MetadataPath),
            new DelveMapViewerObject(x, y, DelveCrawlerRotation).Blob()), $"0x02b9 create the Delve Crawler map viewer 0x{id:x} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 Delve Crawler 0x{id:x} enters wake zone");
        // DelveMapViewer's own transition callbacks select clips for its waypoint model, not this two-clip rig.
        // Explicitly select idle_01 (rig index 0), which loops the running engine's shake and sound.
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 0, 1), "Crawler idle_01");
        foreach (var (model, bone, light) in new[] {
            (DelveCrawlerAttachmentObject.LeftWheel, "aux_L_wheel_attachment", false),
            (DelveCrawlerAttachmentObject.RightWheel, "aux_R_wheel_attachment", false),
            (DelveCrawlerAttachmentObject.Torch, "aux_main_tip", true) })
        {
            uint part = conn.NextObjectId++;
            conn.DelveCrawlerAttachments.Add(part);
            string type = light ? DelveCrawlerAttachmentObject.LightType : EffectObject.TypePath;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(part, InstanceEntities.ObjectTypeHash(type),
                new DelveCrawlerAttachmentObject(x, y, id, model, bone, light).Blob()), $"Crawler attachment 0x{part:x}: {bone}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(part), null);
        }
        await BlockDelveCrawler(remote, stream, ciphers, conn, x, y, DelveCrawlerRotation);
    }

    /// <summary>How far the Crawler's walk blockers stand from its middle along its length (cells).</summary>
    const int DelveCrawlerHalfLength = 6;

    // The Crawler's type does not block (DelveRobot.ot: no blocking), so the player walked through it: three invisible
    // blockers (Metadata/NPC/Blocker) along its facing, and the same squares for the server's walk.
    static async Task BlockDelveCrawler(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int x, int y, float rotation)
    {
        foreach (uint old in conn.DelveCrawlerBlockers)
        {
            conn.RemoveBlocker(old);
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(old), $"0x02bb remove the Crawler blocker 0x{old:x}");
        }
        conn.DelveCrawlerBlockers.Clear();
        foreach (int along in new[] { -DelveCrawlerHalfLength, 0, DelveCrawlerHalfLength })
        {
            int bx = x + (int)MathF.Round(along * MathF.Cos(rotation)), by = y + (int)MathF.Round(along * MathF.Sin(rotation));
            uint id = conn.NextObjectId++;
            conn.DelveCrawlerBlockers.Add(id);
            conn.SetBlocker(id, bx, by, WalkBlockerObject.Size - 1);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WalkBlockerObject.MetadataPath),
                new WalkBlockerObject(bx, by).Blob()), $"0x02b9 create the Crawler blocker 0x{id:x} at ({bx}, {by})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
        await PushOutOfCollision(remote, stream, ciphers, conn);
    }

    // C→S 0x24 on the Crawler: walk there, then the Subterranean Chart (S→C 0x7b kind 0x1c with 1).
    static async Task OpenDelveChart(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint use, double requestedAt)
    {
        var at = conn.DelveCrawlerAt;
        int size = WalkBlockerObject.Size - 1; // Positioned object_size 5 is stored as radius 4.
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, size, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 Delve Crawler (0x{use:x}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        if (!PlayerWalk.InWalkUpRange(stop, at, size)) return;
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 1), "0x007b open the Delve chart (Crawler)",
            still: () => PlayerActionCurrent(conn, action));
    }
}
