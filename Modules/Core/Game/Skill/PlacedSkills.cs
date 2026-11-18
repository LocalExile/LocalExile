using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadPlacedSkillsData()
    {
        TrapVisuals = LoadTrapVisuals();
    }

    static IReadOnlyDictionary<int, string> TrapVisuals = default!;

    static IReadOnlyDictionary<int, string> LoadTrapVisuals()
    {
        return ClientTables.Table("SkillTrapVariations").ToDictionary(r => r.Int("Id"), r => r.Text("Metadata")!);
    }
    /// <summary>A trap's or mine's object type: Metadata/MiscellaneousObjects/RemoteMine, or the SkillTrapVariations row
    /// its trap_variation names (the Rogue Exiles' 12: TrapVisible), LightningTrap without one.</summary>
    public static string PlacedObjectPath(bool mine, Func<string, int> get) => mine ? "Metadata/MiscellaneousObjects/RemoteMine"
        : TrapVisuals.GetValueOrDefault(get("trap_variation"), "Metadata/MiscellaneousObjects/LightningTrap");

    /// <summary>How long a thrown trap or a placed mine flies before it lands (trap_throw_arm_time_override, else .975 s
    /// for a trap, .6 s for a mine), and the half second after landing it arms in.</summary>
    public static double PlacedFlightSeconds(bool mine, Func<string, int> get) =>
        get("trap_throw_arm_time_override") is > 0 and var armMs ? armMs / 1000d : mine ? .6 : .975;
    public const double PlacedArmSeconds = .5;

    /// <summary>A trap's or mine's lifetime after landing: base_trap_duration (else 4 s) or base_mine_duration (else 5 s)
    /// with trap_duration_+% or mine_duration_+%.</summary>
    public static double PlacedDurationSeconds(bool mine, Func<string, int> get) =>
        Math.Max(0, get(mine ? "base_mine_duration" : "base_trap_duration") is > 0 and var ms ? ms / 1000d : mine ? 5 : 4)
            * Math.Max(.01, 1 + get(mine ? "mine_duration_+%" : "trap_duration_+%") / 100d);

    /// <summary>How near an enemy sets a trap off (cells), with trap_trigger_radius_+%. The player's traps have used
    /// 10; Trap.ot and the stats name no base radius.</summary>
    public static double TrapTriggerRadius(Func<string, int> get) => 10 * Math.Max(0, 1 + get("trap_trigger_radius_+%") / 100d);

    /// <summary>The object's stats: one life, no mana, and which kind it is (Trap.ot's is_trap_object), with the
    /// flight it was thrown for.</summary>
    public static List<(uint, int)> PlacedObjectStats(bool mine, double flight)
    {
        var stats = new List<(uint, int)>();
        foreach (var (name, value) in new[] { ("maximum_life", 1), ("uses_no_mana", 1),
            ("is_trap_object", mine ? 0 : 1), ("is_remote_mine", mine ? 1 : 0) })
            if (Stats.Key(name) is uint key) stats.Add((key, value));
        if (Stats.Key("trap_throw_arm_time_override") is uint armTime) stats.Add((armTime, (int)Math.Round(flight * 1000)));
        return stats;
    }

    /// <summary>The throw as the client draws it: the object's BeThrown action from the thrower to the aim, then its walk
    /// there over the flight.</summary>
    public static List<byte[]> PlacedThrowRecords(ClientState conn, uint id, (int X, int Y) from, (int X, int Y) aim, double flight)
    {
        List<byte[]> records = [];
        if (SkillsFor(conn).ActionSkillId("BeThrown") is ushort thrown)
            records.Add(InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, thrown, 0xffff, 0x0408));
        records.Add(InstanceProtocol.MoveTo(id, aim.X, aim.Y, (float)Math.Max(0, flight), InstanceProtocol.FollowTerrain));
        return records;
    }

    /// <summary>The landing: the throw ends, the object stands at its cell and plays Arm.</summary>
    static void PlacedLandRecords(ClientState conn, uint id, (int X, int Y) at, WorldRecordBuilder bytes)
    {
        bytes.World(id, InstanceProtocol.ActionEnded(id));
        bytes.World(id, InstanceProtocol.Teleport(id, at.X, at.Y));
        if (SkillsFor(conn).ActionSkillId("Arm") is ushort arm)
            bytes.World(id, InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, arm, 0xffff, 0x0408));
    }

    public static (bool Trap, bool Mine) PlacedKind(SupportedGem gem)
    {
        var types = (Supports.Of(gem.Effect)?.Types ?? []).Concat(gem.Supports.SelectMany(s => s.Effect.Added)).ToHashSet();
        return (types.Contains(32), types.Contains(35));
    }

    public static int PlacedSkillLimit(ClientState conn, SupportedGem gem, bool mine)
    {
        var player = CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(gem.Stats, n) + GolemStat(player, n);
        int basis = Get(mine ? "base_number_of_remote_mines_allowed" : "base_number_of_traps_allowed");
        return Math.Clamp((basis > 0 ? basis : 15)
            + Get(mine ? "number_of_additional_remote_mines_allowed" : "number_of_additional_traps_allowed"), 0, 200);
    }

    public static int PlacedSkillCount(ClientState conn, SupportedGem gem, bool mine)
    {
        var player = CharacterPlayerStats(conn);
        string additional = mine ? "number_of_additional_mines_to_place" : "number_of_additional_traps_to_throw";
        return Math.Clamp(1 + GolemStat(gem.Stats, additional) + GolemStat(player, additional), 1, 200);
    }

    static async Task<bool> PlaceSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skill, (int X, int Y) from, (int X, int Y) aim, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(conn, skill); var kind = PlacedKind(gem);
        var player = CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(gem.Stats, n) + GolemStat(player, n);
        double release = AreaTime(conn) + Math.Max(.05, (CastMs(conn, gem.Effect, skill) ?? 500) / 1000d);
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed || conn.LeavingArea
                    || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration) return false;
                if (!conn.Paused && AreaTime(conn) >= release) break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        for (int i = 0; i < PlacedSkillCount(conn, gem, kind.Mine); i++)
        {
        uint born = 0;
        double landsAt = 0;
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!current() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || !conn.Layout.WalkableFor(aim.X, aim.Y, 0)) return null;
                var context = ReservationContext(conn, skill);
                var reserve = kind.Mine ? SupportedReservation(conn, skill) : null;
                uint id = conn.TakeObjectId();
                double now = AreaTime(conn);
                double flight = PlacedFlightSeconds(kind.Mine, Get);
                double land = now + flight;
                var placed = new PlacedSkills.Placed(id, skill, gem.Effect, aim, kind.Mine, land + PlacedArmSeconds,
                    land + PlacedDurationSeconds(kind.Mine, Get),
                    SupportedHitFor(conn, skill, suppress: false), new Dictionary<uint, int>(gem.Stats),
                    reserve?.Flat ?? 0, reserve?.Percent ?? 0, context.Life, context.Efficiency) { LandAt = land, Landed = false };
                conn.Placed.All.Add(id, placed);
                if (!ReservationBudgetFits(conn)) { conn.Placed.All.Remove(id); return null; }
                var bytes = new WorldRecordBuilder(conn, admission);
                int limit = PlacedSkillLimit(conn, gem, kind.Mine);
                if (limit == 0) { conn.Placed.All.Remove(id); return null; }
                while (conn.Placed.All.Values.Count(p => p.Mine == kind.Mine) > limit)
                    RemovePlacedLocked(conn, conn.Placed.All.Values.Where(p => p.Mine == kind.Mine).MinBy(p => p.Armed)!, bytes);
                string path = PlacedObjectPath(kind.Mine, Get);
                var payload = new Dictionary<uint, int>(gem.Stats);
                foreach (string flag in new[] { "is_trap", "is_remote_mine", "base_skill_is_trapped", "base_skill_is_mined" })
                    if (Stats.Key(flag) is uint key) payload[key] = 0;
                if (Stats.Key("active_skill_area_of_effect_radius") is uint radius)
                    payload[radius] = (int)SkillRadius(conn, gem.Stats, true, gem.Effect);
                if (Stats.Key("active_skill_base_area_of_effect_radius") is uint baseRadius)
                    payload[baseRadius] = SkillArea.BaseRadius(SkillsFor(conn).EffectId(gem.Effect), Get);
                var proxy = SkillsFor(conn).FindByRow(0x4000, gem.Effect, gem.Level, [.. payload.Select(p => (p.Key, p.Value))]);
                if (proxy is null) { conn.Placed.All.Remove(id); return null; }
                proxy = proxy with { Parent = (PlayerId, skill) };
                var stats = PlacedObjectStats(kind.Mine, flight);
                bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path), new TrapMineObject(from.X, from.Y, kind.Mine, [proxy], stats).Blob()));
                bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
                if (!conn.LockstepClient)
                    foreach (var record in PlacedThrowRecords(conn, id, from, aim, flight)) bytes.Presentation(id, record);
                born = id; landsAt = land;
                conn.Placed.Dirty = true;
                conn.Minions.StatRevision++;
                ClampReservedResources(conn);
                PlacedResourcesLocked(conn, bytes);
                return bytes.Build();
            }
        });
        if (conn.LockstepClient && born != 0)
            await SendWorldRecords(remote, stream, ciphers, conn, born, () =>
            {
                lock (conn.Monsters)
                {
                    if (!current() || conn.Closed || conn.LeavingArea || !conn.Placed.All.ContainsKey(born)) return null;
                    return [.. PlacedThrowRecords(conn, born, from, aim, landsAt - AreaTime(conn))];
                }
            }, "throw trap/mine after birth");
        }
        return true;
    }

    static void RemovePlacedLocked(ClientState conn, PlacedSkills.Placed placed, WorldRecordBuilder bytes)
    {
        if (!conn.Placed.All.Remove(placed.Id)) return;
        if (!conn.Placed.Grounds.Any(g => g.Object == placed.Id))
            bytes.Remove(placed.Id);
        conn.Placed.Dirty = true;
        conn.Minions.StatRevision++;
    }

    static void PlacedResourcesLocked(ClientState conn, WorldRecordBuilder bytes)
    {
        conn.Placed.Dirty = false;
        bytes.World(PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife));
        bytes.World(PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool));
    }

    public static void LandPlacedLocked(ClientState conn, PlacedSkills.Placed placed, double now, List<byte> bytes)
    {
        var records = new WorldRecordBuilder(conn, null);
        LandPlacedRecordsLocked(conn, placed, now, records);
        bytes.AddRange(records.Bytes());
    }

    public static void PlacedGroundsLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now, List<byte> bytes)
    {
        var records = new WorldRecordBuilder(conn, null);
        PlacedGroundRecordsLocked(remote, stream, ciphers, conn, now, records);
        bytes.AddRange(records.Bytes());
    }

    public static void DetonateMinesLocked(ClientState conn, double now)
    {
        conn.Placed.Chain = 0; conn.Placed.NextChain = now;
        foreach (var mine in conn.Placed.All.Values.Where(p => p.Mine && p.Armed <= now).OrderBy(p => p.Armed))
            mine.DetonateAt = now + .2 * conn.Placed.Chain++;
    }

    public static bool CanDetonateMines(ClientState conn) =>
        conn.Placed.All.Values.Any(p => p.Mine && p.Armed <= AreaTime(conn));

    static void LandPlacedRecordsLocked(ClientState conn, PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        if (placed.Landed || now < placed.LandAt) return;
        placed.Landed = true;
        PlacedLandRecords(conn, placed.Id, placed.At, bytes);
    }

    static void PlacedDamageLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        bool bear = SkillsFor(conn).EffectId(placed.Effect) is "BearTrap" or "BearTrapAltX";
        double radius = SkillRadius(conn, placed.Own, spell: true, placed.Effect);
        bool spellDamage = SkillHitPlan.ForSkill(SkillsFor(conn), placed.Effect, 1).Kind != SkillHitKind.Secondary;
        var target = conn.Monsters.FirstOrDefault(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
            && Dist(p.Value.Walk.Position(now), placed.At) <= 60);
        if (bear) target = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                && Dist(p.Value.Walk.Position(now), placed.At) <= radius)
            .OrderBy(p => Dist(p.Value.Walk.Position(now), placed.At)).FirstOrDefault();
        var aim = target.Value?.Walk.Position(now) ?? placed.At;
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
        if (SkillsFor(conn).EffectId(placed.Effect) == "FireTrap")
        {
            var playerSources = CharacterPlayerStats(conn);
            int Get(string n) => GolemStat(placed.Own, n) + GolemStat(playerSources, n);
            double increased = new[] { "damage_+%", "fire_damage_+%", "elemental_damage_+%", "damage_over_time_+%",
                "fire_damage_over_time_+%", "burn_damage_+%", "area_damage_+%" }.Sum(Get);
            if (Get("spell_damage_modifiers_apply_to_skill_dot") != 0) increased += Get("spell_damage_+%");
            double groundMore = new[] { "damage_+%_final", "fire_damage_+%_final", "elemental_damage_+%_final",
                "damage_over_time_+%_final", "trap_damage_+%_final", "area_damage_+%_final" }
                .Aggregate(1d, (factor, n) => factor * Math.Max(0, 1 + Get(n) / 100d));
            double dps = Get("base_fire_damage_to_deal_per_minute") / 60d * Math.Max(0, 1 + increased / 100d)
                * Math.Max(0, 1 + (Get("dot_multiplier_+") + Get("fire_dot_multiplier_+")) / 100d) * groundMore;
            var damage = new SkillDamageContext(SkillHitKind.Spell, playerSources)
                .OverTime(new(DamageType.Fire, dps, 1));
            double seconds = SkillDurationModifiers.Seconds(Get("base_skill_effect_duration"),
                n => GolemStat(playerSources, n), n => GolemStat(placed.Own, n));
            conn.Placed.Grounds.Add(new(placed.Id, placed.Skill, placed.At, radius, damage.DamagePerSecond, now + seconds, now));
        }
        double more = ReservationAura(conn, "SummonSkitterbots") is { } skitter
            ? Math.Max(0, 1 + ReservationStat(conn, skitter, "skitterbots_trap_mine_damage_+%_final") / 100d) : 1;
        var player = CharacterPlayerStats(conn);
        foreach (var (id, m) in conn.Monsters)
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                || bear && id != target.Key || Dist(m.Walk.Position(now), placed.At) > radius) continue;
            var rolled = placed.Damage.Scale(more).Roll(Random.Shared);
            var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesMitigated(m, mitigated, spellDamage, now);
            if (ApplyMonsterHitLocked(conn, m, damage, allyAt: placed.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "trap/mine", playerKill: false);
            if (!m.Dead)
                m.Ailments.Inflict(mitigated, false, m.MaxLife, now,
                    n => GolemStat(placed.Own, n) + GolemStat(player, n), n => m.Effects[n], Random.Shared);
        }
    }

    static void PlacedGroundRecordsLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now, WorldRecordBuilder bytes)
    {
        // Burning grounds do not stack. Keep a separate fractional carry and stop exactly at expiry.
        foreach (var (id, monster) in conn.Monsters.ToArray())
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.Invulnerable) continue;
            var ground = conn.Placed.Grounds.Where(g => g.LastAt < Math.Min(now, g.Until)
                    && Dist(monster.Walk.Position(now), g.At) <= g.Radius)
                .MaxBy(g => g.DamagePerSecond);
            if (ground is null) continue;
            double total = ground.DamagePerSecond * (Math.Min(now, ground.Until) - ground.LastAt)
                * MonsterDotTaken(monster, DamageType.Fire, now) + ground.Carry.GetValueOrDefault(id);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(total)); ground.Carry[id] = total - damage;
            if (damage <= 0 || ApplyMonsterHitLocked(conn, monster, damage, allyAt: ground.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, monster, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, monster, hit, damage, now, "Fire Trap burning ground", playerKill: false);
        }
        foreach (var ground in conn.Placed.Grounds.ToArray())
        {
            ground.LastAt = Math.Min(now, ground.Until);
            if (now < ground.Until && conn.PlayerLife > 0) continue;
            bytes.Remove(ground.Object);
            conn.Placed.Grounds.Remove(ground);
        }
    }

    static async Task PlacedSkillsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        await SendWorldBatch(conn, [], admission =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                var bytes = new WorldRecordBuilder(conn, admission);
                foreach (var placed in conn.Placed.All.Values.ToArray())
                {
                    if (conn.LockstepClient && !conn.WorldOutput.PublishedObject(placed.Id).IsReady) continue;
                    if (conn.PlayerLife == 0 || now >= placed.Until) { RemovePlacedLocked(conn, placed, bytes); continue; }
                    LandPlacedRecordsLocked(conn, placed, now, bytes);
                    if (now < placed.Armed) continue;
                    bool skitter = false;
                    if (!placed.SkitterTriggered)
                    {
                        var bot = conn.Reservations.Fighters.Values.Where(g => g.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal) && now >= g.NextOrb)
                            .MinBy(g => Dist(g.Monster.Walk.Position(now), placed.At));
                        if (bot is not null)
                        {
                            if (Dist(bot.Monster.Walk.Position(now), placed.At) > 8) GolemMoveLocked(conn, bot, placed.At, now);
                            else { skitter = true; bot.NextOrb = now + 3; }
                        }
                    }
                    double trigger = TrapTriggerRadius(n => GolemStat(placed.Own, n));
                    bool nearby = !placed.Mine && conn.Monsters.Values.Any(m => m is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(m.Walk.Position(now), placed.At) <= trigger);
                    if (!skitter && !nearby && now < placed.DetonateAt) continue;
                    PlacedDamageLocked(remote, stream, ciphers, conn, placed, now, bytes);
                    Console.WriteLine($"[gateway] placed skill 0x{placed.Id:x} {(skitter ? "Skitterbot" : nearby ? "proximity" : "chain")} trigger; rearm={skitter && placed.Mine && SkillsFor(conn).EffectId(placed.Effect) != "SmokeMine"}");
                    if (skitter && placed.Mine && SkillsFor(conn).EffectId(placed.Effect) != "SmokeMine")
                    {
                        placed.SkitterTriggered = true; placed.DetonateAt = double.PositiveInfinity;
                        conn.Placed.All[placed.Id] = placed with { Armed = now + .5 };
                    }
                    else RemovePlacedLocked(conn, placed, bytes);
                }
                if (conn.Placed.Dirty) PlacedResourcesLocked(conn, bytes);
                bool detonation = CanDetonateMines(conn);
                if (detonation != conn.Placed.DetonationPublished)
                {
                    bytes.World(PlayerId, InstanceEntities.PlayerSkillsChange(PlayerId, [], [], canDetonateMines: detonation));
                    conn.Placed.DetonationPublished = detonation;
                }
                PlacedGroundRecordsLocked(remote, stream, ciphers, conn, now, bytes);
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        });
    }
}
