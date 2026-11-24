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

    static readonly RuleTable<Player, Dictionary<uint, PlacedTrickChest>> ActiveTrickChests = new();

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

    internal static async Task SpawnTrickChests(Player player)
    {
        if (TrickChestForArea(player.Layout.Id) is not { } rule) return;
        if (QuestObjectTypes.Find(rule.ChestType) is not { } info || !QuestObject.CanSerialize(info.Components))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} trick chest {rule.ChestType}: unsupported or missing object type");
            return;
        }
        var level = player.Layout.Level ?? ServerLevels.For(player.Layout.Id, player.Seed);
        if (FindTrickChestPlacement(player.Layout.Id, player.Seed, level, rule) is not { } chest)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} trick chest {rule.ChestType}: no placement in {player.Layout.Id} seed {player.Seed}");
            return;
        }
        bool opened = player.Instance.ChestOpened(chest.X, chest.Y);
        uint id = player.TakeObjectId();
        var map = ActiveTrickChests.GetOrCreateValue(player);
        lock (map) map[id] = chest;
        player.SetObjectBlocker(id, chest.X, chest.Y, info.Size, info.ActorOrChest);
        await SendWorldCreate(player, id,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(rule.ChestType), TrickChestBlob(chest, info, opened)),
            $"0x02bb trick chest 0x{id:x} {rule.ChestType} at ({chest.X}, {chest.Y}), opened={opened}");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
    }

    internal static async Task<bool> UseTrickChest(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!ActiveTrickChests.TryGetValue(player, out var map)) return false;
        PlacedTrickChest? chest;
        lock (map) chest = map.GetValueOrDefault(use.TargetId);
        if (chest is null) return false;
        if (QuestObjectTypes.Find(chest.Rule.ChestType) is not { } info) return true;
        if (player.Instance.ChestOpened(chest.X, chest.Y))
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }
        if (!TryBeginObjectWalk(player, requestedAt, chest.X, chest.Y, info.Size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, chest.X, chest.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens trick chest 0x{use.TargetId:x} {chest.Rule.ChestType} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return true;
        try
        {
            if (!PlayerWalk.InReach(stop, (chest.X, chest.Y), info.Size, player.Layout.Walkable))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} {chest.Rule.ChestType}: out of reach from ({stop.X}, {stop.Y})");
                return true;
            }
            await OpenTrickChestCore(player, use.TargetId, chest, info);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    internal static async Task OpenTrickChestsNear(Player player, (int X, int Y) at, int radius)
    {
        if (!ActiveTrickChests.TryGetValue(player, out var map)) return;
        List<(uint Id, PlacedTrickChest Chest)> nearby;
        lock (map)
            nearby = [.. map.Where(e => Dist((e.Value.X, e.Value.Y), at) <= radius).Select(e => (e.Key, e.Value))];
        foreach (var (id, chest) in nearby)
            if (QuestObjectTypes.Find(chest.Rule.ChestType) is { } info)
                await OpenTrickChestCore(player, id, chest, info);
    }

    static async Task<bool> OpenTrickChestCore(Player player, uint id, PlacedTrickChest chest, QuestObjectTypes.Info info)
    {
        if (!player.Instance.OpenChest(chest.X, chest.Y)) return false;
        await ReplaceOpenedTrickChest(player, id, chest, info);
        // Mirror the opened chest state to any party peers in the same instance.
        foreach (var other in player.Instance.PlayersExcept(player.Id))
            if (!other.Closed && !other.LeavingArea && ReferenceEquals(other.Instance, player.Instance)
                && ActiveTrickChests.TryGetValue(other, out var peerMap))
            {
                uint peerId = 0;
                lock (peerMap)
                    peerId = peerMap.FirstOrDefault(e => e.Value.X == chest.X && e.Value.Y == chest.Y && e.Value.Rule.ChestType == chest.Rule.ChestType).Key;
                if (peerId != 0)
                    await ReplaceOpenedTrickChest(other, peerId, chest, info);
            }
        byte level = (byte)Math.Clamp(Packs.For(player.Layout.Id)?.AreaLevel ?? ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
        await DropTrickChestLoot(player, chest, level);
        await SpringTrickChestAmbush(player, chest, level);
        return true;
    }

    static async Task ReplaceOpenedTrickChest(Player player, uint oldId, PlacedTrickChest chest, QuestObjectTypes.Info info)
    {
        // Chest has no update reader: replace with a new object id in Opened=true state (plays CopperChestEpicTrick.ao's trap sound).
        uint openedId = player.TakeObjectId();
        var map = ActiveTrickChests.GetOrCreateValue(player);
        lock (map)
        {
            map.Remove(oldId);
            map[openedId] = chest;
        }
        player.RemoveBlocker(oldId);
        player.SetObjectBlocker(openedId, chest.X, chest.Y, info.Size, info.ActorOrChest);
        await SendWorldRemoval(player, oldId, null);
        await SendWorldCreate(player, openedId,
            InstanceEntities.CreateObject(openedId, InstanceEntities.ObjectTypeHash(chest.Rule.ChestType), TrickChestBlob(chest, info, opened: true)),
            $"0x02bb opened trick chest 0x{openedId:x} {chest.Rule.ChestType} (was 0x{oldId:x})");
        await SendWorldRecord(player, openedId, InstanceProtocol.EnterWakeZone(openedId), null);
    }

    static async Task DropTrickChestLoot(Player player, PlacedTrickChest chest, byte level)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(player)) return;
        var rules = LeagueFor(player).Rules;
        int count = Math.Max(1, (int)Math.Round(3 * Math.Max(0, rules.ItemDropMultiplier)));
        var rng = AreaRng(player);
        var drops = new List<ItemDrops.Drop>(DropsFor(player).Container(level, count, 50, rng));
        if (DropsFor(player).Gold(level, MonsterRarity.Magic, rng,
            rules.GoldMultiplier * SystemSettings.Default.GoldDropMultiplier) is { } gold)
            drops.Add(gold);
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            var at = player.Layout.Near((chest.X + rng.Next(-4, 5), chest.Y + rng.Next(-4, 5)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint dropId = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, dropId, ground,
                $"0x02bb trick chest drops {drop.Type.Name} at ({at.X}, {at.Y})", justDropped: true);
        }
    }

    static async Task SpringTrickChestAmbush(Player player, PlacedTrickChest chest, byte level)
    {
        string claim = Quests.InstanceFlag($"trick-chest-ambush:{chest.Rule.ChestType}:{chest.X},{chest.Y}");
        if (!player.Instance.SetState(claim)) return;
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
        var origin = TrickChestAmbushPoint(player.Layout, chest);
        bool spawnedAny = false;
        if (Monsters.Find(chest.Rule.BossVariety) is { Supported: true } boss && boss.Id == chest.Rule.BossVariety)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} trick chest {chest.Rule.ChestType}: ambush! {boss.Name} appears at ({origin.X}, {origin.Y})");
            // Spawn risen (Transitionable state 2: create_transitioned runs EnableTargetable(), immediately targetable & collidable).
            uint bossId = await SpawnMonster(player, boss, level, MonsterRarity.Unique, origin.X, origin.Y,
                unrisen: false, alone: false, log: true);
            if (bossId != 0)
            {
                spawnedAny = true;
                if (player.Area.Monsters.TryGetValue(bossId, out var m)) m.Aggro = true;
            }
        }
        if (chest.Rule.AddCount > 0 && Monsters.Find(chest.Rule.AddVariety) is { Supported: true } add && add.Id == chest.Rule.AddVariety)
        {
            for (int i = 0; i < chest.Rule.AddCount; i++)
            {
                double angle = i * Math.Tau / chest.Rule.AddCount;
                var spot = player.Layout.Near((chest.X + (int)Math.Round(Math.Cos(angle) * 7), chest.Y + (int)Math.Round(Math.Sin(angle) * 7)));
                uint addId = await SpawnMonster(player, add, level, MonsterRarity.Normal, spot.X, spot.Y,
                    unrisen: false, alone: false, log: false);
                if (addId != 0)
                {
                    spawnedAny = true;
                    if (player.Area.Monsters.TryGetValue(addId, out var m)) m.Aggro = true;
                }
            }
        }
        if (spawnedAny) await SendRemainingMonsters(player);
    }
}
