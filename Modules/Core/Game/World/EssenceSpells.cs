using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The essence spells the hit kit cannot represent: the resistance pulses (Hatred/Anger/Wrath), the chaos damage-over-time
/// pulse (Misery) and the grasping-hand grab (Envy). Their daemon effects carry no hit damage, summon or projectile, so
/// <see cref="EssenceKit"/> drops them; a non-hit kit special also fights the auto-attack and stalls the rare, so they are
/// cast here on their own timer while the released rare lives, independent of its AI. The visual is a spell effect object
/// (the client's own art for the skill), not a monster action — the rare's rig has no pulse/grab clip:
/// <list type="bullet">
/// <item>RepeatedPulseCold/Fire/Lightning: a pulse nova (<c>Metadata/Effects/Spells/pulses/&lt;element&gt;pulse.ao</c>) at the
/// rare, then <c>base_&lt;type&gt;_damage_resistance_%</c> (-10) on the players near it for the pulse's duration (the
/// invisible essence elemental-damage-taken debuff, BuffDefinitions 746).</item>
/// <item>RepeatedPulseDegen: a chaos pulse nova (<c>pulses/chaospulse.ao</c>) at the rare, then
/// <c>base_chaos_damage_to_deal_per_minute</c> as a chaos degeneration on the players near it, after chaos resistance,
/// through <c>TrapDegenPerSecond</c> (the essence daemon degen aura, buff 100).</item>
/// <item>EssenceDaemonGrab: the demon-grasp circle and hand (<c>monsters_effects/essence/demon_grasp/</c>) appear where the
/// target stood; after the windup the grasp end plays and a player still inside the circle is pulled to the rare — stepping
/// out evades it (the wiki).</item>
/// </list>
/// Cooldowns live on the instance area, so several players' ticks cast a spell once, not once each.
/// </summary>
public static partial class MapEssences
{
    static double Now => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    const string GrabCircleArt = "Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp_circle.ao";
    const string GrabHandArt = "Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp.ao";
    const string GrabEndArt = "Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp_end.ao";

    /// <summary>One special spell a released rare casts: its nova visual effect and its RepeatedPulse trigger effect (the
    /// pulsation wind-up that debuffs / deals chaos degen), what it does (cold/fire/lightning resistance pulse, chaos degen
    /// pulse, or grab), the daemon variety whose action plays it, the resistance value / chaos dps / duration from the
    /// skill's stats at the rare's level, and its repeat cooldown.</summary>
    sealed record SpecialSpell(int Effect, int Pulse, string Kind, string Daemon, int Value, double Dps, double Duration, double Cooldown, int Tier);

    sealed class CastState
    {
        public IReadOnlyList<SpecialSpell> Spells = [];
        public readonly Dictionary<int, double> Ready = [];
    }

    static string EssenceSpellKind(string? effectId) => effectId switch
    {
        "RepeatedPulseCold" => "cold",
        "RepeatedPulseFire" => "fire",
        "RepeatedPulseLightning" => "lightning",
        "RepeatedPulseDegen" => "chaos",
        "EssenceDaemonGrab" => "grab",
        _ => "",
    };

