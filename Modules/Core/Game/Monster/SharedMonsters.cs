using GameServer.Core.Protocol.Instance;

// The area's monsters are its own (InstanceWorld): every connection in it reads and hits the same objects on the area's
// clock, and every client is written the same records about them (AreaDelta). The area steps them once a frame (AI,
// collisions) through its view: the first connection of its frames still in it (AreaFrames.View). A client that comes
// later is given them where they are, with the life they have (JoinArea); whoever leaves takes nothing with them.
public static partial class Game
{
    // The instance's monster ids start here (InstanceWorld.NewMonsterId); below it are each client's own objects and the
    // players' minions.
    const uint SharedMonsterIds = 0x08000000;

    // Whether this connection's loops step the area's monsters now: outside any instance (a test's) its own; in an instance,
    // the area's view (the first connection of its frames still here). The walks are built for the view that steps them
    // (InstanceWorld.Runner): a new view rebuilds them with its collision. Without frames (before they start, in tests) the
    // view the walks were last built for steps them.
    public static bool IsMonsterRunner(Player player)
    {
        if (player.Instance is not { } instance) return true;
        var world = instance.World;
        if (player.Frames.View is { } view)
        {
            if (!ReferenceEquals(world.Runner, view))
                if (!ReferenceEquals(world.Runner, view)) StepForViewLocked(view, world);
            return ReferenceEquals(view, player);
        }
        return world.Runner is null || ReferenceEquals(world.Runner, player);
    }

    // Until the area's frames name a view, a connection that comes while no live one has the walks takes them.
    static void FirstViewLocked(Player player, InstanceWorld world)
    {
        if (player.Frames.View is null && !(world.Runner is { } r && !r.Closed && !r.LeavingArea && InGame.ContainsKey(r)))
            StepForViewLocked(player, world);
    }

    // At PopulateArea: this connection runs the monsters when nobody live does; true when the area's packs are still to
    // be placed (the first connection in, or one after a restart, from the instance's save).
    public static bool ClaimMonsterHost(Player player)
    {
        var world = player.Instance.World;
        FirstViewLocked(player, world);
        if (!world.Populated)
        {
            world.Populated = true;
            return true;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} monsters: the instance's own ({world.MonsterOrigins.Count}), run by {CharacterName(world.Runner!)}");
        return false;
    }

    // Under the instance's lock: the area's view changed, and this connection's loops step the monsters from now on (their
    // ailments and collisions). Their walks get its collision: the last view of the bodies went with it.
    static void StepForViewLocked(Player player, InstanceWorld world)
    {
        string remote = player.Connection.Remote;
        var old = world.Runner;
        world.Runner = player;
        if (old is null || ReferenceEquals(old, player)) return;
        double now = AreaTime(player);
        int count = 0;
        foreach (var (id, m) in world.Area.Monsters.Concat(world.Area.DormantMonsters).OrderBy(e => e.Key))
        {
            if (!world.MonsterOrigins.ContainsKey(id)) continue;
            var at = m.Walk.Position(now);
            m.Walk = player.NewMonsterWalk(id, at.X, at.Y, m.Walk.Speed, Math.Clamp((m.Variety?.ObjectSize ?? 1) - 1, -1, 127),
                m.Rarity == MonsterRarity.Unique);
            (m.Aggro, m.Leg, m.LastTarget) = (false, null, null);
            count++;
        }
        Console.WriteLine($"[gateway] {remote} monsters: the area steps its {count} through {CharacterName(player)} now ({CharacterName(old)} left)");
    }

