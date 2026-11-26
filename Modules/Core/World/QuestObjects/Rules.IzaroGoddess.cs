using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // The Goddess of Justice (IzaroGoddess) on Izaro's back, from his rise: out of the AI and of every hit for good, no bar,
    // no rewards. Not kept with the instance (the arena places her with him).
    static async Task SpawnIzaroGoddess(Player player, IzaroArena arena,
        uint izaro, byte level, (int X, int Y) at)
    {
        if (Monsters.Find(IzaroGoddess.Variety) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} the Goddess of Justice ({IzaroGoddess.Variety}) cannot be created");
            return;
        }
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Unique, at.X, at.Y,
            placement: MonsterPlacement.Established, extraStats: [(Stat.HideMiniLifeBar, 1)], attachTo: (izaro, IzaroGoddess.Bone, IzaroGoddess.AttachKind, IzaroGoddess.AttachFlags));
        if (player.Area.Monsters.GetValueOrDefault(id) is not { } goddess) return;
        (goddess.BusyUntil, goddess.Aggro, goddess.EntranceGrace, goddess.NoRewards, goddess.ScriptHeld) = (double.PositiveInfinity, false, true, true, true);
        (arena.Goddess, arena.GoddessMonster) = (id, goddess);
    }

    // She rides up on him (the original, live): her .ot creates her held at her emerge clip's start, unseen until an action
    // plays (live: she showed with her first volley), so her Emerge (her Actor's basic action) starts with his.
    static byte[] IzaroGoddessEmerges(IzaroArena arena, int x, int y, Player player) =>
        arena.Goddess != 0 && Emerges.SkillId is ushort emerge
            ? [.. InstanceProtocol.ActionStarted(arena.Goddess, x, y, x, y, emerge, 0xffff, player.MonsterOrderFlags), .. InstanceProtocol.ActionEnded(arena.Goddess)]
            : [];

    // Izaro has died: she turns slowly and flies up (her death clip, live), and is gone after it.
    static async Task IzaroGoddessFliesUp(Player player, IzaroArena arena)
    {
        if (arena.GoddessMonster is not { } goddess) return;
        uint id = arena.Goddess;
        var layout = player.Layout;
        await Send(player, [.. InstanceProtocol.ActionEnded(id), .. InstanceProtocol.AnimationUpdate(id, 0, IzaroGoddess.DeathClip, 1)],
            $"0x02bc the Goddess of Justice 0x{id:x} flies up");
        await AreaDelay(player, IzaroGoddess.DeathSeconds);
        // She leaves the instance's monsters also when the player has gone, or a later entry would find her still here.
        byte[] gone = IzaroGoddessGone(player, arena);
        if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Layout, layout)) return;
        await Send(player, gone, $"0x02bd the Goddess of Justice 0x{id:x} is gone");
    }

    // Her removal, with Izaro's when he leaves (she rides down on him).
    static byte[] IzaroGoddessGone(Player player, IzaroArena arena)
    {
        if (arena.GoddessMonster is not { } goddess) return [];
        uint id = arena.Goddess;
        goddess.Dead = true;
        if (ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), goddess)) player.Area.Monsters.Remove(id);
        (arena.Goddess, arena.GoddessMonster) = (0, null);
        return InstanceProtocol.RemoveObject(id);
    }

    // The Goddess's Guardians (IzaroGoddess): from when Izaro is hurt until the trial ends, her mortars open circles at random
    // places of the arena, each with its guardians, which die by themselves.
    static async Task RunIzaroGuardians(Player player, IzaroArena arena,
        Func<bool> here)
    {
        if (Monsters.Find(IzaroGoddess.Guardian) is not { Supported: true } guardian)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} the Goddess's guardian {IzaroGoddess.Guardian} cannot be created");
            return;
        }
        var layout = player.Layout;
        try
        {
            while (true)
            {
                if (arena.Over || !here() || arena.Boss is not { } boss) return;
                if (boss.Life * 100L <= boss.MaxLife * (long)IzaroGoddess.HurtPercent) break;
                await AreaDelay(player, 0.5);
            }
            while (true)
            {
                List<(int X, int Y)> spots;
                if (arena.Over || !here()) return;
                spots = IzaroGuardianSpots(player, arena);
                // Her mortars fly first, each circle opens where one lands.
                double[] flights = arena.GoddessMonster is { } goddess
                    ? await GoddessOfJustice.ThrowGuardians(player, arena.Goddess, goddess, spots) : new double[spots.Count];
                for (int i = 0; i < spots.Count; i++) _ = OpenIzaroCircle(player, arena, guardian, spots[i], flights[i], layout);
                Console.WriteLine($"[gateway] {player.Connection.Remote} the Goddess opens {spots.Count} circle(s): {string.Join(", ", spots)}");
                await AreaDelay(player, IzaroGoddess.EverySeconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} the Goddess's guardians stopped: {ServerLog.Describe(ex)}"); }
    }

    // Random floor of the arena out of the skill's minimum range of Izaro, none on another. In the area's frame.
    static List<(int X, int Y)> IzaroGuardianSpots(Player player, IzaroArena arena)
    {
        var spots = new List<(int X, int Y)>();
        if (arena.Centre is not { } centre || arena.Boss is not { } boss) return spots;
        var from = boss.Walk.Position(AreaTime(player));
        for (int tries = 0; tries < 200 && spots.Count < IzaroGoddess.Circles; tries++)
        {
            (int X, int Y) at = (centre.X + AreaRng(player).Next(-IzaroGoddess.ArenaReach, IzaroGoddess.ArenaReach + 1),
                centre.Y + AreaRng(player).Next(-IzaroGoddess.ArenaReach, IzaroGoddess.ArenaReach + 1));
            if (!player.Layout.WalkableFor(at.X, at.Y, 2) || player.Barrier.Contains(at)) continue;
            if (Math.Sqrt((double)(at.X - from.X) * (at.X - from.X) + (double)(at.Y - from.Y) * (at.Y - from.Y)) < IzaroGoddess.MinimumRange) continue;
            if (spots.Any(s => Math.Abs(s.X - at.X) + Math.Abs(s.Y - at.Y) < 20)) continue;
            spots.Add(at);
        }
        return spots;
    }

    // A circle where her mortar lands, its guardians in it.
    static async Task OpenIzaroCircle(Player player, IzaroArena arena,
        MonsterCatalog.Variety guardian, (int X, int Y) spot, double after, AreaLayout layout)
    {
        try
        {
            if (after > 0) await AreaDelay(player, after);
            if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Layout, layout)) return;
            uint effect = player.TakeObjectId();
            await Send(player, [.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(spot.X, spot.Y, IzaroGoddess.Impact).Blob()), .. InstanceProtocol.EnterWakeZone(effect)], null);
            _ = RemoveIzaroCircle(player, layout, effect);
            for (int k = 0; k < IzaroGoddess.GuardiansInACircle; k++)
            {
                uint id = await SpawnMonster(player, guardian, (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 1, 100),
                    MonsterRarity.Normal, spot.X, spot.Y, log: false, owned: (_, s) => (s.SummonerId, s.NoRewards) = (arena.Izaro, true));
                if (id != 0) _ = ExpireIzaroGuardian(player, layout, id);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} the Goddess's circle stopped: {ServerLog.Describe(ex)}"); }
    }

    static async Task RemoveIzaroCircle(Player player, AreaLayout layout, uint effect)
    {
        await AreaDelay(player, IzaroGoddess.ImpactSeconds);
        if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Layout, layout)) return;
        try { await Send(player, InstanceProtocol.RemoveObject(effect), null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // IzaroSkeleton.ot's DiesAfterTime, outside its client block, so the server's: it goes when its time is up, and so does
    // the corpse of one killed before that (the user's call: they would pile up in the arena otherwise).
    static async Task ExpireIzaroGuardian(Player player, AreaLayout layout, uint id)
    {
        await AreaDelay(player, IzaroGoddess.GuardianSeconds);
        if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Layout, layout)) return;
        if (player.Area.Monsters.GetValueOrDefault(id) is not { } m) return;
        (m.Life, m.Dead) = (0, true);
        player.Area.Monsters.Remove(id);
        try { await Send(player, InstanceProtocol.RemoveObject(id), null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