    /// <summary>The special spells of a rare's essences: the daemon's pulse effect (the trigger that debuffs / deals chaos
    /// degen) paired with the visual to play. The resistance pulses (Hatred/Anger/Wrath) trigger a projectile nova — the
    /// daemon's Creeping Frost / flame breath / mortar effect (<c>projectiles_nova</c>, additional projectiles) — so the
    /// client fires that nova of projectiles; the chaos pulse (Misery) is its own visual. The damage/summon/projectile
    /// effects stay in the kit (<see cref="EssenceKit"/>); only these are cast here.</summary>
    static IReadOnlyList<SpecialSpell> SpecialSpellsOf(ClientState conn, IReadOnlyList<int> essences, int level)
    {
        var skills = SkillsFor(conn);
        var list = new List<SpecialSpell>();
        var seen = new HashSet<int>();
        foreach (int row in essences)
        {
            if (DaemonEffects(row) is not { } daemon) continue;
            int pulse = daemon.Effects.FirstOrDefault(e => EssenceSpellKind(skills.EffectId(e)).Length > 0, -1);
            if (pulse < 0 || !seen.Add(pulse)) continue;
            string kind = EssenceSpellKind(skills.EffectId(pulse));
            int effectLevel = skills.MaxLevelAt(pulse, level);
            var native = GolemData.Skill(skills, pulse, level);
            int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
            int value = kind switch
            {
                "cold" => Get("base_cold_damage_resistance_%"),
                "fire" => Get("base_fire_damage_resistance_%"),
                "lightning" => Get("base_lightning_damage_resistance_%"),
                _ => 0,
            };
            // The pulse's cast time is its repeat interval; the cooldown holds the next cast.
            double cooldown = Math.Max(Math.Max(0, skills.CooldownMs(pulse, effectLevel) / 1000.0), skills.CastMs(pulse) / 1000.0);
            // The visual: the daemon's projectile nova if it has one (the Creeping Frost circle), else the pulse itself.
            int visual = daemon.Effects.FirstOrDefault(e => e != pulse && IsProjectileNova(skills, e), pulse);
            int tier = MapEssenceCatalog.Find(row)?.Tier ?? 7;
            list.Add(new(visual, pulse, kind, daemon.Daemon, value, Get("base_chaos_damage_to_deal_per_minute") / 60.0,
                Get("base_skill_effect_duration") / 1000.0, cooldown, tier));
        }
        return list;
    }

    static bool IsProjectileNova(SkillCatalog skills, int effect)
    {
        var stats = skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        return Stats.Key("base_is_projectile") is uint key && stats.GetValueOrDefault(key) != 0;
    }

    /// <summary>Called from the essence tick: each released rare casts its ready special spells while a player is in reach.
    /// The rare's own AI is untouched (it fights with its default attack and the hit-kit spells); these cast alongside it.</summary>
    static async Task CastSpecialSpells(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        double now = Now;
        foreach (var mono in area.Monoliths.Where(m => m.Stage >= 4 && m.Monster != 0).ToArray())
        {
            SpawnedMonster? monster;
            lock (conn.Monsters) conn.Monsters.TryGetValue(mono.Monster, out monster);
            if (monster is null || monster.Dead || monster.Life <= 0 || !monster.Hostile) continue;
            CastState state;
            lock (area.Casting)
                if (!area.Casting.TryGetValue(mono.Monster, out state!))
                    area.Casting[mono.Monster] = state = new CastState { Spells = SpecialSpellsOf(conn, mono.Essences, monster.Level) };
            if (state.Spells.Count == 0) continue;
            var at = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            double range = Math.Max(60, monster.Variety?.AggroRange ?? 60);
            var target = Members(conn).Where(c => c.PlayerSent && c.PlayerLife > 0 && !c.Immune
                && Dist(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) <= range)
                .OrderBy(c => Dist(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at)).FirstOrDefault();
            if (target is null) continue;
            lock (state)
                foreach (var spell in state.Spells)
                {
                    if (now < state.Ready.GetValueOrDefault(spell.Effect)) continue;
                    state.Ready[spell.Effect] = now + spell.Cooldown;
                    _ = CastSpell(conn, monster, target, at, spell);
                }
        }
    }

