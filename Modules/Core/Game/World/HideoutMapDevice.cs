using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>Creates the hideout's map device (its template's, or the variant the owner placed) and its button. Called
    /// with the player as Program.ViewOf: the device is each player's own view.</summary>
    public static async Task SpawnHideoutDevice(Player player)
    {
        lock (ButtonsOf(player)) ButtonsOf(player).Clear();
        if (HideoutFurniture(player) is not { } scene) return;
        var d = scene.Device;
        if (HideoutCatalog.TypeOf(d.Type!) is not var (components, states) || !HideoutObject.CanSerialize(components))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} hideout doodad {d.Name} ({d.Type}): unknown type or unsupported components, not created");
            return;
        }
        uint id = player.TakeObjectId();
        // "active" 1: the map device stands open and idle (its .ot: per-player quest flag MapDeviceActive).
        var obj = new HideoutObject(d.X, d.Y, d.Rotation, components, new HideoutDoodad(d.Hash, (byte)d.Variation),
            [.. states.Select(s => s == "active" ? 1 : 0)]);
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(d.Type!), obj.Blob()),
            $"0x02bb create hideout doodad 0x{id:x} {d.Name} ({d.Type}) at ({d.X}, {d.Y}), components {string.Join(", ", components)}");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 hideout doodad 0x{id:x} enters wake zone");
        lock (player.HideoutDoodads) player.HideoutDoodads[id] = d;
        await RegisterStationaryCollision(player, id, d.Type!, d.X, d.Y, d.Rotation, MapDeviceKey, d.Model);
        // The server's blocker is one circle around the model's whole hull; a button or portal place inside it (Sun's
        // button: 20 cells, hull 26; the Precursors' portal at the centre) could not be reached, so the circle stops short
        // of the nearest.
        double nearest = Enumerable.Range(0, MapDevice.PortalCount).Select(i => HideoutPortalPlace(d, i))
            .Min(p => Math.Sqrt((p.X - d.X) * (p.X - d.X) + (p.Y - d.Y) * (p.Y - d.Y)));
        if (HideoutCatalog.MapDeviceButton(d.Hash, d.Variation) is var (_, bx, by)) nearest = Math.Min(nearest, Math.Sqrt(bx * bx + by * by));
        if (StationaryCollision.Resolve(d.Type!, d.Model) is (int size, double hull) && nearest - ReachClearance is var reach && reach < hull)
        {
            double capped = Math.Max(1, reach);
            player.SetBlocker(id, d.X, d.Y, size, capped);
            player.HideoutEditor.Register(id, MapDeviceKey, d.X, d.Y, d.Rotation, size, capped);
        }
        await SpawnMapDeviceButton(player, id, d);
    }

    const double ReachClearance = 4;

    /// <summary>Whether this server can stand the map device type up in a hideout. The Puzzle Cube's client script runs
    /// a native puzzle (InteractPuzzleCube, with its grabber objects and sub-cube state) on hover and drag; without
    /// them the client crashes (live 2026-11-22, the magma cube), so it is refused.</summary>
    public static bool ServableMapDevice(string type) =>
        !type.EndsWith("/PuzzleCubeMapDevice", StringComparison.Ordinal)
        && HideoutCatalog.TypeOf(type) is var (components, _) && HideoutObject.CanSerialize(components);

    // A device variant's separate button (HideoutCatalog.MapDeviceButton), at its offset turned with the device.
    static async Task SpawnMapDeviceButton(Player player, uint device, HideoutCatalog.Doodad d)
    {
        if (HideoutCatalog.MapDeviceButton(d.Hash, d.Variation) is not var (type, dx, dy)) return;
        if (HideoutCatalog.TypeOf(type) is not var (components, states) || !HideoutObject.CanSerialize(components))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} map device button {type}: unknown type or unsupported components, not created");
            return;
        }
        var (x, y) = DeviceCell(d, dx, dy);
        uint id = player.TakeObjectId();
        var obj = new HideoutObject(x, y, d.Rotation, components, null, [.. states.Select(_ => 0)]);
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), obj.Blob()),
            $"0x02bb create map device button 0x{id:x} ({type}) at ({x}, {y}) for device 0x{device:x}");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 map device button 0x{id:x} enters wake zone");
        lock (ButtonsOf(player)) ButtonsOf(player)[id] = (device, d with { X = x, Y = y });
    }

    /// <summary>Where hideout map device <paramref name="d"/>'s portal <paramref name="index"/> stands, turned with the
    /// device: the place its model sets (facing the model's given degrees on top of the device's turn), else on the ring of
    /// its variation's radius (HideoutCatalog.MapDevicePortalRadius; 16 without a MapDevices.dat row). A device that opens
    /// one portal at a time and sets no place stands it at that radius towards its button: Atzoatl's model has a round
    /// platform at the foot of its stairs there (21-hideouts.md). Rotation null: the portals' usual facing.</summary>
    public static (int X, int Y, float? Rotation) HideoutPortalPlace(HideoutCatalog.Doodad d, int index)
    {
        // A device that opens one portal at a time stands each at the same place.
        bool single = HideoutNextPortalMs(d) is not null;
        if (single) index = 0;
        var spawns = HideoutCatalog.PortalSpawns(d.Model);
        if (index >= 0 && index < spawns.Count)
        {
            var (dx, dy, degrees) = spawns[index];
            var (x, y) = DeviceCell(d, dx, dy);
            return (x, y, degrees is double g ? d.Rotation + (float)(g * Math.PI / 180) : null);
        }
        int radius = HideoutCatalog.MapDevicePortalRadius(d.Hash, d.Variation) ?? 16;
        var (rx, ry) = MapDevice.PortalCell(0, 0, index, radius: radius);
        if (single && HideoutCatalog.MapDeviceButton(d.Hash, d.Variation) is var (_, bx, by) && (bx, by) != (0, 0))
        {
            double length = Math.Sqrt(bx * bx + by * by);
            (rx, ry) = ((int)Math.Round(bx * radius / length), (int)Math.Round(by * radius / length));
        }
        var cell = DeviceCell(d, rx, ry);
        return (cell.X, cell.Y, null);
    }

    /// <summary>For a device that opens one portal at a time (HideoutCatalog.MapDeviceNextPortalMs), the delay in ms
    /// before the next stands after one is taken; null for a device that opens all of them at once.</summary>
    public static int? HideoutNextPortalMs(HideoutCatalog.Doodad d) => HideoutCatalog.MapDeviceNextPortalMs(d.Hash, d.Variation);

    static (int X, int Y) DeviceCell(HideoutCatalog.Doodad d, int dx, int dy)
    {
        float c = MathF.Cos(d.Rotation), s = MathF.Sin(d.Rotation);
        return (d.X + (int)MathF.Round(dx * c - dy * s), d.Y + (int)MathF.Round(dx * s + dy * c));
    }

    /// <summary>After the editor moved or turned a map device: its button follows.</summary>
    public static async Task MoveMapDeviceButton(Player player, uint device, HideoutCatalog.Doodad d)
    {
        if (HideoutCatalog.MapDeviceButton(d.Hash, d.Variation) is not var (_, dx, dy)) return;
        var (x, y) = DeviceCell(d, dx, dy);
        uint id;
        lock (ButtonsOf(player))
        {
            if (ButtonsOf(player).FirstOrDefault(b => b.Value.Device == device) is not { Key: > 0 } button) return;
            id = button.Key;
            ButtonsOf(player)[id] = (device, d with { X = x, Y = y });
        }
        await SendWorldRecord(player, id, InstanceProtocol.Teleport(id, x, y, 0x7fffffff),
            $"map device button 0x{id:x} follows device 0x{device:x} to ({x},{y})");
        await Send(player, InstanceHideoutEditing.Rotation(id, d.Rotation), $"map device button rotation {d.Rotation:0.###}");
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, Dictionary<uint, (uint Device, HideoutCatalog.Doodad At)>> MapDeviceButtons = new();
    static Dictionary<uint, (uint Device, HideoutCatalog.Doodad At)> ButtonsOf(Player player) => MapDeviceButtons.GetValue(player, _ => []);

    /// <summary>The map device a click on this object opens, with the button standing in for where to walk; null when
    /// the object is not a device's button.</summary>
    public static (uint Device, HideoutCatalog.Doodad At)? MapDeviceOfButton(Player player, uint id)
    {
        lock (ButtonsOf(player)) return ButtonsOf(player).TryGetValue(id, out var b) ? b : null;
    }

    /// <summary>Removes the hideout's map device and its button; returns the device as it stood.</summary>
    public static async Task<HideoutCatalog.Doodad?> RemoveHideoutDevice(Player player)
    {
        List<uint> ids;
        HideoutCatalog.Doodad? last = null;
        lock (player.HideoutDoodads)
        {
            ids = [.. player.HideoutDoodads.Where(e => HideoutCatalog.IsPlaced(e.Value)).Select(e => e.Key)];
            foreach (uint id in ids) { last = player.HideoutDoodads[id]; player.HideoutDoodads.Remove(id); }
        }
        lock (ButtonsOf(player)) { ids.AddRange(ButtonsOf(player).Keys); ButtonsOf(player).Clear(); }
        foreach (uint id in ids)
        {
            player.RemoveBlocker(id);
            player.HideoutEditor.Unregister(id);
            await SendWorldRemoval(player, id, $"0x02bd remove map device object 0x{id:x}: replaced");
        }
        return last;
    }
}
