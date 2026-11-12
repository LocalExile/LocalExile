using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core;

public static partial class Game
{
    static readonly Lazy<IReadOnlyDictionary<int, string>> TrapVisuals = new(() =>
    {
        return ClientTables.Table("SkillTrapVariations").ToDictionary(r => r.Int("Id"), r => r.Text("Metadata")!);
    });
    public static (bool Trap, bool Mine) PlacedKind(SupportedGem gem)
    {
        var types = (Supports.Of(gem.Effect)?.Types ?? []).Concat(gem.Supports.SelectMany(s => s.Effect.Added)).ToHashSet();
        return (types.Contains(32), types.Contains(35));
    }

    private sealed record PlacedCastAdmission(ushort Skill, int Effect, bool Trap, bool Mine,
        GemSkillIds.Gem Identity, object Area, object? Lifetime, GemSourceLifetimes.SourceLifetime Source);

    // The family decision belongs to the original cast, before its first output/grace/payment await. The captured
    // lifetime keeps the numeric skill id from adopting a physical source that was removed and reinserted.
    private static bool TryCapturePlacedCastAdmissionLocked(ClientState conn, ushort skill,
        out PlacedCastAdmission? admission)
    {
        admission = null;
        if (!CurrentGameWork || !CurrentGameRules
            || CurrentPlayerSkillSourceLocked(conn, skill) is not { Active: true } source)
            return false;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var supported = SupportedSkill(conn, skill);
        var kind = PlacedKind(supported);
        if (supported.Effect < 0 || supported.Effect != source.Effect) return false;
        // A valid ordinary source has no placed family and keeps its existing path without a physical lease binding.
        if (!kind.Trap && !kind.Mine) return true;
        if (!supported.RequirementsMet) return false;
        admission = new(skill, source.Effect, kind.Trap, kind.Mine, source.Identity, conn.Instance, CombatLifetime,
            CaptureGemSourceLifetimeLocked(conn, source));
        return true;
    }

    private static bool PlacedCastStructuralOwnerCurrentLocked(ClientState conn, PlacedCastAdmission admission,
        int? recovery = null)
    {
        return CurrentGameWork && CurrentGameRules && !conn.Closed && !conn.LeavingArea && !conn.RageRetired
            && conn.PlayerLife != 0 && ReferenceEquals(conn.Instance, admission.Area)
            && ReferenceEquals(CombatLifetime, admission.Lifetime)
            && (recovery is not int generation || generation == conn.ActionInterruptionGeneration)
            && conn.GemSourceLifetimes.IsCurrent(admission.Source);
    }

    private static bool PlacedCastAdmissionCurrentLocked(ClientState conn, PlacedCastAdmission admission,
        SupportedGem supported, int? recovery = null)
    {
        if (!PlacedCastStructuralOwnerCurrentLocked(conn, admission, recovery))
            return false;
        var source = CurrentPlayerSkillSourceLocked(conn, admission.Skill);
        var kind = PlacedKind(supported);
        return supported.Effect >= 0 && supported.RequirementsMet && supported.Effect == admission.Effect
            && kind.Trap == admission.Trap && kind.Mine == admission.Mine
            && source is { Active: true } && source.Effect == admission.Effect
            && source.Identity == admission.Identity && source.Identity == admission.Source.Key;
    }

    private static bool PlacedCastFamilyCurrent(ClientState conn, ushort skill, PlacedCastAdmission? admission)
    {
        lock (conn.Monsters)
        {
            if (admission is not null && !PlacedCastStructuralOwnerCurrentLocked(conn, admission))
                return false;
            using var scope = new PlayerStatScope(conn, fresh: true);
            var supported = SupportedSkill(conn, skill);
            if (admission is not null) return PlacedCastAdmissionCurrentLocked(conn, admission, supported);
            var kind = PlacedKind(supported);
            return supported.Effect >= 0 && !kind.Trap && !kind.Mine;
        }
    }

