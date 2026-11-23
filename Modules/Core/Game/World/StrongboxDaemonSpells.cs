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
/// The Vaal daemons only roll on Vaal-empowered chests (the <c>vaal_empowered</c> tag), which no strongbox has.
/// </summary>
public static partial class MapStrongboxes
{
    /// <summary>A ground effect around a point until a UTC time: its damage per second to players inside, and the ailment
    /// it inflicts on them (chill, shock or none).</summary>
    public sealed record Zone((int X, int Y) At, int Radius, double Until, DamageType Type, double Dps, string? Ailment, uint Art);

    /// <summary>The radius (cells) of a daemon whose skill gives none: OUR choice, the reach of an Ice Nova (26).</summary>
    const int DefaultRadius = 26;

    /// <summary>The official art of a strongbox's corpse explosion (MiscAnimated <c>StrongboxExplosion</c>): its one-shot
    /// "animate" clip (1000 ms) carries Audio/…/Chests/Strongbox/NewStrongBoxExplode.ogg.</summary>
    const string StrongboxExplodeAo = "Metadata/Effects/Spells/monsters_effects/strongbox_explode/rig.ao";

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
    static readonly RuleTable<Player, PlayerCurse> Cursed = new();

    /// <summary>GameEvents.PlayerStatSources: the strongbox curses on this player, while they last.</summary>
    public static Dictionary<uint, int> CurseStats(Player player, Dictionary<uint, int> stats)
    {
        if (!Cursed.TryGetValue(player, out var cursed) || cursed.Active.Count == 0) return stats;
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
    static void CastDaemons(Player player, Area area, StrongboxCatalog.Definition type, Box box, Random random)
    {
        foreach (var (mod, daemon, skills) in Daemons(player, type, box))
        {
            var curses = skills.Where(CurseSkills.ContainsKey).ToArray();
            var cast = curses.Length > 0 ? [curses[random.Next(curses.Length)]] : skills;
            foreach (string skill in cast)
                if (curses.Length > 0 || IsSpell(skill))
                    _ = Run(player, area, box, mod, daemon, skill, random.Next());
        }
    }

    static bool IsSpell(string skill) => skill is "ChestIceNova" or "ChestExplode" or "ChestFirestorm" or "ChestLightningStorm"
        or "ChestCausticCloud" or "ChestFreeze" or "ChestIgnite" or "StrongboxCorpseExplode" or "ChestReviveAllWithOnslaught"
        or "ChestSummonSandstormDaemon";

    static async Task Run(Player player, Area area, Box box, string mod, string daemon, string skill, int seed)
    {
        try
        {
            var catalog = SkillsFor(player);
            int row = catalog.EffectRow(skill);
            if (row < 0) return;
            // The skill at the area level: its level row, and its damage scaled by the monster skill effectiveness for the
            // actor's level (GolemData.Skill, as a monster's own skills are).
            var native = GolemData.Skill(catalog, row, AreaLevel(player));
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
            uint shown = await Show(player, daemon, row, at);
            Console.WriteLine($"[map-content] strongbox 0x{box.Id:x} {mod}: {skill} level {level} (area {AreaLevel(player)}), radius {radius}, " +
                $"cast {cast:0.##} s, hit {Hit("spell")} / {Hit("secondary")}");
            // The action stays open for the skill's cast time (800 ms Ice Nova, 900 ms storms, 1000 ms Explode): the
            // client plays the daemon's wind-up for the action's length instead of cutting it at once, which made every
            // spell appear the moment the box opened (live 2026-11-23). The end comes with the effect, so the client's
            // release animation and the server's hit land together.
            await Task.Delay(TimeSpan.FromSeconds(cast));
            if (player.Closed || player.LeavingArea) return;
            await EndAction(player, shown);
            switch (skill)
            {
                case "ChestIceNova":
                    await HitPlayers(player, at, radius, DamageType.Cold, Hit("spell"));
                    // No ground: the chest's nova leaves none (the old ice.ao left a field of ice spikes standing under
                    // the box, live 2026-11-22), whatever the skill's ice_nova_places_ground_ice says.
                    break;
                case "ChestExplode":
                    // The box's own explosion art: MiscAnimated StrongboxExplosion, its "animate" clip carrying
                    // NewStrongBoxExplode (the Unstable mod's hit had no visual at all before).
                    await FallingArt(player, at, StrongboxExplodeAo, 1.2);
                    await HitPlayers(player, at, radius, DamageType.Fire, Hit("secondary"));
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
                    // The server lands the hits, one per fire_storm_fireball_delay_ms at a random spot in the storm's area. The
                    // falling meteors/bolts are not drawn: the client shows only its own first one, and the server-side
                    // art tried for the rest did not match the game (the issue "Strongbox daemon spells").
                    for (double t = 0; t < Math.Max(delay, seconds) && !player.Closed && !player.LeavingArea; t += delay)
                    {
                        double angle = random.NextDouble() * Math.Tau, d = Math.Sqrt(random.NextDouble()) * area_;
                        var spot = player.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * d), at.Y + (int)Math.Round(Math.Sin(angle) * d)));
                        await HitPlayers(player, spot, impact, type, Hit("spell"));
                        if (ground > 0)
                            AddZone(player, area, new(spot, impact, Now + ground, type, fire ? burning : 0, fire ? null : "shock", 0),
                                fire ? "Metadata/Effects/Spells/ground_effects/fire/fire1.ao" : "Metadata/Effects/Spells/ground_effects/lightning/lightning_ground_01.ao");
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                    }
                    break;
                }
                case "ChestCausticCloud":
                    await HitPlayers(player, at, radius, DamageType.Chaos, Hit("spell"));
                    AddZone(player, area, new(at, radius, Now + seconds, DamageType.Chaos, Get("base_chaos_damage_to_deal_per_minute") / 60.0, null, 0),
                        $"Metadata/Effects/Spells/ground_effects/caustic/caustic_0{Math.Clamp(Get("ground_caustic_art_variation") + 1, 1, 3)}.ao");
                    break;
                case "ChestFreeze":
                    // No stat gives the freeze's length: OUR choice, one second.
                    Ailment(player, at, radius, PlayerElementalAilments.Kind.Freeze, 100, 1);
                    break;
                case "ChestIgnite":
                    Ailment(player, at, radius, PlayerElementalAilments.Kind.Ignite, Get("base_fire_damage_to_deal_per_minute") / 60.0, Math.Max(1, seconds));
                    break;
                case "StrongboxCorpseExplode":
                    await ExplodeCorpses(player, at, radius, Hit("secondary"));
                    break;
                case "ChestReviveAllWithOnslaught":
                    await Revive(player, area, box, radius);
                    break;
                case "ChestSummonSandstormDaemon":
                    await Sandstorms(player, area, box, catalog, row, random);
                    break;
                default:
                    if (CurseSkills.TryGetValue(skill, out var curse)) await Curse(player, at, radius, curse, stats, Math.Max(1, seconds));
                    break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
            RemoveDaemon(player, shown);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[map-content] strongbox daemon {skill} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The daemon at the box, sending its skill's action so the client plays it; 0 when it cannot be created.</summary>
    static async Task<uint> Show(Player player, string daemon, int effect, (int X, int Y) at)
    {
        var catalog = SkillsFor(player);
        if (Monsters.Find(daemon) is not { Supported: true } variety || catalog.ActionHash(effect) is not ushort hash) return 0;
        ushort skillId = (ushort)(hash & 0x3fff);
        // The skill record's level is the area's, as the server's damage is: the client scales the nova and storm from
        // the level's active_skill_area_of_effect_radius_+%_final (Ice Nova: 15% at level 1 up to 182% at 37).
        int level = Math.Clamp(AreaLevel(player), 1, 100);
        var stats = catalog.EffectStats(effect, level).ToDictionary(s => s.Item1, s => s.Item2);
        int GetStat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int GetKey(uint key) => stats.GetValueOrDefault(key);
        // The client scales a skill's nova, storm and ground from these two; without them it shows its own default ring
        // instead of the skill's (the shrine's Ice Nova sets them the same way).
        int baseRadius = GetStat("active_skill_base_area_of_effect_radius") is int own and > 0 ? own : DefaultRadius;
        int radius = SkillArea.Radius(baseRadius, GetKey, true);
        var action = new MonsterAttack(skillId, Math.Max(0.1, catalog.CastMs(effect) / 1000.0), radius, DamagePacket.Empty) { Spell = true };
        foreach (var (key, value) in action.InPlaceSkillStats()) stats[key] = value;
        void Set(string name, int value) { if (Stats.Key(name) is uint key) stats[key] = value; }
        Set("active_skill_area_of_effect_radius", radius);
        Set("total_skill_area_of_effect_radius_+%_including_final", SkillArea.RadiusPercent(GetKey, true));
        // GrantedEffects SkillID: the client's own skill-stat builder adds it as skill_behaviour_variation, and an action
        // takes its data row from it (ChestExplode's is 30: its geometry; without it the box's Explodes drew nothing).
        Set("skill_behaviour_variation", catalog.BehaviourVariation(effect));
        if (catalog.FindByRow(skillId, effect, level, [.. stats.Select(p => (p.Key, p.Value))]) is not { } skill) return 0;
        uint id = await SpawnMonster(player, variety, (byte)AreaLevel(player), MonsterRarity.Normal, at.X, at.Y,
            friendly: true, log: false, alone: false, scripted: true, ownedSkills: [skill], placement: MonsterPlacement.Established,
            owned: (_, m) =>
            {
                m.NoRewards = true;
                m.Attack = null;
                m.EncounterStats = new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 };
            });
        if (id == 0) return 0;
        await SendWorldRecord(player, id,
            InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, skillId, 0xffff, player.MonsterOrderFlags),
            $"strongbox daemon {daemon}");
        return id;
    }

    /// <summary>Ends the daemon's cast (see <see cref="Run"/>): the client plays the wind-up for the action's length,
    /// then the skill's effect.</summary>
    static async Task EndAction(Player player, uint id)
    {
        if (id == 0) return;
        await SendWorldRecord(player, id, InstanceProtocol.ActionEnded(id), "strongbox daemon cast ends");
    }

    static void RemoveDaemon(Player player, uint id)
    {
        if (id == 0) return;
        if (player.Area.Monsters.Remove(id, out var monster))
        {
            monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "strongbox daemon ends", id, monster,
                PlayerMinions.OutputKind.Remove, WorldId: id, RemovesObject: true));
        }
    }

    static IEnumerable<Player> PlayersNear(Player player, (int X, int Y) at, double radius) => Members(player)
        .Where(c => c.Connection.PlayerSent && c.PlayerLife > 0 && Distance(c.Walk.Position(AreaTime(c)), at) <= radius);

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>A spell hit on the players within the radius, through the player's own defences (as a shrine's spell).</summary>
    static async Task HitPlayers(Player source, (int X, int Y) at, double radius, DamageType type, DamagePacket packet)
    {
        foreach (var player in PlayersNear(source, at, radius).ToArray())
        {
            int amount = (int)Math.Clamp(packet.Roll(AreaRng(player)).MinTotal, 0, int.MaxValue);
            if (amount <= 0 || player.Immune || InGracePeriod(player) || !TrySyntheticEnemyHit(player, type, amount, block: true, spell: true, suppress: true, out var hit)) continue;
            if (hit.Damage > 0)
            {
                var stats = CharacterPlayerStats(player);
                ApplyPlayerAilmentsLocked(player, DamagePacket.Of(type, amount, amount), DamagePacket.Of(type, hit.Damage, hit.Damage),
                    false, hit.Blocked, AreaTime(player), stats);
            }
            await SendWorldRecord(player, player.Id, InstanceProtocol.LifeUpdate(player.Id, player.PlayerLife, player.PlayerReservedLife), "strongbox daemon hit");
            await SendPlayerEnergyShield(player);
            if (hit.Mana != hit.ManaBefore) await SendPlayerMana(player, "strongbox daemon mana");
            if (player.PlayerLife == 0) await PlayerDied(player, "death from a strongbox daemon");
        }
    }

    static void Ailment(Player source, (int X, int Y) at, double radius, PlayerElementalAilments.Kind kind, double magnitude, double seconds)
    {
        foreach (var player in PlayersNear(source, at, radius).ToArray())
        {
            if (player.Immune || InGracePeriod(player)) continue;
            var stats = CharacterPlayerStats(player);
            double now = AreaTime(player);
            ApplyPlayerAilmentsLocked(player, now, a => a.Inflict(kind, magnitude, seconds, now,
                key => stats.GetValueOrDefault(key), player.IncomingHitRandom));
            double after = player.ElementalAilments.Magnitude(kind, now);
            Console.WriteLine($"[map-content] strongbox daemon {kind} {magnitude:0.#} for {seconds:0.#} s on {CharacterName(player)}: " +
                (after > 0 ? $"active ({after:0.#})" : "avoided or immune"));
        }
    }

    /// <summary>One of the daemon's curses on the players near the box: the curse's stats that act on the one it curses
    /// (and <c>you_are_cursed_with_&lt;curse&gt;</c>), its buff, for its duration; a curse-immune player is not cursed.</summary>
    static async Task Curse(Player source, (int X, int Y) at, double radius, (string Name, ushort Buff) curse,
        Dictionary<uint, int> skillStats, double seconds)
    {
        foreach (var player in PlayersNear(source, at, radius).ToArray())
        {
            if (player.Immune || InGracePeriod(player)) continue;
            var playerStats = CharacterPlayerStats(player);
            int Has(uint key) => playerStats.GetValueOrDefault(key);
            if (Has(Stat.ImmuneToCurses) != 0 || Has(Stat.VirtualImmuneToCurses) != 0 || Has(Stat.Hexproof) != 0) continue;
            var stats = skillStats.Where(p => CurseOnPlayerStats.Contains(Stats.Name(p.Key) ?? "")).ToDictionary(p => p.Key, p => p.Value);
            if (Stats.Key("you_are_cursed_with_" + curse.Name) is uint flag) stats[flag] = 1;
            var cursed = Cursed.GetOrCreateValue(player);
            ushort buff = player.NextBuff++;
            lock (cursed) { cursed.Active.RemoveAll(c => c.Until <= Now); cursed.Active.Add((stats, Now + seconds, buff)); }
            await SendWorldRecord(player, player.Id, InstanceEntities.AddBuff(player.Id, buff, curse.Buff,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"strongbox curse {curse.Name} for {seconds:0.#} s");
            await RefreshStats(player);
            _ = Uncurse(player, buff, seconds);
        }
    }

    static async Task Uncurse(Player player, ushort buff, double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (Cursed.TryGetValue(player, out var cursed)) lock (cursed) cursed.Active.RemoveAll(c => c.Buff == buff);
        if (player.Closed) return;
        await SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, buff), null);
        if (!player.LeavingArea) await RefreshStats(player);
    }

    static async Task RefreshStats(Player c)
    {
        c.Pools = PoolsFor(c, c.Pools.Level);
        c.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(c, () => PlayerStatsOffFrames(c), null);
    }

    /// <summary>Every corpse near the box explodes (and is consumed): each an area hit around it. The art is the official
    /// one: MiscAnimated <c>StrongboxExplosion</c> (Metadata/Effects/Spells/monsters_effects/strongbox_explode/rig.ao),
    /// whose one-shot "animate" clip carries <c>Audio/…/Chests/Strongbox/NewStrongBoxExplode.ogg</c> — the box's own
    /// explosion sound.</summary>
    static async Task ExplodeCorpses(Player player, (int X, int Y) at, int radius, DamagePacket damage)
    {
        var corpses = new List<(uint Id, (int X, int Y) At, bool Remove)>();
        double now = AreaTime(player);
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (!m.Hostile || !m.Dead || m.CorpseConsumed || m.Ailments.Shattered) continue;
            var spot = m.Walk.Position(now);
            if (Distance(spot, at) > radius) continue;
            m.CorpseConsumed = true;
            corpses.Add((id, spot, !m.CreatedAsCorpse));
        }
        Console.WriteLine($"[map-content] strongbox corpse explosion: {corpses.Count} corpse(s) within {radius} cells of ({at.X}, {at.Y})");
        foreach (var (id, spot, remove) in corpses)
        {
            if (remove) await SendWorldRemoval(player, id, null);
            await ShowArt(player, spot, StrongboxExplodeAo, 1.5, playClip: true);
            await HitPlayers(player, spot, radius, DamageType.Fire, damage);
        }
    }

    /// <summary>The monsters that died near the box rise again with Onslaught (20% more attack, cast and movement speed),
    /// as the box's guards: its loot waits for them too.</summary>
    static async Task Revive(Player player, Area area, Box box, int radius)
    {
        var dead = new List<(uint Id, MonsterCatalog.Variety Variety, byte Level, MonsterRarity Rarity, (int X, int Y) At, bool Remove)>();
        double now = AreaTime(player);
        foreach (var (id, m) in player.Area.Monsters)
            if (m.Hostile && m.Dead && !m.CorpseConsumed && !m.Ailments.Shattered && m.Variety is { } variety
                && Distance(m.Walk.Position(now), (box.X, box.Y)) <= radius * 2)
            {
                m.CorpseConsumed = true;
                dead.Add((id, variety, (byte)m.Level, m.Rarity, m.Walk.Position(now), !m.CreatedAsCorpse));
            }
        if (dead.Count == 0) return;
        var onslaught = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<uint, int>
        {
            [Stat.AttackSpeedPlusPct] = 20, [Stat.CastSpeedPlusPct] = 20, [Stat.BaseMovementVelocityPlusPct] = 20,
        }));
        var risen = new List<uint>();
        foreach (var (id, variety, level, rarity, at, remove) in dead)
        {
            if (remove) await SendWorldRemoval(player, id, null);
            uint again = await SpawnMonster(player, variety, level, rarity, at.X, at.Y, alone: false, log: false,
                extraMods: onslaught, owned: (_, m) => m.Aggro = true);
            if (again != 0) risen.Add(again);
        }
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is { } boxes && boxes[box.Index] is { Phase: 1 } current && current.Id == box.Id)
            {
                current.Guards.AddRange(risen);
                Keep(player, area);
            }
        }
        finally { area.Gate.Release(); }
        Console.WriteLine($"[map-content] strongbox 0x{box.Id:x}: {risen.Count} monster(s) revived with Onslaught");
    }

    /// <summary>Deshret's sandstorms: its summon skill's waves of sandstorms, each physical ground around a spot near the
    /// box for the sandstorm's own skill duration; the damage is the sandstorm monster's skill's per-minute damage.</summary>
    static async Task Sandstorms(Player player, Area area, Box box, SkillCatalog catalog, int row, Random random)
    {
        int level = AreaLevel(player);
        if (catalog.MonsterSummon(row, level) is not { } summon || Monsters.Find(summon.Variety.ToString()) is not { } storm) return;
        var stats = catalog.EffectStats(row, level).ToDictionary(s => s.Item1, s => s.Item2);
        int repeats = stats.GetValueOrDefault(Stat.SkillRepeatCount);
        int effect = (storm.GrantedEffects ?? []).FirstOrDefault(-1);
        var own = effect < 0 ? new Dictionary<uint, int>() : GolemData.Skill(catalog, effect, level).Stats;
        int Own(uint key) => own.GetValueOrDefault(key);
        double dps = Own(Stat.BasePhysicalDamageToDealPerMinute) / 60.0;
        double seconds = Own(Stat.BaseSkillEffectDuration) is int ms and > 0 ? ms / 1000.0 : 12;
        int radius = Own(Stat.ActiveSkillBaseAreaOfEffectRadius) is int r and > 0 ? r : 12;
        int speed = stats.GetValueOrDefault(Stat.ActiveSkillCastSpeedPlusPctFinal);
        double interval = Math.Max(0.25, catalog.CastMs(row) / 1000.0 / Math.Max(0.05, 1 + speed / 100.0));
        for (int wave = 0; wave <= repeats && !player.Closed && !player.LeavingArea; wave++)
        {
            for (int i = 0; i < summon.Count; i++)
            {
                double angle = random.NextDouble() * Math.Tau, d = 6 + random.NextDouble() * 14;
                var spot = player.Layout.Near((box.X + (int)Math.Round(Math.Cos(angle) * d), box.Y + (int)Math.Round(Math.Sin(angle) * d)));
                AddZone(player, area, new(spot, radius, Now + seconds, DamageType.Physical, dps, null, 0),
                    "Metadata/Effects/Spells/ground_effects/sandstorm/ground_rig.ao");
            }
            await Task.Delay(TimeSpan.FromSeconds(interval));
        }
    }

    /// <summary>An effect object at a spot for a while; <paramref name="playClip"/> starts its first clip, which is what
    /// fires the clip's SoundEvents (an effect created by the server does not play them by itself).</summary>
    static async Task ShowArt(Player player, (int X, int Y) at, string art, double seconds, bool playClip = false)
    {
        uint id = player.TakeObjectId();
        // The supported world send, callable from a daemon's continuation (the minion output queue is drained on the
        // area's frame).
        _ = SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, art).Blob()), null,
            afterCreation: () => playClip
                ? [InstanceProtocol.EnterWakeZone(id), InstanceProtocol.AnimationUpdate(id, 0, 0, 1)]
                : [InstanceProtocol.EnterWakeZone(id)]);
        await AreaDelay(player, seconds);
        if (Survivor(player) is { } viewer) await SendWorldRemoval(viewer, id, null);
    }

    /// <summary>An effect object that starts its first clip — what fires its SoundEvents — and is removed after
    /// <paramref name="life"/> seconds. <paramref name="speed"/> times the clip's own timing: a meteor whose art contacts
    /// at 0.5 s played at 0.5/delay lands after the storm's drop delay.</summary>
    static async Task FallingArt(Player player, (int X, int Y) at, string art, double life, float speed = 1)
    {
        uint id = player.TakeObjectId();
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(at.X, at.Y, art).Blob()),
            $"{art} at ({at.X}, {at.Y})");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
        await SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, speed), null);
        SendAfter(player, life, async () =>
        {
            if (Survivor(player) is not { } viewer) return;
            await SendWorldRemoval(viewer, id, null);
        });
    }

    static void AddZone(Player player, Area area, Zone zone, string art)
    {
        uint id = player.TakeObjectId();
        _ = SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(zone.At.X, zone.At.Y, art) { Flags0 = EffectObject.FixedRotation, Scale = Math.Max(1f, zone.Radius / 5f) }.Blob()), null,
            afterCreation: () => [InstanceProtocol.EnterWakeZone(id)]);
        lock (area.Zones) area.Zones.Add(zone with { Art = id });
    }

    /// <summary>Called from <see cref="Tick"/>: each player's share of the zones' damage over time (an ailment-free degen,
    /// after resistance, through <c>TrapDegenPerSecond</c>), the zones' ailments, and the expired zones' art removed.</summary>
    static void TickZones(Player player, Area area)
    {
        double now = Now;
        Zone[] zones;
        lock (area.Zones)
        {
            foreach (var gone in area.Zones.Where(z => z.Until <= now).ToArray())
            {
                area.Zones.Remove(gone);
                _ = SendWorldRemoval(player, gone.Art, null);
            }
            zones = [.. area.Zones];
        }
        var share = ZoneShare.GetOrCreateValue(player);
        double at = AreaTime(player);
        var here = player.Walk.Position(at);
        bool alive = player.PlayerLife > 0 && !player.Paused && !InGracePeriod(player);
        var inside = alive ? zones.Where(z => Distance(here, z.At) <= z.Radius).ToArray() : [];
        var immune = inside.Length > 0 ? PlayerImmunities(player) : [];
        double dps = inside.Where(z => z.Dps > 0 && !immune.Contains(z.Type))
            .Sum(z => z.Dps * (z.Type == DamageType.Physical ? 1 : 1 - Math.Min(MaxResistance(player, z.Type), Resistance(player, z.Type)) / 100.0));
        player.Degens[share] = dps;
        share.Dps = dps;
        foreach (var zone in inside.Where(z => z.Ailment is not null))
        {
            var kind = zone.Ailment == "chill" ? PlayerElementalAilments.Kind.Chill : PlayerElementalAilments.Kind.Shock;
            int magnitude = kind == PlayerElementalAilments.Kind.Chill ? 10 : 15;
            if (player.ElementalAilments.Magnitude(kind, at) >= magnitude) continue;
            var stats = CharacterPlayerStats(player);
            ApplyPlayerAilmentsLocked(player, at, a => a.Inflict(kind, magnitude, .3, at,
                key => stats.GetValueOrDefault(key), player.IncomingHitRandom));
        }
    }

    sealed class Exposure { public double Dps; }
    static readonly RuleTable<Player, Exposure> ZoneShare = new();

    /// <summary>The zones' share of a leaving player's degen goes with them.</summary>
    static void LeaveZones(Player player)
    {
        if (!ZoneShare.TryGetValue(player, out var share)) return;
        player.Degens[share] = 0;
        share.Dps = 0;
    }
}
