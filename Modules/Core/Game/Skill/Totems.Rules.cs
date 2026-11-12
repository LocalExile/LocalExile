using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core;

public static partial class Game
{
    /// <summary>"X% of Damage from Hits is taken from your nearest Totem's Life before you": Forest of Ancestors' 20 and
    /// Kaom's Leadership's 25 add.</summary>
    public const string TotemRedirectStat = "damage_removed_from_your_nearest_totem_before_life_or_es_%";

    /// <summary>Metadata/Characters/Character.ot: base_number_of_totems_allowed 1 and base_presence_radius 80 (the wiki's
    /// fixed 8 metres "in your presence"). The client has both from the .ot; the server's stats do not carry them.</summary>
    public const int BaseTotemsAllowed = 1, PresenceRadius = 80;

    /// <summary>Totem aura executors. Unsupported totem payloads are refused at preflight.</summary>
    static readonly Dictionary<string, (ushort Buff, int Radius)> TotemAuras = new() { ["RejuvenationTotem"] = (172, 10) };

    // GrantedEffects row → MonsterVarieties row of its default skin: ActiveSkills.SkillTotemId is SkillTotemVariations'
    // SkillTotemsKey, TotemSkinId 0 (Rejuvenation Totem: 4 → Metadata/Monsters/Totems/LifeRegenTotem).
    static readonly Lazy<Dictionary<int, int>> TotemVarieties = new(LoadTotemVarieties);

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

    static Dictionary<int, int> LoadTotemVarieties()
    {
        var result = new Dictionary<int, int>();
        if (!ClientTables.Has("GrantedEffects") || !ClientTables.Has("ActiveSkills") || !ClientTables.Has("SkillTotemVariations")) return result;
        var skins = new Dictionary<int, int>();
        foreach (var v in ClientTables.Table("SkillTotemVariations"))
            if (v.Int("TotemSkinId") == 0 && v.IntOrNull("MonsterVarietiesKey") is int row)
                skins.TryAdd(v.Int("SkillTotemsKey"), row);
        var skills = ClientTables.Table("ActiveSkills");
        foreach (var e in ClientTables.Table("GrantedEffects"))
            if (e.IntOrNull("ActiveSkill") is int a && a < skills.Count
                && skills[a].IntOrNull("SkillTotemId") is int totem && skins.TryGetValue(totem, out int variety))
                result[e.Index] = variety;
        return result;
    }

    /// <summary>The totem variety a gem skill with a totem executor summons, or null.</summary>
    public static bool IsTotemSkill(ClientState conn, ushort skill) =>
        SupportedSkill(conn, skill).Stats.GetValueOrDefault(Stats.Key("is_totem") ?? 0) != 0;

    public static MonsterCatalog.Variety? TotemVariety(ClientState conn, int effect)
    {
        if (SkillsFor(conn).EffectId(effect) is { } id && TotemAuras.ContainsKey(id)
            && TotemVarieties.Value.TryGetValue(effect, out int native))
            return Monsters.Find(native.ToString()) is { Supported: true } aura ? aura : null;
        if (!SpellTotemEffect(conn, effect) && !BallistaEffect(conn, effect)) return null;
        // Prefer a dedicated native skin; generic spell/ballista proxies use intelligence/dexterity skins 3/2.
        if (TotemVarieties.Value.TryGetValue(effect, out int specific)
            && Monsters.Find(specific.ToString()) is { Supported: true } nativeSpell) return nativeSpell;
        int totemKey = BallistaEffect(conn, effect) ? 2 : 3;
        foreach (var skin in ClientTables.Table("SkillTotemVariations"))
            if (skin.Int("SkillTotemsKey") == totemKey && skin.Int("TotemSkinId") == 0)
                return Monsters.Find(skin.Int("MonsterVarietiesKey").ToString()) is { Supported: true } spell ? spell : null;
        return null;
    }

    public static bool TotemPathAvailable(ClientState conn, ushort skill, int effect) => !IsTotemSkill(conn, skill)
        || PlacedKind(SupportedSkill(conn, skill)) is (false, false)
            && TotemVariety(conn, effect) is not null && TotemLimit(conn, skill) > 0
            && (TotemAuras.ContainsKey(SkillsFor(conn).EffectId(effect) ?? "")
                || SpellTotemEffect(conn, effect) && TotemCastSeconds(conn, skill) is not null
                || BallistaPathAvailable(conn, skill));

