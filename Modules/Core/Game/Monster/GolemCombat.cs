using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    sealed class GolemWanderClock { public double Next; }
    static readonly RuleTable<GolemState.Golem, GolemWanderClock> GolemWander = new();

    internal static bool GolemCurrent(Player player, GolemState.Golem g) => CurrentGameWork && !player.Closed && !player.LeavingArea
        && (player.Golems.All.GetValueOrDefault(g.Id) == g || player.Reservations.Fighters.GetValueOrDefault(g.Id) == g || player.Summons.Fighters.GetValueOrDefault(g.Id) == g)
        && player.Area.Monsters.GetValueOrDefault(g.Id) == g.Monster;

    public static void GolemStepLocked(Player player, GolemState.Golem g, double now)
    {
        var m = g.Monster;
        if (!GolemCurrent(player, g) || m.Dead || player.PlayerLife == 0) return;
        using PlayerStatScope? dotScope = GolemHasDots(g) ? new PlayerStatScope(player, fresh: true) : null;
        var currentPlayer = dotScope is null ? g.Player : CharacterPlayerStats(player);
        // The half-second source cadence is too slow for an exact eight-second window. Re-evaluate only this golem's
        // timed modifier; the input hash leaves an unchanged actor and its ability objects untouched.
        ExpireMinionChargesLocked(player, g, now);
        RefreshGolemLocked(player, g, currentPlayer, now);
        if (m.Dead || m.Life == 0) return;
        double oldLastStep = g.LastStep;
        double elapsed = Math.Max(0, now - oldLastStep);
        g.Regeneration += Math.Max(0, Math.Min(now, g.RecallUntil) - oldLastStep) * m.MaxLife * g.RecallRate / 6000d;
        g.LastStep = Math.Max(oldLastStep, now);
        int Own(uint n) => GolemStat(g.Own, n);
        int Player(uint n) => GolemStat(g.Player, n);
        double regen = (g.Definition.IsGolem ? g.Definition.Kind == "Stone" ? .02 : .01 : 0) +
            (Player(Stat.MinionLifeRegenerationRatePerMinutePct) + Own(Stat.MinionLifeRegenerationRatePerMinutePct)
                + (g.Definition.IsGolem ? Player(Stat.GolemLifeRegenerationPerMinutePct) : 0)) / 6000.0;
        g.Regeneration += elapsed * (m.MaxLife * regen + Player(Stat.Of("minion_base_life_regeneration_rate_per_minute")) / 60.0
            + Player(Stat.MinionLifeRegenerationRatePerSecond) + m.Mods.Effects[Stat.BaseLifeRegenerationRatePerMinute] / 60.0
            + (now < g.HolyRegenerationUntil ? g.HolyRegenerationPerSecond : 0)
            + (now - g.BlockedAt <= 4 ? m.MaxLife * m.Mods.Effects[Stat.LifeRegenerationRatePerMinutePctIfBlockedRecently] / 6000d : 0));
        int heal = (int)Math.Min(int.MaxValue, g.Regeneration); g.Regeneration -= heal;
        if (heal > 0 && m.Life < m.MaxLife)
        {
            m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + heal);
            player.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Life));
        }
        GolemDotsLocked(player, g, oldLastStep, now);
        if (m.Dead || m.Life == 0 || MinionInstabilityLocked(player, g, now)) return;
        g.ShieldPool.Advance(oldLastStep, now, name => m.Mods.Effects[name], alive: true);
        PublishEnergyShieldLocked(player, g);
        GolemHazardsLocked(player, g, now);
        if (MonsterActionFactor(m, now) == 0)
        {
            g.Pending = null; g.Repeat = null; g.Pulses.RemoveAll(p => p.Follows);
            if (double.IsPositiveInfinity(m.BusyUntil)) m.BusyUntil = now;
            m.Walk.Order(m.Walk.Position(now).X, m.Walk.Position(now).Y, now);
            return;
        }
        GolemPulsesLocked(player, g, now);
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
                GolemAbilityContactLocked(player, g, pending, now);
                if (pending.RepeatsRemaining > 0 && !m.Dead && pending.Generation == m.ActionGeneration)
                    g.Repeat = pending;
            }
            else return;
        }
        if (now < m.BusyUntil || now < m.FlinchUntil || MonsterActionFactor(m, now) == 0) return;
        if (RepeatMinionAttackLocked(player, g, now)) return;
        m.Walk.SetSpeed(m.Mods.Effects.MovementSpeed(m.Variety!.MovementSpeed) * MonsterSpeedRoll.Factor(m.SpeedRoll), now);
        var here = m.Walk.Position(now); var owner = player.Walk.Position(now);
        var (aggressive, defensive) = MinionStance(g.Own, g.Player, g.Definition.IsGolem, now - g.BlockedAt is >= 0 and <= 4);
        bool follows = !player.Summons.Definitions.TryGetValue(g.Id, out var summon) || summon.Follows;
        follows |= defensive && summon?.Family != "Clone";
        if (follows && Dist(here, owner) > 150)
        {
            var at = NextToPlayer(player, LivingCollision.Size(m), g.Id);
            m.Walk = player.NewMonsterWalk(g.Id, at.X, at.Y, m.Mods.Effects.MovementSpeed(m.Variety!.MovementSpeed)
                * MonsterSpeedRoll.Factor(m.SpeedRoll), LivingCollision.Size(m), false);
            m.ActionGeneration++; m.LastTarget = null;
            player.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(g.Id, at.X, at.Y), "golem follows owner", g.Id, m, WorldId: g.Id));
            return;
        }
        if (InTown(player)) { GolemFollowLocked(player, g, owner, now); return; }
        if (g.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal))
        { SkitterStepLocked(player, g, here, owner, now); return; }
        if (g.Definition.Kind == "HolyRelic")
        { GolemFollowLocked(player, g, owner, now); return; }
        if (ReaperConsumeLocked(player, g, now)) return;
        if (g.Definition.Id == "SummonReaperAltY")
        {
            GolemFollowLocked(player, g, owner, now);
            return;
        }
        if (g.Definition.Kind == "Carrion" && g.Definition.Special && m.Life < m.MaxLife &&
            g.Abilities.FirstOrDefault(a => a.Effect == 10801) is { } consume && GolemReady(g, consume, now))
        {
            var corpse = player.Area.MonstersNear(here, 15).FirstOrDefault(p => p.Value.Hostile && p.Value.Dead && !p.Value.CorpseConsumed
                && Dist(here, p.Value.Walk.Position(now)) <= 15);
            if (corpse.Value is not null)
            {
                corpse.Value.CorpseConsumed = true;
                g.CorpseEmpowered = true; m.Life = (uint)Math.Min(m.MaxLife, m.Life + m.MaxLife / 5);
                GolemReserveLocked(player, g, consume, corpse.Key, corpse.Value, corpse.Value.Walk.Position(now), now);
                return;
            }
        }
        double aggroRange = aggressive ? Math.Max(m.AggroRange, AggressiveMinionMinimumRange) : m.AggroRange;
        double ownerRange = defensive ? Math.Min(DefensiveMinionMaximumRange, aggroRange) : 100;
        var target = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0 && !p.Value.Invulnerable
                && !p.Value.Asleep && !MonsterAsleep(player, p.Key) && !double.IsPositiveInfinity(p.Value.BusyUntil))
            .Select(p => (p.Key, M: p.Value, At: p.Value.Walk.Position(now)))
            .Where(t => IsPrey(player, g.Own, t.Key, t.M, now) || Dist(t.At, here) <= aggroRange
                && (!(follows || defensive) || Dist(t.At, owner) <= ownerRange))
            .OrderByDescending(t => IsPrey(player, g.Own, t.Key, t.M, now)).ThenBy(t => Dist(t.At, here)).FirstOrDefault();
        if (target.M is null) { if (follows) GolemFollowLocked(player, g, owner, now); return; }
        g.Target = target.Key;
        double distance = Dist(target.At, here);
        if (aggressive && g.Definition.Id == "SummonSkeletons"
            && g.Abilities.FirstOrDefault(a => a.Effect == 10844) is { } dash && GolemReady(g, dash, now)
            && g.Abilities.FirstOrDefault(a => a.Effect == 406) is { } swing
            && distance > swing.Attack.Range + LivingCollision.Size(target.M) && distance <= dash.Attack.Range)
        {
            double fraction = Math.Max(0, swing.Attack.Range - 1) / distance;
            var landing = ((int)Math.Round(target.At.X + (here.X - target.At.X) * fraction),
                (int)Math.Round(target.At.Y + (here.Y - target.At.Y) * fraction));
            if (GolemDashPath(player, m, here, landing))
            {
                GolemReserveLocked(player, g, dash, 0, null, landing, now);
                return;
            }
        }
        var ability = g.Abilities.Where(a => (g.Definition.IsGolem || a.Attack.Damage.MaxTotal > 0) && GolemUsable(g, a) && GolemReady(g, a, now)
                && distance <= a.Attack.Range + LivingCollision.Size(target.M))
            .OrderByDescending(a => a.Cooldown > 0).ThenByDescending(a => a.Effect == 10800).FirstOrDefault();
        if (g.Definition.Kind == "Spectre"
            && g.Abilities.FirstOrDefault(a => SpectreChargeRoar(g, a.Effect) is not null && GolemReady(g, a, now)) is { } roar)
        {
            GolemReserveLocked(player, g, roar, 0, null, here, now);
            return;
        }
        if (ability is null)
        {
            double range = g.Abilities.Where(a => GolemUsable(g, a)).Select(a => a.Attack.Range).DefaultIfEmpty(8).Min();
            double f = distance > 0 ? Math.Max(0, range - 2) / distance : 0;
            GolemMoveLocked(player, g, ((int)Math.Round(target.At.X + (here.X - target.At.X) * f),
                (int)Math.Round(target.At.Y + (here.Y - target.At.Y) * f)), now);
            return;
        }
        if (ability.Effect == 10802 && g.Combo >= 3)
            ability = ability with { Attack = ability.Attack with { Seconds = ability.Attack.Seconds / Math.Max(.1,
                1 + GolemStat(ability.Stats, Stat.BoneGolemAttackSpeedPlusPctFinalAfterThirdComboIndex) / 100.0) } };
        GolemReserveLocked(player, g, ability, target.Key, target.M, target.At, now);
    }

    static bool GolemDashPath(Player player, SpawnedMonster m, (int X, int Y) from, (int X, int Y) to) =>
        ClientPathfinder.LineBelow(from, to, (x, y) =>
            player.Layout.WalkableFor(x, y, LivingCollision.Size(m)) && !player.Barrier.Contains((x, y)) ? 0 : 1, 1);

    static bool GolemUsable(GolemState.Golem g, GolemState.Ability a) =>
        g.Definition.Kind == "Phantasm" ? a.Effect is 9427 or 9428
        : g.Definition.Kind == "Absolution" ? a.Effect is 11994 or 11995
        : g.Definition.Kind == "AnimatedGuardian" ? a.Effect == (g.Definition.Id == "AnimateGuardianAltX" ? 9638 : 406)
        : g.Definition.Kind == "Zombie" ? g.Definition.Id switch
        {
            "RaiseZombieAltX" => a.Effect is 406 or 5590,
            "RaiseZombieAltY" => a.Effect == 7133,
            _ => a.Effect == 406,
        } : a.Effect switch
    {
        4839 or 5026 or 5268 or 10801 or 10844 or 12032 or 12033 or 10584 => false,
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

    static void GolemReserveLocked(Player player, GolemState.Golem g, GolemState.Ability ability, uint target,
        SpawnedMonster? victim, (int X, int Y) aim, double now)
    {
        var m = g.Monster;
        if (ability.Effect == 306)
        {
            var elements = new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning };
            ability = GolemAbilities(player, m.Variety!, m.Level, g, g.Player, prismatic: elements[AreaRng(player).Next(elements.Length)])
                .Single(a => a.Effect == 306);
        }
        int generation = ++m.ActionGeneration;
        g.Pending = new(ability, target, victim, aim, now + ability.Attack.Seconds * ability.Contact, generation);
        m.BusyUntil = double.PositiveInfinity;
        player.Minions.OutputQueue.Add(new([], $"golem {g.Id:x} uses {ability.Id}", g.Id, m, PlayerMinions.OutputKind.GolemAction, Action: generation));
    }

    static byte[][]? GolemActionRecords(Player player, PlayerMinions.Output output)
    {
        if (output.TargetId is not uint id || OwnedFighter(player, id) is not { } g || !GolemCurrent(player, g)
            || g.Monster.Dead || g.Pending is not { Started: false } pending || pending.Generation != output.Action
            || pending.Generation != g.Monster.ActionGeneration) return null;
        double now = LockstepOrderStart(player, AreaTime(player));
        var from = g.Monster.Walk.Position(now); var a = MinionSupportAction(player, g, pending);
        g.Pending = pending = pending with { Ability = a };
        pending.Started = true; pending.At = now + a.Attack.Seconds * a.Contact;
        g.Monster.BusyUntil = now + a.Attack.Seconds;
        g.Monster.Walk.Order(from.X, from.Y, now);
        if (a.Effect is 349 or 2563)
        {
            double speed = g.Monster.Mods.Effects.MovementSpeed(g.Monster.Variety!.MovementSpeed) * MonsterSpeedRoll.Factor(g.Monster.SpeedRoll);
            if (a.Effect == 2563) speed *= 1 + GolemStat(a.Stats, Stat.CycloneMovementSpeedPlusPctFinal) / 100.0;
            g.Monster.Walk.SetSpeed(speed, now);
            g.Monster.Walk.Order(pending.Aim.X, pending.Aim.Y, now);
        }
        if (!g.Charges.TryGetValue(a.Effect, out var charges)) g.Charges[a.Effect] = charges = new();
        double after = charges.TryPeek(out _) ? charges.Last() : now;
        if (pending.RepeatIndex == 0 && a.Cooldown > 0) charges.Enqueue(Math.Max(now, after) + a.Cooldown);
        // The skeleton dash needs movement presentation. A generic skill start can crash the 3.29.3d client.
        // Contact publishes the authoritative landing position after checking the path again.
        if (a.Effect == 10844)
            return [InstanceProtocol.ActionStarted(id, from.X, from.Y, pending.Aim.X, pending.Aim.Y,
                0x2909, NextMonsterMoveSequence(player, g.Monster), player.MonsterOrderFlags)];
        var action = a.Effect is 9927 or 9928
            ? InstanceProtocol.InstantAction(id, a.Skill, pending.Aim.X, pending.Aim.Y)
            : InstanceProtocol.ActionStarted(id, from.X, from.Y, pending.Aim.X, pending.Aim.Y, a.Skill,
                0xffff, player.MonsterOrderFlags, targetObjectId: pending.Target == 0 ? null : pending.Target);
        return [InstanceEntities.PlayerSkillsChange(id, GolemNativeSkills(player, g.Monster.Level, [a]), []), action];
    }

    // Skitterbots actively seek nearby enemies so their Chilling and Shocking auras reach monsters instead of orbiting
    // the player. Armed traps/mines take priority before enemy-seeking can set NextMove. Prefer enemies not yet
    // affected by this bot's ailment, falling back to the nearest enemy in leash range, and approach an adjacent clear
    // cell rather than the enemy's own solid body.
    static void SkitterStepLocked(Player player, GolemState.Golem g, (int X, int Y) here, (int X, int Y) owner, double now)
    {
        if (PendingSkitterPlaced(player, g, owner, now) is { } pending)
        {
            if (now >= g.NextMove && Dist(here, pending.At) > 8 && SkitterApproachCell(player, g, pending.At) is { } goal)
                GolemMoveLocked(player, g, goal, now);
            return;
        }
        var ailment = g.Definition.Kind == "SkitterCold"
            ? MonsterElementalAilments.Kind.Chill : MonsterElementalAilments.Kind.Shock;
        int auraRadius = SkitterAuraRadius(g);
        bool bestUncovered = false;
        double bestDist = double.MaxValue;
        (int X, int Y) bestAt = default;
        SpawnedMonster? best = null;
        foreach (var (id, monster) in player.Area.Monsters)
        {
            if (monster is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || MonsterAsleep(player, id) || double.IsPositiveInfinity(monster.BusyUntil)) continue;
            var at = monster.Walk.Position(now);
            if (Dist(at, owner) > 100) continue;
            bool uncovered = monster.Ailments.Magnitude(ailment, now) <= 0;
            double dist = Dist(at, here);
            if (best is null || (uncovered && !bestUncovered) || (uncovered == bestUncovered && dist < bestDist))
            {
                best = monster;
                bestUncovered = uncovered;
                bestDist = dist;
                bestAt = at;
            }
        }
        if (best is null) { GolemFollowLocked(player, g, owner, now); return; }
        if (bestDist <= Math.Max(6, auraRadius * 0.5) || now < g.NextMove) return;
        int size = Math.Max(0, LivingCollision.Size(g.Monster));
        int offset = Math.Max(2, Math.Max(0, LivingCollision.Size(best)) + size + 1);
        GolemMoveLocked(player, g, SkitterApproachCell(player, g.Id, here, bestAt, size, offset), now);
    }

    static (int X, int Y) SkitterApproachCell(Player player, uint selfId, (int X, int Y) here, (int X, int Y) target, int size, int offset)
    {
        bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, size) && !player.Barrier.Contains((x, y));
        var blockers = player.MonsterBlockers(selfId);
        int w = player.Layout.GridW > 0 ? player.Layout.GridW : int.MaxValue;
        int h = player.Layout.GridH > 0 ? player.Layout.GridH : int.MaxValue;
        ReadOnlySpan<(int Dx, int Dy)> dirs =
            [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
        double baseAngle = Math.Atan2(here.Y - target.Y, here.X - target.X);
        int start = ((int)Math.Round(baseAngle / (Math.PI / 4)) % 8 + 8) % 8;
        (int X, int Y)? walkableFallback = null;
        for (int i = 0; i < 8; i++)
        {
            int idx = (start + (i % 2 == 1 ? (i + 1) / 2 : -i / 2) + 8) % 8;
            int cx = target.X + dirs[idx].Dx * offset;
            int cy = target.Y + dirs[idx].Dy * offset;
            if (!Floor(cx, cy)) continue;
            walkableFallback ??= (cx, cy);
            if (CollisionEscape.Clear((cx, cy), w, h, Floor, blockers, moverSize: size))
                return (cx, cy);
        }
        return walkableFallback ?? target;
    }

    static void GolemFollowLocked(Player player, GolemState.Golem g, (int X, int Y) owner, double now)
    {
        var m = g.Monster;
        int size = Math.Max(0, LivingCollision.Size(m));
        var here = m.Walk.Position(now);
        double distance = Dist(here, owner);
        if (distance > 18)
        {
            if (now >= g.NextMove) GolemMoveLocked(player, g, NextToPlayer(player, size, g.Id), now);
        }
        else
        {
            var wander = GolemWander.GetOrCreateValue(g);
            if (now >= wander.Next && now >= g.NextMove && now >= m.Walk.ArrivesAt)
            {
                int start = (int)((g.Id + (uint)(now / 4)) % 8);
                int radius = Math.Max(9, size + LivingCollision.PlayerSize + 2);
                for (int i = 0; i < 8; i++)
                {
                    double angle = ((start + i) % 8) * Math.PI / 4;
                    var dest = (owner.X + (int)Math.Round(Math.Cos(angle) * radius),
                        owner.Y + (int)Math.Round(Math.Sin(angle) * radius));
                    if (!player.Layout.WalkableFor(dest.Item1, dest.Item2, size) || player.Barrier.Contains(dest)) continue;
                    if (player.Connection.LockstepClient && !LockstepTakesMove(here, dest)) continue;
                    GolemMoveLocked(player, g, dest, now);
                    wander.Next = now + 3 + g.Id % 3;
                    return;
                }
                wander.Next = now + 1;
            }
        }
    }

    static void GolemMoveLocked(Player player, GolemState.Golem g, (int X, int Y) to, double now)
    {
        if (now < g.NextMove || g.Monster.LastTarget is { } old && Dist(old, to) < 5 && now < g.Monster.Walk.ArrivesAt) return;
        if (!player.Layout.WalkableFor(to.X, to.Y, Math.Max(0, LivingCollision.Size(g.Monster))) || player.Barrier.Contains(to)) return;
        var m = g.Monster; var from = m.Walk.Position(now);
        if (player.Connection.LockstepClient)
        {
            if (!LockstepTakesMove(from, to)) return;
            if (InAreaFrame(player))
            {
                g.NextMove = now + .5;
                int generation = ++m.ActionGeneration;
                player.Minions.OutputQueue.Add(new([], null, g.Id, m, Action: generation, At: to, WorldId: g.Id));
                return;
            }
        }
        g.NextMove = now + .5;
        m.Walk.Order(to.X, to.Y, now);
        if (m.Walk.Destination == from)
        {
            if (!player.Connection.LockstepClient && m.LastTarget is not null)
                player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionEnded(g.Id), null, g.Id, m,
                    Action: ++m.ActionGeneration, WorldId: g.Id));
            m.LastTarget = null;
            return;
        }
        m.LastTarget = to; int gen = ++m.ActionGeneration;
        player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionStarted(g.Id, from.X, from.Y, to.X, to.Y,
            0x2909, NextMonsterMoveSequence(player, m), player.MonsterOrderFlags), null, g.Id, m, Action: gen, WorldId: g.Id));
    }

    static byte[]? GolemMoveAtGate(Player player, uint id, SpawnedMonster m, (int X, int Y) to)
    {
        if (m.Dead || m.Life == 0 || MonsterAsleep(player, id)) return null;
        double at;
        if (player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame)
        {
            StepWalkers(player, frame.Start);
            at = frame.Start;
        }
        else at = AreaTime(player);
        var from = m.Walk.Position(at);
        if (player.Connection.LockstepClient && !LockstepTakesMove(from, to)) return null;
        var saved = m.Walk.Save();
        var start = m.Walk.Order(to.X, to.Y, at);
        if (m.Walk.Destination == from)
        {
            m.Walk.Restore(saved);
            return null;
        }
        m.ClientChase = null;
        m.ClientActionWalkStop = null;
        m.LastTarget = to;
        return InstanceProtocol.ActionStarted(id, start.X, start.Y, to.X, to.Y,
            0x2909, NextMonsterMoveSequence(player, m), player.MonsterOrderFlags);
    }

    public static void GolemAbilityContactLocked(Player player,
        GolemState.Golem g, GolemState.Action action, double now)
    {
        if (SpectreChargeRoar(g, action.Ability.Effect) is { } charge)
        {
            var cast = action.Ability;
            ReleaseSpectreChargeRoarLocked(player, g.Monster.Walk.Position(now), charge, cast.Radius, now, cast.Id);
            return;
        }
        var a = action.Ability; var at = g.Monster.Walk.Position(now);
        if (a.Effect == 10844)
        {
            if (GolemDashPath(player, g.Monster, at, action.Aim))
            {
                var end = action.Aim;
                g.Monster.Walk = player.NewMonsterWalk(g.Id, end.X, end.Y, g.Monster.Walk.Speed, LivingCollision.Size(g.Monster), false);
                player.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(g.Id, end.X, end.Y), null, g.Id, g.Monster, WorldId: g.Id));
            }
            return;
        }
        if (a.Effect == 10584 && g.Abilities.FirstOrDefault(s => s.Id.Contains("DashImpactSlash", StringComparison.Ordinal)) is { } slash)
        {
            if (player.Layout.WalkableFor(action.Aim.X, action.Aim.Y, LivingCollision.Size(g.Monster)))
            {
                g.Monster.Walk = player.NewMonsterWalk(g.Id, action.Aim.X, action.Aim.Y, g.Monster.Walk.Speed, LivingCollision.Size(g.Monster), false);
                player.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(g.Id, action.Aim.X, action.Aim.Y), null, g.Id, g.Monster, WorldId: g.Id));
            }
            // GA is the ultimate's contact payload, not an independently animated GroundSlam action on the reaper rig.
            GolemAbilityContactLocked(player, g,
                new(slash, action.Target, action.Victim, action.Aim, now, g.Monster.ActionGeneration), now);
            return;
        }
        if (a.Effect is 12032 or 12033) { ReaperConsumedLocked(player, g, action, now); return; }
        if (a.Effect == 9638)
        {
            AnimatedGuardianSmiteLocked(player, g, action, now);
            return;
        }
        if (a.Effect is 9927 or 9928) HolyRelicRegenerationLocked(player, g, a, now);
        if (a.Effect == 5906) { g.WrathUntil = now + GolemStat(a.Stats, Stat.BaseSkillEffectDuration) / 1000.0; player.Golems.RefreshAt = 0; return; }
        if (a.Effect == 10801) { player.Minions.OutputQueue.Add(new([], "golem consumes corpse", g.Id, g.Monster, PlayerMinions.OutputKind.Life)); return; }
        if (a.Effect is 4336 or 4337)
        {
            var (dps, unitDps, normalIncrease) = GolemHazardDamage(player, g, now,
                GolemStat(a.Stats, Stat.BaseChaosDamageToDealPerMinute) / 60.0, area: true);
            (dps, unitDps) = (dps * DoedreSewers.MinionDamageFactor(player), unitDps * DoedreSewers.MinionDamageFactor(player));
            g.Hazards.Add(new(a.Effect, at, now + GolemStat(a.Stats, Stat.BaseSkillEffectDuration) / 1000.0, a.Radius,
                dps, DamageType.Chaos, Follows: true, Wither: a.Effect == 4337,
                UnitDamagePerSecond: unitDps, NormalDamageIncrease: normalIncrease) { LastAt = now });
            return;
        }
        if (a.Effect == 5908)
        {
            uint obj = player.TakeObjectId();
            var orb = new EffectObject(action.Aim.X, action.Aim.Y, "Metadata/Effects/Spells/monsters_effects/lightning_elemental/orb/orb.ao");
            player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), orb.Blob()), "lightning golem orb", WorldId: obj, CreatesObject: true));
            player.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(obj), null, WorldId: obj));
            g.Hazards.Add(new(a.Effect, action.Aim, now + 8, 40, 0, DamageType.Lightning) { LastAt = now, Object = obj }); return;
        }
        if (a.Effect == 9511)
        {
            int count = Math.Clamp(1 + GolemStat(a.Stats, Stat.NumberOfAdditionalArrows)
                + GolemStat(g.Own, Stat.NumberOfAdditionalArrows) + GolemStat(g.Own, Stat.NumberOfAdditionalProjectiles), 1, 256);
            var enemies = player.Area.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0)
                .Select(m => ProjectileSession.Vector(m.Walk.Position(now))).ToArray();
            double spread = 24 * a.Radius / 10;
            var landings = RainLandings("RainOfArrows", ProjectileSession.Vector(action.Aim), count, spread, enemies, AreaRng(player));
            for (int i = 0; i < landings.Count; i++)
            {
                var p = landings[i]; double when = now + .35 + i * .04;
                g.Pulses.Add(new(a, ((int)Math.Round(p.X), (int)Math.Round(p.Y)), when, 1, Once: true) { Next = when });
            }
            return;
        }
        if (a.Projectile is not null)
        { GolemLaunch(player, g, a, action.Aim, now, nova: a.Effect == 5026); return; }
        if (a.Effect is 3347 or 10799 or 10800)
        {
            double dx = action.Aim.X - at.X, dy = action.Aim.Y - at.Y, length = Math.Max(1, Dist(at, action.Aim));
            int spikes = Math.Clamp(GolemStat(a.Stats, Stat.UpheavalNumberOfSpikes), 1, 12);
            for (int i = 0; i < spikes; i++)
            {
                var spikeAt = (at.X + (int)Math.Round(dx / length * (8 + i * 10)), at.Y + (int)Math.Round(dy / length * (8 + i * 10)));
                double when = now + i * .1;
                g.Pulses.Add(new(a, spikeAt, when, 1) { Next = when });
            }
            if (a.Effect == 10800) g.CorpseEmpowered = false;
            GolemPulsesLocked(player, g, now); return;
        }
        if (a.Effect is 2563 or 349)
        {
            double duration = a.Effect == 2563 ? GolemStat(a.Stats, Stat.BaseSkillEffectDuration) / 1000.0 : Math.Max(.1, g.Monster.BusyUntil - now);
            g.Monster.BusyUntil = now + duration;
            g.Pulses.Add(new(a, at, now + duration, a.Effect == 2563 ? Math.Max(.1, a.Attack.Seconds) : .1, Follows: true, Once: a.Effect == 349) { Next = now });
            GolemPulsesLocked(player, g, now); return;
        }
        bool area = GolemStat(a.Stats, Stat.IsAreaDamage) != 0 || a.Effect is 136 or 349 or 2563 or 3347 or 10799 or 10800 or 869 or 4839 or 9927 or 9928;
        var centre = a.Effect is 3347 or 10799 or 10800 or 869 or 11994 or 11995 or 11996 or 11997 ? action.Aim : at;
        if (a.Effect is 9927 or 9928 && GolemStat(g.Own, Stat.HolyRelicTargetsNearestEnemy) != 0)
            centre = action.Victim?.Walk.Position(now) ?? action.Aim;
        if (a.Effect == 869)
        {
            var end = action.Aim;
            if (player.Layout.WalkableFor(end.X, end.Y, Math.Max(0, LivingCollision.Size(g.Monster))))
            {
                g.Monster.Walk = player.NewMonsterWalk(g.Id, end.X, end.Y, g.Monster.Walk.Speed, LivingCollision.Size(g.Monster), false);
                player.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(g.Id, end.X, end.Y), null, g.Id, g.Monster, WorldId: g.Id));
                centre = end;
            }
        }
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (!m.Hostile || m.Dead || m.Life == 0) continue;
            if (area ? Dist(m.Walk.Position(now), centre) > a.Radius + LivingCollision.Size(m)
                : id != action.Target || Dist(m.Walk.Position(now), at) > a.Attack.Range + LivingCollision.Size(m) + 2) continue;
            bool landed = GolemHitLocked(player, g, a, id, m, now);
            int SplashStat(uint n) => g.Monster.Mods.Effects[n] + GolemStat(a.Stats, n);
            if (landed && !area && MinionMelee(player, a) && MinionSupportMatches(a.Effect, "SupportMeleeSplash")
                && MeleeSplash.Settings(SplashStat) is { } splash)
            {
                var splashAt = m.Walk.Position(now);
                var spread = MinionDamageScale(a, splash.More, splash.More);
                foreach (var (otherId, other) in player.Area.Monsters.ToArray())
                    if (otherId != id && other.Hostile && !other.Dead && other.Life > 0
                        && Dist(other.Walk.Position(now), splashAt) <= splash.Radius + LivingCollision.Size(other))
                        GolemHitLocked(player, g, spread, otherId, other, now);
            }

        }
        if (a.Effect == 5268)
        {
            var (dps, unitDps, normalIncrease) = GolemHazardDamage(player, g, now,
                GolemStat(a.Stats, Stat.BaseFireDamageToDealPerMinute) / 60.0, area: true);
            (dps, unitDps) = (dps * DoedreSewers.MinionDamageFactor(player), unitDps * DoedreSewers.MinionDamageFactor(player));
            if (dps > 0 || (unitDps > 0 && g.Definition.IsGolem))
                g.Hazards.Add(new(a.Effect, at, now + 5, a.Radius, dps, DamageType.Fire,
                    UnitDamagePerSecond: unitDps, NormalDamageIncrease: normalIncrease) { LastAt = now });
        }
        if (a.Effect == 4839)
        {
            if (g.Abilities.FirstOrDefault(a => a.Effect == 5026) is { } nova) GolemLaunch(player, g, nova, at, now, nova: true);
            GolemDieLocked(player, g, now);
        }
        if (a.Effect == 10800) g.CorpseEmpowered = false;
        if (a.Effect == 10802) g.Combo = (g.Combo + 1) % 6;
        if (a.Effect == 7133 && g.Definition.Id == "RaiseZombieAltY") GolemDieLocked(player, g, now);
    }

    static (double DamagePerSecond, double UnitDamagePerSecond, int NormalDamageIncrease) GolemHazardDamage(
        Player player, GolemState.Golem g, double now, double intrinsicDamagePerSecond, bool area)
    {
        RefreshGolemLocked(player, g, g.Player, now);
        int normalIncrease = g.Monster.Mods.Effects[Stat.DamagePlusPct] - GolemRecentDamageIncrease(g, now)
            + (area ? g.Monster.Mods.Effects[Stat.AreaDamagePlusPct] : 0);
        double more = MinionTacticsBaseDamage(player, g, now) * Math.Max(0, 1 + GolemStat(g.Own, Stat.ActiveSkillMinionDamagePlusPctFinal) / 100.0)
            * Math.Max(0, 1 + GolemStat(g.Own, Stat.SupportMinionDamagePlusPctFinal) / 100.0)
            * MindlessAggressionDamage(g.Player)
            * Math.Max(0, 1 + GolemTether(g.Player, "damage") / 100.0);
        double increasedMultiplier = Math.Max(0, 1 + normalIncrease / 100.0);
        double factor = increasedMultiplier * more;
        return (intrinsicDamagePerSecond * factor,
            intrinsicDamagePerSecond * more, normalIncrease);
    }

    static void GolemPulsesLocked(Player player, GolemState.Golem g, double now)
    {
        foreach (var pulse in g.Pulses.ToArray())
        {
            int catchup = 0;
            while (pulse.Next <= Math.Min(now, pulse.Until) && catchup++ < 16)
            {
                var centre = pulse.Follows ? g.Monster.Walk.Position(now) : pulse.At;
                foreach (var (id, m) in player.Area.Monsters.ToArray())
                {
                    if (!m.Hostile || m.Dead || pulse.Once && pulse.Hit.Contains(id) || Dist(m.Walk.Position(now), centre) > pulse.Ability.Radius + LivingCollision.Size(m)) continue;
                    if (GolemHitLocked(player, g, pulse.Ability, id, m, now)) pulse.Hit.Add(id);
                }
                pulse.Next += pulse.Interval;
            }
            if (pulse.Next > pulse.Until) g.Pulses.Remove(pulse);
        }
    }

    public static bool GolemHitLocked(Player player, GolemState.Golem g,
        GolemState.Ability a, uint id, SpawnedMonster target, double now)
    {
        if (!GolemCurrent(player, g) || !CurrentGameWork || target.Dead || target.Invulnerable || !target.Hostile
            || player.Area.Monsters.GetValueOrDefault(id) != target) return false;
        using var scope = new PlayerStatScope(player, fresh: true);
        var currentPlayer = CharacterPlayerStats(player);
        RefreshGolemLocked(player, g, currentPlayer, now);
        if (!GolemCurrent(player, g) || player.PlayerLife == 0 || g.Monster.Dead || g.Monster.Life == 0) return false;
        var source = CurrentPlayerSkillSourceLocked(player, g.Source);
        var supported = SupportedSkill(player, g.Source);
        if (source is null || !source.Active || source.Effect != g.Definition.Effect
            || !supported.RequirementsMet || supported.Level <= 0 || supported.Quality < 0
            || supported.Effect != g.Definition.Effect) return false;
        if (g.Abilities.FirstOrDefault(current => current.Effect == a.Effect) is not { } current) return false;
        if (a.Effect == 306)
        {
            var element = new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning }
                .First(type => GolemStat(a.Stats, Stat.Of($"elemental_hit_cannot_roll_{type.ToString().ToLowerInvariant()}_damage")) == 0);
            current = GolemAbilities(player, g.Monster.Variety!, g.Monster.Level, g, currentPlayer, now,
                prismatic: element).Single(ability => ability.Effect == a.Effect);
        }
        a = CurrentMinionHit(a, current);
        if (!a.Attack.Spell)
        {
            int accuracy = g.Monster.Mods.Effects.Accuracy((Monsters.BaseAccuracy(g.Monster.Level) ?? 0)
                + g.Monster.Mods.Effects[Stat.AccuracyRating]);
            int evasion = UnderBlind(player, id, target.Mods.Effects.Evasion(Monsters.BaseEvasion(target.Level) ?? 0));
            if (!DamageModel.Hits(DamageModel.AccuracyHitChance(evasion, accuracy), AreaRng(player).Next(100))) return false;
        }
        if (MonsterBlocks(target, AreaRng(player), a.Attack.Spell)) return false;
        double factor = DoedreSewers.MinionDamageFactor(player) * MinionTacticsDamage(player, g, id, target, now);
        if (g.Definition.Kind == "Carrion")
        {
            var here = g.Monster.Walk.Position(now);
            int nearby = player.Minions.All.Count(m => !player.Golems.All.ContainsKey(m.Id) && !m.Monster.Dead && Dist(here, m.Monster.Walk.Position(now)) <= 60);
            factor *= 1 + Math.Min(GolemStat(g.Own, Stat.BoneGolemDamagePlusPctFinalPerNonGolemMinionNearby) * nearby,
                GolemStat(g.Own, Stat.BoneGolemDamagePerNonGolemMinionNearbyMaximumPct)) / 100.0;
            if (a.Effect == 10802 && g.Combo >= 3) factor *= 1 + GolemStat(a.Stats, Stat.BoneGolemDamagePlusPctFinalAfterThirdComboIndex) / 100.0;
        }
        bool prey = IsPrey(player, g.Own, id, target, now);
        var effects = g.Monster.Mods.Effects;
        int roll = effects[Stat.Of("never_crit")] == 0 && effects[Stat.AlwaysCrit] == 0 ? AreaRng(player).Next(10_000) : 0;
        bool critical = GolemCriticalStrike(a, g.Monster.Mods, roll,
            prey ? GolemStat(g.Own, Stat.SupportMinionFocusFireCriticalStrikeChancePlusPctVsFocusedTarget) : 0,
            BrittleCriticalChance(target, now));   // every hit against a Brittle enemy
        if (critical) factor *= target.Effects.NoExtraCriticalDamage ? 1 : 1.3 +
            (g.Monster.Mods.Effects[Stat.BaseCriticalStrikeMultiplierPlus] + g.Monster.Mods.Effects[Stat.Of("critical_strike_multiplier_+")]
                + target.Curses[Stat.EnemyAdditionalCriticalStrikeMultiplierAgainstSelf]
                + (prey ? GolemStat(g.Own, Stat.SupportMinionFocusFireCriticalStrikeMultiplierPlusVsFocusedTarget) : 0)) / 100.0;
        var rolled = GolemRollHit(a.Attack.Damage.Scale(factor), effects, a.Attack.Spell, AreaRng(player));
        bool ignoreElemental = g.Shield > 0
            && g.Monster.Mods.Effects[Stat.HitsIgnoreEnemyElementalResistancesWhileHasEnergyShield] != 0;
        var resistanceMods = new HitResistances.Mods(
            CriticalIgnoreElemental: 0,
            AttackCriticalIgnoreElemental: 0,
            NonCriticalPenetration: 0,
            UnconditionalIgnoreElemental: ignoreElemental,
            CannotPenetrateOrIgnore: g.Monster.Mods.Effects[Stat.CannotPenetrateOrIgnoreElementalResistances] != 0 ? 1 : 0);
        resistanceMods = ImpurityHitMods(target, now, resistanceMods);
        var strike = new HitResistances.Strike(Critical: critical, Attack: !a.Attack.Spell);
        var mitigated = DamageModel.Mitigate(rolled,
            type => HitResistances.Effective(type, MonsterHitResistance(target, type, default, now), strike, resistanceMods));
        int damage = MonsterTakesMitigated(target, mitigated, a.Attack.Spell, now, random: AreaRng(player));
        var from = g.Monster.Walk.Position(now);
        var to = target.Walk.Position(now);
        if (ApplyMonsterHitLocked(player, target, damage, allyAt: from) is not { } hit) return false;
        if (!target.Dead && damage > 0) MinionExposureOnHitLocked(g.Own, target, mitigated, now, AreaRng(player));
        if (hit.StageStarts)
        { CommitBossStage(player, id, target, now); hit = hit with { StageStarts = false }; }
        // Immediate kind 3 reaches the attacker's native result consumer; 0xffff never supplies a consuming action.
        player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(g.Id, 0xffff, id,
            flags: MonsterHitFlags(blocked: false, critical), kind: 0x03, code: HitCode(mitigated),
            angle: HitAngle(to.X - from.X, to.Y - from.Y)), null, id, target, WorldId: g.Id));
        PhantasmsOnHitLocked(player, target, g.Source, now, AreaRng(player), g);
        bool melee = MinionMelee(player, a);
        double physicalShare = rolled[DamageType.Physical].Min / Math.Max(1, rolled.MinTotal);
        var stun = new StunHit(HitReaction.StunningDamage(damage, physicalShare, melee),
            g.Monster.Mods.Effects[Stat.BaseStunThresholdReductionPlusPct], g.Monster.Mods.Effects[Stat.BaseStunDurationPlusPct],
            Melee: melee, BaseSeconds: GolemStat(a.Stats, Stat.SupportRuthlessBigHitStunBaseDurationOverrideMs) / 1000d);
        CommitMinionHitLocked(player, id, target, hit, damage, now, $"golem {g.Id:x} {a.Id}", stun, g.Monster, g.Own);
        if (!target.Dead)
        {
            int Golem(uint name) => g.Monster.Mods.Effects[name] + GolemStat(a.Stats, name);
            target.Ailments.Inflict(mitigated, critical, target.MaxLife, now, Golem,
                n => target.Mods.Effects[n], AreaRng(player));
            HitDebuffsOnHitLocked(target, Golem, a.Attack.Spell, critical, now, AreaRng(player));
            InflictMonsterBlind(player, id, target, now, BlindHitInput.Capture(Golem), a.Attack.Spell, critical,
                AreaRng(player));
            MinionAilmentsLocked(player, g, a, id, target, critical, now);
            MinionTauntOnHitLocked(player, g, a, supported.Stats, id, target, now);
        }
        if (damage > 0) FeedingFrenzyOnHitLocked(player, g, now);
        return true;
    }

    public static int GolemCriticalChance(GolemState.Ability ability, MonsterModSet mods, int additionalIncrease = 0)
    {
        long increased = (long)mods.Effects[Stat.CriticalStrikeChancePlusPct] + additionalIncrease
            + (ability.Attack.Spell ? mods.Effects[Stat.SpellCriticalStrikeChancePlusPct] : 0);
        double chance = (ability.Crit + (double)mods.Effects[WakenedFuryChance]) * Math.Max(0, 100L + increased) / 100.0;
        return (int)Math.Clamp(chance, 0, 10_000);
    }

    public static DamagePacket GolemRollHit(DamagePacket damage, MonsterModEffects effects, bool spell, Random random) =>
        GamblerModifiers.From(name => effects[name]).ForSkill(_ => 0, spell).RollOutgoingHit(damage, random);

    /// <summary><paramref name="additionalChance"/>: flat chance in hundredths added after the increases (the target's Brittle).</summary>
    public static bool GolemCriticalStrike(GolemState.Ability ability, MonsterModSet mods, int roll, int additionalIncrease = 0,
        int additionalChance = 0) =>
        mods.Effects[Stat.Of("never_crit")] == 0 && (mods.Effects[Stat.AlwaysCrit] != 0
            || roll < GolemCriticalChance(ability, mods, additionalIncrease) + additionalChance);

    static void GolemLaunch(Player player, GolemState.Golem g,
        GolemState.Ability a, (int X, int Y) aim, double now, bool nova = false)
    {
        if (a.Projectile is not { } p) return;
        var session = ProjectilesFor(player); var from = ProjectileSession.Vector(g.Monster.Walk.Position(now));
        var to = nova ? from + new ProjectileVector(1, 0) : ProjectileSession.Vector(aim);
        double distance = Math.Max(1, (to - from).Length);
        var owner = session.ActorKey(g.Id, g.Monster);
        var mods = g.Monster.Mods.Effects;
        int SpeedStat(uint name) => GolemStat(a.Stats, name)
            + (name == Stat.SupportSlowerProjectilesProjectileSpeedPlusPctFinal ? mods[name] : 0);
        var profile = new ProjectileProfile(p.Metadata)
        {
            Speed = ProjectileSpeed.CellsPerSecond(p.Speed,
                _ => mods[Stat.ProjectileSpeedPlusPct] + mods[Stat.BaseProjectileSpeedPlusPct], SpeedStat), Radius = p.CollisionRadius,
            Range = p.Lobbed ? distance : Math.Max(60, distance + 10), Lobbed = p.Lobbed,
            Count = nova ? 14 : Math.Max(1, p.Count), SpreadRadians = nova ? Math.PI * 2 : p.SpreadRadians,
            DirectDamage = !p.Lobbed,
            Pierce = a.Effect == 2378 || GolemStat(a.Stats, Stat.AlwaysPierce) != 0 ? -1 : 0,
            EndArea = p.Lobbed ? new("golem mortar", a.Radius) : null,
            EndAreaReasons = p.Lobbed ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        };
        // The profile already contains the native skill's quantity; the minion's own modifiers add to it once.
        profile = GatewayProjectileModifiers.Resolve(profile, name => mods[name], attack: !a.Attack.Spell,
            arrows: g.Weapon is { Bow: true }, skillPierce: mods[Stat.Of("number_of_additional_projectiles_to_pierce")]);
        var offense = new ObjectProjectileOffense(a.Id, (s, contact) =>
        {
            if (!CurrentGameWork || player.Area.Monsters.GetValueOrDefault(contact.Target.Id) is not { Hostile: true } victim) return ProjectileHitResult.Miss;
            double at = Math.Max(AreaTime(player), contact.At / (double)ProjectileRuntime.QuantaPerSecond);
            bool hit = GolemHitLocked(player, g, a, contact.Target.Id, victim, at);
            if (hit) s.Enqueue(() => PublishMinionOutcomes(player));
            return hit ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        }) { Quiet = true, Presentation = MonsterProjectilePresentation(SkillsFor(player).Clips(p.Metadata), distance / profile.Speed) };
        session.Schedule(ProjectileRuntime.Time(now), () => CurrentGameWork && !player.Closed && !player.LeavingArea,
            () => new(profile, owner, owner, 1, from, to) { Payload = offense });
    }

    static void GolemMeteorLocked(Player player, GolemState.Golem g, double now)
    {
        if (g.Abilities.FirstOrDefault(a => a.Effect == 5268) is not { } a) return;
        GolemReserveLocked(player, g, a, 0, null, g.Monster.Walk.Position(now), now);
    }

    public static void GolemHazardsLocked(Player player, GolemState.Golem g, double now)
    {
        foreach (var hazard in g.Hazards.ToArray())
        {
            double from = hazard.LastAt;
            double through = Math.Min(now, hazard.Until);
            double elapsed = Math.Max(0, through - from); hazard.LastAt = through;
            double activeSeconds = 0;
            int recentDamageIncrease = 0;
            if (hazard.UnitDamagePerSecond != 0 && g.Definition.IsGolem && double.IsFinite(g.SummonedAt))
            {
                activeSeconds = Math.Max(0, Math.Min(through, g.SummonedAt + 8) - Math.Max(from, g.SummonedAt));
                // Read the current source even when this delayed interval has already crossed the boundary.
                recentDamageIncrease = GolemStat(g.Player, Stat.GolemDamagePlusPctIfSummonedInPast8Seconds);
            }
            double normalMultiplier = Math.Max(0, 1 + hazard.NormalDamageIncrease / 100.0);
            double conditionalMultiplier = Math.Max(0,
                1 + (hazard.NormalDamageIncrease + recentDamageIncrease) / 100.0);
            double deltaDamagePerSecond = hazard.UnitDamagePerSecond * (conditionalMultiplier - normalMultiplier);
            var centre = hazard.Follows ? g.Monster.Walk.Position(now) : hazard.At;
            foreach (var (id, m) in player.Area.Monsters.ToArray())
            {
                if (!m.Hostile || m.Dead || m.Invulnerable || Dist(m.Walk.Position(now), centre) > hazard.Radius) continue;
                if (hazard.Effect == 5908)
                {
                    if (now >= g.NextOrb && g.Abilities.FirstOrDefault(a => a.Effect == 5908) is { } orb)
                    { GolemHitLocked(player, g, orb, id, m, now); g.NextOrb = now + 1; }
                    continue;
                }
                if (hazard.Wither && now >= hazard.Carry.GetValueOrDefault(id, double.NegativeInfinity))
                {
                    m.Withered.RemoveAll(w => w.Until <= now);
                    if (m.Withered.Count >= 15) m.Withered.Remove(m.Withered.MinBy(w => w.Until));
                    { m.Withered.Add((now + 4, 6 * (1 + GolemStat(g.Own, Stat.MinionWitheredEffectPlusPct) / 100.0))); m.PresentationChanged?.Invoke(); }
                    hazard.Carry[id] = now + .3;
                }
                double raw = (hazard.DamagePerSecond * elapsed + deltaDamagePerSecond * activeSeconds)
                    * MeatShieldDamage(player, g.Own, m, now);
                double resisted = DamageModel.Mitigate(DamagePacket.Of(hazard.Type, raw, raw), t => MonsterHitResistance(m, t, default, now))[hazard.Type].Min;
                double total = resisted * MonsterDamageTaken(m, spell: true, now, chaos: hazard.Type == DamageType.Chaos)
                    + hazard.DamageCarry.GetValueOrDefault(id);
                int damage = (int)Math.Floor(total); hazard.DamageCarry[id] = total - damage;
                if (damage <= 0 || ApplyMonsterHitLocked(player, m, damage, allyAt: centre) is not { } hit) continue;
                if (hit.StageStarts) { CommitBossStage(player, id, m, now); hit = hit with { StageStarts = false }; }
                CommitMinionHitLocked(player, id, m, hit, damage, now, $"golem {g.Id:x} ground damage",
                    source: g.Monster, skill: g.Own);
            }
            if (now >= hazard.Until)
            {
                if (hazard.Object is uint obj) player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(obj), null, WorldId: obj, RemovesObject: true));
                g.Hazards.Remove(hazard);
            }
        }
    }
}
