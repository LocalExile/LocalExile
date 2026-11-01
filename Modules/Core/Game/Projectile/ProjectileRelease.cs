using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    internal static bool UsesProjectileRuntime(int effect) => Skills.IsProjectile(effect)
        || ProjectileDefinition(Skills.EffectId(effect) ?? "") is not null;

    // Validate adopted paths before accepting an action, spending mana or reserving a cooldown.
    // Release still resolves again: equipment/stats may change during the ordinary cancellable windup.
    internal static bool ProjectilePathAvailable(string remote, ClientState conn, ushort skillId, int effect)
    {
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
            _ = definition.Parameters.Resolve(Get, 1);
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

    internal static Task ScheduleGemProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) aim, int action, int recoveryGeneration, CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll,
        ushort sequence = 0)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        bool attack = SkillsFor(conn).IsAttack(gem.Effect);
        bool cancelOnNewAction = !attack || ResolveProjectileDefinition(gem.Effect)?.CancelWindupOnNewAction == true;
        int? cast = attack ? 0 : CastMs(conn, gem.Effect, skillId);
        if (cast is null) return Task.CompletedTask;
        double acceptedAt = conn.Clock.Elapsed.TotalSeconds;
        double delay = attack ? AttackReleaseDelay(AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), false))
            : cast.Value / 1000d * Animations.SpellRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject);
        int epoch = conn.ProjectileEpoch;
        // Paid attacks survive Move unless their recipe opts into cancellable windup (e.g. release-time costs).
        // Spells and cooldown tickets retain action cancellation.
        // Epoch, death and hit recovery still cancel unreleased attacks.
        bool Valid() => conn.ProjectileActive(epoch) && conn.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(conn) ?? (cancelOnNewAction
            ? PlayerActionCurrent(conn, action)
            : conn.PlayerLife > 0 && conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds) > 0 && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds)))
            && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is null;
        // A spell's repeats (SpellRepeats) release again a cast time apart, with the use's critical roll; a new action
        // cancels the rest, as it ends the client's.
        // shortcut: a repeat keeps the cast's aim point; check live whether the client re-aims at a moving target.
        int repeats = attack ? 0 : SpellRepeats(conn, skillId, gem.Effect);
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int left = repeats - repeat;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + repeat * cast.Value / 1000d + delay), Valid,
                () => ClientDrawnVolley(conn, session, skillId, gem,
                    CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll), sequence, (uint)left));
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
        // The client's range (slot 0x600, 0x141ba62d0): projectile_maximum_range_override, else 50 with the Reclaimed
        // Malevolence notable, else 150 cells. A return (0x141bb2ae0) flies back to the caster's cell and that range on.
        double range = Get("projectile_maximum_range_override") is > 0 and var maximum ? maximum
            : Get("reclaimed_malevolence_notable_abyss_searching") != 0 ? 50 : 150;
        // The client's object contact uses the row's radii and the monster's size, not the server's 4-cell stand-in.
        use = use with { Profile = use.Profile with { Range = range, ReturnOvershoot = range,
            ContactCells = SkillsFor(conn).ProjectileContactCells(use.Profile.Id) } };
        // 0x141bb13a0 picks parallel (0x141bb89d0) and random directions (0x141bb9280) before nova and fan.
        // shortcut: parallel keeps the server's fan until it is ported.
        if (use.Profile.ReturnChancePercent is > 0 and < 100)
            use = use with { Profile = use.Profile with { ReturnRolls = ReturnRolls(sequence, use.Profile.Count, PlayerId, repeatsLeft) } };
        if (Get("support_parallel_projectile_number_of_points_per_side") == 0)
            use = use with
            {
                Profile = use.Profile with
                {
                    NativeVolley = Get("random_projectile_direction") != 0
                        ? new() { Angles = RandomVolleyAngles(sequence, use.Profile.Count, PlayerId, use.Aim - use.Origin, repeatsLeft) }
                        : new()
                        {
                            Width = Get("total_projectile_spread_angle_override") is > 0 and var width
                                ? width / 10.869565 : 60 / 10.869565,
                            FixedDegrees = Get("projectile_remove_default_spread") != 0 ? -1 : Get("fixed_projectile_spread"),
                            SpreadPercent = Get("multiple_projectiles_projectile_spread_+%"),
                            // 0x141bb33f0 (the Disgorge nova and the action's own flags are not tracked here).
                            Nova = Get("projectiles_nova") != 0
                        }
                }
            };
        if (SkillsFor(conn).EffectId(gem.Effect) is not ("Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark")) return use;
        // The client's life is the skill record's skill_effect_duration, in whole milliseconds.
        double life = Math.Round(1000 * SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own)) / 1000;
        int randomDuration = Get("spark_random_duration_%");
        var at = conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
        double speed = use.Profile.Speed;
        return use with
        {
            Origin = ProjectileSession.Vector(at),
            Profile = use.Profile with
            {
                Legs = i => new SparkLegs(sequence, i, PlayerId, life, randomDuration, speed, repeatsLeft),
                // 0x141b36170: a spark ends 150 cells from where it started.
                LegLeash = 150
            }
        };
    }

    // random_projectile_direction (static RE 2026-10-31). A volley (0x141bb0f20 → 0x141bb9280) seeds 0x141bba070 from
    // [sequence, action +0x11c (0), +0x2d0, +0x458 (0), owner, slot 0x540 (0), 0] and turns each projectile's aim
    // by a 0–2π draw. A single projectile (0x141bb13a0) seeds 0x141a0c9d0 from its detached copy, [sequence, 2, +0x458
    // (0), +0x2d0, owner], and flies along (sin a, cos a) whatever the aim. +0x2d0 is the cast's repeats left
    // (SpellRepeats), which the copy (0x1419e2d40) keeps.
    internal static double[] RandomVolleyAngles(ushort sequence, int count, uint owner, ProjectileVector aim, uint repeatsLeft = 0)
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

    // projectile_return_%_chance (0x141bb04f0, static RE 2026-10-31): each projectile's own action copy rolls 0–99
    // (0x1404d03d0) from a generator seeded (0x1419c29b0) with [sequence, +0x11c (2 + its index, as Spark's), owner,
    // +0x2d0 (the repeats left), byte +0x2b8, 0, 0, 0] and returns when the roll is under the chance.
    // shortcut: +0x2b8 is taken as 0 (only its decrement, 0x141ba4620, was found); check it live with a return chance.
    internal static int[] ReturnRolls(ushort sequence, int count, uint owner, uint repeatsLeft = 0)
    {
        var rolls = new int[count];
        for (int i = 0; i < count; i++)
            rolls[i] = new AnimationVariantSelector.NativeRandom([sequence, (uint)(i + 2), owner, repeatsLeft, 0, 0, 0, 0]).Uniform(0, 99);
        return rolls;
    }

    internal static ProjectileUse CaptureGemProjectile(ClientState conn, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim, SkillCriticalRoll criticalRoll)
    {
        var player = CharacterPlayerStats(conn);
        var own = SupportedSkill(conn, skillId).Stats;
        var merged = MergeSkillStats(player, own);
        int Get(string key) => Stats.Key(key) is uint k ? merged.GetValueOrDefault(k) : 0;
        int Player(string key) => Stats.Key(key) is uint k ? player.GetValueOrDefault(k) : 0;
        int Own(string key) => Stats.Key(key) is uint k ? own.GetValueOrDefault(k) : 0;
        var definition = ResolveProjectileDefinition(gem.Effect)
            ?? throw new InvalidOperationException("No projectile definition at release.");
        string metadata = definition.Metadata;
        int baseSpeed = SkillsFor(conn).Projectile(metadata)?.Speed ?? 1000;
        double speed = ProjectileSpeed.CellsPerSecond(baseSpeed, Player, Own, Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        if (!double.IsFinite(speed) || speed <= 0)
            throw new InvalidOperationException("Projectile release requires a finite positive speed.");
        var profile = definition.Parameters.Resolve(Get, speed);
        var at = conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
        var origin = ProjectileSession.Vector(at);
        if (definition.RequiresTerrain && ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, at.X, at.Y) is null)
            throw new InvalidOperationException("Projectile recipe requires a projectile terrain mask");
        var target = ProjectileSession.Vector(aim);
        var direction = (target - origin).Unit;
        bool spell = !SkillsFor(conn).IsAttack(gem.Effect);
        // An attack gem leaves from its weapon's contact keyframe, as the default attack's shot does: puncture_bow (Burning
        // Arrow's clip) has attack1_bow's contact and position. Released at the feet 1.5 cells ahead, the arrow showed up
        // inside the body at the ground instead of at the bow.
        var release = spell ? null : Animations.AttackContactPoint(
            Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject,
            WeaponProfileFor(conn, activeSet: true));
        double ahead = release is { ForwardCells: > 0 } point ? point.ForwardCells : 1.5;
        // A point closer than the muzzle must not end up behind the released projectile.
        origin += direction * Math.Min(ahead, (target - origin).Length / 2);
        int variation = SkillArtVariation(conn, gem.Gem);
        profile = profile with { Id = Art.Projectile(profile.Id, variation) };
        var local = spell ? null : WeaponFor(conn).LocalSources;
        var shot = new ProjectileOffense(SupportedProjectileHit(conn, skillId, area: false), spell, conn.Pools.Accuracy,
            SupportedReleaseCriticalChance(conn, skillId), SupportedCriticalMultiplier(conn, skillId), gem.Name)
        {
            // Child stages and release specialization can introduce areas after this root is resolved.
            AreaHit = SupportedProjectileHit(conn, skillId, area: true),
            VsBlindedCriticalChance = SupportedCriticalChanceVsBlinded(conn, skillId, release: true),
            VsProfaneCriticalChance = SupportedCriticalChanceVsProfane(conn, skillId, release: true),
            Effect = gem.Effect, Level = gem.Level, Quality = gem.Quality,
            SkillStats = new Dictionary<uint, int>(own),
            Penetration = Penetration.Of(player, Stats, !spell, own, local), ResistanceMods = HitResistMods(player, own),
            ImpaleSettings = ImpaleStats.From(player, Stats, !spell, own, local),
            CriticalImpaleSettings = ImpaleStats.From(player, Stats, !spell, own, local, critical: true),
            AccuracyProfile = spell ? null : SupportedAccuracy(conn, skillId), CriticalRoll = criticalRoll,
            Leech = HitLeechSources(player, !spell, own, local),
            ImpaleChance = spell ? 0 : Get("impale_on_hit_%_chance"),
            // Burning Arrow's 25% base_chance_to_ignite_% and the caster's sources, with the panel's ignite range.
            Ignite = MonsterIgnites.Settings.From(SupportedAilmentRows(conn, skillId, spell ? "spell" : "main_hand"),
                spell ? "spell" : "main_hand", Get),
            AccuracyMoreUnique = conn.Pools.AccuracyMoreVsUniquePct,
            AccuracyMoreClose = conn.Pools.AccuracyMoreInCloseRangePct,
            AlwaysHits = Get("global_always_hit") != 0, FarShot = Get("player_far_shot") != 0,
            PointBlank = Get("keystone_point_blank") != 0,
            ReturnMultiplier = ProjectileReturn.DamageMore(Get),
            // A spell's action spawns its projectiles itself, every one of a volley, from where it stands (Frida
            // 2026-10-29, 0x141ebdf30 from the action at 0x141e938c0): a server object for them was a second fireball.
            // An attack gem's does not: Burning Arrow's action 0x415 started and created no projectile until the server's
            // 0x2b9, as the default attack's arrow (Frida 2026-10-31).
            ClientPredicted = spell,
            // Only where the skill's recipe draws nothing of its own (PresentationFor prefers this one).
            Presentation = release is { } launch && definition.Presentation is null ? new() { LaunchHeight = launch.HeightSteps } : null,
        };
        var use = new ProjectileUse(profile, new(PlayerId, conn.ProjectileEpoch), new(PlayerId, conn.ProjectileEpoch), 1, origin, target)
            { Payload = shot, ReturnAnchor = new(PlayerId, conn.ProjectileEpoch) };
        var context = new ProjectileReleaseContext(conn, session, skillId, gem, variation, Get, Player, Own);
        return definition.SpecializeRelease?.Invoke(context, use) ?? use;
    }
}
