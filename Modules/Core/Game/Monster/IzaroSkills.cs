using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using static Game;
using static Program;

/// <summary>
/// Izaro's own fight (the wiki's Abilities, by his weapon): the skills of his MonsterVarieties row other than his plain
/// attack, which the ordinary AI keeps. Their time and damage are the data's (the row of the effect's level for his level:
/// <c>active_skill_attack_speed_+%_final</c>, <c>active_skill_damage_+%_final</c>, the spells' base damage), their contacts
/// the rig's clips. How often he uses them and the shapes of their hits are ours: the official AI (<c>.ais</c>) is not in
/// the client.
/// </summary>
static class IzaroSkills
{
    enum Shape { Circle, Ahead, Line, Cone, Leap, Charge }

    // Effect: GrantedEffects row. Contact: the clip's contact over its length (LabyrinthBoss's rig.amd). Reach: how far the
    // player may be for him to use it, from at least Near. Size: the radius, or the line's or cone's length; Width: the line's
    // half width, the cone's half angle in degrees. Ticks: further hits while the cast lasts.
    sealed record Move(int Effect, string Name, double Contact, double Near, double Reach, Shape Shape, double Size, double Width,
        double Cooldown, double TickSeconds = 0, double TicksFor = 0, string? Art = null, double HoldSeconds = 0);

    const int ChargeCells = 60, StopCells = 5;
    const string WaveArt ="Metadata/Effects/Spells/monsters_effects/labyrinth/skeleton_king/hyper_reave/izarohyperreave.ao";
    const double WaveSeconds = 1.541;

    static readonly Move[] Moves =
    [
        // Maul, two swords: leapslam_* hit_ground 1266 of 2066 ms; active_skill_base_area_of_effect_radius 15.
        new(811, "Leap Slam", 1266.0 / 2066, 12, 60, Shape.Leap, 15, 0, 4),
        // Maul: groundslam_maul contact 1466 of 2833 ms (VollSlam).
        new(4386, "Slam", 1466.0 / 2833, 0, 24, Shape.Ahead, 20, 0, 3),
        // Sword and shield, two swords: cleave_* contact 1499 of 2933 ms (EffectDrivenAttack, skill_behaviour_variation 55).
        // izarohyperreave.ao's EffectDrivenEvent: a donut from 42 ahead, length 160, width 46, angle 25 (world units): 19 cells.
        // The client plays no wave for this action (live), so the server shows that art on him at the contact.
        new(5922, "Divine Slash", 1499.0 / 2933, 0, 19, Shape.Cone, 19, 30, 3, Art: WaveArt),
        // Sword and shield: VollCharge's dash starts 1.6 s after the order and takes 0.25 s (measured, trace-sync.js), at 270
        // cells/s; its hit is where it ends.
        new(4362, "Shield Bash", 1.82 / 2.11, 12, 70, Shape.Charge, 0, 8, 4),
        // Every weapon: spellcast_cascade_* contact 1966 of 3000 ms. Upheaval (0x141b28470, 0x141b28d10): its 10 spikes go
        // spikes × radius / 1.15 from him towards the aim, radius (12 + active_skill_base_radius_+) × 82 %: 9, so 78 cells.
        // His 2 s cast is shorter than the clip's 3 s: he stands the clip out, or a chase at 2 s walks him through its end.
        new(3315, "Ground Slam", 1966.0 / 3000, 0, 78, Shape.Line, 78, 9, 5, TickSeconds: 0.1, HoldSeconds: 3),
        // Every weapon: spellcast_aoe3_* contact 500 of 3000 ms (grasping_ornament_placement_radius 16, 3 s).
        // Its action (0x141b9df80) hits round the aim every 0.2 s (5 cells × the area, 0x141b9e1c0) while it places its
        // ornaments, one each skill_effect_duration (3 ms, about a frame): 65 of them with its +30% area, about 1.1 s.
        new(5535, "Spikes", 500.0 / 3000, 0, 70, Shape.Circle, 6, 0, 6, TickSeconds: 0.2, TicksFor: 1.1),
    ];

    // Off the elevator he uses one at once, and nearly all the fight he goes from skill to skill, with only a couple of plain
    // swings (live, the original; these numbers and the skills' cooldowns are ours, the .ais is not in the client). Between
    // skills the AI is held back, or it took the free moment for a 1.7 s swing; it swings when none is ready.
    const double FirstSeconds = 0, GapMinSeconds = 0.3, GapMaxSeconds = 0.8, HeldPastGap = 0.1, RunMinSeconds = 1, RunMaxSeconds = 2;

