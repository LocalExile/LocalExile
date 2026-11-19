using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static GemSupports Supports = null!;

    /// <summary>Fills the item-granted skills at startup (the world loader, before the Gateway listens); the Ruthless
    /// set is built with the HardMode data (LoadHardModeData).</summary>
    public static void LoadWornGrantedSkillsData() => WornGrantedSkillCatalog = new(Skills, Items.Mods);

    static WornGrantedSkills WornGrantedSkillCatalog = default!;
    static WornGrantedSkills RuthlessWornGrantedSkillCatalog = default!;
    public sealed record SupportedGem(int Effect, int Level, int Quality, IReadOnlyList<GemSupports.Applied> Supports,
        Dictionary<uint, int> Stats, Item? SourceGem = null, bool RequirementsMet = true, bool Granted = false,
        WornGrantedSkills.Grant? WornGrant = null, uint Host = 0, int Group = -1);

    public sealed record PlayerSkillSource(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem,
        GemSkillIds.Gem Identity, Item? SourceGem, WornGrantedSkills.Grant? WornGrant,
        PassiveGrantedSkills.Grant? PassiveGrant, bool Active);

    /// <summary>
    /// Source-identity owner for item grants in this connection's data mode. Later trigger, minion, aspect and
    /// temporary-state owners should resolve their sources through this (and <see cref="AllWornGrantedSkillsOf"/>)
    /// and validate retained work with <see cref="WornGrantedSkills.StillWorn"/>, never from a translated mod name.
    /// </summary>
    public static WornGrantedSkills WornGrantedSkillOwner(Player conn) =>
        LeagueFor(conn).HardMode ? RuthlessWornGrantedSkillCatalog : WornGrantedSkillCatalog;

    /// <summary>Every item-granted source currently present on the supplied equipment, including unsupported/triggered rows.</summary>
    public static IReadOnlyList<WornGrantedSkills.Grant> AllWornGrantedSkillsOf(Player conn,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null) =>
        WornGrantsFor(conn, equipment ?? ItemsOf(conn).EquipmentSnapshot());

    /// <summary>
    /// Item-granted active skills with an executor. Twister's exported manual flag is true even though its intrinsic
    /// trigger owns admission; its Actor row is needed to receive the triggered action. Unsupported grants remain
    /// available from <see cref="AllWornGrantedSkillsOf"/> but are not presented as usable Actor skills.
    /// </summary>
    public static IReadOnlyList<WornGrantedSkills.Grant> WornGrantedSkillsOf(Player conn,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null)
    {
        equipment ??= ItemsOf(conn).EquipmentSnapshot();
        return [.. AllWornGrantedSkillsOf(conn, equipment).Where(g => !g.Support && g.ManuallyCast
            && LeagueFor(conn).Rules.AllowsGemEffect(g.EffectId)
            && (g.EffectId == UnhingeEffect || g.EffectId == MadnessEffect || IsDashEffect(g.EffectId)
                || IsSummonBeastialCompanionEffect(g.EffectId) || g.EffectId == TwisterEffect || g.EffectId == CatStealthCryEffect
                || AuraCatalog.Supported(g.EffectId)
                || GameEvents.GrantedSkillExecutable.For(conn).Any(accept => accept(conn, g.EffectId))))];
    }

    public static bool IsDashEffect(string? effectId) => effectId is "Dash" or "DashHardMode" or "DashArborix";
    public static SupportedGem SupportedSkill(Player conn, ushort skillId)
    {
        if (BuildScopeOf(conn) is not { } scope)
            lock (conn.Monsters) return SupportedSkillAt(conn, skillId, null);
        // Within one PlayerSkills build each gem is resolved once; callers get their own copy of its stats.
        if (!scope.Supported.TryGetValue(skillId, out var gem))
            lock (conn.Monsters) scope.Supported[skillId] = gem = SupportedSkillAt(conn, skillId, null);
        return gem with { Stats = new(gem.Stats) };
    }

    public static SupportedGem SupportedSkillAt(Player conn, ushort skillId, GemRequirementSnapshot? requirements)
    {
        using var rawSources = new GemSourceScope(conn);
        rawSources.Enter();
        string name = CharacterName(conn);
        var equipment = requirements?.Equipment ?? ItemsOf(conn).EquipmentSnapshot();
        var granted = GrantedSkillsOf(conn);
        var worn = WornGrantedSkillsOf(conn, equipment);
        var inactive = requirements?.Inactive ?? InactiveItems.GetValueOrDefault(name);
        var source = PlayerSkillSourcesAt(name, equipment, inactive, LeagueFor(conn), granted, worn)
            .FirstOrDefault(s => s.Id == skillId);
        if (source is null) return new(-1, 1, 0, [], []);
        if (source.PassiveGrant is { } passive) return GrantedSupportedSkill(conn, passive);
        if (source.WornGrant is { } itemGrant)
            return WornSupportedSkill(conn, itemGrant, equipment, requirements);
        var places = GemRequirementSnapshot.PlacesIn(equipment);
        var selected = (Place: places.Single(p => p.ItemId == source.Identity.Host && p.Socket == source.Identity.Socket),
            Effect: source.Effect);
        // Vaal source/support eligibility must not inherit an earlier equipment notification's cached pools.
        if (requirements is null && SkillsFor(conn).VaalBudget(selected.Effect, ItemsFor(conn).GemLevel(selected.Place.Gem)) is not null)
            requirements = CurrentGemRequirements(conn, equipment);
        var host = equipment.Single(e => e.Item.Id == selected.Place.ItemId).Item.Item;
        var local = ItemModStats.Local(host, ItemsFor(conn).Mods);
        (int Level, int Quality) LocalBonus(int socket, Item gem) =>
            SocketedGemBonuses.For(host, socket, gem, ItemsFor(conn), SkillsFor(conn), local, conn.Pools.Level);
        int group = GemSupports.LinkGroup(host, selected.Place.Socket);
        int Quality(int socket, Item gem)
        {
            int colour = ItemsFor(conn).ByHash(gem.TypeHash) is { } b ? ItemsFor(conn).Gem(b)?.Colour ?? 0 : 0;
            return gem.Quality + LocalBonus(socket, gem).Quality
                + (colour is >= 1 and <= 3 && (int)host.Sockets[socket].Colour == colour ? 10 : 0);
        }
        var candidates = new List<GemSupports.Candidate>();
        foreach (var p in places.Where(p => p.ItemId == selected.Place.ItemId
            && GemSupports.LinkGroup(host, p.Socket) == group && GemRequirementsUnmetAt(conn, p.Gem, requirements) is null))
            if (ItemsFor(conn).ByHash(p.Gem.TypeHash) is { } type && ItemsFor(conn).Gem(type) is { IsSupport: true } gem)
            {
                var variant = gem.Variant(p.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    candidates.Add(new(effect, EffectiveGemLevel(conn, p.Gem, LocalBonus(p.Socket, p.Gem).Level),
                        Quality(p.Socket, p.Gem), p.Socket));
            }
        candidates.AddRange(Supports.ItemCandidates(host, ItemsFor(conn).Mods));
        candidates.AddRange(OffhandSupportsForMainHand(conn, skillId, selected.Place.ItemId, equipment, places, requirements));
        var weapon = equipment.Where(e => e.Row == PlayerInventories.Weapon1).Select(e => e.Item).FirstOrDefault();
        int? weaponClass = weapon is null ? null : ItemsFor(conn).ByHash(weapon.Item.TypeHash)?.ClassRow;
        var supports = Supports.Resolve(selected.Effect, candidates, SkillsFor(conn), weaponClass,
            ItemsFor(conn).GemLevel(selected.Place.Gem));
        int Value(uint k) => supports.Sum(s => s.Stats.Where(v => v.Key == k).Sum(v => v.Value));
        // "+1 to Supported <type> Skill Gems" (Awakened supports): every supported_*_skill_gem_level_+ counts, the
        // generic active-gem one included.
        int SupportGemLevels()
        {
            int total = 0;
            foreach (var support in supports)
                foreach (var (key, value) in support.Stats)
                    if (Stats.Name(key) is { } stat && stat.StartsWith("supported_", StringComparison.Ordinal)
                        && stat.EndsWith("_skill_gem_level_+", StringComparison.Ordinal))
                        total += value;
            return total;
        }
        int level = EffectiveGemLevel(conn, selected.Place.Gem,
            SupportGemLevels() + LocalBonus(selected.Place.Socket, selected.Place.Gem).Level);
        int quality = Quality(selected.Place.Socket, selected.Place.Gem) + Value(Stat.SupportedActiveSkillGemQualityPct)
            + GemQualityBonus(conn, selected.Place.Gem);
        var own = SkillsFor(conn).EffectStats(selected.Effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        ApplySupportStats(conn, own, supports);
        if (CrueltySupportedStat is var crueltyFlag && own.GetValueOrDefault(crueltyFlag) != 0
            && CrueltyActivePct(conn, skillId) is int crueltyPct and > 0
            && CrueltyDotStat is var crueltyKey)
            own[crueltyKey] = own.GetValueOrDefault(crueltyKey) + crueltyPct;
        ApplyBloodAndSandMeleeStats(conn, selected.Effect, own);
        if (SkillsFor(conn).IsAura(selected.Effect, supports.Select(s => s.Effect.Row)))
            own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(selected.Effect, level, quality, supports, own, selected.Place.Gem,
            inactive?.Contains(selected.Place.ItemId) != true
                && GemRequirementsUnmetAt(conn, selected.Place.Gem, requirements) is null,
            Host: selected.Place.ItemId, Group: group);
    }

    /// <summary>
    /// The one ordered source list used by Actor publication, id assignment, support resolution, admission and restore:
    /// socketed gems, worn-item grants, then passive grants.
    /// </summary>
    public static IReadOnlyList<PlayerSkillSource> PlayerSkillSourcesAt(string? character,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment, IReadOnlySet<uint>? inactive, LeagueRuntime league,
        IReadOnlyList<PassiveGrantedSkills.Grant> granted, IReadOnlyList<WornGrantedSkills.Grant> worn)
    {
        string name = string.IsNullOrEmpty(character) ? "Exile" : character;
        var catalog = league.HardMode ? HardModeItems : Items;
        var gems = GemRequirementSnapshot.PlacesIn(equipment)
            .Where(p => catalog.ByHash(p.Gem.TypeHash) is not { } type ? !league.HardMode : league.Rules.AllowsGemBase(type.Id))
            .SelectMany(p => catalog.GemEffects(p.Gem).Select(effect => (Place: p, Effect: effect))).ToList();
        var identities = new List<GemSkillIds.Gem>(gems.Count + worn.Count + granted.Count);
        identities.AddRange(gems.Select(g => new GemSkillIds.Gem(g.Place.ItemId, g.Place.Socket, g.Effect)));
        identities.AddRange(worn.Select(g => new GemSkillIds.Gem(g.Host, g.Socket, g.Effect, GemSkillIds.SourceKind.WornGrant)));
        identities.AddRange(granted.Select(g => new GemSkillIds.Gem(g.Host, 0, g.Effect, GemSkillIds.SourceKind.PassiveGrant)));
        var ids = GemSkillIdsOf.GetOrAdd(name, _ => new GemSkillIds()).Assign(identities, GemSkillIdPool);
        var result = new List<PlayerSkillSource>(identities.Count);
        for (int i = 0; i < identities.Count; i++)
        {
            if (ids[i] is not ushort id) continue;
            if (i < gems.Count)
            {
                var g = gems[i];
                result.Add(new(id, g.Effect, catalog.GemName(g.Place.Gem), catalog.GemLevel(g.Place.Gem),
                    g.Place.Gem.Quality, g.Place.Gem.TypeHash, identities[i], g.Place.Gem, null, null,
                    inactive?.Contains(g.Place.ItemId) != true));
            }
            else if (i < gems.Count + worn.Count)
            {
                var g = worn[i - gems.Count];
                result.Add(new(id, g.Effect, g.EffectId, g.Level, 0, 0, identities[i], null, g, null,
                    inactive?.Contains(g.Host) != true));
            }
            else
            {
                var g = granted[i - gems.Count - worn.Count];
                result.Add(new(id, g.Effect, g.EffectId, g.Level, 0, 0, identities[i], null, null, g, true));
            }
        }
        return result;
    }

    static SupportedGem WornSupportedSkill(Player conn, WornGrantedSkills.Grant grant,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        GemRequirementSnapshot? requirements)
    {
        // The accepted-use path cannot trust the last panel publication: requirements and support eligibility must
        // come from the current equipment/attribute snapshot even when no stat update announced the change.
        requirements ??= CurrentGemRequirements(conn, equipment);
        var host = equipment.Single(e => e.Item.Id == grant.Host).Item.Item;
        var local = ItemModStats.Local(host, ItemsFor(conn).Mods);
        (int Level, int Quality) LocalBonus(int socket, Item gem) =>
            SocketedGemBonuses.For(host, socket, gem, ItemsFor(conn), SkillsFor(conn), local, conn.Pools.Level);
        int Quality(int socket, Item gem)
        {
            int colour = ItemsFor(conn).ByHash(gem.TypeHash) is { } type ? ItemsFor(conn).Gem(type)?.Colour ?? 0 : 0;
            return gem.Quality + LocalBonus(socket, gem).Quality
                + (colour is >= 1 and <= 3 && (int)host.Sockets[socket].Colour == colour ? 10 : 0);
        }
        var candidates = new List<GemSupports.Candidate>();
        // Supports socketed in the granting host apply without link groups; an item-granted active is not a gem link.
        foreach (var place in GemRequirementSnapshot.PlacesIn(equipment).Where(p => p.ItemId == grant.Host
            && GemRequirementsUnmetAt(conn, p.Gem, requirements) is null))
            if (ItemsFor(conn).ByHash(place.Gem.TypeHash) is { } type && ItemsFor(conn).Gem(type) is { IsSupport: true } gem)
            {
                var variant = gem.Variant(place.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    candidates.Add(new(effect, EffectiveGemLevel(conn, place.Gem, LocalBonus(place.Socket, place.Gem).Level),
                        Quality(place.Socket, place.Gem), place.Socket));
            }
        candidates.AddRange(Supports.ItemCandidates(host, ItemsFor(conn).Mods));
        var weapon = equipment.Where(e => e.Row == PlayerInventories.Weapon1).Select(e => e.Item).FirstOrDefault();
        int? weaponClass = weapon is null ? null : ItemsFor(conn).ByHash(weapon.Item.TypeHash)?.ClassRow;
        var supports = Supports.Resolve(grant.Effect, candidates, SkillsFor(conn), weaponClass, sourceIsGem: false);
        var own = SkillsFor(conn).EffectStats(grant.Effect, grant.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        ApplySupportStats(conn, own, supports);
        if (SkillsFor(conn).IsAura(grant.Effect, supports.Select(s => s.Effect.Row)))
            own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(grant.Effect, grant.Level, 0, supports, own,
            RequirementsMet: !requirements.Inactive.Contains(grant.Host), WornGrant: grant);
    }

    static void ApplySupportStats(Player conn, Dictionary<uint, int> own, IReadOnlyList<GemSupports.Applied> supports)
    {
        foreach (var support in supports)
            foreach (var (key, value) in support.Stats)
            {
                uint stat = key;
                if (stat is Stat.BaseSpellCastTimeMs or Stat.VirtualBaseSpellCastTimeMs or Stat.HundredTimesCastsPerSecond
                    or Stat.CastingSpell or Stat.BaseLifeCost or Stat.LifeCost or Stat.BaseEsCost or Stat.EsCost or Stat.BaseManaCost or Stat.ManaCost
                    or Stat.BaseManaCostPerMinute or Stat.ManaCostPerMinute) continue;
                own[key] = Stats.Name(stat).EndsWith("_final", StringComparison.Ordinal) && own.TryGetValue(key, out int previous)
                    ? (int)Math.Round(100 * ((1 + previous / 100.0) * (1 + value / 100.0) - 1))
                    : own.GetValueOrDefault(key) + value;
            }
        // Machinations: its "per Keystone" stats count the character's allocated keystones. The chaos gain feeds the
        // plain stats read by damage conversion and resource costs, scoped to this supported skill.
        int? keystones = null;
        foreach (uint stat in new[] { Stat.SkillCostPlusPctPerKeystone, Stat.PhysicalDamagePctToAddAsChaosPerKeystone })
            if (stat is var perKey && own.TryGetValue(perKey, out int per))
            {
                own[perKey] = per * (keystones ??= KeystoneCount(conn));
                if (stat == Stat.PhysicalDamagePctToAddAsChaosPerKeystone && own[perKey] != 0
                    && Stat.PhysicalDamagePctToAddAsChaos is var plain)
                    own[plain] = own.GetValueOrDefault(plain) + own[perKey];
                if (stat == Stat.SkillCostPlusPctPerKeystone && Stat.BaseCostPlusPct is var costKey)
                    own[costKey] = own.GetValueOrDefault(costKey) + own[perKey];
            }
        double costMultiplier = supports.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0);
        foreach (uint stat in new[] { Stat.ManaCost, Stat.BaseManaCost, Stat.LifeCost, Stat.BaseLifeCost, Stat.EsCost, Stat.BaseEsCost,
            Stat.ManaCostPerMinute, Stat.BaseManaCostPerMinute, Stat.BaseRageCost, Stat.TotalBaseRageCost })
            if (stat is var k && own.TryGetValue(k, out int cost)) own[k] = (int)Math.Floor(cost * costMultiplier);
        // Global added damage on support gems applies to this skill, not to the character or other link groups.
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
            foreach (string bound in new[] { "minimum", "maximum" })
                if (Stats.Key($"global_{bound}_added_{type}_damage") is uint k && own.TryGetValue(k, out int damage))
                    foreach (string kind in new[] { "attack", "spell" })
                        if (Stats.Key($"{kind}_{bound}_added_{type}_damage") is uint target)
                            own[target] = own.GetValueOrDefault(target) + damage;

    }

    // The Squire ("Socketed Support Gems can also Support Skills from your Main Hand"): every support gem in an active
    // off-hand with that mod also supports the gems in the main hand, linked or not. Off-hand sockets are numbered from
    // OffhandSocketBase so they never collide with the main hand's own sockets.
    public const uint OffhandSupportsMainHandStat = Stat.SupportGemsSocketedInOffHandAlsoSupportMainHandSkills;
    public const int OffhandSocketBase = 100;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, ushort), string> OffhandSupportLog = new();

    static List<GemSupports.Candidate> OffhandSupportsForMainHand(Player conn, ushort skillId, uint selectedItem,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment, IReadOnlyList<(byte Row, uint ItemId, int Socket, Item Gem)> places,
        GemRequirementSnapshot? requirements)
    {
        var result = new List<GemSupports.Candidate>();
        if (equipment.FirstOrDefault(e => e.Row == PlayerInventories.Weapon1).Item?.Id != selectedItem
            || equipment.FirstOrDefault(e => e.Row == PlayerInventories.Offhand1).Item is not { } offhand
            || !HasModStat(conn, offhand.Item, OffhandSupportsMainHandStat)
            || (requirements is not null ? requirements.Inactive.Contains(offhand.Id) : IsInactive(conn, offhand.Id)))
            return result;
        var local = ItemModStats.Local(offhand.Item, ItemsFor(conn).Mods);
        foreach (var p in places.Where(p => p.ItemId == offhand.Id && GemRequirementsUnmetAt(conn, p.Gem, requirements) is null))
            if (ItemsFor(conn).ByHash(p.Gem.TypeHash) is { } type && ItemsFor(conn).Gem(type) is { IsSupport: true } gem)
            {
                var bonus = SocketedGemBonuses.For(offhand.Item, p.Socket, p.Gem, ItemsFor(conn), SkillsFor(conn), local, conn.Pools.Level);
                int quality = p.Gem.Quality + bonus.Quality
                    + (gem.Colour is >= 1 and <= 3 && (int)offhand.Item.Sockets[p.Socket].Colour == gem.Colour ? 10 : 0);
                var variant = gem.Variant(p.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    result.Add(new(effect, EffectiveGemLevel(conn, p.Gem, bonus.Level), quality, OffhandSocketBase + p.Socket));
            }
        string summary = string.Join(", ", result.Select(c => $"{Supports.Of(c.Effect)?.Id} L{c.Level} Q{c.Quality}"));
        if (OffhandSupportLog.TryGetValue((CharacterName(conn), skillId), out var last) is false || last != summary)
        {
            OffhandSupportLog[(CharacterName(conn), skillId)] = summary;
            Console.WriteLine($"[gateway] {CharacterName(conn)}: off-hand {offhand.Id} supports main-hand skill {skillId}: {(summary.Length > 0 ? summary : "none")}");
        }
        return result;
    }

    static bool HasModStat(Player conn, Item item, uint stat) =>
        item.Mods.Any(m => ItemsFor(conn).Mods.ByHash(m.Hash)?.Stats.Any(s => s.Id == stat) == true);

    public static Dictionary<uint, int> SupportedStats(Player conn, ushort skillId)
    {
        var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var skill = SupportedSkill(conn, skillId);
        return MergeSkillStats(player, skill.Stats, SkillsFor(conn).IsChannelled(skill.Effect), SkillCostMore(conn, skill.Stats));
    }

    static double SkillCostMore(Player conn, IReadOnlyDictionary<uint, int> own)
    {
        int Get(uint key) => own.GetValueOrDefault(key);
        return BloodCharges.Uses(Get) ? BloodCharges.More(Get(BloodCharges.CostPerCharge), conn.BloodCharges) : 1;
    }

    // Generosity: the increased effect a support gives the aura it supports, and whether the aura may affect the player
    // ("Supported Auras do not affect you", aura_cannot_affect_self).
    public static (int EffectPct, bool AffectsSelf) AuraSupport(Player conn, ushort skillId)
    {
        var stats = SupportedSkill(conn, skillId).Stats;
        int Stat(uint key) => stats.GetValueOrDefault(key);
        return (Stat(global::Stat.NonCurseAuraEffectPlusPct) + Stat(global::Stat.AuraEffectPlusPct), Stat(global::Stat.AuraCannotAffectSelf) == 0);
    }

    public static Dictionary<uint, int> MergeSkillStats(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        bool? channelled = null, double costMore = 1)
    {
        var result = new Dictionary<uint, int>(player);
        foreach (var (key, value) in own)
        {
            uint stat = key;
            string name = Stats.Name(key);
            bool replace = stat is Stat.BaseLifeCost or Stat.LifeCost or Stat.BaseEsCost or Stat.EsCost or Stat.ManaCost or Stat.BaseManaCost
                or Stat.CastingSpell or Stat.VirtualBaseSpellCastTimeMs
                or Stat.BaseSpellCastTimeMs or Stat.HundredTimesCastsPerSecond || name.Contains("_base_", StringComparison.Ordinal)
                && (name.StartsWith("spell_minimum", StringComparison.Ordinal) || name.StartsWith("spell_maximum", StringComparison.Ordinal));
            result[key] = replace ? value : ArcaneMight.IsGrant(key)
                ? ArcaneMight.Combine(key, result.GetValueOrDefault(key), value) : result.GetValueOrDefault(key) + value;
        }
        if (Stat.ManaCost is var mana && own.TryGetValue(mana, out int baseCost))
            result[mana] = ManaRecoveryRules.Cost(baseCost,
                name => name is var key ? result.GetValueOrDefault(key) : 0, channelled, costMore);
        if (Stat.LifeCost is var life && own.TryGetValue(life, out int nativeLife))
        {
            int Read(uint key) => result.GetValueOrDefault(key);
            result[life] = (int)Math.Clamp(decimal.Floor(nativeLife * Math.Max(0m,
                100m + Read(Stat.BaseCostPlusPct) + Read(Stat.BaseLifeCostPlusPct) + Read(Stat.LifeCostPlusPct)) / 100m), 0, int.MaxValue);
        }
        // An Energy Shield cost: the skill's own (CostTypes 2) plus "Skills gain a Base Energy Shield Cost equal to X% of
        // Base Mana Cost" (Replica Covenant), taken from the supported base mana cost, then the increased cost of skills.
        if (Stat.EsCost is var esKey)
        {
            int Read(uint key) => result.GetValueOrDefault(key);
            long baseEs = own.GetValueOrDefault(esKey);
            int gain = Read(Stat.BaseSkillGainEsCostPctOfManaCost);
            if (gain > 0 && Stat.ManaCost is var manaKey && own.TryGetValue(manaKey, out int baseMana))
                baseEs += (long)Math.Floor(Math.Max(0, baseMana) * gain / 100.0);
            if (baseEs > 0)
                result[esKey] = (int)Math.Clamp(decimal.Floor(baseEs * Math.Max(0m, 100m + Read(Stat.BaseCostPlusPct)) / 100m), 0, int.MaxValue);
        }
        return result;
    }

    public static double SupportedAttackSeconds(Player conn, ushort skillId) => SupportedAttackSecondsFor(conn, skillId, null);

    // Multistrike: "Supported Skills Repeat an additional X times" (base_melee_attack_repeat_count). The repeats are
    // swings of one paid use, one full attack time apart. Each repeat's more damage is relative to the initial hit.
    public static int MeleeAttackRepeats(Player conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        var player = CharacterPlayerStats(conn);
        int Stat(uint key) => gem.Stats.GetValueOrDefault(key) + player.GetValueOrDefault(key);
        if (!SkillsFor(conn).IsAttack(gem.Effect) || !SkillsFor(conn).IsMelee(gem.Effect)
            || SkillsFor(conn).IsChannelled(gem.Effect)
            || Stat(global::Stat.DisableAttackRepeats) != 0 || Stat(global::Stat.DisableSkillRepeats) != 0) return 0;
        return Math.Clamp(global::Stat.BaseMeleeAttackRepeatCount is var key
            ? gem.Stats.GetValueOrDefault(key) : 0, 0, 3);
    }

    public static double MeleeRepeatDamageMore(Player conn, ushort skillId, int repeat)
    {
        if (repeat <= 0) return 1;
        var gem = SupportedSkill(conn, skillId);
        uint stat = repeat switch
        {
            1 => Stat.MultistrikeDamagePlusPctFinalOnFirstRepeat,
            2 => Stat.MultistrikeDamagePlusPctFinalOnSecondRepeat,
            _ => Stat.MultistrikeDamagePlusPctFinalOnThirdRepeat,
        };
        return stat is var key ? Math.Max(0, 1 + gem.Stats.GetValueOrDefault(key) / 100.0) : 1;
    }

    // Hallow Support: a melee hit inflicts Hallowing Flame, and the next attack against that enemy gains its share of
    // the attack's physical damage as extra fire (the sweep applies and removes it per target).
    public static void HallowOnMeleeHit(Player conn, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        if (Get(Stat.SupportHallowInflictHallowingFlameOnMeleeHit) == 0) return;
        double seconds = Math.Max(0, Get(Stat.HallowingFlameDurationMs)) / 1000.0;
        if (seconds <= 0) return;
        m.HallowingFlameUntil = Math.Max(m.HallowingFlameUntil, now + seconds);
        m.HallowingFlamePct = 25 * Math.Max(0, 1 + Get(Stat.HallowingFlameMagnitudePlusPct) / 100.0);
    }

    // Excommunicate Support: melee hits excommunicate the enemy for its listed duration; an excommunicated monster's
    // chaos damage is suppressed (the support's physical-as-extra gains already use the conversion stats).
    public static void ExcommunicateOnMeleeHit(Player conn, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        double milliseconds = Math.Max(0, Get(Stat.SupportExcommunicateOnMeleeAttackHitForXMs));
        if (milliseconds > 0) m.ExcommunicatedUntil = Math.Max(m.ExcommunicatedUntil, now + milliseconds / 1000.0);
    }

    // Devour Support: a killing blow recovers its flat life/mana and consumes the corpse. The kill path removes the
    // corpse object (ConsumeDevouredCorpseLocked), so the death, its loot and its corpse-blocking cleanup run first.
    public static void DevourOnKill(Player conn, SpawnedMonster corpse, IReadOnlyDictionary<uint, int>? skill)
    {
        int Get(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        int life = Get(Stat.KillingBlowConsumesCorpseRestoreXLife);
        int mana = Get(Stat.KillingBlowConsumesCorpseRestoreXMana);
        if (life > 0 && conn.PlayerLife > 0)
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, (long)conn.PlayerLife + life);
        if (mana > 0 && conn.PlayerLife > 0)
            conn.PlayerMana = (uint)Math.Min(UnreservedMana(conn), (long)conn.PlayerMana + mana);
        // A shattered or already consumed corpse has nothing left to consume; its life/mana still recovered above, as
        // before, but the kill sends no removal for an object that is already gone.
        if ((life > 0 || mana > 0) && !corpse.Ailments.Shattered && IsCorpse(corpse)) corpse.DevourCorpse = true;
    }

    // FireBeam's duration slot (0x1419ea070) uses base cast time / cast speed, including fractional milliseconds.
    // Channel repeats do not start new actions, so neither spell nor attack channels use the new-action frame cap.
    // A channelled attack swings on its weapon's attack time (Cyclone); a channelled spell pays and stages on its cast
    // time (Winter Orb's 0.25 s, not the caster's weapon).
    public static double? SupportedChannelSeconds(Player conn, ushort skillId)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && SkillsFor(conn).IsSpell(gem.Effect) && !SkillsFor(conn).IsAttack(gem.Effect))
        {
            if (SpellTimingFor(conn, gem.Effect, skillId)?.DurationSeconds is not double duration || duration <= 0)
                return null;
            double seconds = duration / PlayerActionFactor(conn);
            return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
        }
        return SupportedAttackSecondsFor(conn, skillId, null, clampToTick: false);
    }

    // The supported attack's swing time with the off-hand weapon of a dual-wielded pair: its own duration at the
    // off-hand increased speed (the main weapon's local speed taken back out), with the same supports.
    public static double SupportedOffHandAttackSeconds(Player conn, ushort skillId, SkillCatalog.Unarmed offHand) =>
        SupportedAttackSecondsFor(conn, skillId, offHand);

    public static double SupportedAttackSecondsFor(Player conn, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick = true, bool proxy = false) => SupportedAttackTiming(conn, skillId, offHand, clampToTick, proxy).Seconds;

    /// <summary>The supported attack's main-hand increased attack speed, the increase its swing time divides by before
    /// any more multiplier: what the client reads as the skill's <c>combined_main_hand_attack_speed_+%</c>.</summary>
    public static int SupportedMainHandAttackSpeedPct(Player conn, ushort skillId) =>
        SupportedAttackTiming(conn, skillId, null, clampToTick: true, proxy: false).Increased;

    static (double Seconds, int Increased) SupportedAttackTiming(Player conn, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick, bool proxy)
    {
        var stats = SupportedStats(conn, skillId);
        int Get(uint k) => stats.GetValueOrDefault(k);
        var gem = SupportedSkill(conn, skillId);
        if (Stat.AttackSpeedPlusPct is var raw && Stat.MainHandAttackSpeedPlusPct is var final)
        {
            var profile = WeaponProfileFor(conn);
            double factor = profile.DualWield || profile.Unarmed ? 1.1 : 1;
            stats[final] = stats.GetValueOrDefault(final) + (int)Math.Round(gem.Stats.GetValueOrDefault(raw) * factor);
            // "Chain Hook and Shield Charge have X% increased Attack Speed per 10 Rampage Kills".
            if (SkillsFor(conn).EffectId(gem.Effect) is { } effectId
                && (effectId.StartsWith("ChainHook", StringComparison.Ordinal)
                    || effectId.StartsWith("ShieldCharge", StringComparison.Ordinal))
                && Stat.ChainHookAndShieldChargeAttackSpeedPlusPctPer10RampageStacks is var rampageSpeed
                && stats.GetValueOrDefault(rampageSpeed) is int perTen and > 0)
                stats[final] += conn.Rampage.Current.Stacks / 10 * perTen;
        }
        if (proxy && Stat.MainHandAttackSpeedPlusPct is var proxySpeed)
            stats[proxySpeed] = stats.GetValueOrDefault(proxySpeed) + Get(Stat.TotemSkillAttackSpeedPlusPct)
                + Get(Stat.TotemsAttackSpeedPlusPctPerActiveTotem) * conn.Totems.Living(conn.Clock.Elapsed.TotalSeconds).Count()
                + Get(Stat.AttackSpeedPlusPctPerMaximumTotem) * TotemLimit(conn, skillId);
        double more = Supports.AttackSpeedMultiplier(gem.Effect, gem.Level) * Math.Max(.01, 1 + Get(Stat.SupportMeleePhysicalDamageAttackSpeedPlusPctFinal) / 100.0)
            * Math.Max(.01, 1 + Get(Stat.SupportMultipleAttacksMeleeAttackSpeedPlusPctFinal) / 100.0)
            // Pulverise: "15% less Attack Speed".
            * Math.Max(.01, 1 + Get(Stat.SupportPulveriseAttackSpeedPlusPctFinal) / 100.0)
            // Vicious Projectiles: "10% less Attack Speed".
            * Math.Max(.01, 1 + Get(Stat.SupportProjectileAttackSpeedPlusPctFinal) / 100.0);
        if (proxy) more *= Math.Max(.01, 1 + Get(Stat.SupportAttackTotemAttackSpeedPlusPctFinal) / 100d);
        int barrageTime = Get(Stat.SupportBarrageAttackTimePlusPctPerProjectileFired);
        if (barrageTime > 0 && ResolveProjectilePanelParameters(gem.Effect) is { } projectileParameters)
        {
            var projectiles = projectileParameters.Resolve(Get, 1, ArrowsFor(conn, gem.Effect));
            int count = Get(Stat.ProjectilesRain) != 0 && projectiles.EndEmission is { } rain
                ? rain.Child.Count : projectiles.Count;
            // The native final-speed consumer divides by 1 + the per-projectile time penalty before rounding.
            more /= 1 + (double)barrageTime * count / 100;
        }
        // Rage Support: "19% increased Attack Speed with at least 10 Rage" (the rage stat set carries current_rage).
        if (Get(Stat.CurrentRage) >= 10 && Get(Stat.AttackSpeedPlusPctWithAtLeast10Rage) is int rageSpeed and > 0
            && Stat.MainHandAttackSpeedPlusPct is var rageBucket)
            stats[rageBucket] += rageSpeed;
        // A skill's own increased Attack Speed per Frenzy Charge (Flicker Strike's 10%): the character's per-charge stats
        // are in its stats already (ChargeEffects), this one only on the skill. Without it the client swung faster than
        // the server's timer allowed and some uses were ignored (live 2026-11-09, 5-6 charges).
        if (Stat.BaseAttackSpeedPlusPctPerFrenzyCharge is var perFrenzy
            && gem.Stats.GetValueOrDefault(perFrenzy) is int skillPerFrenzy and not 0
            && Stat.MainHandAttackSpeedPlusPct is var frenzyBucket)
        {
            int frenzy;
            lock (conn.Monsters) frenzy = ChargesOf(conn).Effective(ChargeType.Frenzy);
            stats[frenzyBucket] = stats.GetValueOrDefault(frenzyBucket) + skillPerFrenzy * frenzy;
        }
        // Close Combat's Combat Rush: more Attack Speed for a travel skill it does not support.
        more *= CombatRushMore(conn, skillId, gem, conn.Clock.Elapsed.TotalSeconds);
        // Supports.AttackSpeedMultiplier reads the skill's own GEPL row too: its inherent -20 on Tornado Shot is
        // already part of this factor. Do not apply the same GEPL value again.
        int additional = SkillAttackTiming.AdditionalMilliseconds(stats);
        if (additional != 0)
        {
            var weapon = offHand ?? WeaponFor(conn);
            // ItemWeaponStats already includes each hand's local speed in AttackMs. The character bucket is shared
            // (Progression's attackSpeedMore), so neither hand strips a local multiplier from it here.
            int speed = weapon.IncreasedAttackSpeed(stats);
            return (SkillAttackTiming.Seconds(weapon.AttackMs, speed, more, additional,
                proxy ? 1 : PlayerActionFactor(conn), clampToTick), speed);
        }
        int baseSpeed = offHand is null ? WeaponFor(conn).IncreasedAttackSpeed(stats)
            : SkillCatalog.Unarmed.OffHandAttackSpeedPct(stats.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct),
                WeaponProfileFor(conn, activeSet: true).LocalAttackSpeedPct);
        double seconds = offHand is null ? WeaponFor(conn).AttackSeconds(stats) : DualWieldPanel.OffHandSeconds(offHand, baseSpeed);
        // Preserve the zero-additional-time path's existing minimum and integer final-speed rounding.
        double paced = seconds * Math.Max(.01, 1 + baseSpeed / 100.0)
            / (1 + SkillAttackTiming.FinalSpeedPercent(baseSpeed, more) / 100.0) / (proxy ? 1 : PlayerActionFactor(conn));
        return (clampToTick ? Math.Max(TickSeconds, paced) : Math.Max(.001, paced), baseSpeed);
    }

    public static GameServer.Core.World.DamagePacket SupportedHit(Player conn, ushort skillId)
        => SupportedHitFor(conn, skillId, suppress: true);

    // The allocated keystones a "per Keystone" stat counts: the character's own, and the ones a tattoo turns a node
    // into (the node itself is not a keystone then).
    static int KeystoneCount(Player conn)
    {
        int count = 0;
        foreach (ushort node in conn.Passives)
            if (Program.Passives.Find(node) is { IsKeystone: true }) count++;
        foreach (var (node, chosen) in conn.TattooKeystones)
            if (Program.Passives.Find(node) is not { IsKeystone: true } && Program.Passives.Find(chosen) is { IsKeystone: true })
                count++;
        return count;
    }

    // Close Combat: a supported melee hit grants Combat Rush, which speeds up travel skills the support does not
    // support. A paid travel use consumes it, retaining the speed only for that use's action.
    public static void CombatRushOnHit(Player conn, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(uint name) => skill is not null && name is var k ? skill.GetValueOrDefault(k) : 0;
        if (Get(Stat.SupportedBySlashing) == 0) return;
        int milliseconds = Get(Stat.SupportSlashingBuffBaseDurationMs);
        int speed = Get(Stat.SupportSlashingBuffAttackSpeedPlusPctFinalToGrant);
        if (milliseconds <= 0 || speed <= 0) return;
        conn.CombatRushUntil = Math.Max(conn.CombatRushUntil, now + milliseconds / 1000.0);
        conn.CombatRushAttackSpeed = Math.Max(conn.CombatRushAttackSpeed, speed);
    }

    public static double CombatRushMore(Player conn, ushort skillId, SupportedGem gem, double now)
    {
        if (!SkillsFor(conn).IsTravel(gem.Effect) || gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat")) return 1;
        int speed = conn.CombatRushUse is { } use && use.Action == conn.ActionCount && use.Skill == skillId
            ? use.Speed : conn.CombatRushUntil > now ? conn.CombatRushAttackSpeed : 0;
        return Math.Max(.01, 1 + speed / 100.0);
    }

    public static void ConsumeCombatRush(Player conn, ushort skillId, SupportedGem gem, double now)
    {
        if (!SkillsFor(conn).IsTravel(gem.Effect) || conn.CombatRushUntil <= now) return;
        conn.CombatRushUse = (conn.ActionCount, skillId,
            gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat") ? 0 : conn.CombatRushAttackSpeed);
        conn.CombatRushUntil = 0;
        conn.CombatRushAttackSpeed = 0;
    }

    public static GameServer.Core.World.DamagePacket SupportedProjectileHit(Player conn, ushort skillId, bool area,
        long additionalIncreased = 0, int addedChaos = 0)
        => SupportedHitFor(conn, skillId, suppress: false, area, additionalIncreased: additionalIncreased, projectile: true, addedChaos: addedChaos);

    // The same supported attack swung with the off-hand weapon of a dual-wielded attack (the Offence tab's Off Hand rows).
    public static GameServer.Core.World.DamagePacket SupportedOffHandHit(Player conn, ushort skillId, SkillCatalog.Unarmed offHand)
        => SupportedHitFor(conn, skillId, suppress: true, weapon: offHand);

    // A supported skill's hit inputs before conversion and modifiers, shared by the hit itself and the panel's ailment
    // rows, which apply their own buckets to the same raw packet and conversion. Null when the skill cannot hit.
    public sealed record SupportedHitInputs(GameServer.Core.World.DamagePacket Raw, Func<uint, int> Get,
        GameServer.Core.World.DamageConversion Conversion, bool Attack, bool Melee, bool Area, bool Projectile,
        List<(uint Id, int Value)> Modifiers, HashSet<int> Types, SkillHitPlan Plan, Dictionary<uint, int> Caster,
        bool WieldingWand = false, bool WieldingBow = false, bool Secondary = false)
    {
        public IReadOnlyDictionary<uint, int> FrozenStats { get; init; } = new Dictionary<uint, int>();
        public int WeaponIncreased { get; init; }
        public bool Armed { get; init; }
    }

    public static SupportedHitInputs? SupportedHitInputsFor(Player conn, ushort skillId, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, IReadOnlyDictionary<ChargeType, int>? charges = null, SupportedGem? capturedGem = null,
        bool projectile = false, int addedChaos = 0, bool totem = false)
    {
        var gem = capturedGem ?? SupportedSkill(conn, skillId);
        var caster = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, gem.Stats);
        if (charges is not null)
            foreach (var (type, count) in charges)
                if (Stats.Key($"current_removable_{Charges.Name(type)}_charges") is uint removable) stats[removable] = count;
        if (totem && Stat.DamagePlusPct is var damage)
            stats[damage] = stats.GetValueOrDefault(damage) + GolemStat(stats, Stat.TotemDamagePlusPct);
        int Get(uint name) => name == Stat.IsAreaDamage && area is bool isArea ? isArea ? 1 : 0
            : name is var k ? stats.GetValueOrDefault(k) : 0;
        bool attack = SkillsFor(conn).IsAttack(gem.Effect);
        var types = Supports.Of(gem.Effect)?.Types.ToHashSet() ?? [];
        // Lightning Strike's two components share a gem, not a damage context. Its bolts do not gain melee
        // modifiers, and its strike does not gain projectile modifiers or the projectile-only less multiplier.
        bool lightningStrike = IsLightningStrike(gem.Effect);
        if (lightningStrike) types.Remove(projectile ? 19 : 2);
        if (area is true) types.Add(7);
        else if (area is false) types.Remove(7);
        bool molten = GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(conn).EffectId(gem.Effect));
        if (molten)
        {
            if (projectile) { types.Remove(19); types.Add(2); }
            else { types.Remove(2); types.Remove(7); }
        }
        var plan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!plan.CanHit) return null;
        bool secondary = plan.Kind == SkillHitKind.Secondary;
        var attackWeapon = attack ? weapon ?? WeaponFor(conn) : null;
        var coefficients = SkillsFor(conn).DamageCoefficients(gem.Effect, gem.Level);
        var raw = secondary ? SecondaryRawHit(conn, gem, plan.SpellBaseWith(Get), Get)
            : attackWeapon is not null
            ? SupportedAttackDamage.Raw(attackWeapon, Get, coefficients.Base, coefficients.Added)
            : DamageStats.RawAdded(plan.SpellBaseWith(Get), Get, spell: true, AddedEffectiveness(conn, gem.Effect, gem.Level));
        if (!attack && !secondary && addedChaos > 0)
            raw = raw.Add(GameServer.Core.World.DamageType.Chaos,
                new GameServer.Core.World.DamageRange(addedChaos, addedChaos).Scale(AddedEffectiveness(conn, gem.Effect, gem.Level)));
        if (lightningStrike && projectile)
            raw = raw.Scale(Math.Max(0, 1 + Get(Stat.ActiveSkillHitAilmentDamageWithProjectilePlusPctFinal) / 100.0));
        // Spellblade: a supported spell gains a share of the one-hand melee weapon's damage as added spell damage,
        // scaled by the spell's damage effectiveness like any other added damage.
        if (!attack && !secondary && Get(Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage) is int bladeGain and > 0)
        {
            var bladeWeapon = weapon ?? WeaponFor(conn);
            if (bladeWeapon.Melee && bladeWeapon.LocalDamage is not null)
            {
                double effectiveness = AddedEffectiveness(conn, gem.Effect, gem.Level);
                // The weapon's attack-only attribute grant is not added damage for this spell.
                foreach (var (type, range) in SupportedAttackDamage.Raw(bladeWeapon with { LocalSources = null }, Get).Components)
                    raw = raw.Add(type, range.Scale(bladeGain / 100.0 * effectiveness));
            }
        }
        if (molten && projectile && Get(Stat.ActiveSkillHitAilmentDamageWithProjectilePlusPctFinal) is int projFinal and not 0)
            raw = raw.Scale(Math.Max(0, 1 + projFinal / 100.0));
        var modifiers = gem.Supports.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)).ToList();
        modifiers.AddRange(gem.Stats.Where(s => s.Key == Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinal).Select(s => (s.Key, s.Value)));
        return new(raw, Get, DamageStats.Conversion(stats, Stats, secondary ? null : attack), attack,
            Melee: attack && types.Contains(19), Area: attack ? types.Contains(7) : Get(Stat.IsAreaDamage) != 0,
            Projectile: attack ? types.Contains(2) : Get(Stat.BaseIsProjectile) != 0, modifiers, types, plan, caster,
            WieldingWand: attackWeapon?.WieldingWand == true, WieldingBow: attackWeapon?.Bow == true, Secondary: secondary)
        {
            FrozenStats = new Dictionary<uint, int>(stats),
            WeaponIncreased = attackWeapon is null ? 0 : SupportedAttackDamage.WeaponIncrease(attackWeapon, Get),
            Armed = attackWeapon is not null && WeaponAttributeDamage.Armed(attackWeapon),
        };
    }

    public static GameServer.Core.World.DamagePacket SupportedHitFor(Player conn, ushort skillId, bool suppress, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, IReadOnlyDictionary<ChargeType, int>? charges = null, long additionalIncreased = 0, bool projectile = false, int addedChaos = 0)
        => SupportedHitInputsFor(conn, skillId, area, weapon, charges, projectile: projectile, addedChaos: addedChaos) is { } inputs
            ? SupportedHitFrom(inputs, suppress, additionalIncreased) : GameServer.Core.World.DamagePacket.Empty;

    // The supported hit from inputs already worked out (GemSkills reads one gem's inputs once for its hit and ailment rows).
    public static GameServer.Core.World.DamagePacket SupportedHitFrom(SupportedHitInputs inputs, bool suppress,
        long additionalIncreased = 0)
    {
        var hit = PlayerHitDamage.Resolve(inputs.Raw, WeaponAttributeDamage.Read(inputs.Armed, inputs.Get), inputs.Conversion, spell: !inputs.Attack && !inputs.Secondary,
            melee: inputs.Melee, area: inputs.Area, projectile: inputs.Projectile,
            supportModifiers: inputs.Modifiers, skillTypes: inputs.Types,
            wieldingBow: inputs.WieldingBow, wieldingWand: inputs.WieldingWand, retaliation: inputs.Attack && inputs.Types.Contains(120),
            secondary: inputs.Secondary, additionalIncreased: additionalIncreased + (inputs.Attack ? inputs.WeaponIncreased : 0));
        if (inputs.Attack) hit = MaximumAttackDamage.Apply(Volatility.Apply(RageStats.ApplyAttack(hit, inputs.Get), inputs.Get), inputs.Get);
        return suppress ? new SkillDamageContext(inputs.Plan.Kind, inputs.Caster).Hit(hit) : hit;
    }

    // The damaging-ailment rows of a supported skill's hit in one context (AilmentPanelStats): the main or off hand of
    // an attack with that hand's weapon, or a spell's / secondary-damage skill's hit.
    public static List<(uint Stat, int Value)> SupportedAilmentRows(Player conn, ushort skillId, string hand,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, bool projectile = false, int addedChaos = 0)
        => SupportedHitInputsFor(conn, skillId, weapon: weapon, projectile: projectile, addedChaos: addedChaos) is { } inputs
            ? SupportedAilmentRowsFrom(inputs, hand, additionalIncreased) : [];

    public static List<(uint Stat, int Value)> SupportedAilmentRowsFrom(SupportedHitInputs inputs, string hand,
        long additionalIncreased = 0)
    {
        var context = new AilmentPanelStats.Context(inputs.Secondary ? "secondary" : hand, Spell: !inputs.Attack && !inputs.Secondary, inputs.Melee);
        return AilmentPanelStats.Compute(context, inputs.Raw, inputs.Conversion, inputs.Get, additionalIncreased);
    }

    public static AttackAccuracyProfile SupportedAccuracy(Player conn, ushort skillId)
    {
        var own = SupportedSkill(conn, skillId).Stats;
        long Own(uint key) => own.GetValueOrDefault(key);
        var player = CharacterPlayerStats(conn);
        return AttackAccuracyProfile.From(conn.Pools, player, Stats, own, MainHandLocalSources(conn))
            with { Rating = PlayerBlindAccuracy(conn, AccuracyRating.Selected(conn.Pools, WeaponProfileFor(conn, activeSet: true), Own), player) };
    }

    public static int SupportedCriticalChance(Player conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, false);

    // A release retains the unprohibited offensive chance; the hit evaluates current owner prohibitions separately.
    public static int SupportedReleaseCriticalChance(Player conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, true);

    // extraIncrease: a target condition's increase (critical_strike_chance_+%_vs_blinded_enemies, Game's Blind.cs).
    public static int SupportedCriticalChanceFor(Player conn, ushort skillId, bool release, long extraIncrease = 0)
    {
        var gem = SupportedSkill(conn, skillId);
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return 0;
        // Inspiration: "Supported Skills have (6-11)% increased Critical Strike Chance per Inspiration Charge".
        if (conn.SupportGems.InspirationCharges > 0
            && Stat.CriticalStrikeChancePlusPctPerRighteousCharge is var chargeCrit)
            extraIncrease += (long)gem.Stats.GetValueOrDefault(chargeCrit) * conn.SupportGems.InspirationCharges;
        var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        int Own(uint name) => release && IsCriticalProhibition(name) ? 0
            : name is var k ? gem.Stats.GetValueOrDefault(k) : 0;
        int Player(uint name) => release && IsCriticalProhibition(name) ? 0
            : name is var key ? player.GetValueOrDefault(key) : 0;
        if (SkillsFor(conn).IsAttack(gem.Effect))
        {
            if (new[] { Stat.GlobalCannotCrit, Stat.VirtualGlobalCannotCrit, Stat.CannotCriticalStrikeWithAttacks }
                .Any(name => Player(name) != 0 || Own(name) != 0)) return 0;
            var weapon = WeaponProfileFor(conn, activeSet: true);
            int basis = weapon.Unarmed && (!SkillsFor(conn).IsMelee(gem.Effect) || SkillsFor(conn).IsSpell(gem.Effect)) ? 0
                : AttackBaseCriticalStrike.OfMainHand(!weapon.Unarmed, weapon.Critical, Player);
            long increased = conn.Pools.CriticalStrikeChancePct + extraIncrease
                + AttackBaseCriticalStrike.IncreasedSum(weapon, name => Own(name));
            return DisplayedCriticalChance(conn, AttackBaseCriticalStrike.ChanceOfBase(basis, increased, Player, Own), Player);
        }
        return DisplayedCriticalChance(conn, SpellCriticalStrike.Chance(SkillsFor(conn).SpellCritChance(gem.Effect, gem.Level),
            Player, Own, conn.Pools.SpellCriticalStrikeChancePct + extraIncrease), Player);
    }

    public static SkillCatalog.ReservationCost? SupportedReservationCost(Player conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        if (SkillsFor(conn).EffectId(gem.Effect) is not { } effectId) return null;
        var sourceReservation = SkillsFor(conn).Reservation(gem.Effect, gem.Level);
        if (CurseCatalog.Hex(effectId) || effectId == "TemporalChains")
        {
            var blasphemy = gem.Supports.FirstOrDefault(s => IsBlasphemySupport(s.Effect.Id));
            if (blasphemy is null) return null;
            sourceReservation = SkillsFor(conn).Reservation(blasphemy.Effect.Row, blasphemy.Gem.Level);
            var curseSources = CharacterStatSources(conn).Stats;
            if (effectId == "TemporalChains" && Stat.TemporalChainsNoReservation is var free
                && curseSources.GetValueOrDefault(free) != 0) return new(0, 0, 0, 0);
        }
        if (sourceReservation is not { } reservation) return null;
        var sources = CharacterStatSources(conn).Stats;
        int Own(uint name) => GolemStat(gem.Stats, name);
        int Get(uint name) => Own(name) + (name is var key
            ? sources.GetValueOrDefault(key) : 0);
        int manaFlat = reservation.ManaFlat, manaPercent = reservation.ManaPercent;
        int lifeFlat = reservation.LifeFlat, lifePercent = reservation.LifePercent;
        bool curse = gem.Supports.Any(s => IsBlasphemySupport(s.Effect.Id));
        bool mine = Supports.Of(gem.Effect)?.Types.Contains(35) == true;
        bool lifeInstead = SkillsFor(conn).LifeReservation(gem.Effect, gem.Level)
            || Get(Stat.BaseSkillReserveLifeInsteadOfMana) != 0 || Get(Stat.SkillReserveLifeInsteadOfMana) != 0
            || curse && Get(Stat.CurseAuraSkillsReserveLife) != 0
            || mine && Get(Stat.SkillsThatThrowMinesReserveLife) != 0;
        if (lifeInstead)
        {
            lifeFlat += manaFlat;
            lifePercent += manaPercent;
            manaFlat = manaPercent = 0;
        }
        if (Get(Stat.BaseSkillNoLifeReservation) != 0 || Get(Stat.SkillNoLifeReservation) != 0)
            lifeFlat = lifePercent = 0;
        // Mana-only exemptions never erase a Life reservation.
        if (AuraCatalog.Supported(effectId) && Get(AuraCatalog.NoManaReservationStat(effectId)) != 0)
            manaFlat = manaPercent = 0;
        if (effectId == "TemporalChains" && Get(Stat.TemporalChainsNoReservation) != 0)
            manaFlat = manaPercent = 0;
        decimal factor = gem.Supports.Aggregate(1m, (v, support) => v * support.CostMultiplier / 100m);
        return new((int)Math.Floor(manaFlat * factor), (int)Math.Floor(manaPercent * factor),
            (int)Math.Floor(lifeFlat * factor), (int)Math.Floor(lifePercent * factor));
    }

    // Compatibility view for one-pool callers: select the resource the caller's context will use, never a Mana-only
    // projection that would erase a Life cost. Full callers use SupportedReservationCost's four raw slots.
    public static (int Flat, int Percent)? SupportedReservation(Player conn, ushort skillId)
    {
        if (SupportedReservationCost(conn, skillId) is not { } cost) return null;
        var context = ReservationContext(conn, skillId);
        bool explicitLife = cost.LifeFlat != 0 || cost.LifePercent != 0;
        return context.Life
            ? (explicitLife ? cost.LifeFlat : cost.ManaFlat, explicitLife ? cost.LifePercent : cost.ManaPercent)
            : (cost.ManaFlat, cost.ManaPercent);
    }

    public static int SupportedCriticalMultiplier(Player conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        int extra = Stat.BaseCriticalStrikeMultiplierPlus is var k ? gem.Stats.GetValueOrDefault(k) : 0;
        var player = CharacterPlayerStats(conn);
        return CriticalHitMultiplier((long)(SkillsFor(conn).IsAttack(gem.Effect)
            ? AttackCriticalMultiplier(conn, player, gem.Effect, gem.Stats)
            : conn.Pools.SpellCriticalStrikeMultiplier) + extra, player, gem.Stats);
    }
}
