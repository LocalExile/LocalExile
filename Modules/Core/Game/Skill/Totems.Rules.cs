using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core;
using GameServer.Core.Projectiles;
using DamageModel = GameServer.Core.World.DamageModel;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadTotemsRulesData()
    {
        TotemVarieties = LoadTotemVarieties();
        TotemBuffStats = LoadTotemBuffStats();
    }

    /// <summary>"X% of Damage from Hits is taken from your nearest Totem's Life before you": Forest of Ancestors' 20 and
    /// Kaom's Leadership's 25 add.</summary>
    public const uint TotemRedirectStat = Stat.DamageRemovedFromYourNearestTotemBeforeLifeOrEsPct;

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

    // GrantedEffects row → MonsterVarieties row of its default skin: ActiveSkills.SkillTotemId is SkillTotemVariations'
    // SkillTotemsKey, TotemSkinId 0 (Rejuvenation Totem: 4 → Metadata/Monsters/Totems/LifeRegenTotem).
    static (Dictionary<int, int> Effects, Dictionary<int, int> Skins) TotemVarieties = default!;

    // The totem auras' BuffDefinitions stats (172: base_life_regeneration_rate_per_minute and five more); the totem's
    // skill gives their values.
    static Dictionary<ushort, uint[]> TotemBuffStats = default!;

    static Dictionary<ushort, uint[]> LoadTotemBuffStats()
    {
        var result = new Dictionary<ushort, uint[]>();
        if (!ClientTables.Has("BuffDefinitions")) return result;
        var definitions = ClientTables.Table("BuffDefinitions");
        foreach (var (row, _) in TotemAuras.Values)
            if (row < definitions.Count && !definitions[row].IsNull("StatsKeys"))
                result[row] = [.. definitions[row].Ints("StatsKeys").Select(k => (uint)(k + 1))];
        return result;
    }

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
    public static bool IsTotemSkill(Player player, ushort skill) =>
        SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.IsTotem) != 0;

    /// <summary>The totem variety a gem skill summons, or null. A dedicated totem skill (its ActiveSkills row carries
    /// <see cref="TotemSkillType"/>) summons the sprite its own SkillTotemVariations entry (skin 0) names — every
    /// gem-granted totem skill places, damaging or not. An aura executor summons its native skin. A skill that Spell
    /// Totem Support totemifies is not a dedicated row: it takes a dedicated spell skin when the tables have one and the
    /// generic intelligence skin otherwise.</summary>
    public static MonsterCatalog.Variety? TotemVariety(Player player, int effect)
    {
        if (SkillsFor(player).HasType(effect, TotemSkillType)
            && TotemVarieties.Effects.TryGetValue(effect, out int own)
            && Monsters.Find(own.ToString()) is { Supported: true } placed) return placed;
        if (SkillsFor(player).EffectId(effect) is { } id && (TotemAuras.ContainsKey(id) || NativeProjectileTotem(player, effect))
            && TotemVarieties.Effects.TryGetValue(effect, out int native))
            return Monsters.Find(native.ToString()) is { Supported: true } aura ? aura : null;
        if (!SpellTotemEffect(player, effect) && !BallistaEffect(player, effect)) return null;
        // Prefer a dedicated native skin; generic spell/ballista proxies use intelligence/dexterity skins 3/2.
        if (TotemVarieties.Effects.TryGetValue(effect, out int specific)
            && Monsters.Find(specific.ToString()) is { Supported: true } nativeSpell) return nativeSpell;
        return TotemVarieties.Skins.TryGetValue(BallistaEffect(player, effect) ? 2 : 3, out int generic)
            && Monsters.Find(generic.ToString()) is { Supported: true } spell ? spell : null;
    }

    /// <summary>Whether the totem this use would place can be placed at all. A dedicated totem skill places through the
    /// local path (TotemUseLocked) and its payload is supported; an aura executor places; a totemified projectile spell
    /// places when it has a cast time, and a native ranged ballista when its path is available (SpellTotems.cs,
    /// BallistaTotems.cs). Only a totemified skill with no executor of its own is refused, before action or cost
    /// admission.</summary>
    public static bool TotemPathAvailable(Player player, ushort skill, int effect) => !IsTotemSkill(player, skill)
        || PlacedKind(SupportedSkill(player, skill)) is (false, false)
            && TotemVariety(player, effect) is not null && TotemLimit(player, skill) > 0
            && (TotemAuras.ContainsKey(SkillsFor(player).EffectId(effect) ?? "")
                || NativeProjectileTotem(player, effect) && ResolveProjectileDefinition(effect) is not null
                // A dedicated totem skill the local path runs (TotemUseLocked): its own hit, the corpse meal or the taunt.
                // A ranged attack totem is a ballista and Holy Flame Totem flies its own projectile: both place only
                // through their own clause, so what that clause refuses (Focused Ballista off a bow) stays refused, and
                // the hit lookup below is never paid on SpellTotemCurrent's per-tick check for them.
                || SkillsFor(player).HasType(effect, TotemSkillType) && !NativeProjectileTotem(player, effect)
                    && GolemStat(SupportedSkill(player, skill).Stats, Stat.IsRangedAttackTotem) == 0
                    && TotemDoesSomething(player, effect)
                || SpellTotemEffect(player, effect) && TotemCastSeconds(player, skill) is not null
                || BallistaPathAvailable(player, skill));

    /// <summary>Whether a dedicated totem skill has something this module runs behind it: an aura
    /// (<see cref="TotemAuras"/>), Devouring Totem's corpse meal, or a nova spell that hits around the totem
    /// (Shockwave Totem: ActiveSkillType Nova). Read from the skill data, not the player's hit, so Ancestral Bond's
    /// "deal no damage yourself" does not refuse it. This also admits Decoy Totem's taunt. Ancestral melee totems and
    /// Searing Bond are still refused before cost.</summary>
    static bool TotemDoesSomething(Player player, int effect) =>
        TotemAuras.ContainsKey(SkillsFor(player).EffectId(effect) ?? "")
        || SkillsFor(player).EffectId(effect) is "DevouringTotem" or "DecoyTotem"
        || TotemNova(player, effect);

    static bool TotemNova(Player player, int effect) => SkillsFor(player).HasType(effect, "Nova")
        && !SkillsFor(player).IsAttack(effect) && SkillHitPlan.ForSkill(SkillsFor(player), effect, 1).CanHit;

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
    static double TotemPlacementSeconds(Player player, ushort skillId, int effect)
    {
        double factor = player.ElementalAilments.ActionFactor(AreaTime(player));
        double speed = SupportedStats(player, skillId).GetValueOrDefault(Stat.SummonTotemCastSpeedPlusPct);
        // 3.28 changed ranged totem placement from 0.35 to 0.5 seconds (PoEDB version notes).
        double basis = BallistaEffect(player, effect) ? .5 : SpellTotemEffect(player, effect) || NativeProjectileTotem(player, effect) ? .6
            : SkillsFor(player).CastTimeMs(effect) / 1000.0;
        return basis / Math.Max(.01, 1 + speed / 100) / Math.Max(.01, factor);
    }

    static async Task PlaceTotem(Player player,
        ushort skillId, int effect, int level, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool> current)
    {
        double seconds = TotemPlacementSeconds(player, skillId, effect);
        var lifetime = CombatLifetime;
        var source = SupportedSkill(player, skillId);
        bool SourceCurrent() => SupportedSkill(player, skillId) is var live && live.Host == source.Host
            && SameLinkedSkillSource(source, live) && IsTotemSkill(player, skillId);
        bool Valid() => CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime) && current() && SourceCurrent()
            && !player.Closed && !player.LeavingArea && player.PlayerLife > 0;
        double start = AreaTime(player), placedAt = start + seconds;
        async Task<bool> WaitUntil(double at)
        {
            while (player.Paused || AreaTime(player) < at)
            {
                if (!Valid()) return false;
                await AreaWait(PlayerUpdateRole.Minions, 25);
            }
            return Valid();
        }
        if (source.Supports.Any(s => s.Effect.Id is "SupportMultipleTotems" or "SupportMultipleTotemsRoyale"))
        {
            if (await WaitUntil(placedAt))
                await CreateTotems(player, skillId, effect, level, variety, at, Valid, finishPlacement: null, count: 2);
            return;
        }
        bool spell = SpellTotemEffect(player, effect);
        double contact = spell ? start + seconds * Animations.SpellRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject) : placedAt;
        if (await WaitUntil(contact))
            await CreateTotem(player, skillId, effect, level, variety, at, Valid,
                spell ? () => WaitUntil(placedAt) : null);
    }

    /// <summary>
    /// Summons one totem of a skill at a point: Life from the variety's base at the gem's level requirement (PoB's totem
    /// level; the MinionLife column, as the client derives it) with totem_life_+%, duration base_totem_duration with
    /// totem_duration_+% only (wiki Totem), limit Character.ot's 1 plus number_of_additional_totems_allowed (the player's
    /// and the skill's), replacing the oldest. Its aura's stats and radius are the skill's at this moment. Null when the
    /// limit is below one (the client refuses the use itself then) or the player is gone.
    /// </summary>
    public static Task<uint?> CreateTotem(Player player,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current = null) =>
        CreateTotem(player, skillId, effect, gemLevel, variety, at, current, null);

    internal static Task<uint?> CreateTotem(Player player,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current,
        Func<Task<bool>>? finishPlacement)
        => CreateTotems(player, skillId, effect, gemLevel, variety, at, current, finishPlacement, count: 1);

    static async Task<uint?> CreateTotems(Player player,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current,
        Func<Task<bool>>? finishPlacement, int count)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
        var stats = SupportedStats(player, skillId);
        var own = SupportedSkill(player, skillId).Stats;
        int Get(uint k) => stats.GetValueOrDefault(k);
        int limit = TotemLimit(player, skillId);
        if (limit < 1) return null;
        byte level = (byte)Math.Clamp(Get(Stat.TotemSupportGemLevel) is > 0 and var supportLevel
            ? supportLevel : SkillsFor(player).PlayerLevelReq(effect, gemLevel), 1, 100);
        int baseLife = Monsters.BaseLife(variety, level) ?? 1;
        bool decoy = SkillsFor(player).EffectId(effect) == "DecoyTotem";
        uint life = (uint)Math.Clamp((long)baseLife * (100 + Get(Stat.TotemLifePlusPct)) / 100, 1, int.MaxValue);
        double seconds = Get(Stat.BaseTotemDuration) / 1000.0 * Math.Max(0, 100 + Get(Stat.TotemDurationPlusPct)) / 100;
        TotemState.Aura? aura = null;
        if (SkillsFor(player).EffectId(effect) is { } id && TotemAuras.TryGetValue(id, out var source))
        {
            var area = MergeSkillStats(AreaPlayerStats(player), own);
            int Area(uint name) => (name == Stat.SkillAreaOfEffectPlusPct ? player.Pools.AuraAreaPct : 0)
                + area.GetValueOrDefault(name);
            var buffStats = TotemBuffStats.GetValueOrDefault(source.Buff) ?? [];
            aura = new(source.Buff, SkillArea.Radius(source.Radius, Area, spell: true),
                [.. buffStats.Where(own.ContainsKey).Select(s => (s, own[s])).Where(s => s.Item2 != 0)]);
        }
        int bodySize = Math.Clamp(variety.ObjectSize - 1, -1, 127);
        (int X, int Y) requested = (Math.Clamp(at.X, 0, Math.Max(0, player.Layout.GridW - 1)),
            Math.Clamp(at.Y, 0, Math.Max(0, player.Layout.GridH - 1)));
        var place = requested;
        var playerStats = CharacterPlayerStats(player);
        bool ballista = BallistaEffect(player, effect) && GolemStat(own, Stat.IsRangedAttackTotem) != 0;
        bool spellTotem = (SpellTotemEffect(player, effect) || ballista || NativeProjectileTotem(player, effect)) && IsTotemSkill(player, skillId);
        var sourceGem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId).Gem;
        var proxy = spellTotem ? TotemProxy(player, skillId) : null;
        if (spellTotem && proxy is null) return null;
        // A totem that places its own skill (everything except an adopted projectile spell) carries the granted proxy
        // its object names for the action, with the skill's stats and area radius and no pathing, so the client can play
        // what it does. A nova (Shockwave Totem) hits around the totem at the end of each cast (TotemNovaLocked); a
        // spell totem flies instead and has no Strike: SpellTotems.cs ticks it (Effect >= 0).
        int radius = SkillRadius(player, own, spell: true, effect);
        GrantedSkill? action = proxy;
        TotemState.Strike? strike = null;
        if (!spellTotem && TotemProxy(player, skillId) is { } granted)
        {
            // The spell totems' proxy (totem flags cleared, cast time and speed its own): with the gem's own stats
            // (is_totem, base_skill_is_totemified) the client takes the action for a totem placement and plays nothing.
            var payload = (granted.Stats ?? []).ToList();
            if (Stat.ActiveSkillAreaOfEffectRadius is var radiusKey) payload.Add((radiusKey, radius));
            payload.Add((Stat.SkillIsNonPathing, 1));
            action = granted with { Stats = payload };
            if (TotemNova(player, effect)) strike = new(TotemActionSkill, radius);
        }
        ushort grantedId = action is null ? (ushort)0 : TotemActionSkill;
        // Devouring Totem's effect is not a hit: it eats the corpses near it (TotemDevourLocked).
        int Perk(uint name) => Get(name) is var value && value != 0 ? value : GolemStat(own, name);
        TotemState.Devour? devour = SkillsFor(player).EffectId(effect) == "DevouringTotem"
            ? new(Perk(Stat.CorpseConsumptionLifeToGain), Perk(Stat.CorpseConsumptionManaToGain)) : null;
        bool Valid() => CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0 && current?.Invoke() != false;
        (int X, int Y) ClearPosition(uint objectId)
        {
            if (bodySize < 0) return place;
            bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, bodySize) && !player.Barrier.Contains((x, y));
            return CollisionEscape.Find(place, player.Layout.GridW, player.Layout.GridH, Floor, player.MonsterPlacementBlockers(objectId),
                moverSize: bodySize) ?? place;
        }
        void Activate(uint objectId, WorldRecordBuilder bytes)
        {
            double now = AreaTime(player);
            var ownStrike = strike is null ? null : new TotemState.Strike(strike.GrantedId, strike.Radius);
            var ownDevour = devour is null ? null : new TotemState.Devour(devour.Life, devour.Mana);
            var placed = new TotemState.Totem(objectId, skillId, place, life, now + seconds, aura, ownStrike, grantedId, ownDevour,
                row: variety.Row, varietyId: variety.Id, level: level, effectRow: effect)
            { Effect = spellTotem ? effect : -1, SourceGem = sourceGem,
                SourceHost = SupportedSkill(player, skillId).Host, SourceItem = SupportedSkill(player, skillId).SourceGem,
                DecoyRadius = decoy && grantedId != 0 ? TotemTargetRange(player, skillId) : 0, BodySize = Math.Max(0, bodySize) };
            int ordinaryLimit = (int)Math.Clamp((long)BaseTotemsAllowed + GolemStat(playerStats, Stat.NumberOfAdditionalTotemsAllowed)
                + (GolemStat(playerStats, Stat.KeystoneAncestralBond) != 0 ? 1 : 0), 0, int.MaxValue);
            placed.Ballista = ballista;
            placed.BonusSlots = ballista ? Math.Max(0, limit - ordinaryLimit) : 0;
            if (spellTotem) BindTotemSource(player, placed);
            var replaced = player.Totems.Place(placed, limit, now, ordinaryLimit);
            StartTotemTimer(player);
            if (replaced.Count > 0) TotemsDiedLocked(player, now, playerStats);
            // A totem blocks paths on the client as any body does.
            if (bodySize >= 0) player.SetBlocker(objectId, place.X, place.Y, bodySize);
            if (NativeProjectileTotem(player, effect) && seconds > 0)
            {
                int groundRadius = SkillRadius(player, 16, own, spell: true, effect);
                player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, place.X, place.Y, groundRadius,
                    AreaTime(player) + seconds, QueueGroundArtLocked(player, FlaskGrounds.Kind.ConsecratedGround, place, groundRadius),
                    ConsecratedEnemyDamageTakenNow(player)) { SourceAlive = placed.Alive, GrantsCurseImmunity = Get(Stat.ConsecratedGroundImmuneToCurses) != 0 });
            }
            Console.WriteLine($"[gateway] {CharacterName(player)}: totem 0x{objectId:x} {variety}@{level} at ({place.X}, {place.Y}), "
                + $"life {life}, {seconds:0.##} s, limit {limit}, {replaced.Count} replaced"
                + (aura is null ? "" : $", aura {aura.Row} radius {aura.Radius}")
                + (spellTotem ? ", flies its spell" : grantedId == 0 ? ", no action skill" : $", action 0x{grantedId:x}")
                + (strike is null ? "" : $", nova radius {strike.Radius}")
                + (devour is null ? "" : $", devours {devour.Life} life / {devour.Mana} mana a corpse"));
        }

        uint? created = null;
        // Registered and created at the send gate, so the tick's presentation never sends a totem the client lacks.
        await SendWorldBatch(player, [], admission =>
        {
            if (!Valid()) return null;
            if (count > 1)
            {
                limit = TotemLimit(player, skillId);
                if (limit < 1) return null;
                playerStats = CharacterPlayerStats(player);
            }
            var bytes = new WorldRecordBuilder(player, admission);
            if (player.MonsterTypes.Add((variety.Row, level)))
                bytes.Immediate(InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]));
            var objectStats = new List<(uint, int)>();
            if (life != baseLife) objectStats.Add((Stat.MaximumLife, (int)life));
            if (spellTotem && Stat.UsesNoMana is var noMana) objectStats.Add((noMana, 1));
            for (int i = 0; i < Math.Min(count, limit); i++)
            {
                uint objectId = player.TakeObjectId();
                place = requested;
                place = ClearPosition(objectId);
                var blob = new MonsterObject(place.X, place.Y, life, variety.ComponentsOrBase, StateMachine: variety.StateMachine,
                    TransitionState: variety.TransitionStates, Team: 0, Skills: action is null ? null : [action],
                    Stats: objectStats) { LifeData = variety.LifeData };
                bytes.Create(objectId, InstanceEntities.CreateObject(objectId,
                    InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob.Blob()));
                bytes.Presentation(objectId, InstanceProtocol.EnterWakeZone(objectId));
                // The Emerge action needs neither a Transitionable component nor a granted actor skill.
                if (Emerges.SkillId is ushort emerge)
                    bytes.Presentation(objectId, InstanceProtocol.ActionStartedNow(objectId, place.X, place.Y, place.X, place.Y,
                        emerge, player.NextActionSequence(), 0x0800, objectId));
                if (finishPlacement is null) Activate(objectId, bytes);
                created ??= objectId;
            }
            if (finishPlacement is null) TotemRecords(player, bytes);
            return bytes.Build();
        });
        if (created is not uint pendingId || finishPlacement is null) return created;
        bool active = false;
        try
        {
            while (await finishPlacement())
            {
                bool paused = false;
                await SendWorldBatch(player, [player.Connection.WorldOutput.PublishedObject(pendingId)], admission =>
                {
                    if (!Valid()) return null;
                    if (player.Paused) { paused = true; return null; }
                    limit = TotemLimit(player, skillId);
                    if (limit < 1) return null;
                    playerStats = CharacterPlayerStats(player);
                    var bytes = new WorldRecordBuilder(player, admission);
                    var clear = ClearPosition(pendingId);
                    if (clear != place)
                    {
                        place = clear;
                        bytes.World(pendingId, InstanceProtocol.Teleport(pendingId, place.X, place.Y));
                    }
                    Activate(pendingId, bytes);
                    TotemRecords(player, bytes);
                    active = true;
                    return bytes.Build();
                });
                if (!paused) break;
            }
            return active ? pendingId : null;
        }
        finally
        {
            if (!active && !player.Closed && !player.LeavingArea)
                await SendWorldRemoval(player, pendingId, "cancelled totem placement");
        }
    }

    /// <summary>
    /// Before Ward, Energy Shield and Life take a hit (DamagePlayerHit, in the area's frame): the redirect stat's share of
    /// each part goes to the nearest living totem's Life, capped by it (PoB ModParser "taken from your nearest totem's life
    /// before you", HaveTotem; reducePoolsByDamage). No distance limit applies. What the player still takes.
    /// </summary>
    public static (int NonChaos, int Chaos) TotemsTakeHitLocked(Player player, int nonChaos, int chaos, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        int pct = TotemRedirectStat is var key ? stats.GetValueOrDefault(key) : 0;
        // The totems live on the connection clock; the hit's world time places the player.
        double clock = AreaTime(player);
        if (pct <= 0 || player.Totems.Nearest(player.Walk.Position(now), clock) is not { } totem) return (nonChaos, chaos);
        var (keptNonChaos, life) = TotemState.Redirect(nonChaos, pct, totem.Life);
        var (keptChaos, left) = TotemState.Redirect(chaos, pct, life);
        totem.Life = left;
        if (left == 0)
        {
            totem.DiedAt = clock;
            MarkPlayerStats(player, PlayerStatStep.TotemAura);
            TotemsDiedLocked(player, clock, stats);
        }
        return (keptNonChaos, keptChaos);
    }

    // Totems died: with a living Unique enemy within the player's presence, Spirit of Kaom triggers (its node's
    // has_trigger_on_totem_death_in_presence_of_unique; the shared Spirit cooldown decides).
    static void TotemsDiedLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (player.PlayerLife == 0 || !UniqueInPresenceLocked(player, now)) return;
        TriggerSpiritChieftainLocked(player, TotemDeathInPresenceOfUniqueTrigger, stats);
    }

    // The zones of the totems' auras (one per totem, at it) reaching the player; the strongest covering him applies.
    const ZoneKind TotemAuraZones = ZoneKind.TotemAuras;

    static TotemState.Totem? TotemAuraCovering(Player player, double now) => player.Totems.Living(now)
        .Where(t => t.Aura is not null && InZone(player, player.Id, TotemAuraZones, t.Id, player.Id))
        .OrderByDescending(t => t.Aura!.Stats.Sum(s => (long)s.Value)).FirstOrDefault();

    // The aura of the totem covering the living player joins his stats (CharacterPlayerStatsCore), with his aura effect.
    static void AddTotemAuraStats(Player player, StatMap stats)
    {
        double now = AreaTime(player);
        var covering = TotemAuraCovering(player, now);
        // The player walking in or out of an aura is its zone's event; a totem's end is timed.
        double? next = null;
        foreach (var totem in player.Totems.Living(now))
            if (totem.Aura is not null) next = Math.Min(next ?? double.PositiveInfinity, totem.Until);
        if (next is double changes) StatsChangeIn(changes - now);
        if (player.PlayerLife == 0 || covering?.Aura is not { } aura) return;
        foreach (var (key, value) in aura.Stats)
            stats[key] = (int)Math.Clamp(stats.GetValueOrDefault(key)
                + Math.Round(value * Math.Max(0, 100 + player.Pools.AuraEffectPct) / 100.0, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
    }

    // The totems as the client should see them, from the totems' timer and after a placement: expired ones die, their Life
    // (0x1b1), the dead ones' removal (0x2bd), and the aura buff on the player (0x1c1 from the totem, 0x1c2) with his stats.
    public static byte[]? TotemPresentation(Player player) => TotemRecords(player, new(player, null)) is { } batch
        ? [.. batch.Records.SelectMany(r => r.CopyBytes())] : null;

    static OutputBatch? TotemRecords(Player player, WorldRecordBuilder bytes)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
        var totems = player.Totems;
        double now = AreaTime(player);
        if (totems.Expire(now).Count > 0) TotemsDiedLocked(player, now, CharacterPlayerStats(player));
        foreach (var t in totems.All) player.Area.Life.Set(t.Id, AreaLife.LifePool, t.DiedAt is null ? t.Life : 0);
        foreach (var t in totems.Remove(now))
        {
            player.RemoveBlocker(t.Id);
            bytes.Remove(t.Id);
        }
        SyncZones(player, player.Id, TotemAuraZones, totems.Living(now).Where(t => t.Aura is not null)
                .Select(t => (t.Id, t.Aura!.Radius, t.At)),
            new(ReachesOnly(player.Id), Changed: _ => MarkPlayerStats(player, PlayerStatStep.TotemAura)));
        uint source = player.PlayerLife > 0 ? TotemAuraCovering(player, now)?.Id ?? 0 : 0;
        if (source != totems.SentAuraTotem)
        {
            if (totems.SentAuraInstance != 0) player.Area.Buffs.Clear(player.Id, totems.SentAuraInstance, player.Id, tag: (ushort)BuffTag.TotemAura);
            totems.SentAuraInstance = 0;
            // shortcut: the buff names the player as its source (the shape every server buff uses), not the totem and its
            // skill; send the totem once a live check shows the client takes a monster source for a player buff.
            if (totems.All.FirstOrDefault(t => t.Id == source) is { Aura: { } aura })
            {
                // The aura's row is kept to end it (SentAuraInstance).
                totems.SentAuraInstance = aura.Row;
                player.Area.Buffs.Set(player.Id, new(aura.Row, player.Id, Tag: (ushort)BuffTag.TotemAura));
            }
            totems.SentAuraTotem = source;
            bytes.World(player.Id, PlayerStatsOffFrames(player));
        }
        return bytes.Count == 0 ? null : bytes.Build();
    }

    // A totem with a skill of its own uses it. Shockwave Totem (a nova) casts while a targetable hostile is within its
    // totem range (base_totem_range, as the other totems read it: TotemTargetRange) and hits around itself at the end of
    // each cast; Devouring Totem eats a corpse. An aura totem (Rejuvenation) has neither, and an adopted projectile spell
    // totem is ticked by SpellTotems.cs instead.
    // The nova's contact is scheduled on the projectile runtime, as the Arc totem's release is: a hit can kill, and a kill
    // takes the XP gate, which goes before the area's frame (ProjectileMutationScope). This tick holds only the Monsters
    // lock, so a kill committed here deadlocked against any scope waiting on it (live 2026-11-17).
    static void TotemUseLocked(Player player,
        TotemState.Totem totem, double now, WorldRecordBuilder bytes)
    {
        if (player.PlayerLife == 0) return;
        if (totem.DecoyRadius > 0) { DecoyTotemUseLocked(player, totem, now, bytes); return; }
        if (totem.Devour is { } devour) { TotemDevourLocked(player, totem, devour, now, bytes); return; }
        if (totem.Strike is not { } strike || now < strike.NextCast) return;
        double range = TotemTargetRange(player, totem.SkillId);
        if (!player.Area.Monsters.Values.Any(m => TotemTargetable(m) && Dist(m.Walk.Position(now), totem.At) <= range)) return;
        if (TotemCastSeconds(player, totem.SkillId) is not double seconds) return;
        var session = ProjectilesFor(player);
        ushort sequence = ++totem.Sequence;
        strike.NextCast = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, totem.At.X, totem.At.Y,
            strike.GrantedId, sequence, 0x0408));
        var lifetime = CombatLifetime;
        session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(strike.NextCast)), () =>
        {
            if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && !player.Closed
                && totem.Alive(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond))
                TotemNovaLocked(player, session, totem, strike, sequence);
        });
    }

    static bool TotemTargetable(SpawnedMonster m) =>
        m is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, OwnerId: null, Asleep: false };

    // Devouring Totem: while the player is missing Life or Mana it eats the nearest corpse within its totem range once
    // per cast (1.0 s base, over its cast speed), giving the gem's own life and mana. The corpse is the totem's, not the
    // player's: as for a totem's summon (SpellTotems.cs, playerConsumesCorpse: false) it meets no "consumed a corpse"
    // condition of his. The corpse the client holds goes with 0x2bd, and the totem plays its action at it.
    static void TotemDevourLocked(Player player, TotemState.Totem totem, TotemState.Devour devour, double now,
        WorldRecordBuilder bytes)
    {
        if (now < devour.NextUse) return;
        bool life = devour.Life > 0 && player.PlayerLife < player.PlayerMaxLife;
        bool mana = devour.Mana > 0 && player.PlayerMana < UnreservedMana(player);
        if (!life && !mana) return;
        var playerStats = CharacterPlayerStats(player);
        life &= !LifeRecovery.NonLeechBlocked(name => GolemStat(playerStats, name));
        mana &= GolemStat(playerStats, Stat.Of("cannot_recover_mana")) == 0;
        if (!life && !mana) return;
        double range = TotemTargetRange(player, totem.SkillId);
        uint corpseId = 0; SpawnedMonster? corpse = null; (int X, int Y) at = totem.At; double nearest = double.MaxValue;
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (!IsCorpse(m)) continue;
            var cell = m.Walk.Position(now);
            double distance = Dist(cell, totem.At);
            if (distance > range || distance >= nearest) continue;
            nearest = distance; corpseId = id; corpse = m; at = cell;
        }
        if (corpse is null) return;
        ConsumeSummonCorpseLocked(player, corpse, playerConsumesCorpse: false);
        player.Area.Monsters.Remove(corpseId);
        if (life) player.PlayerLife = (uint)Math.Min(player.PlayerMaxLife, (long)player.PlayerLife + devour.Life);
        if (mana) player.PlayerMana = (uint)Math.Min(UnreservedMana(player), (long)player.PlayerMana + devour.Mana);
        if (totem.GrantedId != 0)
            bytes.Presentation(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, at.X, at.Y,
                totem.GrantedId, ++totem.Sequence, 0x0408));
        if (!corpse.CreatedAsCorpse) bytes.Remove(corpseId);
        bytes.Presentation(player.Id, InstanceProtocol.LifeUpdate(player.Id, player.PlayerLife, player.PlayerReservedLife));
        bytes.Presentation(player.Id, InstanceProtocol.LifeUpdate(player.Id, player.PlayerMana, (uint)ReservedMana(player), pool: ManaPool));
        devour.NextUse = now + (TotemCastSeconds(player, totem.SkillId) ?? 1);
        Console.WriteLine($"[gateway] totem 0x{totem.Id:x} devours corpse 0x{corpseId:x} at ({at.X}, {at.Y}): "
            + $"+{(life ? devour.Life : 0)} life, +{(mana ? devour.Mana : 0)} mana (player {player.PlayerLife}/{player.PlayerMaxLife})");
    }

    // A nova's contact, on the projectile runtime (under the XP gate and the area's frame): every targetable hostile
    // within its radius of the totem takes the spell's hit, worked out now from the owner's stats as a totem's
    // (TotemCasterStats: Ancestral Bond's "no damage yourself" is the owner's, not the totem's), in the Arc totem's steps
    // (ArcTotems.cs): block, crit, penetration, the 0x1af, ailments. The totem's kills give the player their experience
    // but are not his for his on-kill effects.
    static void TotemNovaLocked(Player player, ProjectileSession session, TotemState.Totem totem, TotemState.Strike strike,
        ushort sequence)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var skill = SupportedSkill(player, totem.SkillId);
        if (SupportedHitInputsFor(player, totem.SkillId, area: true, totem: true) is not { } inputs) return;
        var caster = TotemCasterStats(CharacterPlayerStats(player));
        var blind = BlindHitInput.Capture(caster, skill.Stats);
        var hit = new SkillDamageContext(SkillHitKind.Spell, caster).Hit(SupportedHitFrom(inputs, suppress: false));
        if (hit.IsZero) return;
        var criticalRoll = CaptureSkillCriticalRoll(player, totem.SkillId);
        var penetration = Penetration.Of(caster, Stats, false, skill.Stats);
        var resistance = HitResistMods(caster, skill.Stats);
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            var targetAt = m.Walk.Position(now);
            if (!TotemTargetable(m) || MonsterAsleep(player, id)
                || Dist(targetAt, totem.At) > strike.Radius || MonsterBlocks(m, AreaRng(player), spell: true)) continue;
            var rolled = WithConditionalSupportDamageLocked(player, skill.Stats, false, m,
                GameEvents.WithOutgoingDamage(player, caster, m, hit.Roll(AreaRng(player))), now);
            int chance = CriticalChanceOn(player, id, SupportedCriticalChance(player, totem.SkillId),
                SupportedCriticalChanceVsBlinded(player, totem.SkillId, false), SupportedCriticalChanceVsGround(player, totem.SkillId, false));
            bool crit = RollCriticalStrike(player, chance, skill.Stats, attack: false, skillRoll: criticalRoll, playerStats: caster);
            var (damage, _, mitigated) = MonsterHitDamage(m, rolled, crit, false, now, resistance, penetration,
                GamblerFor(caster).InvertsResistance(AreaRng(player)), AreaRng(player));
            if (crit) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, SupportedCriticalMultiplier(player, totem.SkillId)));
            if (ApplyMonsterHitLocked(player, m, damage, allyAt: totem.At) is not { } accepted) continue;
            if (accepted.StageStarts) { CommitProjectileBossStage(session, id, m, session.Runtime.Now); accepted = accepted with { StageStarts = false }; }
            if (!m.Dead && damage > 0)
                AilmentsOnHitLocked(player, id, m, mitigated, true, crit, 100, caster, skill.Stats, now, AreaRng(player),
                    blind: blind, hitOrigin: totem.At, attackerSize: totem.BodySize, attackerHasReversedStat: false);
            byte hitCode = HitCode(mitigated, damage, m.MaxLife, crit || accepted.Life == 0);
            ushort hitFlags = (ushort)(0x4200 | HitTypeFlag(hitCode) | (crit ? 0x0020 : 0));
            byte hitKind = 3, hitAngle = HitAngle(targetAt.X - totem.At.X, targetAt.Y - totem.At.Y);
            ConsumeNativeKnockbackLocked(m, ref hitFlags, ref hitKind, ref hitAngle, out ushort u3c, out ushort u3e);
            session.World(totem.Id, InstanceProtocol.ActionHit(totem.Id, sequence, id, flags: hitFlags,
                kind: hitKind, code: hitCode, angle: hitAngle, u3c: u3c, u3e: u3e));
            CommitAcceptedHitLocked(player, id, m, accepted, damage, now, "totem", playerKill: false);
        }
    }

    // The totems as the client should see them, from the totems' timer and after a placement: the living ones with a
    // skill of their own use it, the expired ones die, their Life (0x1b1), the dead ones' removal (0x2bd), and the aura
    // buff on the player (0x1c1 from the totem, 0x1c2) with his stats. An adopted projectile spell totem uses nothing
    // here: it has no Strike and SendTotems ticks its cast first (SpellTotems.cs).

    /// <summary>A dedicated totem skill novas or devours; a totemified spell flies through
    /// <see cref="TickSpellTotemsLocked"/> instead, which keys on <c>Effect >= 0</c>, so neither double-casts the other's
    /// totems.</summary>
    static void TickTotemUsesLocked(Player player, WorldRecordBuilder bytes)
    {
        double now = AreaTime(player);
        foreach (var t in player.Totems.Living(now).Where(t => t.Effect < 0).ToArray())
            TotemUseLocked(player, t, now, bytes);
    }

    /// <summary>The same tick, taking the lock and dropping the records: what each totem's own use does to the world.</summary>
    public static void TickTotemUses(Player player)
    {
        TickTotemUsesLocked(player, new WorldRecordBuilder(player, null));
    }

    // While the player has totems, every frame: their casts and uses, expiry, removal, life and aura.
    static readonly RuleTable<PlayerUpdatesState, object> TotemTimers = new();

    static void StartTotemTimer(Player player)
    {
        if (player.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        if (TotemTimers.TryGetValue(updates, out _)) return;
        TotemTimers.AddOrUpdate(updates, new());
        EveryFrameAsPlayer(player, () =>
        {
            if (player.Totems.All.Count > 0 && PlayerUpdatesCurrent(player, updates)) return true;
            TotemTimers.Remove(updates);
            return false;
        }, SendTotems);
    }

    static Task SendTotems(Player player) =>
        SendWorldBatch(player, [], admission =>
        {
            if (!CurrentGameWork || player.Paused || player.Closed || player.LeavingArea) return null;
            var bytes = new WorldRecordBuilder(player, admission);
            TickSpellTotemsLocked(player, bytes);
            TickTotemUsesLocked(player, bytes);
            return TotemRecords(player, bytes);
        });
}
