using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static HarbingerRoster CompanionsOf(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Companions is null)
            {
                conn.Companions = HarbingerRoster.For(CharacterName(conn));
                conn.Companions.Attach(conn.SummonLease, DateTimeOffset.UtcNow);
                if (conn.Paused) conn.Companions.Pause(conn.SummonLease, DateTimeOffset.UtcNow, true);
            }
            return conn.Companions;
        }
    }

    static HarbingerRoster.Member[] CompanionMembers(ClientState conn) => CompanionsOf(conn).Read(conn.SummonLease,
        PhreciaNodes(conn), DateTimeOffset.UtcNow);

    static Dictionary<uint, int> CompanionStats(ClientState conn)
    {
        _ = CompanionMembers(conn); // remove buffs whose passive source has gone before building any player stats
        var result = new Dictionary<uint, int>();
        foreach (var active in CompanionsOf(conn).Buffs(conn.SummonLease, DateTimeOffset.UtcNow))
            foreach (var (key, value) in active.Buff.Skill.RecipientStats)
                result[key] = result.GetValueOrDefault(key) + value;
        return result;
    }

    static void CompanionCounts(ClientState conn, Dictionary<uint, int> stats)
    {
        var active = CompanionMembers(conn);
        foreach (var source in Harbingers.All)
            if (source.CountStat is uint count)
                stats[count] = active.Any(m => m.Source.Node == source.Node) ? 1 : 0;
    }

    static bool Channelling(ClientState conn) => conn.ChannelSkill != 0
        && GemSkillEffects(conn).Any(g => g.Id == conn.ChannelSkill && Skills.IsChannel(g.Effect));

    static HarbingerCompanionCatalog.Companion? CompanionForEffect(ClientState conn, int effect) =>
        Harbingers.ForNodes(PhreciaNodes(conn)).FirstOrDefault(c => c.SummonEffect == effect);

    static async Task ActivateCompanion(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        HarbingerCompanionCatalog.Companion source, int castMs)
    {
        int epoch, action;
        double completes;
        lock (conn.Monsters)
        {
            epoch = conn.ProjectileEpoch;
            action = conn.ActionCount;
            completes = conn.Clock.Elapsed.TotalSeconds + castMs / 1000.0;
        }
        while (true)
        {
            if (!await WaitProjectileTime(conn, completes, epoch)) return;
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch) || conn.ActionCount != action || conn.PlayerLife == 0
                    || CompanionForEffect(conn, source.SummonEffect) is null) return;
                if (conn.Paused) continue;
                int recovery = Stats.Key("base_cooldown_speed_+%") is uint key ? source.MinionStats.GetValueOrDefault(key) : 0;
                if (!CompanionsOf(conn).Activate(conn.SummonLease, source, recovery, DateTimeOffset.UtcNow)) return;
                break;
            }
        }
        await RefreshCompanions(remote, stream, ciphers, conn);
    }

    static ushort CompanionSkillId(HarbingerCompanionCatalog.Companion source, int effect) =>
        Skills.SkillIds().Take(source.Buffs.Count + 1).ElementAt(effect == source.WarpEffect ? 0
            : 1 + source.Buffs.Select(b => b.Effect).ToList().IndexOf(effect));

    static Task<bool> SendCompanionBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        HarbingerRoster.Active buff, uint id, ushort instance, int epoch) =>
        SendGenerated(remote, stream, ciphers, () =>
        {
            lock (conn.Monsters)
            {
                var roster = CompanionsOf(conn);
                if (!conn.ProjectileActive(epoch) || conn.Paused || conn.PlayerLife == 0 || !roster.Contains(conn.SummonLease, buff.Owner.Id)
                    || !PhreciaNodes(conn).Contains(buff.Owner.Source.Node)) return null;
                double remaining = buff.Buff.EndsAt - roster.Time(conn.SummonLease, DateTimeOffset.UtcNow);
                if (remaining <= 0) return null;
                return InstanceEntities.AddBuff(PlayerId, instance, buff.Buff.Skill.Definition, id,
                    CompanionSkillId(buff.Owner.Source, buff.Buff.Skill.Effect),
                    u68: BitConverter.SingleToUInt32Bits((float)remaining));
            }
        }, $"Harbinger buff {buff.Buff.Skill.BuffId}");

    static async Task RefreshCompanions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        bool sendOwnerStats = true)
    {
        await conn.SummonGate.WaitAsync();
        bool changed = false;
        try
        {
            if (conn.Closed || conn.LeavingArea) return;
            if (conn.PlayerLife == 0) conn.Companions?.Clear(conn.SummonLease);
            var roster = CompanionsOf(conn);
            var members = CompanionMembers(conn);
            int epoch;
            lock (conn.Monsters) epoch = conn.ProjectileEpoch;
            foreach (var (serial, id) in conn.CompanionObjects.ToArray())
                if (!members.Any(m => m.Id == serial))
                {
                    lock (conn.Monsters) if (conn.Monsters.Remove(id, out var old)) old.Dead = true;
                    conn.CompanionObjects.Remove(serial);
                    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), "Harbinger source removed");
                    changed = true;
                }
            foreach (var member in members)
                if (!conn.CompanionObjects.ContainsKey(member.Id))
                {
                    (int X, int Y) at;
                    lock (conn.Monsters) at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                    uint id = await SpawnMonster(remote, stream, ciphers, conn, member.Source.Monster, 1,
                        MonsterRarity.Normal, at.X, at.Y, friendly: true, companion: member.Source,
                        accept: () => conn.ProjectileActive(epoch) && !conn.Paused && conn.PlayerLife > 0
                            && roster.Contains(conn.SummonLease, member.Id) && PhreciaNodes(conn).Contains(member.Source.Node));
                    if (id != 0)
                    {
                        conn.CompanionObjects[member.Id] = id;
                        changed = true;
                    }
                }
            await SendGenerated(remote, stream, ciphers, () =>
            {
                lock (conn.Monsters)
                {
                    if (!conn.ProjectileActive(epoch) || conn.Paused) return null;
                    if (conn.PlayerLife == 0) { roster.Clear(conn.SummonLease); return null; }
                    _ = CompanionMembers(conn);
                    var updates = roster.Step(conn.SummonLease, DateTimeOffset.UtcNow,
                        conn.AiEnabled && !InTown(conn) && conn.PlayerLife > 0, Channelling(conn),
                        m => conn.CompanionObjects.TryGetValue(m.Id, out uint id) && conn.Monsters.ContainsKey(id));
                    var packets = new List<byte>();
                    foreach (var update in updates)
                    {
                        if (!conn.CompanionObjects.TryGetValue(update.Owner.Id, out uint id)
                            || !conn.Monsters.TryGetValue(id, out var actor)) continue;
                        foreach (var cast in update.Change.Started)
                        {
                            var from = actor.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                            actor.Walk.Order(from.X, from.Y, conn.Clock.Elapsed.TotalSeconds);
                            actor.LastTarget = null;
                            var to = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                            packets.AddRange(InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y,
                                CompanionSkillId(update.Owner.Source, cast.Skill.Effect), 0xffff, 0x0408, targetObjectId: PlayerId));
                        }
                    }
                    return packets.Count > 0 ? packets.ToArray() : null;
                }
            }, "Harbinger cast phase");
            var active = roster.Buffs(conn.SummonLease, DateTimeOffset.UtcNow);
            foreach (var (effect, previous) in conn.CompanionBuffs.ToArray())
                if (!active.Any(a => a.Owner.Id == previous.Member && a.Buff.Skill.Effect == effect && a.Buff.EndsAt == previous.Ends))
                {
                    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, previous.Instance), "Harbinger buff expired");
                    conn.CompanionBuffs.Remove(effect);
                    changed = true;
                }
            foreach (var buff in active)
                if (!conn.CompanionBuffs.ContainsKey(buff.Buff.Skill.Effect)
                    && conn.CompanionObjects.TryGetValue(buff.Owner.Id, out uint id))
                {
                    ushort instance = conn.AllocateBuff();
                    bool sent = await SendCompanionBuff(remote, stream, ciphers, conn, buff, id, instance, epoch);
                    if (sent)
                    {
                        conn.CompanionBuffs[buff.Buff.Skill.Effect] = (instance, buff.Owner.Id, buff.Buff.EndsAt);
                        changed = true;
                    }
                }
            // Local following policy: keep a companion near its owner. Server AIS range gates are not shipped.
            foreach (var member in members)
            {
                if (member.Cycles.IsCasting || !conn.CompanionObjects.TryGetValue(member.Id, out uint id)) continue;
                await SendGenerated(remote, stream, ciphers, () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!conn.ProjectileActive(epoch) || conn.Paused || conn.PlayerLife == 0 || !roster.Contains(conn.SummonLease, member.Id)
                            || !PhreciaNodes(conn).Contains(member.Source.Node) || !conn.Monsters.TryGetValue(id, out var actor)) return null;
                        double at = conn.Clock.Elapsed.TotalSeconds;
                        var to = conn.Walk.Position(at);
                        var from = actor.Walk.Position(at);
                        if (Dist(from, to) <= 12 || actor.LastTarget is { } last && Dist(last, to) <= 3) return null;
                        actor.LastTarget = to;
                        from = actor.Walk.Order(to.X, to.Y, at);
                        return InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, 0x0408);
                    }
                }, "Harbinger follows owner");
            }
            if (changed) Interlocked.Increment(ref conn.CompanionRevision);
        }
        finally { conn.SummonGate.Release(); }
        long revision = Interlocked.Read(ref conn.CompanionRevision);
        if (sendOwnerStats && revision != Interlocked.Read(ref conn.PublishedCompanionRevision))
        {
            lock (conn.Monsters)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
            await SendCharacterStats(remote, stream, ciphers, conn, "Harbinger effects changed");
            Interlocked.Exchange(ref conn.PublishedCompanionRevision, revision);
        }
    }
}
