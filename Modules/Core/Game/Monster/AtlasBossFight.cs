using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Game;

/// <summary>
/// One instance of a Shaper or Elder fight: the plumbing both bosses share (the players looking at it, the effect
/// objects they have to see, the immunity shield of an intermission and the serialized step the instance runner calls).
/// The rotation itself lives in <see cref="ShaperFight"/> and <see cref="ElderFight"/>.
/// <para>Actions go out through <see cref="Monsters.MonsterSwing"/>, which owns the timing of a clip (the 0x1ab, the
/// contact at the clip's keyframe, the projectile it launches) and drops the hit when the boss's action generation
/// moved on. The step therefore only decides what the boss does next and how long it stays busy; it never waits for a
/// clip, or one boss would stall every other player's tick.</para>
/// </summary>
abstract class AtlasBossFight
{
    // One step at a time: Tick runs per connection, and the arena's runner and a joining player both reach the fight.
    internal readonly SemaphoreSlim Gate = new(1, 1);
    protected readonly uint id;
    protected readonly SpawnedMonster boss;
    protected readonly List<ClientState> players = [];
    readonly HashSet<ClientState> bound = [];
    protected readonly List<(uint Id, double Ends)> effects = [];
    protected double busyUntil;
    bool stopped, cleaned, shielding;
    ushort shieldBuff;
    uint shieldEffect;

    protected AtlasBossFight(uint id, SpawnedMonster boss, ClientState conn)
    {
        this.id = id;
        this.boss = boss;
        // The player is registered by the Bind that follows (AtlasBosses.Tick), which is also what gives it the boss
        // bar. Registering it here as well made that first Bind return early (`bound` already held it), so whoever
        // opened the arena never saw the bar.
    }