    /// <summary>LabyrinthBoss.ot's <c>base_movement_velocity_+%</c> 15: the client walks him at 41 cells/s where the row's speed
    /// says 36 (measured, trace-sync.js).</summary>
    internal const double WalkFactor = 1.15;

    /// <summary>How often the ordinary AI orders his chase again while he is on his way (ours).</summary>
    internal const double ChaseSeconds = 1;

    // The official monsters' skill ids start at 0x4000 (KingInMists.Kit): the player's gem range crashed the client.
    static ushort SkillId(Move move) => (ushort)(0x4000 + Array.IndexOf(Moves, move));

    public static bool IsIzaro(MonsterCatalog.Variety? variety) =>
        variety is not null && Array.IndexOf(IzaroFights.Varieties, variety.Id) >= 0;

    static IEnumerable<Move> MovesOf(MonsterCatalog.Variety variety) =>
        Moves.Where(m => variety.GrantedEffects?.Contains(m.Effect) == true);

    static int Value(IReadOnlyList<(uint Key, int Value)> stats, string name) =>
        Stats.Key(name) is uint key ? stats.FirstOrDefault(s => s.Key == key).Value : 0;

    // The skill as its level has it: a spell's cast time and base damage, an attack's slower time and more damage than his
    // plain one's.
    static MonsterAttack Attack(ClientState conn, MonsterAttack plain, Move move, byte level)
    {
        var catalog = Game.SkillsFor(conn);
        int effectLevel = catalog.MonsterEffectLevel(move.Effect, level);
        var stats = catalog.EffectStats(move.Effect, effectLevel);
        double more = 1 + (Value(stats, "active_skill_damage_+%_final") + Value(stats, "active_skill_physical_damage_+%_final")) / 100.0;
        bool spell = catalog.IsSpell(move.Effect);
        double speed = 1 + Value(stats, "active_skill_attack_speed_+%_final") / 100.0;
        double seconds = spell ? catalog.CastTimeMs(move.Effect) / 1000.0
            : (Value(stats, "cast_time_overrides_attack_duration") != 0 ? catalog.CastTimeMs(move.Effect) / 1000.0 : plain.Seconds) / speed;
        var damage = spell ? catalog.HitDamage(move.Effect, effectLevel).Scale(more) : plain.Damage.Scale(more);
        return plain with { SkillId = SkillId(move), Seconds = seconds, Range = move.Reach, Damage = damage, Spell = spell, Scripted = true, Projectile = null, Release = null };
    }

    /// <summary>The actor's skill list: his plain attack first, then each skill he uses, with the 0x1ab ids it names.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterAttack attack, GrantedSkill? melee)
    {
        if (!IsIzaro(variety) || melee is null) return null;
        var catalog = Game.SkillsFor(conn);
        var list = new List<GrantedSkill> { melee };
        // The record's times are final, so neither the skill's speed nor the cast-time override goes with them.
        string[] notSent = [.. MonsterSkillStatsNotSent, "active_skill_attack_speed_+%_final", "cast_time_overrides_attack_duration"];
        var finalStats = notSent.Select(name => Stats.Key(name)).OfType<uint>().ToHashSet();
        foreach (var move in MovesOf(variety))
        {
            int effectLevel = catalog.MonsterEffectLevel(move.Effect, level);
            var own = Attack(conn, attack, move, level).InPlaceSkillStats();
            // The client takes neither from the effect's row: its art (the Upheaval's Ornaments, else Glacial Cascade's ice) and
            // the final radius its effect builders read (11-skills.md: without it Ice Nova drew nothing).
            List<(uint, int)> drawn = [.. own];
            if (catalog.ArtVariation(move.Effect) is int art and > 0 && Stats.Key("skill_art_variation") is uint artKey) drawn.Add((artKey, art));
            if (Stats.Key("active_skill_area_of_effect_radius") is uint radiusKey)
                drawn.Add((radiusKey, (int)(move.Shape is Shape.Line or Shape.Charge ? move.Width : move.Size)));
            // The Grasping Ornaments' action spaces them by the final duration and counts them by the final area (0x141b9df80,
            // 0x14193efd0): with no duration only his clip played, and the hits came from nothing (live).
            var effectStats = catalog.EffectStats(move.Effect, effectLevel);
            if (Value(effectStats, "base_skill_effect_duration") is int duration and > 0 && Stats.Key("skill_effect_duration") is uint durationKey)
                drawn.Add((durationKey, duration));
            if (Stats.Key("total_skill_area_of_effect_radius_+%_including_final") is uint areaKey)
                drawn.Add((areaKey, Value(effectStats, "active_skill_area_of_effect_radius_+%_final")));
            var dropped = drawn.Select(s => s.Item1).Concat(finalStats).ToHashSet();
            var stats = WithBehaviourVariation(conn, move.Effect, [.. catalog.EffectStats(move.Effect, effectLevel).Where(s => !dropped.Contains(s.Item1)), .. drawn]);
            if (catalog.FindByRow(SkillId(move), move.Effect, effectLevel, stats) is { } skill) list.Add(skill);
        }
        return list;
    }

