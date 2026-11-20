using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using DotDelivery = MonsterDeathCast.KillDelivery;
using static Game;
using static Program;

public static partial class MapTorment
{
    internal static bool CurrentWeapon(ClientState owner, MonsterDeathCast.Weapon weapon) =>
        ReferenceEquals(RampageWeaponAtLocked(owner, weapon.Id, weapon.Epoch, ProjectileWorldTime(owner)), weapon.Monster);

    // The ordinary 3.29.3c SuicideExplosion action includes equality and the target's signed Positioned radius.
    static bool InMartyrBlast((int X, int Y) from, (int X, int Y) target, int radius, int body)
    {
        long dx = (long)target.X - from.X, dy = (long)target.Y - from.Y, reach = (long)radius + body;
        return dx * dx + dy * dy <= reach * reach;
    }

    // The lethal transition captures the cast and its contacts. The killing transaction resolves them after it has
    // credited the monster, and its output presents them after the killing blow. All state uses the instance's Monsters lock.
    internal static void MartyrDiedLocked(ClientState conn, SpawnedMonster monster) => CaptureMartyrLocked(conn, monster);

    static MonsterDeathCast? CaptureMartyrLocked(ClientState conn, SpawnedMonster monster)
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
        var area = Of(conn) ?? Areas.GetValue(conn.Instance, instance => new(instance.World.TormentRewards));
        Remark(conn, area, id, monster); // A restored host can die before the runner's first sight check.
        monster.CorpseConsumed = true; // TormentTouchedMonsterMartyr: base_monster_will_be_deleted_on_death.
        var viewers = new List<MonsterDeathCast.Viewer>();
        foreach (var player in Members(conn).Where(p => p.PlayerSent))
        {
            bool eligible = !player.Paused && !InGracePeriod(player);
            var here = player.Walk.Position(now);
            bool playerHit = eligible && player.PlayerLife > 0 && !player.Immune && InMartyrBlast(at, here, radius, LivingCollision.PlayerSize);
            GolemState.Golem[] minions = eligible
                ? [.. player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values)
                    .Where(g => !g.Monster.Dead && InMartyrBlast(at, g.Monster.Walk.Position(now), radius, LivingCollision.Size(g.Monster)))] : [];
            var viewer = new MonsterDeathCast.Viewer(player, player.MonsterViews.ContainsKey(id) && !player.AsleepMonsters.Contains(id), playerHit ? here : null, minions);
            if (eligible && player.StandingRampageWorld is { Actors.Count: > 0 } world)
            {
                foreach (var actor in world.Actors.Values)
                {
                    if (RampageWeaponAtLocked(player, actor.Id, actor.Epoch, now) is not { Dead: false, Life: > 0 } weapon) continue;
                    var target = weapon.Walk.Position(now);
                    if (InMartyrBlast(at, target, radius, LivingCollision.Size(weapon))) viewer.Weapons.Add(new(actor.Id, actor.Epoch, weapon, target));
                }
            }
            viewers.Add(viewer);
        }
        byte actorComponent = checked((byte)origin.Components.ToList().IndexOf("Actor"));
        var death = new MonsterDeathCast(monster, at, attack, InstanceEntities.ActorSkills(id, actorComponent, [skill]),
            InstanceProtocol.InstantAction(id, skill.Id, at.X, at.Y, triggered: true), viewers, conn, CombatLifetime!.Generation);
        conn.Instance.World.DeathCasts.Add(id, death);
        return death;
    }

    static MonsterDeathCast? PendingMartyr(ClientState conn, SpawnedMonster monster) =>
        monster.CorpseConsumed && conn.Instance?.World.DeathCasts.GetValueOrDefault(monster.ObjectId) is { } cast && cast.Source == monster
            ? cast : null;

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
                if (!CurrentGameRules || death.Dot != DotDelivery.RunnerOutput || runner.Closed || runner.LeavingArea) return Task.CompletedTask;
                if (!ClaimDotLocked(runner, death)) return Task.CompletedTask;
            }
            return FinishDotKill(runner, death);
        };
    }

    static bool ClaimDotLocked(ClientState runner, MonsterDeathCast death)
    {
        death.OwnerGeneration = CombatLifetime!.Generation;
        if (death.Dot == DotDelivery.RunnerOutput && death.DotPlayerKill
            && death.Source.LastHitBy is { Closed: false, LeavingArea: false } by && by != runner && by.Instance == runner.Instance)
        {
            death.Owner = by;
            death.Dot = DotDelivery.OwnerQueue;
            by.KillShares.Enqueue((_, _, _) =>
            {
                lock (by.Monsters)
                {
                    if (!CurrentGameRules || death.Dot != DotDelivery.OwnerQueue || by.Closed || by.LeavingArea) return Task.CompletedTask;
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

    static Task FinishDotKill(ClientState owner, MonsterDeathCast death) =>
        MonsterKilled(owner.Wire.Remote, owner.Wire.Stream, owner.Wire.Ciphers, owner, death.Source.ObjectId, death.Source,
            byMinion: !death.DotPlayerKill, byHit: !death.DotPlayerKill);

    internal static bool CompleteMartyrKillLocked(ClientState conn, SpawnedMonster monster)
    {
        if (PendingMartyr(conn, monster) is not { Resolved: false } death) return false;
        // An asynchronous ordinary kill can finish its rewards after its Game module was replaced.
        death.KillCredited = true;
        return TryResolveMartyrLocked(conn, death);
    }

    static bool TryResolveMartyrLocked(ClientState conn, MonsterDeathCast death)
    {
        // Reuse the ordinary hit rules and their current defences for the whole synchronous contact.
        // If the host is switching, the map runner can take the credited death on its next tick.
        if (!CurrentGameWork || !CurrentGameRules || !CombatLifetime!.TryRunSynchronous(() =>
        {
            death.Owner = conn;
            death.OwnerGeneration = CombatLifetime.Generation;
            // The source's projectile or player loop can retire after admission. Each contact below
            // belongs to its own current recipient in this instance.
            var previous = ProjectileOutputGuard.Value;
            ProjectileOutputGuard.Value = null;
            try { ResolveContactsLocked(conn.Instance, death); }
            finally { ProjectileOutputGuard.Value = previous; }
            death.Resolved = true;
        }))
        {
            death.OutputDue = true;
            return false;
        }
        return true;
    }

    static void ResolveContactsLocked(InstanceState instance, MonsterDeathCast death)
    {
        var monster = death.Source;
        foreach (var v in death.Viewers)
        {
            var p = v.Player;
            if (p.Closed || p.LeavingArea || p.Instance != instance || v.At is null && v.Minions.Length == 0 && v.Weapons.Count == 0) continue;
            double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
            using var defenceTime = FortificationOf(p).Capture(now);
            foreach (var g in v.Minions)
                GolemTakeHitLocked(p, g, monster.ObjectId, monster, death.Attack, now, area: true, sourceHits: v.MinionHits);
            foreach (var weapon in v.Weapons)
            {
                if (weapon.Monster.Dead || weapon.Monster.Life == 0 || !CurrentWeapon(p, weapon)) continue;
                var (critical, taken) = HitRampageWeaponLocked(monster, weapon.Monster, death.Attack.Damage, attack: false, now, death.Attack.SourceEffects);
                weapon.Hit = InstanceProtocol.ActionHit(monster.ObjectId, 0xffff, weapon.Id,
                    flags: MonsterHitFlags(blocked: false, critical), code: HitCode(taken),
                    angle: HitAngle(weapon.At.X - death.At.X, weapon.At.Y - death.At.Y));
            }
            if (v.At is { } at && p.PlayerLife > 0 && p.IncomingHitEpoch == v.Epoch)
            {
                if (CommitMonsterHitLocked(p.Wire.Remote, p, monster.ObjectId, monster, death.Attack, now, at, death.At, checkRange: false) is { } hit)
                    v.Hit = new(hit.HitPacket, hit.Life, hit.Mana, hit.ManaBefore, hit.Damage, hit.RawDamage, hit.Blocked, hit.Critical,
                        hit.Recovery is { } r ? (r.Epoch, r.Action, r.Until, r.Seconds) : null, hit.Armour, hit.Spirit);
                v.Epoch = p.IncomingHitEpoch;
            }
        }
    }

    internal static async Task PresentMartyr(ClientState conn, SpawnedMonster monster)
    {
        var instance = conn.Instance;
        uint id = monster.ObjectId;
        MonsterDeathCast death;
        long generation;
        lock (instance.World.Monsters)
        {
            if (!CurrentGameRules || PendingMartyr(conn, monster) is not { Resolved: true } pending) return;
            generation = CombatLifetime!.Generation;
            if (pending.Publisher == generation) return;
            death = pending;
            death.Publisher = generation;
            death.OutputDue = true;
        }
        try
        {
            await Task.WhenAll(death.Viewers.Select(v => PublishToMember(instance, v.Player, () => PresentDeathCast(instance, death, v))));
            ClientState[] removals;
            lock (instance.World.Monsters)
            {
                if (!CurrentGameRules || death.Publisher != generation
                    || death.Viewers.Any(v => !v.Presented && Here(v.Player))) return;
                death.Removals ??= RetireMonsterLocked(instance, id, Members(conn, instance).Concat(death.Viewers.Select(v => v.Player)));
                removals = [.. death.Removals];
            }
            await Task.WhenAll(removals.Select(p => PublishToMember(instance, p, () =>
                SendWorldRemoval(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p, id, "[torment] Martyr's Touch exploded", atGate: () =>
                {
                    lock (instance.World.Monsters) return death.Removals.Remove(p) ? InstanceProtocol.RemoveObject(id) : null;
                }))));
            lock (instance.World.Monsters)
            {
                if (!CurrentGameRules || death.Publisher != generation) return;
                death.Removals.RemoveWhere(p => LeftInstance(instance, p));
                if (death.Removals.Count == 0) instance.World.DeathCasts.Remove(id);
            }
        }
        finally
        {
            lock (instance.World.Monsters) if (death.Publisher == generation) death.Publisher = 0;
        }
        bool Here(ClientState p) => !p.LeavingArea && !LeftInstance(instance, p);
    }

    // An unclaimed ailment kill still needs ordinary credit. Already resolved deaths retain their pending output
    // while the map runner changes or the Game assembly is replaced. The original generation owns its output order.
    static async Task FinishAbandonedMartyrs(ClientState conn)
    {
        List<(MonsterDeathCast Death, bool OwesKill)> abandoned;
        lock (conn.Monsters)
        {
            var deaths = conn.Instance.World.DeathCasts;
            if (deaths.Count == 0 || !CurrentGameRules || !IsMonsterRunner(conn)) return;
            long generation = CombatLifetime!.Generation;
            abandoned = [];
            foreach (var d in deaths.Values)
            {
                bool replaced = d.OwnerGeneration != generation;
                if (d.Publisher == generation || !d.OutputDue && !replaced && !d.Owner.Closed && !d.Owner.LeavingArea) continue;
                if (d.Dot is DotDelivery.RunnerOutput or DotDelivery.OwnerQueue)
                {
                    if (replaced) d.Dot = DotDelivery.RunnerOutput;
                    if (ClaimDotLocked(conn, d)) abandoned.Add((d, true));
                }
                else if (d.KillCredited || d.Resolved || d.Source.LastHitBy is not { Closed: false, LeavingArea: false } owner || owner.Instance != conn.Instance)
                {
                    if (d.Resolved || TryResolveMartyrLocked(conn, d)) abandoned.Add((d, false));
                }
            }
        }
        foreach (var (death, owesKill) in abandoned)
            if (owesKill) await FinishDotKill(conn, death);
            else await PresentMartyr(conn, death.Source);
    }

}

public static partial class Game
{
    internal static async Task PresentDeathCast(InstanceState instance, MonsterDeathCast death, MonsterDeathCast.Viewer viewer)
    {
        var p = viewer.Player;
        bool Here() => CurrentGameRules && !p.Closed && !p.LeavingArea && !p.WorldOutput.Closed && p.Instance == instance;
        bool playerHit, flask;
        lock (instance.World.Monsters)
        {
            if (viewer.Presented || !Here()) return;
            playerHit = viewer.Hit is not null && viewer.Epoch == p.IncomingHitEpoch;
            flask = playerHit && viewer.Hit!.Life > 0;
        }
        if (flask) await p.FlaskBarGate.WaitAsync();
        try
        {
            WorldObject[] required;
            lock (instance.World.Monsters)
            {
                if (!Here()) return;
                required = p.LockstepClient && playerHit ? [p.WorldOutput.PublishedObject(PlayerId)] : [];
            }
            await SendWorldBatch(p, required, admission =>
            {
                lock (instance.World.Monsters)
                {
                    if (viewer.Presented || !Here()) return null;
                    uint id = death.Source.ObjectId;
                    var records = new WorldRecordBuilder(p, admission);
                    if (viewer.Awake && p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id))
                    {
                        records.Presentation(id, death.Skill);
                        records.Presentation(id, death.Action);
                        foreach (var contact in viewer.MinionHits)
                            if (p.Monsters.TryGetValue(contact.Id, out var current) && current == contact.Target)
                                records.Presentation(id, contact.Packet);
                        foreach (var weapon in viewer.Weapons)
                            if (weapon.Hit is { } packet && MapTorment.CurrentWeapon(p, weapon)) records.Presentation(id, packet);
                        if (viewer.Hit is { } hit && p.IncomingHitEpoch == viewer.Epoch) records.Presentation(id, hit.HitPacket);
                        records.Presentation(id, InstanceProtocol.LifeUpdate(id, 0));
                    }
                    foreach (var weapon in viewer.Weapons)
                        if (weapon.Hit is not null && MapTorment.CurrentWeapon(p, weapon))
                            records.Presentation(weapon.Id, InstanceProtocol.LifeUpdate(weapon.Id, weapon.Monster.Life));
                    if (viewer.Hit is { } h && p.IncomingHitEpoch == viewer.Epoch)
                    {
                        var hit = new MonsterHitOutcome(h.HitPacket, h.Life, h.Mana, h.ManaBefore, h.Damage, h.RawDamage, h.Blocked, h.Critical,
                            h.Recovery is { } r ? new(r.Epoch, r.Action, r.Until, r.Seconds) : null, h.Armour, h.Spirit);
                        var w = p.Wire;
                        // The receiver's send gate accepted this transaction. Its captured output cannot yield or
                        // return to the old projectile. The recipient can still leave during the commit.
                        var previous = ProjectileOutputGuard.Value;
                        ProjectileOutputGuard.Value = () => !p.Closed && !p.LeavingArea && p.Instance == instance;
                        try { records.Add(CaptureWorldOutput(p, admission, () => PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, hit), flask)); }
                        finally { ProjectileOutputGuard.Value = previous; }
                    }
                    viewer.Presented = true;
                    return records.Build();
                }
            }, still: Here);
        }
        finally { if (flask) p.FlaskBarGate.Release(); }
        if (flask && Here())
        {
            var wire = p.Wire;
            await PublishFlaskChanges(wire.Remote, wire.Stream, wire.Ciphers, p);
        }
    }
}
