using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using static Game;
using static Program;

static partial class Sirus
{
    const string PortalModel = "Metadata/Effects/Spells/portal/unstable_RED/AtlasExile5Portal.ao";
    const string IntroPortal = "Sirus.IntroPortal";
    static string Flag(string name) => Quests.InstanceFlag("Sirus-" + name);
    static readonly ConditionalWeakTable<InstanceState, Scene> Scenes = new();
    sealed class Scene(ClientState c)
    {
        public readonly (int X, int Y)? Entry = EntryPoint(c);
        static (int X, int Y)? EntryPoint(ClientState c)
        {
            var trigger = c.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == "Metadata/NPC/Epilogue/AtlasExile5NPCTrigger");
            if (trigger is null) return null;
            // The population index keeps boss rooms; this quiet entrance room need not be in it.
            var points = c.Layout.Level!.Records.SelectMany(r => r.Rooms).SelectMany(room =>
                room.Room.Points.Where(p => p.Name == "default").Select(p =>
                {
                    var (x, y) = Orientation.Map(room.Orientation, (int)p.X, (int)p.Y, room.Room.Width * 23, room.Room.Height * 23);
                    return (X: room.X * 23 + x, Y: room.Y * 23 + y);
                })).ToArray();
            return points.Length == 0 ? null : points.MinBy(p => Distance(p, (trigger.X, trigger.Y)));
        }
        public readonly (int X, int Y) Centre = ArenaCentre(c);
        public readonly (int X, int Y)? Trigger = c.Layout.Level?.RoomObjects.FirstOrDefault(o =>
            o.Type == "Metadata/NPC/Epilogue/AtlasExile5NPCTrigger") is { } o ? (o.X, o.Y) : null;
        static (int X, int Y) ArenaCentre(ClientState c)
        {
            if (c.LaterFormPoints.TryGetValue(Body, out var supplied)) return supplied;
            var point = AreaPopulation.LoadPoints(Area, c.Seed, []).FirstOrDefault(p => p.Name == "mapboss1");
            return point is null ? (c.Layout.SpawnX + 155, c.Layout.SpawnY + 6) : (point.X, point.Y);
        }
    }
    static Scene SceneOf(ClientState c) => Scenes.GetValue(c.Instance, _ => new(c));

    internal static bool? GateShown(ClientState c, AreaTransitionObject t) =>
        c.Layout.Id == Area && t.SourceType == IntroPortal ? c.Instance.HasState(Flag("IntroDone")) : null;

    internal static void ConfigureArrival(ClientState c)
    {
        if (c.Layout.Id != Area) return;
        var scene = SceneOf(c);
        if (c.Layout.Level is not { } level || scene.Trigger is not { } trigger) return;
        var portals = level.RoomObjects.Where(o => o.Type.StartsWith(Root + "ArenaMechanics/AtlasExile5Portal", StringComparison.Ordinal)
            && char.IsDigit(o.Type[^1])).ToArray();
        var transitions = c.Layout.AreaTransitions.Where(t => t.Id < 0x2f00 || t.Id > 0x2f06).ToList();
        if (portals.Length == 6)
        {
            for (int i = 0; i < portals.Length; i++)
            {
                var destination = level.RoomObjects.FirstOrDefault(o => o.Type == portals[i].Type + "Destination");
                if (destination is null) continue;
                transitions.Add((0x2f00u + (uint)i, new AreaTransitionObject(portals[i].X, portals[i].Y, c.Layout.Hash, PortalModel)
                {
                    SourceType = IntroPortal, Kind = 1, TeleportTo = c.Layout.Near((destination.X, destination.Y)),
                    Label = 272607248, // ClientStrings.TemplarLabAreaName.
                }));
            }
        }
        var entry = scene.Entry;
        c.Use(c.Layout with
        {
            SpawnX = c.ArriveAt is null && entry is not null ? entry.Value.X : c.Layout.SpawnX,
            SpawnY = c.ArriveAt is null && entry is not null ? entry.Value.Y : c.Layout.SpawnY,
            AreaTransitions = [.. transitions],
        });
        Console.WriteLine($"[Sirus] laboratory arrival {scene.Entry}; {transitions.Count - c.Layout.AreaTransitions.Count(t => t.Id < 0x2f00 || t.Id > 0x2f06)} internal portals");
    }

    internal static async Task Populate(ClientState c)
    {
        var at = SceneOf(c).Centre;
        if (Monsters.Find(Throne) is not { Supported: true } v) return;
        await SpawnMonster(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, v, 84, MonsterRarity.Unique, at.X, at.Y,
            rotation: 3 * MathF.PI / 2, scripted: true, owned: (_, m) => { m.NoRewards = true; m.EntranceGrace = true; });
    }
}