    /// <summary>His skills while the fight lasts, one at a time, between the ordinary AI's swings.</summary>
    internal static async Task Run(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster boss, Func<bool> here)
    {
        if (boss.Variety is not { } variety || boss.Attack is not { } plain) return;
        var moves = MovesOf(variety).Select(m => (Move: m, Use: Attack(conn, plain, m, boss.Level))).ToList();
        var ready = new Dictionary<Move, double>();
        double next = conn.Clock.Elapsed.TotalSeconds + FirstSeconds;
        // Holding the AI back until `next`: its BusyUntil is ours, to give up when no skill is ready.
        bool held = false;
        void Hold(double until)
        {
            // A trial's end has taken him out of the fight: its BusyUntil stays.
            if (double.IsFinite(boss.BusyUntil) && boss.Aggro) { boss.BusyUntil = until; held = true; }
        }
        lock (conn.Monsters) Hold(next + HeldPastGap + 0.1);
        try
        {
            while (here())
            {
                await Task.Delay(50);
                if (!here()) return;
                (Move Move, MonsterAttack Use)? chosen = null;
                int action = 0;
                lock (conn.Monsters)
                {
                    if (boss.Dead || boss.Life == 0) return;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    // A monster in its hit recovery starts no action (07 §6), as the ordinary AI keeps.
                    if (now < next || (!held && now < boss.BusyUntil) || now < boss.FlinchUntil || !boss.Aggro || conn.Paused || InGracePeriod(conn)
                        || conn.AsleepMonsters.Contains(id) || MonsterActionFactor(boss, now) == 0) continue;
                    var (from, player) = (boss.Walk.Position(now), conn.Walk.Position(now));
                    double distance = Dist(from, player);
                    var usable = moves.Where(m => distance >= m.Move.Near && distance <= m.Move.Reach && now >= ready.GetValueOrDefault(m.Move)).ToList();
                    if (usable.Count == 0)
                    {
                        // None ready: the AI's turn, a swing or the chase.
                        if (held && double.IsFinite(boss.BusyUntil)) boss.BusyUntil = now;
                        held = false;
                        continue;
                    }
                    held = false;
                    chosen = usable[Random.Shared.Next(usable.Count)];
                    action = ++boss.ActionGeneration;
                    // Not a walk-up swing the AI may call off (a predictive client's), and the chase stops.
                    boss.SwingGeneration = null;
                    boss.BusyUntil = now + chosen.Value.Use.Seconds;
                    ready[chosen.Value.Move] = now + chosen.Value.Move.Cooldown;
                }
                await Use(remote, stream, ciphers, conn, id, boss, action, chosen.Value.Move, chosen.Value.Use);
                lock (conn.Monsters)
                {
                    double now = conn.Clock.Elapsed.TotalSeconds, free = Math.Max(now, boss.BusyUntil);
                    // Out of his plain attack's reach he runs at the player for a moment: held at every gap, he would not chase.
                    if (Dist(boss.Walk.Position(now), conn.Walk.Position(now)) > plain.Range)
                    {
                        next = free + RunMinSeconds + Random.Shared.NextDouble() * (RunMaxSeconds - RunMinSeconds);
                        held = false;
                    }
                    else
                    {
                        next = free + GapMinSeconds + Random.Shared.NextDouble() * (GapMaxSeconds - GapMinSeconds);
                        Hold(next + HeldPastGap);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's skills stopped: {ServerLog.Describe(ex)}"); }
    }

    static async Task Use(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster boss, int action, Move move, MonsterAttack use)
    {
        double startedAt = 0;
        (int X, int Y) from = default, aim = default;
        ushort sequence = conn.NextActionSequence();
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action, () =>
        {
            if (AreaTime(conn) < boss.FlinchUntil
                || MonsterSwingOrderLocked(conn, boss, use, action, generic: false, use.Seconds, AreaTime(conn)) is not { } order)
            {
                boss.BusyUntil = conn.Clock.Elapsed.TotalSeconds;
                return null;
            }
            (startedAt, from, aim) = (order.StartedAt, order.From, order.Player);
            boss.SwingGeneration = null;
            boss.LastTarget = null;
            boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + move.HoldSeconds);
            // A skill played where he stands leaves his walk running (07 §6): he walked on through the cast (measured, trace-sync.js).
            // A short Move replaces the walk; the client refuses one under 3 cells away (0x141949a20).
            var stop = conn.LockstepClient ? from : StopCell(conn, boss, from, aim);
            boss.Walk.Order(stop.X, stop.Y, startedAt);
            byte[] halt = stop == from ? [] : InstanceProtocol.ActionStarted(id, from.X, from.Y, stop.X, stop.Y, IzaroFights.MoveSkill, conn.NextActionSequence(), conn.MonsterOrderFlags);
            // The dash goes to a cell the server names: aimed at the player's object the client chose where it ended by its own
            // player, up to 95 cells from the server's cell, and the next order set him back (measured).
            if (move.Shape == Shape.Charge) aim = ChargeEnd(conn, boss, from, aim);
            // The Upheaval aims at the cell too: aimed at the player's object, it walked him up to about 13 cells from the player
            // through its whole cast (measured).
            return [.. InstanceProtocol.ActionEnded(id), .. halt,
                .. InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, use.SkillId, sequence, conn.MonsterOrderFlags,
                    targetObjectId: move.Shape is Shape.Line or Shape.Charge ? null : PlayerId)];
        }, $"0x01ab Izaro 0x{id:x} uses {move.Name} (0x{use.SkillId:x4}, {use.Seconds:0.00} s)")) return;
        double contact = startedAt + use.Seconds * move.Contact;
        if (!await WaitMonsterActionTime(conn, id, boss, action, contact)) return;
        // The leap and the charge carry him to where they aimed (the client's action moves its copy).
        if (move.Shape is Shape.Leap or Shape.Charge && conn.Layout.WalkableFor(aim.X, aim.Y, 0))
            lock (conn.Monsters) boss.Walk.Teleport(aim.X, aim.Y, conn.Clock.Elapsed.TotalSeconds);
        if (move.Art is { } art) await ShowArt(remote, stream, ciphers, conn, id, art);
        // The spikes rise one after the other (the time between them is ours), and one hit is all the player takes from them.
        if (move.Shape == Shape.Line)
        {
            for (int spike = 0; spike < LineSpikes; spike++)
            {
                if (spike > 0 && !await WaitMonsterActionTime(conn, id, boss, action, contact + spike * move.TickSeconds)) return;
                if (await Hit(remote, stream, ciphers, conn, id, boss, action, move, use, from, aim, sequence, spike)) return;
            }
            return;
        }
        await Hit(remote, stream, ciphers, conn, id, boss, action, move, use, from, aim, sequence);
        if (move.TickSeconds <= 0) return;
        for (double at = contact + move.TickSeconds; at < contact + move.TicksFor; at += move.TickSeconds)
        {
            if (!await WaitMonsterActionTime(conn, id, boss, action, at)) return;
            await Hit(remote, stream, ciphers, conn, id, boss, action, move, use, from, aim, sequence);
        }
    }

