using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Data.Terrain.Generation;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

static partial class QuestobjectsRules
{
    /// <summary>An ambush chest in Merveil's Caverns: its area, Chests.dat type, ambush boss variety, thematic adds
    /// (Slimy Bloodsuckers from the room's <c>-bosspack</c>), and fallback model.</summary>
    internal sealed record TrickChestRule(string Area, string ChestType, string BossVariety, string AddVariety, int AddCount,
        string DefaultModel);

    internal sealed record PlacedTrickChest(TrickChestRule Rule, string Model, int X, int Y, float Rotation, float? Height,
        (int X, int Y)? BossPoint);

    internal const string DaughterAddVariety = "Metadata/Monsters/Spawn/SpawnEmergeDaughters_";
    internal const string TrickChestModel = "Metadata/Chests/CopperChests/CopperChestEpicTrick.ao";

    internal static readonly TrickChestRule[] TrickChestRules =
    [
        new(Act1Areas.CavernOfWrath, Act1Objects.AmarissaTrickChest, Act1Monsters.Amarissa, DaughterAddVariety, 3, TrickChestModel),
        new(Act1Areas.CavernOfAnger, Act1Objects.AmbrosiaTrickChest, Act1Monsters.Ambrosia, DaughterAddVariety, 3, TrickChestModel),
    ];

    static readonly RuleTable<ClientState, Dictionary<uint, PlacedTrickChest>> ActiveTrickChests = new();

    internal static TrickChestRule? TrickChestForArea(string areaId) =>
        TrickChestRules.FirstOrDefault(r => r.Area.Equals(areaId, StringComparison.Ordinal));

    /// <summary>Locates the area's Trick Chest from the generated level's placed rooms (<c>special_seawitchboss1a..e.arm</c>
    /// or <c>2a..e.arm</c>), captured room objects, or <c>mapboss1</c> fallback, together with the room's <c>mapboss1</c>
    /// point when available.</summary>
    internal static PlacedTrickChest? FindTrickChestPlacement(string areaId, uint seed, ServerLevels.Level? level, TrickChestRule rule)
    {
        var points = AreaPopulation.LoadPoints(areaId, seed, []);
        (int X, int Y)? bossPoint = points.FirstOrDefault(p => p.Name.Equals("mapboss1", StringComparison.OrdinalIgnoreCase)) is { } bp
            ? (bp.X, bp.Y) : null;

        if (level is not null)
        {
            var found = level.RoomObjects.FirstOrDefault(o => o.Type.Equals(rule.ChestType, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                string model = string.IsNullOrEmpty(found.Model) ? rule.DefaultModel : found.Model;
                return new(rule, model, found.X, found.Y, found.Rotation, float.IsNaN(found.Z) ? null : found.Z, bossPoint);
            }
        }

        var captured = AreaPopulation.LoadObjects(areaId, seed, [])
            .FirstOrDefault(o => o.Type.Equals(rule.ChestType, StringComparison.OrdinalIgnoreCase));
        if (captured is not null)
        {
            string model = string.IsNullOrEmpty(captured.Model) ? rule.DefaultModel : captured.Model;
            return new(rule, model, captured.X, captured.Y, captured.Angle, captured.Height, bossPoint);
        }

        if (bossPoint is { } fallback)
            return new(rule, QuestChestModel(rule.ChestType) ?? rule.DefaultModel, fallback.X, fallback.Y, 0, null, fallback);

        return null;
    }

    /// <summary>Serializes a Trick Chest using its exact <c>object_types</c> components
    /// (<c>Positioned, BaseEvents, Animated, Chest, Stats, ObjectMagicProperties, InteractionAction</c>).</summary>
    internal static byte[] TrickChestBlob(PlacedTrickChest chest, QuestObjectTypes.Info info, bool opened) =>
        new QuestObject(chest.X, chest.Y, info.Components, chest.Rotation, chest.Height, chest.Model, Opened: opened).Blob();

    /// <summary>Computes where the ambush boss spawns when the Trick Chest opens: at <c>mapboss1</c> when already close,
    /// otherwise 12 cells from the chest toward <c>mapboss1</c> so the ambush erupts in sight inside the chamber.</summary>
    internal static (int X, int Y) TrickChestAmbushPoint(AreaLayout layout, PlacedTrickChest chest)
    {
        (int X, int Y) desired;
        if (chest.BossPoint is { } bp)
        {
            double dx = bp.X - chest.X, dy = bp.Y - chest.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            desired = d is > 4 and <= 24 ? bp
                : d > 24 ? (chest.X + (int)Math.Round(dx / d * 12), chest.Y + (int)Math.Round(dy / d * 12))
                : (chest.X + 8, chest.Y);
        }
        else
        {
            desired = (chest.X + 8, chest.Y);
        }
        return layout.Walkable is { } walk && !walk(desired.X, desired.Y) ? layout.Near(desired) : desired;
    }

    internal static async Task SpawnTrickChests(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (TrickChestForArea(conn.Layout.Id) is not { } rule) return;
        if (QuestObjectTypes.Find(rule.ChestType) is not { } info || !QuestObject.CanSerialize(info.Components))
        {
            Console.WriteLine($"[gateway] {remote} trick chest {rule.ChestType}: unsupported or missing object type");
            return;
        }
        var level = conn.Layout.Level ?? ServerLevels.For(conn.Layout.Id, conn.Seed);
        if (FindTrickChestPlacement(conn.Layout.Id, conn.Seed, level, rule) is not { } chest)
        {
            Console.WriteLine($"[gateway] {remote} trick chest {rule.ChestType}: no placement in {conn.Layout.Id} seed {conn.Seed}");
            return;
        }
        bool opened = conn.Instance.ChestOpened(chest.X, chest.Y);
        uint id = conn.TakeObjectId();
        var map = ActiveTrickChests.GetOrCreateValue(conn);
        lock (map) map[id] = chest;
        conn.SetBlocker(id, chest.X, chest.Y, info.Size);
        await SendWorldCreate(remote, stream, ciphers, conn, id,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(rule.ChestType), TrickChestBlob(chest, info, opened)),
            $"0x02bb trick chest 0x{id:x} {rule.ChestType} at ({chest.X}, {chest.Y}), opened={opened}");
        await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
    }

