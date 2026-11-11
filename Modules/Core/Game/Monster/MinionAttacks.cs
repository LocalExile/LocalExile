using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;

/// <summary>A fighting minion's target this AI step: the hostile monster and where it stands, whether it is in reach, and
/// the cell the minion walks to so it is.</summary>
readonly record struct MinionTarget(uint Id, SpawnedMonster Monster, (int X, int Y) At, bool InReach, (int X, int Y) Approach);

static partial class Game
{
    // A fighting minion reaches a monster's edge: its attack's distance (MaximumAttackDistance, as a monster's reach of the
    // player) plus the target's body.
    static double MinionReach(MonsterAttack a, SpawnedMonster target) => a.Range + Math.Max(0, LivingCollision.Size(target));

    /// <summary>
    /// The monster a fighting minion goes for this AI step: the living hostile one nearest it within its aggro range, of it
    /// and of the player (it does not leave him for a distant pack), not lurking unrisen. Null when there is none or the
    /// player is dead; it then follows him. Under the Monsters lock.
    /// </summary>
    internal static MinionTarget? MinionTargetLocked(ClientState conn, uint id, SpawnedMonster m, (int X, int Y) at,
        (int X, int Y) player, double now)
    {
        if (m.Attack is not { } a || conn.PlayerLife == 0) return null;
        MinionTarget? best = null;
        double nearest = double.MaxValue;
        foreach (var (targetId, t) in conn.Monsters)
        {
            if (targetId == id || !t.Hostile || t.Dead || t.Life == 0 || double.IsPositiveInfinity(t.BusyUntil)) continue;
            var to = t.Walk.Position(now);
            double d = Dist(to, at);
            if (d >= nearest || d > m.AggroRange || Dist(to, player) > m.AggroRange) continue;
            nearest = d;
            double reach = MinionReach(a, t), f = d > 0 ? Math.Max(0, reach - 1) / d : 0;
            best = new(targetId, t, to, d <= reach,
                ((int)Math.Round(to.X + (at.X - to.X) * f), (int)Math.Round(to.Y + (at.Y - to.Y) * f)));
        }
        return best;
    }

    /// <summary>
    /// A fighting minion's hit at its swing's contact, under the Monsters lock: both still there and the target in reach,
    /// the minion's accuracy (DefaultMonsterStats at its level) against the target's evasion, the target's block
    /// (Stalwart), then the attack's damage (the minion's own, with the player's minion modifiers it was summoned with)
    /// mitigated by the target as any hit. It is the minion's hit, not the player's (no hit-recently). Null when it did not
    /// land.
    /// </summary>
    internal static (MonsterHit Hit, int Damage)? MinionHitLocked(ClientState conn, uint id, SpawnedMonster m, MinionTarget target,
        double now, Random random) =>
        MinionHitTargetLocked(conn, id, m, target.Id, target.Monster, now, random, checkReach: true);

    static (MonsterHit Hit, int Damage)? MinionHitTargetLocked(ClientState conn, uint id, SpawnedMonster m,
        uint targetId, SpawnedMonster targetMonster, double now, Random random, bool checkReach,
        MonsterAttack? attack = null, int? accuracy = null)
    {
        var t = targetMonster;
        if (!CurrentGameWork || (attack ?? m.Attack) is not { } a || m.Dead || !conn.Minions.Fights(id) || !conn.Monsters.TryGetValue(id, out var self)
            || !ReferenceEquals(self, m) || t.Dead || t.Life == 0 || !t.Hostile
            || !conn.Monsters.TryGetValue(targetId, out var current) || !ReferenceEquals(current, t)) return null;
        var at = m.Walk.Position(now);
        if (checkReach && Dist(at, t.Walk.Position(now)) > MinionReach(a, t) + 2) return null;
        int hitAccuracy = accuracy ?? m.Mods.Effects.Accuracy(Monsters.BaseAccuracy(m.Level) ?? 0);
        int evasion = UnderBlind(conn, targetId, t.Mods.Effects.Evasion(Monsters.BaseEvasion(t.Level) ?? 0));
        if (!DamageModel.Hits(DamageModel.AccuracyHitChance(evasion, hitAccuracy), random.Next(100)) || MonsterBlocks(t, random, spell: false))
            return null;
        var offense = a.Damage;
        int Flat(string bound) => Stats.Key($"non_golem_minion_global_{bound}_added_physical_damage") is uint key
            ? conn.Golems.OwnerStats.GetValueOrDefault(key) : 0;
        if (Flat("maximum") > 0) offense = offense.Add(GameServer.Core.World.DamageType.Physical,
            new GameServer.Core.World.DamageRange(Flat("minimum"), Flat("maximum")).Scale(Math.Max(0, 1 + m.Mods.Effects["damage_+%"] / 100.0)));
        int damage = MonsterTakesRolled(t, offense.Roll(random), spell: false, now: now);
        return ApplyMonsterHitLocked(conn, t, damage, allyAt: at) is { } hit ? (hit, damage) : null;
    }

