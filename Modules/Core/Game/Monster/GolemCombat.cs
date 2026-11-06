using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    static bool GolemCurrent(ClientState conn, GolemState.Golem g) => CurrentGameWork && !conn.Closed && !conn.LeavingArea
        && conn.Golems.All.GetValueOrDefault(g.Id) == g && conn.Monsters.GetValueOrDefault(g.Id) == g.Monster;

    static void GolemStepLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GolemState.Golem g, double now)
    {
        var m = g.Monster;
        if (!GolemCurrent(conn, g) || m.Dead || conn.PlayerLife == 0) return;
        double elapsed = Math.Max(0, now - g.LastStep); g.LastStep = now;
        int Own(string n) => GolemStat(g.Own, n);
        int Player(string n) => GolemStat(g.Player, n);
        double regen = (g.Definition.Kind == "Stone" ? .02 : .01) +
            (Player("minion_life_regeneration_rate_per_minute_%") + Own("minion_life_regeneration_rate_per_minute_%") + Player("golem_life_regeneration_per_minute_%")) / 6000.0;
        g.Regeneration += elapsed * (m.MaxLife * regen + Player("minion_base_life_regeneration_rate_per_minute") / 60.0
            + Player("minion_life_regeneration_rate_per_second"));
        int heal = (int)Math.Min(int.MaxValue, g.Regeneration); g.Regeneration -= heal;
        if (heal > 0 && m.Life < m.MaxLife)
        {
            m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + heal);
            conn.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Life));
        }
        if (now - g.ShieldHitAt > 2) g.Shield = Math.Min(g.MaximumShield, g.Shield + elapsed * g.MaximumShield / 3);
        if (!g.Burns.Idle)
        {
            int burning = g.Burns.Advance(now, MonsterDotTaken(m, DamageType.Fire, now));
            m.Life = (uint)Math.Max(0, (long)m.Life - burning);
            if (burning > 0) conn.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Life));
            if (!g.Burns.Burning && g.Burns.Buff is ushort old)
            { conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(g.Id, old), null)); g.Burns.Buff = null; }
            if (m.Life == 0) { GolemDieLocked(conn, g, now); return; }
        }
        GolemHazardsLocked(remote, stream, ciphers, conn, g, now);
        if (MonsterActionFactor(m, now) == 0)
        {
            g.Pending = null; g.Pulses.RemoveAll(p => p.Follows);
            if (double.IsPositiveInfinity(m.BusyUntil)) m.BusyUntil = now;
            m.Walk.Order(m.Walk.Position(now).X, m.Walk.Position(now).Y, now);
            return;
        }
        GolemPulsesLocked(remote, stream, ciphers, conn, g, now);
        if (g.Pending is { } pending)
        {
            if (pending.Generation != m.ActionGeneration)
            {
                g.Pending = null;
                if (double.IsPositiveInfinity(m.BusyUntil)) m.BusyUntil = now;
            }
            else if (pending.Started && now >= pending.At)
            {
                g.Pending = null;
                GolemAbilityContactLocked(remote, stream, ciphers, conn, g, pending, now);
            }
            else return;
        }
        if (now < m.BusyUntil || now < m.FlinchUntil || MonsterActionFactor(m, now) == 0) return;
        m.Walk.SetSpeed(m.Mods.Effects.MovementSpeed(m.Variety!.MovementSpeed) * MonsterSpeedRoll.Factor(m.SpeedRoll), now);
        var here = m.Walk.Position(now); var owner = conn.Walk.Position(now);
        if (Dist(here, owner) > 150)
        {
            var at = NextToPlayer(conn);
            m.Walk = conn.NewMonsterWalk(g.Id, at.X, at.Y, m.Mods.Effects.MovementSpeed(m.Variety!.MovementSpeed)
                * MonsterSpeedRoll.Factor(m.SpeedRoll), LivingCollision.Size(m), false);
            m.ActionGeneration++; m.LastTarget = null;
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(g.Id, at.X, at.Y), "golem follows owner", g.Id, m));
            return;
        }
        if (InTown(conn)) { GolemFollowLocked(conn, g, owner, now); return; }
        if (g.Definition.Kind == "Carrion" && g.Definition.Special && m.Life < m.MaxLife &&
            g.Abilities.FirstOrDefault(a => a.Effect == 10801) is { } consume && GolemReady(g, consume, now))
        {
            var corpse = conn.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Dead && !p.Value.CorpseConsumed
                && Dist(here, p.Value.Walk.Position(now)) <= 15);
            if (corpse.Value is not null)
            {
                corpse.Value.CorpseConsumed = true;
                g.CorpseEmpowered = true; m.Life = (uint)Math.Min(m.MaxLife, m.Life + m.MaxLife / 5);
                GolemReserveLocked(conn, g, consume, corpse.Key, corpse.Value, corpse.Value.Walk.Position(now), now);
                return;
            }
        }
        var target = conn.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0 && !p.Value.Invulnerable
                && !p.Value.Asleep && !conn.AsleepMonsters.Contains(p.Key) && !double.IsPositiveInfinity(p.Value.BusyUntil))
            .Select(p => (p.Key, M: p.Value, At: p.Value.Walk.Position(now)))
            .Where(t => Dist(t.At, here) <= m.AggroRange && Dist(t.At, owner) <= 100)
            .OrderBy(t => Dist(t.At, here)).FirstOrDefault();
        if (target.M is null) { GolemFollowLocked(conn, g, owner, now); return; }
        g.Target = target.Key;
        double distance = Dist(target.At, here);
        var ability = g.Abilities.Where(a => GolemUsable(g, a) && GolemReady(g, a, now)
                && distance <= a.Attack.Range + LivingCollision.Size(target.M))
            .OrderByDescending(a => a.Cooldown > 0).ThenByDescending(a => a.Effect == 10800).FirstOrDefault();
        if (ability is null)
        {
            double range = g.Abilities.Where(a => GolemUsable(g, a)).Select(a => a.Attack.Range).DefaultIfEmpty(8).Min();
            double f = distance > 0 ? Math.Max(0, range - 2) / distance : 0;
            GolemMoveLocked(conn, g, ((int)Math.Round(target.At.X + (here.X - target.At.X) * f),
                (int)Math.Round(target.At.Y + (here.Y - target.At.Y) * f)), now);
            return;
        }
        if (ability.Effect == 10802 && g.Combo >= 3)
            ability = ability with { Attack = ability.Attack with { Seconds = ability.Attack.Seconds / Math.Max(.1,
                1 + GolemStat(ability.Stats, "bone_golem_attack_speed_+%_final_after_third_combo_index") / 100.0) } };
        GolemReserveLocked(conn, g, ability, target.Key, target.M, target.At, now);
    }

    static bool GolemUsable(GolemState.Golem g, GolemState.Ability a) => a.Effect switch
    {
        4839 or 5026 or 5268 or 10801 => false,
        4336 => !g.Definition.Special,
        4337 => g.Definition.Special,
        5069 => !g.Definition.Special,
        10800 => g.CorpseEmpowered,
        10799 => !g.CorpseEmpowered,
        _ => true,
    };

    static bool GolemReady(GolemState.Golem g, GolemState.Ability a, double now)
    {
        if (!g.Charges.TryGetValue(a.Effect, out var charges)) return true;
        while (charges.TryPeek(out double at) && at <= now) charges.Dequeue();
        return charges.Count < a.Uses;
    }

    static void GolemReserveLocked(ClientState conn, GolemState.Golem g, GolemState.Ability ability, uint target,
        SpawnedMonster? victim, (int X, int Y) aim, double now)
    {
        var m = g.Monster;
        int generation = ++m.ActionGeneration;
        g.Pending = new(ability, target, victim, aim, now + ability.Attack.Seconds * ability.Contact, generation);
        m.BusyUntil = double.PositiveInfinity;
        conn.Minions.OutputQueue.Add(new([], $"golem {g.Id:x} uses {ability.Id}", g.Id, m, PlayerMinions.OutputKind.GolemAction, Action: generation));
    }

    static byte[]? GolemActionPresentation(ClientState conn, PlayerMinions.Output output)
    {
        if (output.TargetId is not uint id || !conn.Golems.All.TryGetValue(id, out var g) || !GolemCurrent(conn, g)
            || g.Monster.Dead || g.Pending is not { Started: false } pending || pending.Generation != output.Action
            || pending.Generation != g.Monster.ActionGeneration) return null;
        double now = AreaTime(conn); var from = g.Monster.Walk.Position(now); var a = pending.Ability;
        pending.Started = true; pending.At = now + a.Attack.Seconds * a.Contact;
        g.Monster.BusyUntil = now + a.Attack.Seconds;
        g.Monster.Walk.Order(from.X, from.Y, now);
        if (a.Effect is 349 or 2563)
        {
            double speed = g.Monster.Mods.Effects.MovementSpeed(g.Monster.Variety!.MovementSpeed) * MonsterSpeedRoll.Factor(g.Monster.SpeedRoll);
            if (a.Effect == 2563) speed *= 1 + GolemStat(a.Stats, "cyclone_movement_speed_+%_final") / 100.0;
            g.Monster.Walk.SetSpeed(speed, now);
            g.Monster.Walk.Order(pending.Aim.X, pending.Aim.Y, now);
        }
        if (!g.Charges.TryGetValue(a.Effect, out var charges)) g.Charges[a.Effect] = charges = new();
        double after = charges.TryPeek(out _) ? charges.Last() : now;
        if (a.Cooldown > 0) charges.Enqueue(Math.Max(now, after) + a.Cooldown);
        return [.. InstanceEntities.PlayerSkillsChange(id, GolemNativeSkills(conn, g.Monster.Level, [a]), []),
            .. InstanceProtocol.ActionStarted(id, from.X, from.Y, pending.Aim.X, pending.Aim.Y, a.Skill,
                0xffff, 0x0408, targetObjectId: pending.Target == 0 ? null : pending.Target)];
    }

    static void GolemFollowLocked(ClientState conn, GolemState.Golem g, (int X, int Y) owner, double now)
    {
        double distance = Dist(g.Monster.Walk.Position(now), owner);
        if (distance > 18) GolemMoveLocked(conn, g, NextToPlayer(conn), now);
        else if (now >= g.NextMove && now >= g.Monster.Walk.ArrivesAt)
        {
            int n = (int)((g.Id + (uint)(now / 4)) % 8);
            double angle = n * Math.PI / 4;
            GolemMoveLocked(conn, g, (owner.X + (int)(Math.Cos(angle) * 9), owner.Y + (int)(Math.Sin(angle) * 9)), now);
            g.NextMove = now + 3 + g.Id % 3;
        }
    }

    static void GolemMoveLocked(ClientState conn, GolemState.Golem g, (int X, int Y) to, double now)
    {
        if (now < g.NextMove || g.Monster.LastTarget is { } old && Dist(old, to) < 5 && now < g.Monster.Walk.ArrivesAt) return;
        if (!conn.Layout.WalkableFor(to.X, to.Y, Math.Max(0, LivingCollision.Size(g.Monster))) || conn.Barrier.Contains(to)) return;
        var m = g.Monster; var from = m.Walk.Position(now);
        m.LastTarget = to; g.NextMove = now + .5; int generation = ++m.ActionGeneration;
        m.Walk.Order(to.X, to.Y, now);
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionStarted(g.Id, from.X, from.Y, to.X, to.Y,
            0x2909, conn.NextActionSequence(), 0x0408), null, g.Id, m, Action: generation));
    }

    static void GolemAbilityContactLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        GolemState.Golem g, GolemState.Action action, double now)
    {
        var a = action.Ability; var at = g.Monster.Walk.Position(now);
        if (a.Effect == 5906) { g.WrathUntil = now + GolemStat(a.Stats, "base_skill_effect_duration") / 1000.0; conn.Golems.RefreshAt = 0; return; }
        if (a.Effect == 10801) { conn.Minions.OutputQueue.Add(new([], "golem consumes corpse", g.Id, g.Monster, PlayerMinions.OutputKind.Life)); return; }
        if (a.Effect is 4336 or 4337)
        {
            double dps = GolemStat(a.Stats, "base_chaos_damage_to_deal_per_minute") / 60.0;
            dps *= GolemDamageFactor(g, area: true);
            g.Hazards.Add(new(a.Effect, at, now + GolemStat(a.Stats, "base_skill_effect_duration") / 1000.0, a.Radius,
                dps, DamageType.Chaos, Follows: true, Wither: a.Effect == 4337) { LastAt = now });
            return;
        }
        if (a.Effect == 5908)
        {
            uint obj = conn.NextObjectId++;
            var orb = new EffectObject(action.Aim.X, action.Aim.Y, "Metadata/Effects/Spells/monsters_effects/lightning_elemental/orb/orb.ao");
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), orb.Blob()), "lightning golem orb"));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(obj), null));
            g.Hazards.Add(new(a.Effect, action.Aim, now + 8, 40, 0, DamageType.Lightning) { LastAt = now, Object = obj }); return;
        }
        if (a.Projectile is not null)
        { GolemLaunch(remote, stream, ciphers, conn, g, a, action.Aim, now, nova: a.Effect == 5026); return; }
        if (a.Effect is 3347 or 10799 or 10800)
        {
            double dx = action.Aim.X - at.X, dy = action.Aim.Y - at.Y, length = Math.Max(1, Dist(at, action.Aim));
            int spikes = Math.Clamp(GolemStat(a.Stats, "upheaval_number_of_spikes"), 1, 12);
            for (int i = 0; i < spikes; i++)
            {
                var spikeAt = (at.X + (int)Math.Round(dx / length * (8 + i * 10)), at.Y + (int)Math.Round(dy / length * (8 + i * 10)));
                double when = now + i * .1;
                g.Pulses.Add(new(a, spikeAt, when, 1) { Next = when });
            }
            if (a.Effect == 10800) g.CorpseEmpowered = false;
            GolemPulsesLocked(remote, stream, ciphers, conn, g, now); return;
        }
        if (a.Effect is 2563 or 349)
        {
            double duration = a.Effect == 2563 ? GolemStat(a.Stats, "base_skill_effect_duration") / 1000.0 : Math.Max(.1, g.Monster.BusyUntil - now);
            g.Monster.BusyUntil = now + duration;
            g.Pulses.Add(new(a, at, now + duration, a.Effect == 2563 ? Math.Max(.1, a.Attack.Seconds) : .1, Follows: true, Once: a.Effect == 349) { Next = now });
            GolemPulsesLocked(remote, stream, ciphers, conn, g, now); return;
        }
        bool area = GolemStat(a.Stats, "is_area_damage") != 0 || a.Effect is 136 or 349 or 2563 or 3347 or 10799 or 10800 or 869 or 4839;
        var centre = a.Effect is 3347 or 10799 or 10800 or 869 ? action.Aim : at;
        if (a.Effect == 869)
        {
            var end = action.Aim;
            if (conn.Layout.WalkableFor(end.X, end.Y, Math.Max(0, LivingCollision.Size(g.Monster))))
            {
                g.Monster.Walk = conn.NewMonsterWalk(g.Id, end.X, end.Y, g.Monster.Walk.Speed, LivingCollision.Size(g.Monster), false);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(g.Id, end.X, end.Y), null, g.Id, g.Monster));
                centre = end;
            }
        }
        foreach (var (id, m) in conn.Monsters.ToArray())
        {
            if (!m.Hostile || m.Dead || m.Life == 0) continue;
            if (area ? Dist(m.Walk.Position(now), centre) > a.Radius + LivingCollision.Size(m)
                : id != action.Target || Dist(m.Walk.Position(now), at) > a.Attack.Range + LivingCollision.Size(m) + 2) continue;
            GolemHitLocked(remote, stream, ciphers, conn, g, a, id, m, now);
            if (a.Effect == 136 && Random.Shared.Next(100) < GolemStat(a.Stats, "chance_to_taunt_on_hit_%")) g.Taunts[id] = now + 3;
        }
        if (a.Effect == 5268)
        {
            double dps = GolemStat(a.Stats, "base_fire_damage_to_deal_per_minute") / 60.0 * GolemDamageFactor(g, area: true);
            if (dps > 0) g.Hazards.Add(new(a.Effect, at, now + 5, a.Radius, dps, DamageType.Fire) { LastAt = now });
        }
        if (a.Effect == 4839)
        {
            if (g.Abilities.FirstOrDefault(a => a.Effect == 5026) is { } nova) GolemLaunch(remote, stream, ciphers, conn, g, nova, at, now, nova: true);
            GolemDieLocked(conn, g, now);
        }
        if (a.Effect == 10800) g.CorpseEmpowered = false;
        if (a.Effect == 10802) g.Combo = (g.Combo + 1) % 6;
    }

    static double GolemDamageFactor(GolemState.Golem g, bool area = false) => Math.Max(0, 1 +
        (g.Monster.Mods.Effects["damage_+%"] + (area ? g.Monster.Mods.Effects["area_damage_+%"] : 0)) / 100.0)
        * Math.Max(0, 1 + GolemStat(g.Own, "active_skill_minion_damage_+%_final") / 100.0)
        * Math.Max(0, 1 + GolemStat(g.Own, "support_minion_damage_+%_final") / 100.0)
        * Math.Max(0, 1 + GolemTether(g.Player, "damage") / 100.0);

    static void GolemPulsesLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GolemState.Golem g, double now)
    {
        foreach (var pulse in g.Pulses.ToArray())
        {
            int catchup = 0;
            while (pulse.Next <= Math.Min(now, pulse.Until) && catchup++ < 16)
            {
                var centre = pulse.Follows ? g.Monster.Walk.Position(now) : pulse.At;
                foreach (var (id, m) in conn.Monsters.ToArray())
                {
                    if (!m.Hostile || m.Dead || pulse.Once && pulse.Hit.Contains(id) || Dist(m.Walk.Position(now), centre) > pulse.Ability.Radius + LivingCollision.Size(m)) continue;
                    if (GolemHitLocked(remote, stream, ciphers, conn, g, pulse.Ability, id, m, now)) pulse.Hit.Add(id);
                }
                pulse.Next += pulse.Interval;
            }
            if (pulse.Next > pulse.Until) g.Pulses.Remove(pulse);
        }
    }

    static bool GolemHitLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GolemState.Golem g,
        GolemState.Ability a, uint id, SpawnedMonster target, double now)
    {
        if (!CurrentGameWork || target.Dead || target.Invulnerable || !target.Hostile || conn.Monsters.GetValueOrDefault(id) != target) return false;
        if (!a.Attack.Spell)
        {
            int accuracy = g.Monster.Mods.Effects.Accuracy(Monsters.BaseAccuracy(g.Monster.Level) ?? 0);
            int evasion = UnderBlind(conn, id, target.Mods.Effects.Evasion(Monsters.BaseEvasion(target.Level) ?? 0));
            if (!DamageModel.Hits(DamageModel.AccuracyHitChance(evasion, accuracy), Random.Shared.Next(100))) return false;
        }
        if (MonsterBlocks(target, Random.Shared, a.Attack.Spell)) return false;
        double factor = 1;
        if (g.Definition.Kind == "Carrion")
        {
            var here = g.Monster.Walk.Position(now);
            int nearby = conn.Minions.All.Count(m => !conn.Golems.All.ContainsKey(m.Id) && !m.Monster.Dead && Dist(here, m.Monster.Walk.Position(now)) <= 60);
            factor *= 1 + Math.Min(GolemStat(g.Own, "bone_golem_damage_+%_final_per_non_golem_minion_nearby") * nearby,
                GolemStat(g.Own, "bone_golem_damage_per_non_golem_minion_nearby_maximum_%")) / 100.0;
            if (a.Effect == 10802 && g.Combo >= 3) factor *= 1 + GolemStat(a.Stats, "bone_golem_damage_+%_final_after_third_combo_index") / 100.0;
        }
        int crit = (int)(a.Crit * (1 + g.Monster.Mods.Effects["critical_strike_chance_+%"] / 100.0));
        bool critical = Random.Shared.Next(10_000) < crit;
        if (critical) factor *= 1.3 + g.Monster.Mods.Effects["base_critical_strike_multiplier_+"] / 100.0;
        var rolled = a.Attack.Damage.Scale(factor).Roll(Random.Shared);
        int damage = MonsterTakesRolled(target, rolled, a.Attack.Spell);
        if (ApplyMonsterHitLocked(conn, target, damage, allyAt: g.Monster.Walk.Position(now)) is not { } hit) return false;
        if (hit.StageStarts)
        { CommitBossStage(remote, stream, ciphers, conn, id, target, now); hit = hit with { StageStarts = false }; }
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(g.Id, 0xffff, id, flags: (ushort)(0x4601 | (critical ? 0x20 : 0)), kind: 2,
            u34: 450), null, id, target));
        CommitMinionHitLocked(conn, id, target, hit, damage, now, $"golem {g.Id:x} {a.Id}");
        if (!target.Dead)
        {
            var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(target, type, default));
            target.Ailments.Inflict(mitigated, critical, target.MaxLife, now, n => g.Monster.Mods.Effects[n] + GolemStat(a.Stats, n),
                n => target.Mods.Effects[n], Random.Shared);
            HitDebuffsOnHitLocked(target, n => g.Monster.Mods.Effects[n] + GolemStat(a.Stats, n), a.Attack.Spell, critical, now);
        }
        return true;
    }

    static void GolemLaunch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GolemState.Golem g,
        GolemState.Ability a, (int X, int Y) aim, double now, bool nova = false)
    {
        if (a.Projectile is not { } p) return;
        var session = ProjectilesFor(remote, stream, ciphers, conn); var from = ProjectileSession.Vector(g.Monster.Walk.Position(now));
        var to = nova ? from + new ProjectileVector(1, 0) : ProjectileSession.Vector(aim);
        double distance = Math.Max(1, (to - from).Length);
        var owner = session.ActorKey(g.Id, g.Monster);
        var profile = new ProjectileProfile(p.Metadata)
        {
            Speed = ProjectileSpeed.CellsPerSecond(p.Speed, _ => 0, _ => 0), Radius = p.CollisionRadius,
            Range = p.Lobbed ? distance : Math.Max(60, distance + 10), Lobbed = p.Lobbed,
            Count = nova ? 14 : Math.Max(1, p.Count), SpreadRadians = nova ? Math.PI * 2 : p.SpreadRadians,
            DirectDamage = !p.Lobbed,
            Pierce = a.Effect == 2378 ? int.MaxValue : Math.Max(0, GolemStat(g.Own, "number_of_additional_projectiles_to_pierce")),
            EndArea = p.Lobbed ? new("golem mortar", a.Radius) : null,
            EndAreaReasons = p.Lobbed ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        };
        var offense = new ObjectProjectileOffense(a.Id, (s, contact) =>
        {
            if (!CurrentGameWork || conn.Monsters.GetValueOrDefault(contact.Target.Id) is not { Hostile: true } victim) return ProjectileHitResult.Miss;
            double at = Math.Max(AreaTime(conn), contact.At / (double)ProjectileRuntime.QuantaPerSecond);
            bool hit = GolemHitLocked(remote, stream, ciphers, conn, g, a, contact.Target.Id, victim, at);
            if (hit) s.Enqueue(() => PublishMinionOutcomes(remote, stream, ciphers, conn));
            return hit ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        }) { Quiet = true, Presentation = MonsterProjectilePresentation(SkillsFor(conn).Clips(p.Metadata), distance / profile.Speed) };
        session.Schedule(ProjectileRuntime.Time(now), () => CurrentGameWork && !conn.Closed && !conn.LeavingArea,
            () => new(profile, owner, owner, 1, from, to) { Payload = offense });
    }

    static void GolemMeteorLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GolemState.Golem g, double now)
    {
        if (g.Abilities.FirstOrDefault(a => a.Effect == 5268) is not { } a) return;
        GolemReserveLocked(conn, g, a, 0, null, g.Monster.Walk.Position(now), now);
    }

    static void GolemHazardsLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GolemState.Golem g, double now)
    {
        foreach (var hazard in g.Hazards.ToArray())
        {
            double through = Math.Min(now, hazard.Until);
            double elapsed = Math.Max(0, through - hazard.LastAt); hazard.LastAt = through;
            var centre = hazard.Follows ? g.Monster.Walk.Position(now) : hazard.At;
            foreach (var (id, m) in conn.Monsters.ToArray())
            {
                if (!m.Hostile || m.Dead || m.Invulnerable || Dist(m.Walk.Position(now), centre) > hazard.Radius) continue;
                if (hazard.Effect == 5908)
                {
                    if (now >= g.NextOrb && g.Abilities.FirstOrDefault(a => a.Effect == 5908) is { } orb)
                    { GolemHitLocked(remote, stream, ciphers, conn, g, orb, id, m, now); g.NextOrb = now + 1; }
                    continue;
                }
                if (hazard.Wither && now >= hazard.Carry.GetValueOrDefault(id, double.NegativeInfinity))
                {
                    m.Withered.RemoveAll(w => w.Until <= now);
                    if (m.Withered.Count >= 15) m.Withered.Remove(m.Withered.MinBy(w => w.Until));
                    m.Withered.Add((now + 4, 6 * (1 + GolemStat(g.Own, "minion_withered_effect_+%") / 100.0)));
                    hazard.Carry[id] = now + .3;
                }
                double raw = hazard.DamagePerSecond * elapsed;
                double resisted = DamageModel.Mitigate(DamagePacket.Of(hazard.Type, raw, raw), t => MonsterHitResistance(m, t, default))[hazard.Type].Min;
                double total = resisted * MonsterDamageTaken(m, spell: true, now, chaos: hazard.Type == DamageType.Chaos)
                    + hazard.DamageCarry.GetValueOrDefault(id);
                int damage = (int)Math.Floor(total); hazard.DamageCarry[id] = total - damage;
                if (damage <= 0 || ApplyMonsterHitLocked(conn, m, damage, allyAt: centre) is not { } hit) continue;
                if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
                CommitMinionHitLocked(conn, id, m, hit, damage, now, $"golem {g.Id:x} ground damage");
            }
            if (now >= hazard.Until)
            {
                if (hazard.Object is uint obj) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(obj), null));
                g.Hazards.Remove(hazard);
            }
        }
    }
}
