using System.Buffers.Binary;
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

    /// <summary>The MinionType.dat row (TotemMinion) a placed totem is listed under in its owner's minion roster.</summary>
    const ushort TotemMinionType = 5;

    /// <summary>The totem skills that also carry an aura: Rejuvenation Totem's (BuffDefinitions 172
    /// totem_aura_life_regen, base radius 10, wiki Rejuvenation Totem) regenerates the player's Life. A totem skill that
    /// is not here still places its totem and uses its own skill (TotemUseLocked), or casts that skill through the
    /// ordinary projectile runtime when it is an adopted projectile spell (SpellTotems.cs); only the aura is special.</summary>
    const string VaalRejuvenationTotemEffect = "VaalRejuvenationTotem";
    static readonly Dictionary<string, (ushort Buff, int Radius)> TotemAuras = new()
    {
        ["RejuvenationTotem"] = (172, 10),
        [VaalRejuvenationTotemEffect] = (2201, 10),
    };

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
                // through their own clause, so what that clause refuses (a ballista off a bow) stays refused, and
                // the hit lookup below is never paid on SpellTotemCurrent's per-tick check for them.
                || SkillsFor(player).HasType(effect, TotemSkillType) && !NativeProjectileTotem(player, effect)
                    && GolemStat(SupportedSkill(player, skill).Stats, Stat.IsRangedAttackTotem) == 0
                    && (TotemDoesSomething(player, effect) || IsSearingBond(player, effect))
                || SpellTotemEffect(player, effect) && TotemCastSeconds(player, skill) is not null
                || BallistaPathAvailable(player, skill));

    /// <summary>Whether a dedicated totem skill has something this module runs behind it: an aura
    /// (<see cref="TotemAuras"/>), Devouring Totem's corpse meal, or a nova spell that hits around the totem
    /// (Shockwave Totem: ActiveSkillType Nova). Read from the skill data so Ancestral Bond admits it.
    /// This also admits Decoy Totem's taunt.</summary>
    static bool TotemDoesSomething(Player player, int effect) =>
        TotemAuras.ContainsKey(SkillsFor(player).EffectId(effect) ?? "")
        || SkillsFor(player).EffectId(effect) is "DevouringTotem" or "DecoyTotem" or VaalAncestralWarchiefEffect
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
        // 3.28 changed ranged totem placement from 0.35 to 0.5 seconds (PoEDB version notes); all other placed totems
        // share a 0.6 s base placement time (Decoy/Devouring Totem's 1000 ms CastTime is their proxy action, not placement).
        bool ballista = BallistaEffect(player, effect)
            && GolemStat(SupportedSkill(player, skillId).Stats, Stat.IsRangedAttackTotem) != 0;
        double basis = SkillsFor(player).EffectId(effect) == VaalRejuvenationTotemEffect
            ? SkillsFor(player).CastTimeMs(effect) / 1000d : ballista ? .5 : .6;
        return basis / Math.Max(.01, 1 + speed / 100) / Math.Max(.01, factor);
    }

    static double TotemDurationFactor(int increased) => Math.Max(0, 1 + increased / 100d);

    static async Task PlaceTotem(Player player,
        ushort skillId, int effect, int level, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool> current, int count)
    {
        double seconds = TotemPlacementSeconds(player, skillId, effect);
        var lifetime = CombatLifetime;
        var source = SupportedSkill(player, skillId);
        bool SourceCurrent() => SupportedSkill(player, skillId) is var live && live.Host == source.Host
            && SameLinkedSkillSource(source, live) && IsTotemSkill(player, skillId);
        bool Valid() => CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime) && current() && SourceCurrent()
            && !player.Closed && !player.LeavingArea && player.PlayerLife > 0;
        double start = AreaTime(player), placedAt = start + seconds;
        async Task<bool> WaitUntil(double until)
        {
            while (true)
            {
                if (!Valid()) return false;
                double remaining = until - AreaTime(player);
                if (!player.Paused && remaining <= 0) return true;
                int waitMs = player.Paused ? 25
                    : Math.Clamp((int)Math.Ceiling(Math.Min(0.025, remaining) * 1000), 1, 25);
                await AreaWait(PlayerUpdateRole.Minions, waitMs);
            }
        }
        if (count == 2)
        {
            if (await WaitUntil(placedAt))
                await CreateTotems(player, skillId, effect, level, variety, at, Valid, finishPlacement: null, count: 2);
            return;
        }
        bool twoStage = seconds > 0
            && (SpellTotemEffect(player, effect) || NativeProjectileTotem(player, effect)
                || SkillsFor(player).HasType(effect, TotemSkillType) || BallistaEffect(player, effect));
        double contact = twoStage
            ? start + seconds * Animations.SpellRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject)
            : placedAt;
        if (await WaitUntil(contact))
            await CreateTotem(player, skillId, effect, level, variety, at, Valid,
                twoStage ? () => WaitUntil(placedAt) : null);
    }

    const int TotemPlacementSearchRadius = 20;

    static int OrdinaryTotemLimit(IReadOnlyDictionary<uint, int> playerStats) =>
        (int)Math.Clamp((long)BaseTotemsAllowed + GolemStat(playerStats, Stat.NumberOfAdditionalTotemsAllowed)
            + (GolemStat(playerStats, Stat.KeystoneAncestralBond) != 0 ? 1 : 0), 0, int.MaxValue);

    static IReadOnlyList<GameServer.Core.World.ClientPathfinder.Blocker> TotemPlacementBlockers(Player player,
        uint objectId, bool ballista, int group, int bonusSlots, int limit, int? ordinaryLimit, double now)
    {
        var blockers = player.MonsterPlacementBlockers(objectId);
        List<GameServer.Core.World.ClientPathfinder.Blocker>? skip = null;
        foreach (var t in player.Totems.All)
            if (t.DiedAt is null && !t.Alive(now))
                (skip ??= []).Add(new(t.At.X, t.At.Y, t.BodySize));
        foreach (var t in player.Totems.Replacements(limit, now, ordinaryLimit, ballista, group, bonusSlots))
            if (t.DiedAt is null)
                (skip ??= []).Add(new(t.At.X, t.At.Y, t.BodySize));
        if (skip is null) return blockers;
        var filtered = new List<GameServer.Core.World.ClientPathfinder.Blocker>(blockers.Count);
        for (int i = 0; i < blockers.Count; i++)
        {
            int match = skip.IndexOf(blockers[i]);
            if (match >= 0) skip.RemoveAt(match);
            else filtered.Add(blockers[i]);
        }
        return new GameServer.Core.World.BlockerSet(filtered);
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
        int moreLifePerTotem = Get(Stat.StygianSpireTotemLifePlusPctFinalPerMaximumTotem);
        double moreLife = (moreLifePerTotem == 0 ? 1
            : Math.Max(0, 1 + moreLifePerTotem * (double)TotemAllowance(player, effect, own, stats) / 100))
            * Math.Max(0, 1 + Get(Stat.TotemLifePlusPctFinal) / 100d);
        uint life = (uint)Math.Clamp(baseLife * (100L + Get(Stat.TotemLifePlusPct)) / 100d * moreLife, 1, int.MaxValue);
        double seconds = Get(Stat.BaseTotemDuration) / 1000d * TotemDurationFactor(Get(Stat.TotemDurationPlusPct));
        TotemState.Aura? aura = null;
        if (SkillsFor(player).EffectId(effect) is { } id && TotemAuras.TryGetValue(id, out var source))
        {
            var area = MergeSkillStats(AreaPlayerStats(player), own);
            int Area(uint name) => (name == Stat.SkillAreaOfEffectPlusPct ? player.Pools.AuraAreaPct : 0)
                + area.GetValueOrDefault(name);
            var buffStats = TotemBuffStats.GetValueOrDefault(source.Buff) ?? [];
            aura = new(source.Buff, SkillArea.Radius(source.Radius, Area, spell: true),
                [.. buffStats.Where(own.ContainsKey).Select(s => (s, own[s])).Where(s => s.Item2 != 0)])
            {
                HitRedirectPercent = own.GetValueOrDefault(Stat.VaalRejuvenationTotemPctDamageTakenAppliedToTotemInstead),
            };
        }
        int bodySize = Math.Clamp(variety.ObjectSize - 1, -1, 127);
        (int X, int Y) requested = (Math.Clamp(at.X, 0, Math.Max(0, player.Layout.GridW - 1)),
            Math.Clamp(at.Y, 0, Math.Max(0, player.Layout.GridH - 1)));
        bool searingBond = IsSearingBond(player, effect);
        int placementGroup = searingBond ? SearingBondPlacementGroup : 0;
        var place = requested;
        var playerStats = CharacterPlayerStats(player);
        bool SingleTotem() => GolemStat(playerStats, Stat.Maximum1TotemAtATime) != 0;
        int? SharedOrdinaryLimit() => SingleTotem() ? null : OrdinaryTotemLimit(playerStats);
        bool ballista = BallistaEffect(player, effect) && GolemStat(own, Stat.IsRangedAttackTotem) != 0;
        bool spellTotem = (SpellTotemEffect(player, effect) || ballista || NativeProjectileTotem(player, effect)
            || SkillsFor(player).EffectId(effect) == VaalAncestralWarchiefEffect) && IsTotemSkill(player, skillId);
        var sourceGem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId).Gem;
        var proxy = spellTotem ? TotemProxy(player, skillId) : null;
        if (spellTotem && proxy is null) return null;
        int BonusSlots(int ordinary, bool singleTotem) =>
            (ballista || searingBond) && !singleTotem ? Math.Max(0, limit - ordinary) : 0;
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
            int width = player.Layout.GridW, height = player.Layout.GridH;
            if (width <= 0 || height <= 0) return place;
            double now = AreaTime(player);
            bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, bodySize) && !player.Barrier.Contains((x, y));
            int ordinary = OrdinaryTotemLimit(playerStats);
            bool singleTotem = SingleTotem();
            var blockers = TotemPlacementBlockers(player, objectId, ballista, placementGroup,
                BonusSlots(ordinary, singleTotem), limit, singleTotem ? null : ordinary, now);
            if (CollisionEscape.Clear(place, width, height, Floor, blockers, moverSize: bodySize)) return place;
            if (CollisionEscape.Find(place, width, height, Floor, blockers,
                maxRadius: TotemPlacementSearchRadius, moverSize: bodySize) is { } nearAim) return nearAim;
            var around = player.Walk.Position(now);
            around = (Math.Clamp(around.X, 0, width - 1), Math.Clamp(around.Y, 0, height - 1));
            if (CollisionEscape.Clear(around, width, height, Floor, blockers, moverSize: bodySize)) return around;
            return CollisionEscape.Find(around, width, height, Floor, blockers,
                maxRadius: TotemPlacementSearchRadius, moverSize: bodySize) ?? around;
        }
        bool onslaughtChanged = false;
        void Activate(uint objectId, WorldRecordBuilder bytes)
        {
            double now = AreaTime(player);
            var ownStrike = strike is null ? null : new TotemState.Strike(strike.GrantedId, strike.Radius);
            var ownDevour = devour is null ? null : new TotemState.Devour(devour.Life, devour.Mana);
            var placed = new TotemState.Totem(objectId, skillId, place, life, now + seconds, aura, ownStrike, grantedId, ownDevour,
                row: variety.Row, varietyId: variety.Id, level: level, effectRow: effect)
            { Effect = spellTotem ? effect : -1, SourceGem = sourceGem,
                SourceHost = SupportedSkill(player, skillId).Host, SourceItem = SupportedSkill(player, skillId).SourceGem,
                DecoyRadius = decoy && grantedId != 0 ? TotemTargetRange(player, skillId) : 0, BodySize = Math.Max(0, bodySize),
                PlacementGroup = placementGroup };
            int ordinaryLimit = OrdinaryTotemLimit(playerStats);
            bool singleTotem = SingleTotem();
            if (searingBond) SearingBondSources.AddOrUpdate(placed, new(own));
            placed.Ballista = ballista;
            placed.BonusSlots = BonusSlots(ordinaryLimit, singleTotem);
            if (spellTotem) BindTotemSource(player, placed);
            BindFocusedBallista(player, placed);
            var replaced = player.Totems.Place(placed, limit, now, singleTotem ? null : ordinaryLimit);
            // The owner's minion roster lists a placed totem, as the official servers do; the client's totem counter counts
            // these. Written where the totem enters its state, so a cancelled placement never lists one, and in the same
            // batch as the removal of a replaced one (TotemRecords).
            bytes.World(player.Id, InstanceEntities.PlayerMinionsChange(player.Id, [new(objectId, skillId, TotemMinionType)],
                CanDetonateMines(player)));
            onslaughtChanged |= TotemSummonedLocked(player, placed.At, now, playerStats);
            StartTotemTimer(player);
            if (replaced.Count > 0)
            {
                foreach (var old in replaced)
                {
                    player.RemoveBlocker(old.Id);
                    StopTotemChannel(old, bytes);
                    StopSearingBond(old, bytes);
                    TotemChannels.Remove(old);
                }
                PresentNearby(player);
                TotemsDiedLocked(player, now, playerStats);
            }
            // A totem blocks paths on the client as any body does.
            if (bodySize >= 0) player.SetObjectBlocker(objectId, place.X, place.Y, bodySize, true);
            if (NativeProjectileTotem(player, effect) && seconds > 0)
            {
                var groundStats = SkillAreaStats(player, own, effect, playerStats);
                int bonusArea = groundStats.GetValueOrDefault(Stat.HolyFlameTotemConsecratedGroundAreaPlusPct);
                if (bonusArea != 0)
                    groundStats[SkillArea.Increase] = groundStats.GetValueOrDefault(SkillArea.Increase) + bonusArea;
                int groundRadius = SkillArea.Radius(16, key => groundStats.GetValueOrDefault(key), spell: true);
                player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, place.X, place.Y, groundRadius,
                    now + seconds, QueueGroundArtLocked(player, FlaskGrounds.Kind.ConsecratedGround, place, groundRadius),
                    ConsecratedEnemyDamageTakenNow(player)) { SourceAlive = placed.Alive, GrantsCurseImmunity = Get(Stat.ConsecratedGroundImmuneToCurses) != 0 });
                PresentNearby(player);
            }
            Console.WriteLine($"[gateway] {CharacterName(player)}: totem 0x{objectId:x} {variety}@{level} at ({place.X}, {place.Y}), "
                + $"life {life}, {seconds:0.##} s, limit {limit}, {replaced.Count} replaced"
                + (aura is null ? "" : $", aura {aura.Row} radius {aura.Radius}")
                + (spellTotem ? ", flies its spell" : grantedId == 0 ? ", no action skill" : $", action 0x{grantedId:x}")
                + (strike is null ? "" : $", nova radius {strike.Radius}")
                + (devour is null ? "" : $", devours {devour.Life} life / {devour.Mana} mana a corpse"));
        }

        await EnsureMonsterTypeSent(player, variety, level, Valid);
        uint? created = null;
        // The objects are registered and created at the send gate, so the tick's presentation never sends a totem the client lacks.
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
                    Stats: objectStats) { LifeData = variety.LifeData, Chain = variety.Chain };
                bytes.Create(objectId, InstanceEntities.CreateObject(objectId,
                    InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob.Blob()));
                bytes.Presentation(objectId, InstanceProtocol.EnterWakeZone(objectId));
                // The Emerge action needs neither a Transitionable component nor a granted actor skill.
                if (Emerges.SkillId is ushort emerge)
                    bytes.Presentation(objectId, InstanceProtocol.ActionStartedNow(objectId, place.X, place.Y, place.X, place.Y,
                        emerge, player.NextActionSequence(), 0x0800, objectId));
                if (finishPlacement is null) Activate(objectId, bytes);
                else player.Totems.PendingPlacements.Add(objectId);
                created ??= objectId;
            }
            if (finishPlacement is null)
            {
                DetonateSearingBondsLocked(player, playerStats, limit, SharedOrdinaryLimit(), bytes);
                TotemRecords(player, bytes);
            }
            return bytes.Build();
        });
        if (onslaughtChanged) await SendKillOnslaught(player);
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
                    if (!player.Totems.PendingPlacements.Remove(pendingId)) return null;
                    Activate(pendingId, bytes);
                    DetonateSearingBondsLocked(player, playerStats, limit, SharedOrdinaryLimit(), bytes);
                    TotemRecords(player, bytes);
                    active = true;
                    return bytes.Build();
                });
                if (!paused) break;
            }
            if (onslaughtChanged) await SendKillOnslaught(player);
            return active ? pendingId : null;
        }
        finally
        {
            bool removePending = false;
            if (!active && !player.Closed && !player.LeavingArea)
                using (ProjectileMutation(player, advance: false))
                    removePending = player.Totems.PendingPlacements.Remove(pendingId);
            if (removePending)
                await SendWorldRemoval(player, pendingId, "cancelled totem placement");
        }
    }

    /// <summary>The potential placements per action. A skill with a cooldown spends one stored use per totem.</summary>
    public static int TotemsPerUse(Player player, ushort skillId) =>
        CooldownForSkill(player, skillId) is not { Uses: 1 } && WantsTwoTotems(player, skillId) ? 2 : 1;

    static bool WantsTwoTotems(Player player, ushort skillId)
    {
        var own = SupportedSkill(player, skillId);
        return GolemStat(own.Stats, Stat.Summon2Totems) != 0
            || own.Supports.Any(s => s.Effect.Id is "SupportMultipleTotems" or "SupportMultipleTotemsRoyale")
            || Random.Shared.Next(100) < GolemStat(SupportedStats(player, skillId), Stat.ChanceToSummonTwoTotemsPct);
    }

    static bool TotemSummonedLocked(Player player, (int X, int Y) at, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        player.RecentEvents.Record(StatConditions.SummonedTotem, now);
        if (stats.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPctOnPlacingTotem) != 0)
        {
            MarkPlayerStats(player, PlayerStatStep.Sources);
            PresentStats(player);
        }
        if (stats.GetValueOrDefault(Stat.GainArcaneSurgeWhenYouSummonATotem) != 0)
            GainArcaneSurgeLocked(player, now);

        const int hinderRadius = 30;
        if (stats.GetValueOrDefault(Stat.TotemHinderNearbyEnemiesWhenSummonedWith25PctReducedMovementSpeed) != 0)
            foreach (var (_, enemy) in player.Area.MonstersNear(at, hinderRadius))
                if (enemy.Hostile && !enemy.Dead && enemy.Life > 0 && !enemy.Invulnerable
                    && Dist(enemy.Walk.Position(now), at) <= hinderRadius)
                    ApplyHinderLocked(enemy, now, fromTotem: true);

        return stats.GetValueOrDefault(Stat.HasOnslaughtIfTotemSummonedRecently) != 0
            && KillOnslaughtOf(player).Gain(now, RecentlySeconds) && Marked(player, PlayerStatStep.Onslaught);
    }

    /// <summary>
    /// Before Ward, Energy Shield and Life take a hit (DamagePlayerHit, in the area's frame): the redirect stat's share of
    /// each part goes to the nearest living totem's Life, capped by it (PoB ModParser "taken from your nearest totem's life
    /// before you", HaveTotem; reducePoolsByDamage). No distance limit applies. What the player still takes.
    /// </summary>
    public static (int NonChaos, int Chaos) TotemsTakeHitLocked(Player player, int nonChaos, int chaos, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        int pct = TotemRedirectStat is var key ? Math.Clamp(stats.GetValueOrDefault(key), 0, 100) : 0;
        // The totems live on the connection clock; the hit's world time places the player.
        double clock = AreaTime(player);
        var at = player.Walk.Position(now);
        var rejuvenation = RejuvenationTotemAt(player, at);
        int vrtPct = rejuvenation is { } src ? RejuvenationRedirectPct(src.Owner, src.Totem.Aura!) : 0;
        var nearest = pct > 0 ? player.Totems.Nearest(at, clock) : null;
        if (rejuvenation is { } source && vrtPct > 0)
        {
            if (nearest is not null && ReferenceEquals(nearest, source.Totem))
                return RedirectTotemHitLocked(source.Owner, source.Totem, nonChaos, chaos,
                    Math.Min(100, vrtPct + pct), AreaTime(source.Owner), source.Owner == player ? stats : null);
            var afterVrt = RedirectTotemHitLocked(source.Owner, source.Totem, nonChaos, chaos,
                vrtPct, AreaTime(source.Owner), source.Owner == player ? stats : null);
            int remainingPct = Math.Min(pct, Math.Max(0, 100 - vrtPct));
            if (nearest is null || remainingPct <= 0) return afterVrt;
            var afterNearest = RedirectTotemHitLocked(player, nearest, nonChaos, chaos, remainingPct, clock, stats);
            int takenNonChaos = (nonChaos - afterVrt.NonChaos) + (nonChaos - afterNearest.NonChaos);
            int takenChaos = (chaos - afterVrt.Chaos) + (chaos - afterNearest.Chaos);
            return (Math.Max(0, nonChaos - takenNonChaos), Math.Max(0, chaos - takenChaos));
        }
        return nearest is not null
            ? RedirectTotemHitLocked(player, nearest, nonChaos, chaos, pct, clock, stats) : (nonChaos, chaos);
    }

    static (int NonChaos, int Chaos) RedirectTotemHitLocked(Player player, TotemState.Totem totem,
        int nonChaos, int chaos, int pct, double clock, IReadOnlyDictionary<uint, int>? stats)
    {
        var (keptNonChaos, life) = TotemState.Redirect(nonChaos, pct, totem.Life);
        var (keptChaos, left) = TotemState.Redirect(chaos, pct, life);
        totem.Life = left;
        if (left == 0)
        {
            totem.DiedAt = clock;
            player.RemoveBlocker(totem.Id);
            MarkPlayerStats(player, PlayerStatStep.TotemAura);
            PresentNearby(player);
            SyncRejuvenationAllyZones(player, clock);
            TotemsDiedLocked(player, clock, stats ?? PlayerStatValues(player));
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
        var covering = ReceivedTotemAura(player, now);
        // The player walking in or out of an aura is its zone's event; a totem's end is timed.
        double? next = null;
        foreach (var totem in player.Totems.Living(now))
            if (totem.Aura is not null) next = Math.Min(next ?? double.PositiveInfinity, totem.Until);
        if (next is double changes) StatsChangeIn(changes - now);
        if (VaalWarchiefMeleeBonus(player, now) is > 0 and var melee)
            stats[Stat.SlamAncestorTotemGrantedMeleeDamagePlusPctFinal] = melee;
        if (player.PlayerLife == 0 || covering is not { Totem.Aura: { } aura } source) return;
        if (source.Owner != player) StatsChangeIn(Math.Max(0, source.Totem.Until - AreaTime(source.Owner)));
        foreach (var (key, value) in aura.Stats)
            stats[key] = (int)Math.Clamp(stats.GetValueOrDefault(key)
                + TotemAuraStat(source.Owner, value), int.MinValue, int.MaxValue);
    }

    // The totems as the client should see them, from the totems' timer and after a placement: expired ones die, their Life
    // (0x1b1), the dead ones' roster removal (0x2bc) and body removal (0x2bd), and the aura buff on the player (0x1c1 from
    // the totem, 0x1c2) with his stats.
    public static byte[]? TotemPresentation(Player player) => TotemRecords(player, new(player, null)) is { } batch
        ? [.. batch.Records.SelectMany(r => r.CopyBytes())] : null;

    static OutputBatch? TotemRecords(Player player, WorldRecordBuilder bytes)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
        var totems = player.Totems;
        double now = AreaTime(player);
        var expired = totems.Expire(now);
        if (expired.Count > 0)
        {
            foreach (var t in expired) player.RemoveBlocker(t.Id);
            PresentNearby(player);
            TotemsDiedLocked(player, now, CharacterPlayerStats(player));
        }
        foreach (var t in totems.All)
        {
            if (t.DiedAt is not null)
            {
                player.RemoveBlocker(t.Id);
                StopTotemChannel(t, bytes);
                StopSearingBond(t, bytes);
                TotemChannels.Remove(t);
                // Source 0 takes it off the roster; once, in the batch that carries its death.
                if (!t.RosterRemovalSent)
                {
                    t.RosterRemovalSent = true;
                    bytes.World(player.Id, InstanceEntities.PlayerMinionsChange(player.Id, [new(t.Id, 0, TotemMinionType)],
                        CanDetonateMines(player)));
                }
            }
            player.Area.Life.Set(t.Id, AreaLife.LifePool, t.DiedAt is null ? t.Life : 0);
        }
        foreach (var t in totems.Remove(now))
        {
            player.RemoveBlocker(t.Id);
            bytes.Remove(t.Id);
        }
        SyncZones(player, player.Id, TotemAuraZones, totems.Living(now)
                .Select(t => (t.Id, Radius: t.Aura?.Radius ?? VaalWarchiefActivationRange(player, t), t.At))
                .Where(t => t.Radius > 0),
            new(ReachesOnly(player.Id), Changed: _ => MarkPlayerStats(player, PlayerStatStep.TotemAura)));
        SyncRejuvenationAllyZones(player, now);
        SyncReceivedTotemAuraLocked(player, now, bytes);
        SyncVaalWarchiefBuff(player, now, bytes);
        return bytes.Count == 0 ? null : bytes.Build();
    }

    internal static void EndAreaTotems(Player player)
    {
        GameArea area;
        var retired = new List<uint>();
        MinionOwnershipChange[] rosterChanges;
        bool canDetonateMines = false;
        using (ProjectileMutation(player, advance: false))
        {
            var totems = player.Totems;
            if (totems.All.Count == 0 && totems.PendingPlacements.Count == 0) return;
            area = player.Area;
            double now = AreaTime(player);
            totems.KillAll(now);
            foreach (uint pending in totems.PendingPlacements)
                retired.Add(pending);
            foreach (var totem in totems.All)
            {
                player.RemoveBlocker(totem.Id);
                TotemChannels.Remove(totem);
                if (SearingBondSources.TryGetValue(totem, out var source))
                {
                    foreach (uint beam in source.Beams.Values) retired.Add(beam);
                    SearingBondSources.Remove(totem);
                }
                retired.Add(totem.Id);
            }
            rosterChanges = player.Closed
                ? []
                : totems.All.Where(t => !t.RosterRemovalSent)
                    .Select(t => new MinionOwnershipChange(t.Id, 0, TotemMinionType)).ToArray();
            if (rosterChanges.Length > 0) canDetonateMines = CanDetonateMines(player);
            totems.Clear();
            foreach (var ground in player.FlaskGrounds.Expire(now))
                if (ground.Object is uint groundObject) retired.Add(groundObject);
        }

        static byte[] RemoveBytes(uint id)
        {
            var remove = InstanceProtocol.RemoveObject(id);
            BinaryPrimitives.WriteUInt32BigEndian(remove.AsSpan(6), uint.MaxValue);
            return remove;
        }

        if (area.Owned)
        {
            foreach (uint id in retired)
                area.Delta.RemoveSharedObject(id, RemoveBytes(id), $"0x02bd area exit removes totem object 0x{id:x}");
            if (rosterChanges.Length > 0 && !player.Closed)
                area.Delta.Add(player.Id, InstanceEntities.PlayerMinionsChange(player.Id, rosterChanges,
                    canDetonateMines), only: player);
            return;
        }

        // Purge AreaObjectRecords immediately (thread-safe) so returning/joining players never see ghost totems even if
        // this was the last player in the area and the frame driver stops before any deferred callback runs.
        foreach (uint id in retired)
            area.Objects.RecordDirect(id, RemoveBytes(id), AreaDelta.Kind.Remove, player);
        if (area.Driven)
            area.Defer(a =>
            {
                foreach (uint id in retired)
                    a.Delta.RemoveSharedObject(id, RemoveBytes(id), $"0x02bd area exit removes totem object 0x{id:x}");
                if (rosterChanges.Length > 0 && !player.Closed && ReferenceEquals(player.Area, a))
                    a.Delta.Add(player.Id, InstanceEntities.PlayerMinionsChange(player.Id, rosterChanges,
                        canDetonateMines), only: player);
            });
    }

    static void SyncReceivedTotemAuraLocked(Player player, double now, WorldRecordBuilder bytes)
    {
        var totems = player.Totems;
        var auraSource = player.PlayerLife > 0 ? ReceivedTotemAura(player, now) : null;
        uint source = auraSource?.Totem.Id ?? 0;
        if (source == totems.SentAuraTotem) return;
        if (totems.SentAuraInstance != 0)
            player.Area.Buffs.Clear(player.Id, totems.SentAuraInstance, player.Id, tag: (ushort)BuffTag.TotemAura);
        totems.SentAuraInstance = 0;
        // shortcut: the buff names the player as its source (the shape every server buff uses), not the totem and its
        // skill; send the totem once a live check shows the client takes a monster source for a player buff.
        if (auraSource is { Totem.Aura: { } aura })
        {
            // The aura's row is kept to end it (SentAuraInstance).
            totems.SentAuraInstance = aura.Row;
            player.Area.Buffs.Set(player.Id, new(aura.Row, player.Id, Tag: (ushort)BuffTag.TotemAura));
        }
        totems.SentAuraTotem = source;
        MarkPlayerStats(player, PlayerStatStep.TotemAura);
        bytes.World(player.Id, PlayerStatsOffFrames(player));
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
        if (SearingBondSources.TryGetValue(totem, out var source))
        {
            SearingBondUseLocked(player, totem, source, now, bytes);
            return;
        }
        if (totem.Strike is not { } strike || now < strike.NextCast) return;
        double range = TotemTargetRange(player, totem.SkillId);
        if (!player.Area.Monsters.Values.Any(m => TotemTargetable(m) && Dist(m.Walk.Position(now), totem.At) <= range)) return;
        if (TotemCastSeconds(player, totem.SkillId) is not double seconds) return;
        var session = ProjectilesFor(player);
        ushort sequence = ++totem.Sequence;
        strike.NextCast = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, totem.At.X, totem.At.Y,
            strike.GrantedId, sequence, player.MonsterOrderFlags));
        var lifetime = CombatLifetime;
        session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(strike.NextCast)), () =>
        {
            if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && !player.Closed
                && totem.Alive(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond))
                TotemNovaLocked(player, session, totem, totem.At, strike.Radius, sequence);
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
    static void ReleaseTotemNova(Player player, ProjectileSession session, TotemState.Totem totem, int radius,
        ushort sequence, int? ringArt = null) =>
        TotemNovaLocked(player, session, totem, totem.At, radius, sequence, ringArt: ringArt);

    static void TotemNovaLocked(Player player, ProjectileSession session, TotemState.Totem totem,
        (int X, int Y) centre, double radius, ushort sequence, bool hitSummon = false, int? ringArt = null,
        Func<(int X, int Y), bool>? covers = null, double damageMore = 1, bool spellImpale = false, int extraCriticalIncrease = 0,
        Func<uint, (int X, int Y), bool>? targetFilter = null, Action<uint>? onHit = null)
    {
        var skill = SupportedSkill(player, totem.SkillId);
        if (SupportedHitInputsFor(player, totem.SkillId, area: true, totem: true) is not { } inputs) return;
        var caster = TotemCasterStats(CharacterPlayerStats(player));
        var blind = BlindHitInput.Capture(caster, skill.Stats);
        bool attack = SkillsFor(player).IsAttack(skill.Effect);
        var hit = new SkillDamageContext(attack ? SkillHitKind.Attack : SkillHitKind.Spell, caster)
            .Hit(SupportedHitFrom(inputs, suppress: false));
        if (damageMore != 1) hit = hit.Scale(damageMore);
        if (hit.IsZero) return;
        var accuracy = attack ? SupportedAccuracy(player, totem.SkillId) : default;
        var criticalRoll = CaptureSkillCriticalRoll(player, totem.SkillId);
        var penetration = Penetration.Of(caster, Stats, attack, skill.Stats);
        var resistance = HitResistMods(caster, skill.Stats);
        void Contact(uint id, SpawnedMonster m)
        {
            double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            var targetAt = m.Walk.Position(now);
            if (!TotemTargetable(m) || MonsterAsleep(player, id) || Dist(targetAt, centre) > radius
                || covers?.Invoke(targetAt) == false || targetFilter?.Invoke(id, targetAt) == false) return;
            if (attack && !accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(
                AttackHitChanceAt(player, id, m, now, accuracy, caster), now, player.AttackAccuracyRandom)) return;
            if (MonsterBlocks(m, AreaRng(player), spell: !attack)) return;
            var rolled = WithConditionalSupportDamageLocked(player, skill.Stats, attack, m,
                GameEvents.WithOutgoingDamage(player, caster, m, hit.Roll(AreaRng(player))), now);
            int chance = CriticalChanceOn(player, id,
                SupportedCriticalChanceFor(player, totem.SkillId, release: false, extraIncrease: extraCriticalIncrease),
                SupportedCriticalChanceVsBlinded(player, totem.SkillId, false), SupportedCriticalChanceVsGround(player, totem.SkillId, false));
            bool crit = RollCriticalStrike(player, chance, skill.Stats, attack, skillRoll: criticalRoll, playerStats: caster);
            var (damage, _, mitigated) = MonsterHitDamage(m, rolled, crit, attack, now, resistance, penetration,
                HitRollsFor(caster).InvertsResistance(AreaRng(player)), AreaRng(player));
            int sourceMultiplier = crit ? SupportedCriticalMultiplier(player, totem.SkillId) : 100;
            if (crit) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, sourceMultiplier, now, caster, skill.Stats));
            int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: !attack, now, damage);
            if (spellImpale)
            {
                var settings = ImpaleStats.From(caster, Stats, attack: false, skill.Stats, critical: crit);
                damage = WithImpaleHitLocked(player, m, damage, rolled, crit,
                    sourceMultiplier + FullLifeCriticalMultiplier(m, caster, skill.Stats), settings).Damage;
            }
            if (ApplyTypedMonsterHitLocked(player, m, damage, chaosDamage, allyAt: totem.At) is not { } accepted) return;
            onHit?.Invoke(id);
            if (accepted.StageStarts) { CommitProjectileBossStage(session, id, m, session.Runtime.Now); accepted = accepted with { StageStarts = false }; }
            if (hitSummon)
                HitSummonsOnHitLocked(player, m, skill.Stats, totem.SkillId, now, AreaRng(player), playerConsumesCorpse: false);
            if (!m.Dead && damage > 0)
                AilmentsOnHitLocked(player, id, m, mitigated, !attack, crit, 100, caster, skill.Stats, now, AreaRng(player),
                    blind: blind, hitOrigin: totem.At, playerHit: false, attackerSize: totem.BodySize, attackerHasReversedStat: false);
            byte hitCode = HitCode(mitigated, damage, m.MaxLife, crit || accepted.Life == 0);
            ushort hitFlags = (ushort)(0x4200 | HitTypeFlag(hitCode) | (crit ? 0x0020 : 0));
            byte hitKind = 3, hitAngle = HitAngle(targetAt.X - totem.At.X, targetAt.Y - totem.At.Y);
            ConsumeNativeKnockbackLocked(m, ref hitFlags, ref hitKind, ref hitAngle, out ushort u3c, out ushort u3e);
            session.World(totem.Id, InstanceProtocol.ActionHit(totem.Id, sequence, id, flags: hitFlags,
                kind: hitKind, code: hitCode, angle: hitAngle, u3c: u3c, u3e: u3e));
            CommitAcceptedHitLocked(player, id, m, accepted, damage, now, "totem", playerKill: false);
        }

        long release = session.Runtime.Now;
        double releasedAt = release / (double)ProjectileRuntime.QuantaPerSecond;
        var lifetime = CombatLifetime;
        foreach (var (id, target) in player.Area.MonstersNear(centre, radius).ToArray())
        {
            if (ringArt is not int variation) { Contact(id, target); continue; }
            double distance = Dist(target.Walk.Position(releasedAt), centre);
            if (!TotemTargetable(target) || distance > radius) continue;
            session.Runtime.Schedule(release + ProjectileRuntime.Time(IceNovaRing.Reach(distance, (int)Math.Round(radius), variation)), () =>
            {
                if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && player.PlayerLife != 0
                    && player.Area.Monsters.GetValueOrDefault(id) == target)
                    Contact(id, target);
            });
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
