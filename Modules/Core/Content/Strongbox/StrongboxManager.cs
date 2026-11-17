using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// The strongboxes of an area instance: planned once by its first connection (count, types, cells; kept in the
/// instance's ContentState, so a re-entry, a party member and a module reload see the same boxes in the same state),
/// created for each connection as it enters, opened by a walk-up click (C→S 0x24) or a warcry that opens chests.
/// An opened box is created again under a new id with its Chest opened and its StateMachine <c>opened</c> set (Chest has
/// no update reader, as for the quest chests), and the box's ambush and loot follow.
/// </summary>
public static class StrongboxManager
{
    const string PlanKey = "Strongbox/plan";

    /// <summary>A planned box: its Strongboxes row, cell, facing, current object id and whether it was opened.</summary>
    sealed record Box(int Row, int X, int Y, float Rotation, uint Id, bool Opened);

    sealed class SharedArea
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Box>? Boxes;
    }

    static readonly ConditionalWeakTable<InstanceState, SharedArea> Areas = new();
    // The box ids each connection was sent, by the box's index in the plan: who gets an opened box's replacement.
    static readonly ConditionalWeakTable<ClientState, Dictionary<int, uint>> Presented = new();

    public static IDisposable Lifetime() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { Areas.Clear(); Presented.Clear(); }
    }

    static int AreaLevel(ClientState conn) =>
        Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    static IReadOnlyDictionary<uint, int> AreaStats(ClientState conn) => conn.Instance?.KeptAreaStats ?? new Dictionary<uint, int>();

    /// <summary>GameEvents.AreaGenerated: the instance's boxes (planned by its first connection), created for this one.</summary>
    public static async Task SpawnArea(ClientState conn)
    {
        if (conn.Instance is not { } instance || InTown(conn) || conn.Layout.Walkable is null) return;
        var area = Areas.GetOrCreateValue(instance);
        await area.Gate.WaitAsync();
        try
        {
            var boxes = area.Boxes ??= Load(instance) ?? Plan(conn, instance);
            for (int i = 0; i < boxes.Count; i++) await Present(conn, i, boxes[i]);
        }
        finally { area.Gate.Release(); }
    }

    static List<Box>? Load(InstanceState instance) =>
        instance.ContentState.TryGetValue(PlanKey, out string? json) ? JsonSerializer.Deserialize<List<Box>>(json) : null;

    static void Save(InstanceState instance, List<Box> boxes) => instance.ContentState[PlanKey] = JsonSerializer.Serialize(boxes);

    static List<Box> Plan(ClientState conn, InstanceState instance)
    {
        var boxes = new List<Box>();
        int level = AreaLevel(conn);
        var stats = AreaStats(conn);
        var (count, roll) = StrongboxRules.Count(conn.Layout.Id, level, conn.Seed, stats);
        var rng = new Random(unchecked((int)StrongboxRules.Seed(conn.Seed, "boxes")));
        var spots = StrongboxRules.Spots(conn.Layout, count, rng);
        bool hardMode = LeagueFor(conn).HardMode;
        foreach (var (x, y) in spots)
        {
            if (StrongboxRules.Pick(StrongboxCatalog.All, hardMode, level, stats, rng) is not { } type) break;
            boxes.Add(new Box(type.Row, x, y, (float)(rng.NextDouble() * Math.Tau), conn.TakeObjectId(), false));
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} strongboxes: {conn.Layout.Id} seed {conn.Seed} level {level}" +
            $"{(hardMode ? " (HardMode weights)" : "")}: Ambush roll {roll} → {count}; {spots.Count} spot(s), {boxes.Count} box(es)" +
            string.Concat(boxes.Select(b => $"; {StrongboxCatalog.ByRow(b.Row)?.Name} (row {b.Row}) 0x{b.Id:x} at ({b.X}, {b.Y})")));
        Save(instance, boxes);
        return boxes;
    }

    /// <summary>Creates the box for this connection unless it has it already, and blocks its cell on the server's walk
    /// estimate like the client's (Chest.ot <c>blocking</c>).</summary>
    static async Task Present(ClientState conn, int index, Box box)
    {
        if (StrongboxCatalog.ByRow(box.Row) is not { } type) return;
        var shown = Presented.GetOrCreateValue(conn);
        lock (shown)
        {
            if (shown.TryGetValue(index, out uint id) && id == box.Id) return;
            shown[index] = box.Id;
        }
        var (remote, stream, ciphers) = conn.Wire;
        conn.SetBlocker(box.Id, box.X, box.Y, type.Size);
        await SendWorldCreate(remote, stream, ciphers, conn, box.Id,
            InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(type.Type), type.Blob(box.X, box.Y, box.Rotation, box.Opened)),
            $"0x02bb strongbox 0x{box.Id:x} {type.Name} ({type.Type}) at ({box.X}, {box.Y}), opened={box.Opened}");
        await SendWorldRecord(remote, stream, ciphers, conn, box.Id, InstanceProtocol.EnterWakeZone(box.Id), null);
    }

    /// <summary>The box this object id is in the instance's plan, with its index.</summary>
    static async Task<(int Index, Box Box)?> Find(ClientState conn, uint id)
    {
        if (conn.Instance is not { } instance) return null;
        var area = Areas.GetOrCreateValue(instance);
        await area.Gate.WaitAsync();
        try
        {
            var boxes = area.Boxes ??= Load(instance);
            if (boxes is null) return null;
            int index = boxes.FindIndex(b => b.Id == id);
            return index < 0 ? null : (index, boxes[index]);
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>GameEvents.ObjectUse: a click on a box. The player walks up (the quest chests' walk-up, 0x01ab started and
    /// ended), and on arrival the box opens, once per instance.</summary>
    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (await Find(conn, use.TargetId) is not var (index, box) || StrongboxCatalog.ByRow(box.Row) is not { } type) return false;
        if (box.Opened || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        if (!TryBeginObjectWalk(conn, requestedAt, box.X, box.Y, type.Size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        var (remote, stream, ciphers) = conn.Wire;
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, box.X, box.Y,
                    use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens strongbox 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return true;
        if (PlayerWalk.InReach(stop, (box.X, box.Y), type.Size, conn.Layout.Walkable)) await Open(conn, index, "walked up");
        else Console.WriteLine($"[gateway] {remote} strongbox 0x{box.Id:x}: out of reach from ({stop.X}, {stop.Y})");
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
        return true;
    }

    /// <summary>GameEvents.ChestsOpened: a warcry that opens nearby chests opens the boxes in its radius.</summary>
    public static async Task OpenNear(ClientState conn, (int X, int Y) at, int radius)
    {
        if (conn.Instance is not { } instance) return;
        var area = Areas.GetOrCreateValue(instance);
        List<int> near;
        await area.Gate.WaitAsync();
        try
        {
            if ((area.Boxes ??= Load(instance)) is not { } boxes) return;
            near =[.. Enumerable.Range(0, boxes.Count).Where(i => !boxes[i].Opened
                && (long)(boxes[i].X - at.X) * (boxes[i].X - at.X) + (long)(boxes[i].Y - at.Y) * (boxes[i].Y - at.Y) <= (long)radius * radius)];
        }
        finally { area.Gate.Release(); }
        foreach (int index in near) await Open(conn, index, "warcry");
    }

    /// <summary>Opens the box (the first opener only): its replacement for every connection that has it, then its ambush
    /// and its loot, rolled and dropped for the opener.</summary>
    static async Task Open(ClientState conn, int index, string how)
    {
        if (conn.Instance is not { } instance) return;
        var area = Areas.GetOrCreateValue(instance);
        Box opened;
        StrongboxCatalog.Entry type;
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is not { } boxes || index >= boxes.Count || boxes[index].Opened
                || StrongboxCatalog.ByRow(boxes[index].Row) is not { } found) return;
            type = found;
            var closed = boxes[index];
            opened = closed with { Opened = true, Id = conn.TakeObjectId() };
            boxes[index] = opened;
            Save(instance, boxes);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} strongbox 0x{closed.Id:x} {type.Name} at ({closed.X}, {closed.Y}) opened ({how}) by " +
                $"{CharacterName(conn)}: now 0x{opened.Id:x}");
            foreach (var member in Program.InGame.Keys.Where(c => ReferenceEquals(c.Instance, instance) && !c.Closed && !c.LeavingArea))
            {
                if (!Presented.TryGetValue(member, out var shown)) continue;
                uint old;
                lock (shown) if (!shown.TryGetValue(index, out old) || old != closed.Id) continue;
                member.RemoveBlocker(old);
                var (remote, stream, ciphers) = member.Wire;
                await SendWorldRemoval(remote, stream, ciphers, member, old, null);
                await Present(member, index, opened);
            }
        }
        finally { area.Gate.Release(); }
        var stats = AreaStats(conn);
        int level = AreaLevel(conn);
        var rng = new Random();
        await Ambush(conn, type, (opened.X, opened.Y), level, stats, rng);
        await DropLoot(conn, type, (opened.X, opened.Y), level, stats, rng);
    }

    /// <summary>The box's guards: one of the area's own packs (its MonsterPacks by weight, its count), at the chest mods'
    /// summoned rarity, around the box and coming for the player; one more pack with
    /// <c>map_strongboxes_additional_pack_chance_%</c>.</summary>
    static async Task Ambush(ClientState conn, StrongboxCatalog.Entry box, (int X, int Y) at, int level,
        IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var (remote, stream, ciphers) = conn.Wire;
        var packs = (Packs.For(conn.Layout.Id)?.Scattered ?? [])
            .Where(p => p.Pack.Entries.Any(e => Spawnable(e.Variety) is not null)).ToList();
        if (packs.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} strongbox {box.Name}: no monster packs for {conn.Layout.Id}; no ambush");
            return;
        }
        int extra = Stats.Key("map_strongboxes_additional_pack_chance_%") is uint key ? stats.GetValueOrDefault(key) : 0;
        int packCount = 1 + (extra > 0 && rng.Next(100) < extra ? 1 : 0);
        var made = new List<string>();
        for (int p = 0; p < packCount; p++)
        {
            int roll = rng.Next(Math.Max(1, packs.Sum(x => x.Weight)));
            var pack = packs.FirstOrDefault(x => (roll -= x.Weight) < 0).Pack ?? packs[^1].Pack;
            int count = rng.Next(Math.Max(1, pack.MinCount), Math.Max(pack.MinCount, pack.MaxCount) + 1);
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                if (Spawnable(pack.PickVariety(rng)) is not { } variety) continue;
                double angle = rng.NextDouble() * Math.Tau, radius = 5 + rng.NextDouble() * 4;
                int x = at.X + (int)Math.Round(Math.Cos(angle) * radius), y = at.Y + (int)Math.Round(Math.Sin(angle) * radius);
                uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)level, box.SummonRarity, x, y,
                    alone: false, log: false, owned: (_, monster) => monster.Aggro = true);
                if (id == 0) break;
                spawned++;
            }
            made.Add($"{pack.Id} × {spawned}{(p > 0 ? " (additional pack)" : "")}");
        }
        Console.WriteLine($"[gateway] {remote} strongbox {box.Name} ambush, level {level}, {box.SummonRarity}: {string.Join(", ", made)}" +
            $" (additional pack chance {extra}%)");

        static MonsterCatalog.Variety? Spawnable(int row) =>
            Monsters.Find(row.ToString()) is { Supported: true } v && Emerges.Kind(v) == EmergeKind.None ? v : null;
    }

    /// <summary>The box's items (<see cref="StrongboxLoot"/>) on the ground around it, as a monster's drops lie.</summary>
    static async Task DropLoot(ClientState conn, StrongboxCatalog.Entry box, (int X, int Y) at, int level,
        IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return;
        var (remote, stream, ciphers) = conn.Wire;
        var items = ItemDrops.Stacked(StrongboxLoot.Roll(conn, box, level, stats, rng));
        foreach (var drop in items)
        {
            double angle = rng.NextDouble() * Math.Tau, r = 2 + rng.NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * r), y = at.Y + (int)Math.Round(Math.Sin(angle) * r);
            if (conn.Layout.Walkable is { } walk && !walk(x, y)) (x, y) = conn.Layout.Near((x, y));
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground,
                $"0x02bb create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) from strongbox {box.Name} at ({x}, {y})",
                justDropped: true);
        }
        Console.WriteLine($"[gateway] {remote} strongbox {box.Name} ({box.Kind}, {box.Items} base items) dropped {items.Count} item(s): " +
            string.Join(", ", items.Select(d => d.Item.StackSize > 1 ? $"{d.Type.Name} ×{d.Item.StackSize}" : d.Type.Name)));
    }
}
