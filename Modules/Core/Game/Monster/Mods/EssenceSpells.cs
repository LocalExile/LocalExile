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
/// the instant pulse effects, the spell effect objects and the player debuff plumbing. Each essence's own behaviour lives apart so a
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

    /// <summary>The released rares' special spells and each spell's next-ready time, per area instance: what the old area
    /// state kept, shared by every player's tick so a spell is cast once, not once each.</summary>
    static readonly Store<InstanceState, Dictionary<uint, CastState>> Casts = new();

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
    static IReadOnlyList<SpecialSpell> SpecialSpellsOf(Player conn, IReadOnlyList<int> essences, int level)
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
    public static IReadOnlyList<string> SpecialSpellKinds(Player conn, IReadOnlyList<int> essences, int level) =>
        [.. SpecialSpellsOf(conn, essences, level).Select(s => s.Kind)];

    static bool IsProjectileNova(SkillCatalog skills, int effect)
    {
        var stats = skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        return Stats.Key("base_is_projectile") is uint key && stats.GetValueOrDefault(key) != 0;
    }

    /// <summary>Called from the essence tick: each released rare casts its ready special spells while a player is in reach.
    /// The rare's own AI is untouched (it fights with its default attack and the hit-kit spells); these cast alongside it.</summary>
    static Task CastSpecialSpells(Player conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return Task.CompletedTask;
        double now = AreaTime(conn);
        var casts = Casts.GetOrCreateValue(conn.Instance);
        foreach (var mono in area.Monoliths.Where(m => m.Stage >= 4 && m.Monster != 0).ToArray())
        {
            if (!conn.Area.Monsters.TryGetValue(mono.Monster, out var monster)) continue;
            if (monster.Dead || monster.Life <= 0 || !monster.Hostile) continue;
            CastState state;
            lock (casts)
                if (!casts.TryGetValue(mono.Monster, out state!))
                    casts[mono.Monster] = state = new CastState { Spells = SpecialSpellsOf(conn, mono.Essences, monster.Level) };
            if (state.Spells.Count == 0) continue;
            // The tick runs every frame for every player: a rare with nothing off cooldown costs one pass over its few
            // spells, and the target search and position work happen only for a rare about to cast.
            bool ready = false;
            lock (state)
                foreach (var spell in state.Spells) if (now >= state.Ready.GetValueOrDefault(spell.Pulse)) { ready = true; break; }
            if (!ready) continue;
            var at = monster.Walk.Position(AreaTime(conn));
            double range = Math.Max(60, monster.Variety?.AggroRange ?? 60);
            var target = Members(conn).Where(c => c.Connection.PlayerSent && c.PlayerLife > 0 && !c.Immune
                && Dist(c.Walk.Position(AreaTime(c)), at) <= range)
                .OrderBy(c => Dist(c.Walk.Position(AreaTime(c)), at)).FirstOrDefault();
            if (target is null) continue;
            lock (state)
                foreach (var spell in state.Spells)
                {
                    if (now < state.Ready.GetValueOrDefault(spell.Pulse)) continue;
                    state.Ready[spell.Pulse] = now + spell.Cooldown;
                    _ = CastSpell(conn, monster, target, at, spell);
                }
        }
        return Task.CompletedTask;
    }

    static async Task CastSpell(Player conn, SpawnedMonster monster, Player target, (int X, int Y) at, SpecialSpell spell)
    {
        // A pause, a departure or the caster's death stops the spell between its steps: a slain rare must not keep
        // pulsing from its corpse, and a paused area must not land damage.
        bool Stopped() => conn.Closed || conn.LeavingArea || conn.Paused || monster.Dead || monster.Life <= 0;
        try
        {
            if (spell.Kind == "grab")
            {
                var trapAt = target.Walk.Position(AreaTime(target));
                uint circle = SpawnEffect(conn, trapAt, GrabCircleArt);
                uint hand = SpawnEffect(conn, trapAt, GrabHandArt);
                await AreaDelay(conn, 1.0);
                if (!Stopped())
                {
                    uint end = SpawnEffect(conn, trapAt, GrabEndArt);
                    await Grab(conn, monster, target, trapAt, 10);
                    await AreaDelay(conn, 0.6);
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
            at = monster.Walk.Position(AreaTime(conn));
            // Its AI is not held: an essence spell is cast alongside the rare's own fighting, not instead of it. A hold
            // for the wind-up left the rare standing through every pulse and nova, which read as a frozen monster (live
            // 2026-11-23); a walking rare's nova still fires from where it is when the projectiles leave (FireNova), and
            // the pulse is a one-shot effect object at its live cell, so the cast never stops it.
            ShowPulse(conn, spell, at);
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
                for (int i = 0; i < pulses && !Stopped(); i++)
                {
                    // The aura follows the rare: with its AI no longer held it may walk through the pulse window.
                    ChaosDegen(conn, monster.Walk.Position(AreaTime(conn)), PulseRadius, spell);
                    if (i + 1 < pulses)
                    {
                        await AreaDelay(conn, gap);
                        PlayPulse(conn, monster.Walk.Position(AreaTime(conn)), spell);
                    }
                }
                Console.WriteLine($"[map-content] essence chaos aura by 0x{monster.ObjectId:x} at ({at.X}, {at.Y}): {pulses} pulses every {gap:0.##}s");
                return;
            }
            if (spell.Kind is "cold" or "fire" or "lightning")
            {
                // The pulsation wind-up: accelerating pulses (1.25 / 0.5 / 0.25 s), then the nova bursts its projectiles.
                // Each pulse is a one-shot effect at the rare's live cell: the AI is not held, so it may be walking.
                await AreaDelay(conn, 1.25);
                if (Stopped()) return;
                PlayPulse(conn, monster.Walk.Position(AreaTime(conn)), spell);
                await AreaDelay(conn, 0.5);
                if (Stopped()) return;
                PlayPulse(conn, monster.Walk.Position(AreaTime(conn)), spell);
                await AreaDelay(conn, 0.25);
                if (Stopped()) return;
                FireNova(conn, monster, spell);
            }
            await AreaDelay(conn, 0.5);
            // The resistance debuff is the rare's own aura: applied where it stands when the release lands, not where the
            // cast began (the AI is not held, so it may have walked).
            if (!Stopped()) await ResDebuff(conn, monster.Walk.Position(AreaTime(conn)), PulseRadius, spell);
            await AreaDelay(conn, 0.4);
            Console.WriteLine($"[map-content] essence {spell.Kind} pulse by 0x{monster.ObjectId:x} at ({at.X}, {at.Y})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[map-content] essence spell {spell.Kind} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A spell effect object (the client's art for the skill), broadcast to the instance and played at once; the
    /// strongbox ground-effect pattern. Returns its object id, removed when the spell ends.</summary>
    static uint SpawnEffect(Player conn, (int X, int Y) at, string art, float scale = 1)
    {
        uint id = conn.TakeObjectId();
        // One world create with its wake and clip: the supported send path, callable from the spell tasks (the minion
        // output queue is drained on the area's frame and must not be appended to from a continuation).
        _ = SendWorldCreate(conn, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, art) { Scale = scale }.Blob()), null,
            afterCreation: () => [InstanceProtocol.EnterWakeZone(id), InstanceProtocol.AnimationUpdate(id, 0, 0, 1)]);
        return id;
    }

    static void RemoveEffect(Player conn, uint id)
    {
        if (id == 0) return;
        _ = SendWorldRemoval(conn, id, null);
    }

    /// <summary>The pulse is the essence's own instant aura: a one-shot effect object (the MiscAnimated element pulse art) at
    /// the rare's live cell, played at once. It is not a daemon's cast — the cast must not stop or hold the rare, and a
    /// daemon actor left the pulsation behind a walking monster (live 2026-11-23). The damaging part is applied by the
    /// caster (pulse radius, resistance debuff, or the nova's projectiles, <c>EssenceNova.cs</c>); the grab keeps its own
    /// effect objects (its demon_grasp art is a ground trap, not a caster nova).</summary>
    static void ShowPulse(Player conn, SpecialSpell spell, (int X, int Y) at)
    {
        if (conn.Closed || conn.LeavingArea) return;
        uint pulse = SpawnEffect(conn, at, PulseArt(spell.Kind));
        _ = RemovePulseLater(conn, pulse, 2.0);
        Console.WriteLine($"[map-content] essence {spell.Kind} pulse art at ({at.X}, {at.Y})");
    }

    const string IcePulseArt = "Metadata/Effects/Spells/pulses/icepulse.ao";
    const string FirePulseArt = "Metadata/Effects/Spells/pulses/firepulse.ao";
    const string ChaosPulseArt = "Metadata/Effects/Spells/pulses/chaospulse.ao";
    const string LightningPulseArt = "Metadata/Effects/Spells/pulses/lightningpulse.ao";

    static string PulseArt(string kind) => kind switch
    {
        "cold" => IcePulseArt,
        "fire" => FirePulseArt,
        "lightning" => LightningPulseArt,
        _ => ChaosPulseArt,
    };

    static async Task RemovePulseLater(Player conn, uint id, double seconds)
    {
        await AreaDelay(conn, seconds);
        RemoveEffect(conn, id);
    }

    /// <summary>One pulsation of the wind-up: the same one-shot art again, on the rare's live cell.</summary>
    static void PlayPulse(Player conn, (int X, int Y) at, SpecialSpell spell)
    {
        if (conn.Closed || conn.LeavingArea) return;
        uint pulse = SpawnEffect(conn, at, PulseArt(spell.Kind));
        _ = RemovePulseLater(conn, pulse, 1.0);
    }

    static IEnumerable<Player> PlayersNear(Player conn, (int X, int Y) at, double radius) => Members(conn)
        .Where(c => c.Connection.PlayerSent && c.PlayerLife > 0 && Dist(c.Walk.Position(AreaTime(c)), at) <= radius);

    sealed class EssenceEffects
    {
        public readonly List<(Dictionary<uint, int> Stats, double Until, ushort Buff, uint Key)> Debuffs = [];
        /// <summary>Misery's «Essence of Misery» stacks: each a chaos degeneration with its own expiry. The buff (744)
        /// shows the live count; the degeneration share rides <c>Player.Degens</c> under this effects object.</summary>
        public readonly List<(double Dps, double Until)> MiseryStacks = [];
        public ushort MiseryBuff;
        public int MiserySentCount;
        public double MiseryShare;
    }
    static readonly RuleTable<Player, EssenceEffects> Effects = new();

    /// <summary>GameEvents.PlayerStatSources: the resistance pulses on this player, while they last.</summary>
    public static Dictionary<uint, int> EssenceDebuffStats(Player conn, Dictionary<uint, int> stats)
    {
        if (!Effects.TryGetValue(conn, out var effects) || effects.Debuffs.Count == 0) return stats;
        double now = AreaTime(conn);
        lock (effects)
            foreach (var (debuff, until, _, _) in effects.Debuffs)
                if (until > now)
                    foreach (var (key, value) in debuff)
                        stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>Called from the essence tick: expires the player's pulses (their buffs removed, resistances refreshed) and
    /// keeps the chaos degeneration share of <c>Player.Degens</c> equal to the active pulses' sum.</summary>
    static void TickEffects(Player conn)
    {
        if (!Effects.TryGetValue(conn, out var effects)) return;
        double now = AreaTime(conn);
        var goneBuffs = new List<ushort>();
        bool debuffChanged = false;
        lock (effects)
        {
            foreach (var gone in effects.Debuffs.Where(d => d.Until <= now).ToArray())
            { effects.Debuffs.Remove(gone); goneBuffs.Add(gone.Buff); debuffChanged = true; }
        }
        TickMisery(conn, effects);
        if (conn.Closed) return;
        foreach (ushort buff in goneBuffs)
            _ = SendWorldRecord(conn, conn.Id, InstanceEntities.RemoveBuff(conn.Id, buff), null);
        if (debuffChanged && !conn.LeavingArea) _ = RefreshStats(conn);
    }

    /// <summary>A leaving player's pulses and their degeneration share go with them.</summary>
    public static void ClearEffects(Player conn)
    {
        if (Effects.TryGetValue(conn, out var effects))
        {
            conn.Degens[effects] = 0;
            lock (effects)
            {
                effects.Debuffs.Clear(); effects.MiseryStacks.Clear();
                effects.MiseryShare = 0; effects.MiseryBuff = 0; effects.MiserySentCount = 0;
            }
        }
        Effects.Remove(conn);
        GroundExposures.Remove(conn);
    }

    static void LeaveEffects(Player conn) => ClearEffects(conn);

    static async Task RefreshStats(Player c)
    {
        c.Pools = PoolsFor(c, c.Pools.Level);
        c.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(c, () => PlayerStatsOffFrames(c), null);
    }
}