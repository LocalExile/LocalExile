using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    static bool GolemEnemyOrderLocked(ClientState conn, uint id, SpawnedMonster enemy, double now, double ownerDistance)
    {
        if (!enemy.Hostile || enemy.Attack is not { } attack || InTown(conn) || InGracePeriod(conn)) return false;
        if (conn.Golems.IncomingHits.Any(h => h.Enemy == id && h.Generation == enemy.ActionGeneration)) return true;
        if (enemy.BusyUntil > now || MonsterActionFactor(enemy, now) == 0) return false;
        var here = enemy.Walk.Position(now);
        var golem = conn.Golems.All.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0)
            .Select(g => (G: g, Distance: Dist(here, g.Monster.Walk.Position(now))))
            .Where(t => t.Distance <= enemy.AggroRange && (t.Distance < ownerDistance || t.G.Taunts.GetValueOrDefault(id) > now))
            .OrderByDescending(t => t.G.Taunts.GetValueOrDefault(id) > now).ThenBy(t => t.Distance).FirstOrDefault();
        if (golem.G is null) return false;
        var to = golem.G.Monster.Walk.Position(now);
        if (golem.Distance > attack.Range + LivingCollision.Size(golem.G.Monster))
        {
            if (enemy.LastTarget is { } old && Dist(old, to) < 5 && now < enemy.Walk.ArrivesAt) return true;
            double factor = golem.Distance > 0 ? Math.Max(1, attack.Range - 1) / golem.Distance : 0;
            var aim = ((int)Math.Round(to.X + (here.X - to.X) * factor), (int)Math.Round(to.Y + (here.Y - to.Y) * factor));
            int generation = ++enemy.ActionGeneration; enemy.LastTarget = to;
            enemy.Walk.Order(aim.Item1, aim.Item2, now);
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionStarted(id, here.X, here.Y, aim.Item1, aim.Item2,
                0x2909, conn.NextActionSequence(), 0x0408), null, id, enemy, Action: generation));
            return true;
        }
        int action = ++enemy.ActionGeneration;
        enemy.BusyUntil = double.PositiveInfinity; enemy.LastTarget = null;
        conn.Golems.IncomingHits.Add(new(id, enemy, golem.G, attack, action));
        conn.Minions.OutputQueue.Add(new([], null, id, enemy, PlayerMinions.OutputKind.GolemIncoming, Action: action));
        return true;
    }

    static byte[]? GolemIncomingPresentation(ClientState conn, PlayerMinions.Output output)
    {
        var hit = conn.Golems.IncomingHits.FirstOrDefault(h => h.Enemy == output.TargetId && h.Generation == output.Action);
        if (hit is null || hit.Started || hit.Monster.Dead || hit.Monster.ActionGeneration != hit.Generation || hit.Target.Monster.Dead) return null;
        double now = AreaTime(conn); var from = hit.Monster.Walk.Position(now); var to = hit.Target.Monster.Walk.Position(now);
        hit.Started = true; hit.ContactAt = now + hit.Attack.Seconds * (hit.Attack.Release?.Fraction ?? .5);
        hit.Monster.BusyUntil = now + hit.Attack.Seconds;
        hit.Monster.Walk.Order(from.X, from.Y, now);
        return InstanceProtocol.ActionStarted(hit.Enemy, from.X, from.Y, to.X, to.Y, hit.Attack.SkillId,
            0xffff, 0x0408, targetObjectId: hit.Target.Id);
    }

    static void GolemIncomingContactsLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        foreach (var hit in conn.Golems.IncomingHits.ToArray())
        {
            if (conn.Monsters.GetValueOrDefault(hit.Enemy) != hit.Monster || hit.Monster.Dead || hit.Monster.ActionGeneration != hit.Generation
                || !GolemCurrent(conn, hit.Target) || hit.Target.Monster.Dead)
            {
                conn.Golems.IncomingHits.Remove(hit);
                if (hit.Monster.ActionGeneration == hit.Generation && double.IsPositiveInfinity(hit.Monster.BusyUntil))
                    hit.Monster.BusyUntil = now;
                continue;
            }
            if (!hit.Started || now < hit.ContactAt) continue;
            conn.Golems.IncomingHits.Remove(hit);
            if (MonsterActionFactor(hit.Monster, now) == 0) continue;
            if (hit.Attack.Projectile is { } projectile)
            {
                var session = ProjectilesFor(remote, stream, ciphers, conn);
                session.Schedule(session.Runtime.Now, () => CurrentGameWork && !conn.Closed && !conn.LeavingArea,
                    () => MonsterProjectileUse(session, hit.Enemy, hit.Monster, hit.Attack, projectile, hit.Generation, hit.Target.Id));
                continue;
            }
            if (Dist(hit.Monster.Walk.Position(now), hit.Target.Monster.Walk.Position(now))
                > hit.Attack.Range + LivingCollision.Size(hit.Target.Monster) + 2) continue;
            GolemTakeHitLocked(conn, hit.Target, hit.Enemy, hit.Monster, hit.Attack, now);
        }
    }

    internal static bool GolemTakeHitLocked(ClientState conn, GolemState.Golem g, uint attacker, SpawnedMonster source, MonsterAttack attack, double now, bool area = false)
    {
        if (!GolemCurrent(conn, g) || g.Monster.Dead || g.Monster.Life == 0) return false;
        var m = g.Monster; var effects = m.Mods.Effects;
        int Player(string name) => GolemStat(g.Player, name);
        if (!attack.Spell)
        {
            int evasion = effects.Evasion(Monsters.BaseEvasion(m.Level) ?? 0);
            int accuracy = source.Mods.Effects.Accuracy(Monsters.BaseAccuracy(source.Level) ?? 0);
            if (Random.Shared.Next(100) >= DamageModel.AccuracyHitChance(evasion, accuracy)) return false;
        }
        int block = attack.Spell ? Player("minion_additional_spell_block_%") : Player("minion_block_%");
        if (Random.Shared.Next(100) < Math.Clamp(block, 0, 75))
        {
            m.Life = (uint)Math.Min(m.MaxLife, m.Life + m.MaxLife * (long)Player("minion_recover_%_of_maximum_life_on_block") / 100
                + Player("minion_recover_X_life_on_block"));
            conn.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Life));
            return false;
        }
        var rolled = MonsterModHit(source, attack.Damage.Roll(Random.Shared), m.Life, m.MaxLife, Random.Shared, out bool critical);
        bool elementalImmune = Player("golem_immunity_to_elemental_damage") > 0
            || g.Definition.Kind is "Flame" or "Ice" or "Lightning" && Player("elemental_golem_immunity_to_elemental_damage") > 0;
        int damage = 0;
        foreach (var (type, range) in rolled.Components)
        {
            if (elementalImmune && type is DamageType.Fire or DamageType.Cold or DamageType.Lightning) continue;
            var resistance = MonsterHitResistance(m, type, default);
            int value = (int)Math.Round(DamageModel.Mitigate(DamagePacket.Of(type, range.Min, range.Min), _ => resistance)[type].Min);
            if (type == DamageType.Physical)
            {
                double armour = Math.Max(0, Player("minion_physical_damage_reduction_rating") + Player("golem_physical_damage_reduction_rating"));
                double reduction = Math.Min(.9, armour / Math.Max(1, armour + 5 * value) + effects.PhysicalReduction / 100.0);
                value = (int)Math.Round(value * (1 - reduction));
            }
            value = (int)Math.Round(value * effects.DamageTaken(attack.Spell)
                * (area ? Math.Max(0, 1 + effects["damage_taken_+%_from_area_hits"] / 100.0) : 1));
            if (type != DamageType.Chaos && g.Shield > 0)
            { double absorbed = Math.Min(g.Shield, value); g.Shield -= absorbed; value -= (int)absorbed; }
            damage += Math.Max(0, value);
        }
        g.ShieldHitAt = now;
        m.Life = (uint)Math.Max(0, (long)m.Life - damage);
        if (!elementalImmune)
        {
            m.Ailments.Inflict(rolled, critical, m.MaxLife, now, n => source.Mods.Effects[n], n => effects[n], Random.Shared);
            double fire = rolled[DamageType.Fire].Min / (critical ? 1.3 : 1);
            int IgniteStat(string n) => n == "critical_ailment_dot_multiplier_+" ? 50
                : IgniteIncreases.Contains(n) ? 0 : source.Mods.Effects[n];
            if (IgniteFromHit(fire, critical, IgniteStat, 1, m, Random.Shared) is { } ignite && g.Burns.Add(now, ignite.DamagePerSecond, ignite.RemainingSeconds))
            {
                if (g.Burns.Buff is ushort old) conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(g.Id, old), null));
                g.Burns.Buff = conn.NextBuff++;
                conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(g.Id, g.Burns.Buff.Value, IgnitedBuffRow, attacker,
                    u68: BitConverter.SingleToUInt32Bits((float)ignite.RemainingSeconds)), null));
            }
        }
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(attacker, 0xffff, g.Id,
            flags: (ushort)(0x4201 | (critical ? 0x20 : 0)), kind: 2, u34: 450), null, g.Id, m));
        conn.Minions.OutputQueue.Add(new([], "golem takes damage", g.Id, m, PlayerMinions.OutputKind.Life));
        if (m.Life == 0) GolemDieLocked(conn, g, now);
        else if (g.Definition.Kind == "Ice" && g.Definition.Special && !g.DeathBurstDone && m.Life * 2L <= m.MaxLife)
        {
            g.DeathBurstDone = true;
            if (g.Abilities.FirstOrDefault(a => a.Effect == 4839) is { } death)
                GolemReserveLocked(conn, g, death, 0, null, m.Walk.Position(now), now);
        }
        else if (HitReaction.TryFlinch(m, new(damage), now, Random.Shared))
        {
            g.Pending = null; g.Pulses.RemoveAll(p => p.Follows);
            conn.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Flinch, Until: m.FlinchUntil, Action: m.ActionGeneration));
        }
        return true;
    }

    static void GolemDieLocked(ClientState conn, GolemState.Golem g, double now)
    {
        if (g.Monster.Dead && double.IsFinite(g.ReviveAt)) return;
        g.Monster.Dead = true; g.Monster.Life = 0; g.Monster.ActionGeneration++; g.Pending = null;
        foreach (var hazard in g.Hazards) if (hazard.Object is uint obj) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(obj), null));
        g.Hazards.Clear(); g.Pulses.Clear(); g.Burns.Clear();
        // Local gameplay rule: a successfully activated golem returns after two game seconds while its source remains valid.
        g.ReviveAt = now + 2;
        conn.Golems.RefreshAt = 0;
        conn.Minions.OutputQueue.Add(new([], "golem died", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
    }

    static int GolemSafeguardLocked(ClientState conn, int damage, double now)
    {
        if (damage <= 0) return damage;
        var candidates = conn.Golems.All.Values.Where(g => !g.Monster.Dead && g.Definition.Kind == "Stone" && g.Definition.Special)
            .Select(g => (G: g, Pct: GolemStat(g.OwnerBuff, "melee_damage_removed_from_stone_golem_before_life_or_es_%"))).ToArray();
        if (candidates.Length == 0) return damage;
        int total = Math.Min(damage, (int)((long)damage * Math.Clamp(candidates.Max(g => g.Pct), 0, 100) / 100));
        int left = total;
        foreach (var (g, _) in candidates)
        {
            int share = Math.Min(left, (int)g.Monster.Life); g.Monster.Life -= (uint)share; left -= share;
            conn.Minions.OutputQueue.Add(new([], "stone golem safeguards owner", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
            if (g.Monster.Life == 0) GolemDieLocked(conn, g, now);
            if (left == 0) break;
        }
        return damage - (total - left);
    }
}
