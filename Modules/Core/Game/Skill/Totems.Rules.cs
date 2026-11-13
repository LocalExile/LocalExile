using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core;
using DamageModel = GameServer.Core.World.DamageModel;

public static partial class Game
{
    /// <summary>"X% of Damage from Hits is taken from your nearest Totem's Life before you": Forest of Ancestors' 20 and
    /// Kaom's Leadership's 25 add.</summary>
    public const string TotemRedirectStat = "damage_removed_from_your_nearest_totem_before_life_or_es_%";

    /// <summary>Metadata/Characters/Character.ot: base_number_of_totems_allowed 1 and base_presence_radius 80 (the wiki's
    /// fixed 8 metres "in your presence"). The client has both from the .ot; the server's stats do not carry them.</summary>
    public const int BaseTotemsAllowed = 1, PresenceRadius = 80;

    /// <summary>The totem skills that also carry an aura: Rejuvenation Totem's (BuffDefinitions 172
    /// totem_aura_life_regen, base radius 10, wiki Rejuvenation Totem) regenerates the player's Life. A totem skill that
    /// is not here still places its totem and uses its own skill (TotemUseLocked), or casts that skill through the
    /// ordinary projectile runtime when it is an adopted projectile spell (SpellTotems.cs); only the aura is special.</summary>
    static readonly Dictionary<string, (ushort Buff, int Radius)> TotemAuras = new() { ["RejuvenationTotem"] = (172, 10) };

    /// <summary>The ActiveSkillType a dedicated totem skill's own ActiveSkills row carries. SkillTotemId cannot serve as
    /// the marker: every one of the 1941 rows has it, and 1906 share the default 22, so it names the sprite a
    /// Totem-Support conversion would use rather than "this skill summons a totem". SummonsTotem is what only the
    /// totem skills themselves carry (shockwave_totem, holy_flame_totem, ancestor_totem_slash, ...). A skill that Spell
    /// Totem Support totemifies is NOT one of these rows — it carries the `is_totem` stat on its instance instead, which
    /// is what <see cref="IsTotemSkill"/> reads; the two are complements, not alternatives.</summary>
    internal const string TotemSkillType = "SummonsTotem";

    /// <summary>The granted-skill id a totem's object carries for the action the client plays, the id a placed trap or
    /// mine uses for its proxy skill.</summary>
    internal const ushort TotemActionSkill = 0x4000;

    /// <summary>The cells a totem looks for a target in (our number: the server's skill data carries no totem range).
    /// A target is hit with the skill's own radius around the point the use was aimed at.</summary>
    internal const int TotemRange = 60;

    /// <summary>The client's Emerge basic action (`Actor.basic_action` of `Metadata/Monsters/Totems/Totem`): the rise a
    /// totem plays when it appears. Every totem `.act` maps `Emerge`, and no totem variety has a Transitionable
    /// component, so the monster rise's 0x2bc does not apply — 0x1ab then 0x1ac with this id plays it at once.</summary>
    internal const ushort TotemEmergeSkill = 0x0101;

    // GrantedEffects row → MonsterVarieties row of its default skin: ActiveSkills.SkillTotemId is SkillTotemVariations'
    // SkillTotemsKey, TotemSkinId 0 (Rejuvenation Totem: 4 → Metadata/Monsters/Totems/LifeRegenTotem).
    static readonly Lazy<(Dictionary<int, int> Effects, Dictionary<int, int> Skins)> TotemVarieties =
        new(LoadTotemVarieties);

    // The totem auras' BuffDefinitions stats (172: base_life_regeneration_rate_per_minute and five more); the totem's
    // skill gives their values.
    static readonly Lazy<Dictionary<ushort, uint[]>> TotemBuffStats = new(() =>
    {
        var result = new Dictionary<ushort, uint[]>();
        if (!ClientTables.Has("BuffDefinitions")) return result;
        var definitions = ClientTables.Table("BuffDefinitions");
        foreach (var (row, _) in TotemAuras.Values)
            if (row < definitions.Count && !definitions[row].IsNull("StatsKeys"))
                result[row] = [.. definitions[row].Ints("StatsKeys").Select(k => (uint)(k + 1))];
        return result;
    });