    static bool Free(ClientState conn, SpawnedMonster boss, (int X, int Y) cell) =>
        conn.Layout.WalkableFor(cell.X, cell.Y, Math.Max(0, LivingCollision.Size(boss))) && !conn.Barrier.Contains(cell);

    // The cell along his way to the aim, the last free one up to `cells` along it.
    static (int X, int Y) Along(ClientState conn, SpawnedMonster boss, (int X, int Y) from, (int X, int Y) aim, int cells)
    {
        double dx = aim.X - from.X, dy = aim.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return from;
        var last = from;
        for (int k = 1; k <= cells; k++)
        {
            (int X, int Y) cell = ((int)Math.Round(from.X + dx / length * k), (int)Math.Round(from.Y + dy / length * k));
            if (!Free(conn, boss, cell)) break;
            last = cell;
        }
        return last;
    }

    // Where the Move that ends his walk goes: StopCells on, towards the aim, else any free way.
    static (int X, int Y) StopCell(ClientState conn, SpawnedMonster boss, (int X, int Y) from, (int X, int Y) aim)
    {
        foreach (var toward in new[] { aim, (from.X + 1, from.Y), (from.X - 1, from.Y), (from.X, from.Y + 1), (from.X, from.Y - 1) })
        {
            var cell = Along(conn, boss, from, toward, StopCells);
            if (Dist(from, cell) >= StopCells - 1) return cell;
        }
        return from;
    }