    private static bool PlacedCastAdmissionCurrent(ClientState conn, PlacedCastAdmission admission, int? recovery = null)
    {
        lock (conn.Monsters)
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(conn, admission, recovery))
                return false;
            using var scope = new PlayerStatScope(conn, fresh: true);
            return PlacedCastAdmissionCurrentLocked(conn, admission, SupportedSkill(conn, admission.Skill), recovery);
        }
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

    // Preserve the existing placement-time offensive snapshot. A recipient's Blind is selected only at contact.
    static PlacedSkills.DamageSnapshot? CapturePlacedDamage(ClientState conn, ushort skill, SupportedGem gem)
    {
        if (SupportedHitInputsFor(conn, skill, capturedGem: gem) is not { } inputs) return null;
        bool spell = !inputs.Attack;
        long additionalIncreased = inputs.Get(PlayerHitDamage.VsBlindedDamageStat);
        PlacedSkills.DamageProfile Profile(long increased) => new(
            SupportedHitFrom(inputs, suppress: true, additionalIncreased: increased),
            SupportedIgnite(conn, skill, spell, inputs.Caster, additionalIncreased: increased, capturedInputs: inputs),
            SupportedBleed(conn, skill, spell, inputs.Caster, melee: inputs.Melee,
                additionalIncreased: increased, capturedInputs: inputs),
            SupportedPoison(conn, skill, spell, inputs.Caster, additionalIncreased: increased, capturedInputs: inputs));
        var normal = Profile(0);
        return new(normal, additionalIncreased == 0 ? normal : Profile(additionalIncreased), inputs.Caster, spell)
            { Spider = CaptureSpiderDamage(conn, inputs, AddedEffectiveness(conn, gem.Effect, gem.Level), context: true,
                local: inputs.Attack ? MainHandLocalSources(conn) : null) };
    }

    static async Task<bool> PlaceSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) from, (int X, int Y) aim, PlacedCastAdmission admission, int recovery, Func<bool> current)
    {
        lock (conn.Monsters)
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(conn, admission, recovery)) return false;
        }
        double release = AreaTime(conn) + Math.Max(.05, (CastMs(conn, admission.Effect, admission.Skill) ?? 500) / 1000d);
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!PlacedCastStructuralOwnerCurrentLocked(conn, admission, recovery) || !current()) return false;
                if (!conn.Paused && AreaTime(conn) >= release) break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        bool accepted = false;
        var born = new List<(uint Id, double Land)>();
        await SendWorldBatch(conn, [], worldAdmission =>
        {
            lock (conn.Monsters)
            {
                if (!PlacedCastStructuralOwnerCurrentLocked(conn, admission, recovery)
                    || !current() || !conn.Layout.WalkableFor(aim.X, aim.Y, 0)) return null;
                using var scope = new PlayerStatScope(conn, fresh: true);
                var gem = SupportedSkill(conn, admission.Skill);
                if (!PlacedCastAdmissionCurrentLocked(conn, admission, gem, recovery)) return null;
                var player = CharacterPlayerStats(conn);
                int Get(string n) => GolemStat(gem.Stats, n) + GolemStat(player, n);
                int count = PlacedSkillCount(conn, gem, admission.Mine);
                int limit = PlacedSkillLimit(conn, gem, admission.Mine);
                if (count <= 0 || limit <= 0) return null;
                var context = ReservationContext(conn, admission.Skill);
                var reserve = admission.Mine ? SupportedReservation(conn, admission.Skill) : null;
                var offense = CapturePlacedDamage(conn, admission.Skill, gem);
                var damage = offense?.Normal.Hit ?? SupportedHitFor(conn, admission.Skill, suppress: false);
                var payload = new Dictionary<uint, int>(gem.Stats);
                foreach (string flag in new[] { "is_trap", "is_remote_mine", "base_skill_is_trapped", "base_skill_is_mined" })
                    if (Stats.Key(flag) is uint key) payload[key] = 0;
                if (Stats.Key("active_skill_area_of_effect_radius") is uint radius)
                    payload[radius] = (int)SkillRadius(conn, gem.Stats, true, gem.Effect);
                if (Stats.Key("active_skill_base_area_of_effect_radius") is uint baseRadius)
                    payload[baseRadius] = SkillArea.BaseRadius(SkillsFor(conn).EffectId(gem.Effect), Get);
                var proxy = SkillsFor(conn).FindByRow(0x4000, gem.Effect, gem.Level, [.. payload.Select(p => (p.Key, p.Value))]);
                if (proxy is null) return null;
                proxy = proxy with { Parent = (PlayerId, admission.Skill) };
                string path = admission.Mine
                    ? "Metadata/MiscellaneousObjects/RemoteMine"
                    : TrapVisuals.Value.GetValueOrDefault(Get("trap_variation"), "Metadata/MiscellaneousObjects/LightningTrap");
                var stats = new List<(uint, int)>();
                foreach (var (name, value) in new[] { ("maximum_life", 1), ("uses_no_mana", 1),
                    ("is_trap_object", admission.Trap ? 1 : 0), ("is_remote_mine", admission.Mine ? 1 : 0) })
                    if (Stats.Key(name) is uint key) stats.Add((key, value));
                double now = AreaTime(conn);
                double flight = Get("trap_throw_arm_time_override") is > 0 and var armMs ? armMs / 1000d : admission.Mine ? .6 : .975;
                if (Stats.Key("trap_throw_arm_time_override") is uint armTime) stats.Add((armTime, (int)Math.Round(flight * 1000)));
                var bytes = new WorldRecordBuilder(conn, worldAdmission);
                bool committed = false;
                for (int i = 0; i < count; i++)
                {
                    uint id = conn.NextObjectId++;
                    double land = now + flight;
                    var placement = new PlacedSkills.Placed(id, admission.Skill, gem.Effect, aim, admission.Mine, land + .5,
                        land + Math.Max(0, Get(admission.Mine ? "base_mine_duration" : "base_trap_duration") is > 0 and var ms ? ms / 1000d : admission.Mine ? 5 : 4)
                            * Math.Max(.01, 1 + Get(admission.Mine ? "mine_duration_+%" : "trap_duration_+%") / 100d),
                        damage, new Dictionary<uint, int>(gem.Stats), reserve?.Flat ?? 0, reserve?.Percent ?? 0,
                        context.Life, context.Efficiency) { LandAt = land, Landed = false, Offense = offense };
                    conn.Placed.All.Add(id, placement);
                    if (!ReservationBudgetFits(conn))
                    {
                        conn.Placed.All.Remove(id);
                        break;
                    }
                    while (conn.Placed.All.Values.Count(p => p.Mine == admission.Mine) > limit)
                    {
                        var trim = conn.Placed.All.Values.Where(p => p.Mine == admission.Mine).MinBy(p => (p.Armed, p.Id))!;
                        if (trim.Id == id)
                        {
                            // This object has not published a create in this packet yet. Removing it is local state
                            // cleanup, not a native RemoveObject for an entity the client never saw.
                            conn.Placed.All.Remove(id);
                            conn.Placed.Dirty = true;
                            conn.Minions.StatRevision++;
                        }
                        else RemovePlacedLocked(conn, trim, bytes);
                    }
                    if (!conn.Placed.All.ContainsKey(id)) continue;
                    bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path),
                        new TrapMineObject(from.X, from.Y, admission.Mine, [proxy], stats).Blob()));
                    bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
                    if (!conn.LockstepClient)
                    {
                        if (SkillsFor(conn).ActionSkillId("BeThrown") is ushort thrown)
                            bytes.Presentation(id, InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, thrown, 0xffff, 0x0408));
                        bytes.Presentation(id, InstanceProtocol.MoveTo(id, aim.X, aim.Y, (float)flight, InstanceProtocol.FollowTerrain));
                    }
                    born.Add((id, land));
                    committed = true;
                }
                if (!committed) return null;
                RecordAcceptedGemUse(conn, gem.Effect);
                conn.Placed.Dirty = true;
                conn.Minions.StatRevision++;
                ClampReservedResources(conn);
                PlacedResourcesLocked(conn, bytes);
                accepted = true;
                return bytes.Build();
            }
        }, transport: (remote, stream, ciphers));
        if (conn.LockstepClient)
            foreach (var (id, landsAt) in born)
                await SendWorldRecords(remote, stream, ciphers, conn, id, () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!PlacedCastStructuralOwnerCurrentLocked(conn, admission, recovery)
                            || !current() || !conn.Placed.All.ContainsKey(id)) return null;
                        var records = new List<byte[]>();
                        if (SkillsFor(conn).ActionSkillId("BeThrown") is ushort thrown)
                            records.Add(InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, thrown, 0xffff, 0x0408));
                        records.Add(InstanceProtocol.MoveTo(id, aim.X, aim.Y,
                            (float)Math.Max(0, landsAt - AreaTime(conn)), InstanceProtocol.FollowTerrain));
                        return [.. records];
                    }
                }, "throw trap/mine after birth");
        return accepted;
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
        bytes.World(placed.Id, InstanceProtocol.ActionEnded(placed.Id));
        bytes.World(placed.Id, InstanceProtocol.Teleport(placed.Id, placed.At.X, placed.At.Y));
        if (SkillsFor(conn).ActionSkillId("Arm") is ushort arm)
            bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y, placed.At.X, placed.At.Y, arm, 0xffff, 0x0408));
    }

    static void PlacedDamageLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        var target = conn.Monsters.FirstOrDefault(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
            && Dist(p.Value.Walk.Position(now), placed.At) <= 60);
        var aim = target.Value?.Walk.Position(now) ?? placed.At;
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
        double radius = SkillRadius(conn, placed.Own, spell: true, placed.Effect);
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
        var player = placed.Offense?.Caster ?? CharacterPlayerStats(conn);
        bool spell = placed.Offense?.Spell ?? true;
        foreach (var (id, m) in conn.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || conn.AsleepMonsters.Contains(id) || Dist(m.Walk.Position(now), placed.At) > radius
                || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) continue;
            var profile = placed.Offense is { } captured
                ? Blinded(conn, id) ? captured.Blinded : captured.Normal
                : new PlacedSkills.DamageProfile(placed.Damage, MonsterIgnites.Settings.None,
                    MonsterIgnites.Settings.None, MonsterPoisons.Settings.None);
            if (placed.Offense?.Spider is { } spider)
                profile = SpiderDamageProfile(spider, m.SpiderWebs.Count(now), Blinded(conn, id),
                    m.Ignites.Burning, m.Bleeds.Burning, EnemyOnLowLife(m));
            var rolled = profile.Hit.Scale(more).Roll(Random.Shared);
            var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesMitigated(m, mitigated, spell, now);
            if (placed.Mine)
            {
                // This is an explicit owner-leech grant, not the player's ordinary attack/spell leech.
                var owner = CharacterPlayerStats(conn);
                int coefficient = Math.Max(0, GolemStat(owner, "mine_damage_leeched_as_life_to_you_permyriad"));
                if (coefficient > 0)
                {
                    var life = DamageTypes.ConversionOrder.Aggregate(DamagePacket.Empty,
                        (sum, type) => sum.Add(DamagePacket.Of(type, coefficient, coefficient)));
                    LeechOnHitLocked(conn, m, MonsterTakenComponents(m, mitigated, spell, now), damage,
                        new(life, DamagePacket.Empty, DamagePacket.Empty));
                }
            }
            if (ApplyMonsterHitLocked(conn, m, damage, allyAt: placed.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            var poison = profile.Poison with { Min = profile.Poison.Min * more, Max = profile.Poison.Max * more,
                CriticalMin = profile.Poison.CriticalMin * more, CriticalMax = profile.Poison.CriticalMax * more };
            AilmentsOnHitLocked(conn, id, m, mitigated, spell, critical: false, 100, player, placed.Own, now,
                Random.Shared, poison: poison, sourceSkill: placed.Skill, blind: BlindHitInput.Capture(player, placed.Own));
            if (!m.Dead && profile.Ignite.Max > 0)
            {
                var ignite = profile.Ignite with { Min = profile.Ignite.Min * more, Max = profile.Ignite.Max * more,
                    CriticalMin = profile.Ignite.CriticalMin * more, CriticalMax = profile.Ignite.CriticalMax * more };
                foreach (var packet in IgniteOnHitLocked(conn, id, m, ignite, critical: false, now, playerIgnite: false) ?? [])
                    conn.Minions.OutputQueue.Add(new(packet, "trap/mine ignite", id, m));
            }
            if (!m.Dead && !spell && rolled[DamageType.Physical].Max > 0)
            {
                var bleed = profile.Bleed with { Min = profile.Bleed.Min * more, Max = profile.Bleed.Max * more,
                    CriticalMin = profile.Bleed.CriticalMin * more, CriticalMax = profile.Bleed.CriticalMax * more };
                foreach (var packet in BleedOnHitLocked(conn, id, m, bleed, critical: false, now) ?? [])
                    conn.Minions.OutputQueue.Add(new(packet, "trap/mine bleed", id, m));
            }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "trap/mine", playerKill: false);
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
                    bool nearby = !placed.Mine && conn.Monsters.Values.Any(m => m is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(m.Walk.Position(now), placed.At) <= 10);
                    if (!skitter && !nearby && now < placed.DetonateAt) continue;
                    // Scheduling, an unarmed mine, a retired placement and a Skitterbot's detonation are not a
                    // player detonation. Record only this accepted player-chain trigger, even without a hit target.
                    if (placed.Mine && !skitter)
                        conn.RecentEvents.Record(StatConditions.DetonatedMine, conn.Clock.Elapsed.TotalSeconds);
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
        }, transport: (remote, stream, ciphers));
    }
}
