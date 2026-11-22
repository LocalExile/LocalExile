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
    public static string PlacedObjectPath(bool mine, Func<uint, int> get) => mine ? "Metadata/MiscellaneousObjects/RemoteMine"
        : TrapVisuals.GetValueOrDefault(get(Stat.TrapVariation), "Metadata/MiscellaneousObjects/LightningTrap");

    /// <summary>How long a thrown trap or a placed mine flies before it lands (trap_throw_arm_time_override, else .975 s
    /// for a trap, .6 s for a mine), and the half second after landing it arms in.</summary>
    public static double PlacedFlightSeconds(bool mine, Func<uint, int> get) =>
        get(Stat.TrapThrowArmTimeOverride) is > 0 and var armMs ? armMs / 1000d : mine ? .6 : .975;
    public const double PlacedArmSeconds = .5;

    /// <summary>A trap's or mine's lifetime after landing: base_trap_duration (else 4 s) or base_mine_duration (else 5 s)
    /// with trap_duration_+% or mine_duration_+%.</summary>
    public static double PlacedDurationSeconds(bool mine, Func<uint, int> get) =>
        Math.Max(0, get(mine ? Stat.BaseMineDuration : Stat.BaseTrapDuration) is > 0 and var ms ? ms / 1000d : mine ? 5 : 4)
            * Math.Max(.01, 1 + get(mine ? Stat.MineDurationPlusPct : Stat.TrapDurationPlusPct) / 100d);

    /// <summary>How near an enemy sets a trap off (cells), with trap_trigger_radius_+%. The player's traps have used
    /// 10; Trap.ot and the stats name no base radius.</summary>
    public static double TrapTriggerRadius(Func<uint, int> get) => 10 * Math.Max(0, 1 + get(Stat.TrapTriggerRadiusPlusPct) / 100d);

    /// <summary>The object's stats: one life, no mana, and which kind it is (Trap.ot's is_trap_object), with the
    /// flight it was thrown for.</summary>
    public static List<(uint, int)> PlacedObjectStats(bool mine, double flight)
    {
        var stats = new List<(uint, int)>();
        foreach (var (name, value) in new[] { (Stat.MaximumLife, 1), (Stat.UsesNoMana, 1),
            (Stat.IsTrapObject, mine ? 0 : 1), (Stat.IsRemoteMine, mine ? 1 : 0) })
            if (name is var key) stats.Add((key, value));
        if (Stat.TrapThrowArmTimeOverride is var armTime) stats.Add((armTime, (int)Math.Round(flight * 1000)));
        return stats;
    }

    /// <summary>The throw as the client draws it: the object's BeThrown action from the thrower to the aim, then its walk
    /// there over the flight.</summary>
    public static List<byte[]> PlacedThrowRecords(Player player, uint id, (int X, int Y) from, (int X, int Y) aim, double flight)
    {
        List<byte[]> records = [];
        if (SkillsFor(player).ActionSkillId("BeThrown") is ushort thrown)
            records.Add(InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, thrown, 0xffff, 0x0408));
        records.Add(InstanceProtocol.MoveTo(id, aim.X, aim.Y, (float)Math.Max(0, flight), InstanceProtocol.FollowTerrain));
        return records;
    }

    /// <summary>The landing: the throw ends, the object stands at its cell and plays Arm.</summary>
    static void PlacedLandRecords(Player player, uint id, (int X, int Y) at, WorldRecordBuilder bytes)
    {
        bytes.World(id, InstanceProtocol.ActionEnded(id));
        bytes.World(id, InstanceProtocol.Teleport(id, at.X, at.Y));
        if (SkillsFor(player).ActionSkillId("Arm") is ushort arm)
            bytes.World(id, InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, arm, 0xffff, 0x0408));
    }

    public static (bool Trap, bool Mine) PlacedKind(SupportedGem gem)
    {
        var types = (Supports.Of(gem.Effect)?.Types ?? []).Concat(gem.Supports.SelectMany(s => s.Effect.Added)).ToHashSet();
        return (types.Contains(32), types.Contains(35));
    }

    private sealed record PlacedCastAdmission(ushort Skill, int Effect, bool Trap, bool Mine,
        GemSkillIds.Gem Identity, object Area, object? Lifetime, GemSourceLifetimes.SourceLifetime Source);

    // The family decision belongs to the original cast, before its first output/grace/payment await. The captured
    // lifetime keeps the numeric skill id from adopting a physical source that was removed and reinserted.
    private static bool TryCapturePlacedCastAdmissionLocked(Player player, ushort skill,
        out PlacedCastAdmission? admission)
    {
        admission = null;
        if (!CurrentGameWork || !CurrentGameRules
            || CurrentPlayerSkillSourceLocked(player, skill) is not { Active: true } source)
            return false;
        using var scope = new PlayerStatScope(player, fresh: true);
        var supported = SupportedSkill(player, skill);
        var kind = PlacedKind(supported);
        if (supported.Effect < 0 || supported.Effect != source.Effect) return false;
        // A valid ordinary source has no placed family and keeps its existing path without a physical lease binding.
        if (!kind.Trap && !kind.Mine) return true;
        if (!supported.RequirementsMet) return false;
        admission = new(skill, source.Effect, kind.Trap, kind.Mine, source.Identity, player.Instance, CombatLifetime,
            CaptureGemSourceLifetimeLocked(player, source));
        return true;
    }

    private static bool PlacedCastStructuralOwnerCurrentLocked(Player player, PlacedCastAdmission admission,
        int? recovery = null)
    {
        return CurrentGameWork && CurrentGameRules && !player.Closed && !player.LeavingArea && !player.RageRetired
            && player.PlayerLife != 0 && ReferenceEquals(player.Instance, admission.Area)
            && ReferenceEquals(CombatLifetime, admission.Lifetime)
            && (recovery is not int generation || generation == player.ActionInterruptionGeneration)
            && player.GemSourceLifetimes.IsCurrent(admission.Source);
    }

    private static bool PlacedCastAdmissionCurrentLocked(Player player, PlacedCastAdmission admission,
        SupportedGem supported, int? recovery = null)
    {
        if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery))
            return false;
        var source = CurrentPlayerSkillSourceLocked(player, admission.Skill);
        var kind = PlacedKind(supported);
        return supported.Effect >= 0 && supported.RequirementsMet && supported.Effect == admission.Effect
            && kind.Trap == admission.Trap && kind.Mine == admission.Mine
            && source is { Active: true } && source.Effect == admission.Effect
            && source.Identity == admission.Identity && source.Identity == admission.Source.Key;
    }

    private static bool PlacedCastFamilyCurrent(Player player, ushort skill, PlacedCastAdmission? admission)
    {
        {
            if (admission is not null && !PlacedCastStructuralOwnerCurrentLocked(player, admission))
                return false;
            using var scope = new PlayerStatScope(player, fresh: true);
            var supported = SupportedSkill(player, skill);
            if (admission is not null) return PlacedCastAdmissionCurrentLocked(player, admission, supported);
            var kind = PlacedKind(supported);
            return supported.Effect >= 0 && !kind.Trap && !kind.Mine;
        }
    }

    private static bool PlacedCastAdmissionCurrent(Player player, PlacedCastAdmission admission, int? recovery = null)
    {
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery))
                return false;
            using var scope = new PlayerStatScope(player, fresh: true);
            return PlacedCastAdmissionCurrentLocked(player, admission, SupportedSkill(player, admission.Skill), recovery);
        }
    }

    public static int PlacedSkillLimit(Player player, SupportedGem gem, bool mine)
    {
        var playerStats = CharacterPlayerStats(player);
        int Get(uint n) => GolemStat(gem.Stats, n) + GolemStat(playerStats, n);
        int basis = Get(mine ? Stat.BaseNumberOfRemoteMinesAllowed : Stat.BaseNumberOfTrapsAllowed);
        return Math.Clamp((basis > 0 ? basis : 15)
            + Get(mine ? Stat.NumberOfAdditionalRemoteMinesAllowed : Stat.NumberOfAdditionalTrapsAllowed), 0, 200);
    }

    public static int PlacedSkillCount(Player player, SupportedGem gem, bool mine)
    {
        var playerStats = CharacterPlayerStats(player);
        uint additional = mine ? Stat.NumberOfAdditionalMinesToPlace : Stat.NumberOfAdditionalTrapsToThrow;
        return Math.Clamp(1 + GolemStat(gem.Stats, additional) + GolemStat(playerStats, additional), 1, 200);
    }

    // Preserve the existing placement-time offensive snapshot. A recipient's Blind is selected only at contact.
    static PlacedSkills.DamageSnapshot? CapturePlacedDamage(Player player, ushort skill, SupportedGem gem)
    {
        if (SupportedHitInputsFor(player, skill, capturedGem: gem) is not { } inputs) return null;
        bool spell = !inputs.Attack;
        long additionalIncreased = inputs.Get(PlayerHitDamage.VsBlindedDamageStat);
        PlacedSkills.DamageProfile Profile(long increased) => new(
            SupportedHitFrom(inputs, suppress: true, additionalIncreased: increased),
            SupportedIgnite(player, skill, spell, inputs.Caster, additionalIncreased: increased, capturedInputs: inputs),
            SupportedBleed(player, skill, spell, inputs.Caster, melee: inputs.Melee,
                additionalIncreased: increased, capturedInputs: inputs),
            SupportedPoison(player, skill, spell, inputs.Caster, additionalIncreased: increased, capturedInputs: inputs));
        var normal = Profile(0);
        return new(normal, additionalIncreased == 0 ? normal : Profile(additionalIncreased), inputs.Caster, spell)
            { Spider = CaptureSpiderDamage(player, inputs, AddedEffectiveness(player, gem.Effect, gem.Level), context: true,
                local: inputs.Attack ? MainHandLocalSources(player) : null) };
    }

    static async Task<bool> PlaceSkill(Player player,
        (int X, int Y) from, (int X, int Y) aim, PlacedCastAdmission admission, int recovery, Func<bool> current)
    {
        if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery)) return false;
        double release = AreaTime(player) + Math.Max(.05, (CastMs(player, admission.Effect, admission.Skill) ?? 500) / 1000d);
        while (true)
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery) || !current()) return false;
            if (!player.Paused && AreaTime(player) >= release) break;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        bool accepted = false;
        var born = new List<(uint Id, double Land)>();
        await SendWorldBatch(player, [], worldAdmission =>
        {
            {
                if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery)
                    || !current() || !player.Layout.WalkableFor(aim.X, aim.Y, 0)) return null;
                using var scope = new PlayerStatScope(player, fresh: true);
                var gem = SupportedSkill(player, admission.Skill);
                if (!PlacedCastAdmissionCurrentLocked(player, admission, gem, recovery)) return null;
                var playerStats = CharacterPlayerStats(player);
                int Get(uint n) => GolemStat(gem.Stats, n) + GolemStat(playerStats, n);
                int count = PlacedSkillCount(player, gem, admission.Mine);
                int limit = PlacedSkillLimit(player, gem, admission.Mine);
                if (count <= 0 || limit <= 0) return null;
                var context = ReservationContext(player, admission.Skill);
                var reserve = admission.Mine ? SupportedReservation(player, admission.Skill) : null;
                var offense = CapturePlacedDamage(player, admission.Skill, gem);
                var damage = offense?.Normal.Hit ?? SupportedHitFor(player, admission.Skill, suppress: false);
                var payload = new Dictionary<uint, int>(gem.Stats);
                foreach (uint flag in new[] { Stat.IsTrap, Stat.IsRemoteMine, Stat.BaseSkillIsTrapped, Stat.BaseSkillIsMined })
                    payload[flag] = 0;
                if (Stat.ActiveSkillAreaOfEffectRadius is var radius)
                    payload[radius] = (int)SkillRadius(player, gem.Stats, true, gem.Effect);
                if (Stat.ActiveSkillBaseAreaOfEffectRadius is var baseRadius)
                    payload[baseRadius] = SkillArea.BaseRadius(SkillsFor(player).EffectId(gem.Effect), Get);
                var proxy = SkillsFor(player).FindByRow(0x4000, gem.Effect, gem.Level, [.. payload.Select(p => (p.Key, p.Value))]);
                if (proxy is null) return null;
                proxy = proxy with { Parent = (player.Id, admission.Skill) };
                string path = PlacedObjectPath(admission.Mine, Get);
                double now = AreaTime(player);
                double flight = PlacedFlightSeconds(admission.Mine, Get);
                var stats = PlacedObjectStats(admission.Mine, flight);
                var bytes = new WorldRecordBuilder(player, worldAdmission);
                bool committed = false;
                for (int i = 0; i < count; i++)
                {
                    uint id = player.TakeObjectId();
                    double land = now + flight;
                    var placement = new PlacedSkills.Placed(id, admission.Skill, gem.Effect, aim, admission.Mine, land + PlacedArmSeconds,
                        land + PlacedDurationSeconds(admission.Mine, Get),
                        damage, new Dictionary<uint, int>(gem.Stats), reserve?.Flat ?? 0, reserve?.Percent ?? 0,
                        context.Life, context.Efficiency) { LandAt = land, Landed = false, Offense = offense };
                    player.Placed.All.Add(id, placement);
                    if (!ReservationBudgetFits(player))
                    {
                        player.Placed.All.Remove(id);
                        break;
                    }
                    while (player.Placed.All.Values.Count(p => p.Mine == admission.Mine) > limit)
                    {
                        var trim = player.Placed.All.Values.Where(p => p.Mine == admission.Mine).MinBy(p => (p.Armed, p.Id))!;
                        if (trim.Id == id)
                        {
                            // This object has not published a create in this packet yet. Removing it is local state
                            // cleanup, not a native RemoveObject for an entity the client never saw.
                            player.Placed.All.Remove(id);
                            player.Placed.Dirty = true;
                            player.Minions.StatRevision++;
                        }
                        else RemovePlacedLocked(player, trim, bytes);
                    }
                    if (!player.Placed.All.ContainsKey(id)) continue;
                    bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path),
                        new TrapMineObject(from.X, from.Y, admission.Mine, [proxy], stats).Blob()));
                    bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
                    if (!player.Connection.LockstepClient)
                        foreach (var record in PlacedThrowRecords(player, id, from, aim, flight)) bytes.Presentation(id, record);
                    born.Add((id, land));
                    committed = true;
                }
                if (!committed) return null;
                RecordAcceptedGemUse(player, gem.Effect);
                player.Placed.Dirty = true;
                player.Minions.StatRevision++;
                ClampReservedResources(player);
                PlacedResourcesLocked(player, bytes);
                accepted = true;
                return bytes.Build();
            }
        });
        if (player.Connection.LockstepClient)
            foreach (var (id, landsAt) in born)
                await SendWorldRecords(player, id, () =>
                {
                    if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery)
                        || !current() || !player.Placed.All.ContainsKey(id)) return null;
                    return [.. PlacedThrowRecords(player, id, from, aim, landsAt - AreaTime(player))];
                }, "throw trap/mine after birth");
        return accepted;
    }

    static void RemovePlacedLocked(Player player, PlacedSkills.Placed placed, WorldRecordBuilder bytes)
    {
        if (!player.Placed.All.Remove(placed.Id)) return;
        if (!player.Placed.Grounds.Any(g => g.Object == placed.Id))
            bytes.Remove(placed.Id);
        player.Placed.Dirty = true;
        player.Minions.StatRevision++;
    }

    static void PlacedResourcesLocked(Player player, WorldRecordBuilder bytes)
    {
        player.Placed.Dirty = false;
        player.Frames.Area?.Life.Changed(player);
    }

    public static void LandPlacedLocked(Player player, PlacedSkills.Placed placed, double now, List<byte> bytes)
    {
        var records = new WorldRecordBuilder(player, null);
        LandPlacedRecordsLocked(player, placed, now, records);
        bytes.AddRange(records.Bytes());
    }

    public static void PlacedGroundsLocked(Player player, double now, List<byte> bytes)
    {
        var records = new WorldRecordBuilder(player, null);
        PlacedGroundRecordsLocked(player, now, records);
        bytes.AddRange(records.Bytes());
    }

    public static void DetonateMinesLocked(Player player, double now)
    {
        player.Placed.Chain = 0; player.Placed.NextChain = now;
        foreach (var mine in player.Placed.All.Values.Where(p => p.Mine && p.Armed <= now).OrderBy(p => p.Armed))
            mine.DetonateAt = now + .2 * player.Placed.Chain++;
    }

    public static bool CanDetonateMines(Player player) =>
        player.Placed.All.Values.Any(p => p.Mine && p.Armed <= AreaTime(player));

    static void LandPlacedRecordsLocked(Player player, PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        if (placed.Landed || now < placed.LandAt) return;
        placed.Landed = true;
        PlacedLandRecords(player, placed.Id, placed.At, bytes);
    }

    static void PlacedDamageLocked(Player player,
        PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        bool bear = SkillsFor(player).EffectId(placed.Effect) is "BearTrap" or "BearTrapAltX";
        double radius = SkillRadius(player, placed.Own, spell: true, placed.Effect);
        bool spellDamage = SkillHitPlan.ForSkill(SkillsFor(player), placed.Effect, 1).Kind != SkillHitKind.Secondary;
        var target = player.Area.MonstersNear(placed.At, 60).FirstOrDefault(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
            && Dist(p.Value.Walk.Position(now), placed.At) <= 60);
        if (bear) target = player.Area.MonstersNear(placed.At, radius).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                && !MonsterAsleep(player, p.Key) && Dist(p.Value.Walk.Position(now), placed.At) <= radius)
            .OrderBy(p => Dist(p.Value.Walk.Position(now), placed.At)).FirstOrDefault();
        var aim = target.Value?.Walk.Position(now) ?? placed.At;
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
        if (SkillsFor(player).EffectId(placed.Effect) == "FireTrap")
        {
            var playerSources = CharacterPlayerStats(player);
            int Get(uint n) => GolemStat(placed.Own, n) + GolemStat(playerSources, n);
            double increased = new[] { Stat.DamagePlusPct, Stat.FireDamagePlusPct, Stat.ElementalDamagePlusPct, Stat.DamageOverTimePlusPct,
                Stat.FireDamageOverTimePlusPct, Stat.BurnDamagePlusPct, Stat.AreaDamagePlusPct }.Sum(Get);
            if (Get(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += Get(Stat.SpellDamagePlusPct);
            double groundMore = new[] { "damage_+%_final", "fire_damage_+%_final", "elemental_damage_+%_final",
                "damage_over_time_+%_final", "trap_damage_+%_final", "area_damage_+%_final" }
                .Aggregate(1d, (factor, n) => factor * Math.Max(0, 1 + Get(Stat.Of(n)) / 100d));
            double dps = Get(Stat.BaseFireDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d)
                * Math.Max(0, 1 + (Get(Stat.DotMultiplierPlus) + Get(Stat.FireDotMultiplierPlus)) / 100d) * groundMore;
            var damage = new SkillDamageContext(SkillHitKind.Spell, playerSources)
                .OverTime(new(DamageType.Fire, dps, 1));
            double seconds = SkillDurationModifiers.Seconds(Get(Stat.BaseSkillEffectDuration),
                n => GolemStat(playerSources, n), n => GolemStat(placed.Own, n));
            player.Placed.Grounds.Add(new(placed.Id, placed.Skill, placed.At, radius, damage.DamagePerSecond, now + seconds, now));
        }
        double more = ReservationAura(player, "SummonSkitterbots") is { } skitter
            ? Math.Max(0, 1 + ReservationStat(player, skitter, Stat.SkitterbotsTrapMineDamagePlusPctFinal) / 100d) : 1;
        var playerStats = placed.Offense?.Caster ?? CharacterPlayerStats(player);
        bool spell = placed.Offense?.Spell ?? spellDamage;
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || bear && id != target.Key || MonsterAsleep(player, id) || Dist(m.Walk.Position(now), placed.At) > radius
                || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) continue;
            var profile = placed.Offense is { } captured
                ? Blinded(player, id) ? captured.Blinded : captured.Normal
                : new PlacedSkills.DamageProfile(placed.Damage, MonsterIgnites.Settings.None,
                    MonsterIgnites.Settings.None, MonsterPoisons.Settings.None);
            if (placed.Offense?.Spider is { } spider)
                profile = SpiderDamageProfile(spider, m.SpiderWebs.Count(now), Blinded(player, id),
                    m.Ignites.Burning, m.Bleeds.Burning, EnemyOnLowLife(m));
            var rolled = profile.Hit.Scale(more).Roll(AreaRng(player));
            var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesMitigated(m, mitigated, spell, now);
            if (placed.Mine)
            {
                // This is an explicit owner-leech grant, not the player's ordinary attack/spell leech.
                var owner = CharacterPlayerStats(player);
                int coefficient = Math.Max(0, GolemStat(owner, Stat.MineDamageLeechedAsLifeToYouPermyriad));
                if (coefficient > 0)
                {
                    var life = DamageTypes.ConversionOrder.Aggregate(DamagePacket.Empty,
                        (sum, type) => sum.Add(DamagePacket.Of(type, coefficient, coefficient)));
                    LeechOnHitLocked(player, m, MonsterTakenComponents(m, mitigated, spell, now), damage,
                        new(life, DamagePacket.Empty, DamagePacket.Empty));
                }
            }
            if (ApplyMonsterHitLocked(player, m, damage, allyAt: placed.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(player, id, m, now); hit = hit with { StageStarts = false }; }
            var poison = profile.Poison with { Min = profile.Poison.Min * more, Max = profile.Poison.Max * more,
                CriticalMin = profile.Poison.CriticalMin * more, CriticalMax = profile.Poison.CriticalMax * more };
            AilmentsOnHitLocked(player, id, m, mitigated, spell, critical: false, 100, playerStats, placed.Own, now,
                AreaRng(player), poison: poison, sourceSkill: placed.Skill, blind: BlindHitInput.Capture(playerStats, placed.Own), playerHit: false);
            if (!m.Dead && profile.Ignite.Max > 0)
            {
                var ignite = profile.Ignite with { Min = profile.Ignite.Min * more, Max = profile.Ignite.Max * more,
                    CriticalMin = profile.Ignite.CriticalMin * more, CriticalMax = profile.Ignite.CriticalMax * more };
                foreach (var packet in IgniteOnHitLocked(player, id, m, ignite, critical: false, now, playerIgnite: false) ?? [])
                    player.Minions.OutputQueue.Add(new(packet, "trap/mine ignite", id, m));
            }
            if (!m.Dead && !spell && rolled[DamageType.Physical].Max > 0)
            {
                var bleed = profile.Bleed with { Min = profile.Bleed.Min * more, Max = profile.Bleed.Max * more,
                    CriticalMin = profile.Bleed.CriticalMin * more, CriticalMax = profile.Bleed.CriticalMax * more };
                foreach (var packet in BleedOnHitLocked(player, id, m, bleed, critical: false, now) ?? [])
                    player.Minions.OutputQueue.Add(new(packet, "trap/mine bleed", id, m));
            }
            CommitAcceptedHitLocked(player, id, m, hit, damage, now, "trap/mine", playerKill: false);
        }
    }

    static void PlacedGroundRecordsLocked(Player player, double now, WorldRecordBuilder bytes)
    {
        if (player.Placed.Grounds.Count == 0) return;
        // Burning grounds do not stack. Keep a separate fractional carry and stop exactly at expiry.
        foreach (var (id, monster) in player.Area.Monsters.ToArray())
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.Invulnerable) continue;
            var ground = player.Placed.Grounds.Where(g => g.LastAt < Math.Min(now, g.Until)
                    && Dist(monster.Walk.Position(now), g.At) <= g.Radius)
                .MaxBy(g => g.DamagePerSecond);
            if (ground is null) continue;
            double total = ground.DamagePerSecond * (Math.Min(now, ground.Until) - ground.LastAt)
                * MonsterDotTaken(monster, DamageType.Fire, now) + ground.Carry.GetValueOrDefault(id);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(total)); ground.Carry[id] = total - damage;
            if (damage <= 0 || ApplyMonsterHitLocked(player, monster, damage, allyAt: ground.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(player, id, monster, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(player, id, monster, hit, damage, now, "Fire Trap burning ground", playerKill: false);
        }
        foreach (var ground in player.Placed.Grounds.ToArray())
        {
            ground.LastAt = Math.Min(now, ground.Until);
            if (now < ground.Until && player.PlayerLife > 0) continue;
            bytes.Remove(ground.Object);
            player.Placed.Grounds.Remove(ground);
        }
    }

    static async Task PlacedSkillsTick(Player player, double now)
    {
        if (player.Placed.All.Count == 0 && player.Placed.Grounds.Count == 0) return;
        await SendWorldBatch(player, [], admission =>
        {
            using (ProjectileMutation(player))
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return null;
                var bytes = new WorldRecordBuilder(player, admission);
                foreach (var placed in player.Placed.All.Values.ToArray())
                {
                    if (player.Connection.LockstepClient && !player.Connection.WorldOutput.PublishedObject(placed.Id).IsReady) continue;
                    if (player.PlayerLife == 0 || now >= placed.Until) { RemovePlacedLocked(player, placed, bytes); continue; }
                    LandPlacedRecordsLocked(player, placed, now, bytes);
                    if (now < placed.Armed) continue;
                    bool skitter = false;
                    if (!placed.SkitterTriggered)
                    {
                        var bot = player.Reservations.Fighters.Values.Where(g => g.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal) && now >= g.NextOrb)
                            .MinBy(g => Dist(g.Monster.Walk.Position(now), placed.At));
                        if (bot is not null)
                        {
                            if (Dist(bot.Monster.Walk.Position(now), placed.At) > 8) GolemMoveLocked(player, bot, placed.At, now);
                            else { skitter = true; bot.NextOrb = now + 3; }
                        }
                    }
                    double trigger = TrapTriggerRadius(n => GolemStat(placed.Own, n));
                    bool nearby = !placed.Mine && player.Area.Monsters.Values.Any(m => m is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(m.Walk.Position(now), placed.At) <= trigger);
                    if (!skitter && !nearby && now < placed.DetonateAt) continue;
                    // Scheduling, an unarmed mine, a retired placement and a Skitterbot's detonation are not a
                    // player detonation. Record only this accepted player-chain trigger, even without a hit target.
                    if (placed.Mine && !skitter)
                        player.RecentEvents.Record(StatConditions.DetonatedMine, AreaTime(player));
                    PlacedDamageLocked(player, placed, now, bytes);
                    Console.WriteLine($"[gateway] placed skill 0x{placed.Id:x} {(skitter ? "Skitterbot" : nearby ? "proximity" : "chain")} trigger; rearm={skitter && placed.Mine && SkillsFor(player).EffectId(placed.Effect) != "SmokeMine"}");
                    if (skitter && placed.Mine && SkillsFor(player).EffectId(placed.Effect) != "SmokeMine")
                    {
                        placed.SkitterTriggered = true; placed.DetonateAt = double.PositiveInfinity;
                        player.Placed.All[placed.Id] = placed with { Armed = now + .5 };
                    }
                    else RemovePlacedLocked(player, placed, bytes);
                }
                if (player.Placed.Dirty) PlacedResourcesLocked(player, bytes);
                bool detonation = CanDetonateMines(player);
                if (detonation != player.Placed.DetonationPublished)
                {
                    bytes.World(player.Id, InstanceEntities.PlayerSkillsChange(player.Id, [], [], canDetonateMines: detonation));
                    player.Placed.DetonationPublished = detonation;
                }
                PlacedGroundRecordsLocked(player, now, bytes);
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        });
    }
}
