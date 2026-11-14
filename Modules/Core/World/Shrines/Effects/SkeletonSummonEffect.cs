using GameServer.Core.Protocol.Instance;
using static Game;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The Skeletal shrine's effect (Shrines row 15 <c>Skeletons</c>, whose BuffTemplates 399 <c>skeleton_player</c> grants
/// BuffDefinitions 254 <c>shrine_skeleton</c>, "Skeletons are summoned to you", for the row's 45 s). The row names two
/// summon daemons, 2926 <c>Metadata/Monsters/Daemon/ShrineDaemonSummonSkeletons</c> for the unclaimed shrine and 2927
/// <c>…SkeletonsPlayer</c> for the player's. Both are NoAi daemons whose one granted effect is the shrine's own
/// GrantedEffects 1315 (<c>ShrineSummonSkeletons</c>, 1316 for the player's): ActiveSkill 183 <c>summon_skeletons</c>, the
/// player's own skill, cast every 1000 ms, with stat set 1326/1327 <c>base_skill_effect_duration</c> 30000,
/// <c>active_skill_elemental_damage_+%_final</c> -50, <c>alternate_minion</c> 1 and
/// <c>summoned_monsters_no_drops_or_experience</c>. So one Skeleton Warrior a second, of
/// <c>Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard</c> — the monster the skill summons, which the skill's own
/// description names ("Summon Skeleton Warrior minions … die after a duration") — each living the 30 s of that duration,
/// at the area's level, and giving no experience or items.
/// <para>Before the shrine is claimed the skeletons are the shrine's: it is the monsters' side until the player takes it,
/// so they rise around the statue and fight him. Once its buff is up they are his allies instead, rising beside him,
/// following him and striking the monsters near him. They are allies, not his minions: GGG's own answer to a report that
/// minion modifiers do not reach them is "You are not considered to be summoning them", so they carry no
/// <c>is_player_minion</c> stat, take none of his minion modifiers, count towards no minion limit and are not in
/// <see cref="PlayerMinions"/>. The engine fights them as the AI's own allies (<c>SpawnedMonster.AlliedFighter</c>).</para>
/// <para>Each skeleton ends after its own 30 s, whether or not the shrine's 45 s buff is still up; the shrine's own end
/// takes the rest. The data gives no cap on how many: the daemon would raise one a second for the whole buff. We keep
/// <see cref="MaxAlive"/> alive, as many as a Summon Skeletons gem's skeletons plus its quality.</para>
/// </summary>
sealed class SkeletonSummonEffect : IShrineEffect
{
    /// <summary>GrantedEffects row of the unclaimed shrine's summon (<c>ShrineSummonSkeletons</c>); its ActiveSkill is
    /// the player's own <c>summon_skeletons</c>.</summary>
    internal const int Skill = 1315;

    /// <summary>The daemons the Shrines row names: the unclaimed shrine's and the player's.</summary>
    internal const string Daemon = "Metadata/Monsters/Daemon/ShrineDaemonSummonSkeletons";
    internal const string PlayerDaemon = "Metadata/Monsters/Daemon/ShrineDaemonSummonSkeletonsPlayer";

    /// <summary>The monster the skill brings: the Skeleton Warrior the player's own Summon Skeletons uses.</summary>
    internal const string Variety = "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard";

    /// <summary>The daemon's cast time, which is how often one skeleton comes.</summary>
    internal const double SummonSeconds = 1;

    /// <summary>The skill's <c>base_skill_effect_duration</c> (stat set 1326): how long each skeleton lives.</summary>
    internal const double LifetimeSeconds = 30;

    /// <summary>Our cap on the living skeletons (the data has none).</summary>
    internal const int MaxAlive = 8;

    /// <summary>The skeletons this effect brought and when each one ends (game seconds), and where each rose.</summary>
    readonly Dictionary<uint, double> _expires = [];
    readonly Dictionary<uint, (int X, int Y)> _spots = [];

    double _nextSummon;

    /// <summary>How many have been summoned: the ring slot advances every summon, so a steady state (one ending as one
    /// comes) does not put them all on the same cell.</summary>
    int _placed;