    // The dash goes about ChargeCells towards the player, not to the player (55 to 63 measured, trace-sync.js).
    static (int X, int Y) ChargeEnd(ClientState conn, SpawnedMonster boss, (int X, int Y) from, (int X, int Y) player) =>
        Along(conn, boss, from, player, ChargeCells);

    // An effect that rides on him turned as he is (attachment kind 0, flags 3, as the Arrow Nova arrow), for its clip.
    static async Task ShowArt(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, string art)
    {
        uint effect;
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            effect = conn.NextObjectId++;
            at = conn.Monsters.TryGetValue(id, out var boss) ? boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds) : default;
        }
        var layout = conn.Layout;
        await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, art) { Parent = id, AttachKind = 0, AttachFlags = 3 }.Blob()), .. InstanceProtocol.EnterWakeZone(effect)], null);
        _ = RemoveArt(remote, stream, ciphers, conn, layout, effect);
    }

    static async Task RemoveArt(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaLayout layout, uint effect)
    {
        await Task.Delay(TimeSpan.FromSeconds(WaveSeconds));
        if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
        try { await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(effect), null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    const int LineSpikes = 10;

    // Whether a cell is in the skill's hit: round the aim, ahead of him, round a spike on the way from him to the aim.
    static bool Inside(Move move, (int X, int Y) from, (int X, int Y) aim, (int X, int Y) cell, int spike = 0)
    {
        double ax = aim.X - from.X, ay = aim.Y - from.Y, length = Math.Max(1, Math.Sqrt(ax * ax + ay * ay));
        (ax, ay) = (ax / length, ay / length);
        double dx = cell.X - from.X, dy = cell.Y - from.Y, along = dx * ax + dy * ay, across = Math.Abs(dx * ay - dy * ax);
        switch (move.Shape)
        {
            case Shape.Circle or Shape.Leap: return Dist(aim, cell) <= move.Size;
            case Shape.Ahead:
                return Dist(((int)Math.Round(from.X + ax * move.Size / 2), (int)Math.Round(from.Y + ay * move.Size / 2)), cell) <= move.Size;
            case Shape.Line:
                double ahead = (spike + 1) * move.Size / LineSpikes;
                return Dist(((int)Math.Round(from.X + ax * ahead), (int)Math.Round(from.Y + ay * ahead)), cell) <= move.Width;
            case Shape.Charge: return along >= 0 && along <= length + move.Width && across <= move.Width;
            default:
                double reach = Math.Sqrt(dx * dx + dy * dy);
                return reach <= move.Size && (reach < 1 || Math.Atan2(across, along) * 180 / Math.PI <= move.Width);
        }
    }

    // Whether the player took it.
    static async Task<bool> Hit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster boss, int action,
        Move move, MonsterAttack use, (int X, int Y) from, (int X, int Y) aim, ushort sequence, int spike = 0)
    {
        MonsterHitOutcome? outcome = null;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action, () =>
        {
            if (InGracePeriod(conn)) return null;
            double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
            foreach (var golem in conn.Golems.All.Values.Concat(conn.Reservations.Fighters.Values).ToArray())
                if (!golem.Monster.Dead && Inside(move, from, aim, golem.Monster.Walk.Position(now), spike))
                    GolemTakeHitLocked(conn, golem, id, boss, use, now, area: true);
            var here = conn.Walk.Position(now);
            if (!Inside(move, from, aim, here, spike)) return null;
            outcome = CommitMonsterHitLocked(remote, conn, id, boss, use, now, here, aim, checkRange: false, sequence: sequence);
            return outcome?.HitPacket;
        }, $"0x01af Izaro 0x{id:x} {move.Name} hits the player")) return false;
        if (outcome is not null) await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
        return outcome is not null;
    }
}