    // Under the instance's lock, in this connection's AI pass: whether it acts for the monster now. A monster with no
    // live target takes this player when in its reach; another player's takes this one only when nearer by
    // TargetMargin and in its reach, so it does not flip between two standing side by side. A dead, gone or other-area
    // target leaves it to whoever is near.
    public static bool MonsterTargetsLocked(Player player, uint id, SpawnedMonster m, (int X, int Y) at, double d, double now)
    {
        (double Distance, bool Taunted) TargetPriority(Player owner, double distance)
        {
            if (m.Attack is null || InTown(owner) || InGracePeriod(owner)) return (distance, false);
            var minion = GolemTargetLocked(owner, id, m, at, now, distance);
            return minion.G is null ? (distance, false)
                : (Math.Min(distance, minion.Distance), minion.G.Taunts.GetValueOrDefault(id) > now);
        }
        if (DecoyTarget(m, now) is { } decoy)
        {
            if (!ReferenceEquals(player, decoy.Owner)) return false;
            if (!ReferenceEquals(m.Target, player))
            {
                (m.LastTarget, m.SwingGeneration) = (null, null);
                m.ActionGeneration++;
                if (double.IsPositiveInfinity(m.BusyUntil)) m.BusyUntil = now;
                m.Target = player;
            }
            return true;
        }
        var candidate = TargetPriority(player, d);
        var t = m.Target;
        bool live = TargetLive(t, player);
        if (live && ReferenceEquals(t, player))
        {
            if (!m.Aggro && candidate.Distance > m.AggroRange) m.Target = null; // gave up: free for whoever comes
            return true;
        }
        // In its reach, or still chasing (the AI gives up beyond twice its aggro range, as before).
        double reach = Math.Max(1, m.AggroRange) * (m.Aggro && !live ? 2 : 1);
        if (player.PlayerLife == 0 || candidate.Distance > reach) return false;
        if (live)
        {
            var there = t!.Walk.Position(now);
            double dt = Math.Sqrt((double)(at.X - there.X) * (at.X - there.X) + (double)(at.Y - there.Y) * (at.Y - there.Y));
            var incumbent = TargetPriority(t, dt);
            if (incumbent.Taunted || (!candidate.Taunted && candidate.Distance + TargetMargin >= incumbent.Distance)) return false;
        }
        // From another player: what it did for that one ends (its next pass orders after this one).
        if (t is not null)
        {
            (m.LastTarget, m.SwingGeneration) = (null, null);
            m.ActionGeneration++;
            if (double.IsPositiveInfinity(m.BusyUntil)) m.BusyUntil = now;
        }
        m.Target = player;
        return true;
    }

    const double TargetMargin = 15;

    // Whether a monster's target still counts: a live player of this connection's instance, in the area.
    static bool TargetLive(Player? t, Player player) =>
        t is not null && !t.Closed && !t.LeavingArea && t.PlayerLife > 0 && ReferenceEquals(t.Instance, player.Instance);

    // At disconnect: this player's minions leave the instance with them.
    public static void RemoveLeavingMinionsLocked(Player left)
    {
        foreach (uint id in left.MinionOrigins.Keys) left.Instance.World.Area.Monsters.Remove(id);
    }


    // The area's wake zone: a monster is awake while any player in the area is near it (WakeZoneRadius, back within
    // WakeZoneReturn), and every client is told when that changes (S->C 0x1a9 / 0x1aa). Asleep, its walk stands and the
    // AI gives it no order. Players far apart each keep their own surroundings awake.
    const int WakeZoneRadius = 171, WakeZoneReturn = 163;

