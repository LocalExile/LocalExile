using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal static readonly GemSupports Supports = new();
    static readonly Lazy<WornGrantedSkills> WornGrantedSkillCatalog =
        new(() => new(Skills, Items.Mods));
    static readonly Lazy<WornGrantedSkills> RuthlessWornGrantedSkillCatalog =
        new(() => new(HardModeSkills.Value, HardModeItems.Value.Mods, ruthless: true));

    internal sealed record SupportedGem(int Effect, int Level, int Quality, IReadOnlyList<GemSupports.Applied> Supports,
        Dictionary<uint, int> Stats, Item? SourceGem = null, bool RequirementsMet = true, bool Granted = false,
        WornGrantedSkills.Grant? WornGrant = null, uint Host = 0, int Group = -1);

    internal sealed record PlayerSkillSource(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem,
        GemSkillIds.Gem Identity, Item? SourceGem, WornGrantedSkills.Grant? WornGrant,
        PassiveGrantedSkills.Grant? PassiveGrant, bool Active);

    /// <summary>
    /// Source-identity owner for item grants in this connection's data mode. Later trigger, minion, aspect and
    /// temporary-state owners should resolve their sources through this (and <see cref="AllWornGrantedSkillsOf"/>)
    /// and validate retained work with <see cref="WornGrantedSkills.StillWorn"/>, never from a translated mod name.
    /// </summary>
    internal static WornGrantedSkills WornGrantedSkillOwner(ClientState conn) =>
        LeagueFor(conn).HardMode ? RuthlessWornGrantedSkillCatalog.Value : WornGrantedSkillCatalog.Value;

    /// <summary>Every item-granted source currently present on the supplied equipment, including unsupported/triggered rows.</summary>
    internal static IReadOnlyList<WornGrantedSkills.Grant> AllWornGrantedSkillsOf(ClientState conn,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null) =>
        WornGrantedSkillOwner(conn).For(equipment ?? ItemsOf(conn).EquipmentSnapshot());

    /// <summary>
    /// Item-granted active skills whose real executor exists and which the game marks manually cast. Trigger-only rows
    /// remain available from <see cref="AllWornGrantedSkillsOf"/> for their future trigger owner, but never enter this
    /// manually published list. Unsupported grants are likewise not presented as usable Actor skills.
    /// </summary>
    internal static IReadOnlyList<WornGrantedSkills.Grant> WornGrantedSkillsOf(ClientState conn,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null)
    {
        equipment ??= ItemsOf(conn).EquipmentSnapshot();
        return [.. AllWornGrantedSkillsOf(conn, equipment).Where(g => !g.Support && g.ManuallyCast
            && LeagueFor(conn).Rules.AllowsGemEffect(g.EffectId)
            && (g.EffectId == UnhingeEffect || IsDashEffect(g.EffectId)
                || IsSummonBeastialCompanionEffect(g.EffectId)
                || AuraCatalog.Supported(g.EffectId)
                || GameEvents.GrantedSkillExecutable.For(conn).Any(accept => accept(conn, g.EffectId))))];
    }

    internal static bool IsDashEffect(string? effectId) => effectId is "Dash" or "DashHardMode" or "DashArborix";
    internal static SupportedGem SupportedSkill(ClientState conn, ushort skillId)
    {
        if (BuildScopeOf(conn) is not { } scope)
            lock (conn.Monsters) return SupportedSkillAt(conn, skillId, null);
        // Within one PlayerSkills build each gem is resolved once; callers get their own copy of its stats.
        if (!scope.Supported.TryGetValue(skillId, out var gem))
            lock (conn.Monsters) scope.Supported[skillId] = gem = SupportedSkillAt(conn, skillId, null);
        return gem with { Stats = new(gem.Stats) };
    }

    internal static SupportedGem SupportedSkillAt(ClientState conn, ushort skillId, GemRequirementSnapshot? requirements)
    {
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
        var weapon = equipment.Where(e => e.Row == PlayerInventories.Weapon1).Select(e => e.Item).FirstOrDefault();
        int? weaponClass = weapon is null ? null : ItemsFor(conn).ByHash(weapon.Item.TypeHash)?.ClassRow;
        var supports = Supports.Resolve(selected.Effect, candidates, SkillsFor(conn), weaponClass);
        int Value(string stat) => Stats.Key(stat) is uint k ? supports.Sum(s => s.Stats.Where(v => v.Key == k).Sum(v => v.Value)) : 0;
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
        int quality = Quality(selected.Place.Socket, selected.Place.Gem) + Value("supported_active_skill_gem_quality_%")
            + GemQualityBonus(conn, selected.Place.Gem);
        var own = SkillsFor(conn).EffectStats(selected.Effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        ApplySupportStats(conn, own, supports);
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
    internal static IReadOnlyList<PlayerSkillSource> PlayerSkillSourcesAt(string? character,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment, IReadOnlySet<uint>? inactive, LeagueRuntime league,
        IReadOnlyList<PassiveGrantedSkills.Grant> granted, IReadOnlyList<WornGrantedSkills.Grant> worn)
    {
        string name = string.IsNullOrEmpty(character) ? "Exile" : character;
        var catalog = league.HardMode ? HardModeItems.Value : Items;
        var gems = GemRequirementSnapshot.PlacesIn(equipment)
            .Where(p => catalog.ByHash(p.Gem.TypeHash) is not { } type ? !league.HardMode : league.Rules.AllowsGemBase(type.Id))
            .SelectMany(p => catalog.GemEffects(p.Gem).Select(effect => (Place: p, Effect: effect))).ToList();
        var identities = new List<GemSkillIds.Gem>(gems.Count + worn.Count + granted.Count);
        identities.AddRange(gems.Select(g => new GemSkillIds.Gem(g.Place.ItemId, g.Place.Socket, g.Effect)));
        identities.AddRange(worn.Select(g => new GemSkillIds.Gem(g.Host, g.Socket, g.Effect, GemSkillIds.SourceKind.WornGrant)));
        identities.AddRange(granted.Select(g => new GemSkillIds.Gem(g.Host, 0, g.Effect, GemSkillIds.SourceKind.PassiveGrant)));
        var ids = GemSkillIdsOf.GetOrAdd(name, _ => new GemSkillIds()).Assign(identities, GemSkillIdPool.Value);
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

    static SupportedGem WornSupportedSkill(ClientState conn, WornGrantedSkills.Grant grant,
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

    static void ApplySupportStats(ClientState conn, Dictionary<uint, int> own, IReadOnlyList<GemSupports.Applied> supports)
    {
        foreach (var support in supports)
            foreach (var (key, value) in support.Stats)
            {
                string stat = Stats.Name(key) ?? "";
                if (stat is "base_spell_cast_time_ms" or "virtual_base_spell_cast_time_ms" or "hundred_times_casts_per_second"
                    or "casting_spell" or "base_mana_cost" or "mana_cost" or "base_mana_cost_per_minute" or "mana_cost_per_minute") continue;
                own[key] = stat.EndsWith("_final", StringComparison.Ordinal) && own.TryGetValue(key, out int previous)
                    ? (int)Math.Round(100 * ((1 + previous / 100.0) * (1 + value / 100.0) - 1))
                    : own.GetValueOrDefault(key) + value;
            }
        // Machinations: its "per Keystone" stats count the character's allocated keystones. The chaos gain feeds the
        // plain stat the damage conversion reads; the cost increase has no consumer yet (costs read player stats only).
        int keystones = KeystoneCount(conn);
        foreach (string stat in new[] { "skill_cost_+%_per_keystone", "physical_damage_%_to_add_as_chaos_per_keystone" })
            if (Stats.Key(stat) is uint perKey && own.TryGetValue(perKey, out int per))
            {
                own[perKey] = per * keystones;
                if (stat == "physical_damage_%_to_add_as_chaos_per_keystone" && own[perKey] != 0
                    && Stats.Key("physical_damage_%_to_add_as_chaos") is uint plain)
                    own[plain] = own.GetValueOrDefault(plain) + own[perKey];
            }
        double costMultiplier = supports.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0);
        foreach (string stat in new[] { "mana_cost", "base_mana_cost", "mana_cost_per_minute", "base_mana_cost_per_minute", "base_rage_cost", "total_base_rage_cost" })
            if (Stats.Key(stat) is uint k && own.TryGetValue(k, out int cost)) own[k] = (int)Math.Floor(cost * costMultiplier);
        // Global added damage on support gems applies to this skill, not to the character or other link groups.
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
            foreach (string bound in new[] { "minimum", "maximum" })
                if (Stats.Key($"global_{bound}_added_{type}_damage") is uint k && own.TryGetValue(k, out int damage))
                    foreach (string kind in new[] { "attack", "spell" })
                        if (Stats.Key($"{kind}_{bound}_added_{type}_damage") is uint target)
                            own[target] = own.GetValueOrDefault(target) + damage;
    }

    internal static Dictionary<uint, int> SupportedStats(ClientState conn, ushort skillId)
    {
        var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return MergeSkillStats(player, SupportedSkill(conn, skillId).Stats);
    }

    // Generosity: the increased effect a support gives the aura it supports, and whether the aura may affect the player
    // ("Supported Auras do not affect you", aura_cannot_affect_self).
    internal static (int EffectPct, bool AffectsSelf) AuraSupport(ClientState conn, ushort skillId)
    {
        var stats = SupportedSkill(conn, skillId).Stats;
        int Stat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return (Stat("non_curse_aura_effect_+%") + Stat("aura_effect_+%"), Stat("aura_cannot_affect_self") == 0);
    }

    internal static Dictionary<uint, int> MergeSkillStats(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        var result = new Dictionary<uint, int>(player);
        foreach (var (key, value) in own)
        {
            string stat = Stats.Name(key) ?? "";
            bool replace = stat is "mana_cost" or "base_mana_cost" or "casting_spell" or "virtual_base_spell_cast_time_ms"
                or "base_spell_cast_time_ms" or "hundred_times_casts_per_second" || stat.Contains("_base_", StringComparison.Ordinal)
                && (stat.StartsWith("spell_minimum", StringComparison.Ordinal) || stat.StartsWith("spell_maximum", StringComparison.Ordinal));
            result[key] = replace ? value : ArcaneMight.IsGrant(key)
                ? ArcaneMight.Combine(key, result.GetValueOrDefault(key), value) : result.GetValueOrDefault(key) + value;
        }
        if (Stats.Key("mana_cost") is uint mana && own.TryGetValue(mana, out int baseCost))
            result[mana] = ManaRecoveryRules.Cost(baseCost, name => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0);
        return result;
    }

    internal static double SupportedAttackSeconds(ClientState conn, ushort skillId) => SupportedAttackSecondsFor(conn, skillId, null);

    // Multistrike: "Supported Skills Repeat an additional X times" (base_melee_attack_repeat_count). The repeats are
    // hits of the same use, so the skill's attack time is split between them and the support's per-repeat more damage
    // applies to each one after the first.
    internal static int MeleeAttackRepeats(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        if (!SkillsFor(conn).IsAttack(gem.Effect)) return 0;
        return Math.Clamp(Stats.Key("base_melee_attack_repeat_count") is uint key
            ? gem.Stats.GetValueOrDefault(key) : 0, 0, 3);
    }

    internal static double MeleeRepeatDamageMore(ClientState conn, ushort skillId, int repeat)
    {
        if (repeat <= 0) return 1;
        var gem = SupportedSkill(conn, skillId);
        string stat = repeat switch
        {
            1 => "multistrike_damage_+%_final_on_first_repeat",
            2 => "multistrike_damage_+%_final_on_second_repeat",
            _ => "multistrike_damage_+%_final_on_third_repeat",
        };
        return Stats.Key(stat) is uint key ? Math.Max(0, 1 + gem.Stats.GetValueOrDefault(key) / 100.0) : 1;
    }

    // Hallow Support: a melee hit inflicts Hallowing Flame, and the next attack against that enemy gains its share of
    // the attack's physical damage as extra fire (the sweep applies and removes it per target).
    internal static void HallowOnMeleeHit(ClientState conn, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        if (Get("support_hallow_inflict_hallowing_flame_on_melee_hit") == 0) return;
        double seconds = Math.Max(0, Get("hallowing_flame_duration_ms")) / 1000.0;
        if (seconds <= 0) return;
        m.HallowingFlameUntil = Math.Max(m.HallowingFlameUntil, now + seconds);
        m.HallowingFlamePct = 25 * Math.Max(0, 1 + Get("hallowing_flame_magnitude_+%") / 100.0);
    }

    // Excommunicate Support: melee hits excommunicate the enemy for its listed duration; an excommunicated monster's
    // chaos damage is suppressed (the support's physical-as-extra gains already use the conversion stats).
    internal static void ExcommunicateOnMeleeHit(ClientState conn, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        double milliseconds = Math.Max(0, Get("support_excommunicate_on_melee_attack_hit_for_X_ms"));
        if (milliseconds > 0) m.ExcommunicatedUntil = Math.Max(m.ExcommunicatedUntil, now + milliseconds / 1000.0);
    }

    // Devour Support: a killing blow recovers its flat life/mana (the corpse itself is not removed by this server yet).
    internal static void DevourOnKill(ClientState conn, IReadOnlyDictionary<uint, int>? skill)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        int life = Get("killing_blow_consumes_corpse_restore_x_life");
        int mana = Get("killing_blow_consumes_corpse_restore_x_mana");
        if (life > 0 && conn.PlayerLife > 0)
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, (long)conn.PlayerLife + life);
        if (mana > 0 && conn.PlayerLife > 0)
            conn.PlayerMana = (uint)Math.Min(UnreservedMana(conn), (long)conn.PlayerMana + mana);
    }

    // Channelling repeats are not limited by the one-new-action-per-frame cap (Action speed, PoE Wiki): they use the
    // speed-only interval so a very fast channel can hit more than 30 times a second.
    static double SupportedChannelSeconds(ClientState conn, ushort skillId) =>
        SupportedAttackSecondsFor(conn, skillId, null, clampToTick: false);

    // The supported attack's swing time with the off-hand weapon of a dual-wielded pair: its own duration at the
    // off-hand increased speed (the main weapon's local speed taken back out), with the same supports.
    internal static double SupportedOffHandAttackSeconds(ClientState conn, ushort skillId, SkillCatalog.Unarmed offHand) =>
        SupportedAttackSecondsFor(conn, skillId, offHand);

    internal static double SupportedAttackSecondsFor(ClientState conn, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick = true)
    {
        var stats = SupportedStats(conn, skillId);
        int Get(string stat) => Stats.Key(stat) is uint k ? stats.GetValueOrDefault(k) : 0;
        var gem = SupportedSkill(conn, skillId);
        if (Stats.Key("attack_speed_+%") is uint raw && Stats.Key("main_hand_attack_speed_+%") is uint final)
        {
            var profile = WeaponProfileFor(conn);
            double factor = profile.DualWield || profile.Unarmed ? 1.1 : 1;
            stats[final] = stats.GetValueOrDefault(final) + (int)Math.Round(gem.Stats.GetValueOrDefault(raw) * factor);
            // "Chain Hook and Shield Charge have X% increased Attack Speed per 10 Rampage Kills".
            if (SkillsFor(conn).EffectId(gem.Effect) is { } effectId
                && (effectId.StartsWith("ChainHook", StringComparison.Ordinal)
                    || effectId.StartsWith("ShieldCharge", StringComparison.Ordinal))
                && Stats.Key("chain_hook_and_shield_charge_attack_speed_+%_per_10_rampage_stacks") is uint rampageSpeed
                && stats.GetValueOrDefault(rampageSpeed) is int perTen and > 0)
                stats[final] += conn.Rampage.Current.Stacks / 10 * perTen;
        }
        double more = Supports.AttackSpeedMultiplier(gem.Effect, gem.Level) * Math.Max(.01, 1 + Get("support_melee_physical_damage_attack_speed_+%_final") / 100.0)
            * Math.Max(.01, 1 + Get("support_multiple_attacks_melee_attack_speed_+%_final") / 100.0)
            // Pulverise: "15% less Attack Speed".
            * Math.Max(.01, 1 + Get("support_pulverise_attack_speed_+%_final") / 100.0)
            // Vicious Projectiles: "10% less Attack Speed".
            * Math.Max(.01, 1 + Get("support_projectile_attack_speed_+%_final") / 100.0);
        // Rage Support: "19% increased Attack Speed with at least 10 Rage" (the rage stat set carries current_rage).
        if (Get("current_rage") >= 10 && Get("attack_speed_+%_with_at_least_10_rage") is int rageSpeed and > 0
            && Stats.Key("main_hand_attack_speed_+%") is uint rageBucket)
            stats[rageBucket] += rageSpeed;
        // Close Combat's Combat Rush: more Attack Speed for a travel skill it does not support.
        if (conn.CombatRushUntil > conn.Clock.Elapsed.TotalSeconds && SkillsFor(conn).IsTravel(gem.Effect)
            && !gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat"))
            more *= Math.Max(.01, 1 + conn.CombatRushAttackSpeed / 100.0);
        // Supports.AttackSpeedMultiplier reads the skill's own GEPL row too: its inherent -20 on Tornado Shot is
        // already part of this factor. Do not apply the same GEPL value again.
        int baseSpeed = offHand is null ? WeaponFor(conn).IncreasedAttackSpeed(stats)
            : SkillCatalog.Unarmed.OffHandAttackSpeedPct(stats.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct),
                WeaponProfileFor(conn, activeSet: true).LocalAttackSpeedPct);
        double seconds = offHand is null ? WeaponFor(conn).AttackSeconds(stats) : DualWieldPanel.OffHandSeconds(offHand, baseSpeed);
        // Preserve the weapon timing's existing minimum, but compose MORE through the native integer final percent.
        double paced = seconds * Math.Max(.01, 1 + baseSpeed / 100.0)
            / (1 + SkillAttackTiming.FinalSpeedPercent(baseSpeed, more) / 100.0) / PlayerActionFactor(conn);
        return clampToTick ? Math.Max(TickSeconds, paced) : Math.Max(.001, paced);
    }

    internal static GameServer.Core.World.DamagePacket SupportedHit(ClientState conn, ushort skillId)
        => SupportedHitFor(conn, skillId, suppress: true);

    // The allocated keystones a "per Keystone" stat counts: the character's own, and the ones a tattoo turns a node
    // into (the node itself is not a keystone then).
    static int KeystoneCount(ClientState conn)
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
    // support. The buff ends at its listed deadline (the travel skill that uses it does not end it early here).
    internal static void CombatRushOnHit(ClientState conn, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint k ? skill.GetValueOrDefault(k) : 0;
        if (Get("supported_by_slashing") == 0) return;
        int milliseconds = Get("support_slashing_buff_base_duration_ms");
        int speed = Get("support_slashing_buff_attack_speed_+%_final_to_grant");
        if (milliseconds <= 0 || speed <= 0) return;
        conn.CombatRushUntil = Math.Max(conn.CombatRushUntil, now + milliseconds / 1000.0);
        conn.CombatRushAttackSpeed = Math.Max(conn.CombatRushAttackSpeed, speed);
    }

    internal static GameServer.Core.World.DamagePacket SupportedProjectileHit(ClientState conn, ushort skillId, bool area,
        long additionalIncreased = 0)
        => SupportedHitFor(conn, skillId, suppress: false, area, additionalIncreased: additionalIncreased);

    // The same supported attack swung with the off-hand weapon of a dual-wielded attack (the Offence tab's Off Hand rows).
    internal static GameServer.Core.World.DamagePacket SupportedOffHandHit(ClientState conn, ushort skillId, SkillCatalog.Unarmed offHand)
        => SupportedHitFor(conn, skillId, suppress: true, weapon: offHand);

    // A supported skill's hit inputs before conversion and modifiers, shared by the hit itself and the panel's ailment
    // rows, which apply their own buckets to the same raw packet and conversion. Null when the skill cannot hit.
    internal sealed record SupportedHitInputs(GameServer.Core.World.DamagePacket Raw, Func<string, int> Get,
        GameServer.Core.World.DamageConversion Conversion, bool Attack, bool Melee, bool Area, bool Projectile,
        List<(string Name, int Value)> Modifiers, HashSet<int> Types, SkillHitPlan Plan, Dictionary<uint, int> Caster,
        bool WieldingWand = false);

    internal static SupportedHitInputs? SupportedHitInputsFor(ClientState conn, ushort skillId, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, SupportedGem? capturedGem = null)
    {
        var gem = capturedGem ?? SupportedSkill(conn, skillId);
        var caster = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, gem.Stats);
        int Get(string name) => name == "is_area_damage" && area is bool isArea ? isArea ? 1 : 0
            : Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        bool attack = SkillsFor(conn).IsAttack(gem.Effect);
        var types = Supports.Of(gem.Effect)?.Types.ToHashSet() ?? [];
        if (area is true) types.Add(7);
        else if (area is false) types.Remove(7);
        var plan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!plan.CanHit) return null;
        var raw = attack
            ? SpellDamage.WithEffectiveness(SupportedAttackDamage.Raw(weapon ?? WeaponFor(conn), Get), SkillsFor(conn).DamageEffectiveness(gem.Effect, gem.Level))
            : DamageStats.RawAdded(plan.SpellBase, Get, spell: true, AddedEffectiveness(conn, gem.Effect, gem.Level));
        // Spellblade: a supported spell gains a share of the one-hand melee weapon's damage as added spell damage,
        // scaled by the spell's damage effectiveness like any other added damage.
        if (!attack && Get("support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage") is int bladeGain and > 0)
        {
            var bladeWeapon = weapon ?? WeaponFor(conn);
            if (bladeWeapon.Melee && bladeWeapon.LocalDamage is not null)
            {
                double effectiveness = AddedEffectiveness(conn, gem.Effect, gem.Level);
                foreach (var (type, range) in SupportedAttackDamage.Raw(bladeWeapon, Get).Components)
                    raw = raw.Add(type, range.Scale(bladeGain / 100.0 * effectiveness));
            }
        }
        var modifiers = gem.Supports.SelectMany(s => s.Stats).Select(s => (Stats.Name(s.Key) ?? "", s.Value)).ToList();
        modifiers.AddRange(gem.Stats.Select(s => (Name: Stats.Name(s.Key) ?? "", s.Value))
            .Where(s => s.Name == "blood_sand_stance_melee_skills_area_damage_+%_final"));
        return new(raw, Get, DamageStats.Conversion(stats, Stats, attack), attack,
            Melee: attack && types.Contains(19), Area: attack ? types.Contains(7) : Get("is_area_damage") != 0,
            Projectile: attack ? types.Contains(2) : Get("base_is_projectile") != 0, modifiers, types, plan, caster,
            WieldingWand: attack && (weapon ?? WeaponFor(conn)).WieldingWand);
    }

    internal static GameServer.Core.World.DamagePacket SupportedHitFor(ClientState conn, ushort skillId, bool suppress, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0)
        => SupportedHitInputsFor(conn, skillId, area, weapon) is { } inputs
            ? SupportedHitFrom(inputs, suppress, additionalIncreased) : GameServer.Core.World.DamagePacket.Empty;

    // The supported hit from inputs already worked out (GemSkills reads one gem's inputs once for its hit and ailment rows).
    internal static GameServer.Core.World.DamagePacket SupportedHitFrom(SupportedHitInputs inputs, bool suppress,
        long additionalIncreased = 0)
    {
        var hit = PlayerHitDamage.Resolve(inputs.Raw, inputs.Get, inputs.Conversion, spell: !inputs.Attack,
            melee: inputs.Melee, area: inputs.Area, projectile: inputs.Projectile,
            supportModifiers: inputs.Modifiers, skillTypes: inputs.Types,
            wieldingWand: inputs.WieldingWand, retaliation: inputs.Attack && inputs.Types.Contains(120),
            additionalIncreased: additionalIncreased);
        if (inputs.Attack) hit = MaximumAttackDamage.Apply(Volatility.Apply(RageStats.ApplyAttack(hit, inputs.Get), inputs.Get), inputs.Get);
        return suppress ? new SkillDamageContext(inputs.Plan.Kind, inputs.Caster).Hit(hit) : hit;
    }

    // The damaging-ailment rows of a supported skill's hit in one context (AilmentPanelStats): the main or off hand of
    // an attack with that hand's weapon, or a spell's / secondary-damage skill's hit.
    internal static List<(string Stat, int Value)> SupportedAilmentRows(ClientState conn, ushort skillId, string hand,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0)
        => SupportedHitInputsFor(conn, skillId, weapon: weapon) is { } inputs
            ? SupportedAilmentRowsFrom(inputs, hand, additionalIncreased) : [];

    internal static List<(string Stat, int Value)> SupportedAilmentRowsFrom(SupportedHitInputs inputs, string hand,
        long additionalIncreased = 0)
    {
        var context = new AilmentPanelStats.Context(hand, Spell: !inputs.Attack, inputs.Melee);
        return AilmentPanelStats.Compute(context, inputs.Raw, inputs.Conversion, inputs.Get, additionalIncreased);
    }

    internal static AttackAccuracyProfile SupportedAccuracy(ClientState conn, ushort skillId)
    {
        var own = SupportedSkill(conn, skillId).Stats;
        long Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        var player = CharacterPlayerStats(conn);
        return AttackAccuracyProfile.From(conn.Pools, player, Stats, own, MainHandLocalSources(conn))
            with { Rating = PlayerBlindAccuracy(conn, AccuracyRating.Selected(conn.Pools, WeaponProfileFor(conn, activeSet: true), Own), player) };
    }

    internal static int SupportedCriticalChance(ClientState conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, false);

    // A release retains the unprohibited offensive chance; the hit evaluates current owner prohibitions separately.
    internal static int SupportedReleaseCriticalChance(ClientState conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, true);

    // extraIncrease: a target condition's increase (critical_strike_chance_+%_vs_blinded_enemies, Game's Blind.cs).
    internal static int SupportedCriticalChanceFor(ClientState conn, ushort skillId, bool release, long extraIncrease = 0)
    {
        var gem = SupportedSkill(conn, skillId);
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return 0;
        // Inspiration: "Supported Skills have (6-11)% increased Critical Strike Chance per Inspiration Charge".
        if (conn.SupportGems.InspirationCharges > 0
            && Stats.Key("critical_strike_chance_+%_per_righteous_charge") is uint chargeCrit)
            extraIncrease += (long)gem.Stats.GetValueOrDefault(chargeCrit) * conn.SupportGems.InspirationCharges;
        var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        int Own(string name) => release && IsCriticalProhibition(name) ? 0
            : Stats.Key(name) is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        int Player(string name) => release && IsCriticalProhibition(name) ? 0
            : Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        if (SkillsFor(conn).IsAttack(gem.Effect))
        {
            if (new[] { "global_cannot_crit", "virtual_global_cannot_crit", "cannot_critical_strike_with_attacks" }
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

    internal static SkillCatalog.ReservationCost? SupportedReservationCost(ClientState conn, ushort skillId)
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
            if (effectId == "TemporalChains" && Stats.Key("temporal_chains_no_reservation") is uint free
                && conn.PlayerStats.GetValueOrDefault(free, curseSources.GetValueOrDefault(free)) != 0) return new(0, 0, 0, 0);
        }
        if (sourceReservation is not { } reservation) return null;
        var sources = CharacterStatSources(conn).Stats;
        int Own(string name) => GolemStat(gem.Stats, name);
        int Get(string name) => Own(name) + (Stats.Key(name) is uint key
            ? conn.PlayerStats.GetValueOrDefault(key, sources.GetValueOrDefault(key)) : 0);
        int manaFlat = reservation.ManaFlat, manaPercent = reservation.ManaPercent;
        int lifeFlat = reservation.LifeFlat, lifePercent = reservation.LifePercent;
        bool curse = gem.Supports.Any(s => IsBlasphemySupport(s.Effect.Id));
        bool mine = Supports.Of(gem.Effect)?.Types.Contains(35) == true;
        bool lifeInstead = SkillsFor(conn).LifeReservation(gem.Effect, gem.Level)
            || Get("base_skill_reserve_life_instead_of_mana") != 0 || Get("skill_reserve_life_instead_of_mana") != 0
            || curse && Get("curse_aura_skills_reserve_life") != 0
            || mine && Get("skills_that_throw_mines_reserve_life") != 0;
        if (lifeInstead)
        {
            lifeFlat += manaFlat;
            lifePercent += manaPercent;
            manaFlat = manaPercent = 0;
        }
        if (Get("base_skill_no_life_reservation") != 0 || Get("skill_no_life_reservation") != 0)
            lifeFlat = lifePercent = 0;
        // Mana-only exemptions never erase a Life reservation.
        if (AuraCatalog.Supported(effectId) && Get(AuraCatalog.NoManaReservationStat(effectId)) != 0)
            manaFlat = manaPercent = 0;
        if (effectId == "TemporalChains" && Get("temporal_chains_no_reservation") != 0)
            manaFlat = manaPercent = 0;
        double factor = gem.Supports.Aggregate(1.0, (v, support) => v * support.CostMultiplier / 100.0);
        return new((int)Math.Floor(manaFlat * factor), (int)Math.Floor(manaPercent * factor),
            (int)Math.Floor(lifeFlat * factor), (int)Math.Floor(lifePercent * factor));
    }

    // Compatibility view for one-pool callers: select the resource the caller's context will use, never a Mana-only
    // projection that would erase a Life cost. Full callers use SupportedReservationCost's four raw slots.
    internal static (int Flat, int Percent)? SupportedReservation(ClientState conn, ushort skillId)
    {
        if (SupportedReservationCost(conn, skillId) is not { } cost) return null;
        var context = ReservationContext(conn, skillId);
        bool explicitLife = cost.LifeFlat != 0 || cost.LifePercent != 0;
        return context.Life
            ? (explicitLife ? cost.LifeFlat : cost.ManaFlat, explicitLife ? cost.LifePercent : cost.ManaPercent)
            : (cost.ManaFlat, cost.ManaPercent);
    }

    internal static int SupportedCriticalMultiplier(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        int extra = Stats.Key("base_critical_strike_multiplier_+") is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        return (int)Math.Clamp((long)(SkillsFor(conn).IsAttack(gem.Effect)
            ? AttackCriticalMultiplier(conn, CharacterPlayerStats(conn), gem.Effect, gem.Stats)
            : conn.Pools.SpellCriticalStrikeMultiplier) + extra, int.MinValue, int.MaxValue);
    }
}