    /// <summary>How far, in cells, an ally may trail the player before it is ordered back to him, and how often.</summary>
    internal const double FollowCells = 10;
    internal const double FollowSeconds = 1;

    /// <summary>When each ally may be ordered to the player again (game seconds).</summary>
    readonly Dictionary<uint, double> _nextFollow = [];

    public Task OnSpawn(ShrineContext ctx, ShrineInstance shrine)
    {
        Console.WriteLine($"[gateway] shrine {shrine.Definition.Id}: summon {Skill} ({Variety}), daemons " +
            $"{(Monsters.Find(Daemon) is null ? "MISSING" : "found")}/{(Monsters.Find(PlayerDaemon) is null ? "MISSING" : "found")}, " +
            $"{LifetimeSeconds:0} s each, at most {MaxAlive}");
        return Task.CompletedTask;
    }

    public async Task Tick(ShrineContext ctx, ShrineInstance shrine)
    {
        var conn = ctx.Conn;
        if (conn.Closed) return;
        double now = ctx.Now;
        List<uint> ended = [];
        List<(uint Id, SpawnedMonster Monster, (int X, int Y) From, (int X, int Y) To)> behind = [];
        (int X, int Y) at;
        bool summon = false;
        lock (conn.Monsters)
        {
            // A connection that has left the area keeps none of them: the skeletons went with the area.
            if (conn.LeavingArea)
            {
                _expires.Clear();
                _spots.Clear();
                _nextSummon = 0;
                return;
            }
            var player = conn.Walk.Position(now);
            foreach (var (id, until) in _expires)
            {
                if (now >= until || !conn.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0)
                {
                    ended.Add(id);
                    continue;
                }
                // The player's allies follow him: one the AI left behind (a fight it walked out of, a slot it keeps) is
                // ordered to him, at most once a second each.
                if (shrine.State == ShrineState.Inactive || now < _nextFollow.GetValueOrDefault(id)) continue;
                var here = m.Walk.Position(now);
                if (Dist(here, player) <= FollowCells) continue;
                // A skeleton with a swing in flight is left to finish it; it is pulled after the player once it lands.
                if (conn.Minions.Attacks.ContainsKey(id)) continue;
                _nextFollow[id] = now + FollowSeconds;
                m.Walk.Order(player.X, player.Y, now);
                m.LastTarget = null;
                behind.Add((id, m, here, player));
            }
            at = shrine.State == ShrineState.Inactive ? shrine.At : conn.Walk.Position(now);
            if (now >= _nextSummon && _expires.Count < MaxAlive)
            {
                _nextSummon = now + SummonSeconds;
                summon = true;
            }
        }
        foreach (uint id in ended) End(conn, id);
        if (summon) await Summon(ctx, shrine, at);
        foreach (var (id, m, from, to) in behind) await Follow(ctx, id, m, from, to);
    }