    internal static void StepAreaWake(Player view)
    {
        var area = view.Area;
        double now = AreaTime(view);
        var players = view.Frames.Players().Where(p => !p.Closed && !p.LeavingArea && p.Connection.PlayerSent)
            .Select(p => p.Walk.Position(now)).ToArray();
        if (players.Length == 0) return;
        foreach (var (id, m) in area.Monsters)
        {
            if (m.Dead || m.Life <= 0 || m.OwnerId is not null || m.WorldHidden) continue;
            var at = m.Walk.Position(now);
            double nearest = double.MaxValue;
            foreach (var p in players) nearest = Math.Min(nearest, Dist(at, p));
            if (!m.Asleep && nearest > WakeZoneRadius)
            {
                m.Asleep = true;
                m.Walk.StopAt(now);
                m.Leg = null;
                m.ClientChase = null;
                m.LastTarget = null;
                m.SwingGeneration = null;
                m.ActionGeneration++;
                area.Delta.Add(id, InstanceProtocol.LeaveWakeZone(id), $"0x01aa monster 0x{id:x} leaves the area's wake zone");
            }
            else if (m.Asleep && nearest <= WakeZoneReturn)
            {
                m.Asleep = false;
                area.Delta.Add(id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 monster 0x{id:x} enters the area's wake zone");
                // Back where the server has it (its walk stood while it slept).
                area.Delta.Add(id, InstanceProtocol.Teleport(id, at.X, at.Y));
                if (Arakaali.StanceUpdateLocked(view, id, now) is { } stance) area.Delta.Add(id, stance);
            }
        }
    }

    /// <summary>Whether a spot is out of every player's wake zone (a monster created there is created asleep).</summary>
    static bool FarFromPlayers(Player player, (int X, int Y) at)
    {
        double now = AreaTime(player);
        foreach (var p in player.Frames.Players())
            if (!p.Closed && !p.LeavingArea && Dist(at, p.Walk.Position(now)) <= WakeZoneRadius) return false;
        return Dist(at, player.Walk.Position(now)) > WakeZoneRadius;
    }

    /// <summary>Whether the monster is in the area: then every client has it (its create went to all of them).</summary>
    public static bool MonsterShown(Player player, uint id) => player.Area.Monsters.ContainsKey(id) || player.Area.DormantMonsters.ContainsKey(id);

    /// <summary>Whether the monster is out of every player's wake zone (StepAreaWake).</summary>
    public static bool MonsterAsleep(Player player, uint id) => player.Area.Monsters.GetValueOrDefault(id)?.Asleep == true;

    // What the area has shown every client once: bosses' life bars and scene objects (by object id).
    sealed class AreaShown
    {
        public readonly HashSet<uint> BossBars = [], Scenes = [], Stances = [];
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameArea, AreaShown> Shown = new();
    /// <summary>The bosses whose life bar the area's clients were shown.</summary>
    public static HashSet<uint> BossBars(Player player) => Shown.GetOrCreateValue(player.Area).BossBars;
    /// <summary>The bosses whose second stance the area's clients were shown (Arakaali).</summary>
    public static HashSet<uint> StancesShown(Player player) => Shown.GetOrCreateValue(player.Area).Stances;
    /// <summary>The scene objects the area's clients were given.</summary>
    public static HashSet<uint> Scenes(Player player) => Shown.GetOrCreateValue(player.Area).Scenes;

    // The Gateway's join hand-over is the loaded rules': set when they load and taken back when they unload.
    static readonly Action<Player> Join = JoinArea;
    public static IDisposable InstallJoining()
    {
        Volatile.Write(ref Program.Joining, Join);
        Volatile.Write(ref AreaLife.ReservedMana, ReservedManaOf);
        Volatile.Write(ref AreaStats.Written, RelayWalk);
        return new JoiningRelease();
    }
    static readonly Func<Player, uint> ReservedManaOf = p => (uint)ReservedMana(p);
    static readonly Action<GameArea, uint, IReadOnlyList<(uint Key, int Value)>> RelayWalk = RelayWalkForStats;
    sealed class JoiningRelease : IDisposable
    {
        public void Dispose()
        {
            Interlocked.CompareExchange(ref Program.Joining, null, Join);
            Interlocked.CompareExchange(ref AreaLife.ReservedMana, null, ReservedManaOf);
            Interlocked.CompareExchange(ref AreaStats.Written, null, RelayWalk);
        }
    }

    // A connection's first frame in the area (Program.Joining): what its client is missing of the area goes in the
    // area's frame for it alone (the area's monster types, its monsters and minions where they are, the other players),
    // and its player for everyone else.
    static void JoinArea(Player player)
    {
        if (!CurrentGameWork || player.Instance is null || player.Closed || player.LeavingArea) return;
        var area = player.Area;
        var world = player.Instance.World;
        double now = AreaTime(player);
        if (area.MonsterTypes.Count > 0)
            area.Delta.Add(0, InstanceEntities.MonsterTypes([.. area.MonsterTypes.Select(t => ((ushort)t.Row, t.Level))]),
                $"0x0020 the area's {area.MonsterTypes.Count} monster type(s)", AreaDelta.Kind.Message, only: player);
        foreach (var (id, m) in area.Monsters.Concat(area.DormantMonsters).OrderBy(e => e.Key))
        {
            bool corpse = (m.Dead || m.Life == 0) && m.CreatedAsCorpse && !m.CorpseConsumed;
            if (!m.WorldHidden && ((!m.Dead && m.Life > 0) || corpse) && m.OwnerId != player.Id)
                AddMonsterCreate(player, id, m, now, only: player);
        }
        foreach (var peer in player.Instance.PlayersExcept(player.Id))
            if (peer is { } other && other.Connection.PlayerSent && !other.Closed && !other.LeavingArea && other.PeerTemplate is not null)
                foreach (var (bytes, kind) in PlayerCreate(other, player, now)) area.Delta.Add(other.Id, bytes, null, kind, only: player);
        if (player.PeerTemplate is not null)
            foreach (var (bytes, kind) in PlayerCreate(player, player, now))
                area.Delta.Add(player.Id, bytes, $"player 0x{player.Id:x} {CharacterName(player)} joins the area", kind, except: player);
        // The area's objects as the others have them, then what the area's entities have now: their buffs and the stats
        // written since their creates.
        area.Objects.Join(player);
        area.Buffs.Join(player);
        area.Stats.Join(player);
    }

    // A monster's create and what goes with it (awake, its stance, its walk; its buffs: AreaBuffs.Join), as the area
    // has it now: for a joining client (only), or for every client when a hidden monster shows itself again.
    static void AddMonsterCreate(Player player, uint id, SpawnedMonster m, double now, Player? only = null)
    {
        var area = player.Area;
        var o = player.Instance.World.MonsterOrigins.GetValueOrDefault(id)
            ?? (m.OwnerId is uint owner && player.Frames.Players().FirstOrDefault(p => p.Id == owner) is { } master
                ? master.MinionOrigins.GetValueOrDefault(id) : null);
        if (o is null) return;
        bool dormant = area.DormantMonsters.ContainsKey(id);
        bool corpse = (m.Dead || m.Life == 0) && m.CreatedAsCorpse && !m.CorpseConsumed;
        var at = m.Walk.Position(now);
        var shown = o.Object with { GridX = at.X, GridY = at.Y, Life = corpse ? 1u : m.Life };
        // One that has risen meanwhile is created risen (targetable), not in the wait it was placed in.
        if (!corpse && !dormant && m.Variety is { } v && Emerges.Kind(v, m.Rarity) != EmergeKind.None
            && !MonsterEmerge.Waiting(player, id) && shown.TransitionState != MonsterEmerge.RisenState(v)
            && v.ComponentsOrBase.Contains("Transitionable"))
            shown = shown with { TransitionState = MonsterEmerge.RisenState(v) };
        var create = InstanceEntities.CreateObject(id, o.Type, shown.Blob());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(create.AsSpan(6), uint.MaxValue);
        area.Delta.Add(id, create, $"0x02bb monster 0x{id:x} where the area has it", AreaDelta.Kind.Create, only: only);
        if (!m.Asleep) area.Delta.Add(id, InstanceProtocol.EnterWakeZone(id), null, only: only);
        if (corpse) area.Delta.Add(id, InstanceProtocol.LifeUpdate(id, 0), null, only: only);
        if (InstanceEntities.MonsterEquipmentUpdate(id, shown) is { } stance) area.Delta.Add(id, stance, null, only: only);
        if (m.OwnerId is uint ownerId && player.Frames.Players().FirstOrDefault(p => p.Id == ownerId) is { } ownerPlayer
            && MirageWarriorPeerDisplay(ownerPlayer, id, id) is { } warriorDisplay)
            area.Delta.Add(id, warriorDisplay, null, only: only);
        SettleBleeding(m, now);
        var to = m.Walk.Destination;
        if (!dormant && to != at)
            area.Delta.Add(id, InstanceProtocol.ActionStarted(id, at.X, at.Y, to.X, to.Y, 0x2909, 0xffff, 0x0408), null, only: only);
    }

    /// <summary>A monster hidden from the world (SpawnedMonster.WorldHidden) leaves every client, and comes back to all of
    /// them where it is when it shows itself again.</summary>
    public static async Task SetWorldHidden(Player player, uint id, SpawnedMonster m, bool hidden)
    {
        if (m.WorldHidden == hidden) return;
        m.WorldHidden = hidden;
        if (hidden) await SendWorldRemoval(player, id, $"monster 0x{id:x} hidden from the world");
        else if (player.Area.Owned) AddMonsterCreate(player, id, m, AreaTime(player));
    }

    // Stats.dat use_town_stance (row 21140; a stat key is its row + 1).
    const uint TownStanceStat = 21141;

    // A player's object as another client gets it: created where it stands, awake, in its equipment's stance (the client
    // picks it only when the equipment changes, 0x141e9da30). The beach wake-up is the owner's alone.
    static IEnumerable<(byte[] Bytes, AreaDelta.Kind Kind)> PlayerCreate(Player shown, Player viewer, double now)
    {
        var at = shown.Walk.Position(now);
        var playerObject = shown.PeerTemplate! with { GridX = at.X, GridY = at.Y, Life = shown.PlayerLife,
            Skills = shown.PeerTemplate!.Skills?.Where(s => s.Id != TutorialObjects.WashedUpSkillId).ToList(),
            QuestFlags = [], LeagueQuestFlags = [], WashedUp = false, Rotation = 0 };
        // In a town another player stands and walks in the town stance (Stats.dat use_town_stance).
        if (InTown(viewer))
            playerObject = playerObject with { Stats = [.. (playerObject.Stats ?? []).Where(s => s.Key != TownStanceStat), (TownStanceStat, 1)] };
        var create = InstanceEntities.CreateObject(shown.Id, shown.PeerType, playerObject.Blob());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(create.AsSpan(6), uint.MaxValue);
        yield return (create, AreaDelta.Kind.Create);
        yield return (InstanceProtocol.EnterWakeZone(shown.Id), AreaDelta.Kind.World);
        yield return (InstanceEntities.PlayerEquipmentUpdate(shown.Id, playerObject.Equipped ?? []), AreaDelta.Kind.World);
    }

    // A player leaves the area: the others' clients take its object and its minions away.
    internal static void LeaveArea(Player player)
    {
        if (player.Instance is null || !player.Area.Owned) return;
        var remove = InstanceProtocol.RemoveObject(player.Id);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(remove.AsSpan(6), uint.MaxValue);
        player.Area.Delta.Add(player.Id, remove, $"0x02bd player 0x{player.Id:x} {CharacterName(player)} leaves the area", AreaDelta.Kind.Remove, except: player);
        foreach (uint id in player.MinionOrigins.Keys)
        {
            var gone = InstanceProtocol.RemoveObject(id);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(gone.AsSpan(6), uint.MaxValue);
            player.Area.Delta.Add(id, gone, null, AreaDelta.Kind.Remove, except: player);
        }
    }

    /// <summary>A new monster type in the area (S->C 0x20 registers "Id@level"; a create of that type needs it first):
    /// every client of the area is told once, and a client not in the area's frames yet is told now.</summary>
    static async Task RegisterMonsterType(Player player, ushort row, byte level, string? log)
    {
        if (!player.Area.MonsterTypes.Add((row, level))) return;
        var bytes = InstanceEntities.MonsterTypes([(row, level)]);
        if (player.Area.Owned) player.Area.Delta.Add(0, bytes, log, AreaDelta.Kind.Message);
        if (!player.Connection.WorldOutput.Started) await Send(player, bytes, log);
    }
}
