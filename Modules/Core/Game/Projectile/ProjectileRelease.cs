using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>ItemClasses row 13 is Bow. A gem skill fires arrows only while the active main hand is a bow; its
    /// additional-arrow stats use the native arrow alias rather than the generic projectile count alone.</summary>
    public const int BowClassRow = 13;

    public static bool ArrowsFor(ClientState conn, int effect) =>
        SkillsFor(conn).IsAttack(effect) && WeaponProfileFor(conn, activeSet: true).Bow;

    public static bool ArrowsForSkill(int effect) =>
        Skills.IsAttack(effect) && Skills.WeaponRestrictions(effect).Contains(BowClassRow);

    /// <summary>Tornado Shot's primary arrow travels only to the targeted location, then releases its child group;
    /// it must not keep flying to the generic projectile range. Arrow Nova's payload arrow lands at the aim before its
    /// volley, so it stops there too. Other projectiles keep their full range.</summary>
    public static double PrimaryRange(string? effect, double range, double targetDistance, bool toTarget = false) =>
        toTarget || effect is "TornadoShot" or "TornadoShotAltX" or "TornadoShotAltY"
            ? Math.Min(range, Math.Max(.001, targetDistance)) : range;

    /// <summary>The shared client fan for a multi-arrow use. Attack gems and ordinary bow shots otherwise used the
    /// fixed 30-degree server spread, which could send an arrow past the point where the client's own arrow burst.</summary>
    public static ProjectileProfile NativeVolley(ProjectileProfile profile, Func<string, int> get)
    {
        if (get("support_parallel_projectile_number_of_points_per_side") != 0) return profile;
        return profile with
        {
            NativeVolley = new()
            {
                Width = get("total_projectile_spread_angle_override") is > 0 and var width
                    ? width / 10.869565 : 60 / 10.869565,
                FixedDegrees = get("projectile_remove_default_spread") != 0 ? -1 : get("fixed_projectile_spread"),
                SpreadPercent = get("multiple_projectiles_projectile_spread_+%"),
                Nova = profile.NativeVolley?.Nova == true || get("projectiles_nova") != 0
            }
        };
    }

    public static bool UsesProjectileRuntime(int effect) => Skills.IsProjectile(effect)
        || ProjectileDefinition(Skills.EffectId(effect) ?? "") is not null;

    // Presentation only: Burning Arrow's real local input emits its own primary. Keep its existing simulation
    // geometry separate from ClientPredicted, which also selects native range/contact/volley specialization.
    public static bool ClientDrawsPrimaryProjectile(bool predicted, string? effect) =>
        predicted || effect == "BurningArrow";

    // Validate adopted paths before accepting an action, spending mana or reserving a cooldown.
    // Release still resolves again: equipment/stats may change during the ordinary cancellable windup.
    public static bool ProjectilePathAvailable(string remote, ClientState conn, ushort skillId, int effect)
    {
        if (!TotemPathAvailable(conn, skillId, effect)) return false;
        if (!UsesProjectileRuntime(effect)) return true;
        try
        {
            var definition = ResolveProjectileDefinition(effect)
                ?? throw new InvalidOperationException("no available projectile recipe/metadata");
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
            if (gem.Name is null || !SkillHitPlan.ForSkill(SkillsFor(conn), effect, gem.Level, gem.Quality).CanHit)
                throw new InvalidOperationException("no supported projectile damage plan");
            var resolved = MergeSkillStats(CharacterPlayerStats(conn), SupportedSkill(conn, skillId).Stats);
            int Get(string name) => Stats.Key(name) is uint key ? resolved.GetValueOrDefault(key) : 0;
            _ = definition.Parameters.Resolve(Get, 1, ArrowsFor(conn, effect));
            var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            if (definition.RequiresTerrain && ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, at.X, at.Y) is null)
                throw new InvalidOperationException("Projectile recipe requires a projectile terrain mask");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[gateway] {remote} {SkillsFor(conn).EffectId(effect)} unavailable: {ex.Message}");
            return false;
        }
    }

    public static Task ScheduleGemProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) aim, int action, int recoveryGeneration, CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll,
        ushort sequence = 0, double? swingAt = null, bool cryExerted = false, int seals = 0, double reoccurrenceMore = 1)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        bool attack = SkillsFor(conn).IsAttack(gem.Effect);
        bool cancelOnNewAction = !attack || ResolveProjectileDefinition(gem.Effect)?.CancelWindupOnNewAction == true;
        int? cast = attack ? 0 : CastMs(conn, gem.Effect, skillId);
        if (cast is null) return Task.CompletedTask;
        // From the swing's start when it began before now (a lockstep walk-up's goal frame, CastGemSkill's swingAt).
        double acceptedAt = swingAt ?? conn.Clock.Elapsed.TotalSeconds;
        // A Spectral Throw's own weapon leaves on the client's swing (SpectralThrowRelease), without the server-object lead.
        double? thrown = attack && SkillsFor(conn).EffectId(gem.Effect) == "SpectralThrow"
            ? SpectralThrowRelease.Delay(conn, Environment.GetEnvironmentVariable("Game_PLAYER_AO")
                ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject, WeaponProfileFor(conn, activeSet: true),
                SupportedAttackSeconds(conn, skillId))
            : null;
        double delay = thrown is { } swing ? swing
            : attack ? AttackReleaseDelay(AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), false))
            : cast.Value / 1000d * Animations.SpellRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject);
        int epoch = conn.ProjectileEpoch;
        var sacrifice = new SacrificeUse(conn, skillId, gem.Effect, gem.Gem);
        // Paid attacks survive Move unless their recipe opts into cancellable windup (e.g. release-time costs).
        // Spells and cooldown tickets retain action cancellation.
        // Epoch, death and hit recovery still cancel unreleased attacks.
        bool Valid() => sacrifice.Current && conn.ProjectileActive(epoch) && conn.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(conn) ?? (cancelOnNewAction
            ? PlayerActionCurrent(conn, action)
            : conn.PlayerLife > 0 && conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds) > 0 && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds)))
            && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is null;
        // A spell's repeats (SpellRepeats) release again a cast time apart, with the use's critical roll; a new action
        // cancels the rest, as it ends the client's.
        // shortcut: a repeat keeps the cast's aim point; check live whether the client re-aims at a moving target.
        if (SkillsFor(conn).EffectId(gem.Effect) == "VaalSpark")
        {
            if (conn.VaalUse is not { Executed: true } paid || paid.Skill != skillId || paid.Effect != gem.Effect)
                return Task.CompletedTask;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + delay), () => Valid() && VaalAdmissionCurrent(conn, paid), () =>
            {
                var use = CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll, sequence, sacrifice: sacrifice);
                var own = SupportedSkill(conn, skillId).Stats;
                // The default profile substitutes one shot for a missing base count. Vaal Spark
                // already supplies that base as its hundred-shot stream; quality adds to the stream.
                int extra = use.Profile.Count - (GolemStat(own, "base_number_of_projectiles") > 0 ? 0 : 1);
                int count = Math.Clamp(GolemStat(own, "base_number_of_projectiles_in_spiral_nova") + extra, 1, 256);
                long interval = ProjectileRuntime.Time(GolemStat(own, "projectile_spiral_nova_time_ms") / (1000d * count));
                var profile = use.Profile with { Count = count, SequentialDelay = interval, ShareSequentialHits = true };
                use = ClientDrawnVolley(conn, session, skillId, gem, use with { Profile = profile }, sequence, 0);
                var angles = RandomVolleyAngles(sequence, count, use.Emitter.Id, use.Aim - use.Origin);
                return use with { CanEmit = () => session.Active && conn.PlayerLife > 0 && conn.ProjectileActive(epoch),
                    Profile = use.Profile with { NativeVolley = new() { Angles = angles } } };
            });
            return Task.CompletedTask;
        }
        if (SkillsFor(conn).EffectId(gem.Effect) == "VaalFireball")
        {
            if (conn.VaalUse is not { Executed: true } paid || paid.Skill != skillId || paid.Effect != gem.Effect)
                return Task.CompletedTask;
            var own = SupportedSkill(conn, skillId).Stats;
            int count = GolemStat(own, "base_number_of_projectiles_in_spiral_nova");
            int span = GolemStat(own, "projectile_spiral_nova_time_ms");
            int degrees = GolemStat(own, "projectile_spiral_nova_angle");
            // Capture once at the cast contact. Walking after release cannot interrupt a paid spiral.
            ProjectileUse? captured = null;
            for (int i = 0; i < count; i++)
            {
                double turn = i / (double)count;
                session.Schedule(ProjectileRuntime.Time(acceptedAt + delay + turn * span / 1000d),
                    () => session.Active && conn.PlayerLife > 0 && (captured is not null || Valid() && VaalAdmissionCurrent(conn, paid)),
                    () =>
                    {
                        captured ??= CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll, sequence, sacrifice: sacrifice);
                        var direction = (captured.Aim - captured.Origin).Unit.Rotate(turn * degrees * Math.PI / 180);
                        return captured with { Aim = captured.Origin + direction * captured.Profile.Range };
                    });
            }
            return Task.CompletedTask;
        }
        int repeats = attack ? 0 : SpellRepeats(conn, skillId, gem.Effect);
        for (int release = 0; release <= repeats + seals; release++)
        {
            int ordinal = Math.Max(0, release - seals);
            int left = repeats - ordinal;
            double more = release > 0 && release <= seals ? reoccurrenceMore : 1;
            double castOffset = ordinal + Math.Min(release, seals) * .1;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + castOffset * cast.Value / 1000d + delay), Valid,
                () => ClientDrawnVolley(conn, session, skillId, gem,
                    CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll, sequence, (uint)left, delay,
                        cryExerted: cryExerted, sacrifice: sacrifice, repeat: ordinal, reoccurrenceMore: more),
                    sequence, (uint)left));
        }
        return Task.CompletedTask;
    }

    // A spell's projectiles are the client's own (ClientPredicted): the server spreads them as the client's volley
    // does, and flies Spark's sparks on the client's seeded wander from the caster's cell (SparkLegs), so a hit lands
    // where a projectile is drawn.
    static ProjectileUse ClientDrawnVolley(ClientState conn, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, ProjectileUse use, ushort sequence,
        uint repeatsLeft)
    {
        if (use.Payload is not ProjectileOffense { ClientPredicted: true }) return use;
        var player = CharacterPlayerStats(conn);
        var own = SupportedSkill(conn, skillId).Stats;
        int Player(string key) => Stats.Key(key) is uint k ? player.GetValueOrDefault(k) : 0;
        int Own(string key) => Stats.Key(key) is uint k ? own.GetValueOrDefault(k) : 0;
        int Get(string key) => Player(key) + Own(key);
        use = use with { Profile = ClientDrawnProfile(conn, use.Profile, Get, sequence, use.Aim - use.Origin, repeatsLeft, use.Emitter.Id) };
        if (SkillsFor(conn).EffectId(gem.Effect) == "SpectralThrow")
        {
            double thrown = use.Profile.Speed;
            return use with
            {
                // Its hits showed no blood or particles without the 0x1af the ordinary arrow sends (live 2026-11-02).
                Payload = (ProjectileOffense)use.Payload! with { HitPacket = true, ActionSequence = sequence },
                Profile = use.Profile with
                {
                    Legs = _ => new SpectralThrowLegs(thrown), LegLeash = SpectralThrowLegs.Leash(thrown), ReturnOvershoot = 0,
                    ReturnSpeedMultiplier = use.Profile.ReturnSpeedMultiplier * SpectralThrowLegs.ReturnSpeedMultiplier
                }
            };
        }
        if (GatewayProjectileSkillProfiles.IsKineticBolt(SkillsFor(conn).EffectId(gem.Effect) ?? ""))
        {
            return use with
            {
                Payload = (ProjectileOffense)use.Payload! with { HitPacket = true, ActionSequence = sequence },
                Profile = GatewayProjectileSkillProfiles.WithKineticBoltLegs(use.Profile, sequence)
            };
        }
        if (SkillsFor(conn).EffectId(gem.Effect) is not ("Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark" or "Twister")) return use;
        // The client's life is the skill record's skill_effect_duration, in whole milliseconds.
        double life = Math.Round(1000 * SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own)) / 1000;
        int randomDuration = Get("spark_random_duration_%");
        var sparkOrigin = use.Emitter.Id == PlayerId
            ? ProjectileSession.Vector(conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond))
            : use.Origin;
        double speed = use.Profile.Speed;
        var shot = (ProjectileOffense)use.Payload!;
        // The proxy's Actor action does not draw Spark. Give its simulated legs a flight object and the art's own clip.
        if (use.Emitter.Id != PlayerId)
            shot = shot with
            {
                ClientPredicted = false,
                Presentation = MonsterProjectilePresentation(SkillsFor(conn).Clips(use.Profile.Id), life)
            };
        return use with
        {
            Origin = sparkOrigin,
            Payload = shot,
            Profile = use.Profile with
            {
                Legs = i => new SparkLegs(sequence, i, use.Emitter.Id, life, randomDuration, speed, repeatsLeft),
                // 0x141b38c00: a spark ends 150 cells from where it started.
                LegLeash = SkillsFor(conn).EffectId(gem.Effect) == TwisterEffect ? 0 : 150
            }
        };
    }

    // What a projectile the client draws itself takes from the client's own rules, so a hit lands where it is drawn:
    // its range (slot 0x600, 0x141ba8d60: projectile_maximum_range_override, else 50 with the Reclaimed Malevolence
    // notable, else 150 cells; a return, 0x141bb5570, flies back to the caster's cell and that range on), its object
    // contact from the row's radii and the target's size instead of the server's 4-cell stand-in, its return rolls, and
    // its volley: 0x141bb3e30 picks parallel (0x141bbb460) and random directions (0x141bbbd10) before nova and fan.
    // shortcut: parallel keeps the server's fan until it is ported.
    public static ProjectileProfile ClientDrawnProfile(ClientState conn, ProjectileProfile profile, Func<string, int> get,
        ushort sequence, ProjectileVector aim, uint repeatsLeft = 0, uint? emitter = null)
    {
        double range = get("projectile_maximum_range_override") is > 0 and var maximum ? maximum
            : get("reclaimed_malevolence_notable_abyss_searching") != 0 ? 50 : 150;
        profile = profile with { Range = range, ReturnOvershoot = range, ContactCells = SkillsFor(conn).ProjectileContactCells(profile.Id) };
        if (profile.ReturnChancePercent is > 0 and < 100)
            profile = profile with { ReturnRolls = ReturnRolls(sequence, profile.Count, PlayerId, repeatsLeft) };
        if (get("support_parallel_projectile_number_of_points_per_side") == 0 && get("random_projectile_direction") != 0)
            return profile with { NativeVolley = new() { Angles = RandomVolleyAngles(sequence, profile.Count, emitter ?? PlayerId, aim, repeatsLeft) } };
        // 0x141bb5e80 (the Disgorge nova and the action's own flags are not tracked here).
        return NativeVolley(profile, get);
    }

    // random_projectile_direction (static RE 2026-10-31). A volley (0x141bb39b0 → 0x141bbbd10) seeds 0x141bbcb00 from
    // [sequence, action +0x11c (0), +0x2d0, +0x458 (0), owner, slot 0x540 (0), 0] and turns each projectile's aim
    // by a 0–2π draw. A single projectile (0x141bb3e30) seeds 0x141a0f3e0 from its detached copy, [sequence, 2, +0x458
    // (0), +0x2d0, owner], and flies along (sin a, cos a) whatever the aim. +0x2d0 is the cast's repeats left
    // (SpellRepeats), which the copy (0x1419e5750) keeps.
    public static double[] RandomVolleyAngles(ushort sequence, int count, uint owner, ProjectileVector aim, uint repeatsLeft = 0)
    {
        const float Tau = 6.2831855f;
        if (count == 1)
        {
            float a = new AnimationVariantSelector.NativeRandom([sequence, 2, 0, repeatsLeft, owner]).Range(0, Tau);
            return [Math.Atan2(MathF.Cos(a), MathF.Sin(a)) - Math.Atan2(aim.Y, aim.X)];
        }
        var random = new AnimationVariantSelector.NativeRandom([sequence, 0, repeatsLeft, 0, owner, 0, 0]);
        var angles = new double[count];
        for (int i = 0; i < count; i++) angles[i] = random.Range(0, Tau);
        return angles;
    }

    // projectile_return_%_chance (0x141bb2f80, static RE 2026-10-31): each projectile's own action copy rolls 0–99
    // (0x1404d0990) from a generator seeded (0x1419c73c0) with [sequence, +0x11c (2 + its index, as Spark's), owner,
    // +0x2d0 (the repeats left), byte +0x2b8, 0, 0, 0] and returns when the roll is under the chance.
    // shortcut: +0x2b8 is taken as 0 (only its decrement, 0x141ba70b0, was found); check it live with a return chance.
    public static int[] ReturnRolls(ushort sequence, int count, uint owner, uint repeatsLeft = 0)
    {
        var rolls = new int[count];
        for (int i = 0; i < count; i++)
            rolls[i] = new AnimationVariantSelector.NativeRandom([sequence, (uint)(i + 2), owner, repeatsLeft, 0, 0, 0, 0]).Uniform(0, 99);
        return rolls;
    }

    public static ProjectileUse CaptureGemProjectile(ClientState conn, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll criticalRoll, ushort sequence = 0, uint repeatsLeft = 0, double releaseDelay = 0,
        TotemState.Totem? totem = null, bool cryExerted = false, int repeat = 0, SacrificeUse? sacrifice = null, double reoccurrenceMore = 1)
    {
        // A delayed release reads current combat state once, independently of the input handler's earlier snapshot.
        // Its damage, crit, ailments and secondary stages resolve the same supported gem rather than repeating the
        // equipment/requirements/support walk for every field while holding the combat lock.
        using var snapshot = new PlayerStatScope(conn, fresh: true);
        var player = CharacterPlayerStats(conn);
        var admittedGem = SupportedSkill(conn, skillId);
        var own = admittedGem.Stats;
        var merged = MergeSkillStats(player, own);
        int Get(string key) => Stats.Key(key) is uint k ? merged.GetValueOrDefault(k) : 0;
        int Player(string key) => Stats.Key(key) is uint k ? player.GetValueOrDefault(k) : 0;
        int Own(string key) => Stats.Key(key) is uint k ? own.GetValueOrDefault(k) : 0;
        var definition = ResolveProjectileDefinition(gem.Effect) ?? (ArrowRain(SkillsFor(conn).EffectId(gem.Effect)) ? GroundArrow : null)
            ?? throw new InvalidOperationException("No projectile definition at release.");
        string metadata = definition.Metadata;
        int baseSpeed = SkillsFor(conn).Projectile(metadata)?.Speed ?? 1000;
        double speed = ProjectileSpeed.CellsPerSecond(baseSpeed, Player, Own, Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        if (!double.IsFinite(speed) || speed <= 0)
            throw new InvalidOperationException("Projectile release requires a finite positive speed.");
        var profile = definition.Parameters.Resolve(Get, speed, ArrowsFor(conn, gem.Effect));
        var at = totem?.At ?? conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
        var origin = ProjectileSession.Vector(at);
        if (definition.RequiresTerrain && ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, at.X, at.Y) is null)
            throw new InvalidOperationException("Projectile recipe requires a projectile terrain mask");
        var target = ProjectileSession.Vector(aim);
        var direction = (target - origin).Unit;
        bool spell = !SkillsFor(conn).IsAttack(gem.Effect);
        // Arrow Nova fires one payload arrow to the aim, then the skill's arrows in a circle from there.
        bool rain = SkillsFor(conn).IsAttack(gem.Effect)
            && (Get("projectiles_rain") != 0 || Get("skill_is_rain_skill") != 0);
        // An attack gem leaves from its weapon's contact keyframe, as the default attack's shot does: puncture_bow (Burning
        // Arrow's clip) has attack1_bow's contact and position. Released at the feet 1.5 cells ahead, the arrow showed up
        // inside the body at the ground instead of at the bow.
        var release = spell ? null : Animations.AttackContactPoint(
            Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject,
            WeaponProfileFor(conn, activeSet: true));
        // LightningSlash is released three cells ahead at ground level, not at the sword's contact point
        // (3.29.3c Positioned trace: player (391,352), every bolt starts (388,352) aimed left).
        double ahead = IsLightningStrike(gem.Effect) ? 3 : release is { ForwardCells: > 0 } point ? point.ForwardCells : 1.5;
        // A point closer than the muzzle must not end up behind the released projectile.
        if (totem is null) origin += direction * Math.Min(ahead, (target - origin).Length / 2);
        profile = profile with { Range = PrimaryRange(SkillsFor(conn).EffectId(gem.Effect), profile.Range,
            (target - origin).Length, toTarget: rain) };
        // Arrow Nova's payload takes the rig's fixed timeline, not the arrow's speed: the official landing rig came
        // 415 ms after the use at 40 cells (Frida 2026-11-04), so the flight is what is left after the release delay.
        if (rain)
        {
            double flight = (target - origin).Length;
            double untilLanding = Math.Max(0.05, 0.415 - releaseDelay);
            profile = profile with { Speed = Math.Max(0.001, flight / untilLanding) };
        }
        if (!spell) profile = NativeVolley(profile, Get);
        int variation = SkillArtVariation(conn, gem.Gem);
        profile = profile with { Id = Art.Projectile(profile.Id, variation) };
        var local = spell ? null : WeaponFor(conn).LocalSources;
        int addedChaos = sacrifice?.AddedChaos(() => session.Packet(sacrifice.LifePacket)) ?? 0;
        string hand = spell ? "spell" : "main_hand";
        var hitInputs = SupportedHitInputsFor(conn, skillId, area: false, capturedGem: admittedGem, projectile: true, addedChaos: addedChaos, totem: totem is not null);
        var areaHitInputs = SupportedHitInputsFor(conn, skillId, area: true, capturedGem: admittedGem, projectile: true, addedChaos: addedChaos, totem: totem is not null);
        var ailmentRows = hitInputs is null ? [] : SupportedAilmentRowsFrom(hitInputs, hand);
        long additionalBlindedIncreased = (long)Player(PlayerHitDamage.VsBlindedDamageStat)
            + Own(PlayerHitDamage.VsBlindedDamageStat);
        var blindedRows = hitInputs is null || additionalBlindedIncreased == 0 ? null
            : SupportedAilmentRowsFrom(hitInputs, hand, additionalBlindedIncreased);
        var shot = new ProjectileOffense(
            hitInputs is null ? DamagePacket.Empty : SupportedHitFrom(hitInputs, suppress: false),
            spell, PlayerAccuracy(conn, player),
            SupportedReleaseCriticalChance(conn, skillId), SupportedCriticalMultiplier(conn, skillId), gem.Name)
        {
            NoExtraCriticalDamage = CriticalHitsDealNoExtraDamage(player, own),
            // Child stages and release specialization can introduce areas after this root is resolved.
            AreaHit = areaHitInputs is null ? DamagePacket.Empty : SupportedHitFrom(areaHitInputs, suppress: false),
            VsBlindedCriticalChance = SupportedCriticalChanceVsBlinded(conn, skillId, release: true),
            VsProfaneCriticalChance = SupportedCriticalChanceVsProfane(conn, skillId, release: true),
            ActualCaster = totem is null ? null : TotemCasterStats(player),
            Effect = gem.Effect, Level = gem.Level, Quality = gem.Quality, SourceSkill = skillId,
            SkillStats = new Dictionary<uint, int>(own),
            Spider = hitInputs is null ? null : CaptureSpiderDamage(conn, hitInputs,
                AddedEffectiveness(conn, gem.Effect, gem.Level), local: local,
                bow: !spell && WeaponProfileFor(conn, activeSet: true).Bow),
            SpiderArea = areaHitInputs is null ? null : CaptureSpiderDamage(conn, areaHitInputs,
                AddedEffectiveness(conn, gem.Effect, gem.Level), local: local,
                bow: !spell && WeaponProfileFor(conn, activeSet: true).Bow),
            MirageSource = totem is null && Own("support_mirage_archer_base_duration") > 0 ? SupportedSkill(conn, skillId) : null,
            CriticalTrigger = spell ? null : CaptureCriticalTriggerSource(conn, skillId),
            Penetration = Penetration.Of(player, Stats, !spell, own, local), ResistanceMods = HitResistMods(player, own),
            ImpaleSettings = ImpaleStats.From(player, Stats, !spell, own, local),
            CriticalImpaleSettings = ImpaleStats.From(player, Stats, !spell, own, local, critical: true),
            AccuracyProfile = spell ? null : SupportedAccuracy(conn, skillId), CriticalRoll = criticalRoll,
            Leech = HitLeechSources(player, !spell, own, local),
            ImpaleChance = spell ? 0 : Get("impale_on_hit_%_chance"),
            // Burning Arrow's 25% base_chance_to_ignite_% and the caster's sources, with the panel's ignite range.
            Ignite = NotedIgnite(conn, skillId, WithIgniteSpread(MonsterIgnites.Settings.From(ailmentRows, hand, Get), player, own, spell, hand)),
            Poison = MonsterPoisons.Settings.From(ailmentRows, hand, Get, local,
                projectileAttack: !spell),
            EssenceDrain = EssenceDrainCast(conn, skillId, gem.Effect),
            Bleed = spell ? MonsterIgnites.Settings.None : BleedSettings(ailmentRows, "main_hand", Get, local, melee: false,
                bow: WeaponProfileFor(conn, activeSet: true).Bow, projectileAttack: true),
            VsBlinded = blindedRows is null || hitInputs is null ? null : new(
                SupportedHitFrom(hitInputs, suppress: false, additionalIncreased: additionalBlindedIncreased),
                areaHitInputs is null ? DamagePacket.Empty
                    : SupportedHitFrom(areaHitInputs, suppress: false,
                        additionalIncreased: additionalBlindedIncreased),
                MonsterIgnites.Settings.From(blindedRows, hand, Get),
                spell ? MonsterIgnites.Settings.None
                    : BleedSettings(blindedRows, "main_hand", Get, local, melee: false,
                        bow: WeaponProfileFor(conn, activeSet: true).Bow, projectileAttack: true),
                MonsterPoisons.Settings.From(blindedRows, hand, Get, local, projectileAttack: !spell)),
            MaimChance = spell ? 0 : Get("projectile_attacks_chance_to_maim_on_hit_%_if_you_have_beast_minion"),
            AccuracyMoreUnique = conn.Pools.AccuracyMoreVsUniquePct,
            AccuracyMoreClose = conn.Pools.AccuracyMoreInCloseRangePct,
            AlwaysHits = Get("global_always_hit") != 0, FarShot = Get("player_far_shot") != 0,
            PointBlank = Get("keystone_point_blank") != 0,
            ReturnMultiplier = ProjectileReturn.DamageMore(Get), Blind = BlindHitInput.Capture(player, own),
            // A spell's action spawns its projectiles itself, every one of a volley, from where it stands (Frida
            // 2026-10-29, 0x141ec47e0 from the action at 0x141e9a160): a server object for them was a second fireball.
            // The older packet-driven Burning Arrow control missed local input prediction: real mouse input creates
            // a client projectile too. ClientDrawsPrimaryProjectile handles its visual without changing simulation.
            // Spectral Throw's action draws its own weapon too (Frida 2026-11-02): a server object was a second one.
            // Arrow Nova's volley is drawn by the client (the 18 SplitArrowDefault children were client objects), but
            // its payload arrow is not, so the server still sends that one object.
            // Kinetic Bolt's ZigZagProjectile action creates and zig-zags both primary and secondary projectiles locally.
            ClientPredicted = totem is not null || spell
                || SkillsFor(conn).EffectId(gem.Effect) is "SpectralThrow" or "KineticBolt" or "KineticBoltAltX" or "KineticBoltAltY" || IsLightningStrike(gem.Effect),
            // Only where the skill's recipe draws nothing of its own (PresentationFor prefers this one).
            Presentation = rain
                // Arrow Nova's payload is the support's rain rig: the official server sends no payload object, only
                // this effect at the release and again where the volley leaves the ground (Frida 2026-11-04).
                ? new()
                {
                    LaunchHeight = release is { } bow ? bow.HeightSteps : null,
                    EventEffect = static e => e.Projectile.ParentId == 0
                        && e.Kind is ProjectileEventKind.Spawn or ProjectileEventKind.End
                        ? "Metadata/Effects/Spells/supports/arrow_rain/rain_rig.ao" : null,
                    // The official server attaches the skill's own arrow art to the rig's projectile bone, which is
                    // what draws the arrow leaving the bow (the rain rig alone is only the caster's animation).
                    AttachEffect = SkillsFor(conn).ProjectileAo(profile.Id) is { } arrowAo
                        ? (arrowAo, "aux_projectile_attach") : null,
                    // The native rain rig's player-side Positioned uses flags0 0x04 with the Animation.dat row (22,
                    // SpellAreaOfEffect) and its -172.0 value; the landing effect keeps the plain layout.
                    EffectAnimatedFlags = 9,
                    // The official rain rig's Animated bit3 word: 6 at the caster (the up-cast), 0 at the target
                    // (the landing); sending 0 on both made the player side play the landing backwards.
                    SpawnAnimatedId = 6,
                    SpawnAtCaster = true,
                    SpawnFlags0 = 4,
                    SpawnAnimationWord = 22,
                    SpawnAnimationValue = -172f,
                    EffectLifetime = ProjectileRuntime.Time(1)
                }
                : release is { } launch && definition.Presentation is null ? new() { LaunchHeight = launch.HeightSteps } : null,
        };
        shot = shot with { IntimidatingCryExerted = cryExerted };
        if (spell && repeat > 0)
        {
            double more = GreaterEchoHitMore(repeat, Own);
            shot = shot with { Hit = shot.Hit.Scale(more), AreaHit = shot.AreaHit?.Scale(more), HitProfileMore = more };
        }
        if (totem is not null || reoccurrenceMore != 1)
        {
            double totemMore = reoccurrenceMore * (totem is null ? 1 : Math.Max(0, 1 + Get("support_totem_damage_+%_final") / 100d));
            shot = shot with { Hit = shot.Hit.Scale(totemMore), AreaHit = shot.AreaHit?.Scale(totemMore),
                HitProfileMore = shot.HitProfileMore * totemMore, AilmentProfileMore = totemMore,
                Ignite = shot.Ignite with { Min = shot.Ignite.Min * totemMore, Max = shot.Ignite.Max * totemMore,
                    CriticalMin = shot.Ignite.CriticalMin * totemMore, CriticalMax = shot.Ignite.CriticalMax * totemMore },
                Poison = shot.Poison with { Min = shot.Poison.Min * totemMore, Max = shot.Poison.Max * totemMore,
                    CriticalMin = shot.Poison.CriticalMin * totemMore, CriticalMax = shot.Poison.CriticalMax * totemMore },
                Bleed = shot.Bleed with { Min = shot.Bleed.Min * totemMore, Max = shot.Bleed.Max * totemMore,
                    CriticalMin = shot.Bleed.CriticalMin * totemMore, CriticalMax = shot.Bleed.CriticalMax * totemMore },
                // Pending casts still require their source; released projectiles live with the area session.
                Leech = totem is null ? shot.Leech : new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty) };
        }
        uint emitter = totem?.Id ?? PlayerId;
        var use = new ProjectileUse(profile, new(emitter, conn.ProjectileEpoch), new(emitter, conn.ProjectileEpoch), 1, origin, target)
            { Payload = shot, ReturnAnchor = new(emitter, conn.ProjectileEpoch), Sequence = sequence, RepeatsLeft = repeatsLeft };
        var context = new ProjectileReleaseContext(conn, session, skillId, gem, variation, Get, Player, Own);
        return definition.SpecializeRelease?.Invoke(context, use) ?? use;
    }
}