    /// <summary>An ally left behind is walked back to the player: the estimate's walk and the client's 0x01ab Move, the
    /// same pair the monster AI sends for its own Move orders (move skill 0x2909).</summary>
    async Task Follow(ShrineContext ctx, uint id, SpawnedMonster m, (int X, int Y) from, (int X, int Y) to)
    {
        var conn = ctx.Conn;
        var (remote, stream, ciphers) = conn.Wire;
        await SendWorldRecord(remote, stream, ciphers, conn, id,
            InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, conn.MonsterOrderFlags),
            $"0x01ab shrine ally 0x{id:x} follows the player from ({from.X}, {from.Y}) to ({to.X}, {to.Y})",
            still: () => CurrentGameWork && !conn.Closed && !conn.LeavingArea
                && conn.Monsters.TryGetValue(id, out var held) && ReferenceEquals(held, m) && !m.Dead && m.Life > 0);
    }

    /// <summary>One Skeleton Warrior beside the caster: the shrine's own around the statue while it stands, the player's
    /// beside him once the buff is up.</summary>
    async Task Summon(ShrineContext ctx, ShrineInstance shrine, (int X, int Y) at)
    {
        var conn = ctx.Conn;
        if (Monsters.Find(Variety) is not { Supported: true } variety) return;
        bool allied = shrine.State != ShrineState.Inactive;
        byte level = ShrineManager.AreaLevel(conn);
        double now = ctx.Now, until = now + LifetimeSeconds;
        (int X, int Y) spot;
        lock (conn.Monsters) spot = SpotLocked(conn, at, allied);
        var (x, y) = spot;
        var (remote, stream, ciphers) = conn.Wire;
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, x, y,
            friendly: allied, log: false, alone: false,
            owned: (id, m) =>
            {
                // summoned_monsters_no_drops_or_experience: no experience, items or quest progress of its own.
                m.NoRewards = true;
                _expires[id] = until;
                _spots[id] = spot;
                // The shrine's creature either way: it goes with the area rather than being saved with it.
                m.SummonerId = shrine.ObjectId;
                if (!allied) return;
                // The player's ally: no minion stats, no minion limit, no minion modifiers.
                m.Aggro = true;
                m.AlliedFighter = true;
            },
            ownedCurrent: () => conn.PlayerLife > 0 && !conn.LeavingArea);
        if (id == 0) return;
        Console.WriteLine($"[gateway] {remote} shrine {shrine.Definition.Id} skeleton 0x{id:x} at ({x}, {y}) " +
            $"level {level}, {(allied ? "the player's ally" : "the shrine's")}, {_expires.Count} of {MaxAlive}");
    }

    /// <summary>
    /// Where the next skeleton rises: a ring clear of the statue's seven-cell body while the shrine stands (or a few cells
    /// around the player once its buff is up), the slot advancing every summon, and each candidate two cells from every
    /// skeleton still up and on walkable ground — otherwise the spawn's own collision escape lays them on one cell.
    /// Under the Monsters lock.
    /// </summary>
    (int X, int Y) SpotLocked(ClientState conn, (int X, int Y) at, bool allied)
    {
        int slot = _placed++;
        int inner = allied ? 3 : ShrinePlacement.ObjectSize + 1;
        double start = Math.PI * 2 * (slot % 12) / 12;
        for (int ring = 0; ring <= 3; ring++)
            for (int turn = 0; turn < 12; turn++)
            {
                double angle = start + turn * Math.PI / 6 + ring * 0.13;
                int radius = inner + ring + slot % 2;
                var (x, y) = (at.X + (int)Math.Round(Math.Cos(angle) * radius), at.Y + (int)Math.Round(Math.Sin(angle) * radius));
                if (conn.Layout.Walkable is { } walk && !walk(x, y)) continue;
                if (_spots.Values.Any(s => Math.Abs(s.X - x) + Math.Abs(s.Y - y) < 2)) continue;
                return (x, y);
            }
        return at;
    }

    /// <summary>An ended skeleton leaves the world as a summoned monster does: no corpse, no life bar.</summary>
    void End(ClientState conn, uint id)
    {
        if (conn.Closed) return;
        lock (conn.Monsters)
        {
            _expires.Remove(id);
            _spots.Remove(id);
            _nextFollow.Remove(id);
            conn.Minions.Remove(id);
            if (!conn.Monsters.Remove(id, out var m)) return;
            m.Aggro = false;
            m.Dead = true;
            m.Life = 0;
            m.ActionGeneration++;
            conn.MonsterViews.Remove(id);
            conn.AsleepMonsters.Remove(id);
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "shrine skeleton ends", id, m,
                PlayerMinions.OutputKind.Remove, WorldId: id, RemovesObject: true));
        }
    }

    /// <summary>Claiming and expiring both end the skeletons this effect brought.</summary>
    public Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine)
    {
        _expires.Clear();
        _spots.Clear();
        _nextSummon = 0;
        return Task.CompletedTask;
    }

    Task Clear(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea) return Task.CompletedTask;
        uint[] ids;
        lock (conn.Monsters) ids = [.. _expires.Keys];
        foreach (uint id in ids) End(conn, id);
        return Task.CompletedTask;
    }
}
