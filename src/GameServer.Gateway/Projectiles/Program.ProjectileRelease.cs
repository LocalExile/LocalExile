using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    static bool UsesProjectileRuntime(int effect) => Skills.IsProjectile(effect)
        || ProjectileDefinition(Skills.EffectId(effect) ?? "") is not null;

    // Validate adopted paths before accepting an action, spending mana or reserving a cooldown.
    // Release still resolves again: equipment/stats may change during the ordinary cancellable windup.
    static bool ProjectilePathAvailable(string remote, ClientState conn, ushort skillId, int effect)
    {
        if (!UsesProjectileRuntime(effect)) return true;
        try
        {
            var definition = ResolveProjectileDefinition(effect)
                ?? throw new InvalidOperationException("no available projectile recipe/metadata");
            var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
            if (gem.Name is null || !SkillHitPlan.ForSkill(Skills, effect, gem.Level, gem.Quality).CanHit)
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
            Console.WriteLine($"[gateway] {remote} {Skills.EffectId(effect)} unavailable: {ex.Message}");
            return false;
        }
    }

    static Task ScheduleGemProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) aim, int action, int recoveryGeneration, CooldownUseTicket? ticket)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        bool attack = Skills.IsAttack(gem.Effect);
        bool cancelOnNewAction = !attack || ResolveProjectileDefinition(gem.Effect)?.CancelWindupOnNewAction == true;
        int? cast = attack ? 0 : CastMs(conn, gem.Effect, skillId);
        if (cast is null) return Task.CompletedTask;
        double acceptedAt = conn.Clock.Elapsed.TotalSeconds;
        double delay = attack ? AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), false)
            : cast.Value / 1000d * Animations.SpellRelease(Skills.ClassFor(conn.ClassRow).AnimatedObject);
        int epoch = conn.ProjectileEpoch;
        session.Schedule(ProjectileRuntime.Time(acceptedAt + delay),
            // Paid attacks survive Move unless their recipe opts into cancellable windup (e.g. release-time costs).
            // Spells and cooldown tickets retain action cancellation.
            // Epoch, death and hit recovery still cancel unreleased attacks.
            () => conn.ProjectileActive(epoch) && conn.HitRecovery.Generation == recoveryGeneration
                && (ticket?.Current(conn) ?? (cancelOnNewAction
                ? PlayerActionCurrent(conn, action)
                : conn.PlayerLife > 0 && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds)))
                && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is null,
            () => CaptureGemProjectile(conn, session, skillId, gem, aim));
        return Task.CompletedTask;
    }

    static ProjectileUse CaptureGemProjectile(ClientState conn, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim)
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
        int baseSpeed = Skills.Projectile(metadata)?.Speed ?? 1000;
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
        // A point closer than the muzzle must not end up behind the released projectile.
        origin += direction * Math.Min(1.5, (target - origin).Length / 2);
        int variation = SkillArtVariation(conn, gem.Gem);
        profile = profile with { Id = Art.Projectile(profile.Id, variation) };
        bool spell = !Skills.IsAttack(gem.Effect);
        var shot = new ProjectileOffense(SupportedProjectileHit(conn, skillId, area: false), spell, conn.Pools.Accuracy,
            SupportedReleaseCriticalChance(conn, skillId), SupportedCriticalMultiplier(conn, skillId), gem.Name)
        {
            // Child stages and release specialization can introduce areas after this root is resolved.
            AreaHit = SupportedProjectileHit(conn, skillId, area: true),
            Effect = gem.Effect, Level = gem.Level, Quality = gem.Quality,
            SkillStats = new Dictionary<uint, int>(own),
            Penetration = Penetration.Of(player, Stats, !spell, own), ResistanceMods = HitResistMods(player, own),
            ImpaleChance = spell ? 0 : Get("impale_on_hit_%_chance"),
            AccuracyMoreUnique = conn.Pools.AccuracyMoreVsUniquePct,
            AccuracyMoreClose = conn.Pools.AccuracyMoreInCloseRangePct,
            AlwaysHits = Get("global_always_hit") != 0, FarShot = Get("player_far_shot") != 0,
            ReturnMultiplier = ProjectileReturn.DamageMore(Get)
        };
        var use = new ProjectileUse(profile, new(PlayerId, conn.ProjectileEpoch), new(PlayerId, conn.ProjectileEpoch), 1, origin, target)
            { Payload = shot, ReturnAnchor = new(PlayerId, conn.ProjectileEpoch) };
        var context = new ProjectileReleaseContext(conn, session, skillId, gem, variation, Get, Player, Own);
        return definition.SpecializeRelease?.Invoke(context, use) ?? use;
    }

}
