using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The spell daemons of a box's mods (Mods.MonsterMetadata), cast once when the box opens. Each daemon is created at the
/// box and sends its skill's action, so the client plays the skill (as the shrine daemons do); the server applies what the
/// skill's stats give, at the skill's level for the area level:
/// <list type="bullet">
/// <item>ChestIceNova, ChestExplode (Unstable): one area hit around the box after the cast; Ice Nova leaves chilling ground.</item>
/// <item>ChestFirestorm, ChestLightningStorm: an impact every <c>fire_storm_fireball_delay_ms</c> for the skill's duration,
/// at random spots in its storm area, then burning or shocking ground at each for its ground duration.</item>
/// <item>ChestCausticCloud: a hit, then caustic ground (<c>base_chaos_damage_to_deal_per_minute</c>) for its duration.</item>
/// <item>ChestFreeze, ChestIgnite: freezes or ignites (its damage per minute over its duration) the players near the box.</item>
/// <item>StrongboxCorpseExplode: every corpse near the box explodes, each an area hit.</item>
/// <item>ChestReviveAllWithOnslaught: the monsters that died near the box rise again with Onslaught, as its guards.</item>
/// <item>The random-curse daemon: one of its curses on the players near the box for the curse's duration.</item>
/// <item>ChestSummonSandstormDaemon (Deshret's Storm): sandstorms (physical ground) around the box, in its waves.</item>
/// </list>
/// Exquisite Vaal Vessel casts run on the shared instance clock (StrongboxVesselSpells.cs).
/// </summary>
public static partial class MapStrongboxes
{
    /// <summary>A ground effect around a point until an instance clock time: its damage per second to players inside, and the ailment
    /// it inflicts on them (chill, shock or none).</summary>
    public sealed record Zone((int X, int Y) At, int Radius, double Until, DamageType Type, double Dps, string? Ailment, uint Art);
    sealed record GroundPatch(Zone Zone, string Model, double? CausticFrom = null, float? Scale = null);

    /// <summary>The radius (cells) of a daemon whose skill gives none: OUR choice, the reach of an Ice Nova (26).</summary>
    const int DefaultRadius = 26;

    static readonly Dictionary<string, (string Curse, ushort Buff)> CurseSkills = new(StringComparer.Ordinal)
    {
        ["SkeletonTemporalChains"] = ("temporal_chains", 69), ["MonsterEnfeeble"] = ("enfeeble", 76),
        ["SkeletonVulnerability"] = ("vulnerability", 79), ["MonsterElementalWeakness"] = ("elemental_weakness", 70),
        ["MonsterFlammability"] = ("flammability", 71), ["MonsterFrostbite"] = ("frostbite", 72),
        ["MonsterConductivity"] = ("conductivity", 73), ["Silence"] = ("silence", 275),
    };

    /// <summary>The curse stats a cursed player takes (what a curse's own stats do to the one it curses).</summary>
    static readonly HashSet<string> CurseOnPlayerStats = new(StringComparer.Ordinal)
    {
        "base_resist_all_elements_%", "base_fire_damage_resistance_%", "base_cold_damage_resistance_%", "base_lightning_damage_resistance_%",
        "base_chaos_damage_resistance_%", "accuracy_rating_+%", "enfeeble_damage_+%_final", "physical_damage_taken_+%",
        "damage_taken_+%_on_low_life", "temporal_chains_action_speed_+%_final", "buff_time_passed_+%_other_than_temporal_chains",
        "chance_to_be_ignited_%", "chance_to_be_frozen_%", "chance_to_be_shocked_%", "base_additional_physical_damage_reduction_%",
        "degen_effect_+%", "self_elemental_status_duration_-%",
    };

    sealed class PlayerCurse
    {
        public readonly List<(Dictionary<uint, int> Stats, double Until, ushort Buff)> Active = [];
    }
    static readonly RuleTable<ClientState, PlayerCurse> Cursed = new();

