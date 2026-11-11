using System.IO;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Winter Orb (effect <c>WinterOrb</c>, ActiveSkillType Channel): channelling conjures an orb above the player that
/// pelts nearby enemies with mortar projectiles that impact the ground and explode. The exported stats (3.29.3c,
/// GrantedEffectStatSets "WinterOrb") carry the damage (<c>spell_minimum/maximum_base_cold_damage</c> 39–59 at gem
/// level 1), the cadence (<c>frost_fury_base_fire_interval_ms</c> 1200,
/// <c>frost_fury_fire_speed_+%_final_while_channelling</c> 80), the stages (<c>frost_fury_max_number_of_stages</c> 8 plus
/// the quality's <c>frost_fury_additional_max_number_of_stages</c>, <c>frost_fury_fire_speed_+%_per_stage</c> 20,
/// <c>frost_fury_duration_+%_per_stage</c> 30, <c>base_skill_effect_duration</c> 1600), the projectiles
/// (<c>base_number_of_projectiles</c> 2 up to gem level 13, 3 from level 14; <c>additional_projectiles_fired_with_distance_offset</c>
/// 100), the critical strike chance (GESSPL SpellCritChance 750 permyriad = 7.5% at every level) and the 25% chance
/// to freeze (<c>base_chance_to_freeze_%</c> 25, read by the hit's ailment pass). Cast speed is additive with the
/// frequency increases and the first stage adds none (PoE Wiki, Winter Orb, "Projectile Rate").
/// The orb is its own runtime: the channel's cast ticks build stages, but the orb keeps firing after the button comes up,
/// with the 80% more frequency gone, while its stages decay one at a time across the duration they leave (the gem's
/// 1.6 s grown by 30% per stage). When the last stage goes the orb is removed.
/// The stages also show in the buff bar, as the game's own <c>frost_fury_stage</c> row (BuffDefinitions 1216, ShowCount,
/// its icon the skill's own): the count is the orb's (bit2) and, once the button is up, the icon counts down the
/// duration the stages leave (bit1). The row's BuffVisuals carries the client's Winter Orb channelling art, which reads
/// this buff's trailing u32 list (buff +0x80) at entries 1 and 2 with no length check: an empty list dereferenced null
/// at 0x1417b6067 and killed the client (three live tests 2026-11-09), so every 0x1c1 here carries three entries.
/// The orb itself is the client's own art: the buff above plays the skill's channelling effect, which draws the orb and
/// its swirl at the caster (live 2026-11-09, seen with no effect object of the player's). An effect object of
/// MiscAnimated FrostFurySphere 2494 at the player's cell was a second, ground-level orb beside it, so none is created.
/// Each impact is still an effect object of FrostFuryExplode 2495 at its cell.
/// </summary>
static partial class Game
{
    // The orb's own art comes from the client (see the class summary); only its impacts are objects of ours.
    const string WinterOrbImpactArt = "Metadata/Effects/Spells/FrostFury/projectile_impact.ao";

    // FrostFuryExplode's clip is 250 ms (MiscAnimated __col4 250): the object goes when it has played.
    const int WinterOrbImpactMilliseconds = 250;

    /// <summary>
    /// One orb, from its channel until the duration its stages leave runs out. It outlives the channel that made it, so
    /// the firing loop (and not the channel's) owns both the interval and the deadline.
    /// </summary>
    internal sealed class WinterOrbRuntime
    {
        /// <summary>Identifies this orb against a later one on the same connection: its loop stops when it changes.</summary>
        public long Generation;

        public uint ObjectId;
        public ushort SkillId;
        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;
        public int Action;
        /// <summary>The channel generation that last held the orb: an older channel's end must not release a newer one.</summary>
        public int ChannelGeneration;
        public int Stages;
        /// <summary>True while the button is held: stages build and the frequency keeps its 80% more.</summary>
        public bool Channelling;
        public double NextFireAt;
        /// <summary>When the stages left at the release have all decayed; zero while channelling.</summary>
        public double ExpiresAt;
        public double NextDecayAt;
        /// <summary>Seconds each stage lasts after the release: the remaining duration divided by the stages it holds.</summary>
        public double DecaySeconds;
        /// <summary>The 0x1c1 instance the buff bar shows the stages under; zero while no icon is up.</summary>
        public ushort PresentedBuff;
        /// <summary>The count the last 0x1c1 carried: an unchanged count sends no packet.</summary>
        public int PresentedStages = -1;
    }

