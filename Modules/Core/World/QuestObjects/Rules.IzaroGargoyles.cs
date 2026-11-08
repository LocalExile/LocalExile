using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // A gargoyle of Izaro's (IzaroGargoyles): the arena's own, on a small elevator, or one an earlier trial left (Carried:
    // the effect it was left as), in an alcove behind Railing. It takes no hits and does not move until it awakens (or
    // comes out of the alcove); its buff is on Izaro while Buff is set.
    sealed class GargoyleSlot(IzaroEffects.Effect effect, uint id, SpawnedMonster monster, byte transitionable, double awakenSeconds)
    {
        public readonly IzaroEffects.Effect Effect = effect;
        public readonly uint Id = id;
        public readonly SpawnedMonster Monster = monster;
        public readonly byte Transitionable = transitionable;
        public readonly double AwakenSeconds = awakenSeconds;
        public (uint Id, AreaPopulation.RoomObject At)? Lift;
        public string? Carried;
        public uint? Railing;
        public ushort Buff;
    }

    // IzaroGargoyle.ot's Transitionable: 2 raising, 3 raised. Its on_transition_to_2 is the server's: the server plays
    // ascending_elevator (rig.amd clip 0, 10.7 s) with 0x1b2, or the client leaves it down in its hole (live 2026-11-08).
    const byte GargoyleRaising = 2, GargoyleRaised = 3;
    const uint AscendingClip = 0;

    // A statue gargoyle's damage taken (ours: "little", live).
    static readonly IReadOnlyDictionary<string, int> Dormant = new Dictionary<string, int> { ["base_damage_taken_+%"] = -90 };

    static bool GargoyleDead(ClientState conn, GargoyleSlot g) =>
        g.Monster.Dead || g.Monster.Life == 0 || !ReferenceEquals(conn.Monsters.GetValueOrDefault(g.Id), g.Monster);

    // The arena's gargoyles, one on each small elevator, down in its hole (a new one is in IzaroGargoyle.ot's state 5).
    static async Task PlaceIzaroGargoyles(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena)
    {
        var gargoyles = IzaroEffects.Of(IzaroGargoyles.Mechanic, arena.Trial.Tier, returning: false);
        for (int i = 0; i < Math.Min(gargoyles.Count, arena.Lifts.Count); i++)
            if (await SpawnIzaroGargoyle(remote, stream, ciphers, conn, gargoyles[i], arena.Lifts[i].At, null, null) is { } slot)
            {
                slot.Lift = arena.Lifts[i];
                arena.Gargoyles.Add(slot);
            }
    }

    // A gargoyle as a statue: out of the AI and taking little damage (the original, live: little until its beam starts,
    // all of it after; how little is ours), never stunned; one an earlier trial left takes none until it comes out (it is
    // behind its railing). Not kept with the instance (the arena places it again).
    static async Task<GargoyleSlot?> SpawnIzaroGargoyle(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IzaroEffects.Effect effect, AreaPopulation.RoomObject at, string? carried, uint? railing)
    {
        if (Monsters.Find(effect.Variety) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {remote} Izaro's gargoyle {effect.Id}: {effect.Variety} cannot be created");
            return null;
        }
        byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y, rotation: at.Angle,
            placement: MonsterPlacement.Established);
        lock (conn.Monsters)
        {
            if (conn.Monsters.GetValueOrDefault(id) is not { } m) return null;
            (m.BusyUntil, m.Aggro, m.EntranceGrace, m.SummonerId) = (double.PositiveInfinity, false, carried is not null, 0);
            m.StunImmuneUntil = double.PositiveInfinity;
            m.EncounterStats = Dormant;
            return new GargoyleSlot(effect, id, m, (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable"), Emerges.Seconds(variety))
            {
                Carried = carried, Railing = railing,
            };
        }
    }

    // A gargoyle wakes: its buff goes on Izaro and, once its rise has played (seconds), it fights.
    static async Task WakeIzaroGargoyle(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        GargoyleSlot g, double seconds, string what)
    {
        ushort buff;
        lock (conn.Monsters)
        {
            if (GargoyleDead(conn, g)) return;
            g.Buff = buff = conn.NextBuff++;
            (g.Monster.BusyUntil, g.Monster.Aggro, g.Monster.EntranceGrace) = (conn.Clock.Elapsed.TotalSeconds + seconds, true, false);
            (g.Monster.EncounterStats, g.Monster.StunImmuneUntil) = (null, double.NegativeInfinity);
            UpdateIzaroStats(arena);
        }
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(arena.Izaro, buff, g.Effect.Buff, g.Id),
            $"0x01c1 Izaro 0x{arena.Izaro:x} has {g.Effect.Id}: gargoyle 0x{g.Id:x} {what}");
    }

    // The earlier trials' gargoyles: their alcoves' railings lower, their buffs go on Izaro and they come out.
    static async Task ReleaseIzaroGargoyles(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        var carried = arena.Gargoyles.Where(g => g.Carried is not null).ToList();
        foreach (uint railing in carried.Select(g => g.Railing).OfType<uint>().Distinct())
        {
            await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(railing, IzaroFights.TransitionableIndex, IzaroFights.RailingDown),
                .. InstanceEntities.BlockageUpdate(railing, false)], $"0x02ba alcove railing 0x{railing:x} lowers");
            OpenIzaroDoor(conn, railing);
        }
        foreach (var g in carried)
            await WakeIzaroGargoyle(remote, stream, ciphers, conn, arena, g, IzaroFights.RailingSeconds, "comes out of the alcove");
    }

    // The arena's gargoyles rise together with Izaro, stand, then one at a time a beam runs from it to Izaro (the Beam's
    // end follows its object, as the Soul Eater's line follows the player; the original's beam stays on him, live) and it
    // wakes.
    static async Task RaiseIzaroGargoyles(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            var own = arena.Gargoyles.Where(g => g.Lift is not null).ToList();
            List<byte> bytes = [];
            lock (conn.Monsters)
            {
                if (arena.Over || !here()) return;
                foreach (var g in own)
                    bytes.AddRange([.. LiftState(conn, g.Lift!.Value.Id, g.Lift.Value.At, IzaroFights.Raising),
                        .. InstanceEntities.TransitionableUpdate(g.Id, g.Transitionable, GargoyleRaising),
                        .. InstanceProtocol.AnimationUpdate(g.Id, 0, AscendingClip, 1)]);
            }
            await Send(remote, stream, ciphers, [.. bytes], $"0x02ba Izaro's {own.Count} gargoyles rise");
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.RiseSeconds));
            bytes = [];
            lock (conn.Monsters)
            {
                if (!here()) return;
                foreach (var g in own)
                    bytes.AddRange([.. LiftState(conn, g.Lift!.Value.Id, g.Lift.Value.At, IzaroFights.Raised),
                        .. InstanceEntities.TransitionableUpdate(g.Id, g.Transitionable, GargoyleRaised)]);
            }
            await Send(remote, stream, ciphers, [.. bytes], "0x02ba Izaro's gargoyles up");
            await Task.Delay(TimeSpan.FromSeconds(IzaroGargoyles.StandSeconds));
            foreach (var g in own.OrderBy(_ => Random.Shared.Next()))
            {
                uint beam;
                ushort awaken = 0;
                (int X, int Y) from, to;
                lock (conn.Monsters)
                {
                    if (arena.Over || !here()) return;
                    if (GargoyleDead(conn, g)) continue;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    from = g.Monster.Walk.Position(now);
                    to = arena.Boss!.Walk.Position(now);
                    g.Monster.EncounterStats = null;
                    beam = conn.NextObjectId++;
                    if (IzaroGargoyles.AwakenBuff is not null) awaken = conn.NextBuff++;
                }
                List<byte> start = [.. InstanceEntities.CreateObject(beam, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath),
                    new BeamEffectObject(from.X, from.Y, g.Id, to.X * BeamEffectObject.WorldPerCell, to.Y * BeamEffectObject.WorldPerCell,
                        arena.Izaro, IzaroGargoyles.BeamRow, IzaroGargoyles.MouthBone).Blob()), .. InstanceProtocol.EnterWakeZone(beam)];
                if (IzaroGargoyles.AwakenBuff is ushort row) start.AddRange(InstanceEntities.AddBuff(g.Id, awaken, row, arena.Izaro));
                await Send(remote, stream, ciphers, [.. start], $"0x02b9 beam 0x{beam:x} from gargoyle 0x{g.Id:x} ({g.Effect.Id}) to Izaro");
                // The beam runs its time, or until the gargoyle dies or Izaro leaves (live: it goes with the gargoyle).
                for (double waited = 0; waited < IzaroGargoyles.BeamSeconds; waited += 0.2)
                {
                    await Task.Delay(200);
                    lock (conn.Monsters) if (!here() || arena.Over || GargoyleDead(conn, g)) break;
                }
                if (!here()) return;
                List<byte> end = [.. InstanceProtocol.RemoveObject(beam)];
                if (awaken != 0) end.AddRange(InstanceEntities.RemoveBuff(g.Id, awaken));
                bool over, dead;
                lock (conn.Monsters) (over, dead) = (arena.Over, GargoyleDead(conn, g));
                if (!over && !dead && Emerges.SkillId is ushort emerge)
                    end.AddRange([.. InstanceProtocol.ActionStarted(g.Id, from.X, from.Y, from.X, from.Y, emerge, 0xffff, conn.MonsterOrderFlags),
                        .. InstanceProtocol.ActionEnded(g.Id)]);
                await Send(remote, stream, ciphers, [.. end], $"0x02bb beam 0x{beam:x} ends{(over || dead ? "" : $", gargoyle 0x{g.Id:x} awakens")}");
                if (over) return;
                if (!dead) await WakeIzaroGargoyle(remote, stream, ciphers, conn, arena, g, g.AwakenSeconds, "awakens");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's gargoyles stopped: {ServerLog.Describe(ex)}"); }
    }

    // While he fights: a gargoyle killed takes its buff off Izaro; when the last of the arena's own dies he says so.
    static async Task WatchIzaroGargoyles(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            while (here())
            {
                List<(GargoyleSlot G, ushort Buff)> gone = [];
                bool allDead = false;
                lock (conn.Monsters)
                {
                    if (arena.Over) return;
                    foreach (var g in arena.Gargoyles.Where(g => g.Buff != 0 && GargoyleDead(conn, g)))
                    {
                        gone.Add((g, g.Buff));
                        g.Buff = 0;
                    }
                    if (gone.Count > 0) UpdateIzaroStats(arena);
                    var own = arena.Gargoyles.Where(g => g.Carried is null).ToList();
                    if (own.Count > 0 && !arena.AllGargoylesDeadSaid && own.All(g => GargoyleDead(conn, g)))
                        allDead = arena.AllGargoylesDeadSaid = true;
                }
                foreach (var (g, buff) in gone)
                    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(arena.Izaro, buff),
                        $"0x01c2 Izaro 0x{arena.Izaro:x} loses {g.Effect.Id}: gargoyle 0x{g.Id:x} is dead");
                if (allDead) await IzaroSays(remote, stream, ciphers, arena.Izaro, IzaroGargoyles.AllDeadLine);
                await Task.Delay(200);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's gargoyles' watch stopped: {ServerLog.Describe(ex)}"); }
    }
}