    static (Dictionary<int, int> Effects, Dictionary<int, int> Skins) LoadTotemVarieties()
    {
        var result = new Dictionary<int, int>();
        if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills") || !ClientTables.Has("SkillTotemVariations")) return (result, []);
        var skins = new Dictionary<int, int>();
        foreach (var v in ClientTables.Table("SkillTotemVariations"))
            if (v.Int("TotemSkinId") == 0 && v.IntOrNull("MonsterVarietiesKey") is int row)
                skins.TryAdd(v.Int("SkillTotemsKey"), row);
        var skills = ClientTables.Table("ActiveSkills");
        foreach (var e in ClientTables.Table("GrantedEffects"))
            if (e.IntOrNull("ActiveSkill") is int a && a < skills.Count
                && skills[a].IntOrNull("SkillTotemId") is int totem && skins.TryGetValue(totem, out int variety))
                result[e.Index] = variety;
        return (result, skins);
    }

    /// <summary>Whether this skill is one that summons a totem: a dedicated totem skill or a skill Spell Totem Support
    /// totemified, both of which carry the `is_totem` stat on the instance.</summary>
    public static bool IsTotemSkill(ClientState conn, ushort skill) =>
        SupportedSkill(conn, skill).Stats.GetValueOrDefault(Stats.Key("is_totem") ?? 0) != 0;

    /// <summary>The totem variety a gem skill summons, or null. A dedicated totem skill (its ActiveSkills row carries
    /// <see cref="TotemSkillType"/>) summons the sprite its own SkillTotemVariations entry (skin 0) names — every
    /// gem-granted totem skill places, damaging or not. An aura executor summons its native skin. A skill that Spell
    /// Totem Support totemifies is not a dedicated row: it takes a dedicated spell skin when the tables have one and the
    /// generic intelligence skin otherwise.</summary>
    public static MonsterCatalog.Variety? TotemVariety(ClientState conn, int effect)
    {
        if (SkillsFor(conn).HasType(effect, TotemSkillType)
            && TotemVarieties.Value.Effects.TryGetValue(effect, out int own)
            && Monsters.Find(own.ToString()) is { Supported: true } placed) return placed;
        if (SkillsFor(conn).EffectId(effect) is { } id && TotemAuras.ContainsKey(id)
            && TotemVarieties.Value.Effects.TryGetValue(effect, out int native))
            return Monsters.Find(native.ToString()) is { Supported: true } aura ? aura : null;
        if (!SpellTotemEffect(conn, effect) && !BallistaEffect(conn, effect)) return null;
        // Prefer a dedicated native skin; generic spell/ballista proxies use intelligence/dexterity skins 3/2.
        if (TotemVarieties.Value.Effects.TryGetValue(effect, out int specific)
            && Monsters.Find(specific.ToString()) is { Supported: true } nativeSpell) return nativeSpell;
        return TotemVarieties.Value.Skins.TryGetValue(BallistaEffect(conn, effect) ? 2 : 3, out int generic)
            && Monsters.Find(generic.ToString()) is { Supported: true } spell ? spell : null;
    }

    /// <summary>Whether the totem this use would place can be placed at all. A dedicated totem skill places through the
    /// local path (TotemUseLocked) and its payload is supported; an aura executor places; a totemified projectile spell
    /// places when it has a cast time, and a native ranged ballista when its path is available (SpellTotems.cs,
    /// BallistaTotems.cs). Only a totemified skill with no executor of its own is refused, before action or cost
    /// admission.</summary>
    public static bool TotemPathAvailable(ClientState conn, ushort skill, int effect) => !IsTotemSkill(conn, skill)
        || PlacedKind(SupportedSkill(conn, skill)) is (false, false)
            && TotemVariety(conn, effect) is not null && TotemLimit(conn, skill) > 0
            && (TotemAuras.ContainsKey(SkillsFor(conn).EffectId(effect) ?? "")
                // Upstream refused every payload it did not execute, which would refuse Decoy and Devouring Totem too:
                // those are dedicated totem skills the local path places and gives their effect (taunt, corpse meal).
                // A dedicated totem skill whose row is a ranged attack totem is a ballista: it places only through the
                // ballista path, so a payload main refuses there (Focused Ballista Support off a bow) is refused here
                // too instead of being placed by the dedicated-skill clause and then kept alive by it.
                || SkillsFor(conn).HasType(effect, TotemSkillType)
                    && (GolemStat(SupportedSkill(conn, skill).Stats, "is_ranged_attack_totem") == 0
                        || BallistaPathAvailable(conn, skill))
                || SpellTotemEffect(conn, effect) && TotemCastSeconds(conn, skill) is not null
                || BallistaPathAvailable(conn, skill));