    /// <summary>GameEvents.PlayerStatSources: the strongbox curses on this player, while they last.</summary>
    public static Dictionary<uint, int> CurseStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!Cursed.TryGetValue(conn, out var cursed) || cursed.Active.Count == 0) return stats;
        double now = Now;
        lock (cursed)
            foreach (var (curse, until, _) in cursed.Active)
                if (until > now)
                    foreach (var (key, value) in curse)
                        stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>A skill's base hit damage from its stats: <c>spell_minimum_base_&lt;type&gt;_damage</c> and the maximum, or the
    /// <c>secondary_</c> ones (an explosion's).</summary>
    static DamagePacket Packet(Dictionary<uint, int> stats, string prefix)
    {
        var result = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            int min = Get($"{prefix}_minimum_base_{slug}_damage"), max = Get($"{prefix}_maximum_base_{slug}_damage");
            if (max > 0) result = result.Add(type, new(min, Math.Max(min, max)));
        }
        return result;
    }

    static double Now => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>Starts the box's spell daemons (they run on their own; the box does not wait for them).</summary>
    static void CastDaemons(ClientState conn, Area area, StrongboxCatalog.Definition type, Box box, Random random)
    {
        foreach (var (mod, daemon, skills) in Daemons(conn, type, box))
        {
            var curses = skills.Where(CurseSkills.ContainsKey).ToArray();
            var cast = curses.Length > 0 ? [curses[random.Next(curses.Length)]] : skills;
            foreach (string skill in cast)
                if (curses.Length > 0 || IsSpell(skill))
                    _ = Run(conn, area, box, mod, daemon, skill, random.Next());
        }
    }

    static bool IsSpell(string skill) => skill is "ChestIceNova" or "ChestExplode" or "ChestFirestorm" or "ChestLightningStorm"
        or "ChestCausticCloud" or "ChestFreeze" or "ChestIgnite" or "StrongboxCorpseExplode" or "ChestReviveAllWithOnslaught"
        or "ChestSummonSandstormDaemon";

    static async Task Run(ClientState conn, Area area, Box box, string mod, string daemon, string skill, int seed)
    {
        try
        {
            var catalog = SkillsFor(conn);
            int row = catalog.EffectRow(skill);
            if (row < 0) return;
            // The skill at the area level: its level row, and its damage scaled by the monster skill effectiveness for the
            // actor's level (GolemData.Skill, as a monster's own skills are).
            int actorLevel = AreaLevel(conn);
            var native = GolemData.Skill(catalog, row, actorLevel);
            int level = native.Level;
            var stats = native.Stats;
            DamagePacket Hit(string prefix) => Packet(stats, prefix).Scale(native.Multiplier);
            int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            double cast = Math.Max(0.1, catalog.CastMs(row) / 1000.0);
            double seconds = Get("base_skill_effect_duration") / 1000.0;
            int radius = Get("active_skill_base_area_of_effect_radius") is int r and > 0 ? r : DefaultRadius;
            radius = (int)Math.Round(radius * Math.Max(0, 1 + Get("active_skill_area_of_effect_radius_+%_final") / 100.0));
            var random = new Random(seed);
            var at = (box.X, box.Y);
            uint shown = await Show(conn, daemon, row, actorLevel, stats, at);
            Console.WriteLine($"[map-content] strongbox 0x{box.Id:x} {mod}: {skill} level {level} (area {actorLevel}), radius {radius}, " +
                $"cast {cast:0.##} s, hit {Hit("spell")} / {Hit("secondary")}");
            await Task.Delay(TimeSpan.FromSeconds(cast));
            if (conn.Closed || conn.LeavingArea) return;
            switch (skill)
            {
                case "ChestIceNova":
                    await HitPlayers(conn, at, radius, DamageType.Cold, Hit("spell"));
                    if (Get("ice_nova_places_ground_ice") != 0)
                        AddZone(conn, area, new(at, radius, conn.Clock.Elapsed.TotalSeconds + seconds, DamageType.Cold, 0, "chill", 0), "Metadata/Effects/Spells/ground_effects/ice/ice.ao");
                    break;
                case "ChestExplode":
                    await HitPlayers(conn, at, radius, DamageType.Fire, Hit("secondary"));
                    break;
                case "ChestFirestorm" or "ChestLightningStorm":
                {
                    bool fire = skill == "ChestFirestorm";
                    var type = fire ? DamageType.Fire : DamageType.Lightning;
                    int area_ = (int)Math.Round(radius * Math.Sqrt(Math.Max(0, 1 + Get("firestorm_base_area_of_effect_+%") / 100.0)));
                    double delay = Math.Max(0.05, Get("fire_storm_fireball_delay_ms") / 1000.0);
                    double ground = Get(fire ? "firestorm_drop_burning_ground_duration_ms" : "firestorm_drop_ground_shock_duration_ms") / 1000.0;
                    double burning = Get("base_fire_damage_to_deal_per_minute") / 60.0;
                    int impact = Math.Max(4, radius / 2);
                    for (double t = 0; t < Math.Max(delay, seconds) && !conn.Closed && !conn.LeavingArea; t += delay)
                    {
                        double angle = random.NextDouble() * Math.Tau, d = Math.Sqrt(random.NextDouble()) * area_;
                        var spot = conn.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * d), at.Y + (int)Math.Round(Math.Sin(angle) * d)));
                        await HitPlayers(conn, spot, impact, type, Hit("spell"));
                        if (ground > 0)
                            AddZone(conn, area, new(spot, impact, conn.Clock.Elapsed.TotalSeconds + ground, type, fire ? burning : 0, fire ? null : "shock", 0),
                                fire ? "Metadata/Effects/Spells/ground_effects/fire/fire1.ao" : "Metadata/Effects/Spells/ground_effects/lightning/lightning_ground_01.ao");
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                    }
                    break;
                }
                case "ChestCausticCloud":
                    await HitPlayers(conn, at, radius, DamageType.Chaos, Hit("spell"));
                    AddZone(conn, area, new(at, radius, conn.Clock.Elapsed.TotalSeconds + seconds, DamageType.Chaos, Get("base_chaos_damage_to_deal_per_minute") / 60.0, null, 0),
                        $"Metadata/Effects/Spells/ground_effects/caustic/caustic_0{Math.Clamp(Get("ground_caustic_art_variation") + 1, 1, 3)}.ao");
                    break;
                case "ChestFreeze":
                    // No stat gives the freeze's length: OUR choice, one second.
                    Ailment(conn, at, radius, PlayerElementalAilments.Kind.Freeze, 100, 1);
                    break;
                case "ChestIgnite":
                    Ailment(conn, at, radius, PlayerElementalAilments.Kind.Ignite, Get("base_fire_damage_to_deal_per_minute") / 60.0, Math.Max(1, seconds));
                    break;
                case "StrongboxCorpseExplode":
                    await ExplodeCorpses(conn, at, radius, Hit("secondary"));
                    break;
                case "ChestReviveAllWithOnslaught":
                    await Revive(conn, area, box, radius);
                    break;
                case "ChestSummonSandstormDaemon":
                    await Sandstorms(conn, area, box, catalog, row, random);
                    break;
                default:
                    if (CurseSkills.TryGetValue(skill, out var curse)) await Curse(conn, at, radius, curse, stats, Math.Max(1, seconds));
                    break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
            RemoveDaemon(conn, shown);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[map-content] strongbox daemon {skill} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The daemon at the box, sending its skill's action so the client plays it; 0 when it cannot be created.</summary>
    static async Task<uint> Show(ClientState conn, string daemon, int effect, int actorLevel,
        IReadOnlyDictionary<uint, int> nativeStats, (int X, int Y) at, Action<uint>? admitted = null, bool play = true, SpellCastTiming? timing = null,
        MonsterAttack? actionRecord = null, (int X, int Y)? aim = null, GrantedSkill? precedingGrant = null)
    {
        var catalog = SkillsFor(conn);
        if (Monsters.Find(daemon) is not { Supported: true } variety || catalog.ActionHash(effect) is not ushort hash) return 0;
        ushort skillId = (ushort)(hash & 0x3fff);
        var stats = new Dictionary<uint, int>(nativeStats);
        var action = actionRecord ?? new MonsterAttack(skillId, Math.Max(0.1, catalog.CastMs(effect) / 1000.0), DefaultRadius, DamagePacket.Empty) { Spell = true };
        foreach (var (key, value) in action.InPlaceSkillStats()) stats[key] = value;
        timing?.ApplyTo(stats, Stats);
        if (catalog.FindMonsterSkill(skillId, effect, actorLevel, [.. stats.Select(p => (p.Key, p.Value))]) is not { } skill) return 0;
        var (remote, stream, ciphers) = conn.Wire;
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)actorLevel, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, log: false, alone: false, scripted: true, ownedSkills: precedingGrant is null ? [skill] : [precedingGrant, skill], placement: MonsterPlacement.Established,
            instanceOwned: admitted is not null, owned: (actor, m) =>
            {
                m.NoRewards = true;
                m.Attack = null;
                m.EncounterStats = new Dictionary<string, int> { ["base_cannot_be_damaged"] = 1 };
                admitted?.Invoke(actor);
            });
        if (id == 0) return 0;
        if (play) await PlayDaemon(conn, id, skillId, at, aim ?? at, daemon, shared: admitted is not null);
        return id;
    }

    static Task PlayDaemon(ClientState conn, uint id, ushort skillId, (int X, int Y) at, (int X, int Y) aim, string daemon, bool shared)
    {
        IEnumerable<ClientState> viewers = shared ? Members(conn) : [conn];
        return Task.WhenAll(viewers.Select(Play));
        async Task Play(ClientState viewer)
        {
            var wire = viewer.Wire;
            var view = shared ? ViewFor(viewer) : null;
            if (view is not null)
            {
                if (!viewer.PlayerSent) return;
                bool seen;
                lock (viewer.Monsters)
                {
                    if (view.Casts.Contains(id)) return;
                    seen = viewer.MonsterViews.ContainsKey(id);
                }
                if (!seen) await SyncInstanceMonsters(wire.Remote, wire.Stream, wire.Ciphers, viewer, reveal: false);
            }
            await SendWorldRecords(wire.Remote, wire.Stream, wire.Ciphers, viewer, id, () =>
            {
                lock (viewer.Monsters)
                    if (view is not null && (!viewer.MonsterViews.ContainsKey(id) || viewer.AsleepMonsters.Contains(id) || !view.Casts.Add(id))) return null;
                return [InstanceProtocol.ActionStarted(id, at.X, at.Y, aim.X, aim.Y, skillId, 0xffff, viewer.MonsterOrderFlags), InstanceProtocol.ActionEnded(id)];
            }, $"strongbox daemon {daemon}");
        }
    }

    static void RemoveDaemon(ClientState conn, uint id)
    {
        if (id == 0) return;
        lock (conn.Monsters)
            if (conn.Monsters.Remove(id, out var monster))
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                conn.MonsterViews.Remove(id); conn.AsleepMonsters.Remove(id);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "strongbox daemon ends", id, monster,
                    PlayerMinions.OutputKind.Remove, WorldId: id, RemovesObject: true));
            }
    }

    static IEnumerable<ClientState> PlayersNear(ClientState conn, (int X, int Y) at, double radius) => Members(conn)
        .Where(c => c.PlayerSent && c.PlayerLife > 0 && Distance(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) <= radius);

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>A spell hit on the players within the radius, through the player's own defences (as a shrine's spell).</summary>
    static Task HitPlayers(ClientState source, (int X, int Y) at, double radius, DamageType type, DamagePacket packet)
    {
        return Task.WhenAll(PlayersNear(source, at, radius).ToArray().Select(Hit));

        async Task Hit(ClientState conn)
        {
            int amount = (int)Math.Clamp(packet.Roll(Random.Shared).MinTotal, 0, int.MaxValue);
            if (amount <= 0 || conn.Immune || InGracePeriod(conn) || !TrySyntheticEnemyHit(conn, type, amount, block: true, spell: true, suppress: true, out var hit)) return;
            if (hit.Damage > 0)
            {
                var stats = CharacterPlayerStats(conn);
                lock (conn.Monsters)
                    ApplyPlayerAilmentsLocked(conn, DamagePacket.Of(type, amount, amount), DamagePacket.Of(type, hit.Damage, hit.Damage),
                        false, hit.Blocked, conn.Clock.Elapsed.TotalSeconds, stats);
            }
            var (remote, stream, ciphers) = conn.Wire;
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife), "strongbox daemon hit");
            await SendPlayerEnergyShield(remote, stream, ciphers, conn);
            if (hit.Mana != hit.ManaBefore) await SendPlayerMana(remote, stream, ciphers, conn, "strongbox daemon mana");
            if (conn.PlayerLife == 0) await PlayerDied(remote, stream, ciphers, conn, "death from a strongbox daemon");
        }
    }

    static void Ailment(ClientState source, (int X, int Y) at, double radius, PlayerElementalAilments.Kind kind, double magnitude, double seconds)
    {
        foreach (var conn in PlayersNear(source, at, radius).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            var stats = CharacterPlayerStats(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            double after;
            lock (conn.Monsters)
            {
                ApplyPlayerAilmentsLocked(conn, now, a => a.Inflict(kind, magnitude, seconds, now,
                    name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, conn.IncomingHitRandom));
                after = conn.ElementalAilments.Magnitude(kind, now);
            }
            Console.WriteLine($"[map-content] strongbox daemon {kind} {magnitude:0.#} for {seconds:0.#} s on {CharacterName(conn)}: " +
                (after > 0 ? $"active ({after:0.#})" : "avoided or immune"));
        }
    }

    /// <summary>One of the daemon's curses on the players near the box: the curse's stats that act on the one it curses
    /// (and <c>you_are_cursed_with_&lt;curse&gt;</c>), its buff, for its duration; a curse-immune player is not cursed.</summary>
    static async Task Curse(ClientState source, (int X, int Y) at, double radius, (string Name, ushort Buff) curse,
        Dictionary<uint, int> skillStats, double seconds)
    {
        foreach (var conn in PlayersNear(source, at, radius).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            var player = CharacterPlayerStats(conn);
            int Has(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
            if (Has("immune_to_curses") != 0 || Has("virtual_immune_to_curses") != 0 || Has("hexproof") != 0) continue;
            var stats = skillStats.Where(p => CurseOnPlayerStats.Contains(Stats.Name(p.Key) ?? "")).ToDictionary(p => p.Key, p => p.Value);
            if (Stats.Key("you_are_cursed_with_" + curse.Name) is uint flag) stats[flag] = 1;
            var cursed = Cursed.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (cursed) { cursed.Active.RemoveAll(c => c.Until <= Now); cursed.Active.Add((stats, Now + seconds, buff)); }
            var (remote, stream, ciphers) = conn.Wire;
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, curse.Buff,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"strongbox curse {curse.Name} for {seconds:0.#} s");
            await RefreshStats(conn);
            _ = Uncurse(conn, buff, seconds);
        }
    }

    static async Task Uncurse(ClientState conn, ushort buff, double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (Cursed.TryGetValue(conn, out var cursed)) lock (cursed) cursed.Active.RemoveAll(c => c.Buff == buff);
        if (conn.Closed) return;
        var (remote, stream, ciphers) = conn.Wire;
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        if (!conn.LeavingArea) await RefreshStats(conn);
    }

    static async Task RefreshStats(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }

    /// <summary>Every corpse near the box explodes (and is consumed): each an area hit around it.</summary>
    static async Task ExplodeCorpses(ClientState conn, (int X, int Y) at, int radius, DamagePacket damage)
    {
        var corpses = new List<(uint Id, (int X, int Y) At, bool Remove)>();
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
            foreach (var (id, m) in conn.Monsters)
            {
                if (!m.Hostile || !m.Dead || m.CorpseConsumed || m.Ailments.Shattered) continue;
                var spot = m.Walk.Position(now);
                if (Distance(spot, at) > radius * 2) continue;
                m.CorpseConsumed = true;
                corpses.Add((id, spot, !m.CreatedAsCorpse));
            }
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var (id, spot, remove) in corpses)
        {
            if (remove) await SendWorldRemoval(remote, stream, ciphers, conn, id, null);
            await ShowArt(conn, spot, KillExplosionAo, 1.5);
            await HitPlayers(conn, spot, radius, DamageType.Fire, damage);
        }
    }

    /// <summary>The monsters that died near the box rise again with Onslaught (20% more attack, cast and movement speed),
    /// as the box's guards: its loot waits for them too.</summary>
    static async Task Revive(ClientState conn, Area area, Box box, int radius)
    {
        var dead = new List<(uint Id, MonsterCatalog.Variety Variety, byte Level, MonsterRarity Rarity, (int X, int Y) At, bool Remove)>();
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
            foreach (var (id, m) in conn.Monsters)
                if (m.Hostile && m.Dead && !m.CorpseConsumed && !m.Ailments.Shattered && m.Variety is { } variety
                    && Distance(m.Walk.Position(now), (box.X, box.Y)) <= radius * 2)
                {
                    m.CorpseConsumed = true;
                    dead.Add((id, variety, (byte)m.Level, m.Rarity, m.Walk.Position(now), !m.CreatedAsCorpse));
                }
        if (dead.Count == 0) return;
        var onslaught = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
        {
            ["attack_speed_+%"] = 20, ["cast_speed_+%"] = 20, ["base_movement_velocity_+%"] = 20,
        }));
        var (remote, stream, ciphers) = conn.Wire;
        var risen = new List<uint>();
        foreach (var (id, variety, level, rarity, at, remove) in dead)
        {
            if (remove) await SendWorldRemoval(remote, stream, ciphers, conn, id, null);
            uint again = await SpawnMonster(remote, stream, ciphers, conn, variety, level, rarity, at.X, at.Y, alone: false, log: false,
                extraMods: onslaught, owned: (_, m) => m.Aggro = true);
            if (again != 0) risen.Add(again);
        }
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is { } boxes && boxes[box.Index] is { Phase: 1 } current && current.Id == box.Id)
            {
                current.Guards.AddRange(risen);
                Keep(conn, area);
            }
        }
        finally { area.Gate.Release(); }
        Console.WriteLine($"[map-content] strongbox 0x{box.Id:x}: {risen.Count} monster(s) revived with Onslaught");
    }

    /// <summary>Deshret's sandstorms: its summon skill's waves of sandstorms, each physical ground around a spot near the
    /// box for the sandstorm's own skill duration; the damage is the sandstorm monster's skill's per-minute damage.</summary>
    static async Task Sandstorms(ClientState conn, Area area, Box box, SkillCatalog catalog, int row, Random random)
    {
        int level = AreaLevel(conn);
        if (catalog.MonsterSummon(row, level) is not { } summon || Monsters.Find(summon.Variety.ToString()) is not { } storm) return;
        var stats = catalog.EffectStats(row, level).ToDictionary(s => s.Item1, s => s.Item2);
        int repeats = Stats.Key("skill_repeat_count") is uint k ? stats.GetValueOrDefault(k) : 0;
        int effect = (storm.GrantedEffects ?? []).FirstOrDefault(-1);
        var own = effect < 0 ? new Dictionary<uint, int>() : GolemData.Skill(catalog, effect, level).Stats;
        int Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        double dps = Own("base_physical_damage_to_deal_per_minute") / 60.0;
        double seconds = Own("base_skill_effect_duration") is int ms and > 0 ? ms / 1000.0 : 12;
        int radius = Own("active_skill_base_area_of_effect_radius") is int r and > 0 ? r : 12;
        int speed = Stats.Key("active_skill_cast_speed_+%_final") is uint c ? stats.GetValueOrDefault(c) : 0;
        double interval = Math.Max(0.25, catalog.CastMs(row) / 1000.0 / Math.Max(0.05, 1 + speed / 100.0));
        for (int wave = 0; wave <= repeats && !conn.Closed && !conn.LeavingArea; wave++)
        {
            for (int i = 0; i < summon.Count; i++)
            {
                double angle = random.NextDouble() * Math.Tau, d = 6 + random.NextDouble() * 14;
                var spot = conn.Layout.Near((box.X + (int)Math.Round(Math.Cos(angle) * d), box.Y + (int)Math.Round(Math.Sin(angle) * d)));
                AddZone(conn, area, new(spot, radius, conn.Clock.Elapsed.TotalSeconds + seconds, DamageType.Physical, dps, null, 0),
                    "Metadata/Effects/Spells/ground_effects/sandstorm/ground_rig.ao");
            }
            await Task.Delay(TimeSpan.FromSeconds(interval));
        }
    }

    static async Task ShowArt(ClientState conn, (int X, int Y) at, string art, double seconds)
    {
        uint id = conn.TakeObjectId();
        lock (conn.Monsters)
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, art).Blob()), null, WorldId: id, CreatesObject: true));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
        }
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        lock (conn.Monsters)
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), null, WorldId: id, RemovesObject: true));
    }

    static void AddZone(ClientState conn, Area area, Zone zone, string art)
    {
        if (zone.Art == 0) zone = zone with { Art = conn.TakeObjectId() };
        lock (area.Zones) area.Zones.Add(zone.Art, new(zone, art));
    }

    /// <summary>Called from <see cref="Tick"/>: each player's share of the zones' damage over time (an ailment-free degen,
    /// after resistance, through <c>TrapDegenPerSecond</c>), the zones' ailments, and the expired zones' art removed.</summary>
    static void TickZones(ClientState conn, Area area, double now)
    {
        GroundPatch[] patches;
        lock (area.Zones)
        {
            foreach (var gone in area.Zones.Values.Where(p => p.Zone.Until <= now).ToArray()) area.Zones.Remove(gone.Zone.Art);
            patches = [.. area.Zones.Values];
        }
        var share = ZoneShare.GetOrCreateValue(conn);
        lock (conn.Monsters)
        {
            foreach (uint id in share.Art.Except(patches.Select(p => p.Zone.Art)).ToArray())
            {
                share.Art.Remove(id);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), null, WorldId: id, RemovesObject: true));
            }
            share.Caustic = [.. patches.Where(p => p.CausticFrom.HasValue)];
            foreach (var (zone, model, _, scale) in patches)
            {
                if (!share.Art.Add(zone.Art)) continue;
                var effect = new EffectObject(zone.At.X, zone.At.Y, model) { Flags0 = EffectObject.FixedRotation, Scale = scale ?? Math.Max(1f, zone.Radius / 5f) };
                conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(zone.Art, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
                    null, WorldId: zone.Art, CreatesObject: true));
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(zone.Art), null, WorldId: zone.Art));
            }
        }
        double at = conn.Clock.Elapsed.TotalSeconds;
        var here = conn.Walk.Position(at);
        bool alive = conn.PlayerLife > 0 && !conn.Paused && !conn.Immune && !InGracePeriod(conn);
        var inside = alive ? patches.Where(p => p.CausticFrom is null).Select(p => p.Zone).Where(z => Distance(here, z.At) <= z.Radius).ToArray() : [];
        var stats = inside.Length > 0 ? CharacterPlayerStats(conn) : null;
        var immune = stats is not null ? PlayerImmunities(conn, stats) : [];
        double Taken(DamageType type) => type == DamageType.Physical ? 1 : 1 - Math.Min(MaxResistance(conn, type), Resistance(conn, type)) / 100.0;
        double dps = inside.Where(z => z.Dps > 0 && z.Type != DamageType.Fire && !immune.Contains(z.Type)).Sum(z => z.Dps * Taken(z.Type));
        // Strongbox burning ground applies one ground_fire_burn to the player, including where successive meteors overlap.
        if (!immune.Contains(DamageType.Fire) && inside.Any(z => z.Type == DamageType.Fire))
        {
            int effect = stats!.GetValueOrDefault(Stats.Key("burning_ground_effect_on_self_+%")!.Value);
            dps += inside.Where(z => z.Type == DamageType.Fire).Max(z => z.Dps) * Taken(DamageType.Fire) * Math.Max(0, 1 + effect / 100.0);
        }
        lock (conn.Monsters)
        {
            conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - share.Dps + dps);
            share.Dps = dps;
        }
        foreach (var zone in inside.Where(z => z.Ailment is not null))
        {
            var kind = zone.Ailment == "chill" ? PlayerElementalAilments.Kind.Chill : PlayerElementalAilments.Kind.Shock;
            int magnitude = kind == PlayerElementalAilments.Kind.Chill ? 10 : 15;
            if (conn.ElementalAilments.Magnitude(kind, at) >= magnitude) continue;
            lock (conn.Monsters)
                ApplyPlayerAilmentsLocked(conn, at, a => a.Inflict(kind, magnitude, .3, at,
                    name => Stats.Key(name) is uint key ? stats!.GetValueOrDefault(key) : 0, conn.IncomingHitRandom));
        }
        if (patches.Length == 0) ZoneShare.Remove(conn);
    }

    sealed class Exposure
    {
        public double Dps;
        public readonly HashSet<uint> Art = [];
        public GroundPatch[] Caustic = [];
        public double CausticThrough = double.NegativeInfinity, CausticStarted = double.NegativeInfinity, TakenCarry;
    }
    static readonly RuleTable<ClientState, Exposure> ZoneShare = new();

    /// <summary>The zones' share of a leaving player's degen goes with them.</summary>
    static void LeaveZones(ClientState conn, bool removeArt = false)
    {
        if (!ZoneShare.TryGetValue(conn, out var share)) return;
        lock (conn.Monsters)
        {
            conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - share.Dps);
            if (removeArt)
                foreach (uint id in share.Art)
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), null, WorldId: id, RemovesObject: true));
            ZoneShare.Remove(conn);
        }
    }
}