    internal static async Task<bool> UseTrickChest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!ActiveTrickChests.TryGetValue(conn, out var map)) return false;
        PlacedTrickChest? chest;
        lock (map) chest = map.GetValueOrDefault(use.TargetId);
        if (chest is null) return false;
        if (QuestObjectTypes.Find(chest.Rule.ChestType) is not { } info) return true;
        if (conn.Instance.ChestOpened(chest.X, chest.Y))
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
            return true;
        }
        if (!TryBeginObjectWalk(conn, requestedAt, chest.X, chest.Y, info.Size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, chest.X, chest.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens trick chest 0x{use.TargetId:x} {chest.Rule.ChestType} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return true;
        try
        {
            if (!PlayerWalk.InReach(stop, (chest.X, chest.Y), info.Size, conn.Layout.Walkable))
            {
                Console.WriteLine($"[gateway] {remote} {chest.Rule.ChestType}: out of reach from ({stop.X}, {stop.Y})");
                return true;
            }
            await OpenTrickChestCore(remote, stream, ciphers, conn, use.TargetId, chest, info);
        }
        finally
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
                still: () => PlayerActionCurrent(conn, action));
        }
        return true;
    }

    internal static async Task OpenTrickChestsNear(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius)
    {
        if (!ActiveTrickChests.TryGetValue(conn, out var map)) return;
        List<(uint Id, PlacedTrickChest Chest)> nearby;
        lock (map)
            nearby = [.. map.Where(e => Dist((e.Value.X, e.Value.Y), at) <= radius).Select(e => (e.Key, e.Value))];
        foreach (var (id, chest) in nearby)
            if (QuestObjectTypes.Find(chest.Rule.ChestType) is { } info)
                await OpenTrickChestCore(remote, stream, ciphers, conn, id, chest, info);
    }

    static async Task<bool> OpenTrickChestCore(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, PlacedTrickChest chest, QuestObjectTypes.Info info)
    {
        if (!conn.Instance.OpenChest(chest.X, chest.Y)) return false;
        await ReplaceOpenedTrickChest(remote, stream, ciphers, conn, id, chest, info);
        // Mirror the opened chest state to any party peers in the same instance.
        foreach (var peer in conn.Instance.PlayersExcept(conn.PeerId))
            if (peer.Owner is { Closed: false, LeavingArea: false } other && ReferenceEquals(other.Instance, conn.Instance)
                && ActiveTrickChests.TryGetValue(other, out var peerMap))
            {
                uint peerId = 0;
                lock (peerMap)
                    peerId = peerMap.FirstOrDefault(e => e.Value.X == chest.X && e.Value.Y == chest.Y && e.Value.Rule.ChestType == chest.Rule.ChestType).Key;
                if (peerId != 0)
                {
                    var pw = other.Wire;
                    await ReplaceOpenedTrickChest(pw.Remote, pw.Stream, pw.Ciphers, other, peerId, chest, info);
                }
            }
        byte level = (byte)Math.Clamp(Packs.For(conn.Layout.Id)?.AreaLevel ?? ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
        await DropTrickChestLoot(remote, stream, ciphers, conn, chest, level);
        await SpringTrickChestAmbush(remote, stream, ciphers, conn, chest, level);
        return true;
    }

    static async Task ReplaceOpenedTrickChest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint oldId, PlacedTrickChest chest, QuestObjectTypes.Info info)
    {
        // Chest has no update reader: replace with a new object id in Opened=true state (plays CopperChestEpicTrick.ao's trap sound).
        uint openedId = conn.TakeObjectId();
        var map = ActiveTrickChests.GetOrCreateValue(conn);
        lock (map)
        {
            map.Remove(oldId);
            map[openedId] = chest;
        }
        conn.RemoveBlocker(oldId);
        conn.SetBlocker(openedId, chest.X, chest.Y, info.Size);
        await SendWorldRemoval(remote, stream, ciphers, conn, oldId, null);
        await SendWorldCreate(remote, stream, ciphers, conn, openedId,
            InstanceEntities.CreateObject(openedId, InstanceEntities.ObjectTypeHash(chest.Rule.ChestType), TrickChestBlob(chest, info, opened: true)),
            $"0x02bb opened trick chest 0x{openedId:x} {chest.Rule.ChestType} (was 0x{oldId:x})");
        await SendWorldRecord(remote, stream, ciphers, conn, openedId, InstanceProtocol.EnterWakeZone(openedId), null);
    }

    static async Task DropTrickChestLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlacedTrickChest chest, byte level)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return;
        var rules = LeagueFor(conn).Rules;
        int count = Math.Max(1, (int)Math.Round(3 * Math.Max(0, rules.ItemDropMultiplier)));
        var drops = new List<ItemDrops.Drop>(DropsFor(conn).Container(level, count, 50, Random.Shared));
        if (DropsFor(conn).Gold(level, MonsterRarity.Magic, Random.Shared,
            rules.GoldMultiplier * SystemSettings.Default.GoldDropMultiplier) is { } gold)
            drops.Add(gold);
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            var at = conn.Layout.Near((chest.X + Random.Shared.Next(-4, 5), chest.Y + Random.Shared.Next(-4, 5)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint dropId = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, dropId, ground,
                $"0x02bb trick chest drops {drop.Type.Name} at ({at.X}, {at.Y})", justDropped: true);
        }
    }

    static async Task SpringTrickChestAmbush(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlacedTrickChest chest, byte level)
    {
        string claim = Quests.InstanceFlag($"trick-chest-ambush:{chest.Rule.ChestType}:{chest.X},{chest.Y}");
        if (!conn.Instance.SetState(claim)) return;
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
        var origin = TrickChestAmbushPoint(conn.Layout, chest);
        bool spawnedAny = false;
        if (Monsters.Find(chest.Rule.BossVariety) is { Supported: true } boss && boss.Id == chest.Rule.BossVariety)
        {
            Console.WriteLine($"[gateway] {remote} trick chest {chest.Rule.ChestType}: ambush! {boss.Name} appears at ({origin.X}, {origin.Y})");
            // Spawn risen (Transitionable state 2: create_transitioned runs EnableTargetable(), immediately targetable & collidable).
            uint bossId = await SpawnMonster(remote, stream, ciphers, conn, boss, level, MonsterRarity.Unique, origin.X, origin.Y,
                unrisen: false, alone: false, log: true);
            if (bossId != 0)
            {
                spawnedAny = true;
                lock (conn.Monsters)
                    if (conn.Monsters.TryGetValue(bossId, out var m)) m.Aggro = true;
            }
        }
        if (chest.Rule.AddCount > 0 && Monsters.Find(chest.Rule.AddVariety) is { Supported: true } add && add.Id == chest.Rule.AddVariety)
        {
            for (int i = 0; i < chest.Rule.AddCount; i++)
            {
                double angle = i * Math.Tau / chest.Rule.AddCount;
                var spot = conn.Layout.Near((chest.X + (int)Math.Round(Math.Cos(angle) * 7), chest.Y + (int)Math.Round(Math.Sin(angle) * 7)));
                uint addId = await SpawnMonster(remote, stream, ciphers, conn, add, level, MonsterRarity.Normal, spot.X, spot.Y,
                    unrisen: false, alone: false, log: false);
                if (addId != 0)
                {
                    spawnedAny = true;
                    lock (conn.Monsters)
                        if (conn.Monsters.TryGetValue(addId, out var m)) m.Aggro = true;
                }
            }
        }
        if (spawnedAny) await SendRemainingMonsters(remote, stream, ciphers, conn);
    }
}