    // After placement pays its cost, the totem rises at the aimed point. Placement speed and owner action speed
    // scale this delay; the proxy's spell/weapon speed only controls its subsequent attacks (GrantedEffects CastTime,
    // 0.6 s, over totem placement speed and action speed; wiki Totem: cast speed does not apply). The wait is gated on
    // the caller's `current` predicate and on the placement skill's own source still being the same one, and it polls
    // instead of sleeping, so death, an area change, a closed connection, a re-socketed gem and the combat lifetime
    // ending all cancel it.
    // The predicate the caller passes is deliberately NOT the ordinary cast gate: that one carries the action token, and
    // live 2026-11-09 gating this wait on it dropped six of seven casts while their mana stayed paid, because the client
    // lets the player act long before a 1.0 s wait is over (it walked 0.38 s after the cast). Combat.cs passes a gate
    // that keeps the skill's own cooldown ticket and the Vaal/channel admission but not the token; what still stops a
    // stray placement is everything below. A live round should measure casts attempted against totems actually placed
    // (Agents/docs/protocol/11-skills.md §10g).
    static async Task PlaceTotem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, int level, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool> current)
    {
        double factor, speed;
        lock (conn.Monsters) factor = conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds);
        speed = Stats.Key("summon_totem_cast_speed_+%") is uint k ? SupportedStats(conn, skillId).GetValueOrDefault(k) : 0;
        // 3.28 changed ranged totem placement from 0.35 to 0.5 seconds (PoEDB version notes).
        double basis = BallistaEffect(conn, effect) ? .5 : SpellTotemEffect(conn, effect) ? .6
            : SkillsFor(conn).CastTimeMs(effect) / 1000.0;
        double seconds = basis / Math.Max(.01, 1 + speed / 100) / Math.Max(.01, factor);
        var lifetime = CombatLifetime;
        var source = SupportedSkill(conn, skillId);
        bool SourceCurrent() => SupportedSkill(conn, skillId) is var live && live.Host == source.Host
            && SameTotemSource(source, live) && IsTotemSkill(conn, skillId);
        bool Valid() => CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime) && current() && SourceCurrent()
            && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0;
        double start = AreaTime(conn), placedAt = start + seconds;
        async Task<bool> WaitUntil(double at)
        {
            while (conn.Paused || AreaTime(conn) < at)
            {
                if (!Valid()) return false;
                await AreaWait(PlayerUpdateRole.Minions, 25);
            }
            return Valid();
        }
        bool spell = SpellTotemEffect(conn, effect);
        double contact = spell ? start + seconds * Animations.SpellRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject) : placedAt;
        if (await WaitUntil(contact))
            await CreateTotem(remote, stream, ciphers, conn, skillId, effect, level, variety, at, Valid,
                spell ? () => WaitUntil(placedAt) : null);
    }

    /// <summary>
    /// Summons one totem of a skill at a point: Life from the variety's base at the gem's level requirement (PoB's totem
    /// level; the MinionLife column, as the client derives it) with totem_life_+%, duration base_totem_duration with
    /// totem_duration_+% only (wiki Totem), limit Character.ot's 1 plus number_of_additional_totems_allowed (the player's
    /// and the skill's), replacing the oldest. Its aura's stats and radius are the skill's at this moment. Null when the
    /// limit is below one (the client refuses the use itself then) or the player is gone.
    /// </summary>
    public static Task<uint?> CreateTotem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current = null) =>
        CreateTotem(remote, stream, ciphers, conn, skillId, effect, gemLevel, variety, at, current, null);

    internal static async Task<uint?> CreateTotem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current,
        Func<Task<bool>>? finishPlacement)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
        var stats = SupportedStats(conn, skillId);
        var own = SupportedSkill(conn, skillId).Stats;
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        int limit = TotemLimit(conn, skillId);
        if (limit < 1) return null;
        byte level = (byte)Math.Clamp(Get("totem_support_gem_level") is > 0 and var supportLevel
            ? supportLevel : SkillsFor(conn).PlayerLevelReq(effect, gemLevel), 1, 100);
        int baseLife = Monsters.BaseLife(variety, level) ?? 1;
        uint life = (uint)Math.Clamp((long)baseLife * (100 + Get("totem_life_+%")) / 100, 1, int.MaxValue);
        double seconds = Get("base_totem_duration") / 1000.0 * Math.Max(0, 100 + Get("totem_duration_+%")) / 100;
        TotemState.Aura? aura = null;
        if (SkillsFor(conn).EffectId(effect) is { } id && TotemAuras.TryGetValue(id, out var source))
        {
            var area = MergeSkillStats(AreaPlayerStats(conn), own);
            int Area(string name) => (name == "skill_area_of_effect_+%" ? conn.Pools.AuraAreaPct : 0)
                + (Stats.Key(name) is uint k ? area.GetValueOrDefault(k) : 0);
            var buffStats = TotemBuffStats.Value.GetValueOrDefault(source.Buff) ?? [];
            aura = new(source.Buff, SkillArea.Radius(source.Radius, Area, spell: true),
                [.. buffStats.Where(own.ContainsKey).Select(s => (s, own[s])).Where(s => s.Item2 != 0)]);
        }
        int bodySize = Math.Clamp(variety.ObjectSize - 1, -1, 127);
        (int X, int Y) place = (Math.Clamp(at.X, 0, Math.Max(0, conn.Layout.GridW - 1)), Math.Clamp(at.Y, 0, Math.Max(0, conn.Layout.GridH - 1)));
        var playerStats = CharacterPlayerStats(conn);
        bool ballista = BallistaEffect(conn, effect) && GolemStat(own, "is_ranged_attack_totem") != 0;
        bool spellTotem = (SpellTotemEffect(conn, effect) || ballista) && IsTotemSkill(conn, skillId);
        var sourceGem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId).Gem;
        var proxy = spellTotem ? TotemProxy(conn, skillId) : null;
        if (spellTotem && proxy is null) return null;
        // A totem that places its own skill (everything except an adopted projectile spell) carries the granted proxy
        // its object names for the action, with the skill's stats and area radius and no pathing, so the client can play
        // what it does; the hit that use deals is captured here, from the player's stats at this moment, and lands as an
        // instant contact at half the use's interval (TotemStrikeLocked). A spell totem flies instead and has no Strike:
        // its proxy is the one SpellTotems.cs schedules, and it keeps Effect >= 0 so only it is ticked there.
        bool spell = !SkillsFor(conn).HasType(effect, "Attack");
        int radius = SkillRadius(conn, own, spell, effect);
        GrantedSkill? action = proxy;
        TotemState.Strike? strike = null;
        if (!spellTotem)
        {
            var payload = own.Select(p => (p.Key, p.Value)).ToList();
            if (Stats.Key("active_skill_area_of_effect_radius") is uint radiusKey) payload.Add((radiusKey, radius));
            payload.Add((StatKeys.SkillIsNonPathing, 1));
            action = SkillsFor(conn).FindByRow(TotemActionSkill, effect, gemLevel, payload) is { } granted
                ? granted with { Parent = (PlayerId, skillId) } : null;
            if (action is not null && SupportedHitFor(conn, skillId, suppress: false) is { IsZero: false } hit)
            {
                double interval = Math.Max(.3, SkillsFor(conn).CastTimeMs(effect) / 1000.0);
                strike = new(TotemActionSkill, hit, radius, TotemRange, spell, interval, Math.Max(.05, interval / 2))
                { Sources = new Dictionary<uint, int>(own) };
            }
        }
        ushort grantedId = action is null ? (ushort)0 : TotemActionSkill;
        // The two totems whose effect is not a hit of their own: Devouring Totem eats the corpses near it
        // (TotemDevourLocked) and Decoy Totem draws the monsters to itself (TotemTauntLocked, read by the AI).
        string? effectId = SkillsFor(conn).EffectId(effect);
        int Perk(string name) => Get(name) is var value && value != 0 ? value : GolemStat(own, name);
        TotemState.Devour? devour = effectId == "DevouringTotem"
            ? new(Perk("corpse_consumption_life_to_gain"), Perk("corpse_consumption_mana_to_gain")) : null;
        bool taunt = effectId == "DecoyTotem";
        bool Valid() => CurrentGameWork && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0 && current?.Invoke() != false;
        (int X, int Y) ClearPosition(uint objectId)
        {
            if (bodySize < 0) return place;
            bool Floor(int x, int y) => conn.Layout.WalkableFor(x, y, bodySize) && !conn.Barrier.Contains((x, y));
            return CollisionEscape.Find(place, conn.Layout.GridW, conn.Layout.GridH, Floor, conn.MonsterPlacementBlockers(objectId),
                moverSize: bodySize) ?? place;
        }
        void Activate(uint objectId, WorldRecordBuilder bytes)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var placed = new TotemState.Totem(objectId, skillId, place, life, now + seconds, aura, strike, grantedId, devour, taunt,
                row: variety.Row, varietyId: variety.Id, level: level, effectRow: effect)
            { Effect = spellTotem ? effect : -1, SourceGem = sourceGem,
                SourceHost = SupportedSkill(conn, skillId).Host, SourceItem = SupportedSkill(conn, skillId).SourceGem };
            int ordinaryLimit = (int)Math.Clamp((long)BaseTotemsAllowed + GolemStat(playerStats, "number_of_additional_totems_allowed")
                + (GolemStat(playerStats, "keystone_ancestral_bond") != 0 ? 1 : 0), 0, int.MaxValue);
            placed.Ballista = ballista;
            placed.BonusSlots = ballista ? Math.Max(0, limit - ordinaryLimit) : 0;
            if (spellTotem) BindTotemSource(conn, placed);
            var replaced = conn.Totems.Place(placed, limit, now, ordinaryLimit);
            if (replaced.Count > 0) TotemsDiedLocked(conn, now, playerStats);
            // A totem blocks paths on the client as any body does.
            if (bodySize >= 0) conn.SetBlocker(objectId, place.X, place.Y, bodySize);
            TotemRecords(conn, bytes);
            Console.WriteLine($"[gateway] {CharacterName(conn)}: totem 0x{objectId:x} {variety}@{level} at ({place.X}, {place.Y}), "
                + $"life {life}, {seconds:0.##} s, limit {limit}, {replaced.Count} replaced"
                + (aura is null ? "" : $", aura {aura.Row} radius {aura.Radius}")
                + (spellTotem ? ", flies its spell" : grantedId == 0 ? ", no action skill" : $", action 0x{grantedId:x}")
                + (strike is null ? "" : $" radius {strike.Radius} every {strike.Interval:0.##} s")
                + (devour is null ? "" : $", devours {devour.Life} life / {devour.Mana} mana a corpse")
                + (taunt ? ", taunts" : ""));
        }

        uint? created = null;
        // Registered and created at the send gate, so the tick's presentation never sends a totem the client lacks.
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!Valid()) return null;
                uint objectId = conn.TakeObjectId();
                place = ClearPosition(objectId);
                var bytes = new WorldRecordBuilder(conn, admission);
                if (conn.MonsterTypes.Add((variety.Row, level)))
                    bytes.Immediate(InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]));
                var objectStats = new List<(uint, int)>();
                if (life != baseLife) objectStats.Add((StatKeys.MaximumLife, (int)life));
                if (spellTotem && Stats.Key("uses_no_mana") is uint noMana) objectStats.Add((noMana, 1));
                var blob = new MonsterObject(place.X, place.Y, life, variety.ComponentsOrBase, StateMachine: variety.StateMachine,
                    TransitionState: variety.TransitionStates, Team: 0, Skills: action is null ? null : [action],
                    Stats: objectStats) { LifeData = variety.LifeData };
                bytes.Create(objectId, InstanceEntities.CreateObject(objectId,
                    InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob.Blob()));
                bytes.Presentation(objectId, InstanceProtocol.EnterWakeZone(objectId));
                // It rises where it appeared: the totem base's own basic action, at once (0x1ab then 0x1ac). A totem has
                // no Transitionable component, so the monster rise's 0x2bc is not sent and the actor needs no grant.
                bytes.Presentation(objectId, InstanceProtocol.ActionStartedNow(objectId, place.X, place.Y, place.X, place.Y,
                    TotemEmergeSkill, conn.NextActionSequence(), 0x0800, objectId));
                if (finishPlacement is null) Activate(objectId, bytes);
                created = objectId;
                return bytes.Build();
            }
        }, transport: (remote, stream, ciphers));
        if (created is not uint pendingId || finishPlacement is null) return created;
        bool active = false;
        try
        {
            while (await finishPlacement())
            {
                bool paused = false;
                await SendWorldBatch(conn, [conn.WorldOutput.PublishedObject(pendingId)], admission =>
                {
                    lock (conn.Monsters)
                    {
                        if (!Valid()) return null;
                        if (conn.Paused) { paused = true; return null; }
                        limit = TotemLimit(conn, skillId);
                        if (limit < 1) return null;
                        playerStats = CharacterPlayerStats(conn);
                        var bytes = new WorldRecordBuilder(conn, admission);
                        var clear = ClearPosition(pendingId);
                        if (clear != place)
                        {
                            place = clear;
                            bytes.World(pendingId, InstanceProtocol.Teleport(pendingId, place.X, place.Y));
                        }
                        Activate(pendingId, bytes);
                        active = true;
                        return bytes.Build();
                    }
                }, transport: (remote, stream, ciphers));
                if (!paused) break;
            }
            return active ? pendingId : null;
        }
        finally
        {
            if (!active && !conn.Closed && !conn.LeavingArea)
                await SendWorldRemoval(remote, stream, ciphers, conn, pendingId, "cancelled totem placement");
        }
    }

    /// <summary>
    /// Before Ward, Energy Shield and Life take a hit (DamagePlayerHit, under the Monsters lock): the redirect stat's share of
    /// each part goes to the nearest living totem's Life, capped by it (PoB ModParser "taken from your nearest totem's life
    /// before you", HaveTotem; reducePoolsByDamage). No distance limit applies. What the player still takes.
    /// </summary>
    public static (int NonChaos, int Chaos) TotemsTakeHitLocked(ClientState conn, int nonChaos, int chaos, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        int pct = Stats.Key(TotemRedirectStat) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The totems live on the connection clock; the hit's world time places the player.
        double clock = conn.Clock.Elapsed.TotalSeconds;
        if (pct <= 0 || conn.Totems.Nearest(conn.Walk.Position(now), clock) is not { } totem) return (nonChaos, chaos);
        var (keptNonChaos, life) = TotemState.Redirect(nonChaos, pct, totem.Life);
        var (keptChaos, left) = TotemState.Redirect(chaos, pct, life);
        totem.Life = left;
        if (left == 0)
        {
            totem.DiedAt = clock;
            TotemsDiedLocked(conn, clock, stats);
        }
        return (keptNonChaos, keptChaos);
    }

    // Totems died: with a living Unique enemy within the player's presence, Spirit of Kaom triggers (its node's
    // has_trigger_on_totem_death_in_presence_of_unique; the shared Spirit cooldown decides).
    static void TotemsDiedLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.PlayerLife == 0 || !UniqueInPresenceLocked(conn, now)) return;
        TriggerSpiritChieftainLocked(conn, TotemDeathInPresenceOfUniqueTrigger, stats);
    }

    // The aura of the totem covering the living player joins his stats (CharacterPlayerStatsCore), with his aura effect.
    static void AddTotemAuraStats(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.PlayerLife == 0 || conn.Totems.AuraAt(conn.Walk.Position(now), now)?.Aura is not { } aura) return;
            foreach (var (key, value) in aura.Stats)
                stats[key] = (int)Math.Clamp(stats.GetValueOrDefault(key)
                    + Math.Round(value * Math.Max(0, 100 + conn.Pools.AuraEffectPct) / 100.0, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
        }
    }

    // The totems as the client should see them, from the player tick and after a placement: expired ones die, their Life
    // (0x1b1), the dead ones' removal (0x2bd), and the aura buff on the player (0x1c1 from the totem, 0x1c2) with his stats.
    public static byte[]? TotemPresentation(ClientState conn) => TotemRecords(conn, new(conn, null)) is { } batch
        ? [.. batch.Records.SelectMany(r => r.CopyBytes())] : null;

    static OutputBatch? TotemRecords(ClientState conn, WorldRecordBuilder bytes)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
            var totems = conn.Totems;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (totems.Expire(now).Count > 0) TotemsDiedLocked(conn, now, CharacterPlayerStats(conn));
            foreach (var t in totems.All)
            {
                uint life = t.DiedAt is null ? t.Life : 0;
                if (t.SentDeath || life == t.SentLife && life > 0) continue;
                bytes.Presentation(t.Id, InstanceProtocol.LifeUpdate(t.Id, life));
                (t.SentLife, t.SentDeath) = (life, life == 0);
            }
            foreach (var t in totems.Remove(now))
            {
                conn.RemoveBlocker(t.Id);
                bytes.Remove(t.Id);
            }
            uint source = conn.PlayerLife > 0 ? totems.AuraAt(conn.Walk.Position(now), now)?.Id ?? 0 : 0;
            if (source != totems.SentAuraTotem)
            {
                if (totems.SentAuraInstance != 0) bytes.World(PlayerId, InstanceEntities.RemoveBuff(PlayerId, totems.SentAuraInstance));
                totems.SentAuraInstance = 0;
                // shortcut: the buff names the player as its source (the shape every server buff uses), not the totem and its
                // skill; send the totem once a live check shows the client takes a monster source for a player buff.
                if (totems.All.FirstOrDefault(t => t.Id == source) is { Aura: { } aura })
                    bytes.World(PlayerId, InstanceEntities.AddBuff(PlayerId, totems.SentAuraInstance = conn.NextBuff++, aura.Row, PlayerId));
                totems.SentAuraTotem = source;
                bytes.World(PlayerId, PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
            }
            return bytes.Count == 0 ? null : bytes.Build();
        }
    }

    // A totem with a skill of its own uses it: it aims at the nearest living hostile monster in its range (0x1ab from
    // the totem, naming the granted skill its object carries, as a monster's swing names its own) and the hit lands at
    // the contact. A totem that only carries an aura (Rejuvenation Totem) has no strike and does nothing here, and so
    // has an adopted projectile spell totem: SpellTotems.cs schedules that cast instead.
    static void TotemUseLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        TotemState.Totem totem, double now, WorldRecordBuilder bytes)
    {
        if (conn.PlayerLife == 0) return;
        if (totem.Devour is { } devour) { TotemDevourLocked(remote, stream, ciphers, conn, totem, devour, now, bytes); return; }
        if (totem.Strike is not { } strike) return;
        if (strike.ContactAt is double contact && now >= contact)
        {
            TotemStrikeLocked(remote, stream, ciphers, conn, totem, strike, now);
            strike.ContactAt = null;
        }
        if (now < strike.NextUse || strike.ContactAt is not null) return;
        uint target = 0; (int X, int Y) aim = totem.At; double nearest = double.MaxValue;
        foreach (var (id, m) in conn.Monsters)
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }) continue;
            var at = m.Walk.Position(now);
            double distance = Dist(at, totem.At);
            if (distance > strike.Range || distance >= nearest) continue;
            nearest = distance; target = id; aim = at;
        }
        if (target == 0) return;
        bytes.Presentation(totem.Id,
            InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, aim.X, aim.Y, strike.GrantedId,
            0xffff, 0x0408, targetObjectId: target));
        strike.Target = target; strike.Aim = aim;
        strike.ContactAt = now + strike.Contact;
        strike.NextUse = now + strike.Interval;
    }

    // Devouring Totem: it eats the nearest corpse within its range (the engine's: a monster killed here whose Life type
    // allows corpse use and that nothing has consumed), giving the player the gem's own life and mana for it — the
    // Deathrattle warcry's corpse consumption (Warcries.cs) with Devour Support's clamp. The corpse the client holds goes
    // with 0x2bd, and the totem plays its action at it.
    static void TotemDevourLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        TotemState.Totem totem, TotemState.Devour devour, double now, WorldRecordBuilder bytes)
    {
        if (now < devour.NextUse) return;
        uint corpseId = 0; SpawnedMonster? corpse = null; (int X, int Y) at = totem.At; double nearest = double.MaxValue;
        foreach (var (id, m) in conn.Monsters)
        {
            if (!IsCorpse(m)) continue;
            var cell = m.Walk.Position(now);
            double distance = Dist(cell, totem.At);
            if (distance > TotemRange || distance >= nearest) continue;
            nearest = distance; corpseId = id; corpse = m; at = cell;
        }
        if (corpse is null) return;
        corpse.CorpseConsumed = true;
        conn.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
        conn.Monsters.Remove(corpseId);
        if (devour.Life > 0) conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, (long)conn.PlayerLife + devour.Life);
        if (devour.Mana > 0) conn.PlayerMana = (uint)Math.Min(UnreservedMana(conn), (long)conn.PlayerMana + devour.Mana);
        if (totem.GrantedId != 0)
            bytes.Presentation(totem.Id,
                InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, at.X, at.Y,
                totem.GrantedId, 0xffff, 0x0408));
        bytes.Remove(corpseId);
        bytes.Presentation(PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife));
        bytes.Presentation(PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool));
        devour.NextUse = now + 1;
        Console.WriteLine($"[gateway] totem 0x{totem.Id:x} devours corpse 0x{corpseId:x} at ({at.X}, {at.Y}): "
            + $"+{devour.Life} life, +{devour.Mana} mana (player {conn.PlayerLife}/{conn.PlayerMaxLife})");
    }

    /// <summary>The taunting totem a monster at a point is drawn to: the nearest living one within
    /// <see cref="TotemRange"/> cells, or null. The monster AI reads this so its chase and its hit go to the totem
    /// instead of the player (Decoy Totem). Under the Monsters lock.</summary>
    internal static uint? TotemTauntLocked(ClientState conn, (int X, int Y) at, double now)
        => conn.Totems.Living(now).Where(t => t.Taunt && Dist(t.At, at) <= TotemRange)
            .OrderBy(t => (long)(t.At.X - at.X) * (t.At.X - at.X) + (long)(t.At.Y - at.Y) * (t.At.Y - at.Y))
            .Select(t => (uint?)t.Id).FirstOrDefault();

    /// <summary>The cell of a living totem, or null when that id is not (or no longer) one of them. The monster AI reads
    /// it to walk a taunted monster to the totem it goes for. Under the Monsters lock.</summary>
    internal static (int X, int Y)? TotemAtLocked(ClientState conn, uint id, double now)
        => conn.Totems.All.FirstOrDefault(t => t.Id == id && t.Alive(now))?.At;

    /// <summary>A monster's whole hit on a totem (a taunt's contact): its damage goes to the totem's Life, which is the
    /// redirect stat's 100% case (the wiki's Decoy Totem takes the hits it draws). At 0 the totem dies like any other;
    /// damage past its Life is lost, as a totem takes no overflow. Under the Monsters lock.</summary>
    internal static void TotemTakesMonsterHitLocked(ClientState conn, uint totemId, int damage, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.Totems.All.FirstOrDefault(t => t.Id == totemId) is not { } totem || !totem.Alive(now)) return;
        var (_, left) = TotemState.Redirect(damage, 100, totem.Life);
        totem.Life = left;
        if (left == 0) { totem.DiedAt = now; TotemsDiedLocked(conn, now, stats); }
    }

    // A totem's use lands at its contact: every living hostile monster within the skill's radius of the point the use
    // was aimed at takes that skill's hit (the player's damage for it, as a placed trap's), mitigated as any hit and
    // with its ailments. The totem's kills give the player their experience but are not his for his on-kill effects.
    // The lab's ledger does not record this hit yet: NoteTotemOutgoing and the ledger it feeds are their own change
    // (see the integration note), so "did the totem's attack land?" is answered by /totems and the totem's Life for now.
    static void TotemStrikeLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        TotemState.Totem totem, TotemState.Strike strike, double now)
    {
        var player = CharacterPlayerStats(conn);
        foreach (var (id, m) in conn.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                || Dist(m.Walk.Position(now), strike.Aim) > strike.Radius) continue;
            var rolled = strike.Hit.Roll(Random.Shared);
            var mitigated = DamageModel.Mitigate(rolled,
                type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesMitigated(m, mitigated, strike.Spell, now);
            if (damage <= 0) continue;
            if (ApplyMonsterHitLocked(conn, m, damage, allyAt: totem.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "totem", playerKill: false);
            if (!m.Dead)
                m.Ailments.Inflict(mitigated, strike.Spell, m.MaxLife, now,
                    n => GolemStat(strike.Sources, n) + GolemStat(player, n), n => m.Effects[n], Random.Shared);
        }
    }

    // The totems as the client should see them, from the player tick and after a placement: the living ones with a
    // skill of their own use it, the expired ones die, their Life (0x1b1), the dead ones' removal (0x2bd), and the aura
    // buff on the player (0x1c1 from the totem, 0x1c2) with his stats. An adopted projectile spell totem uses nothing
    // here: it has no Strike and SendTotems ticks its cast first (SpellTotems.cs).

    /// <summary>A dedicated totem skill strikes, devours, taunts and can be hit; a totemified spell flies through
    /// <see cref="TickSpellTotemsLocked"/> instead, which keys on <c>Effect >= 0</c>, so neither double-casts the other's
    /// totems.</summary>
    static void TickTotemUsesLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, WorldRecordBuilder bytes)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var t in conn.Totems.Living(now).Where(t => t.Effect < 0).ToArray())
            TotemUseLocked(remote, stream, ciphers, conn, t, now, bytes);
    }

    /// <summary>The same tick, taking the lock and dropping the records: what each totem's own use does to the world.</summary>
    public static void TickTotemUses(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters) TickTotemUsesLocked(remote, stream, ciphers, conn, new WorldRecordBuilder(conn, null));
    }

    static Task SendTotems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Paused || conn.Closed || conn.LeavingArea) return null;
                var bytes = new WorldRecordBuilder(conn, admission);
                TickSpellTotemsLocked(remote, stream, ciphers, conn, bytes);
                TickTotemUsesLocked(remote, stream, ciphers, conn, bytes);
                return TotemRecords(conn, bytes);
            }
        }, transport: (remote, stream, ciphers));
}