    static long WinterOrbOrbs;

    internal static bool IsWinterOrb(int effect) =>
        Skills.EffectId(effect) is "WinterOrb" or "WinterOrbAltX" or "WinterOrbAltY";

    static int WinterOrbStat(ClientState conn, ushort skillId, string name) =>
        Stats.Key(name) is uint key ? SupportedSkill(conn, skillId).Stats.GetValueOrDefault(key) : 0;

    /// <summary>
    /// Seconds between the orb's volleys: the base fire interval divided by the frequency increases. Cast speed is
    /// additive with them and the first stage adds none, then the 80% more while channelling multiplies the result
    /// (PoE Wiki: 1/1.2 × (1 + 0.2 × stages past the first + cast speed) × 1.8). That multiplier belongs to the channel,
    /// so the caller passes the stages alone and the interval is slower once the button is up.
    /// </summary>
    internal static double WinterOrbFireSeconds(ClientState conn, ushort skillId, int effect, int stages,
        bool channelling = true)
    {
        int Stat(string name) => WinterOrbStat(conn, skillId, name);
        double baseSeconds = Math.Max(.05,
            (Stat("frost_fury_base_fire_interval_ms") is > 0 and var milliseconds ? milliseconds : 1200) / 1000.0);
        double castMs = CastMs(conn, effect, skillId) is int supported and > 0 ? supported : 250;
        double increased = 250.0 / castMs - 1
            + Stat("frost_fury_fire_speed_+%_per_stage") * Math.Max(0, stages - 1) / 100.0;
        double more = channelling
            ? Math.Max(.01, 1 + Stat("frost_fury_fire_speed_+%_final_while_channelling") / 100.0) : 1;
        return baseSeconds / (Math.Max(.01, 1 + increased) * more);
    }

    /// <summary>The gem's 8 stages plus what quality adds, both from the data rather than from a constant.</summary>
    static int WinterOrbMaxStages(ClientState conn, ushort skillId)
    {
        int max = WinterOrbStat(conn, skillId, "frost_fury_max_number_of_stages");
        int extra = WinterOrbStat(conn, skillId, "frost_fury_additional_max_number_of_stages");
        return Math.Clamp((max > 0 ? max : 8) + Math.Max(0, extra), 1, 32);
    }

    /// <summary>The game's own stage row: BuffDefinitions 1216 <c>frost_fury_stage</c> (ShowCount, the skill's icon).</summary>
    const ushort WinterOrbStagesBuffRow = 1216;

    // The trailing u32 list every stage buff carries; an empty one makes the client's art dereference null.
    static readonly uint[] WinterOrbStagesList = [0, 0, 0];

    /// <summary>
    /// The orb's stages in the top-left buff bar. The count is the orb's own, so it never shows a stage the orb does not
    /// hold; a changed count replaces the instance (0x1c2 for the old one, then 0x1c1, as the charge buffs do), and the
    /// icon stays up while the released orb's stages decay. While channelling it has no timer; after the release it
    /// counts the duration the stages leave, re-sent with every stage that goes.
    /// The three trailing u32 entries are required: this row's visual starts the client's Winter Orb channelling art
    /// (0x1415d17d0 → 0x1417b6030), which reads entries 1 and 2 of the buff's list without checking its length (a
    /// non-totem, first-form visual is all zeroes). Nothing else consumes them.
    /// </summary>
    internal static async Task WinterOrbPresentStages(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        byte[] packet;
        int stages;
        lock (conn.Monsters)
        {
            var runtime = conn.OrbRuntime;
            if (runtime is null || runtime.Stages <= 0 || runtime.PresentedStages == runtime.Stages) return;
            stages = runtime.Stages;
            double left = runtime.Channelling ? 0 : runtime.ExpiresAt - conn.Clock.Elapsed.TotalSeconds;
            // The same instance is updated in place: replacing it (0x1c2 + a new 0x1c1) restarted the client's timer with
            // every stage, so a duration it draws could never be seen running.
            ushort instance = runtime.PresentedBuff;
            if (instance == 0) instance = runtime.PresentedBuff = conn.NextBuff++;
            packet = InstanceEntities.AddBuff(PlayerId, instance, WinterOrbStagesBuffRow, PlayerId, runtime.SkillId,
                u68: left > 0 ? (uint?)BitConverter.SingleToUInt32Bits((float)left) : null,
                stacks: (ushort)runtime.Stages, list: WinterOrbStagesList);
            runtime.PresentedStages = runtime.Stages;
        }
        await Send(remote, stream, ciphers, packet, $"winter orb {stages} stages");
    }

