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
        "EssenceDaemonFireRuneTrapCluster" => "rune",
        "EssenceFrostBomb" => "frostbomb",
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

    /// <summary>The pulsation wind-up: the essence's daemon at the rare plays a pulse nova action, so the native client
    /// selects, scales and animates it (the shrine nova pattern — a standalone pulse effect object draws nothing). The
    /// RepeatedPulse action is element-agnostic (one skill id for cold/fire/lightning, so it always drew the frost pulse);
    /// Wrath therefore plays the shrine's ShockNova, a lightning nova, on the same Daemon rig. The pulse is self-centred and
    /// rides the shrine circle (26); the nova's projectiles are fired separately (<c>EssenceNova.cs</c>) after the wind-up.
    /// The daemon is friendly, invulnerable and out of the AI, removed when the pulse ends. The grab uses effect objects
    /// instead (its demon_grasp art is a ground trap, not a caster nova).</summary>
    static int PulseVisualEffect(ClientState conn, SpecialSpell spell) =>
        spell.Kind == "lightning" ? SkillsFor(conn).EffectRow("ShrineShockNova") : spell.Pulse;

    static async Task<uint> ShowPulseDaemon(ClientState conn, SpecialSpell spell, (int X, int Y) at, uint summoner)
    {
        var catalog = SkillsFor(conn);
        int visual = PulseVisualEffect(conn, spell);
        if (Monsters.Find(spell.Daemon) is not { Supported: true } variety || catalog.ActionHash(visual) is not ushort hash) return 0;
        ushort skillId = (ushort)(hash & 0x3fff);
        var stats = catalog.EffectStats(visual, 1).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The pulse is self-centred with no radius of its own, riding the shrine circle (26). The nova's projectiles are
        // fired separately (FireNova); the pulse effect carries no area radius, so reading the nova's (absent) radius drew a
        // radius-1 circle nothing could see.
        int radius = Math.Max(1, SkillArea.Radius(26, Get, true));
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
        // Fire and chaos have no element nova daemon action (the RepeatedPulse action always draws the frost pulse), so
        // their own pulse art is spawned over it as an effect object for the wind-up's duration.
        if (spell.Kind is "fire" or "chaos")
        {
            uint pulse = SpawnEffect(conn, at, spell.Kind == "fire" ? FirePulseArt : ChaosPulseArt, 5f);
            _ = RemovePulseLater(conn, pulse, 2.0);
        }
        Console.WriteLine($"[map-content] essence pulse daemon {spell.Daemon} {spell.Kind}, radius {radius}");
        return id;
    }

    const string FirePulseArt = "Metadata/Effects/Spells/pulses/firepulse.ao";
    const string ChaosPulseArt = "Metadata/Effects/Spells/pulses/chaospulse.ao";

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
    }

    static async Task RefreshStats(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
}