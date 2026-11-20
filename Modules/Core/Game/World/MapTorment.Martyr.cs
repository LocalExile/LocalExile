using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapTorment
{
    sealed record MartyrWeapon(RampageWorld.Actor Actor, SpawnedMonster Monster, (int X, int Y) At)
    {
        public byte[]? Hit;
        public bool Current(ClientState owner) =>
            ReferenceEquals(RampageWeaponAtLocked(owner, Actor, ProjectileWorldTime(owner)), Monster);
    }

    sealed record MartyrViewer(ClientState Player, bool Awake, (int X, int Y)? At, GolemState.Golem[] Minions)
    {
        public readonly List<(uint Id, SpawnedMonster Target, byte[] Packet)> MinionHits = [];
        public readonly List<MartyrWeapon> Weapons = [];
        public MonsterHitOutcome? Hit;
        public int Epoch = Player.IncomingHitEpoch;
    }

    sealed record MartyrDeath(SpawnedMonster Source, (int X, int Y) At, MonsterAttack Attack,
        byte[] Skill, byte[] Action, List<MartyrViewer> Viewers)
    {
        public required ClientState Owner;
        public DotDelivery Dot;
        public bool DotPlayerKill, Resolved, Publishing;
    }

    enum DotDelivery { None, RunnerOutput, OwnerQueue, Accepted }

    // The ordinary 3.29.3c SuicideExplosion action includes equality and the target's signed Positioned radius.
    static bool InMartyrBlast((int X, int Y) from, (int X, int Y) target, int radius, int body)
    {
        long dx = (long)target.X - from.X, dy = (long)target.Y - from.Y, reach = (long)radius + body;
        return dx * dx + dy * dy <= reach * reach;
    }

    // The lethal transition captures the cast and its contacts. The killing transaction resolves them after it has
    // credited the monster, and its output presents them after the killing blow. All state uses the instance's Monsters lock.
    internal static void MartyrDiedLocked(ClientState conn, SpawnedMonster monster) => CaptureMartyrLocked(conn, monster);

    static MartyrDeath? CaptureMartyrLocked(ClientState conn, SpawnedMonster monster)
    {
        uint id = monster.ObjectId;
        if (id == 0 || !monster.Hostile || monster.CorpseConsumed || TormentCatalog.MartyrGrantRow(conn, monster.Mods) is not int row || !CurrentGameWork) return null;
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
            { Spell = true, SourceEffects = source };
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
            var viewer = new MartyrViewer(player, player.MonsterViews.ContainsKey(id) && !player.AsleepMonsters.Contains(id), playerHit ? here : null, minions);
            if (eligible && player.StandingRampageWorld is { Actors.Count: > 0 } world)
            {
                foreach (var actor in world.Actors.Values)
                {
                    if (RampageWeaponAtLocked(player, actor, now) is not { Dead: false, Life: > 0 } weapon) continue;
                    var target = weapon.Walk.Position(now);
                    if (InMartyrBlast(at, target, radius, LivingCollision.Size(weapon))) viewer.Weapons.Add(new(actor, weapon, target));
                }
            }
            viewers.Add(viewer);
        }
        byte actorComponent = checked((byte)origin.Components.ToList().IndexOf("Actor"));
        var death = new MartyrDeath(monster, at, attack, InstanceEntities.ActorSkills(id, actorComponent, [skill]),
            InstanceProtocol.InstantAction(id, skill.Id, at.X, at.Y, triggered: true), viewers) { Owner = conn };
        area.MartyrDeaths.Add(id, death);
        return death;
    }

    static MartyrDeath? PendingMartyr(ClientState conn, SpawnedMonster monster) =>
        monster.CorpseConsumed && conn.Instance is not null && Areas.TryGetValue(conn.Instance, out var area)
            ? area.MartyrDeaths.GetValueOrDefault(monster.ObjectId) : null;

    internal static bool HoldsCorpseLocked(ClientState conn, SpawnedMonster monster) => PendingMartyr(conn, monster) is not null;

    // The runner writes the lethal ailment records before handing off kill credit. Keep that same claim across
    // a runner transfer, including when the original runner resumes after the replacement has finished the kill.
    internal static Func<Task>? CaptureMartyrDotLocked(ClientState runner, SpawnedMonster monster, bool playerKill)
    {
        if (CaptureMartyrLocked(runner, monster) is not { } death) return null;
        death.Dot = DotDelivery.RunnerOutput;
        death.DotPlayerKill = playerKill;
        return () =>
        {
            lock (runner.Monsters)
            {
                if (death.Dot != DotDelivery.RunnerOutput || runner.Closed || runner.LeavingArea) return Task.CompletedTask;
                if (!ClaimDotLocked(runner, death)) return Task.CompletedTask;
            }
            return FinishDotKill(runner, death);
        };
    }

    static bool ClaimDotLocked(ClientState runner, MartyrDeath death)
    {
        if (death.Dot == DotDelivery.RunnerOutput && death.DotPlayerKill
            && death.Source.LastHitBy is { Closed: false, LeavingArea: false } by && by != runner && by.Instance == runner.Instance)
        {
            death.Owner = by;
            death.Dot = DotDelivery.OwnerQueue;
            by.KillShares.Enqueue((_, _, _) =>
            {
                lock (by.Monsters)
                {
                    if (death.Dot != DotDelivery.OwnerQueue || by.Closed || by.LeavingArea) return Task.CompletedTask;
                    death.Dot = DotDelivery.Accepted;
                }
                return FinishDotKill(by, death);
            });
            return false;
        }
        death.Owner = runner;
        death.Dot = DotDelivery.Accepted;
        return true;
    }

    static Task FinishDotKill(ClientState owner, MartyrDeath death) =>
        MonsterKilled(owner.Wire.Remote, owner.Wire.Stream, owner.Wire.Ciphers, owner, death.Source.ObjectId, death.Source,
            byMinion: !death.DotPlayerKill, byHit: !death.DotPlayerKill);

    internal static bool ResolveMartyrLocked(ClientState conn, SpawnedMonster monster)
    {
        if (PendingMartyr(conn, monster) is not { Resolved: false } death) return false;
        death.Owner = conn;
        death.Resolved = true;
        foreach (var v in death.Viewers)
        {
            var p = v.Player;
            if (p.Closed || p.LeavingArea || v.At is null && v.Minions.Length == 0 && v.Weapons.Count == 0) continue;
            double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
            using var defenceTime = FortificationOf(p).Capture(now);
            foreach (var g in v.Minions)
                GolemTakeHitLocked(p, g, monster.ObjectId, monster, death.Attack, now, area: true, sourceHits: v.MinionHits);
            foreach (var weapon in v.Weapons)
            {
                if (weapon.Monster.Dead || weapon.Monster.Life == 0 || !weapon.Current(p)) continue;
                var (critical, taken) = HitRampageWeaponLocked(monster, weapon.Monster, death.Attack.Damage, attack: false, now, death.Attack.SourceEffects);
                weapon.Hit = InstanceProtocol.ActionHit(monster.ObjectId, 0xffff, weapon.Actor.Id,
                    flags: MonsterHitFlags(blocked: false, critical), code: HitCode(taken),
                    angle: HitAngle(weapon.At.X - death.At.X, weapon.At.Y - death.At.Y));
            }
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
            var instance = conn.Instance;
            await Task.WhenAll(death.Viewers.Select(v => PublishToMember(instance, v.Player, () => Present(v))));
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
                    foreach (var weapon in v.Weapons)
                        if (weapon.Hit is { } packet && weapon.Current(p)) records.Add(packet);
                    if (v.Hit is { } hit && p.IncomingHitEpoch == v.Epoch) records.Add(hit.HitPacket);
                    records.Add(InstanceProtocol.LifeUpdate(id, 0));
                    return [.. records];
                }
            }, still: Here);
            foreach (var weapon in v.Weapons)
                if (weapon.Hit is not null && Here()) await SendWorldRecord(p, weapon.Actor.Id, () =>
                {
                    lock (p.Monsters) return weapon.Current(p) ? InstanceProtocol.LifeUpdate(weapon.Actor.Id, weapon.Monster.Life) : null;
                }, still: Here);
            if (v.Hit is { } outcome && Here() && p.IncomingHitEpoch == v.Epoch)
                await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
    }

    // A retired killer no longer drains its output. Unclaimed shared DoT kills still need ordinary kill credit.
    static async Task FinishAbandonedMartyrs(ClientState conn, Area area)
    {
        List<(MartyrDeath Death, bool OwesKill)> abandoned;
        lock (conn.Monsters)
        {
            if (area.MartyrDeaths.Count == 0 || !IsMonsterRunner(conn)) return;
            abandoned = [];
            foreach (var d in area.MartyrDeaths.Values)
            {
                if (d.Publishing || !d.Owner.Closed && !d.Owner.LeavingArea) continue;
                if (d.Dot is DotDelivery.RunnerOutput or DotDelivery.OwnerQueue)
                {
                    if (ClaimDotLocked(conn, d)) abandoned.Add((d, true));
                }
                else if (d.Resolved || d.Source.LastHitBy is not { Closed: false, LeavingArea: false } owner || owner.Instance != conn.Instance)
                {
                    ResolveMartyrLocked(conn, d.Source);
                    abandoned.Add((d, false));
                }
            }
        }
        foreach (var (death, owesKill) in abandoned)
            if (owesKill) await FinishDotKill(conn, death);
            else await PresentMartyr(conn, death.Source);
    }
}