    /// <summary>
    /// The channel started (or started again while the orb is still up): one orb per player, not one per channel, so a
    /// second channel keeps the runtime it finds and only refreshes it — no second sphere, and the stages it already
    /// built are kept instead of being dropped.
    /// </summary>
    internal static async Task<uint> WinterOrbStart(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int action)
    {
        WinterOrbRuntime runtime;
        bool created = false;
        lock (conn.Monsters)
        {
            runtime = conn.OrbRuntime ?? new WinterOrbRuntime();
            if (runtime.ObjectId == 0)
            {
                runtime.ObjectId = conn.NextObjectId++;
                runtime.Generation = ++WinterOrbOrbs;
                conn.OrbRuntime = runtime;
                created = true;
            }
            double now = conn.Clock.Elapsed.TotalSeconds;
            runtime.SkillId = skillId;
            runtime.Gem = gem;
            runtime.Action = action;
            runtime.ChannelGeneration = conn.ChannelGeneration;
            runtime.Channelling = true;
            runtime.ExpiresAt = 0;
            runtime.NextDecayAt = 0;
            // The first moment of channelling is the first stage; a fresh orb therefore never fires at zero. A count
            // carried from an earlier orb never exceeds this gem's maximum, which another gem or quality can lower.
            runtime.Stages = Math.Clamp(runtime.Stages, 1, WinterOrbMaxStages(conn, skillId));
            runtime.NextFireAt = now + WinterOrbFireSeconds(conn, skillId, gem.Effect, runtime.Stages);
            // A held stage has no countdown: the release resends the icon with the duration its stages leave.
            runtime.PresentedStages = -1;
            if (!created)
                Console.WriteLine($"[gateway] {remote} winter orb 0x{runtime.ObjectId:x} re-channelled at {runtime.Stages} stages");
        }
        await WinterOrbPresentStages(remote, stream, ciphers, conn);
        if (created)
        {
            // The loop outlives this channel and this call: it must not inherit the channel's output delegates.
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(() => RunWinterOrb(conn, runtime, runtime.Generation));
        }
        return runtime.ObjectId;
    }

