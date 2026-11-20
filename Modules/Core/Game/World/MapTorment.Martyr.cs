using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapTorment
{
    sealed record MartyrViewer(ClientState Player, bool Awake, (int X, int Y)? At, GolemState.Golem[] Minions)
    {
        public readonly List<(uint Id, SpawnedMonster Target, byte[] Packet)> MinionHits = [];
        public MonsterHitOutcome? Hit;
        public int Epoch = Player.IncomingHitEpoch;
    }

    sealed record MartyrDeath(SpawnedMonster Source, (int X, int Y) At, MonsterAttack Attack,
        byte[] Skill, byte[] Action, List<MartyrViewer> Viewers)
    {
        public required ClientState Owner;
        public bool DeferredDot, Resolved, Publishing;
    }

    // The ordinary 3.29.3c SuicideExplosion action includes equality and the target's signed Positioned radius.
    static bool InMartyrBlast((int X, int Y) from, (int X, int Y) target, int radius, int body)
    {
        long dx = (long)target.X - from.X, dy = (long)target.Y - from.Y, reach = (long)radius + body;
        return dx * dx + dy * dy <= reach * reach;
    }

    // The lethal transition captures the cast and its contacts. The killing transaction resolves them after it has
    // credited the monster, and its output presents them after the killing blow. All state uses the instance's Monsters lock.
    internal static void MartyrDiedLocked(ClientState conn, SpawnedMonster monster)
    {
        uint id = monster.ObjectId;
        if (id == 0 || !monster.Hostile || monster.CorpseConsumed || TormentCatalog.MartyrGrantRow(conn, monster.Mods) is not int row || !CurrentGameWork) return;
        var origin = conn.Instance.World.MonsterOrigins[id].Object;
        var skill = origin.Skills!.Single(s => s.EffectsPerLevelRow == row);
        var source = monster.Effects;
        int radiusPct = SkillArea.RadiusPercent(name => source[name], spell: true);
        int radius = (int)(15L * (100L + radiusPct) / 100);
        var stats = skill.Stats!.ToDictionary(s => s.Key, s => s.Value);
        stats[Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value] = radiusPct;
        skill = skill with { Stats = [.. stats.Select(s => (s.Key, s.Value))] };
        int defaultLife = Monsters.DefaultLife(monster.Level) ?? throw new InvalidDataException($"No default monster life at level {monster.Level}");
        double fire = defaultLife * (double)stats[Stats.Key("suicide_explosion_%_of_default_monster_life_to_deal_as_fire")!.Value] / 100;
        var effects = source.Plus("damage_+%", source["fire_damage_+%"] + source["elemental_damage_+%"] + source["area_damage_+%"]);
        var attack = new MonsterAttack(skill.Id, 0, radius, effects.Hit(DamagePacket.Of(DamageType.Fire, fire, fire)))
            { Spell = true, IncludesTemporaryDamage = true };
        double now = ProjectileWorldTime(conn);
        var at = monster.Walk.Position(now);
        var area = Of(conn) ?? Areas.GetValue(conn.Instance, _ => new());
        Remark(conn, area, id, monster); // A restored host can die before the runner's first sight check.
        monster.CorpseConsumed = true; // TormentTouchedMonsterMartyr: base_monster_will_be_deleted_on_death.
        var viewers = new List<MartyrViewer>();
        foreach (var player in Members(conn).Where(p => p.PlayerSent))
        {
            bool eligible = !player.Paused && !InGracePeriod(player);
            var here = player.Walk.Position(now);
            bool playerHit = eligible && player.PlayerLife > 0 && !player.Immune && InMartyrBlast(at, here, radius, LivingCollision.PlayerSize);
            GolemState.Golem[] minions = eligible
                ? [.. player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values)
                    .Where(g => !g.Monster.Dead && InMartyrBlast(at, g.Monster.Walk.Position(now), radius, LivingCollision.Size(g.Monster)))] : [];
            viewers.Add(new(player, player.MonsterViews.ContainsKey(id) && !player.AsleepMonsters.Contains(id), playerHit ? here : null, minions));
        }
        byte actor = checked((byte)origin.Components.ToList().IndexOf("Actor"));
        area.MartyrDeaths.Add(id, new(monster, at, attack, InstanceEntities.ActorSkills(id, actor, [skill]),
            InstanceProtocol.InstantAction(id, skill.Id, at.X, at.Y, triggered: true), viewers) { Owner = conn });
    }

    static MartyrDeath? PendingMartyr(ClientState conn, SpawnedMonster monster) =>
        monster.CorpseConsumed && conn.Instance is not null && Areas.TryGetValue(conn.Instance, out var area)
            ? area.MartyrDeaths.GetValueOrDefault(monster.ObjectId) : null;

    internal static bool HoldsCorpseLocked(ClientState conn, SpawnedMonster monster) => PendingMartyr(conn, monster) is not null;

    // DotKilled hands a shared ailment kill to its attacker. If that connection leaves before draining its shares,
    // the resident runner can claim this same pending kill. The two claims use the world's Monsters lock.
    internal static bool DeferMartyrDot(ClientState owner, SpawnedMonster monster)
    {
        lock (owner.Monsters)
        {
            if (PendingMartyr(owner, monster) is not { } death) return false;
            death.Owner = owner;
            death.DeferredDot = true;
            owner.KillShares.Enqueue((remote, stream, ciphers) =>
            {
                lock (owner.Monsters)
                {
                    if (!death.DeferredDot || owner.Closed || owner.LeavingArea) return Task.CompletedTask;
                    death.DeferredDot = false;
                }
                return MonsterKilled(remote, stream, ciphers, owner, monster.ObjectId, monster, byHit: false);
            });
            return true;
        }
    }

    internal static bool ResolveMartyrLocked(ClientState conn, SpawnedMonster monster)
    {
        if (PendingMartyr(conn, monster) is not { Resolved: false } death) return false;
        death.Owner = conn;
        death.Resolved = true;
        foreach (var v in death.Viewers)
        {
            var p = v.Player;
            if (p.Closed || p.LeavingArea || v.At is null && v.Minions.Length == 0) continue;
            double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
            using var defenceTime = FortificationOf(p).Capture(now);
            foreach (var g in v.Minions)
                GolemTakeHitLocked(p, g, monster.ObjectId, monster, death.Attack, now, area: true, sourceHits: v.MinionHits);
            if (v.At is { } at && p.PlayerLife > 0 && p.IncomingHitEpoch == v.Epoch)
            {
                v.Hit = CommitMonsterHitLocked(p.Wire.Remote, p, monster.ObjectId, monster, death.Attack, now, at, death.At, checkRange: false);
                v.Epoch = p.IncomingHitEpoch;
            }
        }
        return true;
    }

    internal static async Task PresentMartyr(ClientState conn, SpawnedMonster monster)
    {
        uint id = monster.ObjectId;
        MartyrDeath death;
        lock (conn.Monsters)
        {
            if (PendingMartyr(conn, monster) is not { Resolved: true, Publishing: false } pending) return;
            death = pending;
            death.Publishing = true;
        }
        try
        {
            await Task.WhenAll(death.Viewers.Select(Present));
        }
        finally
        {
            try { await Remove(conn, id, "Martyr's Touch exploded"); }
            finally { lock (conn.Monsters) Areas.GetValue(conn.Instance, _ => new()).MartyrDeaths.Remove(id); }
        }

        async Task Present(MartyrViewer v)
        {
            var p = v.Player;
            bool Here() => CurrentGameWork && !p.Closed && !p.LeavingArea && p.Instance == conn.Instance;
            if (!Here()) return;
            var w = p.Wire;
            if (v.Awake) await SendWorldRecords(w.Remote, w.Stream, w.Ciphers, p, id, () =>
            {
                lock (p.Monsters)
                {
                    if (!p.MonsterViews.ContainsKey(id) || p.AsleepMonsters.Contains(id)) return null;
                    var records = new List<byte[]> { death.Skill, death.Action };
                    foreach (var contact in v.MinionHits)
                        if (p.Monsters.TryGetValue(contact.Id, out var current) && ReferenceEquals(current, contact.Target))
                            records.Add(contact.Packet);
                    if (v.Hit is { } hit && p.IncomingHitEpoch == v.Epoch) records.Add(hit.HitPacket);
                    records.Add(InstanceProtocol.LifeUpdate(id, 0));
                    return [.. records];
                }
            }, still: Here);
            if (v.Hit is { } outcome && Here() && p.IncomingHitEpoch == v.Epoch)
                await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
    }

    // A retired killer no longer drains its output. Unclaimed shared DoT kills still need ordinary kill credit.
    static async Task FinishAbandonedMartyrs(ClientState conn, Area area)
    {
        List<(SpawnedMonster Monster, bool DeferredDot)> abandoned;
        lock (conn.Monsters)
        {
            if (area.MartyrDeaths.Count == 0 || !IsMonsterRunner(conn)) return;
            abandoned = [];
            foreach (var d in area.MartyrDeaths.Values)
            {
                if (d.Publishing || !d.Owner.Closed && !d.Owner.LeavingArea) continue;
                if (d.DeferredDot)
                {
                    d.DeferredDot = false;
                    d.Owner = conn;
                    abandoned.Add((d.Source, true));
                }
                else if (d.Resolved || d.Source.LastHitBy is not { Closed: false, LeavingArea: false } owner || owner.Instance != conn.Instance)
                {
                    ResolveMartyrLocked(conn, d.Source);
                    abandoned.Add((d.Source, false));
                }
            }
        }
        foreach (var (m, deferredDot) in abandoned)
            if (deferredDot) await MonsterKilled(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, m.ObjectId, m, byHit: false);
            else await PresentMartyr(conn, m);
    }
}
