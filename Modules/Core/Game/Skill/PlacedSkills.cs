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

    // Throwing takes its own time, not the supported spell's cast time: 0.6 s for a trap and 0.3 s for a mine (PoB
    // CalcOffence TrapThrowingTime and MineLayingTime; PoE Wiki "Trap", "Mine"), over the summed throwing speed and
    // each support's "more" throwing speed. The caller applies action speed.
    static readonly uint[] TrapThrowFinal =
        [Stat.SupportTrapAndMineDamageTrapThrowingSpeedPlusPctFinal, Stat.ActiveSkillTrapThrowingSpeedPlusPctFinal];
    static readonly uint[] MineThrowFinal =
        [Stat.SupportTrapAndMineDamageMineThrowingSpeedPlusPctFinal, Stat.SupportMinefieldMineThrowingSpeedPlusPctFinal];
    public static double PlacedThrowSeconds(Func<uint, int> get, bool mine, int frenzyCharges = 0)
    {
        // Charged Traps and Charged Mines: increased throwing speed per Frenzy Charge.
        double speed = Math.Max(0, 1 + (get(mine ? Stat.MineLayingSpeedPlusPct : Stat.TrapThrowingSpeedPlusPct)
            + get(Stat.TrapAndMineThrowingSpeedPlusPct) + frenzyCharges
                * get(mine ? Stat.MineThrowingSpeedPlusPctPerFrenzyCharge : Stat.TrapThrowingSpeedPlusPctPerFrenzyCharge)) / 100d);
        foreach (uint stat in mine ? MineThrowFinal : TrapThrowFinal) speed *= Math.Max(0, 1 + get(stat) / 100d);
        return (mine ? .3 : .6) / Math.Max(.01, speed);
    }

    // Swift Assembly: three separate chances, of 1, 2 and 3 additional traps or mines. Each is rolled on its own and
    // the successes add up, so one use throws up to 6 more (3.8.0 patch notes).
    static readonly uint[] SwiftAssemblyStats =
    [
        Stat.SupportAdditionalTrapMinePctChanceFor1AdditionalTrapMine,
        Stat.SupportAdditionalTrapMinePctChanceFor2AdditionalTrapMine,
        Stat.SupportAdditionalTrapMinePctChanceFor3AdditionalTrapMine,
    ];
    public static int SwiftAssemblyExtra(Func<uint, int> get, Random random)
    {
        int total = 0;
        for (int i = 0; i < SwiftAssemblyStats.Length; i++)
            if (random.Next(100) < get(SwiftAssemblyStats[i])) total += i + 1;
        return total;
    }

    // Where each trap or mine of one use lands. One lands on the aim. Several scatter at random around it: within
    // throw_traps_in_circle_radius when the support gives one (Cluster Traps, Swift Assembly), otherwise (Multiple
    // Traps, Minefield) within a small radius that grows with their number, 4 cells and 2 more for each. They cluster
    // near the aim with a few outliers: the distance is the radius times a uniform roll to the power 1.5, so about
    // five in eight land within half of it. The growing radius and the clustering are a player's description of the live game, not a
    // client measurement.
    public static double PlacedScatterRadius(int count, int circle) => count <= 1 ? 0 : circle > 0 ? circle : 4 + 2 * count;

    public static (int X, int Y) PlacedThrowPoint((int X, int Y) aim, int count, int circle, Random random)
    {
        double radius = PlacedScatterRadius(count, circle);
        if (radius <= 0) return aim;
        double angle = random.NextDouble() * Math.Tau, distance = radius * Math.Pow(random.NextDouble(), 1.5);
        return (aim.X + (int)Math.Round(Math.Cos(angle) * distance), aim.Y + (int)Math.Round(Math.Sin(angle) * distance));
    }

    // Preserve the existing placement-time offensive snapshot. A recipient's Blind is selected only at contact.
    static PlacedSkills.DamageSnapshot? CapturePlacedDamage(Player player, ushort skill, SupportedGem gem)
    {
        if (SupportedHitInputsFor(player, skill, capturedGem: gem) is not { } inputs) return null;
        inputs = inputs with { Caster = TotemCasterStats(inputs.Caster) };
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
        double seconds;
        int swiftExtra;
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery)) return false;
            using var scope = new PlayerStatScope(player, fresh: true);
            var gem = SupportedSkill(player, admission.Skill);
            if (!PlacedCastAdmissionCurrentLocked(player, admission, gem, recovery)) return false;
            var playerStats = CharacterPlayerStats(player);
            int Get(uint n) => GolemStat(gem.Stats, n) + GolemStat(playerStats, n);
            // Decided once per use: Swift Assembly's chance of more traps or mines.
            swiftExtra = SwiftAssemblyExtra(Get, AreaRng(player));
            seconds = Math.Max(TickSeconds, PlacedThrowSeconds(Get, admission.Mine,
                ChargesOf(player).Effective(ChargeType.Frenzy)) / PlayerActionFactor(player));
        }
        // The oldest traps or mines over the limit go when the throw starts, in a packet of their own. Removed in the
        // packet that created the new one, the client showed the old object moving to the new place (live 2026-11-17).
        await SendWorldBatch(player, [], worldAdmission =>
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, recovery)) return null;
            using var scope = new PlayerStatScope(player, fresh: true);
            var gem = SupportedSkill(player, admission.Skill);
            if (!PlacedCastAdmissionCurrentLocked(player, admission, gem, recovery)) return null;
            var bytes = new WorldRecordBuilder(player, worldAdmission);
            int count = PlacedSkillCount(player, gem, admission.Mine) + swiftExtra;
            int limit = PlacedSkillLimit(player, gem, admission.Mine);
            while (limit > 0 && player.Placed.All.Values.Count(p => p.Mine == admission.Mine) + count > limit
                && player.Placed.All.Values.Where(p => p.Mine == admission.Mine).MinBy(p => (p.Armed, p.Id)) is { } oldest)
                RemovePlacedLocked(player, oldest, bytes);
            if (bytes.Count == 0) return null;
            ClampReservedResources(player);
            PlacedResourcesLocked(player, bytes);
            return bytes.Build();
        });
        double started = AreaTime(player);
        double release = started + seconds;
        Console.WriteLine($"[gateway] {(admission.Mine ? "mine" : "trap")} throw of skill 0x{admission.Skill:x4}: {seconds:0.000} s");
        // While the button is held the client starts its next throw when its own throw has finished, which can be before
        // this wait ends. A throw replaced after at least half its time has left the hand, so it still lands; before that
        // it was cancelled.
        bool replaced = false;
        while (true)
        {
            if (!PlacedCastStructuralOwnerCurrentLocked(player, admission)) return false;
            if (!current() || recovery != player.ActionInterruptionGeneration)
            {
                if (AreaTime(player) - started < seconds / 2) return false;
                replaced = true;
                break;
            }
            if (!player.Paused && AreaTime(player) >= release) break;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        // A replaced throw is no longer the current action, so it is held to its source and area, not to the action.
        int? action = replaced ? null : recovery;
        bool accepted = false;
        var born = new List<(uint Id, double Land, (int X, int Y) At)>();
        await SendWorldBatch(player, [], worldAdmission =>
        {
            {
                if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, action)
                    || (!replaced && !current())) return null;
                (int X, int Y) clamped = (Math.Clamp(aim.X, 0, Math.Max(0, player.Layout.GridW - 1)),
                    Math.Clamp(aim.Y, 0, Math.Max(0, player.Layout.GridH - 1)));
                if (!player.Layout.WalkableFor(clamped.X, clamped.Y, 0))
                {
                    clamped = CollisionEscape.Find(clamped, player.Layout.GridW, player.Layout.GridH,
                        (x, y) => player.Layout.WalkableFor(x, y, 0), [], moverSize: 0) ?? from;
                    if (!player.Layout.WalkableFor(clamped.X, clamped.Y, 0)) return null;
                }
                aim = clamped;
                using var scope = new PlayerStatScope(player, fresh: true);
                var gem = SupportedSkill(player, admission.Skill);
                if (!PlacedCastAdmissionCurrentLocked(player, admission, gem, action)) return null;
                var playerStats = CharacterPlayerStats(player);
                int Get(uint n) => GolemStat(gem.Stats, n) + GolemStat(playerStats, n);
                int count = PlacedSkillCount(player, gem, admission.Mine) + swiftExtra;
                int circle = Get(Stat.ThrowTrapsInCircleRadius);
                int limit = PlacedSkillLimit(player, gem, admission.Mine);
                if (count <= 0 || limit <= 0) return null;
                var context = ReservationContext(player, admission.Skill);
                var reserve = admission.Mine ? SupportedReservation(player, admission.Skill) : null;
                var offense = CapturePlacedDamage(player, admission.Skill, gem);
                var damage = offense?.Normal.Hit ?? SupportedHitFor(player, admission.Skill, suppress: false);
                var payload = new Dictionary<uint, int>(gem.Stats);
                foreach (uint flag in new[] { Stat.IsTrap, Stat.IsRemoteMine, Stat.BaseSkillIsTrapped,
                    Stat.BaseSkillIsMined, Stat.SkillIsTrapped, Stat.SkillIsMined })
                    payload[flag] = 0;
                payload[Stat.UsesNoMana] = 1;
                payload[Stat.ManaCost] = 0;
                payload[Stat.SkillIsNonPathing] = 1;
                int castMs = SkillsFor(player).CastTimeMs(gem.Effect);
                if (castMs > 0)
                {
                    payload[Stat.BaseSpellCastTimeMs] = castMs;
                    payload[Stat.VirtualBaseSpellCastTimeMs] = castMs;
                }
                if (!SkillsFor(player).IsAttack(gem.Effect))
                    payload[Stat.CastSpeedPlusPct] = FinalCastSpeedPct(player, MergeSkillStats(playerStats, gem.Stats),
                        CastSpeedPct(playerStats) + GolemStat(gem.Stats, Stat.BaseCastSpeedPlusPct));
                if (GolemStat(gem.Stats, Stat.BaseSkillEffectDuration) is int baseDuration and > 0)
                    payload[Stat.SkillEffectDuration] = (int)Math.Round(1000 * SkillDurationModifiers.Seconds(baseDuration,
                        n => GolemStat(playerStats, n), n => GolemStat(gem.Stats, n), DurationKinds(player, gem.Effect)));
                bool spellArea = !SkillsFor(player).IsAttack(gem.Effect);
                var areaStats = SkillAreaStats(player, gem.Stats, gem.Effect, playerStats);
                int AreaGet(uint key) => areaStats.GetValueOrDefault(key);
                int baseRadius = SkillArea.BaseRadius(SkillsFor(player).EffectId(gem.Effect), Get);
                payload[Stat.ActiveSkillBaseAreaOfEffectRadius] = baseRadius;
                payload[Stat.ActiveSkillAreaOfEffectRadius] = SkillArea.Radius(baseRadius, AreaGet, spellArea);
                payload[Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal] = SkillArea.RadiusPercent(AreaGet, spellArea);
                payload[Stat.SkillAreaOfEffectPlusPct] = SkillArea.Increased(AreaGet, spellArea);
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
                    // Several from one use scatter around the aim; one that would land off the walkable ground lands on it.
                    var point = PlacedThrowPoint(aim, count, circle, AreaRng(player));
                    if (!player.Layout.WalkableFor(point.X, point.Y, 0)) point = aim;
                    uint id = player.TakeObjectId();
                    double land = now + flight;
                    var placement = new PlacedSkills.Placed(id, admission.Skill, gem.Effect, point, admission.Mine, land + PlacedArmSeconds,
                        land + PlacedDurationSeconds(admission.Mine, Get),
                        damage, new Dictionary<uint, int>(gem.Stats), reserve?.Flat ?? 0, reserve?.Percent ?? 0,
                        context.Life, context.Efficiency)
                    {
                        LandAt = land, Landed = false, Offense = offense, TriggerRadius = TrapTriggerRadius(Get),
                        EffectDurationSeconds = GolemStat(payload, Stat.SkillEffectDuration) / 1000d,
                    };
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
                        foreach (var record in PlacedThrowRecords(player, id, from, point, flight)) bytes.Presentation(id, record);
                    born.Add((id, land, point));
                    committed = true;
                }
                if (!committed) return null;
                RecordAcceptedGemUse(player, gem.Effect, clamped);
                player.Placed.Dirty = true;
                player.Minions.StatRevision++;
                ClampReservedResources(player);
                PlacedResourcesLocked(player, bytes);
                accepted = true;
                return bytes.Build();
            }
        });
        if (player.Connection.LockstepClient)
            foreach (var (id, landsAt, point) in born)
                await SendWorldRecords(player, id, () =>
                {
                    if (!PlacedCastStructuralOwnerCurrentLocked(player, admission, action)
                        || (!replaced && !current()) || !player.Placed.All.ContainsKey(id)) return null;
                    return [.. PlacedThrowRecords(player, id, from, point, Math.Max(0, landsAt - AreaTime(player)))];
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

    // A trap or mine that has gone off stays for its activation to play, then is removed (0.6 s, a local value):
    // removed in the packet that started the action, it vanished without showing it.
    static void RetirePlacedLocked(Player player, PlacedSkills.Placed placed, double now)
    {
        if (!player.Placed.All.Remove(placed.Id)) return;
        if (!player.Placed.Grounds.Any(g => g.Object == placed.Id)) player.Placed.Leaving.Add((placed.Id, now + .6));
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

    // The wait before a mine of a detonation sequence goes off after the one before it: the mine's detonation time
    // (base_mine_detonation_time_ms, 250 in every 3.29.3c mine row) at 10% more detonation speed for each mine before
    // it in the sequence (Detonate Mines' display_mine_deontation_mechanics_detonation_speed_+%_final_per_sequence_mine).
    public static double MineSequenceGap(int milliseconds, int prior) =>
        (milliseconds > 0 ? milliseconds : 250) / 1000d / (1 + .1 * Math.Max(0, prior));

    // One use detonates every mine in sequence, in the order they arm: the first at once, each later one after its gap.
    // A mine that is not armed yet goes off once it is. A use during a running sequence adds the new mines to its end
    // and keeps its speed; a use after the last queued mine has gone off starts a new sequence.
    public static void DetonateMinesLocked(Player player, double now)
    {
        bool running = player.Placed.All.Values.Any(p => p.Mine && !double.IsPositiveInfinity(p.DetonateAt));
        double next = running ? player.Placed.NextChain : now;
        int prior = running ? player.Placed.Chain : 0;
        var playerAt = player.Walk.Position(now);
        foreach (var mine in player.Placed.All.Values.Where(p => p.Mine && double.IsPositiveInfinity(p.DetonateAt)
            && MineCanDetonateFrom(p, playerAt))
            .OrderBy(p => p.Armed).ThenBy(p => p.Id))
        {
            int time = GolemStat(mine.Own, Stat.BaseMineDetonationTimeMs);
            if (time <= 0) time = GolemStat(mine.Own, Stat.SupportRemoteMine2BaseMineDetonationTimeMs);
            if (prior > 0) next += MineSequenceGap(time, prior);
            next = Math.Max(Math.Max(next, now), mine.Armed);
            mine.DetonateAt = next;
            prior++;
        }
        player.Placed.NextChain = next;
        player.Placed.Chain = prior;
    }

    static bool MineCanDetonateFrom(PlacedSkills.Placed mine, (int X, int Y) playerAt)
    {
        int minimum = GolemStat(mine.Own, Stat.SupportLocusMineCannotDetonateMinesWithinXUnits);
        return minimum <= 0 || Dist(mine.At, playerAt) >= minimum;
    }

    public static bool CanDetonateMines(Player player)
    {
        double now = AreaTime(player);
        var playerAt = player.Walk.Position(now);
        return player.Placed.All.Values.Any(p => p.Mine && p.Armed <= now && now < p.Until && MineCanDetonateFrom(p, playerAt));
    }

    static void LandPlacedRecordsLocked(Player player, PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        if (placed.Landed || now < placed.LandAt) return;
        placed.Landed = true;
        PlacedLandRecords(player, placed.Id, placed.At, bytes);
    }

    // Where a trap or mine at a cell fires its projectile skill: at the nearest enemy within 60 cells that does not
    // stand on it, and with none in range 20 cells straight away from the player, so that it still uses its skill.
    public static (int X, int Y) PlacedProjectileAim((int X, int Y) at, (int X, int Y) player, IEnumerable<(int X, int Y)> enemies)
    {
        var near = enemies.Where(e => Dist(e, at) is >= 1 and <= 60).OrderBy(e => Dist(e, at)).ToArray();
        if (near.Length > 0) return near[0];
        double away = Dist(at, player);
        return away < 1 ? (at.X + 20, at.Y)
            : (at.X + (int)Math.Round((at.X - player.X) / away * 20), at.Y + (int)Math.Round((at.Y - player.Y) / away * 20));
    }

    static void PlacedDamageLocked(Player player,
        PlacedSkills.Placed placed, double now, WorldRecordBuilder bytes)
    {
        int inventionRate = GolemStat(placed.Own, Stat.SupportInventionTrapAndMineDamagePlusPctFinalPerSecondPlacedUpTo100);
        double inventionMore = 1 + Math.Clamp(inventionRate * Math.Max(0, now - placed.LandAt), 0, 100) / 100;
        bool bear = SkillsFor(player).EffectId(placed.Effect) is "BearTrap" or "BearTrapAltX";
        double radius = SkillRadius(player, placed.Own, spell: true, placed.Effect);
        bool spellDamage = SkillHitPlan.ForSkill(SkillsFor(player), placed.Effect, 1).Kind != SkillHitKind.Secondary;
        var payload = GemSkillEffects(player).FirstOrDefault(g => g.Id == placed.Skill);
        bool currentSkill = payload.Name is not null && payload.Effect == placed.Effect;
        bool projectile = !bear && currentSkill
            && UsesProjectileRuntime(placed.Effect) && ResolveProjectileDefinition(placed.Effect) is not null;
        KeyValuePair<uint, SpawnedMonster> target = default;
        (int X, int Y) aim = placed.At;
        if (projectile)
        {
            aim = PlacedProjectileAim(placed.At, player.Walk.Position(now), player.Area.MonstersNear(placed.At, 60)
                .Where(m => m.Value is { Hostile: true, Dead: false, Life: > 0 }).Select(m => m.Value.Walk.Position(now)));
        }
        else if (bear)
        {
            target = player.Area.MonstersNear(placed.At, radius).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && !MonsterAsleep(player, p.Key) && Dist(p.Value.Walk.Position(now), placed.At) <= radius)
                .OrderBy(p => Dist(p.Value.Walk.Position(now), placed.At)).FirstOrDefault();
            aim = target.Value?.Walk.Position(now) ?? placed.At;
        }
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
        // A projectile spell leaves the trap or mine towards the nearest enemy as its own projectile, with the hit and
        // support modifiers of the supported skill, instead of an area hit around the object. The server draws it, as it
        // does a triggered spell's: no local input action predicts it.
        if (projectile)
        {
            var session = ProjectilesFor(player);
            var lifetime = CombatLifetime;
            var critical = CaptureSkillCriticalRoll(player, placed.Skill);
            var at = placed.At;
            var shot = aim;
            session.Schedule(session.Runtime.Now, () => session.Active && ReferenceEquals(lifetime, CombatLifetime), () =>
            {
                var use = CaptureGemProjectile(player, session, placed.Skill, payload, shot, critical, from: at);
                use = ClientDrawnVolley(player, session, placed.Skill, payload, use, 0, 0);
                var offense = ((ProjectileOffense)use.Payload!).ScaleDamage(inventionMore);
                return use with { Payload = offense with { ClientPredicted = false, FromTrapOrMine = true } };
            });
            return;
        }
        using var statScope = new PlayerStatScope(player, fresh: true);
        double more = ReservationAura(player, "SummonSkitterbots") is { } skitter
            ? Math.Max(0, 1 + ReservationStat(player, skitter, Stat.SkitterbotsTrapMineDamagePlusPctFinal) / 100d) : 1;
        more *= inventionMore;
        var owner = CharacterPlayerStats(player);
        var playerStats = placed.Offense?.Caster ?? owner;
        bool spell = placed.Offense?.Spell ?? spellDamage;
        bool attack = SkillsFor(player).IsAttack(placed.Effect);
        int chance = currentSkill ? SupportedReleaseCriticalChance(player, placed.Skill) : 0;
        int? vsBlinded = currentSkill ? SupportedCriticalChanceVsBlinded(player, placed.Skill, release: true) : null;
        GroundCritical? vsGround = currentSkill ? SupportedCriticalChanceVsGround(player, placed.Skill, release: true) : null;
        int multiplier = currentSkill ? SupportedCriticalMultiplier(player, placed.Skill) : 100;
        var criticalRoll = currentSkill ? new SkillCriticalRoll(CriticalExtraRollsFor(player, owner, placed.Own)) : null;
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
            var rolled = WithConditionalSupportDamageLocked(player, placed.Own, !spell, m,
                profile.Hit.Scale(more).Roll(AreaRng(player)), now);
            var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesTrapOrMineHit(m, mitigated, spell, now, AreaRng(player));
            bool critical = currentSkill && RollCriticalStrike(player, CriticalChanceOn(player, id, chance, vsBlinded, vsGround),
                placed.Own, attack, criticalRoll, owner);
            int hitMultiplier = critical ? CriticalMultiplierOn(m, multiplier, owner, placed.Own) : 100;
            if (critical) damage = DamageModel.CriticalDamage(damage, hitMultiplier);
            if (placed.Mine)
            {
                // This is an explicit owner-leech grant, not the player's ordinary attack/spell leech.
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
            OwnedKillLocked(m, hit.Life);
            var poison = profile.Poison with { Min = profile.Poison.Min * more, Max = profile.Poison.Max * more,
                CriticalMin = profile.Poison.CriticalMin * more, CriticalMax = profile.Poison.CriticalMax * more };
            AilmentsOnHitLocked(player, id, m, mitigated, spell, critical, hitMultiplier, playerStats, placed.Own, now,
                AreaRng(player), poison: poison, sourceSkill: placed.Skill, blind: BlindHitInput.Capture(playerStats, placed.Own), playerHit: false);
            if (!m.Dead && profile.Ignite.Max > 0)
            {
                var ignite = profile.Ignite with { Min = profile.Ignite.Min * more, Max = profile.Ignite.Max * more,
                    CriticalMin = profile.Ignite.CriticalMin * more, CriticalMax = profile.Ignite.CriticalMax * more };
                foreach (var packet in IgniteOnHitLocked(player, id, m, ignite, critical, now, playerIgnite: false) ?? [])
                    player.Minions.OutputQueue.Add(new(packet, "trap/mine ignite", id, m));
            }
            if (!m.Dead && !spell && rolled[DamageType.Physical].Max > 0)
            {
                var bleed = profile.Bleed with { Min = profile.Bleed.Min * more, Max = profile.Bleed.Max * more,
                    CriticalMin = profile.Bleed.CriticalMin * more, CriticalMax = profile.Bleed.CriticalMax * more };
                foreach (var packet in BleedOnHitLocked(player, id, m, bleed, critical, now) ?? [])
                    player.Minions.OutputQueue.Add(new(packet, "trap/mine bleed", id, m));
            }
            CommitAcceptedHitLocked(player, id, m, hit, damage, now, "trap/mine", playerKill: false);
            if (bear)
                BearTrapOnHitLocked(m, damage, now, n => GolemStat(playerStats, n), n => GolemStat(placed.Own, n));
        }
        if (GolemStat(placed.Own, Stat.BaseFireDamageToDealPerMinute) > 0)
        {
            int Get(uint n) => GolemStat(placed.Own, n) + GolemStat(playerStats, n);
            double increased = new[] { Stat.DamagePlusPct, Stat.FireDamagePlusPct, Stat.ElementalDamagePlusPct, Stat.DamageOverTimePlusPct,
                Stat.FireDamageOverTimePlusPct, Stat.BurnDamagePlusPct, Stat.AreaDamagePlusPct }.Sum(Get);
            increased += (double)Get(placed.Mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct) + Get(Stat.TrapOrMineDamagePlusPct);
            if (Get(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += Get(Stat.SpellDamagePlusPct);
            double groundMore = new[] { "damage_+%_final", "fire_damage_+%_final", "elemental_damage_+%_final",
                "damage_over_time_+%_final", "trap_damage_+%_final", "area_damage_+%_final" }
                .Aggregate(1d, (factor, n) => factor * Math.Max(0, 1 + Get(Stat.Of(n)) / 100d));
            double dps = Get(Stat.BaseFireDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d)
                * Math.Max(0, 1 + (Get(Stat.DotMultiplierPlus) + Get(Stat.FireDotMultiplierPlus)) / 100d) * groundMore * inventionMore;
            var damage = new SkillDamageContext(SkillHitKind.Spell, placed.Offense?.Caster ?? TotemCasterStats(playerStats))
                .OverTime(new(DamageType.Fire, dps, 1));
            int baseDuration = Get(Stat.BaseSkillEffectDuration);
            double seconds = SkillDurationModifiers.Seconds(baseDuration > 0 ? baseDuration : 1750,
                n => GolemStat(playerStats, n), n => GolemStat(placed.Own, n), DurationKinds(player, placed.Effect));
            player.Placed.Grounds.Add(new(placed.Id, placed.Skill, placed.At, radius, damage.DamagePerSecond, now + seconds, now));
        }
    }

    // Charged Traps: a trap an enemy set off may grant a Frenzy and a Power Charge. Charged Mines: so may a mine
    // detonated with an enemy in its target range (the 60 cells its skill aims within).
    static void PlacedChargesLocked(Player player, PlacedSkills.Placed placed, bool enemyTriggered, double now)
    {
        if (placed.Mine ? !player.Area.MonstersNear(placed.At, 60).Any(m => m.Value is { Hostile: true, Dead: false, Life: > 0 }
                && Dist(m.Value.Walk.Position(now), placed.At) <= 60) : !enemyTriggered) return;
        foreach (var (type, stat) in new[] {
            (ChargeType.Frenzy, placed.Mine ? Stat.Of("%_chance_to_gain_frenzy_charge_on_mine_detonated_targeting_an_enemy")
                : Stat.Of("%_chance_to_gain_frenzy_charge_on_trap_triggered_by_an_enemy")),
            (ChargeType.Power, placed.Mine ? Stat.Of("%_chance_to_gain_power_charge_on_mine_detonated_targeting_an_enemy")
                : Stat.Of("%_chance_to_gain_power_charge_on_trap_triggered_by_an_enemy")) })
            if (GolemStat(placed.Own, stat) is > 0 and var chance && AreaRng(player).Next(100) < chance)
                ChargesOf(player).Enqueue(new ChargeEvent.Grant(type, 1, placed.Mine ? "Charged Mines" : "Charged Traps"));
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
            OwnedKillLocked(monster, hit.Life);
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
                var playerAt = player.Walk.Position(now);
                foreach (var placed in player.Placed.All.Values.ToArray())
                {
                    if (player.Connection.LockstepClient && !player.Connection.WorldOutput.PublishedObject(placed.Id).IsReady) continue;
                    if (player.PlayerLife == 0 || now >= placed.Until) { RemovePlacedLocked(player, placed, bytes); continue; }
                    LandPlacedRecordsLocked(player, placed, now, bytes);
                    if (now < placed.Armed) continue;
                    if (placed.Mine && !MineCanDetonateFrom(placed, playerAt))
                    {
                        if (now >= placed.DetonateAt) placed.DetonateAt = double.PositiveInfinity;
                        continue;
                    }

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
                    string? effect = SkillsFor(player).EffectId(placed.Effect);
                    bool conversion = !placed.Mine && effect == "ConversionTrap";
                    var trigger = conversion ? ConversionTrapTarget(player, placed, now) : default;
                    bool nearby = !placed.Mine && (conversion ? trigger.Value is not null
                        : player.Area.MonstersNear(placed.At, placed.TriggerRadius).Any(m => m.Value is { Hostile: true, Dead: false, Life: > 0 }
                            && Dist(m.Value.Walk.Position(now), placed.At) <= placed.TriggerRadius));
                    if (!skitter && !nearby && now < placed.DetonateAt) continue;
                    // Scheduling, an unarmed mine, a retired placement and a Skitterbot's detonation are not a
                    // player detonation. Record only this accepted player-chain trigger, even without a hit target.
                    if (placed.Mine && !skitter)
                        player.RecentEvents.Record(StatConditions.DetonatedMine, AreaTime(player));
                    if (conversion) TriggerConversionTrap(player, placed, trigger, now, bytes);
                    else PlacedDamageLocked(player, placed, now, bytes);
                    PlacedChargesLocked(player, placed, nearby, now);
                    bool rearm = skitter && placed.Mine && effect != "SmokeMine";
                    Console.WriteLine($"[gateway] placed skill 0x{placed.Id:x} {(skitter ? "Skitterbot" : nearby ? "proximity" : "chain")} trigger; rearm={rearm}");
                    if (rearm)
                    {
                        placed.SkitterTriggered = true; placed.DetonateAt = double.PositiveInfinity;
                        player.Placed.All[placed.Id] = placed with { Armed = now + .5 };
                    }
                    else RetirePlacedLocked(player, placed, now);
                }
                foreach (var leaving in player.Placed.Leaving.Where(l => now >= l.At || player.PlayerLife == 0).ToArray())
                {
                    bytes.Remove(leaving.Object);
                    player.Placed.Leaving.Remove(leaving);
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
