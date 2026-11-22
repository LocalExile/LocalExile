using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The essence spells the hit kit cannot represent, cast on their own timer while the released rare lives, independent of
/// its AI. Their daemon effects carry no hit damage, summon or projectile, so <see cref="EssenceKit"/> drops them; a non-hit
/// kit special also fights the auto-attack and stalls the rare. This file holds the shared machinery — the periodic caster,
/// the pulse daemon, the spell effect objects and the player debuff plumbing. Each essence's own behaviour lives apart so a
/// change to one cannot disturb the others:
/// <list type="bullet">
/// <item>the resistance novas (Hatred cold / Anger fire / Wrath lightning) and their debuff — <c>EssenceNova.cs</c>;</item>
/// <item>Hatred's Creeping Frost chilling trail — <c>EssenceHatredTrail.cs</c>;</item>
/// <item>Misery's chaos pulse — <c>EssenceMisery.cs</c>;</item>
/// <item>Envy's grasping-hand grab — <c>EssenceGrab.cs</c>.</item>
/// </list>
/// Cooldowns live on the instance area, so several players' ticks cast a spell once, not once each.
/// </summary>
public static partial class MapEssences
{
    static double Now => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>One special spell a released rare casts: its nova visual effect and its RepeatedPulse trigger effect (the
    /// pulsation wind-up that debuffs / deals chaos degen), what it does (cold/fire/lightning resistance pulse, chaos degen
    /// pulse, or grab), the daemon variety whose action plays it, the resistance value / chaos dps / duration from the
    /// skill's stats at the rare's level, and its repeat cooldown.</summary>
    sealed record SpecialSpell(int Effect, int Pulse, string Kind, string Daemon, int Value, double Dps, double Duration, double Cooldown, int Tier)
    {
        /// <summary>The skill's own cast window in seconds (GrantedEffects.CastTime): the repeated pulse pulses across it.</summary>
        public double Cast { get; init; }
    }

    /// <summary>The RepeatedPulse action's radius in cells. ActionType 526 carries no radius column and no essence pulse
    /// skill holds a radius stat, so the client's own action code owns the number; 12 is calibrated from the retail
    /// observation that the replica's reach (the monster's aggro range, 60+) was about five times too wide. It replaces
    /// both the shrine circle's 26 the visual rode and the aggro range the debuff used.</summary>
    const int PulseRadius = 12;

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
        "EssenceDaemonFireRuneTrapCluster" => "rune",
        "EssenceFrostBomb" => "frostbomb",
        "EssenceChaosstorm" => "chaosstorm",
        "EssenceDaemonMeteorFirestorm" => "meteorfirestorm",
        "EssenceDaemonSummonOrbOfStormsDaemon" => "torment",
        "EssenceLightningBeacon" => "beacon",
        "EssenceDaemonVaalFlameblast" => "flameblast",
        "EssenceFireballBurst" => "fireball",
        "EssenceDaemonStormCascadeDaemon" => "stormcascade",
        "EssenceChaosProjectileWithGroundEffects" => "deliriumchaos",
        "GTFaridunEssenceDaemonBoulder" => "boulder",
        _ => "",
    };

    /// <summary>The special spells of a rare's essences: every daemon effect handled by the periodic caster (pulses,
    /// grab, runes, frost bomb, storms, ball lightning, storm call, flameblast, fireball burst, storm cascade, caustic
    /// chaos projectile, desolation boulder). The remaining damage/summon effects stay in <see cref="EssenceKit"/>.</summary>
    static IReadOnlyList<SpecialSpell> SpecialSpellsOf(ClientState conn, IReadOnlyList<int> essences, int level)
    {
        var skills = SkillsFor(conn);
        var list = new List<SpecialSpell>();
        var seen = new HashSet<int>();
        foreach (int row in essences)
        {
            int tier = MapEssenceCatalog.Find(row)?.Tier ?? 7;
            foreach (var daemon in DaemonEffects(row))
            foreach (int pulse in daemon.Effects)
            {
                string kind = EssenceSpellKind(skills.EffectId(pulse));
                if (kind.Length == 0 || !seen.Add(pulse)) continue;
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
                // The cooldown is the skill's own; zero-cooldown or windup-heavy specials keep a sensible repeat cadence.
                double cooldown = Math.Max(Math.Max(0, skills.CooldownMs(pulse, effectLevel) / 1000.0), skills.CastMs(pulse) / 1000.0);
                cooldown = kind switch
                {
                    "beacon" or "flameblast" => Math.Max(cooldown, 4.0),
                    "stormcascade" => Math.Max(cooldown, 5.0),
                    "deliriumchaos" => Math.Max(cooldown, 2.5),
                    _ => cooldown,
                };
                // Only resistance pulses pair with a separate projectile nova effect on the same daemon.
                int visual = kind is "cold" or "fire" or "lightning"
                    ? daemon.Effects.FirstOrDefault(e => e != pulse && IsProjectileNova(skills, e), pulse)
                    : pulse;
                list.Add(new(visual, pulse, kind, daemon.Daemon, value, Get("base_chaos_damage_to_deal_per_minute") / 60.0,
                    Get("base_skill_effect_duration") / 1000.0, cooldown, tier) { Cast = skills.CastMs(pulse) / 1000.0 });
            }
        }
        return list;
    }