    // A fighting minion attacks its target: its skill aimed at it (0x1ab with the target's id, as a monster's swing at the
    // player), and at the contact point (half the attack time) the hit lands (MinionHitLocked): 0x1af from the minion, then
    // the target's hit and death as for the player's hits. Its kills give the player their experience, items and quest
    // progress, but are not his for his on-kill effects.
    static void StartMinionSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlayerMinions.Attack attack)
    {
        var updates = AreaUpdateContext.Value;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || updates is null || !PlayerUpdatesCurrent(conn, updates)
                || !updates.MinionAttacks.Add(attack)) return;
        }
        _ = Continue();
        async Task Continue()
        {
            try { await MinionSwing(remote, stream, ciphers, conn, attack); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
            finally { lock (conn.Monsters) updates.MinionAttacks.Remove(attack); }
        }
    }

    internal static async Task MinionSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlayerMinions.Attack attack)
    {
        uint id = attack.Id;
        try
        {
        var rules = CombatLifetime;
        var (_, m, from, targetId, targetMonster, toAt, contactAt) = attack;
        var target = new MinionTarget(targetId, targetMonster, toAt, true, toAt);
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules) && !conn.Closed && !conn.LeavingArea && conn.Minions.Fights(id)
            && conn.Monsters.TryGetValue(id, out var held) && ReferenceEquals(held, m) && !m.Dead
            && conn.Minions.Attacks.TryGetValue(id, out var pending) && ReferenceEquals(pending, attack);
        lock (conn.Monsters) if (!Current()) return;
        var a = m.Attack!;
        if (!attack.Started)
            await Send(remote, stream, ciphers, [],
                $"0x01ab minion 0x{id:x} ({m.Label}) attacks monster 0x{target.Id:x}, attack time {a.Seconds:0.00} s", atGate: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!Current() || attack.Started) return null;
                        attack.Started = true;
                        return a.InPlace
                            ? InstanceProtocol.ActionStarted(id, from.X, from.Y, target.At.X, target.At.Y, a.SkillId, 0xffff, conn.MonsterOrderFlags, targetObjectId: target.Id)
                            : InstanceProtocol.ActionStartedNow(id, from.X, from.Y, target.At.X, target.At.Y, a.SkillId, 0xffff, conn.MonsterOrderFlags, target.Id);
                    }
                });
        while (true)
        {
            int wait;
            lock (conn.Monsters)
            {
                if (!Current()) return;
                double left = contactAt - AreaTime(conn);
                wait = !conn.Paused && left <= 0 ? 0 : (int)Math.Clamp(Math.Ceiling(left * 1000), 1, 1000);
            }
            if (wait == 0) break;
            await AreaWait(PlayerUpdateRole.MonsterAction, wait);
        }
        (MonsterHit Hit, int Damage)? landed = null;
        (int X, int Y) at, to;
        while (true)
        {
            using (ProjectileMutation(conn))
            {
                if (!Current()) return;
                if (!conn.Paused)
                {
                    double now = ProjectileWorldTime(conn);
                    (at, to) = (m.Walk.Position(now), target.Monster.Walk.Position(now));
                    if (a.Projectile is { } projectile)
                    {
                        ReleaseMinionProjectile(remote, stream, ciphers, conn, attack, projectile, now);
                        return;
                    }
                    conn.Minions.Attacks.Remove(id);
                    landed = MinionHitLocked(conn, id, m, target, now, Random.Shared);
                    if (landed is { Hit.StageStarts: true } staged)
                    {
                        CommitBossStage(remote, stream, ciphers, conn, target.Id, target.Monster, now);
                        landed = staged with { Hit = staged.Hit with { StageStarts = false } };
                    }
                    if (landed is { } accepted)
                    {
                        conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(id, 0xffff, target.Id, flags: 0x4601, kind: 0x02,
                            u34: 450, angle: HitAngle(to.X - at.X, to.Y - at.Y)),
                            $"0x01af minion 0x{id:x} hit on monster 0x{target.Id:x}", target.Id, target.Monster));
                        CommitMinionHitLocked(conn, target.Id, target.Monster, accepted.Hit, accepted.Damage, now, $"minion 0x{id:x} ({m.Label})");
                    }
                    break;
                }
            }
            await AreaWait(PlayerUpdateRole.MonsterAction, 100);
        }
        if (landed is not { } hit)
        {
            Console.WriteLine($"[gateway] {remote} minion 0x{id:x} ({m.Label}) missed monster 0x{target.Id:x}");
            return;
        }
        await PublishMinionOutcomesFor(remote, stream, ciphers, conn, rules);
        }
        finally
        {
            lock (conn.Monsters)
                if (conn.Minions.Attacks.TryGetValue(id, out var pending) && ReferenceEquals(pending, attack))
                    conn.Minions.Attacks.Remove(id);
        }
    }
}