    protected LifePhaseGate Phases => boss.LifePhases!;
    protected MonsterCatalog.Variety Variety => boss.Variety!;
    // Log prefix: one line per action, the way the King and the Brine King log theirs.
    protected abstract string Name { get; }
    protected abstract ushort ImmunityRow { get; }
    protected abstract string ShieldArt { get; }
    protected abstract string ShieldOffArt { get; }
    /// <summary>Where a phase change puts him back: the arena's own middle marker when the area places one, else the
    /// cell he stood on when the fight began. Never the layout's spawn: that is the map entry, and the Shaper stands
    /// 1050 cells from it, so a "blink to the middle" landed him at the door. The Brine King reads the same marker.</summary>
    protected static (int X, int Y) ArenaCentre(ClientState conn, SpawnedMonster boss)
    {
        var placed = new QuestObjectTypes.AreaPlacements(conn.Layout.Id, conn.Seed, []);
        if (placed.Of("Metadata/MiscellaneousObjects/ArenaMiddle").FirstOrDefault() is { } middle) return (middle.X, middle.Y);
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) return boss.Walk.Position(now);
    }

    // Where the boss stands when it enters an intermission: the middle of the arena, away from the player.
    protected abstract (int X, int Y) Stage(ClientState conn);

    /// <summary>Adds a player that just entered (or a second one joining the arena) and gives it the boss bar.</summary>
    internal async Task Bind(ClientState conn)
    {
        if (!bound.Add(conn)) return;
        lock (players) players.Add(conn);
        if (boss.Variety is not null)
        {
            var (remote, stream, ciphers) = conn.Wire;
            // ShowBossLifeBar sends only a bar that is meant to show. Sirus's fight shows and hides his own (Sirus.cs).
            if (!AtlasBosses.IsSirus(boss.Variety)) lock (conn.Monsters) boss.BossBarShown = true;
            await ShowBossLifeBar(remote, stream, ciphers, id, boss, conn);
        }
    }

    /// <summary>The runner's step: the phase the boss is in decides what happens, everything else is the rotation of
    /// <see cref="Act"/>.</summary>
    internal async Task Step(ClientState conn, double now)
    {
        if (stopped || cleaned) return;
        if (boss.Dead || boss.Life == 0) { await Cleanup(conn); return; }
        await Expire(now);
        // Timed effects of a skill already cast (a degen's ticks, a slam's delayed contact) run whatever the boss is doing:
        // they belong to the clip that started them, not to the rotation.
        await Ticks(conn, now);
        bool watched = players.Any(p => !p.Closed && !p.LeavingArea && p.PlayerLife > 0);
        if (!watched)
        {
            // Nobody is left to fight: the arena idles. An intermission that was running ends, so a player who comes back
            // finds a boss that can be damaged instead of one locked behind a shield nothing will ever lift.
            if (shielding && players.FirstOrDefault(p => !p.Closed) is { } back) await Shield(back, false);
            if (Phases.Active) EndIntermission();
            return;
        }
        if (!Phases.Active && Phases.Begin(boss.Life, boss.MaxLife)) { await Intermission(conn, now); return; }
        if (Phases.Active) { await Intermission(conn, now); return; }
        if (now < busyUntil) return;
        await Act(conn, now);
    }

    /// <summary>Whatever a skill already cast still owes the fight: a degen's ticks, a slam landing after its telegraph.
    /// Called on every step, before the rotation.</summary>
    protected virtual Task Ticks(ClientState conn, double now) => Task.CompletedTask;

    /// <summary>The rotation outside an intermission (the boss is free to act: <see cref="busyUntil"/> has passed).</summary>
    protected abstract Task Act(ClientState conn, double now);
    /// <summary>One step of an intermission, entered when a life threshold is crossed and left through
    /// <see cref="EndIntermission"/>.</summary>
    protected abstract Task Intermission(ClientState conn, double now);
    // Kept for the classes below: an intermission that has played all of its waves closes its own gate.
    protected void EndIntermission() => Phases.Finish();

    /// <summary>Plays one of the boss's skills. The clip's own cast time keeps the boss busy; the method returns at once
    /// (MonsterSwing awaits the clip and lands the hit, which must not hold the instance's tick).</summary>
    protected void Swing(ClientState conn, int effect, bool area = false, double radius = 0) =>
        SwingWith(conn, Attack(conn, effect), effect, area, radius);

    /// <summary>The same, for a skill whose numbers the controller adjusts (a slam that hits harder than the melee it is
    /// built from); <paramref name="effect"/> still names the clip.</summary>
    protected void SwingWith(ClientState conn, MonsterAttack attack, int effect, bool area = false, double radius = 0)
    {
        int action;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var at = boss.Walk.Position(now);
            boss.Walk.Order(at.X, at.Y, now);
            action = ++boss.ActionGeneration;
            busyUntil = Math.Max(busyUntil, now) + attack.Seconds + 0.2;
        }
        var (remote, stream, ciphers) = conn.Wire;
        _ = Run(remote, stream, ciphers, conn, action, attack, area, radius, effect);
    }

    async Task Run(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int action, MonsterAttack attack, bool area, double radius, int effect)
    {
        try
        {
            await MonsterSwing(remote, stream, ciphers, conn, id, boss, action, attack, area, radius);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] {remote} {Name} 0x{id:x} skill {effect} failed: {ServerLog.Describe(ex)}");
        }
    }

    protected MonsterAttack Attack(ClientState conn, int effect) =>
        AtlasBosses.Attack(conn, Variety, boss.Level, effect, boss.Attack);

    /// <summary>Walks the boss towards the player, one order per call (the step decides when).</summary>
    protected async Task Move(ClientState conn, (int X, int Y) to, double now)
    {
        lock (conn.Monsters) boss.Walk.Order(to.X, to.Y, now);
        await All(InstanceProtocol.ActionStarted(id, boss.Walk.Position(now).X, boss.Walk.Position(now).Y, to.X, to.Y,
            AtlasBosses.MoveSkill, conn.NextActionSequence(), conn.MonsterOrderFlags));
    }

    /// <summary>Blinks the boss (0x1d4): the walk estimate and the client's actor both move, no action is played.</summary>
    protected async Task Teleport(ClientState conn, (int X, int Y) to, double now)
    {
        lock (conn.Monsters) boss.Walk.Teleport(to.X, to.Y, now);
        await All(InstanceProtocol.Teleport(id, to.X, to.Y));
    }

    /// <summary>The intermission shield: an invisible cannot-be-damaged buff (the boss's own row, so the client shows the
    /// right icon) plus the shield .ao around it. Damage never lands while it is up, the phase gate stops it anyway.</summary>
    protected async Task Shield(ClientState conn, bool on)
    {
        if (on == shielding) return;
        shielding = on;
        lock (conn.Monsters) if (on) shieldBuff = conn.NextBuff++;
        if (on)
        {
            await All(InstanceEntities.AddBuff(id, shieldBuff, ImmunityRow, id), monster: true);
            shieldEffect = await Effect(conn, boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds), ShieldArt, 600, 1);
        }
        else
        {
            if (shieldBuff != 0) await All(InstanceEntities.RemoveBuff(id, shieldBuff), monster: true);
            shieldBuff = 0;
            if (shieldEffect != 0)
            {
                var gone = shieldEffect;
                shieldEffect = 0;
                await Effect(conn, boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds), ShieldOffArt, 1.5, 1);
                await RemoveEffect(gone);
            }
        }
    }

    protected async Task<uint> Effect(ClientState conn, (int X, int Y) at, string ao, double life, float scale = 1)
    {
        uint effect;
        lock (conn.Monsters) effect = conn.NextObjectId++;
        var obj = new EffectObject(at.X, at.Y, ao) { Scale = scale };
        await All(InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()));
        await All(InstanceProtocol.EnterWakeZone(effect));
        effects.Add((effect, conn.Clock.Elapsed.TotalSeconds + life));
        return effect;
    }

    async Task RemoveEffect(uint effect)
    {
        effects.RemoveAll(e => e.Id == effect);
        foreach (var player in players) lock (player.QuestObjects) player.QuestObjects.Remove(effect);
        await All(InstanceProtocol.RemoveObject(effect));
    }

    protected async Task Expire(double now)
    {
        foreach (var effect in effects.Where(e => e.Ends <= now).ToArray()) await RemoveEffect(effect.Id);
        foreach (var due in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(due);
            await Effect(due.Conn, due.Pos, due.Ao, due.Life, due.Scale);
        }
    }

    // Art that belongs to a later moment than the action that starts it: the celestial beam's rig stands on the ground
    // from the end of the cast, not from its start, or the arena shows a beam nothing appears to have cast. Spawned by
    // Expire on a later step, so no step waits for it.
    readonly List<(double At, ClientState Conn, (int X, int Y) Pos, string Ao, double Life, float Scale)> pending = [];

    /// <summary>Shows art at <paramref name="at"/> (clock seconds) instead of now.</summary>
    protected void EffectAt(double at, ClientState conn, (int X, int Y) pos, string ao, double life, float scale = 1) =>
        pending.Add((at, conn, pos, ao, life, scale));

    /// <summary>A single hit from one of the boss's skills, on the player the skill lands on (a degen tick or a slam the
    /// action did not carry: the contact of a swing goes through MonsterSwing).</summary>
    protected async Task Hit(ClientState conn, MonsterAttack attack, (int X, int Y) from)
    {
        var (remote, stream, ciphers) = conn.Wire;
        Game.MonsterHitOutcome? outcome = null;
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0 ||
                    InGracePeriod(conn) || boss.Dead) return null;
                double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
                outcome = CommitMonsterHitLocked(remote, conn, id, boss, attack, now, conn.Walk.Position(now), from, false,
                    conn.NextActionSequence());
                return outcome?.HitPacket;
            }
        });
        if (outcome is not null) await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
    }

    /// <summary>The players the boss can see now (in the instance, awake, alive).</summary>
    protected ClientState[] Watchers() =>
        [.. players.Where(p => !p.Closed && !p.LeavingArea && p.PlayerLife > 0)];

    protected async Task All(byte[] bytes, bool monster = false)
    {
        if (stopped) return;
        foreach (var conn in Watchers().Where(p => !monster || Awake(p)))
        {
            // Send encrypts in place: every recipient needs its own plaintext, including effect broadcasts.
            var (remote, stream, ciphers) = conn.Wire;
            await Send(remote, stream, ciphers, [.. bytes], null,
                still: () => !stopped && !conn.Closed && !conn.LeavingArea && (!monster || Awake(conn)));
        }
    }

    protected bool Awake(ClientState conn)
    {
        lock (conn.Monsters) return conn.MonsterViews.ContainsKey(id) && !conn.AsleepMonsters.Contains(id);
    }

    /// <summary>The boss left the fight (killed, or the instance emptied): the shield comes off so nothing stays locked
    /// behind a phase gate, and the walk estimate keeps the boss's own speed.</summary>
    protected async Task Cleanup(ClientState conn)
    {
        if (cleaned) return;
        cleaned = true;
        try
        {
            if (shielding) await Shield(conn, false);
            if (Phases.Active) EndIntermission();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        Stop();
    }

    internal void Stop()
    {
        stopped = true;
        Phases.DamageTakenPercent = 100;
        if (players.FirstOrDefault() is { } conn)
            lock (conn.Monsters) boss.Walk.SetSpeed(boss.Variety?.MovementSpeed ?? 30, conn.Clock.Elapsed.TotalSeconds);
    }

    protected static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