    // After placement pays its cost, the totem rises at the aimed point. Placement speed and owner action speed
    // scale this delay; the proxy's spell/weapon speed only controls its subsequent attacks.
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
        double release = conn.Clock.Elapsed.TotalSeconds + seconds;
        while (conn.Paused || conn.Clock.Elapsed.TotalSeconds < release)
        {
            if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || !SourceCurrent()
                || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || !SourceCurrent()
            || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
        await CreateTotem(remote, stream, ciphers, conn, skillId, effect, level, variety, at,
            () => ReferenceEquals(lifetime, CombatLifetime) && current() && SourceCurrent());
    }

    /// <summary>
    /// Summons one totem of a skill at a point: Life from the variety's base at the gem's level requirement (PoB's totem
    /// level; the MinionLife column, as the client derives it) with totem_life_+%, duration base_totem_duration with
    /// totem_duration_+% only (wiki Totem), limit Character.ot's 1 plus number_of_additional_totems_allowed (the player's
    /// and the skill's), replacing the oldest. Its aura's stats and radius are the skill's at this moment. Null when the
    /// limit is below one (the client refuses the use itself then) or the player is gone.
    /// </summary>
    public static async Task<uint?> CreateTotem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current = null)
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
        uint? created = null;
        // Registered and created at the send gate, so the tick's presentation never sends a totem the client lacks.
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || current?.Invoke() == false) return null;
                uint objectId = conn.TakeObjectId();
                if (bodySize >= 0)
                {
                    bool Floor(int cx, int cy) => conn.Layout.WalkableFor(cx, cy, bodySize) && !conn.Barrier.Contains((cx, cy));
                    if (CollisionEscape.Find(place, conn.Layout.GridW, conn.Layout.GridH, Floor, conn.MonsterPlacementBlockers(objectId),
                        moverSize: bodySize) is { } clear) place = clear;
                }
                double now = conn.Clock.Elapsed.TotalSeconds;
                var placed = new TotemState.Totem(objectId, skillId, place, life, now + seconds, aura)
                { Effect = spellTotem ? effect : -1, SourceGem = sourceGem,
                    SourceHost = SupportedSkill(conn, skillId).Host, SourceItem = SupportedSkill(conn, skillId).SourceGem };
                int ordinaryLimit = (int)Math.Clamp((long)BaseTotemsAllowed + GolemStat(playerStats, "number_of_additional_totems_allowed"), 0, int.MaxValue);
                placed.Ballista = ballista;
                placed.BonusSlots = ballista ? Math.Max(0, limit - ordinaryLimit) : 0;
                if (spellTotem) BindTotemSource(conn, placed);
                var replaced = conn.Totems.Place(placed, limit, now, ordinaryLimit);
                if (replaced.Count > 0) TotemsDiedLocked(conn, now, playerStats);
                // A totem blocks paths on the client as any body does.
                if (bodySize >= 0) conn.SetBlocker(objectId, place.X, place.Y, bodySize);
                var bytes = new WorldRecordBuilder(conn, admission);
                if (conn.MonsterTypes.Add((variety.Row, level)))
                    bytes.Immediate(InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]));
                var objectStats = new List<(uint, int)>();
                if (life != baseLife) objectStats.Add((StatKeys.MaximumLife, (int)life));
                if (spellTotem && Stats.Key("uses_no_mana") is uint noMana) objectStats.Add((noMana, 1));
                var blob = new MonsterObject(place.X, place.Y, life, variety.ComponentsOrBase, StateMachine: variety.StateMachine,
                    TransitionState: variety.TransitionStates, Team: 0, Skills: proxy is null ? null : [proxy],
                    Stats: objectStats) { LifeData = variety.LifeData };
                bytes.Create(objectId, InstanceEntities.CreateObject(objectId,
                    InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob.Blob()));
                bytes.Presentation(objectId, InstanceProtocol.EnterWakeZone(objectId));
                TotemRecords(conn, bytes);
                Console.WriteLine($"[gateway] {CharacterName(conn)}: totem 0x{objectId:x} {variety}@{level} at ({place.X}, {place.Y}), "
                    + $"life {life}, {seconds:0.##} s, limit {limit}, {replaced.Count} replaced"
                    + (aura is null ? "" : $", aura {aura.Row} radius {aura.Radius}"));
                created = objectId;
                return bytes.Build();
            }
        }, transport: (remote, stream, ciphers));
        return created;
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

    static Task SendTotems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Paused || conn.Closed || conn.LeavingArea) return null;
                var bytes = new WorldRecordBuilder(conn, admission);
                TickSpellTotemsLocked(remote, stream, ciphers, conn, bytes);
                return TotemRecords(conn, bytes);
            }
        }, transport: (remote, stream, ciphers));
}