    /// <summary>
    /// One more stage for this channel's cast tick, up to the maximum; the cast tick paces it, so a faster caster charges
    /// the orb sooner. The channel does not stop at the maximum.
    /// </summary>
    internal static int WinterOrbStageStep(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var runtime = conn.OrbRuntime;
            if (runtime is null) return 0;
            int max = WinterOrbMaxStages(conn, runtime.SkillId);
            if (runtime.Stages < max) runtime.Stages++;
            return runtime.Stages;
        }
    }

    /// <summary>
    /// The channel ended. The orb stays: the stages it holds then decay one at a time over the duration they leave
    /// (<c>base_skill_effect_duration</c> grown by <c>frost_fury_duration_+%_per_stage</c> per stage and by the player's
    /// duration stats), and the loop keeps firing all the while, without the while-channelling multiplier. The ending
    /// channel's generation and action both identify it: the client repeats the use while the button is held, and those
    /// repeats share the connection's channel generation (only a release or another action bumps it), so an older repeat
    /// ending must not release the orb the newest one has just taken.
    /// </summary>
    internal static void WinterOrbRelease(ClientState conn, int generation, int action)
    {
        lock (conn.Monsters)
        {
            var runtime = conn.OrbRuntime;
            if (runtime is null || !runtime.Channelling || runtime.ChannelGeneration != generation
                || runtime.Action != action) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            double duration = WinterOrbDuration(conn, runtime);
            runtime.Channelling = false;
            runtime.ExpiresAt = now + duration;
            runtime.DecaySeconds = duration / Math.Max(1, runtime.Stages);
            runtime.NextDecayAt = now + runtime.DecaySeconds;
            // The next volley comes at the released frequency, not the channelled one.
            double interval = WinterOrbFireSeconds(conn, runtime.SkillId, runtime.Gem.Effect, runtime.Stages,
                channelling: false);
            runtime.NextFireAt = Math.Min(runtime.NextFireAt, now + interval);
            // The icon now counts down, so the same count is presented again with the time left.
            runtime.PresentedStages = -1;
            Console.WriteLine($"[gateway] winter orb released at {runtime.Stages} stages: "
                + $"{duration:0.00}s over {runtime.DecaySeconds:0.00}s a stage");
        }
    }

    /// <summary>
    /// Death: the orb ends with the connection's combat work, but its stage icon is owned by the runtime, and
    /// <c>RetireOwnedCombat</c> drops that runtime without touching the client. The loop's own cleanup then finds the orb
    /// already detached and removes nothing, so the icon stayed — with the stages the player died with and the orb art
    /// the buff draws, but no orb and no loop behind it (live 2026-11-09). Queue the instance for removal before the
    /// runtime goes: the combat worker flushes it after the respawn, when the client will accept it.
    /// </summary>
    internal static void WinterOrbRetire(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var runtime = conn.OrbRuntime;
            if (runtime is null) return;
            if (runtime.PresentedBuff != 0) conn.RetiredCombatBuffs.Add((PlayerId, runtime.PresentedBuff));
            runtime.PresentedBuff = 0;
            runtime.PresentedStages = -1;
            runtime.Channelling = false;
            conn.OrbRuntime = null;
            Console.WriteLine($"[gateway] winter orb 0x{runtime.ObjectId:x} retired at {runtime.Stages} stages");
        }
    }

    /// <summary>
    /// The orb's own loop, one per orb: fire when due, decay after the release, and end when the last stage or the
    /// duration goes. It holds no channel reference, so moving, releasing or aiming elsewhere cannot end it early; death,
    /// an area change, a pause-menu reload or a connection close can.
    /// </summary>
    static async Task RunWinterOrb(ClientState conn, WinterOrbRuntime runtime, long generation)
    {
        var (remote, stream, ciphers) = conn.Wire;
        bool Current()
        {
            lock (conn.Monsters)
                return CurrentGameRules && !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
                    && ReferenceEquals(conn.OrbRuntime, runtime) && runtime.Generation == generation;
        }
        try
        {
            while (true)
            {
                double wait;
                bool fire = false, expire = false;
                lock (conn.Monsters)
                {
                    if (!Current()) return;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    if (conn.Paused) wait = .1;
                    else
                    {
                        if (!runtime.Channelling)
                        {
                            while (runtime.Stages > 0 && now >= runtime.NextDecayAt)
                            {
                                runtime.Stages--;
                                runtime.NextDecayAt += runtime.DecaySeconds;
                            }
                            expire = runtime.Stages <= 0 || now >= runtime.ExpiresAt;
                        }
                        if (expire) wait = 0;
                        else
                        {
                            fire = now >= runtime.NextFireAt;
                            wait = runtime.NextFireAt - now;
                            if (!runtime.Channelling) wait = Math.Min(wait, runtime.NextDecayAt - now);
                        }
                    }
                }
                if (expire) break;
                // The count follows the orb: each stage that decays re-sends the icon, as does the release's timer.
                await WinterOrbPresentStages(remote, stream, ciphers, conn);
                if (wait > 0)
                {
                    // A quarter of a second at most: a release, a retirement or an expiry is heard without waiting for a
                    // long interval to pass.
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, .25)));
                    continue;
                }
                if (!fire) continue;
                bool channelling;
                lock (conn.Monsters)
                {
                    if (!Current()) return;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    // Fixed cadence: a late volley must not stretch every later one.
                    double interval = WinterOrbFireSeconds(conn, runtime.SkillId, runtime.Gem.Effect, runtime.Stages,
                        runtime.Channelling);
                    do runtime.NextFireAt += interval; while (runtime.NextFireAt <= now);
                    channelling = runtime.Channelling;
                    if (!channelling)
                        Console.WriteLine($"[gateway] {remote} winter orb fires after the release at {runtime.Stages} stages");
                }
                await WinterOrbVolley(remote, stream, ciphers, conn, runtime);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            uint id = 0;
            ushort buff = 0;
            bool remove = false, resume = false;
            lock (conn.Monsters)
            {
                bool current = ReferenceEquals(conn.OrbRuntime, runtime) && runtime.Generation == generation;
                // A re-channel that lands between the loop's last expiry check and this cleanup sets the orb live again,
                // and a re-channel never starts a second loop: the orb must keep this one rather than be torn down.
                resume = current && runtime.Channelling && CurrentGameRules && !conn.Closed && !conn.LeavingArea
                    && !conn.RageRetired && conn.PlayerLife > 0;
                bool retire = !resume;
                if (current && retire)
                {
                    conn.OrbRuntime = null;
                    runtime.Channelling = false;
                }
                if (retire)
                {
                    // The icon belongs to this loop even when something else already detached the runtime (death retires
                    // the orb from the hit handler before PlayerDiedAtEpoch): remove it here rather than leave the stages
                    // the player died with on the client until the next orb replaces them.
                    id = runtime.ObjectId;
                    buff = runtime.PresentedBuff;
                    runtime.PresentedBuff = 0;
                    runtime.PresentedStages = -1;
                    remove = id != 0 && !conn.Closed && !conn.LeavingArea;
                }
            }
            if (resume)
            {
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(() => RunWinterOrb(conn, runtime, generation));
            }
            else
            {
                if (remove)
                    Console.WriteLine($"[gateway] {remote} winter orb 0x{id:x} expired");
                // The icon leaves with the orb: the last stage going, death, an area change or a closed connection.
                if (buff != 0 && !conn.Closed && !conn.LeavingArea)
                    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), null);
            }
        }
    }

    /// <summary>
    /// The seconds the stages left at the release are worth: the gem's <c>base_skill_effect_duration</c> with each stage's
    /// <c>frost_fury_duration_+%_per_stage</c> (30) as an increase, which is additive with the player's duration stats the
    /// way the game's duration system is, not a multiplication of the whole.
    /// </summary>
    static double WinterOrbDuration(ClientState conn, WinterOrbRuntime runtime)
    {
        var supported = SupportedSkill(conn, runtime.SkillId);
        int Own(string name) => Stats.Key(name) is uint key ? supported.Stats.GetValueOrDefault(key) : 0;
        var player = CharacterPlayerStats(conn);
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int perStage = Math.Max(0, Own("frost_fury_duration_+%_per_stage"));
        int stages = Math.Max(0, runtime.Stages);
        return Math.Max(.1, SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player,
            name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? perStage * stages : 0)));
    }

    /// <summary>An impact's art at its landing cell, removed once FrostFuryExplode has played.</summary>
    internal static async Task WinterOrbImpact(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) at)
    {
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        try
        {
            await Send(remote, stream, ciphers,
                InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(at.X, at.Y, WinterOrbImpactArt).Blob()), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return; }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WinterOrbImpactMilliseconds);
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        });
    }

    /// <summary>
    /// One volley of the orb: each projectile is the row's own mortar
    /// (<c>Metadata/Projectiles/FrostFuryMortar</c>), sent from the player's cell and glided to a landing cell around
    /// the nearest enemy, where it explodes for the skill's area hit. The volley spreads along a line across the aim,
    /// one <c>additional_projectiles_fired_with_distance_offset</c> (10 cells) per extra projectile, as the wiki
    /// describes multiple projectiles landing in a rough line rather than a cluster; concentrating them on one enemy
    /// means a volley on a single target lands every projectile on it instead of scattering over three different enemies.
    /// The hit lands when the projectile does, not when the button does, so it carries neither the channel's action nor
    /// its generation: the orb keeps firing after the release, when the player has no such action any more.
    /// </summary>
    internal static async Task WinterOrbVolley(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, WinterOrbRuntime runtime)
    {
        ushort skillId = runtime.SkillId;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null || gem.Effect != runtime.Gem.Effect) return;
        int Stat(string name) => WinterOrbStat(conn, skillId, name);
        (int X, int Y) from;
        List<(uint Id, (int X, int Y) At)> targets;
        lock (conn.Monsters)
        {
            double now = ProjectileWorldTime(conn);
            from = conn.Walk.Position(now);
            targets = [.. conn.Monsters
                .Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null)
                .Select(e => (e.Key, At: e.Value.Walk.Position(now)))
                .Select(e => (e.Key, e.At, Distance: Dist(e.At, from)))
                .Where(e => e.Distance <= SystemSettings.Default.ProjectileMaximumRangeCells)
                .OrderBy(e => e.Distance)
                .Select(e => (e.Key, e.At))];
            if (targets.Count == 0) return;
            if (runtime.Stages <= 0) return;
        }
        var anchor = targets[0].At;
        int count = Math.Clamp(Stat("base_number_of_projectiles") is > 0 and var baseCount ? baseCount : 1, 1, 16);
        // The offset stat is in 1/100 m; cells are 1/10 m (GroundSlamWave.CellsPerSecond).
        int spacing = Math.Max(0, Stat("additional_projectiles_fired_with_distance_offset") / 10);
        double dx = anchor.X - from.X, dy = anchor.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        // A line across the aim; a target on the caster's own cell has no direction, so the line runs east-west.
        double acrossX = length < 1e-6 ? 0 : -dy / length, acrossY = length < 1e-6 ? 1 : dx / length;
        // One stat sheet for the whole volley: its shots leave together, so its speed cannot change between them.
        float cellsPerSecond = WinterOrbProjectileSpeed(conn, runtime);
        for (int i = 0; i < count; i++)
        {
            double offset = spacing * (i - (count - 1) / 2.0);
            var landing = ((int)Math.Round(anchor.X + acrossX * offset), (int)Math.Round(anchor.Y + acrossY * offset));
            double seconds = WinterOrbFlightSeconds(cellsPerSecond, from, landing);
            uint projectile = await WinterOrbProjectile(remote, stream, ciphers, conn, from, landing, seconds);
            if (projectile == 0) continue;
            _ = LandWinterOrbProjectile(remote, stream, ciphers, conn, runtime, projectile, from, landing, seconds);
        }
    }

    /// <summary>The row's own mortar: its Projectiles.dat AOFiles are <c>FrostFury/projectile.ao</c>, so the client
    /// animates the shot itself once the object names this metadata.</summary>
    const string WinterOrbProjectileArt = "Metadata/Projectiles/FrostFuryMortar";

    /// <summary>
    /// The row's own speed through <see cref="ProjectileSpeed"/>, so projectile speed modifiers move the landing.
    /// Resolved once per volley: the player's stat sheet and the skill's row are the same for every mortar in it.
    /// </summary>
    static float WinterOrbProjectileSpeed(ClientState conn, WinterOrbRuntime runtime)
    {
        var own = SupportedSkill(conn, runtime.SkillId).Stats;
        var player = CharacterPlayerStats(conn);
        int Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int units = Skills.ProjectileFor(runtime.Gem.Effect)?.Speed ?? 1500;
        return ProjectileSpeed.CellsPerSecond(units, Player, Own,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
    }

    /// <summary>
    /// The time a mortar spends in the air over the distance it has to cover. The floor keeps a shot on the player's own
    /// cell visible and the cap keeps a far one from hanging.
    /// </summary>
    static double WinterOrbFlightSeconds(float cellsPerSecond, (int X, int Y) from, (int X, int Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double cells = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp(cells / Math.Max(20, cellsPerSecond), .05, 1.5);
    }

    /// <summary>
    /// One mortar in flight: a projectile object created at the launch cell and glided to its landing cell over
    /// <paramref name="seconds"/> (0x1ba carries a duration, not a speed). The projectile blob wakes the object, so it
    /// needs no 0x1a9 of its own.
    /// </summary>
    static async Task<uint> WinterOrbProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) from, (int X, int Y) to, double seconds)
    {
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        try
        {
            await Send(remote, stream, ciphers,
                CreateProjectileObject(id, new ProjectileObject(WinterOrbProjectileArt, from.X, from.Y)),
                $"0x02bb winter orb projectile 0x{id:x} ({from.X},{from.Y})->({to.X},{to.Y}) {seconds:0.000}s");
            await Send(remote, stream, ciphers,
                ProjectileHeight.Terrain.MoveTo(id, to.X, to.Y, (float)seconds), null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return 0; }
        return id;
    }

    /// <summary>
    /// The mortar lands: its object goes, FrostFuryExplode plays and the skill's area hit lands at that cell. Death, an
    /// area change and a closed connection drop a shot that is still in the air.
    /// </summary>
    static async Task LandWinterOrbProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, WinterOrbRuntime runtime, uint projectile, (int X, int Y) from, (int X, int Y) at,
        double seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            lock (conn.Monsters)
                if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(projectile), null);
            await WinterOrbImpact(remote, stream, ciphers, conn, at);
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == runtime.SkillId);
            if (gem.Name is null || gem.Effect != runtime.Gem.Effect) return;
            await AreaSkillHit(remote, stream, ciphers, conn, runtime.SkillId, gem, from, at,
                immediate: true, sequence: conn.ChannelSkillSequence);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
