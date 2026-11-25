using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>ItemClasses row 13 is Bow. A gem skill fires arrows only while the active main hand is a bow; its
    /// additional-arrow stats use the native arrow alias rather than the generic projectile count alone.</summary>
    public const int BowClassRow = 13;

    public static bool ArrowsFor(Player player, int effect) =>
        SkillsFor(player).IsAttack(effect) && WeaponProfileFor(player, activeSet: true).Bow;

    public static bool ArrowsForSkill(int effect) =>
        Skills.IsAttack(effect) && Skills.WeaponRestrictions(effect).Contains(BowClassRow);

    /// <summary>Tornado Shot's primary arrow travels only to the targeted location, then releases its child group;
    /// it must not keep flying to the generic projectile range. Arrow Nova's payload arrow lands at the aim before its
    /// volley, so it stops there too. Other projectiles keep their full range.</summary>
    public static double PrimaryRange(string? effect, double range, double targetDistance, bool toTarget = false) =>
        // Caustic Arrow stops and bursts at the aimed point, where the client draws its impact (live 2026-11-17).
        toTarget || IsCreepingFrost(effect) || effect is "TornadoShot" or "TornadoShotAltX" or "TornadoShotAltY"
            || effect?.StartsWith("CausticArrow", StringComparison.Ordinal) == true
            ? Math.Min(range, Math.Max(.001, targetDistance)) : range;

    /// <summary>The shared client fan for a multi-arrow use. Attack gems and ordinary bow shots otherwise used the
    /// fixed 30-degree server spread, which could send an arrow past the point where the client's own arrow burst.</summary>
    public static ProjectileProfile NativeVolley(ProjectileProfile profile, Func<uint, int> get)
    {
        if (get(Stat.SupportParallelProjectileNumberOfPointsPerSide) != 0) return profile;
        return profile with
        {
            NativeVolley = new()
            {
                Width = get(Stat.TotalProjectileSpreadAngleOverride) is > 0 and var width
                    ? width / 10.869565 : 60 / 10.869565,
                FixedDegrees = get(Stat.ProjectileRemoveDefaultSpread) != 0 ? -1 : get(Stat.FixedProjectileSpread),
                SpreadPercent = get(Stat.MultipleProjectilesProjectileSpreadPlusPct),
                Nova = profile.NativeVolley?.Nova == true || get(Stat.ProjectilesNova) != 0
            }
        };
    }

    /// <summary>Applies <c>random_projectile_direction</c> (e.g. Nimis) using the client's seeded draw before falling
    /// back to the native fan, so non-<c>ClientPredicted</c> skills also randomize instead of firing a tight fan.</summary>
    public static ProjectileProfile NativeVolley(ProjectileProfile profile, Func<uint, int> get,
        ushort sequence, ProjectileVector aim, uint emitter, uint repeatsLeft = 0)
    {
        if (get(Stat.SupportParallelProjectileNumberOfPointsPerSide) == 0 && get(Stat.RandomProjectileDirection) != 0)
            return profile with { NativeVolley = new() { Angles = RandomVolleyAngles(sequence, profile.Count, emitter, aim, repeatsLeft) } };
        return NativeVolley(profile, get);
    }

    /// <summary>
    /// 3.29.3c client spawn forward offset (<c>0x141ba7840</c> → <c>0x141ba7c30</c>):
    /// only skills with <c>projectile_uses_contact_position</c> use the animation contact offset;
    /// all other skills offset 30 world units (~2.76 cells, or <c>projectile_firing_forward_distance_override</c>)
    /// along the aim direction unless <c>is_trap_object</c>, <c>is_remote_mine</c>, or <c>projectiles_not_offset</c> is set.
    /// </summary>
    public static double NativeProjectileForwardCells(Func<uint, int> get, ContactPoint? release, bool lightningStrike = false)
    {
        if (lightningStrike) return 3;
        if (get(Stat.ProjectileUsesContactPosition) != 0 && release is { ForwardCells: > 0 } contact)
            return contact.ForwardCells;
        if (get(Stat.IsTrapObject) != 0 || get(Stat.IsRemoteMine) != 0 || get(Stat.ProjectilesNotOffset) != 0)
            return 0;
        int overrideUnits = get(Stat.ProjectileFiringForwardDistanceOverride);
        return Math.Max(0, (overrideUnits != 0 ? overrideUnits : 30.0) / WorldUnits.PerCell);
    }

    public static bool UsesProjectileRuntime(int effect) => Skills.IsProjectile(effect)
        || ProjectileDefinition(Skills.EffectId(effect) ?? "") is not null;

    // Presentation only: these skills emit their own primary projectiles locally in 3.29.3c (verified live for
    // Burning Arrow, Caustic Arrow and Ice Shot). Keep their simulation geometry separate from ClientPredicted,
    // which also selects native range/contact/volley specialization.
    public static bool ClientDrawsPrimaryProjectile(bool predicted, string? effect) =>
        predicted || effect is "BurningArrow" or "CausticArrow" or "CausticArrowAltX" or "CausticArrowAltY" or "VaalCausticArrow"
            or "IceShot" or "IceShotAltX" or "IceShotAltY" or "IceShotRoyale" or "VaalIceShot";

    // Validate adopted paths before accepting an action, spending mana or reserving a cooldown.
    // Release still resolves again: equipment/stats may change during the ordinary cancellable windup.
    public static bool ProjectilePathAvailable(Player player, ushort skillId, int effect)
    {
        if (!TotemPathAvailable(player, skillId, effect)) return false;
        if (!UsesProjectileRuntime(effect)) return true;
        try
        {
            var definition = ResolveProjectileDefinition(effect)
                ?? throw new InvalidOperationException("no available projectile recipe/metadata");
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
            if (gem.Name is null || !SkillHitPlan.ForSkill(SkillsFor(player), effect, gem.Level, gem.Quality).CanHit)
                throw new InvalidOperationException("no supported projectile damage plan");
            var resolved = MergeSkillStats(CharacterPlayerStats(player), SupportedSkill(player, skillId).Stats);
            int Get(uint key) => resolved.GetValueOrDefault(key);
            _ = definition.Parameters.Resolve(Get, 1, ArrowsFor(player, effect));
            var at = player.Walk.Position(AreaTime(player));
            if (definition.RequiresTerrain && ProjectileGrid.Blocks(player.Layout.Id, player.Seed, at.X, at.Y) is null)
                throw new InvalidOperationException("Projectile recipe requires a projectile terrain mask");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} {SkillsFor(player).EffectId(effect)} unavailable: {ex.Message}");
            return false;
        }
    }

    public static Task ScheduleGemProjectile(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) aim, int action, int recoveryGeneration, CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll,
        ushort sequence = 0, double? swingAt = null, bool cryExerted = false, int seals = 0, double reoccurrenceMore = 1)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        bool attack = SkillsFor(player).IsAttack(gem.Effect);
        // On a lockstep client every action the server starts is one the client runs: a Move started before the release
        // (the early cancel it allows, LockstepPlayerFree) ended the client's attack there, so it ends this release too.
        bool cancelOnNewAction = !attack || player.Connection.LockstepClient
            || ResolveProjectileDefinition(gem.Effect)?.CancelWindupOnNewAction == true;
        int? cast = attack ? 0 : CastMs(player, gem.Effect, skillId);
        if (cast is null) return Task.CompletedTask;
        // From the swing's start when it began before now (a lockstep walk-up's goal frame, CastGemSkill's swingAt).
        double acceptedAt = swingAt ?? AreaTime(player);
        // A Spectral Throw's own weapon leaves on the client's swing (SpectralThrowRelease), without the server-object lead.
        double? thrown = attack && SkillsFor(player).EffectId(gem.Effect) is "SpectralThrow" or "SpectralThrowAltX"
            ? SpectralThrowRelease.Delay(player, Environment.GetEnvironmentVariable("Game_PLAYER_AO")
                ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject, AnimationWeaponProfileFor(player),
                SupportedAttackSeconds(player, skillId, sequence))
            : null;
        double delay = thrown is { } swing ? swing
            : attack ? AttackReleaseDelay(AttackContactDelay(player, SupportedAttackSeconds(player, skillId, sequence), false))
            : cast.Value / 1000d * Animations.SpellRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        int epoch = player.ProjectileEpoch;
        var sacrifice = new SacrificeUse(player, skillId, gem.Effect, gem.Gem);
        // Paid attacks survive Move unless their recipe opts into cancellable windup (e.g. release-time costs).
        // Spells and cooldown tickets retain action cancellation.
        // Epoch, death and hit recovery still cancel unreleased attacks.
        bool Valid() => sacrifice.Current && player.ProjectileActive(epoch) && player.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(player) ?? (cancelOnNewAction
            ? PlayerActionCurrent(player, action)
            : player.PlayerLife > 0 && player.ElementalAilments.ActionFactor(AreaTime(player)) > 0 && !player.HitRecovery.Active(AreaTime(player))))
            && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;
        if (IsBarrageGem(SkillsFor(player).EffectId(gem.Effect)))
        {
            ScheduleBarrage(player, session, skillId, gem.Effect, acceptedAt, SupportedAttackSeconds(player, skillId, sequence),
                sequence, Valid, firstRelease => CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll,
                    sequence, releaseDelay: firstRelease, cryExerted: cryExerted, sacrifice: sacrifice));
            return Task.CompletedTask;
        }
        // A spell's repeats (SpellRepeats) release again a cast time apart, with the use's critical roll; a new action
        // cancels the rest, as it ends the client's.
        // shortcut: a repeat keeps the cast's aim point; check live whether the client re-aims at a moving target.
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalSpark")
        {
            if (player.VaalUse is not { Executed: true } paid || paid.Skill != skillId || paid.Effect != gem.Effect)
                return Task.CompletedTask;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + delay), () => Valid() && VaalAdmissionCurrent(player, paid), () =>
            {
                var use = CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, sequence, sacrifice: sacrifice);
                var own = SupportedSkill(player, skillId).Stats;
                // The default profile substitutes one shot for a missing base count. Vaal Spark
                // already supplies that base as its hundred-shot stream; quality adds to the stream.
                int extra = use.Profile.Count - (GolemStat(own, Stat.BaseNumberOfProjectiles) > 0 ? 0 : 1);
                int count = Math.Clamp(GolemStat(own, Stat.BaseNumberOfProjectilesInSpiralNova) + extra, 1, 256);
                long interval = ProjectileRuntime.Time(GolemStat(own, Stat.ProjectileSpiralNovaTimeMs) / (1000d * count));
                var profile = use.Profile with { Count = count, SequentialDelay = interval, ShareSequentialHits = true };
                use = ClientDrawnVolley(player, session, skillId, gem, use with { Profile = profile }, sequence, 0);
                var angles = RandomVolleyAngles(sequence, count, use.Emitter.Id, use.Aim - use.Origin);
                return use with { CanEmit = () => session.Active && player.PlayerLife > 0 && player.ProjectileActive(epoch),
                    Profile = use.Profile with { NativeVolley = new() { Angles = angles } } };
            });
            return Task.CompletedTask;
        }
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalFireball")
        {
            if (player.VaalUse is not { Executed: true } paid || paid.Skill != skillId || paid.Effect != gem.Effect)
                return Task.CompletedTask;
            var own = SupportedSkill(player, skillId).Stats;
            int count = GolemStat(own, Stat.BaseNumberOfProjectilesInSpiralNova);
            int span = GolemStat(own, Stat.ProjectileSpiralNovaTimeMs);
            int degrees = GolemStat(own, Stat.ProjectileSpiralNovaAngle);
            // Capture once at the cast contact. Walking after release cannot interrupt a paid spiral.
            ProjectileUse? captured = null;
            for (int i = 0; i < count; i++)
            {
                double turn = i / (double)count;
                session.Schedule(ProjectileRuntime.Time(acceptedAt + delay + turn * span / 1000d),
                    () => session.Active && player.PlayerLife > 0 && (captured is not null || Valid() && VaalAdmissionCurrent(player, paid)),
                    () =>
                    {
                        captured ??= CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, sequence, sacrifice: sacrifice);
                        var direction = (captured.Aim - captured.Origin).Unit.Rotate(turn * degrees * Math.PI / 180);
                        return captured with { Aim = captured.Origin + direction * captured.Profile.Range };
                    });
            }
            return Task.CompletedTask;
        }
        int repeats = attack ? 0 : SpellRepeats(player, skillId, gem.Effect);
        var intensity = CastingIntensity(player, skillId);
        Action? completed = intensity is null ? null : intensity.Complete;
        for (int release = 0; release <= repeats + seals; release++)
        {
            int ordinal = Math.Max(0, release - seals);
            int left = repeats - ordinal;
            double more = release > 0 && release <= seals ? reoccurrenceMore : 1;
            double castOffset = ordinal + Math.Min(release, seals) * .1;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + castOffset * cast.Value / 1000d + delay), Valid,
                () =>
                {
                    using var scope = intensity is null ? null : new IntensityScope(intensity);
                    return ClientDrawnVolley(player, session, skillId, gem,
                        CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, sequence, (uint)left, delay,
                            cryExerted: cryExerted, sacrifice: sacrifice, repeat: ordinal, reoccurrenceMore: more),
                        sequence, (uint)left);
                }, completed);
        }
        return Task.CompletedTask;
    }

    // A spell's projectiles are the client's own (ClientPredicted): the server spreads them as the client's volley
    // does, and flies Spark's sparks on the client's seeded wander from the caster's cell (SparkLegs), so a hit lands
    // where a projectile is drawn.
    static ProjectileUse ClientDrawnVolley(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, ProjectileUse use, ushort sequence,
        uint repeatsLeft)
    {
        if (use.Payload is not ProjectileOffense { ClientPredicted: true }) return use;
        var playerStats = CharacterPlayerStats(player);
        var own = SupportedSkill(player, skillId).Stats;
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        int Own(uint k) => own.GetValueOrDefault(k);
        int Get(uint key) => Player(key) + Own(key);
        use = use with { Profile = ClientDrawnProfile(player, use.Profile, Get, sequence, use.Aim - use.Origin, repeatsLeft, use.Emitter.Id) };
        if (SkillsFor(player).EffectId(gem.Effect) == "BlazingSalvo" && use.Profile.NativeVolley is { Angles: null, Nova: false })
            use = use with { Profile = use.Profile with { NativeVolley = null } };
        // A totem's action plays its cast but spawns no projectile on the client (live 2026-11-19: a totem's Fireball
        // cast and hit with nothing flying), so the server draws a totem's shots, with the rig's flight and impact clips.
        if (use.Emitter.Id != player.Id && use.Payload is ProjectileOffense totemShot)
            use = use with { Payload = totemShot with { ClientPredicted = false,
                Presentation = WithProjectileClips(totemShot.Presentation, SkillsFor(player).Clips(use.Profile.Id),
                    use.Profile.Range / Math.Max(1, use.Profile.Speed)) } };
        if (SkillsFor(player).EffectId(gem.Effect) is "SpectralThrow" or "SpectralThrowAltX")
        {
            bool materialising = SkillsFor(player).EffectId(gem.Effect) == "SpectralThrowAltX";
            double thrown = use.Profile.Speed;
            double deceleration = Math.Max(.01, 1 + Get(Stat.SpectralThrowProjectileDecelerationPlusPct) / 100d);
            return use with
            {
                // Its hits showed no blood or particles without the 0x1af the ordinary arrow sends (live 2026-11-02).
                Payload = (ProjectileOffense)use.Payload! with { HitPacket = true, ActionSequence = sequence },
                Profile = use.Profile with
                {
                    Legs = _ => new SpectralThrowLegs(thrown, deceleration, returnAtWall: materialising),
                    LegLeash = SpectralThrowLegs.Leash(thrown, deceleration), ReturnOvershoot = 0,
                    ReturnSpeedMultiplier = use.Profile.ReturnSpeedMultiplier * SpectralThrowLegs.ReturnSpeedMultiplier
                }
            };
        }
        if (GatewayProjectileSkillProfiles.IsKineticBolt(SkillsFor(player).EffectId(gem.Effect) ?? ""))
        {
            return use with
            {
                Payload = (ProjectileOffense)use.Payload! with { HitPacket = true, ActionSequence = sequence },
                Profile = GatewayProjectileSkillProfiles.WithKineticBoltLegs(use.Profile, sequence)
            };
        }
        if (GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(gem.Effect)))
            return use;
        if (SkillsFor(player).EffectId(gem.Effect) is not ("Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark" or "Twister")) return use;
        // The client's life is the skill record's skill_effect_duration, in whole milliseconds.
        double life = Math.Round(1000 * SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own)) / 1000;
        int randomDuration = Get(Stat.SparkRandomDurationPct);
        var sparkOrigin = use.Emitter.Id == player.Id
            ? ProjectileSession.Vector(player.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond))
            : use.Origin;
        double speed = use.Profile.Speed;
        var shot = (ProjectileOffense)use.Payload!;
        // The proxy's Actor action does not draw Spark. Give its simulated legs a flight object and the art's own clip.
        if (use.Emitter.Id != player.Id)
            shot = shot with
            {
                ClientPredicted = false,
                Presentation = MonsterProjectilePresentation(SkillsFor(player).Clips(use.Profile.Id), life)
            };
        return use with
        {
            Origin = sparkOrigin,
            Payload = shot,
            Profile = use.Profile with
            {
                Legs = i => new SparkLegs(sequence, i, use.Emitter.Id, life, randomDuration, speed, repeatsLeft),
                // 0x141b38c00: a spark ends 150 cells from where it started.
                LegLeash = SkillsFor(player).EffectId(gem.Effect) == TwisterEffect ? 0 : 150
            }
        };
    }

    // What a projectile the client draws itself takes from the client's own rules, so a hit lands where it is drawn:
    // its range (slot 0x600, 0x141ba8d60: projectile_maximum_range_override, else 50 with the Reclaimed Malevolence
    // notable, else 150 cells; a return, 0x141bb5570, flies back to the caster's cell and that range on), its object
    // contact from the row's radii and the target's size instead of the server's 4-cell stand-in, its return rolls, and
    // its volley: 0x141bb3e30 picks parallel (0x141bbb460) and random directions (0x141bbbd10) before nova and fan.
    // shortcut: parallel keeps the server's fan until it is ported.
    public static ProjectileProfile ClientDrawnProfile(Player player, ProjectileProfile profile, Func<uint, int> get,
        ushort sequence, ProjectileVector aim, uint repeatsLeft = 0, uint? emitter = null)
    {
        double range = get(Stat.ProjectileMaximumRangeOverride) is > 0 and var maximum ? maximum
            : get(Stat.ReclaimedMalevolenceNotableAbyssSearching) != 0 ? 50 : 150;
        profile = profile with { Range = range, ReturnOvershoot = range, ContactCells = SkillsFor(player).ProjectileContactCells(profile.Id) };
        if (profile.ReturnChancePercent is > 0 and < 100)
            profile = profile with { ReturnRolls = ReturnRolls(sequence, profile.Count, player.Id, repeatsLeft) };
        if (get(Stat.SupportParallelProjectileNumberOfPointsPerSide) == 0 && get(Stat.RandomProjectileDirection) != 0)
            return profile with { NativeVolley = new() { Angles = RandomVolleyAngles(sequence, profile.Count, emitter ?? player.Id, aim, repeatsLeft) } };
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

    public static ProjectileUse CaptureGemProjectile(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll criticalRoll, ushort sequence = 0, uint repeatsLeft = 0, double releaseDelay = 0,
        TotemState.Totem? totem = null, bool cryExerted = false, int repeat = 0, SacrificeUse? sacrifice = null, double reoccurrenceMore = 1,
        (int X, int Y)? from = null, SupportedGem? capturedSkill = null)
    {
        // from: where a placed trap or mine stands, when it and not the player releases the skill.
        // A delayed release reads current combat state once, independently of the input handler's earlier snapshot.
        // Its damage, crit, ailments and secondary stages resolve the same supported gem rather than repeating the
        // equipment/requirements/support walk for every field while holding the combat lock.
        using var snapshot = new PlayerStatScope(player, fresh: true);
        var playerStats = CharacterPlayerStats(player);
        var admittedGem = capturedSkill ?? SupportedSkill(player, skillId);
        bool projectileDamage = admittedGem.WildStrikeFollowupElement is null or DamageType.Cold;
        var own = admittedGem.Stats;
        var merged = MergeSkillStats(playerStats, own);
        if (SkillsFor(player).EffectId(gem.Effect) is "Fireball" or "FireballAltX" or "FireballAltY")
        {
            bool cannotIgnite = merged.GetValueOrDefault(Stat.FireballCannotIgnite) != 0;
            int scorch = merged.GetValueOrDefault(Stat.FireballChanceToScorchPct);
            if (cannotIgnite || scorch != 0)
            {
                var projected = new Dictionary<uint, int>(own);
                if (cannotIgnite) projected[Stat.NeverIgnite] = 1;
                if (scorch != 0) projected[Stat.ChanceToScorchPct] =
                    ActorScale.Add(Stat.ChanceToScorchPct, projected.GetValueOrDefault(Stat.ChanceToScorchPct), scorch);
                own = projected;
                admittedGem = admittedGem with { Stats = own };
                merged = MergeSkillStats(playerStats, own);
            }
        }
        int Get(uint k) => merged.GetValueOrDefault(k);
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        int Own(uint k) => own.GetValueOrDefault(k);
        var definition = ResolveProjectileDefinition(gem.Effect) ?? (ArrowRain(SkillsFor(player).EffectId(gem.Effect)) ? GroundArrow : null)
            ?? throw new InvalidOperationException("No projectile definition at release.");
        string metadata = definition.Metadata;
        int baseSpeed = SkillsFor(player).Projectile(metadata)?.Speed ?? 1000;
        double speed = ProjectileSpeed.CellsPerSecond(baseSpeed, Player, Own, Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        if (!double.IsFinite(speed) || speed <= 0)
            throw new InvalidOperationException("Projectile release requires a finite positive speed.");
        var profile = definition.Parameters.Resolve(Get, speed, ArrowsFor(player, gem.Effect));
        var at = totem?.At ?? from ?? player.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
        var origin = ProjectileSession.Vector(at);
        if (definition.RequiresTerrain && ProjectileGrid.Blocks(player.Layout.Id, player.Seed, at.X, at.Y) is null)
            throw new InvalidOperationException("Projectile recipe requires a projectile terrain mask");
        var target = ProjectileSession.Vector(aim);
        var direction = (target - origin).Unit;
        bool spell = !SkillsFor(player).IsAttack(gem.Effect);
        // Arrow Nova fires one payload arrow to the aim, then the skill's arrows in a circle from there.
        bool rain = SkillsFor(player).IsAttack(gem.Effect)
            && (Get(Stat.ProjectilesRain) != 0 || Get(Stat.SkillIsRainSkill) != 0);
        // An attack gem leaves from its weapon's contact keyframe, as the default attack's shot does: puncture_bow (Burning
        // Arrow's clip) has attack1_bow's contact and position. Released at the feet 1.5 cells ahead, the arrow showed up
        // inside the body at the ground instead of at the bow.
        var release = spell ? null : Animations.AttackContactPoint(
            Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject,
            AnimationWeaponProfileFor(player));
        // 3.29.3c client spawn forward offset (0x141ba7840 -> 0x141ba7c30): 30 world units (~2.76 cells) unless
        // projectile_uses_contact_position, projectile_firing_forward_distance_override, or projectiles_not_offset applies.
        double ahead = NativeProjectileForwardCells(Get, release, IsLightningStrike(gem.Effect));
        // A point closer than the muzzle must not end up behind the released projectile.
        if (totem is null && from is null && ahead > 0)
            origin += direction * Math.Min(ahead, (target - origin).Length / 2);
        profile = profile with { Range = PrimaryRange(SkillsFor(player).EffectId(gem.Effect), profile.Range,
            (target - origin).Length, toTarget: rain) };
        // Arrow Nova's payload takes the rig's fixed timeline, not the arrow's speed: the official landing rig came
        // 415 ms after the use at 40 cells (Frida 2026-11-04), so the flight is what is left after the release delay.
        if (rain)
        {
            double flight = (target - origin).Length;
            double untilLanding = Math.Max(0.05, 0.415 - releaseDelay);
            profile = profile with { Speed = Math.Max(0.001, flight / untilLanding) };
        }
        if (!spell) profile = NativeVolley(profile, Get, sequence, target - origin, totem?.Id ?? player.Id, repeatsLeft);
        int variation = SkillArtVariation(player, gem.Gem);
        profile = profile with { Id = Art.Projectile(profile.Id, variation) };
        // A dual-wielded attack's projectiles carry the hand this use swings (AttackHands): its weapon's damage, local
        // modifiers, accuracy and critical chance. A totem's own use swings the main hand.
        var swingHand = spell || totem is not null ? AttackHand.Main : AttackHandFor(player, skillId, sequence);
        var shield = spell ? null : ShieldWeapon(player, skillId);
        var handWeapon = shield ?? OffHandSwing(player, swingHand);
        int? handCritical = SwingWeaponCritical(player, swingHand);
        var local = spell || shield is not null ? null : HandLocalSources(player, swingHand);
        int addedChaos = sacrifice?.AddedChaos() ?? 0;
        string hand = spell ? "spell" : "main_hand";
        var hitInputs = SupportedHitInputsFor(player, skillId, area: false, weapon: handWeapon, capturedGem: admittedGem, projectile: true, addedChaos: addedChaos, totem: totem is not null);
        var areaHitInputs = SupportedHitInputsFor(player, skillId, area: true, weapon: handWeapon, capturedGem: admittedGem, projectile: true, addedChaos: addedChaos, totem: totem is not null);
        var ailmentRows = hitInputs is null ? [] : SupportedAilmentRowsFrom(hitInputs, hand);
        long additionalBlindedIncreased = (long)Player(PlayerHitDamage.VsBlindedDamageStat)
            + Own(PlayerHitDamage.VsBlindedDamageStat);
        var blindedRows = hitInputs is null || additionalBlindedIncreased == 0 ? null
            : SupportedAilmentRowsFrom(hitInputs, hand, additionalBlindedIncreased);
        bool weaponAttack = !spell && local is not null && SkillsFor(player).IsAttack(gem.Effect) && !SkillsFor(player).RequiresShield(gem.Effect);
        var shot = new ProjectileOffense(
            hitInputs is null ? DamagePacket.Empty : SupportedHitFrom(hitInputs, suppress: false),
            spell, handWeapon is null ? PlayerAccuracy(player, playerStats) : HandAccuracy(player, swingHand, playerStats),
            SupportedCriticalChanceFor(player, skillId, release: true, weaponCritical: handCritical),
            SupportedCriticalMultiplier(player, skillId, handWeapon), gem.Name)
        {
            WeaponAttack = weaponAttack,
            TargetCritical = TargetCriticalBonuses.From(playerStats, own),
            SkeletonWeaponContactSource = weaponAttack && totem is null ? SkeletonWeaponContactSource(player, swingHand, local) : null,
            ProjectileAttack = projectileDamage && !spell && SkillsFor(player).IsProjectile(gem.Effect),
            SkillResistanceInversionChance = Own(Stat.TreatEnemyResistancesAsNegatedOnElementalDamageHitPctChance),
            TinctureKill = weaponAttack && totem is null ? TinctureKillBonusFor(player, playerStats, swingHand) : null,
            NoExtraCriticalDamage = CriticalHitsDealNoExtraDamage(playerStats, own),
            // Child stages and release specialization can introduce areas after this root is resolved.
            AreaHit = areaHitInputs is null ? DamagePacket.Empty : SupportedHitFrom(areaHitInputs, suppress: false),
            VsBlindedCriticalChance = SupportedCriticalChanceVsBlinded(player, skillId, release: true, handCritical),
            VsGroundCriticalChance = SupportedCriticalChanceVsGround(player, skillId, release: true, handCritical),
            ActualCaster = totem is null ? null : TotemCasterStats(playerStats), FromTotem = totem is not null,
            Effect = gem.Effect, Level = gem.Level, Quality = gem.Quality, SourceSkill = skillId, Gem = gem.Gem,
            HitPacket = true, ActionSequence = sequence,
            SkillStats = new Dictionary<uint, int>(own), FirstHit = new(),
            FreezingPulseFalloff = SkillsFor(player).EffectId(gem.Effect) is
                "FreezingPulse" or "FreezingPulseAltX" or "FreezingPulseAltY" or "FreezingPulseRoyale",
            RemainingChainDamagePercent = Own(Stat.CobraLashHitAndAilmentDamagePlusPctFinalForEachRemainingChain),
            Spider = hitInputs is null ? null : CaptureSpiderDamage(player, hitInputs,
                AddedEffectiveness(player, gem.Effect, gem.Level), context: false, local: local,
                bow: !spell && WeaponProfileFor(player, activeSet: true).Bow, offHand: swingHand == AttackHand.Off),
            SpiderArea = areaHitInputs is null ? null : CaptureSpiderDamage(player, areaHitInputs,
                AddedEffectiveness(player, gem.Effect, gem.Level), context: false, local: local,
                bow: !spell && WeaponProfileFor(player, activeSet: true).Bow, offHand: swingHand == AttackHand.Off),
            MirageSource = totem is null && Own(Stat.SupportMirageArcherBaseDuration) > 0 ? SupportedSkill(player, skillId) : null,
            CriticalTrigger = spell ? null : CaptureCriticalTriggerSource(player, skillId),
            Penetration = Penetration.Of(playerStats, Stats, !spell, own, local), ResistanceMods = HitResistMods(playerStats, own),
            ImpaleSettings = ImpaleStats.From(playerStats, Stats, !spell, own, local),
            CriticalImpaleSettings = ImpaleStats.From(playerStats, Stats, !spell, own, local, critical: true),
            AccuracyProfile = spell ? null : SupportedAccuracy(player, skillId, swingHand), CriticalRoll = criticalRoll,
            Leech = HitLeechSources(playerStats, !spell, own, weaponAttack ? local : null), WeaponLocal = local,
            ImpaleChance = spell ? 0 : Get(Stat.ImpaleOnHitPctChance),
            // Burning Arrow's 25% base_chance_to_ignite_% and the caster's sources, with the panel's ignite range.
            Ignite = WithOverheat(player, skillId, NotedIgnite(player, skillId, WithIgniteSpread(MonsterIgnites.Settings.From(ailmentRows, hand, Get), playerStats, own, spell, hand))),
            Poison = WithOverheat(player, skillId, MonsterPoisons.Settings.From(ailmentRows, hand, Get, local,
                projectileAttack: projectileDamage && !spell)),
            EssenceDrain = EssenceDrainCast(player, skillId, gem.Effect),
            Bleed = spell ? MonsterIgnites.Settings.None : WithOverheat(player, skillId, BleedSettings(ailmentRows, "main_hand", Get, local, melee: false,
                bow: WeaponProfileFor(player, activeSet: true).Bow, projectileAttack: projectileDamage)),
            VsBlinded = blindedRows is null || hitInputs is null ? null : new(
                SupportedHitFrom(hitInputs, suppress: false, additionalIncreased: additionalBlindedIncreased),
                areaHitInputs is null ? DamagePacket.Empty
                    : SupportedHitFrom(areaHitInputs, suppress: false,
                        additionalIncreased: additionalBlindedIncreased),
                WithOverheat(player, skillId, NotedIgnite(player, skillId, WithIgniteSpread(
                    MonsterIgnites.Settings.From(blindedRows, hand, Get), playerStats, own, spell, hand))),
                spell ? MonsterIgnites.Settings.None
                    : WithOverheat(player, skillId, BleedSettings(blindedRows, "main_hand", Get, local, melee: false,
                        bow: WeaponProfileFor(player, activeSet: true).Bow, projectileAttack: projectileDamage)),
                WithOverheat(player, skillId, MonsterPoisons.Settings.From(blindedRows, hand, Get, local, projectileAttack: projectileDamage && !spell))),
            MaimChance = spell ? 0 : Get(Stat.ProjectileAttacksChanceToMaimOnHitPctIfYouHaveBeastMinion),
            AccuracyMoreUnique = player.Pools.AccuracyMoreVsUniquePct,
            LocalStunThresholdReduction = spell ? 0 : local?.GetValueOrDefault(Stat.LocalStunThresholdReductionPlusPct) ?? 0,
            WeaponClass = spell ? null : (handWeapon ?? WeaponFor(player)).ItemClass ?? "",
            AccuracyMoreClose = player.Pools.AccuracyMoreInCloseRangePct,
            AlwaysHits = Get(Stat.GlobalAlwaysHit) != 0, FarShot = projectileDamage && Get(Stat.PlayerFarShot) != 0,
            PointBlank = projectileDamage && Get(Stat.KeystonePointBlank) != 0,
            ReturnMultiplier = ProjectileReturn.DamageMore(Get), Blind = BlindHitInput.Capture(playerStats, own),
            // A spell's action spawns its projectiles itself, every one of a volley, from where it stands (Frida
            // 2026-10-29, 0x141ec47e0 from the action at 0x141e9a160): a server object for them was a second fireball.
            // The older packet-driven Burning Arrow control missed local input prediction: real mouse input creates
            // a client projectile too. ClientDrawsPrimaryProjectile handles its visual without changing simulation.
            // Spectral Throw's action draws its own weapon too (Frida 2026-11-02): a server object was a second one.
            // Arrow Nova's volley is drawn by the client (the 18 SplitArrowDefault children were client objects), but
            // its payload arrow is not, so the server still sends that one object.
            // Kinetic Bolt's ZigZagProjectile action creates and zig-zags both primary and secondary projectiles locally.
            // Molten Strike's action spawns its local chunks on melee hit (0x141a6a4b0 -> 0x141ba7c30); server objects would duplicate them.
            ClientPredicted = totem is not null || spell
                || SkillsFor(player).EffectId(gem.Effect) is "SpectralThrow" or "SpectralThrowAltX" or "KineticBolt" or "KineticBoltAltX" or "KineticBoltAltY" || IsLightningStrike(gem.Effect)
                || GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(gem.Effect)),
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
                    AttachEffect = SkillsFor(player).ProjectileAo(profile.Id) is { } arrowAo
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
            double totemMore = reoccurrenceMore * (totem is null ? 1 : Math.Max(0, 1 + Get(Stat.SupportTotemDamagePlusPctFinal) / 100d));
            shot = shot.ScaleDamage(totemMore) with
            {
                // Pending casts still require their source; released projectiles live with the area session.
                Leech = totem is null ? shot.Leech : new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
            };
        }
        uint emitter = totem?.Id ?? player.Id;
        var use = new ProjectileUse(profile, new(emitter, player.ProjectileEpoch), new(emitter, player.ProjectileEpoch), 1, origin, target)
            { Payload = shot, ReturnAnchor = new(emitter, player.ProjectileEpoch), Sequence = sequence, RepeatsLeft = repeatsLeft };
        var context = new ProjectileReleaseContext(player, session, skillId, gem, variation, Get, Player, Own);
        if (IsExplosiveArrow(SkillsFor(player).EffectId(gem.Effect)) && areaHitInputs is not null)
            use = use with { Payload = shot with { ExplosiveFuse = CaptureExplosiveFuse(context, areaHitInputs, playerStats, own, hand, local) } };
        return definition.SpecializeRelease?.Invoke(context, use) ?? use;
    }
}