    static async Task CastSpell(ClientState conn, SpawnedMonster monster, ClientState target, (int X, int Y) at, SpecialSpell spell)
    {
        try
        {
            if (spell.Kind == "grab")
            {
                var trapAt = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
                uint circle = SpawnEffect(conn, trapAt, GrabCircleArt);
                uint hand = SpawnEffect(conn, trapAt, GrabHandArt);
                await Task.Delay(TimeSpan.FromSeconds(1.0));
                if (!conn.Closed && !conn.LeavingArea)
                {
                    uint end = SpawnEffect(conn, trapAt, GrabEndArt);
                    await Grab(conn, monster, target, trapAt, 10);
                    await Task.Delay(TimeSpan.FromSeconds(0.6));
                    RemoveEffect(conn, end);
                }
                RemoveEffect(conn, circle); RemoveEffect(conn, hand);
                Console.WriteLine($"[map-content] essence grab by 0x{monster.ObjectId:x} at ({trapAt.X}, {trapAt.Y})");
                return;
            }
            // The cast originates from the rare's live position, not the spot captured when the tick queued it: the rare
            // keeps fighting between the queue and the cast, and a stale origin reads as the cast coming from somewhere the
            // monster no longer is (a client/server position desync).
            lock (conn.Monsters)
            {
                at = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                // The rare stops to cast: hold its AI for the wind-up and the release (~1 s) so the pulsation and the nova
                // stay where it stands; it resumes fighting once the projectiles are out, not for the whole daemon linger.
                monster.BusyUntil = Math.Max(monster.BusyUntil, conn.Clock.Elapsed.TotalSeconds + 1.0);
            }
            uint daemon = await ShowPulseDaemon(conn, spell, at, monster.ObjectId);
            if (spell.Kind is "cold" or "fire" or "lightning")
            {
                // The pulsation wind-up: accelerating pulses (0.5 / 0.2 / 0.1 s), then the nova bursts its projectiles.
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                await PlayPulse(conn, daemon, at, spell);
                await Task.Delay(TimeSpan.FromSeconds(0.2));
                await PlayPulse(conn, daemon, at, spell);
                await Task.Delay(TimeSpan.FromSeconds(0.1));
                FireNova(conn, monster, spell);
            }
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            if (!conn.Closed && !conn.LeavingArea)
            {
                double range = Math.Max(60, monster.Variety?.AggroRange ?? 60);
                if (spell.Kind == "chaos") ChaosDegen(conn, at, range, spell);
                else await ResDebuff(conn, at, range, spell);
            }
            await Task.Delay(TimeSpan.FromSeconds(0.4));
            RemoveDaemon(conn, daemon);
            Console.WriteLine($"[map-content] essence {spell.Kind} pulse by 0x{monster.ObjectId:x} at ({at.X}, {at.Y})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[map-content] essence spell {spell.Kind} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A spell effect object (the client's art for the skill), broadcast to the instance and played at once; the
    /// strongbox ground-effect pattern. Returns its object id, removed when the spell ends.</summary>
    static uint SpawnEffect(ClientState conn, (int X, int Y) at, string art, float scale = 1)
    {
        uint id = conn.TakeObjectId();
        lock (conn.Monsters)
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, art) { Scale = scale }.Blob()), null, WorldId: id, CreatesObject: true));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.AnimationUpdate(id, 0, 0, 1), null, WorldId: id));
        }
        return id;
    }

    static void RemoveEffect(ClientState conn, uint id)
    {
        if (id == 0) return;
        lock (conn.Monsters)
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), null, WorldId: id, RemovesObject: true));
    }

    /// <summary>The pulsation wind-up: the essence's daemon at the rare plays its RepeatedPulse action, so the native client
    /// selects, scales and animates the pulse (the shrine nova pattern — a standalone pulse effect object draws nothing).
    /// The pulse is self-centred and rides the shrine circle (26); the nova's projectiles are fired separately
    /// (<see cref="FireNova"/>) after the wind-up. The daemon is friendly, invulnerable and out of the AI, removed when the
    /// pulse ends. The grab uses effect objects instead (its demon_grasp art is a ground trap, not a caster nova).</summary>
    static async Task<uint> ShowPulseDaemon(ClientState conn, SpecialSpell spell, (int X, int Y) at, uint summoner)
    {
        var catalog = SkillsFor(conn);
        if (Monsters.Find(spell.Daemon) is not { Supported: true } variety || catalog.ActionHash(spell.Pulse) is not ushort hash) return 0;
        ushort skillId = (ushort)(hash & 0x3fff);
        var stats = catalog.EffectStats(spell.Pulse, 1).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The daemon plays the RepeatedPulse wind-up — the pulsation before the nova — a self-centred pulse with no radius
        // of its own, riding the shrine circle (26). The nova's projectiles are fired separately (FireNova); the pulse
        // effect carries no area radius, so reading the nova's (absent) radius drew a radius-1 circle nothing could see.
        int radius = Math.Max(1, SkillArea.Radius(26, Get, true));
        var action = new MonsterAttack(skillId, Math.Max(0.1, catalog.CastMs(spell.Pulse) / 1000.0), radius, DamagePacket.Empty) { Spell = true };
        foreach (var (key, value) in action.InPlaceSkillStats()) stats[key] = value;
        void Set(string name, int value) { if (Stats.Key(name) is uint key) stats[key] = value; }
        // The client reads the final area radius explicitly (the shrine nova sets it).
        Set("active_skill_area_of_effect_radius", radius);
        Set("total_skill_area_of_effect_radius_+%_including_final", SkillArea.RadiusPercent(Get, true));
        if (catalog.FindByRow(skillId, spell.Pulse, 1, [.. stats.Select(p => (p.Key, p.Value))]) is not { } skill) return 0;
        var (remote, stream, ciphers) = conn.Wire;
        // PositionedBlocking false: the daemon's body must not block the rare's own walk (a monster AI walk is stopped by
        // living bodies, unlike the client-driven player walk); it still renders and plays the nova.
        var caster = variety with { PositionedBlocking = false };
        uint id = await SpawnMonster(remote, stream, ciphers, conn, caster, (byte)AreaLevel(conn), MonsterRarity.Normal, at.X, at.Y,
            friendly: true, log: false, alone: false, scripted: true, ownedSkills: [skill], placement: MonsterPlacement.Established,
            owned: (_, m) =>
            {
                m.NoRewards = true; m.Attack = null; m.SummonerId = summoner;
                m.EncounterStats = new Dictionary<string, int> { ["base_cannot_be_damaged"] = 1 };
            });
        if (id == 0) return 0;
        await PlayPulse(conn, id, at, spell);
        Console.WriteLine($"[map-content] essence pulse daemon {spell.Daemon} {spell.Kind}, radius {radius}");
        return id;
    }

    /// <summary>One pulsation of the wind-up: re-plays the daemon's RepeatedPulse action in place.</summary>
    static async Task PlayPulse(ClientState conn, uint daemonId, (int X, int Y) at, SpecialSpell spell)
    {
        if (daemonId == 0 || conn.Closed || conn.LeavingArea) return;
        if (SkillsFor(conn).ActionHash(spell.Pulse) is not ushort hash) return;
        ushort skillId = (ushort)(hash & 0x3fff);
        var (remote, stream, ciphers) = conn.Wire;
        await SendWorldRecord(remote, stream, ciphers, conn, daemonId,
            [.. InstanceProtocol.ActionStarted(daemonId, at.X, at.Y, at.X, at.Y, skillId, 0xffff, conn.MonsterOrderFlags), .. InstanceProtocol.ActionEnded(daemonId)],
            null);
    }

    static void RemoveDaemon(ClientState conn, uint id)
    {
        if (id == 0) return;
        lock (conn.Monsters)
            if (conn.Monsters.Remove(id, out var monster))
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                conn.MonsterViews.Remove(id); conn.AsleepMonsters.Remove(id);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "essence pulse daemon ends", id, monster,
                    PlayerMinions.OutputKind.Remove, WorldId: id, RemovesObject: true));
            }
    }

    static IEnumerable<ClientState> PlayersNear(ClientState conn, (int X, int Y) at, double radius) => Members(conn)
        .Where(c => c.PlayerSent && c.PlayerLife > 0 && Dist(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) <= radius);

    /// <summary>The resistance pulse's nova: a ring of the essence's projectiles fired through the monster projectile
    /// runtime (the Rampage nova pattern), so the client draws them and the server simulates their hits on the player. A
    /// daemon action alone drew no projectiles — the runtime creates the flying objects. The shot is the rare's own (it
    /// originates from the rare's live position, not the pulse daemon's): Hatred fires Creeping Frost (ArcticBreath) and
    /// leaves a chilling trail, Anger fires the fire Creeping Frost (FlameBreath) with its fire damage, and Wrath
    /// is a lobbed molten mortar (its MonsterProjectile row) the runtime lands in a ring around the rare.</summary>
    static void FireNova(ClientState conn, SpawnedMonster monster, SpecialSpell spell)
    {
        if (monster.Dead || monster.Life <= 0 || monster.Variety is null) return;
        var catalog = SkillsFor(conn);
        var stats = catalog.EffectStats(spell.Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int count = ScaledSpellCount(spell.Effect, spell.Tier, Get("number_of_additional_projectiles") + 1);
        SkillCatalog.MonsterProjectileInfo projectile;
        if (catalog.MonsterProjectile(spell.Effect, includeWeaponAttacks: true) is { } mortar)
            // Wrath: a lightning mortar (MortarLightning, the lightning molten-projectile rig), lobbed. A full-turn spread
            // lands each shell its landing distance out in its own direction — a ring of explosions around the rare. The
            // row's speed 750 crossed the short landing in ~0.14 s (too fast to see) and the molten art read as fire, so the
            // shell is slowed (~1.2 s arc) and its minimum range widened (~2x the ring) for a visible lightning volley.
            projectile = mortar with { Metadata = "Metadata/Projectiles/MortarLightning", Count = count,
                SpreadRadians = Math.PI * 2, Speed = 190, MinimumRange = 250 };
        else
        {
            // Hatred's Creeping Frost (ArcticBreath), or Anger's fire Creeping Frost (FlameBreath — the fire arctic breath
            // its EssenceTriggeredFlameBreath effect names).
            string projId = spell.Kind == "fire" ? "Metadata/Projectiles/FlameBreath" : "Metadata/Projectiles/ArcticBreath";
            if (catalog.Projectile(projId) is not { } meta) return;
            projectile = new SkillCatalog.MonsterProjectileInfo(meta.Id, meta.Speed, false, 1)
                { Count = count, SpreadRadians = Math.PI * 2 };
        }
        var attack = BossKits.AttackFor(conn, monster.Variety, (byte)AreaLevel(conn), spell.Effect, 0x4000);
        var session = ProjectilesFor(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
        var use = MonsterProjectileUse(session, monster.ObjectId, monster, attack, projectile, monster.ActionGeneration);
        session.Schedule(ProjectileRuntime.Time(ProjectileWorldTime(conn)), () => !monster.Dead && monster.Life > 0 && !conn.Closed, () => use);
        if (spell.Kind == "cold") FrostTrail(conn, monster, count);
        Console.WriteLine($"[map-content] essence {spell.Kind} nova: {count} {projectile.Metadata}{(projectile.Lobbed ? " (lobbed)" : "")} from rare 0x{monster.ObjectId:x}");
    }

    /// <summary>Hatred's Creeping Frost trail: a line of small chilling-ground ice crystals along each nova projectile's
    /// radial path (the flame-dash trail pattern), lasting the skill's chilling duration. A player walking over them is
    /// chilled (the strongbox ice-nova ground pattern, <see cref="TickTrail"/>).</summary>
    const string TrailArt = "Metadata/Effects/Spells/ground_effects/ice/ice.ao";
    sealed record TrailZone((int X, int Y) At, int Radius, double Until, uint Art);

    static void FrostTrail(ClientState conn, SpawnedMonster monster, int count)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        var at = monster.Walk.Position(now);
        var aim = conn.Walk.Position(now);
        double facing = Math.Atan2(aim.Y - at.Y, aim.X - at.X);
        double until = Now + 7.5; // base_skill_effect_duration of EssenceArcticBreathNova
        for (int i = 0; i < count; i++)
        {
            double angle = facing + Math.Tau * (i / (double)count - .5);
            // A trail of ice crystals along each projectile's bearing, reaching ~40% further than the first pass and a
            // touch smaller; spaced so neighbouring crystals meet into a near-continuous ice path.
            for (double d = 6; d <= 56; d += 10)
            {
                var spot = conn.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * d), at.Y + (int)Math.Round(Math.Sin(angle) * d)));
                uint id = conn.TakeObjectId();
                lock (conn.Monsters)
                {
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                        new EffectObject(spot.X, spot.Y, TrailArt) { Flags0 = EffectObject.FixedRotation, Scale = 1.05f }.Blob()),
                        null, WorldId: id, CreatesObject: true));
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
                }
                lock (area.Trail) area.Trail.Add(new(spot, 5, until, id));
            }
        }
    }

    /// <summary>Called from the essence tick: expires the trail's ice crystals (their art removed) and chills a player who
    /// stands on one (the strongbox chilling-ground pattern). The chill refreshes while they remain on the ice.</summary>
    static void TickTrail(ClientState conn)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        double now = Now;
        TrailZone[] zones;
        lock (area.Trail)
        {
            foreach (var gone in area.Trail.Where(z => z.Until <= now).ToArray())
            {
                area.Trail.Remove(gone);
                lock (conn.Monsters)
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(gone.Art), null, WorldId: gone.Art, RemovesObject: true));
            }
            zones = [.. area.Trail];
        }
        if (zones.Length == 0 || conn.PlayerLife <= 0 || conn.Paused || conn.Immune || InGracePeriod(conn)) return;
        double at = conn.Clock.Elapsed.TotalSeconds;
        var here = conn.Walk.Position(at);
        if (!zones.Any(z => Dist(here, z.At) <= z.Radius)) return;
        const int chill = 20;
        if (conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, at) >= chill) return;
        var stats = CharacterPlayerStats(conn);
        lock (conn.Monsters)
            ApplyPlayerAilmentsLocked(conn, at, a => a.Inflict(PlayerElementalAilments.Kind.Chill, chill, .3, at,
                name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, conn.IncomingHitRandom));
    }

    /// <summary>The resistance pulse: -10% of its element on the players near the rare for the pulse's duration. The same
    /// element's debuff refreshes rather than stacks; the buff (746) is invisible, so the debuff is the stat alone.</summary>
    static async Task ResDebuff(ClientState source, (int X, int Y) at, double range, SpecialSpell spell)
    {
        string statName = spell.Kind switch
        {
            "cold" => "base_cold_damage_resistance_%",
            "fire" => "base_fire_damage_resistance_%",
            _ => "base_lightning_damage_resistance_%",
        };
        if (Stats.Key(statName) is not uint key || spell.Value == 0) return;
        double seconds = Math.Max(1, spell.Duration);
        foreach (var conn in PlayersNear(source, at, range).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            var effects = Effects.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (effects)
            {
                effects.Debuffs.RemoveAll(d => d.Until <= Now || d.Key == key);
                effects.Debuffs.Add((new Dictionary<uint, int> { [key] = spell.Value }, Now + seconds, buff, key));
            }
            var (remote, stream, ciphers) = conn.Wire;
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 746,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"essence {spell.Kind} resistance pulse {spell.Value}%");
            await RefreshStats(conn);
        }
    }

    /// <summary>The chaos pulse: a chaos degeneration on the players near the rare for the pulse's duration, after their
    /// chaos resistance, dealt through <c>TrapDegenPerSecond</c> (the tick recomputes the share). Pulses stack.</summary>
    static void ChaosDegen(ClientState source, (int X, int Y) at, double range, SpecialSpell spell)
    {
        if (spell.Dps <= 0) return;
        double seconds = Math.Max(1, spell.Duration);
        foreach (var conn in PlayersNear(source, at, range).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            double dps = spell.Dps * Math.Max(0, 1 - Math.Min(MaxResistance(conn, DamageType.Chaos), Resistance(conn, DamageType.Chaos)) / 100.0);
            var effects = Effects.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (effects)
            {
                effects.Degens.RemoveAll(d => d.Until <= Now);
                effects.Degens.Add((dps, Now + seconds, buff));
            }
            var (remote, stream, ciphers) = conn.Wire;
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 100,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"essence chaos degen pulse {dps:0.#}/s");
        }
    }

    /// <summary>The grab: the demon-grasp circle appeared where the target stood at the cast; after its windup, a target
    /// still inside it is pulled to the rare, one who stepped out is not (the wiki). The teleport is the gate's own (0x01b7).</summary>
    static async Task Grab(ClientState conn, SpawnedMonster monster, ClientState target, (int X, int Y) trapAt, double trapRadius)
    {
        if (target.Immune || InGracePeriod(target)) return;
        double now = target.Clock.Elapsed.TotalSeconds;
        if (Dist(target.Walk.Position(now), trapAt) > trapRadius) return;
        var at = monster.Walk.Position(now);
        var (remote, stream, ciphers) = target.Wire;
        using (ProjectileMutation(target))
            target.Walk.Teleport(at.X, at.Y, ProjectileWorldTime(target));
        await SendWorldRecord(remote, stream, ciphers, target, PlayerId, InstanceProtocol.Teleport(PlayerId, at.X, at.Y),
            $"essence grab to ({at.X}, {at.Y})");
    }

    sealed class EssenceEffects
    {
        public readonly List<(Dictionary<uint, int> Stats, double Until, ushort Buff, uint Key)> Debuffs = [];
        public readonly List<(double Dps, double Until, ushort Buff)> Degens = [];
        public double DegenShare;
    }
    static readonly RuleTable<ClientState, EssenceEffects> Effects = new();

    /// <summary>GameEvents.PlayerStatSources: the resistance pulses on this player, while they last.</summary>
    public static Dictionary<uint, int> EssenceDebuffStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!Effects.TryGetValue(conn, out var effects) || effects.Debuffs.Count == 0) return stats;
        double now = Now;
        lock (effects)
            foreach (var (debuff, until, _, _) in effects.Debuffs)
                if (until > now)
                    foreach (var (key, value) in debuff)
                        stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>Called from the essence tick: expires the player's pulses (their buffs removed, resistances refreshed) and
    /// keeps the chaos degeneration share of <c>TrapDegenPerSecond</c> equal to the active pulses' sum.</summary>
    static void TickEffects(ClientState conn)
    {
        if (!Effects.TryGetValue(conn, out var effects)) return;
        double now = Now;
        var goneBuffs = new List<ushort>();
        bool debuffChanged = false;
        lock (effects)
        {
            foreach (var gone in effects.Debuffs.Where(d => d.Until <= now).ToArray())
            { effects.Debuffs.Remove(gone); goneBuffs.Add(gone.Buff); debuffChanged = true; }
            foreach (var gone in effects.Degens.Where(d => d.Until <= now).ToArray())
            { effects.Degens.Remove(gone); goneBuffs.Add(gone.Buff); }
            double dps = effects.Degens.Sum(d => d.Dps);
            if (dps != effects.DegenShare)
            {
                lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.DegenShare + dps);
                effects.DegenShare = dps;
            }
        }
        if (conn.Closed) return;
        var (remote, stream, ciphers) = conn.Wire;
        foreach (ushort buff in goneBuffs)
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        if (debuffChanged && !conn.LeavingArea) _ = RefreshStats(conn);
    }

    /// <summary>A leaving player's pulses and their degeneration share go with them.</summary>
    static void LeaveEffects(ClientState conn)
    {
        if (Effects.TryGetValue(conn, out var effects))
        {
            lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.DegenShare);
            lock (effects) { effects.Debuffs.Clear(); effects.Degens.Clear(); effects.DegenShare = 0; }
        }
        Effects.Remove(conn);
    }

    static async Task RefreshStats(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
}