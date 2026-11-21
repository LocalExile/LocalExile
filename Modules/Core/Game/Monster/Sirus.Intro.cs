using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

static partial class Sirus
{
    const string Kirac = "Metadata/Monsters/Masters/KiracOrionThroneRoom", Zana = "Metadata/Monsters/Masters/ZanaOrionThroneRoom";
    internal sealed partial class Fight
    {
        uint introSirus, kirac, zana;
        async Task MoveAlly(Player c, uint actor, (int X, int Y) destination, double now)
        {
            if (actor == 0 || !c.Area.Monsters.TryGetValue(actor, out var monster)) return;
            var from = monster.Walk.Position(now);
            var to = c.Layout.Near(destination);
            monster.Walk.Order(to.X, to.Y, now);
            await State(c, actor, ("weary", 1), ("run", 1));
            await All(actor, InstanceProtocol.ActionStarted(actor, from.X, from.Y, to.X, to.Y, 0x2909,
                c.NextActionSequence(), 0x0408), true);
        }
        static readonly (double At, ushort Line, ushort Speaker)[] IntroLines =
        [
            (0, 5643, 407), (7, 5381, 416), (11, 5644, 407), (14, 5645, 407), (18, 5646, 407),
            (20, 5382, 416), (22, 5383, 416), (24, 5647, 407), (28, 5649, 407), (40, 5650, 407), (53, 5384, 416),
        ];
        async Task<uint> IntroAlly(Player c, string type, double now)
        {
            foreach (var (existingId, present) in c.Area.Monsters)
                if (present.Variety?.Id == type && !present.Dead)
                {
                    return existingId;
                }
            var point = c.Layout.Level?.RoomObjects.FirstOrDefault(p => p.Type == type);
            if (point is null || Monsters.Find(type) is not { Supported: true } v) return 0;
            uint key = await SpawnMonster(c, v, 84, MonsterRarity.Normal,
                point.X, point.Y, friendly: true, rotation: point.Rotation, scripted: true,
                owned: (_, m) => { m.NoRewards = true; m.EntranceGrace = true; });
            if (c.MinionOrigins.TryRemove(key, out var origin)) c.Instance.World.MonsterOrigins[key] = origin;
            return key;
        }
        async Task Intro(Player c, Player target, double now)
        {
            if (scene.Trigger is not { } trigger)
            {
                c.Instance.SetState(Flag("IntroDone")); stage = 2; return;
            }
            if (stage == 0)
            {
                if (Distance(target.Walk.Position(now), trigger) > 170) return;
                introAt = now; stage = 1; introBeat = 0;
                introSirus = await Fx(c, (trigger.X + 15, trigger.Y), Root + "AtlasExile5.ao", now + 58, clip: 0, rotation: -MathF.PI / 2, temporary: false);
                kirac = await IntroAlly(c, Kirac, now); zana = await IntroAlly(c, Zana, now);
                await MoveAlly(c, kirac, (trigger.X - 30, trigger.Y + 8), now);
                await MoveAlly(c, zana, (trigger.X - 40, trigger.Y - 8), now);
                Console.WriteLine("[Sirus] laboratory confrontation begins");
            }
            while (introBeat < IntroLines.Length && now - introAt >= IntroLines[introBeat].At)
            {
                var line = IntroLines[introBeat++]; await Voice(line.Line, line.Speaker);
                if (line.Line == 5646)
                {
                    await All(introSirus, InstanceProtocol.AnimationUpdate(introSirus, 0, 1, 1));
                    await Fx(c, (trigger.X + 15, trigger.Y), Art + "Kirac_beam.ao", now + 2, clip: null, rotation: -MathF.PI / 2, temporary: false);
                    await State(c, kirac, ("run", 0), ("dive", 1));
                    await State(c, zana, ("run", 0), ("react", 1));
                }
                if (line.Line == 5383)
                {
                    await State(c, kirac, ("injured", 1));
                    await State(c, zana, ("healing", 1));
                }
                if (line.Line == 5650)
                    await All(introSirus, InstanceProtocol.AnimationUpdate(introSirus, 0, 2, 1));
            }
            if (now - introAt < 58) return;
            c.Instance.SetState(Flag("IntroDone")); stage = 2;
            await Remove(introSirus);
            foreach (var p in Present)
            {
                await UpdateShownTransitions(p, "Sirus fled to Oriath");
            }
            Console.WriteLine("[Sirus] six laboratory portals ready");
        }
    }
}
