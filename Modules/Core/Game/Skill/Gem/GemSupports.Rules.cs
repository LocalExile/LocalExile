using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static GemSupports Supports = null!;

    /// <summary>Fills the item-granted skills at startup (the world loader, before the Gateway listens); the Ruthless
    /// set is built with the HardMode data (LoadHardModeData).</summary>
    public static void LoadWornGrantedSkillsData() =>
        WornGrantedSkillCatalog = new(Skills, Items.Mods, legacyGrantMods: LegacyWornGrantMods);

    // Older and generated Lightpoacher items can carry only the display mod. Its companion owns the exact trigger level.
    static readonly Dictionary<string, string> LegacyWornGrantMods = new()
    {
        ["LocalDisplayGrantLevelXSpiritBurstUnique__1"] = "SpiritBurstOnSkillUseUnique",
    };

    static WornGrantedSkills WornGrantedSkillCatalog = default!;
    static WornGrantedSkills RuthlessWornGrantedSkillCatalog = default!;
    public sealed record SupportedGem(int Effect, int Level, int Quality, IReadOnlyList<GemSupports.Applied> Supports,
        IReadOnlyDictionary<uint, int> Stats, Item? SourceGem = null, bool RequirementsMet = true, bool Granted = false,
        WornGrantedSkills.Grant? WornGrant = null, uint Host = 0, int Group = -1)
    {
        internal IReadOnlyList<GemSupports.Applied> LivingLightningSupports { get; init; } = [];
    }

    public sealed record PlayerSkillSource(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem,
        GemSkillIds.Gem Identity, Item? SourceGem, WornGrantedSkills.Grant? WornGrant,
        PassiveGrantedSkills.Grant? PassiveGrant, bool Active);

    /// <summary>
    /// Source-identity owner for item grants in this connection's data mode. Later trigger, minion, aspect and
    /// temporary-state owners should resolve their sources through this (and <see cref="AllWornGrantedSkillsOf"/>)
    /// and validate retained work with <see cref="WornGrantedSkills.StillWorn"/>, never from a translated mod name.
    /// </summary>
    public static WornGrantedSkills WornGrantedSkillOwner(Player player) =>
        LeagueFor(player).HardMode ? RuthlessWornGrantedSkillCatalog : WornGrantedSkillCatalog;

    /// <summary>Every item-granted source currently present on the supplied equipment, including unsupported/triggered rows.</summary>
    public static IReadOnlyList<WornGrantedSkills.Grant> AllWornGrantedSkillsOf(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null) =>
        WornGrantsFor(player, equipment ?? ItemsOf(player).EquipmentSnapshot());

    /// <summary>
    /// Item-granted active skills with an executor. Twister and Spirit Burst have Actor rows for their
    /// built-in triggers. Unsupported grants remain
    /// available from <see cref="AllWornGrantedSkillsOf"/> but are not presented as usable Actor skills.
    /// </summary>
    public static IReadOnlyList<WornGrantedSkills.Grant> WornGrantedSkillsOf(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null)
    {
        equipment ??= ItemsOf(player).EquipmentSnapshot();
        return [.. AllWornGrantedSkillsOf(player, equipment).Where(g => !g.Support && (g.ManuallyCast || g.EffectId is SpiritBurstEffect or GuardianWeaponEffect || TriggeredItemMinion(g) is not null)
            && LeagueFor(player).Rules.AllowsGemEffect(g.EffectId)
            && (g.EffectId == UnhingeEffect || g.EffectId == MadnessEffect || IsDashEffect(g.EffectId)
                || SkillsFor(player).IsWarcry(g.Effect)
                || IsSummonBeastialCompanionEffect(g.EffectId) || g.EffectId == TwisterEffect || g.EffectId == CatStealthCryEffect
                || g.EffectId is SnipeEffect or SpiritBurstEffect or "DoryanisTouch" or ApproachingFlamesEffect
                    or "BearTrap" or "WintertideBrand" or "DecoyTotem" or "Smite" or "Frostblink"
                    or "SummonStoneGolem" or "Convocation" or "EnduringCry" or "IntimidatingCry" or GuardianWeaponEffect
                || TriggeredItemMinion(g) is not null
                || g.EffectId == ShaperMemoryEffect
                || SummonData.Find(g.EffectId) is { Family: "DoedresEffigy" or ItemHarbingerFamily }
                || AuraCatalog.Supported(g.EffectId)
                || GameEvents.GrantedSkillExecutable.For(player).Any(accept => accept(player, g.EffectId))))];
    }

    public static bool IsDashEffect(string? effectId) => effectId is "Dash" or "DashHardMode" or "DashArborix";
    public static SupportedGem SupportedSkill(Player player, ushort skillId) =>
        ApplyCurrentMomentum(player, skillId, ApplyCurrentIntensity(player, skillId, BaseSupportedSkill(player, skillId)));

    static SupportedGem BaseSupportedSkill(Player player, ushort skillId)
    {
        var scope = BuildScopeOf(player);
        // Outside the skill build, the skills it left (ActiveSkills) while current: a use reads its skill's state.
        if (scope is not { Building: true } && CurrentActiveSkills(player) is { } current)
        {
            if (!current.Supported.TryGetValue(skillId, out var kept))
                current.Supported[skillId] = kept = SupportedSkillAt(player, skillId, null);
            return kept;
        }
        if (scope is null)
            return SupportedSkillAt(player, skillId, null);
        // Within one PlayerSkills build each gem is resolved once (its stats are read-only, so it is shared).
        if (!scope.Supported.TryGetValue(skillId, out var gem))
            scope.Supported[skillId] = gem = SupportedSkillAt(player, skillId, null);
        return gem;
    }

    public static SupportedGem SupportedSkillAt(Player player, ushort skillId, GemRequirementSnapshot? requirements)
    {
        using var rawSources = new GemSourceScope(player);
        rawSources.Enter();
        string name = CharacterName(player);
        var equipment = requirements?.Equipment ?? ItemsOf(player).EquipmentSnapshot();
        var granted = GrantedSkillsOf(player);
        var worn = WornGrantedSkillsOf(player, equipment);
        var inactive = requirements?.Inactive ?? InactiveItems.GetValueOrDefault(name);
        var source = PlayerSkillSourcesAt(name, equipment, inactive, LeagueFor(player), granted, worn)
            .FirstOrDefault(s => s.Id == skillId);
        if (source is null) return new(-1, 1, 0, [], new Dictionary<uint, int>());
        if (source.PassiveGrant is { } passive) return GrantedSupportedSkill(player, passive);
        if (source.WornGrant is { } itemGrant)
            return WornSupportedSkill(player, itemGrant, equipment, requirements);
        var places = GemRequirementSnapshot.PlacesIn(equipment);
        var selected = (Place: places.Single(p => p.ItemId == source.Identity.Host && p.Socket == source.Identity.Socket),
            Effect: source.Effect);
        // Vaal source/support eligibility must not inherit an earlier equipment notification's cached pools.
        if (requirements is null && SkillsFor(player).VaalBudget(selected.Effect, ItemsFor(player).GemLevel(selected.Place.Gem)) is not null)
            requirements = CurrentGemRequirements(player, equipment);
        var host = equipment.Single(e => e.Item.Id == selected.Place.ItemId).Item.Item;
        var local = ItemModStats.Local(host, ItemsFor(player).Mods);
        (int Level, int Quality) LocalBonus(int socket, Item gem) =>
            SocketedGemBonuses.For(host, socket, gem, ItemsFor(player), SkillsFor(player), local, player.Pools.Level);
        int group = GemSupports.LinkGroup(host, selected.Place.Socket);
        int Quality(int socket, Item gem)
        {
            int colour = ItemsFor(player).ByHash(gem.TypeHash) is { } b ? ItemsFor(player).Gem(b)?.Colour ?? 0 : 0;
            return gem.Quality + LocalBonus(socket, gem).Quality
                + (colour is >= 1 and <= 3 && (int)host.Sockets[socket].Colour == colour ? 10 : 0);
        }
        var candidates = new List<GemSupports.Candidate>();
        foreach (var p in places.Where(p => p.ItemId == selected.Place.ItemId
            && GemSupports.LinkGroup(host, p.Socket) == group && GemRequirementsUnmetAt(player, p.Gem, requirements) is null))
            if (ItemsFor(player).ByHash(p.Gem.TypeHash) is { } type && ItemsFor(player).Gem(type) is { } gem)
            {
                var variant = gem.Variant(p.Gem.GemVariant);
                if (gem.IsSupport)
                    foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                        candidates.Add(new(effect, EffectiveGemLevel(player, p.Gem, LocalBonus(p.Socket, p.Gem).Level),
                            Quality(p.Socket, p.Gem), p.Socket));
                else if (variant.Effect2 >= 0
                    && Supports.Of(variant.Effect2) is { Support: true, Id: SnipeSupportEffect or GeneralsCrySupportEffect
                        or "SupportBrandSupport" or "SupportIntuitiveLink" or AutomationSupport or AutoexertionSupport })
                    candidates.Add(new(variant.Effect2, EffectiveGemLevel(player, p.Gem, LocalBonus(p.Socket, p.Gem).Level),
                        Quality(p.Socket, p.Gem), p.Socket));
            }
        candidates.AddRange(Supports.ItemCandidates(host, ItemsFor(player).Mods));
        candidates.AddRange(OffhandSupportsForMainHand(player, skillId, selected.Place.ItemId, equipment, places, requirements));
        var supports = ResolveEquippedSupports(player, selected.Effect, ItemsFor(player).GemLevel(selected.Place.Gem),
            candidates, equipment, requirements?.Inactive ?? inactive, sourceIsGem: true);
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
        int level = EffectiveGemLevel(player, selected.Place.Gem,
            SupportGemLevels() + LocalBonus(selected.Place.Socket, selected.Place.Gem).Level);
        int quality = Quality(selected.Place.Socket, selected.Place.Gem) + Value(Stat.SupportedActiveSkillGemQualityPct)
            + GemQualityBonus(player, selected.Place.Gem);
        var own = SkillsFor(player).EffectStats(selected.Effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        ApplySupportStats(player, own, supports);
        ApplySocketedGolemEquipment(player, selected.Effect, host, local, own);
        AddIntensityBaseStats(player, own);
        if (CrueltySupportedStat is var crueltyFlag && own.GetValueOrDefault(crueltyFlag) != 0
            && CrueltyActivePct(player, skillId) is int crueltyPct and > 0
            && CrueltyDotStat is var crueltyKey)
            own[crueltyKey] = own.GetValueOrDefault(crueltyKey) + crueltyPct;
        ApplyBloodAndSandMeleeStats(player, selected.Effect, own);
        if (SkillsFor(player).IsAura(selected.Effect, supports.Select(s => s.Effect.Row)))
            own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(selected.Effect, level, quality, supports, own, selected.Place.Gem,
            inactive?.Contains(selected.Place.ItemId) != true
                && GemRequirementsUnmetAt(player, selected.Place.Gem, requirements) is null,
            Host: selected.Place.ItemId, Group: group)
        {
            LivingLightningSupports = ResolveLivingLightningSupports(player, supports, candidates, equipment,
                requirements?.Inactive ?? inactive),
        };
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

    static SupportedGem WornSupportedSkill(Player player, WornGrantedSkills.Grant grant,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        GemRequirementSnapshot? requirements, int? supportTarget = null)
    {
        // The accepted-use path cannot trust the last panel publication: requirements and support eligibility must
        // come from the current equipment/attribute snapshot even when no stat update announced the change.
        requirements ??= CurrentGemRequirements(player, equipment);
        var host = equipment.Single(e => e.Item.Id == grant.Host).Item.Item;
        var local = ItemModStats.Local(host, ItemsFor(player).Mods);
        (int Level, int Quality) LocalBonus(int socket, Item gem) =>
            SocketedGemBonuses.For(host, socket, gem, ItemsFor(player), SkillsFor(player), local, player.Pools.Level);
        int Quality(int socket, Item gem)
        {
            int colour = ItemsFor(player).ByHash(gem.TypeHash) is { } type ? ItemsFor(player).Gem(type)?.Colour ?? 0 : 0;
            return gem.Quality + LocalBonus(socket, gem).Quality
                + (colour is >= 1 and <= 3 && (int)host.Sockets[socket].Colour == colour ? 10 : 0);
        }
        var candidates = new List<GemSupports.Candidate>();
        // Supports socketed in the granting host apply without link groups; an item-granted active is not a gem link.
        foreach (var place in GemRequirementSnapshot.PlacesIn(equipment).Where(p => p.ItemId == grant.Host
            && GemRequirementsUnmetAt(player, p.Gem, requirements) is null))
            if (ItemsFor(player).ByHash(place.Gem.TypeHash) is { } type && ItemsFor(player).Gem(type) is { IsSupport: true } gem)
            {
                var variant = gem.Variant(place.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    candidates.Add(new(effect, EffectiveGemLevel(player, place.Gem, LocalBonus(place.Socket, place.Gem).Level),
                        Quality(place.Socket, place.Gem), place.Socket));
            }
        candidates.AddRange(Supports.ItemCandidates(host, ItemsFor(player).Mods));
        var supports = ResolveEquippedSupports(player, supportTarget ?? grant.Effect, grant.Level, candidates, equipment,
            requirements.Inactive, sourceIsGem: false);
        var own = SkillsFor(player).EffectStats(grant.Effect, grant.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        ApplySupportStats(player, own, supports);
        AddIntensityBaseStats(player, own);
        if (SkillsFor(player).IsAura(grant.Effect, supports.Select(s => s.Effect.Row)))
            own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(grant.Effect, grant.Level, 0, supports, own,
            RequirementsMet: !requirements.Inactive.Contains(grant.Host), WornGrant: grant)
        {
            LivingLightningSupports = ResolveLivingLightningSupports(player, supports, candidates, equipment, requirements.Inactive),
        };
    }

    static IReadOnlyList<GemSupports.Applied> ResolveEquippedSupports(Player player, int effect, int level,
        List<GemSupports.Candidate> candidates, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        IReadOnlySet<uint>? inactive, bool sourceIsGem)
    {
        int? weaponClass = null;
        bool allOneHandMelee = false;
        if (candidates.Exists(c => Supports.Of(c.Effect) is { Weapons.Length: > 0 }))
        {
            var catalog = ItemsFor(player);
            foreach (var (row, item) in equipment)
            {
                if (row != PlayerInventories.Weapon1 || inactive?.Contains(item.Id) == true) continue;
                weaponClass = catalog.ByHash(item.Item.TypeHash)?.ClassRow;
                allOneHandMelee = WeaponIdentity.AllOneHandMelee(item.Item, catalog.Mods);
                break;
            }
        }
        return Supports.Resolve(effect, candidates, SkillsFor(player), weaponClass, level, sourceIsGem, allOneHandMelee);
    }

    static void ApplySupportStats(Player player, Dictionary<uint, int> own, IReadOnlyList<GemSupports.Applied> supports)
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
                own[perKey] = per * (keystones ??= KeystoneCount(player));
                if (stat == Stat.PhysicalDamagePctToAddAsChaosPerKeystone && own[perKey] != 0
                    && Stat.PhysicalDamagePctToAddAsChaos is var plain)
                    own[plain] = own.GetValueOrDefault(plain) + own[perKey];
                if (stat == Stat.SkillCostPlusPctPerKeystone && Stat.BaseCostPlusPct is var costKey)
                    own[costKey] = own.GetValueOrDefault(costKey) + own[perKey];
            }
        int warcryMana = GolemStat(own, Stat.SupportAutoexertionBaseManaCostOverride);
        if (warcryMana > 0) own[Stat.ManaCost] = own[Stat.BaseManaCost] = warcryMana;
        double costMultiplier = supports.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0);
        if (GolemStat(own, Stat.SkillIsBlessingSkill) != 0 && GolemStat(own, Stat.SupportAuraDurationBaseBuffDuration) > 0)
            foreach (uint instant in new[] { Stat.BaseSkillIsInstant, Stat.SkillIsInstant })
                own[instant] = 0;
        foreach (uint stat in new[] { Stat.ManaCost, Stat.BaseManaCost, Stat.LifeCost, Stat.BaseLifeCost, Stat.EsCost, Stat.BaseEsCost,
            Stat.ManaCostPerMinute, Stat.BaseManaCostPerMinute, Stat.BaseManaCostPctPerMinute, Stat.BaseRageCost, Stat.TotalBaseRageCost })
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

    static List<GemSupports.Candidate> OffhandSupportsForMainHand(Player player, ushort skillId, uint selectedItem,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment, IReadOnlyList<(byte Row, uint ItemId, int Socket, Item Gem)> places,
        GemRequirementSnapshot? requirements)
    {
        var result = new List<GemSupports.Candidate>();
        if (equipment.FirstOrDefault(e => e.Row == PlayerInventories.Weapon1).Item?.Id != selectedItem
            || equipment.FirstOrDefault(e => e.Row == PlayerInventories.Offhand1).Item is not { } offhand
            || !HasModStat(player, offhand.Item, OffhandSupportsMainHandStat)
            || (requirements is not null ? requirements.Inactive.Contains(offhand.Id) : IsInactive(player, offhand.Id)))
            return result;
        var local = ItemModStats.Local(offhand.Item, ItemsFor(player).Mods);
        foreach (var p in places.Where(p => p.ItemId == offhand.Id && GemRequirementsUnmetAt(player, p.Gem, requirements) is null))
            if (ItemsFor(player).ByHash(p.Gem.TypeHash) is { } type && ItemsFor(player).Gem(type) is { IsSupport: true } gem)
            {
                var bonus = SocketedGemBonuses.For(offhand.Item, p.Socket, p.Gem, ItemsFor(player), SkillsFor(player), local, player.Pools.Level);
                int quality = p.Gem.Quality + bonus.Quality
                    + (gem.Colour is >= 1 and <= 3 && (int)offhand.Item.Sockets[p.Socket].Colour == gem.Colour ? 10 : 0);
                var variant = gem.Variant(p.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    result.Add(new(effect, EffectiveGemLevel(player, p.Gem, bonus.Level), quality, OffhandSocketBase + p.Socket));
            }
        string summary = string.Join(", ", result.Select(c => $"{Supports.Of(c.Effect)?.Id} L{c.Level} Q{c.Quality}"));
        if (OffhandSupportLog.TryGetValue((CharacterName(player), skillId), out var last) is false || last != summary)
        {
            OffhandSupportLog[(CharacterName(player), skillId)] = summary;
            Console.WriteLine($"[gateway] {CharacterName(player)}: off-hand {offhand.Id} supports main-hand skill {skillId}: {(summary.Length > 0 ? summary : "none")}");
        }
        return result;
    }

    static bool HasModStat(Player player, Item item, uint stat) =>
        item.Mods.Any(m => ItemsFor(player).Mods.ByHash(m.Hash)?.Stats.Any(s => s.Id == stat) == true);

    /// <summary>The skill's final stats (the player's with its own laid over), not to be changed: kept with the skills
    /// (ActiveSkills) by skill and Blood Charge cost, the stats its use reads (cost, area, speed).</summary>
    public static IReadOnlyDictionary<uint, int> SupportedStats(Player player, ushort skillId)
    {
        if (BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current)
        {
            return current.SkillGraphOf(player, skillId, SupportedSkill(player, skillId)).Stats;
        }
        return SupportedStatsNow(player, skillId);
    }

    static Dictionary<uint, int> SupportedStatsNow(Player player, ushort skillId)
    {
        var playerStats = FinalPlayerStats(player).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var skill = SupportedSkill(player, skillId);
        var skills = SkillsFor(player);
        MergeSkillStatsInto(playerStats.GetValueOrDefault, (key, value) => playerStats[key] = value,
            WithManaPoolCost(player, skill, WithBlessingCost(player, skill), ManaCostPool(player, skill)),
            skills.IsChannelled(skill.Effect), SkillCostMore(player, skill.Stats),
            CostEfficiencyFor(skills, skill.Effect));
        return playerStats;
    }

    // Frostmage is a variant of Archmage. Support resolution keeps only one member of that family.
    static int ManaCostPool(Player player, SupportedGem skill)
    {
        if (skill.Stats.GetValueOrDefault(Stat.FrostmageCostEqualsPctReservedMana) > 0) return ReservedMana(player);
        return skill.Stats.GetValueOrDefault(Stat.ManaweaveCostEqualsPctUnreservedMana) > 0 ? UnreservedMana(player) : -1;
    }

    static IReadOnlyDictionary<uint, int> WithManaPoolCost(Player player, SupportedGem skill,
        IReadOnlyDictionary<uint, int> own, int pool)
    {
        if (pool < 0) return own;
        int percent = Math.Max(own.GetValueOrDefault(Stat.ManaweaveCostEqualsPctUnreservedMana),
            own.GetValueOrDefault(Stat.FrostmageCostEqualsPctReservedMana));
        uint native = ManaCost(Stats, SkillsFor(player), skill.Effect, skill.Level, skill.Quality);
        decimal factor = skill.Supports.Aggregate(1m, (value, support) => value * support.CostMultiplier / 100m);
        // The added base cost and the gem's own base are charged by the same support multipliers, with one floor.
        int cost = (int)Math.Clamp(decimal.Floor((native + pool * (decimal)percent / 100m) * factor), 0, int.MaxValue);
        return new Dictionary<uint, int>(own) { [Stat.BaseManaCost] = cost, [Stat.ManaCost] = cost };
    }

    [Flags]
    enum CostEfficiency { None = 0, Spell = 1, Curse = 2, Spectre = 4, Minion = 8 }

    static CostEfficiency CostEfficiencyFor(SkillCatalog skills, int effect) =>
        (skills.IsSpell(effect) ? CostEfficiency.Spell : CostEfficiency.None)
        | (skills.HasType(effect, "AppliesCurse") ? CostEfficiency.Curse : CostEfficiency.None)
        | (skills.EffectId(effect) is "RaiseSpectre" or "RaiseSpectreAltX" ? CostEfficiency.Spectre : CostEfficiency.None)
        | (skills.HasType(effect, "Minion") ? CostEfficiency.Minion : CostEfficiency.None);

    static double SkillCostMore(Player player, IReadOnlyDictionary<uint, int> own)
    {
        int Get(uint key) => own.GetValueOrDefault(key);
        return BloodCharges.Uses(Get) ? BloodCharges.More(Get(BloodCharges.CostPerCharge), player.BloodCharges) : 1;
    }

    // Generosity: the increased effect a support gives the aura it supports, and whether the aura may affect the player
    // ("Supported Auras do not affect you", aura_cannot_affect_self).
    public static (int EffectPct, bool AffectsSelf) AuraSupport(Player player, ushort skillId)
    {
        var stats = SupportedSkill(player, skillId).Stats;
        int Stat(uint key) => stats.GetValueOrDefault(key);
        return (Stat(global::Stat.NonCurseAuraEffectPlusPct) + Stat(global::Stat.AuraEffectPlusPct), Stat(global::Stat.AuraCannotAffectSelf) == 0);
    }

    public static Dictionary<uint, int> MergeSkillStats(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        bool? channelled = null, double costMore = 1) =>
        MergeSkillStats(player, own, channelled, costMore, CostEfficiency.None);

    static Dictionary<uint, int> MergeSkillStats(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        bool? channelled, double costMore, CostEfficiency efficiency)
    {
        var result = new Dictionary<uint, int>(player);
        MergeSkillStatsInto(result.GetValueOrDefault, (key, value) => result[key] = value, own, channelled, costMore, efficiency);
        return result;
    }

    /// <summary>A skill's own stats over the player's: <paramref name="read"/> gives a stat as merged so far (the player's
    /// until the skill's wrote it), <paramref name="write"/> sets one. Only the skill's own stats and its costs are written.</summary>
    public static void MergeSkillStatsInto(Func<uint, int> read, Action<uint, int> write, IReadOnlyDictionary<uint, int> own,
        bool? channelled = null, double costMore = 1) =>
        MergeSkillStatsInto(read, write, own, channelled, costMore, CostEfficiency.None);

    static void MergeSkillStatsInto(Func<uint, int> read, Action<uint, int> write, IReadOnlyDictionary<uint, int> own,
        bool? channelled, double costMore, CostEfficiency efficiency)
    {
        foreach (var (key, value) in own)
        {
            uint stat = key;
            string name = Stats.Name(key);
            bool replace = stat is Stat.BaseLifeCost or Stat.LifeCost or Stat.BaseEsCost or Stat.EsCost or Stat.ManaCost or Stat.BaseManaCost
                or Stat.CastingSpell or Stat.VirtualBaseSpellCastTimeMs
                or Stat.BaseSpellCastTimeMs or Stat.HundredTimesCastsPerSecond || name.Contains("_base_", StringComparison.Ordinal)
                && (name.StartsWith("spell_minimum", StringComparison.Ordinal) || name.StartsWith("spell_maximum", StringComparison.Ordinal));
            write(key, replace ? value : ArcaneMight.IsGrant(key)
                ? ArcaneMight.Combine(key, read(key), value) : read(key) + value);
        }
        long conditionalEfficiency = ((efficiency & CostEfficiency.Spell) != 0 ? (long)read(Stat.BaseSpellManaCostEfficiencyPlusPct) : 0)
            + ((efficiency & CostEfficiency.Curse) != 0 ? read(Stat.BaseManaCostEfficiencyPlusPctOfCurseSkills) : 0)
            + ((efficiency & CostEfficiency.Minion) != 0 ? read(Stat.BaseManaCostEfficiencyPlusPctOfMinionSkills) : 0);
        if (conditionalEfficiency != 0)
            write(ItemChargeCosts.ManaEfficiency, ActorScale.Saturate(read(ItemChargeCosts.ManaEfficiency) + conditionalEfficiency));
        if (Stat.ManaCost is var mana && own.TryGetValue(mana, out int baseCost))
        {
            long scopedCost = ((efficiency & CostEfficiency.Spectre) != 0 ? (long)read(Stat.RaiseSpectreManaCostPlusPct) : 0)
                + ((efficiency & CostEfficiency.Minion) != 0 ? read(Stat.MinionSkillManaCostPlusPct) : 0);
            int ManaInput(uint name) => name == Stat.ManaCostPlusPct
                ? ActorScale.Saturate(read(name) + scopedCost) : read(name);
            write(mana, ManaRecoveryRules.Cost(baseCost, ManaInput, channelled, costMore));
        }
        // "Socketed Movement Skills Cost no Mana": the item's support grants no_mana_cost to the skills it supports.
        if (own.GetValueOrDefault(Stat.NoManaCost) != 0) write(Stat.ManaCost, 0);
        long nativeLife = own.GetValueOrDefault(Stat.LifeCost);
        int additiveLife = read(Stat.BaseSkillGainLifeCostPctOfManaCost);
        if (additiveLife > 0 && !ManaRecoveryRules.ConvertsManaToLife(read))
            nativeLife += (long)Math.Max(0, own.GetValueOrDefault(Stat.ManaCost)) * additiveLife / 100;
        if (nativeLife != 0 || own.ContainsKey(Stat.LifeCost))
            write(Stat.LifeCost, ManaRecoveryRules.LifeCost(nativeLife, read));
        // An Energy Shield cost: the skill's own (CostTypes 2) plus "Skills gain a Base Energy Shield Cost equal to X% of
        // Base Mana Cost" (Replica Covenant), taken from the supported base mana cost, then the increased cost of skills.
        if (Stat.EsCost is var esKey)
        {
            long baseEs = own.GetValueOrDefault(esKey);
            int gain = read(Stat.BaseSkillGainEsCostPctOfManaCost);
            if (gain > 0 && Stat.ManaCost is var manaKey && own.TryGetValue(manaKey, out int baseMana))
                baseEs += (long)Math.Floor(Math.Max(0, baseMana) * gain / 100.0);
            if (baseEs > 0)
                write(esKey, (int)Math.Clamp(decimal.Floor(baseEs * Math.Max(0m, 100m + read(Stat.BaseCostPlusPct)) / 100m), 0, int.MaxValue));
        }
    }

    public static double SupportedAttackSeconds(Player player, ushort skillId) => SupportedAttackSecondsFor(player, skillId, null);

    // Multistrike: "Supported Skills Repeat an additional X times" (base_melee_attack_repeat_count). The repeats are
    // swings of one paid use, one full attack time apart. Each repeat's more damage is relative to the initial hit.
    public static int MeleeAttackRepeats(Player player, ushort skillId)
    {
        var gem = SupportedSkill(player, skillId);
        var playerStats = CharacterPlayerStats(player);
        int Stat(uint key) => gem.Stats.GetValueOrDefault(key) + playerStats.GetValueOrDefault(key);
        if (!SkillsFor(player).IsAttack(gem.Effect) || !SkillsFor(player).IsMelee(gem.Effect)
            || SkillsFor(player).IsChannelled(gem.Effect)
            || Stat(global::Stat.DisableAttackRepeats) != 0 || Stat(global::Stat.DisableSkillRepeats) != 0) return 0;
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalSweep")
            return Math.Clamp(gem.Stats.GetValueOrDefault(global::Stat.BaseAttackRepeatCount), 0, 64);
        return Math.Clamp(global::Stat.BaseMeleeAttackRepeatCount is var key
            ? gem.Stats.GetValueOrDefault(key) : 0, 0, 3);
    }

    public static double MeleeRepeatDamageMore(Player player, ushort skillId, int repeat)
    {
        if (repeat <= 0) return 1;
        var gem = SupportedSkill(player, skillId);
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
    public static void HallowOnMeleeHit(Player player, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
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
    public static void ExcommunicateOnMeleeHit(Player player, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        double milliseconds = Math.Max(0, Get(Stat.SupportExcommunicateOnMeleeAttackHitForXMs));
        if (milliseconds > 0) m.ExcommunicatedUntil = Math.Max(m.ExcommunicatedUntil, now + milliseconds / 1000.0);
    }

    // Devour Support: a killing blow recovers its flat life/mana and consumes the corpse. The kill path removes the
    // corpse object (ConsumeDevouredCorpseLocked), so the death, its loot and its corpse-blocking cleanup run first.
    public static void DevourOnKill(Player player, SpawnedMonster corpse, IReadOnlyDictionary<uint, int>? skill)
    {
        int Get(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        int life = Get(Stat.KillingBlowConsumesCorpseRestoreXLife);
        int mana = Get(Stat.KillingBlowConsumesCorpseRestoreXMana);
        if (life > 0 && player.PlayerLife > 0 && !LifeRecovery.NonLeechBlocked(Get))
            player.PlayerLife = (uint)Math.Min(player.PlayerMaxLife, (long)player.PlayerLife + life);
        if (mana > 0 && player.PlayerLife > 0)
            player.PlayerMana = (uint)Math.Min(UnreservedMana(player), (long)player.PlayerMana + mana);
        // A shattered or already consumed corpse has nothing left to consume; its life/mana still recovered above, as
        // before, but the kill sends no removal for an object that is already gone.
        if ((life > 0 || mana > 0) && !corpse.Ailments.Shattered && IsCorpse(corpse)) corpse.DevourCorpse = true;
    }

    // FireBeam's duration slot (0x1419ea070) uses base cast time / cast speed, including fractional milliseconds.
    // Channel repeats do not start new actions, so neither spell nor attack channels use the new-action frame cap.
    // A channelled attack swings on its weapon's attack time (Cyclone); a channelled spell pays and stages on its cast
    // time (Winter Orb's 0.25 s, not the caster's weapon).
    public static double? SupportedChannelSeconds(Player player, ushort skillId)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && SkillsFor(player).IsSpell(gem.Effect) && !SkillsFor(player).IsAttack(gem.Effect))
        {
            if (SpellTimingFor(player, gem.Effect, skillId)?.DurationSeconds is not double duration || duration <= 0)
                return null;
            double seconds = duration / PlayerActionFactor(player);
            return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
        }
        return SupportedAttackSecondsFor(player, skillId, null, clampToTick: false);
    }

    // The supported attack's swing time with the off-hand weapon of a dual-wielded pair: its own duration at the
    // off-hand increased speed (the main weapon's local speed taken back out), with the same supports.
    public static double SupportedOffHandAttackSeconds(Player player, ushort skillId, SkillCatalog.Unarmed offHand) =>
        SupportedAttackSecondsFor(player, skillId, offHand);

    public static double SupportedAttackSecondsFor(Player player, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick = true, bool proxy = false) => SupportedAttackTiming(player, skillId, offHand, clampToTick, proxy).Seconds;

    /// <summary>The supported off-hand attack's duration and speed components: increased % (combined_off_hand_attack_speed_+%),
    /// more/less % (combined_off_hand_attack_speed_+%_final) and final % (off_hand_attack_speed_+%, 0x127).</summary>
    public static (double Seconds, int IncreasedPct, int MorePct, int FinalPct) SupportedOffHandAttackSpeed(
        Player player, ushort skillId, SkillCatalog.Unarmed offHand)
    {
        var (seconds, increased, more) = SupportedAttackTiming(player, skillId, offHand, clampToTick: true, proxy: false);
        int morePct = (int)MathF.Round(((float)more - 1f) * 100f, MidpointRounding.AwayFromZero);
        int finalPct = SkillAttackTiming.FinalSpeedPercent(increased, more);
        return (seconds, increased, morePct, finalPct);
    }

    /// <summary>The supported attack's main-hand increased attack speed, the increase its swing time divides by before
    /// any more multiplier: what the client reads as the skill's <c>combined_main_hand_attack_speed_+%</c>.</summary>
    public static int SupportedMainHandAttackSpeedPct(Player player, ushort skillId) =>
        SupportedAttackTiming(player, skillId, null, clampToTick: true, proxy: false).Increased;

    static (double Seconds, int Increased, double More) SupportedAttackTiming(Player player, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick, bool proxy)
    {
        var stats = new Dictionary<uint, int>(SupportedStats(player, skillId));
        int Get(uint k) => stats.GetValueOrDefault(k);
        var gem = SupportedSkill(player, skillId);
        if (Stat.AttackSpeedPlusPct is var raw && Stat.MainHandAttackSpeedPlusPct is var final)
        {
            var profile = WeaponProfileFor(player);
            // The pools' dual-wield "more" (PassiveBonus), doubled by the Claw Mastery with two claws.
            double factor = profile.DualWield || profile.Unarmed
                ? profile.DualClaws && Get(Stat.DualWieldInherentAttackSpeedIsDoubledWhileDualWieldingClaws) != 0 ? 1.2 : 1.1
                : 1;
            int skillSpeed = gem.Stats.GetValueOrDefault(raw) + TaggedAttackAndCastSpeedPct(SkillsFor(player), gem.Effect, Get);
            if (gem.Stats.GetValueOrDefault(Stat.AttackSpeedPlusPctIfChangedStanceRecently) is int stanceSpeed and not 0
                && player.RecentEvents.Happened(StatConditions.ChangedStance, AreaTime(player)))
                skillSpeed += stanceSpeed;
            if (Supports.Of(gem.Effect)?.Types.Contains(120) == true)
                skillSpeed += Get(Stat.RetaliationSkillSpeedPlusPct);
            stats[final] = stats.GetValueOrDefault(final) + (int)Math.Round(skillSpeed * factor);
            // "Chain Hook and Shield Charge have X% increased Attack Speed per 10 Rampage Kills".
            if (SkillsFor(player).EffectId(gem.Effect) is { } effectId
                && (effectId.StartsWith("ChainHook", StringComparison.Ordinal)
                    || effectId.StartsWith("ShieldCharge", StringComparison.Ordinal))
                && Stat.ChainHookAndShieldChargeAttackSpeedPlusPctPer10RampageStacks is var rampageSpeed
                && stats.GetValueOrDefault(rampageSpeed) is int perTen and > 0)
                stats[final] += player.Rampage.Current.Stacks / 10 * perTen;
            if (IsSnipe(gem.Effect) && stats.GetValueOrDefault(Stat.SnipeAttackSpeedPlusPct) is int snipeInc and not 0)
                stats[final] += snipeInc;
        }
        if (proxy && Stat.MainHandAttackSpeedPlusPct is var proxySpeed)
            stats[proxySpeed] = stats.GetValueOrDefault(proxySpeed) + Get(Stat.TotemSkillAttackSpeedPlusPct)
                + Get(Stat.TotemsAttackSpeedPlusPctPerActiveTotem) * player.Totems.Living(AreaTime(player)).Count()
                + Get(Stat.AttackSpeedPlusPctPerMaximumTotem) * TotemLimit(player, skillId);
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
            var projectiles = projectileParameters.Resolve(Get, 1, ArrowsFor(player, gem.Effect));
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
            frenzy = ChargesOf(player).Effective(ChargeType.Frenzy);
            stats[frenzyBucket] = stats.GetValueOrDefault(frenzyBucket) + skillPerFrenzy * frenzy;
        }
        // The Frenzy gem's own "5% more Attack Speed per Frenzy Charge" (a more multiplier of value x charges).
        if (Get(Stat.FrenzySkillAttackSpeedPlusPctFinalPerFrenzyCharge) is int frenzyMore and not 0)
        {
            int frenzy = ChargesOf(player).Effective(ChargeType.Frenzy);
            more *= Math.Max(.01, 1 + (double)frenzyMore * frenzy / 100);
        }
        // Close Combat's Combat Rush: more Attack Speed for a travel skill it does not support.
        more *= CombatRushMore(player, skillId, gem, AreaTime(player));
        // Supports.AttackSpeedMultiplier reads the skill's own GEPL row too: its inherent -20 on Tornado Shot is
        // already part of this factor. Do not apply the same GEPL value again.
        int additional = SkillAttackTiming.AdditionalMilliseconds(stats);
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalSweep"
            && gem.Stats.GetValueOrDefault(Stat.CastTimeOverridesAttackDuration) != 0
            && SkillsFor(player).CastMs(gem.Effect) is > 0 and var swingMilliseconds)
        {
            int speed = (offHand ?? WeaponFor(player)).IncreasedAttackSpeed(stats);
            more *= Math.Max(.01, 1 + gem.Stats.GetValueOrDefault(Stat.ActiveSkillAttackSpeedPlusPctFinal) / 100d);
            return (SkillAttackTiming.Seconds(swingMilliseconds, speed, more, additional,
                proxy ? 1 : PlayerActionFactor(player), clampToTick), speed, more);
        }
        if (additional != 0)
        {
            var weapon = offHand ?? WeaponFor(player);
            // ItemWeaponStats already includes each hand's local speed in AttackMs. The character bucket is shared
            // (Progression's attackSpeedMore), so neither hand strips a local multiplier from it here.
            int speed = weapon.IncreasedAttackSpeed(stats);
            return (SkillAttackTiming.Seconds(weapon.AttackMs, speed, more, additional,
                proxy ? 1 : PlayerActionFactor(player), clampToTick), speed, more);
        }
        int baseSpeed = offHand is null ? WeaponFor(player).IncreasedAttackSpeed(stats)
            : SkillCatalog.Unarmed.OffHandAttackSpeedPct(stats.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct),
                WeaponProfileFor(player, activeSet: true).LocalAttackSpeedPct);
        if (!clampToTick && offHand is not null)
            return (SkillAttackTiming.Seconds(offHand.AttackMs, baseSpeed, more,
                actionSpeed: proxy ? 1 : PlayerActionFactor(player), clampToTick: false), baseSpeed, more);
        double seconds = offHand is null ? WeaponFor(player).AttackSeconds(stats) : DualWieldPanel.OffHandSeconds(offHand, baseSpeed);
        // Preserve the zero-additional-time path's existing minimum and integer final-speed rounding.
        double paced = seconds * Math.Max(.01, 1 + baseSpeed / 100.0)
            / (1 + SkillAttackTiming.FinalSpeedPercent(baseSpeed, more) / 100.0) / (proxy ? 1 : PlayerActionFactor(player));
        return (clampToTick ? Math.Max(TickSeconds, paced) : Math.Max(.001, paced), baseSpeed, more);
    }

    public static GameServer.Core.World.DamagePacket SupportedHit(Player player, ushort skillId)
        => SupportedHitFor(player, skillId, suppress: true);

    // The allocated keystones a "per Keystone" stat counts: the character's own, and the ones a tattoo turns a node
    // into (the node itself is not a keystone then).
    static int KeystoneCount(Player player)
    {
        int count = 0;
        foreach (ushort node in player.Character.Passives)
            if (Program.Passives.Find(node) is { IsKeystone: true }) count++;
        foreach (var (node, chosen) in player.Character.TattooKeystones)
            if (Program.Passives.Find(node) is not { IsKeystone: true } && Program.Passives.Find(chosen) is { IsKeystone: true })
                count++;
        return count;
    }

    // Close Combat: a supported melee hit grants Combat Rush, which speeds up travel skills the support does not
    // support. A paid travel use consumes it, retaining the speed only for that use's action.
    public static void CombatRushOnHit(Player player, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(uint name) => skill is not null && name is var k ? skill.GetValueOrDefault(k) : 0;
        if (Get(Stat.SupportedBySlashing) == 0) return;
        int milliseconds = Get(Stat.SupportSlashingBuffBaseDurationMs);
        int speed = Get(Stat.SupportSlashingBuffAttackSpeedPlusPctFinalToGrant);
        if (milliseconds <= 0 || speed <= 0) return;
        player.CombatRushUntil = Math.Max(player.CombatRushUntil, now + milliseconds / 1000.0);
        player.CombatRushAttackSpeed = Math.Max(player.CombatRushAttackSpeed, speed);
    }

    public static double CombatRushMore(Player player, ushort skillId, SupportedGem gem, double now)
    {
        if (!SkillsFor(player).IsTravel(gem.Effect) || gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat")) return 1;
        int speed = player.CombatRushUse is { } use && use.Action == player.ActionCount && use.Skill == skillId
            ? use.Speed : player.CombatRushUntil > now ? player.CombatRushAttackSpeed : 0;
        return Math.Max(.01, 1 + speed / 100.0);
    }

    public static void ConsumeCombatRush(Player player, ushort skillId, SupportedGem gem, double now)
    {
        if (!SkillsFor(player).IsTravel(gem.Effect) || player.CombatRushUntil <= now) return;
        player.CombatRushUse = (player.ActionCount, skillId,
            gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat") ? 0 : player.CombatRushAttackSpeed);
        player.CombatRushUntil = 0;
        player.CombatRushAttackSpeed = 0;
    }

    public static GameServer.Core.World.DamagePacket SupportedProjectileHit(Player player, ushort skillId, bool area,
        long additionalIncreased = 0, int addedChaos = 0)
        => SupportedHitFor(player, skillId, suppress: false, area, additionalIncreased: additionalIncreased, projectile: true, addedChaos: addedChaos);

    // The same supported attack swung with the off-hand weapon of a dual-wielded attack (the Offence tab's Off Hand rows).
    public static GameServer.Core.World.DamagePacket SupportedOffHandHit(Player player, ushort skillId, SkillCatalog.Unarmed offHand)
        => SupportedHitFor(player, skillId, suppress: true, weapon: offHand);

    // A supported skill's hit inputs before conversion and modifiers, shared by the hit itself and the panel's ailment
    // rows, which apply their own buckets to the same raw packet and conversion. Null when the skill cannot hit.
    public sealed record SupportedHitInputs(GameServer.Core.World.DamagePacket Raw, Func<uint, int> Get,
        GameServer.Core.World.DamageConversion Conversion, bool Attack, bool Melee, bool Area, bool Projectile,
        List<(uint Id, int Value)> Modifiers, HashSet<int> Types, SkillHitPlan Plan, Dictionary<uint, int> Caster,
        bool WieldingWand = false, bool WieldingBow = false, bool Secondary = false)
    {
        public IReadOnlyDictionary<uint, int> FrozenStats { get; init; } = new Dictionary<uint, int>();
        public int WeaponIncreased { get; init; }
        public HitIncreases WeaponTypedIncreases { get; init; }
        public bool Armed { get; init; }
        public bool Claw { get; init; }
        public double ReleaseAilmentMore { get; init; } = 1;
    }

    static int PlayerMinionDamageShare(IReadOnlyDictionary<uint, int> stats) =>
        Math.Max(stats.GetValueOrDefault(Stat.AdditiveMinionDamageModifiersApplyToYouAtPctValue),
            stats.GetValueOrDefault(Stat.AdditiveMinionDamageModifiersApplyToYouAt150PctValue) != 0 ? 150
            : stats.GetValueOrDefault(Stat.MinionDamageIncreasesAndReductionsAlsoAffectsYou) != 0 ? 100 : 0);

    public static SupportedHitInputs? SupportedHitInputsFor(Player player, ushort skillId, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, IReadOnlyDictionary<ChargeType, int>? charges = null, SupportedGem? capturedGem = null,
        bool projectile = false, int addedChaos = 0, bool totem = false)
    {
        var gem = capturedGem ?? SupportedSkill(player, skillId);
        var caster = FinalPlayerStats(player).ToDictionary(v => v.Item1, v => v.Item2);
        var skills = SkillsFor(player);
        int manaPool = ManaCostPool(player, gem);
        var stats = MergeSkillStats(caster, WithManaPoolCost(player, gem, gem.Stats, manaPool),
            skills.IsChannelled(gem.Effect), SkillCostMore(player, gem.Stats), CostEfficiencyFor(skills, gem.Effect));
        if (manaPool >= 0)
        {
            AddManaPoolDamage(Stat.ArchmageGainLightningDamagePctOfMaxUnreservedMana,
                Stat.SpellMinimumAddedLightningDamage, Stat.SpellMaximumAddedLightningDamage);
            AddManaPoolDamage(Stat.FrostmageGainColdDamagePctOfMaxReservedMana,
                Stat.SpellMinimumAddedColdDamage, Stat.SpellMaximumAddedColdDamage);
        }
        void AddManaPoolDamage(uint percent, uint min, uint max)
        {
            int added = (int)Math.Clamp((long)manaPool * gem.Stats.GetValueOrDefault(percent) / 100, 0, int.MaxValue);
            if (added == 0) return;
            stats[min] = ActorScale.Add(min, stats.GetValueOrDefault(min), added);
            stats[max] = ActorScale.Add(max, stats.GetValueOrDefault(max), added);
        }
        int corpseDamage = GolemStat(gem.Stats, Stat.DamagePlusPctIfYouHaveConsumedACorpseRecently);
        if (corpseDamage != 0 && player.RecentEvents.Happened(StatConditions.ConsumedCorpse, AreaTime(player)))
            stats[Stat.DamagePlusPct] = ActorScale.Add(Stat.DamagePlusPct, stats.GetValueOrDefault(Stat.DamagePlusPct), corpseDamage);
        int globalMinionShare = PlayerMinionDamageShare(caster);
        int skillMinionShare = Math.Max(gem.Stats.GetValueOrDefault(Stat.ActiveSkillAdditiveMinionDamageModifiersApplyToAllDamageAtPctValue),
            caster.GetValueOrDefault(Stat.DominatingBlowAndAbsolutionAdditiveMinionDamageModifiersApplyToYouAt150PctValue) != 0
                && skills.EffectId(gem.Effect) is { } effect && (effect.StartsWith("Absolution", StringComparison.Ordinal)
                    || effect.StartsWith("DominatingBlow", StringComparison.Ordinal)) ? 150 : 0);
        if (skillMinionShare > 0)
        {
            // Only the strongest transfer applies. The character snapshot already carries the global share.
            long increased = (long)stats.GetValueOrDefault(Stat.MinionDamagePlusPct) * Math.Max(globalMinionShare, skillMinionShare) / 100
                - (long)caster.GetValueOrDefault(Stat.MinionDamagePlusPct) * globalMinionShare / 100;
            stats[Stat.DamagePlusPct] = ActorScale.Saturate(stats.GetValueOrDefault(Stat.DamagePlusPct) + increased);
        }
        if (skills.IsVaal(gem.Effect) && stats.GetValueOrDefault(Stat.VaalSkillDamagePlusPct) is int vaalDamage and not 0)
            stats[Stat.DamagePlusPct] = ActorScale.Add(Stat.DamagePlusPct, stats.GetValueOrDefault(Stat.DamagePlusPct), vaalDamage);
        if (charges is not null)
            foreach (var (type, count) in charges)
                if (Stats.Key($"current_removable_{Charges.Name(type)}_charges") is uint removable) stats[removable] = count;
        if (totem && Stat.DamagePlusPct is var damage)
            stats[damage] = stats.GetValueOrDefault(damage) + GolemStat(stats, Stat.TotemDamagePlusPct);
        var placed = PlacedKind(gem);
        if (placed is { Trap: true } or { Mine: true })
        {
            uint placedDamage = placed.Mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct;
            long increased = (long)GolemStat(stats, placedDamage) + GolemStat(stats, Stat.TrapOrMineDamagePlusPct);
            if (increased != 0) stats[Stat.DamagePlusPct] = ActorScale.Saturate(stats.GetValueOrDefault(Stat.DamagePlusPct) + increased);
            if (placed.Mine && GolemStat(stats, Stat.MineAreaDamagePlusPctIfDetonatedMineRecently) is int recentArea and not 0
                && player.RecentEvents.Happened(StatConditions.DetonatedMine, AreaTime(player)))
                stats[Stat.AreaDamagePlusPct] = ActorScale.Add(Stat.AreaDamagePlusPct, GolemStat(stats, Stat.AreaDamagePlusPct), recentArea);
        }
        int Get(uint name) => name == Stat.IsAreaDamage && area is bool isArea ? isArea ? 1 : 0
            : name is var k ? stats.GetValueOrDefault(k) : 0;
        bool attack = SkillsFor(player).IsAttack(gem.Effect);
        var types = Supports.Of(gem.Effect)?.Types.ToHashSet() ?? [];
        // Support-converted traps and mines receive the same hit modifiers as native placed skills.
        if (placed.Trap) types.Add(32);
        if (placed.Mine) types.Add(35);
        if (skills.IsRetaliation(gem.Effect))
        {
            if (Get(Stat.RetaliationSkillDamagePlusPct) is int retaliation and not 0)
                stats[Stat.DamagePlusPct] = ActorScale.Add(Stat.DamagePlusPct, Get(Stat.DamagePlusPct), retaliation);
            ApplyRetaliationAilmentDuration(stats);
        }
        // A strike and the projectiles it releases each use their own damage modifiers.
        bool projectileStrike = IsLightningStrike(gem.Effect) || IsFrostBlades(gem.Effect);
        if (projectileStrike) types.Remove(projectile ? 19 : 2);
        bool volcanicFissure = IsVolcanicFissure(gem.Effect);
        if (volcanicFissure) types.Remove(projectile ? 19 : 2);
        if (area is true) types.Add(7);
        else if (area is false) types.Remove(7);
        if (area is true && IsGalvanicArrow(SkillsFor(player).EffectId(gem.Effect))
            && Get(Stat.GalvanicArrowAreaDamagePlusPct) is int galvanic and not 0)
            stats[Stat.AreaDamagePlusPct] = ActorScale.Saturate((long)Get(Stat.AreaDamagePlusPct) + galvanic);
        if (IsSmite(gem.Effect) && area is true) types.Remove(19);
        bool molten = GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(gem.Effect));
        if (molten)
        {
            if (projectile) { types.Remove(19); types.Add(2); }
            else { types.Remove(2); types.Remove(7); }
        }
        var plan = SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality);
        if (!plan.CanHit) return null;
        bool secondary = plan.Kind == SkillHitKind.Secondary;
        var attackWeapon = attack ? weapon ?? WeaponFor(player) : null;
        var coefficients = SkillsFor(player).DamageCoefficients(gem.Effect, gem.Level);
        var raw = secondary ? SecondaryRawHit(player, gem, plan.SpellBaseWith(Get), Get)
            : attackWeapon is not null
            ? SupportedAttackDamage.Raw(attackWeapon, Get, new(WeaponAccuracyFor(player, weapon, caster, gem.Stats, gem.Effect)),
                coefficients.Base, coefficients.Added, melee: types.Contains(19) && !SkillsFor(player).RequiresShield(gem.Effect))
            : DamageStats.RawAdded(plan.SpellBaseWith(Get), Get, spell: true, AddedEffectiveness(player, gem.Effect, gem.Level));
        if (!attack && !secondary && gem.Stats.GetValueOrDefault(Stat.SpellBaseFireDamagePctMaximumLife) is int lifePct and > 0)
        {
            double fire = (long)player.PlayerMaxLife * lifePct / 100d;
            raw = raw.Add(GameServer.Core.World.DamageType.Fire, new(fire, fire));
        }
        if (!attack && !secondary && Get(Stat.KeystoneBattlemage) != 0
            && WeaponIn(player, ActiveMainHand(player))?.LocalDamage is { } mainHandDamage)
            raw = raw.Add(mainHandDamage.Scale(AddedEffectiveness(player, gem.Effect, gem.Level)));
        if (!attack && !secondary && addedChaos > 0)
            raw = raw.Add(GameServer.Core.World.DamageType.Chaos,
                new GameServer.Core.World.DamageRange(addedChaos, addedChaos).Scale(AddedEffectiveness(player, gem.Effect, gem.Level)));
        if ((projectileStrike || volcanicFissure) && projectile)
            raw = raw.Scale(Math.Max(0, 1 + Get(Stat.ActiveSkillHitAilmentDamageWithProjectilePlusPctFinal) / 100.0));
        // Spellblade: a supported spell gains a share of the one-hand melee weapon's damage as added spell damage,
        // scaled by the spell's damage effectiveness like any other added damage.
        if (!attack && !secondary && Get(Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage) is int bladeGain and > 0)
        {
            if (ActiveMainHand(player) is { Type: { } bladeType } bladeHand && OneHandMelee(bladeType))
            {
                var offHand = ActiveOffHand(player);
                var offWeapon = WeaponIn(player, offHand);
                long gain = (long)bladeGain * (offWeapon is null ? 100 : 60);
                double effectiveness = AddedEffectiveness(player, gem.Effect, gem.Level);
                // Only the item's damage transfers; global additions and attack-only grants are not weapon damage.
                // Floor each hand's transfer before spell effectiveness. Keep the percentage numerator integral
                // until division so a floating-point multiplier cannot move a whole-number endpoint below its floor.
                void AddWeapon(SkillCatalog.Unarmed blade)
                {
                    foreach (var (type, range) in blade.LocalDamage!.Components)
                        raw = raw.Add(type, new GameServer.Core.World.DamageRange(
                            Math.Floor(range.Min * gain / 10_000d),
                            Math.Floor(range.Max * gain / 10_000d)).Scale(effectiveness));
                }
                AddWeapon(WeaponIn(player, bladeHand)!);
                if (offHand is { Type: { } offType } && OneHandMelee(offType))
                    AddWeapon(offWeapon!);
            }
        }
        if (molten && projectile && Get(Stat.ActiveSkillHitAilmentDamageWithProjectilePlusPctFinal) is int projFinal and not 0)
            raw = raw.Scale(Math.Max(0, 1 + projFinal / 100.0));
        if (totem && Get(Stat.StygianSpireTotemDamagePlusPctFinalPerMaximumTotem) is int totemMore and not 0)
            raw = raw.Scale(Math.Max(0, 1 + totemMore * (double)TotemAllowance(player, gem.Effect, own: gem.Stats, stats) / 100));
        // Phase Run's damage buff: more physical damage for the player's own melee attacks (a totem's are not its own).
        if (attack && !totem && types.Contains(19) && Get(Stat.PhaseRunMeleePhysicalDamagePlusPctFinal) is int phase and not 0)
            raw = raw.Add(GameServer.Core.World.DamageType.Physical,
                raw[GameServer.Core.World.DamageType.Physical].Scale(Math.Max(-1, phase / 100.0)));
        // The Frenzy gem's "5% more Attack Damage per Frenzy Charge": its hit and the ailments from it.
        if (attack && Get(Stat.FrenzySkillAttackDamagePlusPctFinalPerFrenzyCharge) is int frenzyMore and not 0)
            raw = raw.Scale(Math.Max(0, 1 + (double)frenzyMore * Get(Stat.CurrentFrenzyCharges) / 100));
        // Manaforged Arrows: "Supported Skills deal 1% more Damage with Hits and Ailments per 1 Mana Cost".
        if (Get(Stat.SupportManaforgedArrowsDamagePlusPctFinalPerManaSpent) is int manaforgedPerMana and not 0
            && !Lifetap.ConvertsCostToLife(gem.Stats, stats))
        {
            int manaCost = Math.Max(0, Get(Stat.ManaCost));
            if (Math.Clamp(Get(Stat.BaseAttackSkillCostLifeInsteadOfManaPct), 0, 100) is int split and > 0
                && attack && gem.Stats.TryGetValue(Stat.ManaCost, out int baseMana))
                (manaCost, _, _) = ManaRecoveryRules.SplitCostToLife(baseMana, Get, skills.IsChannelled(gem.Effect),
                    SkillCostMore(player, gem.Stats), split);
            if (manaCost > 0)
                raw = raw.Scale(Math.Max(0, 1 + (double)manaforgedPerMana * manaCost / 100.0));
        }
        if (IsDischarge(skills.EffectId(gem.Effect)) && Get(Stat.DischargeDamagePlusPctFinal) is int dischargeMore and not 0)
            raw = raw.Scale(Math.Max(0, 1 + dischargeMore / 100d));
        if (skills.EffectId(gem.Effect) is "RollingMagma" or "RollingMagmaAltX" or "RollingMagmaAltY" or "RollingMagmaRoyale"
            && Get(Stat.ThresholdJewelMagmaOrbDamagePlusPctFinal) is int magmaMore and not 0)
            raw = raw.Scale(Math.Max(0, 1 + magmaMore / 100d));
        if (gem.Stats.GetValueOrDefault(Stat.ActiveSkillDamagePlusPctFinal) is int skillDamage and not 0)
            raw = raw.Scale(Math.Max(0, 1 + skillDamage / 100d));
        var modifiers = gem.Supports.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)).ToList();
        modifiers.AddRange(gem.Stats.Where(s => s.Key == Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinal).Select(s => (s.Key, s.Value)));
        // Elemental Overload's per-skill window (see WithElementalOverload): this skill's hits and ailments, including a
        // totem's or a trap's use of it, gain the bonus while its own eight-second crit window is open.
        if (HasElementalOverload(stats)) AddElementalOverload(player, SkillsFor(player).EffectId(gem.Effect), stats);
        Func<uint, int> hitStats = attackWeapon is null || secondary ? Get : SupportedAttackDamage.HitModifiers(attackWeapon, Get);
        var conversion = DamageStats.Conversion(stats, Stats, secondary ? null : attack);
        if (attackWeapon is not null && !secondary)
            SupportedAttackDamage.WithWeaponGains(conversion, attackWeapon, Get);
        return new(raw, hitStats, conversion, attack,
            Melee: attack && types.Contains(19), Area: attack ? types.Contains(7) : Get(Stat.IsAreaDamage) != 0,
            Projectile: attack ? types.Contains(2) : Get(Stat.BaseIsProjectile) != 0, modifiers, types, plan, caster,
            WieldingWand: attackWeapon?.WieldingWand == true, WieldingBow: attackWeapon?.Bow == true, Secondary: secondary)
        {
            FrozenStats = new Dictionary<uint, int>(stats),
            WeaponIncreased = attackWeapon is not null && !secondary ? SupportedAttackDamage.WeaponIncrease(attackWeapon, Get,
                gem.Stats.GetValueOrDefault(Stat.BaseProjectileSpeedPlusPct))
                : !attack && !secondary && Get(Stat.SpellStaffDamagePlusPct) is int staff and not 0
                    && WeaponTypeDamage.FamilyOf(WeaponFor(player).ItemClass) == "staff" ? staff : 0,
            WeaponTypedIncreases = attackWeapon is null || secondary ? default : WeaponTypeDamage.Of(attackWeapon, Get),
            Armed = attackWeapon is not null && !secondary && WeaponAttributeDamage.Armed(attackWeapon),
            ReleaseAilmentMore = gem.Stats.ContainsKey(Stat.SupportOverheatAilmentDamagePlusPctFinalPer3FortificationConsumed)
                ? OverheatAilmentMore(player, skillId) : 1,
            Claw = attackWeapon is not null && !secondary
                && WeaponIdentity.CountsAs(attackWeapon.ItemClass, attackWeapon.LocalSources, "Claw"),
        };
    }

    public static GameServer.Core.World.DamagePacket SupportedHitFor(Player player, ushort skillId, bool suppress, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, IReadOnlyDictionary<ChargeType, int>? charges = null, long additionalIncreased = 0, bool projectile = false, int addedChaos = 0)
        => SupportedHitInputsFor(player, skillId, area, weapon, charges, projectile: projectile, addedChaos: addedChaos) is { } inputs
            ? SupportedHitFrom(inputs, suppress, additionalIncreased) : GameServer.Core.World.DamagePacket.Empty;

    // The supported hit from inputs already worked out (GemSkills reads one gem's inputs once for its hit and ailment rows).
    public static GameServer.Core.World.DamagePacket SupportedHitFrom(SupportedHitInputs inputs, bool suppress,
        long additionalIncreased = 0)
    {
        var hit = PlayerHitDamage.Resolve(inputs.Raw, WeaponAttributeDamage.Read(inputs.Armed, inputs.Get), inputs.Conversion, spell: !inputs.Attack && !inputs.Secondary,
            melee: inputs.Melee, area: inputs.Area, projectile: inputs.Projectile,
            supportModifiers: inputs.Modifiers, skillTypes: inputs.Types,
            wieldingBow: inputs.WieldingBow, wieldingWand: inputs.WieldingWand, retaliation: inputs.Attack && inputs.Types.Contains(120),
            secondary: inputs.Secondary, additionalIncreased: additionalIncreased + inputs.WeaponIncreased,
            additional: inputs.WeaponTypedIncreases, trackPhysicalOrigin: inputs.Melee && !inputs.Secondary
                && inputs.Get(Stat.SupportBloodlustMeleePhysicalDamagePlusPctFinalVsBleedingEnemies) != 0);
        if (inputs.Attack) hit = MaximumAttackDamage.Apply(Volatility.Apply(RageStats.ApplyAttack(hit, inputs.Get), inputs.Get), inputs.Get);
        if (inputs.Melee && !inputs.Secondary) hit = WithVaalWarchiefBuff(hit, inputs.Get);
        double intensityMore = SupportedHitIntensityMore(inputs);
        if (intensityMore != 1) hit = hit.Scale(intensityMore);
        hit = ApplyRoilingTempest(hit, inputs.Get);
        return suppress ? new SkillDamageContext(inputs.Plan.Kind, inputs.Caster).Hit(hit) : hit;
    }

    internal static DamagePacket ApplyRoilingTempest(DamagePacket hit, Func<uint, int> get)
        => get(Stat.KeystoneLightningPurist) == 0 ? hit : hit.Select((type, range) =>
            type == DamageType.Lightning ? new DamageRange(range.Min * .5, range.Max * 1.25) : range);

    static double SupportedHitIntensityMore(SupportedHitInputs inputs)
    {
        double intensityMore = 1;
        if (inputs.Projectile)
            intensityMore *= Math.Max(0, 1 + inputs.Get(Stat.SupportGreaterProjectileIntensityProjectileDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + inputs.Get(Stat.SupportGreaterProjectileIntensityProjectileDamagePlusPctFinalFromIntensity) / 100d);
        if (inputs.Area)
            intensityMore *= Math.Max(0, 1 + inputs.Get(Stat.AreaDamagePlusPctFinalFromSpellBoostCharges) / 100d);
        return intensityMore;
    }

    // The damaging-ailment rows of a supported skill's hit in one context (AilmentPanelStats): the main or off hand of
    // an attack with that hand's weapon, or a spell's / secondary-damage skill's hit.
    public static List<(uint Stat, int Value)> SupportedAilmentRows(Player player, ushort skillId, string hand,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, bool projectile = false, int addedChaos = 0)
        => SupportedHitInputsFor(player, skillId, weapon: weapon, projectile: projectile, addedChaos: addedChaos) is { } inputs
            ? SupportedAilmentRowsFrom(inputs, hand, additionalIncreased) : [];

    public static List<(uint Stat, int Value)> SupportedAilmentRowsFrom(SupportedHitInputs inputs, string hand,
        long additionalIncreased = 0)
    {
        var context = new AilmentPanelStats.Context(inputs.Secondary ? "secondary" : hand, Spell: !inputs.Attack && !inputs.Secondary, inputs.Melee);
        return AilmentPanelStats.Compute(context, inputs.Raw, inputs.Conversion, inputs.Get, additionalIncreased);
    }

    public static AttackAccuracyProfile SupportedAccuracy(Player player, ushort skillId)
    {
        var gem = SupportedSkill(player, skillId);
        var playerStats = CharacterPlayerStats(player);
        return AttackAccuracyProfile.From(player.Pools, playerStats, Stats, gem.Stats, MainHandLocalSources(player))
            with { Rating = WeaponAccuracyFor(player, null, playerStats, gem.Stats, gem.Effect) };
    }

    public static int SupportedCriticalChance(Player player, ushort skillId) => SupportedCriticalChanceFor(player, skillId, false);

    // A release retains the unprohibited offensive chance; the hit evaluates current owner prohibitions separately.
    public static int SupportedReleaseCriticalChance(Player player, ushort skillId) => SupportedCriticalChanceFor(player, skillId, true);

    // extraIncrease: a target condition's increase (critical_strike_chance_+%_vs_blinded_enemies, Game's Blind.cs).
    // weaponCritical: the swinging hand's weapon base (AttackHands' off hand) in the main hand's place.
    public static int SupportedCriticalChanceFor(Player player, ushort skillId, bool release, long extraIncrease = 0,
        int? weaponCritical = null)
    {
        var gem = SupportedSkill(player, skillId);
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return 0;
        // Inspiration: "Supported Skills have (6-11)% increased Critical Strike Chance per Inspiration Charge".
        if (player.SupportGems.InspirationCharges > 0
            && Stat.CriticalStrikeChancePlusPctPerRighteousCharge is var chargeCrit)
            extraIncrease += (long)gem.Stats.GetValueOrDefault(chargeCrit) * player.SupportGems.InspirationCharges;
        var playerStats = FinalPlayerStats(player).ToDictionary(v => v.Item1, v => v.Item2);
        int Own(uint name) => release && IsCriticalProhibition(name) ? 0
            : gem.Stats.GetValueOrDefault(name);
        int Player(uint name) => release && IsCriticalProhibition(name) ? 0
            : playerStats.GetValueOrDefault(name);
        var skills = SkillsFor(player);
        var placed = PlacedKind(gem);
        if (skills.IsVaal(gem.Effect))
            extraIncrease += (long)Own(Stat.VaalSkillCriticalStrikeChancePlusPct) + Player(Stat.VaalSkillCriticalStrikeChancePlusPct);
        // The per-use critical chance families read the skill's own tags, not the hit: a totem skill by its
        // resolved is_totem, a mine/trap by PlacedKind. They join extraIncrease, which both branches fold in.
        if (gem.Stats.GetValueOrDefault(Stat.IsTotem) != 0)
            extraIncrease += (long)Player(Stat.TotemCriticalStrikeChancePlusPct) + Own(Stat.TotemCriticalStrikeChancePlusPct);
        if (placed.Mine)
        {
            extraIncrease += (long)Player(Stat.MineCriticalStrikeChancePlusPct) + Own(Stat.MineCriticalStrikeChancePlusPct);
            if (Own(Stat.MineCriticalStrikeChancePlusPctPerPowerCharge) is int minePerPower and not 0)
                extraIncrease += (long)minePerPower * Player(Stat.CurrentPowerCharges);
        }
        else if (placed.Trap)
            extraIncrease += (long)Player(Stat.TrapCriticalStrikeChancePlusPct) + Own(Stat.TrapCriticalStrikeChancePlusPct);
        if (skills.IsAttack(gem.Effect))
        {
            if (new[] { Stat.GlobalCannotCrit, Stat.VirtualGlobalCannotCrit, Stat.CannotCriticalStrikeWithAttacks }
                .Any(name => Player(name) != 0 || Own(name) != 0)) return 0;
            var weapon = WeaponProfileFor(player, activeSet: true);
            int basis;
            if (skills.RequiresShield(gem.Effect) && skills.AttackCritChance(gem.Effect, gem.Level) is int shieldCritical)
            {
                basis = shieldCritical;
                int per10 = Own(Stat.OffHandCriticalStrikeChancePlusPer10EsOnShield);
                if (per10 != 0 && ActiveShieldDefences(player) is { EnergyShield: > 0 } shield)
                    basis = ActorScale.Saturate((long)basis + (shield.EnergyShield / 10) * (long)per10);
            }
            else
                basis = weapon.Unarmed && (!skills.IsMelee(gem.Effect) || skills.IsSpell(gem.Effect)) ? 0
                    : AttackBaseCriticalStrike.OfMainHand(!weapon.Unarmed, weaponCritical ?? weapon.Critical, Player);
            if (Own(Stat.AdditionalCriticalStrikeChancePermyriadWhileAffectedByElusive) is int elusiveBase and not 0
                && ElusiveFactor(player) > 0)
                basis = ActorScale.Saturate((long)basis + elusiveBase);
            long increased = player.Pools.CriticalStrikeChancePct + extraIncrease
                + AttackBaseCriticalStrike.IncreasedSum(weapon, name => Own(name));
            int chance = AttackBaseCriticalStrike.ChanceOfBase(basis, increased, Player, Own);
            // Power Siphon's "10% more Critical Strike Chance per Power Charge".
            if (Own(Stat.CriticalStrikeChancePlusPctFinalPerPowerChargeFromPowerSiphon) is int siphon and not 0)
                chance = (int)Math.Clamp(chance * (1 + (double)siphon * Player(Stat.CurrentPowerCharges) / 100), 0, 10_000);
            return DisplayedCriticalChance(player, chance, Player);
        }
        if (SkillsFor(player).IsBrand(gem.Effect) || Own(Stat.SkillGetsBrandDamageCritModifiers) != 0)
            extraIncrease += Player(Stat.SigilCriticalStrikeChancePlusPct) + Own(Stat.SigilCriticalStrikeChancePlusPct);
        if (Player(Stat.SpellCriticalStrikeChancePlusPctPerRaisedSpectre) is int perSpectre and not 0)
            extraIncrease += (long)perSpectre * LivingSummons(player, "Spectre", AreaTime(player));
        return DisplayedCriticalChance(player, SpellCriticalStrike.Chance(SkillsFor(player).SpellCritChance(gem.Effect, gem.Level),
            Player, Own, player.Pools.SpellCriticalStrikeChancePct + extraIncrease), Player);
    }

    public static SkillCatalog.ReservationCost? SupportedReservationCost(Player player, ushort skillId)
    {
        var gem = SupportedSkill(player, skillId);
        if (SkillsFor(player).EffectId(gem.Effect) is not { } effectId) return null;
        if (IsBlessing(gem)) return null;
        var sourceReservation = SkillsFor(player).Reservation(gem.Effect, gem.Level);
        if (CurseCatalog.Hex(effectId) || effectId == "TemporalChains")
        {
            var blasphemy = gem.Supports.FirstOrDefault(s => IsBlasphemySupport(s.Effect.Id));
            if (blasphemy is null) return null;
            sourceReservation = SkillsFor(player).Reservation(blasphemy.Effect.Row, blasphemy.Gem.Level);
            var curseSources = StatSourceValues(player);
            if (effectId == "TemporalChains" && Stat.TemporalChainsNoReservation is var free
                && curseSources.GetValueOrDefault(free) != 0) return new(0, 0, 0, 0);
        }
        if (sourceReservation is not { } reservation) return null;
        if (gem.Stats.GetValueOrDefault(Stat.BaseSkillNoReservation) != 0
            || gem.Stats.GetValueOrDefault(Stat.AuraSkillNoReservation) != 0)
            return new(0, 0, 0, 0);
        var sources = StatSourceValues(player);
        int Own(uint name) => GolemStat(gem.Stats, name);
        int Get(uint name) => Own(name) + (name is var key
            ? sources.GetValueOrDefault(key) : 0);
        int manaFlat = reservation.ManaFlat, manaPercent = reservation.ManaPercent;
        int lifeFlat = reservation.LifeFlat, lifePercent = reservation.LifePercent;
        bool curse = gem.Supports.Any(s => IsBlasphemySupport(s.Effect.Id));
        bool lifeInstead = ReservesLifeInstead(player, gem, curse, Get);
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
        decimal manaFactor = Get(Stat.KeystoneSupremeEgo) != 0
            && SkillsFor(player).IsAura(gem.Effect, gem.Supports.Select(s => s.Effect.Row)) ? factor * 1.4m : factor;
        return new((int)Math.Floor(manaFlat * manaFactor), (int)Math.Floor(manaPercent * manaFactor),
            (int)Math.Floor(lifeFlat * factor), (int)Math.Floor(lifePercent * factor));
    }

    // Compatibility view for one-pool callers: select the resource the caller's context will use, never a Mana-only
    // projection that would erase a Life cost. Full callers use SupportedReservationCost's four raw slots.
    public static (int Flat, int Percent)? SupportedReservation(Player player, ushort skillId)
    {
        if (SupportedReservationCost(player, skillId) is not { } cost) return null;
        var context = ReservationContext(player, skillId);
        bool explicitLife = cost.LifeFlat != 0 || cost.LifePercent != 0;
        return context.Life
            ? (explicitLife ? cost.LifeFlat : cost.ManaFlat, explicitLife ? cost.LifePercent : cost.ManaPercent)
            : (cost.ManaFlat, cost.ManaPercent);
    }

    public static int SupportedCriticalMultiplier(Player player, ushort skillId) =>
        SupportedCriticalMultiplier(player, skillId, hitWeapon: null);

    static int SupportedCriticalMultiplier(Player player, ushort skillId, SkillCatalog.Unarmed? hitWeapon)
    {
        var gem = SupportedSkill(player, skillId);
        long extra = (long)gem.Stats.GetValueOrDefault(Stat.BaseCriticalStrikeMultiplierPlus) + NightbladeCriticalMultiplier(player, gem.Stats);
        var playerStats = CharacterPlayerStats(player);
        long Sum(uint key) => (long)playerStats.GetValueOrDefault(key) + gem.Stats.GetValueOrDefault(key);
        if (Sum(Stat.CriticalStrikeMultiplierPlusIfYouveBeenChannellingForAtLeast1Second) is long channelBonus and not 0
            && player.PaidChannel is { } channel && ReferenceEquals(channel.Rules, CombatLifetime) && channel.Rules.IsActive
            && double.IsFinite(channel.StartedAt) && ChannelCurrent(player, channel.Skill, channel.Action, channel.Generation))
        {
            double remaining = channel.StartedAt + 1 - AreaTime(player);
            if (remaining <= 0) extra += channelBonus;
            else StatsChangeIn(remaining);
        }
        var skills = SkillsFor(player);
        var placed = PlacedKind(gem);
        if (placed.Trap) extra += Sum(Stat.TrapCriticalStrikeMultiplierPlus);
        if (placed.Mine) extra += Sum(Stat.MineCriticalStrikeMultiplierPlus);
        bool lightning = skills.HasType(gem.Effect, "Lightning");
        bool fire = skills.HasType(gem.Effect, "Fire");
        bool cold = skills.HasType(gem.Effect, "Cold");
        if (lightning) extra += Sum(Stat.LightningCriticalStrikeMultiplierPlus);
        if (fire) extra += Sum(Stat.FireCriticalStrikeMultiplierPlus);
        if (cold) extra += Sum(Stat.ColdCriticalStrikeMultiplierPlus);
        if (skills.HasType(gem.Effect, "Chaos")) extra += Sum(Stat.ChaosCriticalStrikeMultiplierPlus);
        // "with elemental skills" is one value with three flags set and keyword flags match any of them, so it
        // counts once for the skill, never once per element dealt.
        if (fire || cold || lightning) extra += Sum(Stat.ElementalCriticalStrikeMultiplierPlus);
        // The skill's own "+X% to Critical Strike Multiplier per Power Charge" (Power Siphon): the character's own
        // per-charge stats are already in its stats (ChargeEffects), this one only on the skill.
        if (GolemStat(gem.Stats, Stat.CriticalStrikeMultiplierPlusPerPowerCharge) is int perPower and not 0)
            extra += perPower * GolemStat(playerStats, Stat.CurrentPowerCharges);
        if (GolemStat(gem.Stats, Stat.TrapCriticalStrikeMultiplierPlusPerPowerCharge) is int trapPerPower and not 0
            && PlacedKind(gem) is (true, false))
            extra += trapPerPower * GolemStat(playerStats, Stat.CurrentPowerCharges);
        if (SkillsFor(player).IsBrand(gem.Effect) || GolemStat(gem.Stats, Stat.SkillGetsBrandDamageCritModifiers) != 0)
            extra += GolemStat(playerStats, Stat.SigilCriticalStrikeMultiplierPlus) + GolemStat(gem.Stats, Stat.SigilCriticalStrikeMultiplierPlus);
        bool attack = SkillsFor(player).IsAttack(gem.Effect);
        if (attack && Sum(Stat.CriticalStrikeMultiplierPlusPctWithClawsDaggers) != 0)
            extra += ClawDaggerCriticalBonus(hitWeapon ?? WeaponFor(player), playerStats, gem.Stats);
        return CriticalHitMultiplier((long)(attack ? AttackCriticalMultiplier(player, playerStats, gem.Effect, gem.Stats)
            : player.Pools.SpellCriticalStrikeMultiplier) + extra, playerStats, gem.Stats);
    }
}