    /// <summary>The periodic special spell kinds resolved for <paramref name="essences"/> at <paramref name="level"/>.</summary>
    public static IReadOnlyList<string> SpecialSpellKinds(ClientState conn, IReadOnlyList<int> essences, int level) =>
        [.. SpecialSpellsOf(conn, essences, level).Select(s => s.Kind)];

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
                    if (now < state.Ready.GetValueOrDefault(spell.Pulse)) continue;
                    state.Ready[spell.Pulse] = now + spell.Cooldown;
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
            if (spell.Kind == "rune")
            {
                await CastRunes(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "frostbomb")
            {
                await CastFrostBomb(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "chaosstorm")
            {
                await CastChaosstorm(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "meteorfirestorm")
            {
                await CastMeteorFirestorm(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "torment")
            {
                await CastTormentBallLightning(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "beacon")
            {
                await CastLightningBeacons(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "flameblast")
            {
                await CastFlameblast(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "fireball")
            {
                await CastFireballBurst(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "stormcascade")
            {
                await CastStormCascade(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "deliriumchaos")
            {
                await CastDeliriumChaosProjectile(conn, monster, target, spell);
                return;
            }
            if (spell.Kind == "boulder")
            {
                await CastDesolationBoulder(conn, monster, target, spell);
                return;
            }
            // The cast originates from the rare's live position, not the spot captured when the tick queued it: the rare
            // keeps fighting between the queue and the cast, and a stale origin reads as the cast coming from somewhere the
            // monster no longer is (a client/server position desync).
            lock (conn.Monsters)
            {
                at = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                // The rare stops to cast: hold its AI for the wind-up and the release so the pulsation and the nova stay
                // where it stands; it resumes fighting once the projectiles are out, not for the whole daemon linger.
                monster.BusyUntil = Math.Max(monster.BusyUntil, conn.Clock.Elapsed.TotalSeconds + 2.3);
            }
            uint daemon = await ShowPulseDaemon(conn, spell, at, monster.ObjectId);
            if (spell.Kind == "chaos")
            {
                // Misery's pulse triggers its 5 s proximity shield (GE 2842, cast_when_triggered_by_other_skill_% = 100).
                ActivateProximityShield(conn, monster, 5.0);
                // Misery is the aura itself pulsing, with no wind-up in front of it: the first stack lands with the pulse
                // the daemon has just played, not half a second after it. It then keeps pulsing across its own cast window
                // every half debuff-duration (2.5 s / 2 = 1.25 s, inside the 1-2 s cadence seen on retail), so the stacks
                // overlap; one pulse per 8 s cooldown read as a far slower aura than the client's.
                double gap = Math.Max(0.5, spell.Duration / 2);
                int pulses = Math.Max(1, (int)Math.Round(spell.Cast / gap));
                for (int i = 0; i < pulses && !conn.Closed && !conn.LeavingArea; i++)
                {
                    ChaosDegen(conn, at, PulseRadius, spell);
                    if (i + 1 < pulses)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(gap));
                        await PlayPulse(conn, daemon, at, spell);
                    }
                }
                RemoveDaemon(conn, daemon);
                Console.WriteLine($"[map-content] essence chaos aura by 0x{monster.ObjectId:x} at ({at.X}, {at.Y}): {pulses} pulses every {gap:0.##}s");
                return;
            }
            if (spell.Kind is "cold" or "fire" or "lightning")
            {
                // The pulsation wind-up: accelerating pulses (1.25 / 0.5 / 0.25 s), then the nova bursts its projectiles.
                await Task.Delay(TimeSpan.FromSeconds(1.25));
                await PlayPulse(conn, daemon, at, spell);
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                await PlayPulse(conn, daemon, at, spell);
                await Task.Delay(TimeSpan.FromSeconds(0.25));
                FireNova(conn, monster, spell);
            }
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            if (!conn.Closed && !conn.LeavingArea) await ResDebuff(conn, at, PulseRadius, spell);
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

    /// <summary>The pulsation wind-up: the essence's daemon at the rare plays a pulse nova action, so the native client
    /// selects, scales and animates it (the shrine nova pattern — a standalone pulse effect object draws nothing). The
    /// RepeatedPulse action is element-agnostic (one skill id for cold/fire/lightning, so it always drew the frost pulse);
    /// Wrath therefore plays the shrine's ShockNova, a lightning nova, on the same Daemon rig. The pulse is self-centred and
    /// rides the shrine circle (26); the nova's projectiles are fired separately (<c>EssenceNova.cs</c>) after the wind-up.
    /// The daemon is friendly, invulnerable and out of the AI, removed when the pulse ends. The grab uses effect objects
    /// instead (its demon_grasp art is a ground trap, not a caster nova).</summary>
    static int PulseVisualEffect(ClientState conn, SpecialSpell spell) => spell.Pulse;

    static async Task<uint> ShowPulseDaemon(ClientState conn, SpecialSpell spell, (int X, int Y) at, uint summoner)
    {
        var catalog = SkillsFor(conn);
        // The RepeatedPulse action is element-agnostic: it always draws the frost pulse. Only Hatred (cold) wants that, so
        // the other elements do not create the daemon or play its action at all — their own pulse art is the whole visual.
        // Playing the action under an overlay left an ice pulsation in front of Misery's chaos aura and Anger's fire one.
        if (spell.Kind != "cold")
        {
            uint pulse = SpawnEffect(conn, at, PulseArt(spell.Kind));
            _ = RemovePulseLater(conn, pulse, 2.0);
            Console.WriteLine($"[map-content] essence {spell.Kind} pulse art at ({at.X}, {at.Y})");
            return 0;
        }
        int visual = PulseVisualEffect(conn, spell);
        if (Monsters.Find(spell.Daemon) is not { Supported: true } variety || catalog.ActionHash(visual) is not ushort hash) return 0;
        ushort skillId = (ushort)(hash & 0x3fff);
        var stats = catalog.EffectStats(visual, catalog.MaxLevelAt(visual, (byte)AreaLevel(conn))).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The pulse is self-centred with no radius stat of its own; it uses the action's own reach (PulseRadius), not the
        // shrine circle's 26 it used to ride. The nova's projectiles are fired separately (FireNova).
        int radius = Math.Max(1, SkillArea.Radius(PulseRadius, Get, true));
        var action = new MonsterAttack(skillId, Math.Max(0.1, catalog.CastMs(visual) / 1000.0), radius, DamagePacket.Empty) { Spell = true };
        foreach (var (key, value) in action.InPlaceSkillStats()) stats[key] = value;
        void Set(string name, int value) { if (Stats.Key(name) is uint key) stats[key] = value; }
        // The client reads the final area radius explicitly (the shrine nova sets it).
        Set("active_skill_area_of_effect_radius", radius);
        Set("total_skill_area_of_effect_radius_+%_including_final", SkillArea.RadiusPercent(Get, true));
        if (catalog.FindByRow(skillId, visual, 1, [.. stats.Select(p => (p.Key, p.Value))]) is not { } skill) return 0;
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

    const string FirePulseArt = "Metadata/Effects/Spells/pulses/firepulse.ao";
    const string ChaosPulseArt = "Metadata/Effects/Spells/pulses/chaospulse.ao";
    const string LightningPulseArt = "Metadata/Effects/Spells/pulses/lightningpulse.ao";

    static string PulseArt(string kind) => kind switch
    {
        "fire" => FirePulseArt,
        "lightning" => LightningPulseArt,
        _ => ChaosPulseArt,
    };

    static async Task RemovePulseLater(ClientState conn, uint id, double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        RemoveEffect(conn, id);
    }

    /// <summary>One pulsation of the wind-up: re-plays the daemon's pulse nova action in place.</summary>
    static async Task PlayPulse(ClientState conn, uint daemonId, (int X, int Y) at, SpecialSpell spell)
    {
        if (daemonId == 0 || conn.Closed || conn.LeavingArea) return;
        if (SkillsFor(conn).ActionHash(PulseVisualEffect(conn, spell)) is not ushort hash) return;
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

    sealed class EssenceEffects
    {
        public readonly List<(Dictionary<uint, int> Stats, double Until, ushort Buff, uint Key)> Debuffs = [];
        /// <summary>Misery's «Essence of Misery» stacks: each a chaos degeneration with its own expiry. The buff (744)
        /// shows the live count; the degeneration share rides <c>TrapDegenPerSecond</c>.</summary>
        public readonly List<(double Dps, double Until)> MiseryStacks = [];
        public ushort MiseryBuff;
        public int MiserySentCount;
        public double MiseryShare;
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
        }
        TickMisery(conn, effects);
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
            lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.MiseryShare);
            lock (effects)
            {
                effects.Debuffs.Clear(); effects.MiseryStacks.Clear();
                effects.MiseryShare = 0; effects.MiseryBuff = 0; effects.MiserySentCount = 0;
            }
        }
        Effects.Remove(conn);
        GroundExposures.Remove(conn);
    }

    static async Task RefreshStats(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
}